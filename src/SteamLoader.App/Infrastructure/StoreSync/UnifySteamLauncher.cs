using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Handles OmniLibrary download and launch worker invocations outside Steam's UI process.
/// The managed shortcuts are the bridge into Steam's native library UI; Xbox remains
/// responsible for account licensing and installation.
/// </summary>
internal static class UnifySteamLauncher
{
    internal enum UbisoftAccountLinkState
    {
        LinkRequired,
        Redeemable,
        Activated,
        NotEligible,
    }

    private enum ExternalPublisherOperation
    {
        Install,
        Launch,
        Uninstall,
    }

    private const string GogManagedInstallMarkerFileName =
        ".tools-for-steam-omnilibrary-gog";
    private const int GogLaunchFallbackThresholdMilliseconds = 3000;
    private const int EpicMaximumDownloadAttempts = 5;
    private const long EpicDiskSafetyReserveBytes = 15L * 1024 * 1024 * 1024;
    public const string InstallArgument = "--unifysteam-install";
    public const string RepairArgument = "--unifysteam-repair";
    public const string UninstallArgument = "--unifysteam-uninstall";
    public const string CancelDownloadArgument = "--unifysteam-cancel-download";
    private const string EpicMutationMutexName = @"Local\ToolsForSteamOmniLibraryEpicMutation";
    private const string GogMutationMutexName = @"Local\ToolsForSteamOmniLibraryGogMutation";
    private static readonly object EpicDownloadLogGate = new();
    private static readonly object GogDownloadLogGate = new();

    internal sealed record GogLaunchTask(
        int Index,
        string ExecutablePath,
        string WorkingDirectory,
        string? RawArguments,
        IReadOnlyList<string> ArgumentList,
        string Category,
        bool IsPrimary,
        string CompatibilityFlags)
    {
        public bool RequiresElevation =>
            CompatibilityFlags
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(flag => flag.Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record GogHelperLaunchResult(
        int ExitCode,
        bool TargetProcessObserved,
        TimeSpan Duration,
        string DiagnosticOutput);

    private sealed record EpicDownloadPlan(
        string InstallDirectory,
        long DownloadSizeBytes,
        long DiskSizeBytes,
        long CompletedBytes)
    {
        public int ProgressPercent => DiskSizeBytes > 0
            ? Math.Clamp(
                (int)Math.Floor(CompletedBytes * 100d / DiskSizeBytes),
                0,
                99)
            : 0;
    }

    private sealed record EpicDownloadRunResult(
        int ExitCode,
        int ProgressPercent,
        long DownloadedBytes,
        long TotalBytes,
        double DownloadBytesPerSecond,
        double DecompressedBytesPerSecond,
        double DiskWriteBytesPerSecond,
        double DiskReadBytesPerSecond,
        string Diagnostic);

    private sealed record GogDownloadPlan(
        string InstallDirectory,
        long DiskSizeBytes,
        long CompletedBytes)
    {
        public int ProgressPercent => DiskSizeBytes > 0
            ? Math.Clamp(
                (int)Math.Floor(CompletedBytes * 100d / DiskSizeBytes),
                0,
                99)
            : 0;
    }

    private sealed record ManagedDownloadRunResult(
        int ExitCode,
        int ProgressPercent,
        long DownloadedBytes,
        long TotalBytes,
        double DownloadBytesPerSecond,
        double DecompressedBytesPerSecond,
        double DiskWriteBytesPerSecond,
        double DiskReadBytesPerSecond,
        string Diagnostic);

    public static int Install(string target)
    {
        try
        {
            if (!TryParseTarget(target, out var storeId, out var gameId))
            {
                ShowError("The OmniLibrary install target is invalid. Refresh the store library and try again.");
                return 1;
            }

            if (ShouldAbortPendingDownloadWorker(storeId, gameId))
            {
                return 0;
            }

            using var sleepBlocker =
                OmniLibraryDownloadSleepBlocker.AcquireForCurrentThread();
            return storeId switch
            {
                "xbox-game-pass" => InstallXbox(gameId),
                "epic-games" => InstallEpic(gameId),
                "gog-galaxy" => InstallGog(gameId),
                _ => Fail($"Unknown OmniLibrary store '{storeId}'."),
            };
        }
        catch (Exception exception)
        {
            ShowError($"The OmniLibrary download failed: {exception.Message}");
            return 1;
        }
    }

    public static int Repair(string target)
    {
        try
        {
            if (!TryParseTarget(target, out var storeId, out var gameId) ||
                !storeId.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(
                    "The OmniLibrary repair target is invalid. Refresh the GOG library and try again.");
                return 1;
            }

            if (ShouldAbortPendingDownloadWorker(storeId, gameId))
            {
                return 0;
            }

            using var sleepBlocker =
                OmniLibraryDownloadSleepBlocker.AcquireForCurrentThread();
            return WithStoreMutationLock(
                GogMutationMutexName,
                storeId,
                gameId,
                "queued",
                "Waiting for the current GOG operation to finish before repair.",
                () => InstallGogCore(gameId, repairRequested: true));
        }
        catch (Exception exception)
        {
            ShowError($"The GOG repair failed: {exception.Message}");
            return 1;
        }
    }

    public static void ResumeInterruptedDownloads()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
            {
                return;
            }

            var recoveryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var storeId in new[]
                     {
                         "xbox-game-pass",
                         "epic-games",
                         "gog-galaxy",
                     })
            {
                foreach (var interrupted in
                         UnifySteamDownloadStatusStore.GetRecoverableDownloads(storeId))
                {
                    recoveryKeys.Add(
                        $"{interrupted.StoreId}:{interrupted.GameId}");
                    if ((storeId == "epic-games" &&
                         !File.Exists(ManagedLegendaryHelper.UserDataPath)) ||
                        (storeId == "gog-galaxy" &&
                         !File.Exists(ManagedGogDlHelper.AuthPath)))
                    {
                        UnifySteamDownloadStatusStore.Update(
                            interrupted.StoreId,
                            interrupted.GameId,
                            "failed",
                            interrupted.Status.ProgressPercent,
                            $"The interrupted {GetStoreDisplayName(interrupted.StoreId)} download " +
                            "could not resume because its store sign-in is missing. Reconnect the store, then select Retry Download.",
                            workerProcessId: 0,
                            downloadedBytes: interrupted.Status.DownloadedBytes,
                            totalBytes: interrupted.Status.TotalBytes,
                            attempt: interrupted.Status.Attempt);
                        continue;
                    }

                    UnifySteamDownloadStatusStore.Update(
                        interrupted.StoreId,
                        interrupted.GameId,
                        "reconnecting",
                        interrupted.Status.ProgressPercent,
                        $"Recovering interrupted {GetStoreDisplayName(interrupted.StoreId)} " +
                        "download after restart.",
                        workerProcessId: 0,
                        downloadedBytes: interrupted.Status.DownloadedBytes,
                        totalBytes: interrupted.Status.TotalBytes,
                        attempt: interrupted.Status.Attempt);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    var gogTransaction = storeId == "gog-galaxy"
                        ? GogOperationJournal.Get(interrupted.GameId)
                        : null;
                    startInfo.ArgumentList.Add(gogTransaction?.IsRepair == true
                        ? RepairArgument
                        : InstallArgument);
                    startInfo.ArgumentList.Add(
                        $"{interrupted.StoreId}:{interrupted.GameId}");
                    try
                    {
                        using var process = Process.Start(startInfo);
                        if (process is null)
                        {
                            throw new InvalidOperationException(
                                "Windows did not start the recovery worker.");
                        }

                        AssignDownloadWorkerIfUnclaimed(
                            interrupted.StoreId,
                            interrupted.GameId,
                            process.Id);
                    }
                    catch (Exception exception)
                    {
                        UnifySteamDownloadStatusStore.Update(
                            interrupted.StoreId,
                            interrupted.GameId,
                            "failed",
                            interrupted.Status.ProgressPercent,
                            $"The interrupted {GetStoreDisplayName(interrupted.StoreId)} " +
                            $"download could not be resumed automatically: {exception.Message}",
                            workerProcessId: 0,
                            downloadedBytes: interrupted.Status.DownloadedBytes,
                            totalBytes: interrupted.Status.TotalBytes,
                            attempt: interrupted.Status.Attempt);
                    }
                }
            }

            foreach (var transaction in GogOperationJournal.GetAll().Where(
                         transaction =>
                             transaction.IsRecoverableInstall &&
                             transaction.ManagedByOmniLibrary &&
                             transaction.Phase != GogOperationPhases.WaitingForGalaxy))
            {
                var recoveryKey = $"gog-galaxy:{transaction.GameId}";
                var current = UnifySteamDownloadStatusStore.Get(
                    "gog-galaxy",
                    transaction.GameId);
                if (recoveryKeys.Contains(recoveryKey) ||
                    current.Status is "paused" or "canceled" or "canceling" ||
                    !File.Exists(ManagedGogDlHelper.AuthPath))
                {
                    continue;
                }

                UnifySteamDownloadStatusStore.Update(
                    "gog-galaxy",
                    transaction.GameId,
                    "reconnecting",
                    current.ProgressPercent,
                    "Recovering the saved GOG installation transaction after restart.",
                    workerProcessId: 0,
                    downloadedBytes: Math.Max(
                        current.DownloadedBytes,
                        transaction.DownloadedBytes),
                    totalBytes: Math.Max(
                        current.TotalBytes,
                        transaction.TotalBytes),
                    attempt: Math.Max(current.Attempt, transaction.Attempt));
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                startInfo.ArgumentList.Add(transaction.IsRepair
                    ? RepairArgument
                    : InstallArgument);
                startInfo.ArgumentList.Add(
                    $"gog-galaxy:{transaction.GameId}");
                try
                {
                    using var process = Process.Start(startInfo);
                    if (process is null)
                    {
                        throw new InvalidOperationException(
                            "Windows did not start the GOG recovery worker.");
                    }

                    AssignDownloadWorkerIfUnclaimed(
                        "gog-galaxy",
                        transaction.GameId,
                        process.Id);
                }
                catch (Exception exception)
                {
                    GogOperationJournal.Fail(
                        transaction.GameId,
                        exception.Message);
                    UnifySteamDownloadStatusStore.Update(
                        "gog-galaxy",
                        transaction.GameId,
                        "failed",
                        current.ProgressPercent,
                        $"The GOG installation transaction could not resume automatically: {exception.Message}",
                        workerProcessId: 0,
                        downloadedBytes: Math.Max(
                            current.DownloadedBytes,
                            transaction.DownloadedBytes),
                        totalBytes: Math.Max(
                            current.TotalBytes,
                            transaction.TotalBytes),
                        attempt: Math.Max(current.Attempt, transaction.Attempt));
                }
            }

            foreach (var interrupted in
                     UnifySteamDownloadStatusStore.GetRecoverableCancellations())
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                startInfo.ArgumentList.Add(CancelDownloadArgument);
                startInfo.ArgumentList.Add(
                    $"{interrupted.StoreId}:{interrupted.GameId}");
                try
                {
                    using var process = Process.Start(startInfo);
                    if (process is null)
                    {
                        throw new InvalidOperationException(
                            "Windows did not start the cleanup worker.");
                    }

                    AssignDownloadWorkerIfUnclaimed(
                        interrupted.StoreId,
                        interrupted.GameId,
                        process.Id);
                }
                catch (Exception exception)
                {
                    UnifySteamDownloadStatusStore.Update(
                        interrupted.StoreId,
                        interrupted.GameId,
                        "cancel-failed",
                        interrupted.Status.ProgressPercent,
                        $"Partial-file cleanup could not resume after restart: {exception.Message}",
                        workerProcessId: 0,
                        downloadedBytes: interrupted.Status.DownloadedBytes,
                        totalBytes: interrupted.Status.TotalBytes,
                        attempt: interrupted.Status.Attempt);
                }
            }
        }
        catch
        {
            // Startup recovery is best-effort. The durable status remains visible,
            // and the user can always select Download to resume manually.
        }
    }

    private static string GetStoreDisplayName(string storeId)
    {
        return storeId switch
        {
            "xbox-game-pass" => "Xbox",
            "epic-games" => "Epic",
            "gog-galaxy" => "GOG",
            _ => "store",
        };
    }

    public static int Run(string target)
    {
        try
        {
            if (!TryParseTarget(target, out var storeId, out var gameId))
            {
                ShowError("The OmniLibrary launch target is invalid. Refresh the store library and try again.");
                return 1;
            }

            return storeId switch
            {
                OmniLibraryRomSystemRegistry.StoreId => RunRom(gameId),
                "xbox-game-pass" => RunXbox(gameId),
                "epic-games" => RunEpic(gameId),
                "gog-galaxy" => RunGog(gameId),
                _ => Fail($"Unknown OmniLibrary store '{storeId}'."),
            };
        }
        catch (Exception exception)
        {
            ShowError($"The OmniLibrary launch failed: {exception.Message}");
            return 1;
        }
    }

    public static int Uninstall(string target)
    {
        try
        {
            if (!TryParseTarget(target, out var storeId, out var gameId))
            {
                ShowError("The OmniLibrary uninstall target is invalid. Refresh the store library and try again.");
                return 1;
            }

            return storeId switch
            {
                "xbox-game-pass" => UninstallXbox(gameId),
                "epic-games" => UninstallEpic(gameId),
                "gog-galaxy" => UninstallGog(gameId),
                _ => Fail($"Unknown OmniLibrary store '{storeId}'."),
            };
        }
        catch (Exception exception)
        {
            ShowError($"The OmniLibrary uninstall action failed: {exception.Message}");
            return 1;
        }
    }

    private static int RunRom(string gameId)
    {
        var configuration = LoadStoreSyncConfiguration();
        if (!configuration.UnifySteam.Stores.TryGetValue(
                OmniLibraryRomSystemRegistry.StoreId,
                out var store) ||
            store?.Enabled != true)
        {
            return Fail("The Emulator library is disabled in OmniLibrary.");
        }

        var game = store.Cache?.Games?.FirstOrDefault(candidate =>
            candidate is not null &&
            candidate.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase));
        if (game is null)
        {
            return Fail("This ROM is no longer in the Emulator library. Scan the ROM folder again.");
        }

        var romPath = !string.IsNullOrWhiteSpace(game.RomPath)
            ? game.RomPath
            : game.ExecutablePath;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            return Fail("The ROM file was moved or removed. OmniLibrary will remove the stale entry during its next delta scan.");
        }

        OmniLibraryRomSystemDescriptor system;
        try
        {
            system = OmniLibraryRomSystemRegistry.GetRequired(game.PlatformId);
        }
        catch (InvalidOperationException)
        {
            return Fail($"The {game.PlatformTitle} emulator is not supported by this OmniLibrary build.");
        }

        store.RomSystems.TryGetValue(system.Id, out var systemSettings);
        var configuredPath = systemSettings?.EmulatorPath ??
            (system.Id.Equals("psp", StringComparison.OrdinalIgnoreCase)
                ? store.ToolPath
                : string.Empty);
        var emulatorPath = UnifySteamService.ResolveRomEmulatorExecutable(
            system.Id,
            configuredPath);
        if (string.IsNullOrWhiteSpace(emulatorPath))
        {
            return Fail(
                $"{system.EmulatorTitle} was not found. Install it or select {system.EmulatorExecutableName} in OmniLibrary's {system.Title} settings.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = emulatorPath,
            WorkingDirectory = Path.GetDirectoryName(emulatorPath) ?? string.Empty,
            UseShellExecute = false,
        };
        foreach (var argument in BuildRomLaunchArguments(
                     system.Id,
                     romPath,
                     systemSettings?.Fullscreen ?? true))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var emulatorProcess = Process.Start(startInfo);
        if (emulatorProcess is null)
        {
            return Fail($"{system.EmulatorTitle} could not be started.");
        }

        // Keep the Steam shortcut's launcher process alive for the complete
        // emulation session. Steam can then retain its Running state, overlay
        // ownership and play-time tracking until PPSSPP exits.
        emulatorProcess.WaitForExit();
        return emulatorProcess.ExitCode;
    }

    internal static IReadOnlyList<string> BuildPpssppLaunchArguments(
        string romPath) =>
        BuildRomLaunchArguments("psp", romPath, fullscreen: true);

    internal static IReadOnlyList<string> BuildRomLaunchArguments(
        string systemId,
        string romPath,
        bool fullscreen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);
        var fullRomPath = Path.GetFullPath(romPath);
        return systemId.Trim().ToLowerInvariant() switch
        {
            "psp" => fullscreen
                ? ["--fullscreen", "--pause-menu-exit", fullRomPath]
                : ["--pause-menu-exit", fullRomPath],
            "gamecube" => fullscreen
                ? ["--batch", "--config=Dolphin.Display.Fullscreen=True", "--exec", fullRomPath]
                : ["--batch", "--exec", fullRomPath],
            "game-boy-advance" => fullscreen
                ? ["-f", fullRomPath]
                : [fullRomPath],
            "nintendo-64" => fullscreen
                ? ["--system", "Nintendo 64", "--no-file-prompt", "--fullscreen", fullRomPath]
                : ["--system", "Nintendo 64", "--no-file-prompt", fullRomPath],
            _ => throw new InvalidOperationException(
                $"Unsupported ROM system '{systemId}'."),
        };
    }

    public static int CancelDownload(string target)
    {
        if (!TryParseTarget(target, out var storeId, out var gameId))
        {
            ShowError(
                "The OmniLibrary cancel target is invalid. Refresh Download Center and try again.");
            return 1;
        }

        var previous = UnifySteamDownloadStatusStore.Get(storeId, gameId);
        UnifySteamDownloadStatusStore.Update(
            storeId,
            gameId,
            "canceling",
            previous.ProgressPercent,
            "Stopping the transfer and removing its partial files.",
            downloadedBytes: previous.DownloadedBytes,
            totalBytes: previous.TotalBytes,
            attempt: previous.Attempt);
        try
        {
            using var sleepBlocker =
                OmniLibraryDownloadSleepBlocker.AcquireForCurrentThread();
            switch (storeId)
            {
                case "epic-games":
                    CancelEpicDownload(gameId);
                    break;
                case "gog-galaxy":
                    CancelGogDownload(gameId);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{GetStoreDisplayName(storeId)} downloads cannot be deleted automatically.");
            }

            UnifySteamDownloadStatusStore.Update(
                storeId,
                gameId,
                "canceled",
                0,
                "Download canceled and partial files removed.",
                workerProcessId: 0);
            return 0;
        }
        catch (Exception exception)
        {
            var current = UnifySteamDownloadStatusStore.Get(storeId, gameId);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                gameId,
                "cancel-failed",
                current.ProgressPercent,
                $"The partial download could not be removed: {exception.Message}",
                workerProcessId: 0,
                downloadedBytes: current.DownloadedBytes,
                totalBytes: current.TotalBytes,
                attempt: current.Attempt);
            return 1;
        }
    }

    public static bool TryPauseDownload(
        string storeId,
        string gameId,
        out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        if (normalizedStoreId is not ("epic-games" or "gog-galaxy") ||
            !IsSafeLauncherId(normalizedGameId))
        {
            message =
                "This store does not expose a safe managed pause operation. Open its official download manager instead.";
            return false;
        }

        var current = UnifySteamDownloadStatusStore.Get(
            normalizedStoreId,
            normalizedGameId);
        if (!IsPauseableDownloadStatus(current.Status))
        {
            message = current.Status == "paused"
                ? "This download is already paused."
                : "This download is no longer in a pauseable phase.";
            return current.Status == "paused";
        }

        if (!TryStopManagedDownloadWorker(current, out var stopError))
        {
            var latest = UnifySteamDownloadStatusStore.Get(
                normalizedStoreId,
                normalizedGameId);
            if (latest.Status is "completed" or "failed" or "canceled")
            {
                message = latest.DetailText;
                return latest.Status == "completed";
            }

            message = stopError;
            return false;
        }

        UnifySteamDownloadStatusStore.Update(
            normalizedStoreId,
            normalizedGameId,
            "paused",
            current.ProgressPercent,
            $"{GetStoreDisplayName(normalizedStoreId)} download paused. Resume keeps the saved files.",
            workerProcessId: 0,
            downloadedBytes: current.DownloadedBytes,
            totalBytes: current.TotalBytes,
            downloadBytesPerSecond: 0,
            decompressedBytesPerSecond: 0,
            diskWriteBytesPerSecond: 0,
            diskReadBytesPerSecond: 0,
            attempt: current.Attempt);
        message = $"{GetStoreDisplayName(normalizedStoreId)} download paused.";
        return true;
    }

    internal static bool TryPrepareManagedDownloadCancellation(
        string storeId,
        string gameId,
        out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        if (normalizedStoreId is not ("epic-games" or "gog-galaxy") ||
            !IsSafeLauncherId(normalizedGameId))
        {
            message =
                "Only downloads managed by Tools for Steam can be canceled and cleaned up directly.";
            return false;
        }

        var current = UnifySteamDownloadStatusStore.Get(
            normalizedStoreId,
            normalizedGameId);
        if (!StoreSyncService.CanCancelManagedDownload(
                managedByToolsForSteam: true,
                current.Status))
        {
            message =
                "This transfer is no longer in a phase that can be canceled.";
            return false;
        }

        if (current.WorkerProcessId > 0 &&
            !TryStopManagedDownloadWorker(current, out var stopError))
        {
            message = stopError;
            return false;
        }

        // Publish the cancellation intent before waiting for a just-created
        // worker. AssignDownloadWorkerIfUnclaimed can still attach that process
        // to this busy state, allowing us to terminate it without PID guessing.
        UnifySteamDownloadStatusStore.Update(
            normalizedStoreId,
            normalizedGameId,
            "canceling",
            current.ProgressPercent,
            "Stopping the transfer before partial-file cleanup.",
            workerProcessId: 0,
            downloadedBytes: current.DownloadedBytes,
            totalBytes: current.TotalBytes,
            attempt: current.Attempt,
            gameTitle: current.GameTitle,
            steamAppId: current.SteamAppId);

        if (current.WorkerProcessId <= 0)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                Thread.Sleep(100);
                var claimed = UnifySteamDownloadStatusStore.Get(
                    normalizedStoreId,
                    normalizedGameId);
                if (claimed.WorkerProcessId <= 0)
                {
                    continue;
                }

                if (!TryStopManagedDownloadWorker(
                        claimed,
                        out var claimedStopError))
                {
                    message = claimedStopError;
                    return false;
                }

                break;
            }
        }

        var latest = UnifySteamDownloadStatusStore.Get(
            normalizedStoreId,
            normalizedGameId);
        UnifySteamDownloadStatusStore.Update(
            normalizedStoreId,
            normalizedGameId,
            "canceling",
            latest.ProgressPercent,
            "The transfer stopped. Removing its partial files.",
            workerProcessId: 0,
            downloadedBytes: latest.DownloadedBytes,
            totalBytes: latest.TotalBytes,
            attempt: latest.Attempt,
            gameTitle: latest.GameTitle,
            steamAppId: latest.SteamAppId);
        message =
            $"{GetStoreDisplayName(normalizedStoreId)} download stopped safely.";
        return true;
    }

    internal static bool TryStopTrackingDownload(
        string storeId,
        string gameId,
        out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        if (!IsSafeLauncherId(normalizedGameId))
        {
            message = "The Download Center entry is invalid.";
            return false;
        }

        var current = UnifySteamDownloadStatusStore.Get(
            normalizedStoreId,
            normalizedGameId);
        if (UnifySteamDownloadStatusStore.IsBusyOperation(current.Status) &&
            current.WorkerProcessId > 0 &&
            !TryStopManagedDownloadWorker(current, out var stopError))
        {
            message = stopError;
            return false;
        }

        WriteTrackingStoppedStatus(
            normalizedStoreId,
            normalizedGameId,
            current);

        // Cover the narrow gap between Process.Start and worker assignment.
        // The tombstone prevents a worker that has not entered Install yet;
        // this loop stops one that was already entering at the same moment.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(100);
            var latest = UnifySteamDownloadStatusStore.Get(
                normalizedStoreId,
                normalizedGameId);
            if (latest.Status == "tracking-stopped")
            {
                continue;
            }

            if (latest.WorkerProcessId > 0 &&
                !TryStopManagedDownloadWorker(
                    latest,
                    out var lateStopError))
            {
                message = lateStopError;
                return false;
            }

            WriteTrackingStoppedStatus(
                normalizedStoreId,
                normalizedGameId,
                latest);
        }

        message =
            "Tracking stopped. A download owned by another store may continue in that store's app.";
        return true;
    }

    private static void WriteTrackingStoppedStatus(
        string storeId,
        string gameId,
        UnifySteamDownloadStatus previous)
    {
        UnifySteamDownloadStatusStore.Update(
            storeId,
            gameId,
            "tracking-stopped",
            previous.ProgressPercent,
            "Tracking was stopped by the user.",
            workerProcessId: 0,
            downloadedBytes: previous.DownloadedBytes,
            totalBytes: previous.TotalBytes,
            attempt: previous.Attempt,
            gameTitle: previous.GameTitle,
            steamAppId: previous.SteamAppId);
    }

    internal static bool ShouldAbortPendingDownloadWorker(
        string storeId,
        string gameId)
    {
        return UnifySteamDownloadStatusStore
            .Get(storeId, gameId)
            .Status is "canceling" or "canceled" or "tracking-stopped";
    }

    public static bool TryOpenExternalDownloadManager(
        string storeId,
        string gameId,
        out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        if (!IsSafeLauncherId(normalizedGameId))
        {
            message = "The OmniLibrary game ID is invalid.";
            return false;
        }

        if (normalizedStoreId == "xbox-game-pass")
        {
            if (TryOpenXboxProductPage(normalizedGameId, out _))
            {
                message =
                    "Xbox opened. Pause, resume, or cancel the native Windows download there; OmniLibrary keeps tracking it.";
                return true;
            }

            message = "The Xbox product page could not be opened.";
            return false;
        }

        if (normalizedStoreId == "gog-galaxy")
        {
            var client = FindGogGalaxyClientPath();
            if (!string.IsNullOrWhiteSpace(client))
            {
                OpenGogGalaxyGameView(client, normalizedGameId);
                message = "GOG Galaxy opened for this download.";
                return true;
            }
        }

        if (normalizedStoreId == "epic-games")
        {
            var game = GetEpicCachedGame(normalizedGameId);
            if (game?.DeliveryProvider == "ea-app")
            {
                var availability =
                    EaAppIntegration.GetAvailability(forceRefresh: true);
                var eaApp = availability.ExecutablePath;
                if (!string.IsNullOrWhiteSpace(eaApp))
                {
                    RunVisibleAndWait(eaApp, [], waitForExit: false);
                    message = "EA app opened for this Epic-owned title.";
                    return true;
                }

                if (TryOpenShellTarget(EaAppIntegration.OfficialDownloadUrl))
                {
                    message =
                        "The official EA app download page opened. Install the EA app, then retry the game handoff.";
                    return true;
                }
            }
            else if (game?.DeliveryProvider == "ubisoft-connect" &&
                     OpenUbisoftConnect(game, openProduct: true))
            {
                message = "Ubisoft Connect opened for this Epic-owned title.";
                return true;
            }
            else if (OpenEpicGamesLauncher(normalizedGameId))
            {
                message = "Epic Games Launcher opened for this title.";
                return true;
            }
        }

        message = "No external download manager is available for this entry.";
        return false;
    }

    private static bool IsPauseableDownloadStatus(string status)
    {
        return status.Trim().ToLowerInvariant() is
            "preparing" or
            "queued" or
            "downloading" or
            "reconnecting";
    }

    private static bool TryStopManagedDownloadWorker(
        UnifySteamDownloadStatus status,
        out string error)
    {
        error = string.Empty;
        if (status.WorkerProcessId <= 0)
        {
            error =
                "The download worker is still starting. Wait a moment and try Pause again.";
            return false;
        }

        if (status.WorkerProcessId == Environment.ProcessId)
        {
            error = "TFS refused to stop its main service process.";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(status.WorkerProcessId);
            if (process.HasExited)
            {
                return true;
            }

            var expectedExecutable = Environment.ProcessPath;
            var processExecutable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(expectedExecutable) ||
                string.IsNullOrWhiteSpace(processExecutable) ||
                !string.Equals(
                    Path.GetFullPath(processExecutable),
                    Path.GetFullPath(expectedExecutable),
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "The recorded worker no longer belongs to TFS, so it was not terminated.";
                return false;
            }

            if (status.WorkerStartedAtUtc.HasValue)
            {
                var actualStartedAtUtc =
                    new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                if (Math.Abs(
                        (actualStartedAtUtc - status.WorkerStartedAtUtc.Value)
                        .TotalSeconds) >= 2)
                {
                    error =
                        "The recorded worker ID was reused by another TFS process, so it was not terminated.";
                    return false;
                }
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(10000))
            {
            error = "The operation worker did not stop within 10 seconds.";
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception exception)
        {
            error = $"The operation worker could not be stopped: {exception.Message}";
            return false;
        }
    }

    private static void CancelEpicDownload(string appName)
    {
        var configuration = LoadStoreSyncConfiguration();
        configuration.UnifySteam.Stores.TryGetValue("epic-games", out var store);
        var baseDirectory = string.IsNullOrWhiteSpace(store?.InstallPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Games")
            : Path.GetFullPath(store.InstallPath);
        var installDirectory = ResolveContainedInstallDirectory(
            baseDirectory,
            TryReadEpicFolderName(appName),
            appName);
        DeleteContainedDownloadDirectory(baseDirectory, installDirectory);

        var resumePath = Path.Combine(
            ManagedLegendaryHelper.ConfigDirectory,
            "tmp",
            $"{appName}.resume");
        if (File.Exists(resumePath))
        {
            File.Delete(resumePath);
        }

        // Finalizing is cancelable as well. If Legendary already committed its
        // installed record before the worker was stopped, remove only that
        // record after our contained cleanup so a later retry starts cleanly.
        var legendary = ResolveLegendaryTool(installWhenMissing: false);
        if (!string.IsNullOrWhiteSpace(legendary) &&
            IsEpicGameInstalled(legendary, appName))
        {
            var metadataExitCode = RunHiddenAndWait(
                legendary,
                [
                    "-y",
                    "uninstall",
                    appName,
                    "--keep-files",
                    "--skip-uninstaller",
                ]);
            if (metadataExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Epic partial files were removed, but its local installation record could not be cleared.");
            }
        }

        UpdateEpicUninstalledCache(appName);
    }

    private static void CancelGogDownload(string gameId)
    {
        var baseDirectory = Path.GetFullPath(GetGogPreferredInstallPath());
        var transaction = GogOperationJournal.Get(gameId);
        var installDirectory =
            transaction is not null &&
            IsContainedGogOperationRoot(
                baseDirectory,
                transaction.InstallRoot,
                gameId)
                ? Path.GetFullPath(transaction.InstallRoot)
                : Path.GetFullPath(Path.Combine(baseDirectory, gameId));
        DeleteContainedDownloadDirectory(baseDirectory, installDirectory);
        ManagedGogDlHelper.ClearInstalledState(gameId);
        UpdateGogUninstalledCache(gameId);
        GogOperationJournal.Clear(gameId);
    }

    private static void DeleteContainedDownloadDirectory(
        string baseDirectory,
        string targetDirectory)
    {
        var normalizedBase = Path.GetFullPath(baseDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var containedPrefix = normalizedBase + Path.DirectorySeparatorChar;
        if (normalizedTarget.Length <= normalizedBase.Length ||
            !normalizedTarget.StartsWith(
                containedPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The partial download path is outside the configured store directory.");
        }

        if (!Directory.Exists(normalizedTarget))
        {
            return;
        }

        if ((File.GetAttributes(normalizedTarget) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The partial download contains a filesystem link and was left untouched for safety.");
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(normalizedTarget);
        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var childDirectory in
                     Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(childDirectory) &
                     FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "The partial download contains a filesystem link and was left untouched for safety.");
                }

                pendingDirectories.Push(childDirectory);
            }
        }

        Directory.Delete(normalizedTarget, recursive: true);
    }

    internal static bool TryParseTarget(string target, out string storeId, out string gameId)
    {
        storeId = string.Empty;
        gameId = string.Empty;
        var separatorIndex = (target ?? string.Empty).IndexOf(':');
        if (string.IsNullOrWhiteSpace(target) || separatorIndex <= 0 || separatorIndex >= target.Length - 1)
        {
            return false;
        }

        storeId = target[..separatorIndex].Trim().ToLowerInvariant();
        gameId = target[(separatorIndex + 1)..].Trim();
        if (storeId.Equals(
                OmniLibraryRomSystemRegistry.StoreId,
                StringComparison.OrdinalIgnoreCase))
        {
            var romIdParts = gameId.Split(':');
            return romIdParts.Length == 2 &&
                   IsSafeLauncherId(romIdParts[0]) &&
                   IsSafeLauncherId(romIdParts[1]);
        }

        return IsSafeLauncherId(gameId);
    }

    private static int InstallEpic(string appName)
    {
        return WithStoreMutationLock(
            EpicMutationMutexName,
            "epic-games",
            appName,
            "queued",
            "Waiting for the current Epic operation to finish.",
            () => InstallEpicCore(appName));
    }

    private static int InstallEpicCore(string appName)
    {
        const string storeId = "epic-games";
        var previous = UnifySteamDownloadStatusStore.Get(storeId, appName);
        UnifySteamDownloadStatusStore.Update(
            storeId,
            appName,
            "preparing",
            previous.ProgressPercent,
            "Preparing Epic download and checking the installation manifest.",
            downloadedBytes: previous.DownloadedBytes,
            totalBytes: previous.TotalBytes,
            attempt: previous.Attempt);
        try
        {
            var legendary = ResolveLegendaryTool(installWhenMissing: true);
            var cachedGame = GetEpicCachedGame(appName);
            if (cachedGame?.DeliveryProvider == "ea-app")
            {
                return OpenEaAppAction(
                    legendary,
                    cachedGame,
                    installationRequested: true);
            }

            if (cachedGame?.DeliveryProvider == "ubisoft-connect")
            {
                if (!EnsureUbisoftAccountLink(
                        legendary,
                        cachedGame,
                        out var accountLinkDetail))
                {
                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        appName,
                        "action-required",
                        0,
                        accountLinkDetail);
                    return 0;
                }

                if (!cachedGame.HasInstallableAsset)
                {
                    return OpenExternalPublisherAction(
                        cachedGame,
                        ExternalPublisherOperation.Install);
                }
            }
            else if (cachedGame?.RequiresExternalLauncher == true)
            {
                return OpenExternalPublisherAction(
                    cachedGame,
                    ExternalPublisherOperation.Install);
            }

            if (cachedGame?.IsPreloaded == true)
            {
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    appName,
                    "action-required",
                    100,
                    "This preload is complete, but Epic has not unlocked the release yet.");
                return 0;
            }

            var alreadyInstalled = IsEpicGameInstalled(legendary, appName);
            var updateRequired =
                alreadyInstalled &&
                cachedGame is not null &&
                !string.IsNullOrWhiteSpace(cachedGame.LatestVersion) &&
                !string.IsNullOrWhiteSpace(cachedGame.Version) &&
                !string.Equals(
                    cachedGame.LatestVersion,
                    cachedGame.Version,
                    StringComparison.OrdinalIgnoreCase);
            if (alreadyInstalled && !updateRequired)
            {
                if (!CompleteEpicWindowsSetup(
                        legendary,
                        cachedGame,
                        appName))
                {
                    return 1;
                }

                UnifySteamDownloadStatusStore.Update(storeId, appName, "completed", 100, "Installed.");
                return 0;
            }

            var configuration = LoadStoreSyncConfiguration();
            configuration.UnifySteam.Stores.TryGetValue(storeId, out var epicStore);
            var downloadWorkers = Math.Clamp(epicStore?.DownloadWorkers ?? 16, 1, 32);
            var downloadTimeoutSeconds = Math.Clamp(
                epicStore?.DownloadTimeoutSeconds ?? 60,
                15,
                300);
            var installPath = epicStore?.InstallPath ?? string.Empty;
            EpicDownloadRunResult? lastResult = null;
            var lastAttempt = 1;

            for (var attempt = 1; attempt <= EpicMaximumDownloadAttempts; attempt++)
            {
                lastAttempt = attempt;
                var plan = BuildEpicDownloadPlan(
                    legendary,
                    appName,
                    installPath,
                    Math.Max(
                        previous.DownloadedBytes,
                        lastResult?.DownloadedBytes ?? 0));
                EnsureEpicDownloadHasSpace(plan);
                var arguments = new List<string>
                {
                    "-y",
                    "install",
                    appName,
                    "--skip-sdl",
                    "--skip-dlcs",
                    "--max-workers",
                    downloadWorkers.ToString(CultureInfo.InvariantCulture),
                    "--dl-timeout",
                    downloadTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrWhiteSpace(installPath))
                {
                    arguments.Add("--base-path");
                    arguments.Add(installPath);
                }

                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    appName,
                    "downloading",
                    plan.ProgressPercent,
                    attempt == 1
                        ? "Starting Epic download."
                        : $"Resuming Epic download (attempt {attempt}/{EpicMaximumDownloadAttempts}).",
                    downloadedBytes: plan.CompletedBytes,
                    totalBytes: plan.DiskSizeBytes,
                    attempt: attempt);
                lastResult = RunHiddenDownloadAndTrack(
                    legendary,
                    arguments,
                    storeId,
                    appName,
                    plan,
                    attempt);
                if (lastResult.ExitCode == 0)
                {
                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        appName,
                        "finalizing",
                        99,
                        "Epic finished downloading. Verifying the installation before Play becomes available.",
                        downloadedBytes: Math.Max(
                            lastResult.DownloadedBytes,
                            plan.DiskSizeBytes),
                        totalBytes: plan.DiskSizeBytes,
                        attempt: attempt);
                    if (!CompleteEpicWindowsSetup(
                            legendary,
                            cachedGame,
                            appName))
                    {
                        return 1;
                    }

                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        appName,
                        "completed",
                        100,
                        "Installed.",
                        downloadedBytes: Math.Max(
                            lastResult.DownloadedBytes,
                            plan.DiskSizeBytes),
                        totalBytes: plan.DiskSizeBytes,
                        attempt: attempt);
                    return 0;
                }

                if (attempt >= EpicMaximumDownloadAttempts ||
                    !ShouldRetryEpicDownload(lastResult.Diagnostic))
                {
                    break;
                }

                var retryDelay = TimeSpan.FromSeconds(
                    attempt switch
                    {
                        1 => 15,
                        2 => 30,
                        3 => 60,
                        _ => 120,
                    });
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    appName,
                    "reconnecting",
                    lastResult.ProgressPercent,
                    $"Epic download paused. Resuming in {(int)retryDelay.TotalSeconds}s " +
                    $"(attempt {attempt + 1}/{EpicMaximumDownloadAttempts}).",
                    downloadedBytes: lastResult.DownloadedBytes,
                    totalBytes: lastResult.TotalBytes,
                    downloadBytesPerSecond: lastResult.DownloadBytesPerSecond,
                    decompressedBytesPerSecond: lastResult.DecompressedBytesPerSecond,
                    diskWriteBytesPerSecond: lastResult.DiskWriteBytesPerSecond,
                    diskReadBytesPerSecond: lastResult.DiskReadBytesPerSecond,
                    attempt: attempt);
                Thread.Sleep(retryDelay);
            }

            var diagnostic = NormalizeEpicDownloadDiagnostic(lastResult?.Diagnostic);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                appName,
                "failed",
                lastResult?.ProgressPercent ?? 0,
                string.IsNullOrWhiteSpace(diagnostic)
                    ? "Epic download stopped after all automatic resume attempts. Select Retry Download to continue from its saved files."
                    : $"Epic download stopped: {diagnostic}",
                downloadedBytes: lastResult?.DownloadedBytes ?? 0,
                totalBytes: lastResult?.TotalBytes ?? 0,
                downloadBytesPerSecond: lastResult?.DownloadBytesPerSecond ?? 0,
                decompressedBytesPerSecond: lastResult?.DecompressedBytesPerSecond ?? 0,
                diskWriteBytesPerSecond: lastResult?.DiskWriteBytesPerSecond ?? 0,
                diskReadBytesPerSecond: lastResult?.DiskReadBytesPerSecond ?? 0,
                attempt: lastAttempt);
            return 1;
        }
        catch (Exception exception)
        {
            var current = UnifySteamDownloadStatusStore.Get(storeId, appName);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                appName,
                "failed",
                current.ProgressPercent,
                exception.Message,
                downloadedBytes: current.DownloadedBytes,
                totalBytes: current.TotalBytes,
                attempt: current.Attempt);
            return Fail($"The Epic download failed: {exception.Message}");
        }
    }

    private static int UninstallEpic(string appName)
    {
        return WithStoreMutationLock(
            EpicMutationMutexName,
            "epic-games",
            appName,
            "uninstalling",
            "Waiting for the current Epic operation to finish before uninstalling.",
            () =>
        {
            const string storeId = "epic-games";
            var legendary = ResolveLegendaryTool(installWhenMissing: false);
            var cachedGame = GetEpicCachedGame(appName);
            if (cachedGame?.RequiresExternalLauncher == true)
            {
            return cachedGame.DeliveryProvider == "ea-app"
                    ? OpenEaAppForUninstall(cachedGame)
                    : OpenExternalPublisherAction(
                        cachedGame,
                        ExternalPublisherOperation.Uninstall);
            }

            if (!IsEpicGameInstalled(legendary, appName))
            {
                UpdateEpicUninstalledCache(appName);
                UnifySteamDownloadStatusStore.Clear(storeId, appName);
                return 0;
            }

            UnifySteamDownloadStatusStore.Update(
                storeId,
                appName,
                "uninstalling",
                0,
                "Removing Epic game files.");
            var exitCode = RunHiddenAndWait(legendary, ["-y", "uninstall", appName]);
            if (exitCode != 0)
            {
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    appName,
                    "uninstall-failed",
                    0,
                    "Epic could not uninstall this game. Select Retry Uninstall to try again.");
                return 1;
            }

            UpdateEpicUninstalledCache(appName);
            UnifySteamDownloadStatusStore.Clear(storeId, appName);
            return 0;
        });
    }

    private static int UninstallXbox(string productId)
    {
        const string storeId = "xbox-game-pass";
        if (!TryGetInstalledXboxGame(productId, out _))
        {
            UpdateXboxUninstalledCache(productId);
            UnifySteamDownloadStatusStore.Clear(storeId, productId);
            return 0;
        }

        // Use the same single protocol handoff as the proven Xbox download
        // fallback. Multiple generic/package activations can race on Xbox
        // Insider builds and replace the requested game page with Home.
        if (!TryOpenXboxProductPage(productId, out _))
        {
            UnifySteamDownloadStatusStore.Update(
                storeId,
                productId,
                "uninstall-failed",
                0,
                "The Xbox product page could not be opened.",
                workerProcessId: 0);
            return 1;
        }

        UnifySteamDownloadStatusStore.Update(
            storeId,
            productId,
            "uninstall-action-required",
            0,
            "Finish uninstalling this game in the Xbox app. OmniLibrary will detect its removal automatically.",
            workerProcessId: 0);
        return 0;
    }

    private static int WithStoreMutationLock(
        string mutexName,
        string storeId,
        string gameId,
        string waitingStatus,
        string waitingDetail,
        Func<int> action)
    {
        using var mutex = new Mutex(false, mutexName);
        var lockTaken = false;
        try
        {
            var previous = UnifySteamDownloadStatusStore.Get(storeId, gameId);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                gameId,
                waitingStatus,
                previous.ProgressPercent,
                waitingDetail,
                downloadedBytes: previous.DownloadedBytes,
                totalBytes: previous.TotalBytes,
                downloadBytesPerSecond: previous.DownloadBytesPerSecond,
                decompressedBytesPerSecond: previous.DecompressedBytesPerSecond,
                diskWriteBytesPerSecond: previous.DiskWriteBytesPerSecond,
                diskReadBytesPerSecond: previous.DiskReadBytesPerSecond,
                attempt: previous.Attempt);
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromHours(24));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "failed",
                    0,
                    "The store operation queue timed out.");
                return 1;
            }

            return action();
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static int InstallGog(string gameId)
    {
        return WithStoreMutationLock(
            GogMutationMutexName,
            "gog-galaxy",
            gameId,
            "queued",
            "Waiting for the current GOG operation to finish.",
            () => InstallGogCore(gameId));
    }

    private static int InstallGogCore(
        string gameId,
        bool repairRequested = false)
    {
        const string storeId = "gog-galaxy";
        var failureStage = "preparing";
        var previous = UnifySteamDownloadStatusStore.Get(storeId, gameId);
        UnifySteamDownloadStatusStore.Update(
            storeId,
            gameId,
            "preparing",
            previous.ProgressPercent,
            "Preparing the GOG manifest and checking existing files.",
            downloadedBytes: previous.DownloadedBytes,
            totalBytes: previous.TotalBytes,
            attempt: previous.Attempt);
        try
        {
            var configuration = LoadStoreSyncConfiguration();
            configuration.UnifySteam.Stores.TryGetValue(
                storeId,
                out var gogStoreConfiguration);
            gogStoreConfiguration ??= new UnifySteamStoreConfiguration();
            var includeDlc = gogStoreConfiguration.IncludeGogDlc;
            var downloadWorkers = Math.Clamp(
                gogStoreConfiguration.DownloadWorkers,
                1,
                32);
            var baseDirectory = GetGogPreferredInstallPath();
            Directory.CreateDirectory(baseDirectory);

            var transaction = GogOperationJournal.Get(gameId);
            var transactionRoot =
                transaction is not null &&
                transaction.IsInstall &&
                IsContainedGogOperationRoot(
                    baseDirectory,
                    transaction.InstallRoot,
                    gameId)
                    ? transaction.InstallRoot
                    : string.Empty;
            var knownInstallRoot = !string.IsNullOrWhiteSpace(transactionRoot)
                ? transactionRoot
                : FindKnownGogInstallRoot(baseDirectory, gameId);
            var knownExecutable = ResolveGogExecutablePath(knownInstallRoot, gameId);
            var cachedGame = gogStoreConfiguration.Cache?.Games?.FirstOrDefault(game =>
                game is not null &&
                string.Equals(game.Id, gameId, StringComparison.OrdinalIgnoreCase));
            var updateRequested =
                cachedGame?.Installed == true &&
                !string.IsNullOrWhiteSpace(cachedGame.LatestVersion) &&
                !string.IsNullOrWhiteSpace(cachedGame.Version) &&
                !string.Equals(
                    cachedGame.LatestVersion,
                    cachedGame.Version,
                    StringComparison.OrdinalIgnoreCase);
            if (!updateRequested &&
                !repairRequested &&
                !string.IsNullOrWhiteSpace(knownExecutable) &&
                File.Exists(knownExecutable))
            {
                var managedByOmniLibrary =
                    IsManagedGogInstall(knownInstallRoot, gameId) ||
                    transaction?.ManagedByOmniLibrary == true;
                if (managedByOmniLibrary)
                {
                    var knownGogdl = ResolveGogTool(installWhenMissing: true);
                    var knownAuthPath = FindGogAuthPath();
                    WriteGogManagedInstallMarker(knownInstallRoot!, gameId);
                    GogOperationJournal.Advance(
                        gameId,
                        GogOperationPhases.FilesVerified,
                        installRoot: knownInstallRoot,
                        downloadedBytes: previous.DownloadedBytes,
                        totalBytes: previous.TotalBytes,
                        detailText: "The downloaded GOG files were verified.");
                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        gameId,
                        "finalizing",
                        99,
                        "The game files are ready. Finishing GOG Windows setup before Play becomes available.");
                    GogOperationJournal.Advance(
                        gameId,
                        GogOperationPhases.WindowsSetup,
                        detailText: "Completing GOG Windows setup.");
                    if (!GogInstallPreparation.EnsureReady(
                            knownGogdl,
                            knownAuthPath,
                            knownInstallRoot!,
                            gameId,
                            message => WriteGogLaunchLog(gameId, message),
                            out var knownPreparationError))
                    {
                        GogOperationJournal.Fail(gameId, knownPreparationError);
                        UnifySteamDownloadStatusStore.Update(
                            storeId,
                            gameId,
                            "failed",
                            99,
                            knownPreparationError);
                        return Fail($"GOG could not finish preparing this game: {knownPreparationError}");
                    }
                }

                GogOperationJournal.Advance(
                    gameId,
                    GogOperationPhases.Ready,
                    installRoot: knownInstallRoot,
                    detailText: "The GOG installation is ready.");
                UpdateGogInstalledCache(gameId, knownInstallRoot, knownExecutable);
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "completed",
                    100,
                    "Installed.");
                GogOperationJournal.Clear(gameId);
                return 0;
            }

            failureStage = "helper";
            var gogdl = ResolveGogTool(installWhenMissing: true);
            if (string.IsNullOrWhiteSpace(gogdl))
            {
                throw new InvalidOperationException(
                    "The managed GOG download helper is unavailable.");
            }

            failureStage = "authentication";
            var authPath = FindGogAuthPath();
            if (string.IsNullOrWhiteSpace(authPath))
            {
                throw new InvalidOperationException(
                    "No isolated GOG sign-in was found. Connect GOG in OmniLibrary first.");
            }

            failureStage = "managed-transfer";
            var installRoot = !string.IsNullOrWhiteSpace(knownInstallRoot) &&
                              IsManagedGogInstallRoot(baseDirectory, knownInstallRoot, gameId)
                ? knownInstallRoot
                : Path.Combine(baseDirectory, gameId);
            Directory.CreateDirectory(installRoot);
            transaction = GogOperationJournal.Get(gameId);
            var requestedOperation = repairRequested
                ? "repair"
                : updateRequested
                    ? "update"
                    : "install";
            if (transaction is null ||
                !transaction.IsInstall ||
                !transaction.ManagedByOmniLibrary ||
                repairRequested && !transaction.IsRepair ||
                updateRequested && !transaction.IsUpdate)
            {
                transaction = GogOperationJournal.BeginInstall(
                    gameId,
                    installRoot,
                    includeDlc,
                    operation: requestedOperation);
            }
            else if (transaction.Phase == GogOperationPhases.Preparing)
            {
                transaction = GogOperationJournal.Advance(
                    gameId,
                    GogOperationPhases.Preparing,
                    installRoot: installRoot,
                    detailText: updateRequested
                        ? "Preparing the GOG update and checking existing files."
                        : "Preparing the GOG manifest and checking existing files.");
            }

            // A real interrupted download keeps both partial files and gogdl's
            // manifest so the next invocation can resume. Clear the manifest
            // only for a clean/manual reinstall where no active transfer state
            // proves that those files belong to a checkpoint.
            var resumeExpected =
                transaction.ManagedByOmniLibrary &&
                (
                    transaction.ResumePhase is
                        GogOperationPhases.Downloading or
                        GogOperationPhases.FilesVerified or
                        GogOperationPhases.WindowsSetup ||
                    UnifySteamDownloadStatusStore.IsActivelyTransferring(previous.Status) ||
                    previous.Status is "paused" or "failed"
                ) &&
                HasGogResumeState(gameId, installRoot);
            if (!resumeExpected)
            {
                ManagedGogDlHelper.ClearInstalledState(gameId);
                WriteGogLaunchLog(
                    gameId,
                    "cleared stale per-game gogdl install state before clean download");
            }
            else
            {
                WriteGogLaunchLog(
                    gameId,
                    $"preserving partial download for resume at {previous.ProgressPercent}%");
            }

            var plan = BuildGogDownloadPlan(
                gogdl,
                authPath,
                gameId,
                installRoot,
                Math.Max(
                    previous.DownloadedBytes,
                    transaction.DownloadedBytes),
                includeDlc);
            EnsureDownloadHasSpace(
                "GOG",
                plan.InstallDirectory,
                plan.DiskSizeBytes,
                plan.CompletedBytes);
            var arguments = new List<string>
            {
                "--auth-config-path",
                authPath,
                repairRequested
                    ? "repair"
                    : updateRequested
                        ? "update"
                        : "download",
                gameId,
                "--platform",
                "windows",
                "--path",
                installRoot,
                "--support",
                ManagedGogDlHelper.GetSupportDirectory(gameId),
            };
            arguments.Add(includeDlc ? "--with-dlcs" : "--skip-dlcs");
            arguments.Add("--max-workers");
            arguments.Add(downloadWorkers.ToString(CultureInfo.InvariantCulture));
            ManagedDownloadRunResult? lastResult = null;
            var lastAttempt = 1;
            for (var attempt = 1; attempt <= EpicMaximumDownloadAttempts; attempt++)
            {
                GogOperationJournal.Advance(
                    gameId,
                    GogOperationPhases.Downloading,
                    installRoot: installRoot,
                    downloadedBytes: Math.Max(
                        plan.CompletedBytes,
                        lastResult?.DownloadedBytes ?? 0),
                    totalBytes: plan.DiskSizeBytes,
                    attempt: attempt,
                    detailText: attempt == 1
                        ? repairRequested
                            ? "Verifying and repairing the GOG installation."
                            : updateRequested
                            ? "Downloading the GOG update."
                            : "Downloading the GOG game."
                        : $"Resuming the GOG transfer (attempt {attempt}).");
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "downloading",
                    Math.Max(plan.ProgressPercent, lastResult?.ProgressPercent ?? 0),
                    attempt == 1
                        ? "Starting GOG download."
                        : $"Resuming GOG download (attempt {attempt}/{EpicMaximumDownloadAttempts}).",
                    downloadedBytes: Math.Max(
                        plan.CompletedBytes,
                        lastResult?.DownloadedBytes ?? 0),
                    totalBytes: plan.DiskSizeBytes,
                    attempt: attempt);
                lastAttempt = attempt;
                lastResult = RunHiddenDownloadAndTrack(
                    gogdl,
                    arguments,
                    storeId,
                    gameId,
                    plan,
                    attempt,
                    Math.Clamp(
                        gogStoreConfiguration.DownloadTimeoutSeconds,
                        15,
                        300));
                if (lastResult.ExitCode == 0)
                {
                    break;
                }

                if (attempt >= EpicMaximumDownloadAttempts ||
                    !ShouldRetryManagedDownload(lastResult.Diagnostic))
                {
                    GogOperationJournal.Advance(
                        gameId,
                        GogOperationPhases.Downloading,
                        downloadedBytes: lastResult.DownloadedBytes,
                        totalBytes: lastResult.TotalBytes,
                        attempt: lastAttempt,
                        detailText: lastResult.Diagnostic);
                    GogOperationJournal.Fail(
                        gameId,
                        string.IsNullOrWhiteSpace(lastResult.Diagnostic)
                            ? "GOG download stopped after automatic resume attempts."
                            : lastResult.Diagnostic);
                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        gameId,
                        "failed",
                        lastResult.ProgressPercent,
                        string.IsNullOrWhiteSpace(lastResult.Diagnostic)
                            ? "GOG download stopped after automatic resume attempts."
                            : $"GOG download stopped: {lastResult.Diagnostic}",
                        downloadedBytes: lastResult.DownloadedBytes,
                        totalBytes: lastResult.TotalBytes,
                        downloadBytesPerSecond: lastResult.DownloadBytesPerSecond,
                        attempt: lastAttempt);
                    return 1;
                }

                var retryDelay = TimeSpan.FromSeconds(
                    attempt switch
                    {
                        1 => 15,
                        2 => 30,
                        3 => 60,
                        _ => 120,
                    });
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "reconnecting",
                    lastResult.ProgressPercent,
                    $"GOG connection stopped. Resuming automatically in " +
                    $"{(int)retryDelay.TotalSeconds}s (attempt " +
                    $"{attempt + 1}/{EpicMaximumDownloadAttempts}).",
                    downloadedBytes: lastResult.DownloadedBytes,
                    totalBytes: lastResult.TotalBytes,
                    downloadBytesPerSecond: lastResult.DownloadBytesPerSecond,
                    attempt: attempt);
                Thread.Sleep(retryDelay);
            }

            var actualInstallRoot = FindGogInstallRoot(installRoot, gameId) ?? installRoot;
            var executablePath = ResolveGogExecutablePath(actualInstallRoot, gameId);
            if (!File.Exists(Path.Combine(actualInstallRoot, $"goggame-{gameId}.info")) &&
                (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)))
            {
                GogOperationJournal.Fail(
                    gameId,
                    "GOG finished without a usable installation manifest.");
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "failed",
                    lastResult?.ProgressPercent ?? 0,
                    "GOG finished without a usable installation manifest.");
                return 1;
            }

            // Mark the validated gogdl installation before the elevated setup
            // worker starts. The worker rejects every path that is not owned by
            // this managed install handoff.
            var setupPlan = GogInstallPreparation.ResolvePlan(
                ManagedGogDlHelper.RuntimeConfigPath,
                actualInstallRoot,
                gameId);
            GogOperationJournal.Advance(
                gameId,
                GogOperationPhases.FilesVerified,
                installRoot: actualInstallRoot,
                buildId: setupPlan?.BuildId ?? string.Empty,
                downloadedBytes: plan.DiskSizeBytes,
                totalBytes: plan.DiskSizeBytes,
                attempt: lastAttempt,
                detailText: "The downloaded GOG files were verified.");
            WriteGogManagedInstallMarker(actualInstallRoot, gameId);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                gameId,
                "finalizing",
                99,
                "GOG finished downloading. Completing Windows setup before Play becomes available.",
                downloadedBytes: plan.DiskSizeBytes,
                totalBytes: plan.DiskSizeBytes,
                attempt: lastAttempt);
            GogOperationJournal.Advance(
                gameId,
                GogOperationPhases.WindowsSetup,
                detailText: "Completing GOG Windows setup.");
            if (!GogInstallPreparation.EnsureReady(
                    gogdl,
                    authPath,
                    actualInstallRoot,
                    gameId,
                    message => WriteGogLaunchLog(gameId, message),
                    out var preparationError))
            {
                GogOperationJournal.Fail(gameId, preparationError);
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "failed",
                    99,
                    preparationError);
                return Fail($"GOG could not finish preparing this game: {preparationError}");
            }

            GogOperationJournal.Advance(
                gameId,
                GogOperationPhases.Ready,
                installRoot: actualInstallRoot,
                buildId: setupPlan?.BuildId ?? string.Empty,
                downloadedBytes: plan.DiskSizeBytes,
                totalBytes: plan.DiskSizeBytes,
                attempt: lastAttempt,
                detailText: "The GOG installation is ready.");
            UpdateGogInstalledCache(gameId, actualInstallRoot, executablePath);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                gameId,
                "completed",
                100,
                "Installed.");
            GogOperationJournal.Clear(gameId);
            return 0;
        }
        catch (Exception exception)
        {
            var current = UnifySteamDownloadStatusStore.Get(storeId, gameId);
            if (exception is InvalidOperationException &&
                exception.Message.StartsWith(
                    "Not enough free space",
                    StringComparison.OrdinalIgnoreCase))
            {
                GogOperationJournal.Fail(gameId, exception.Message);
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "failed",
                    current.ProgressPercent,
                    exception.Message,
                    workerProcessId: 0,
                    downloadedBytes: current.DownloadedBytes,
                    totalBytes: current.TotalBytes,
                    attempt: current.Attempt);
                return Fail($"The GOG download failed: {exception.Message}");
            }

            var galaxyClient = failureStage == "helper"
                ? FindGogGalaxyClientPath()
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(galaxyClient))
            {
                OpenGogGalaxyGameView(galaxyClient, gameId);
                GogOperationJournal.BeginInstall(
                    gameId,
                    string.Empty,
                    includeDlc: false,
                    managedByOmniLibrary: false);
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "action-required",
                    current.ProgressPercent,
                    "Managed downloading is unavailable. Continue this installation in the opened GOG Galaxy window.",
                    workerProcessId: 0,
                    downloadedBytes: current.DownloadedBytes,
                    totalBytes: current.TotalBytes,
                    attempt: current.Attempt);
                return 0;
            }

            GogOperationJournal.Fail(gameId, exception.Message);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                gameId,
                "failed",
                current.ProgressPercent,
                exception.Message,
                workerProcessId: 0,
                downloadedBytes: current.DownloadedBytes,
                totalBytes: current.TotalBytes,
                attempt: current.Attempt);
            return Fail($"The GOG download failed: {exception.Message}");
        }
    }

    internal static void AssignDownloadWorkerIfUnclaimed(
        string storeId,
        string gameId,
        int workerProcessId)
    {
        if (workerProcessId <= 0)
        {
            return;
        }

        var current = UnifySteamDownloadStatusStore.Get(storeId, gameId);
        if (!UnifySteamDownloadStatusStore.IsBusyOperation(current.Status) ||
            current.WorkerProcessId > 0)
        {
            return;
        }

        UnifySteamDownloadStatusStore.Update(
            storeId,
            gameId,
            current.Status,
            current.ProgressPercent,
            current.DetailText,
            workerProcessId,
            current.DownloadedBytes,
            current.TotalBytes,
            current.DownloadBytesPerSecond,
            current.DecompressedBytesPerSecond,
            current.DiskWriteBytesPerSecond,
            current.DiskReadBytesPerSecond,
            current.Attempt);
    }

    private static int UninstallGog(string gameId)
    {
        return WithStoreMutationLock(
            GogMutationMutexName,
            "gog-galaxy",
            gameId,
            "uninstalling",
            "Waiting for the current GOG operation to finish before uninstalling.",
            () =>
            {
                const string storeId = "gog-galaxy";
                try
                {
                    var baseDirectory = GetGogPreferredInstallPath();
                    var installRoot = FindKnownGogInstallRoot(baseDirectory, gameId);
                    if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
                    {
                        ClearGogInstalledStateBestEffort(gameId);
                        UpdateGogUninstalledCache(gameId);
                        UnifySteamDownloadStatusStore.Clear(storeId, gameId);
                        GogOperationJournal.Clear(gameId);
                        return 0;
                    }

                    if (IsManagedGogInstallRoot(baseDirectory, installRoot, gameId))
                    {
                        GogOperationJournal.BeginUninstall(
                            gameId,
                            installRoot,
                            managedByOmniLibrary: true);
                        UnifySteamDownloadStatusStore.Update(
                            storeId,
                            gameId,
                            "uninstalling",
                            0,
                            "Removing GOG game files.");
                        Directory.Delete(installRoot, recursive: true);
                        ClearGogInstalledStateBestEffort(gameId);
                        UpdateGogUninstalledCache(gameId);
                        UnifySteamDownloadStatusStore.Clear(storeId, gameId);
                        GogOperationJournal.Clear(gameId);
                        return 0;
                    }

                    var galaxyClient = FindGogGalaxyClientPath();
                    if (string.IsNullOrWhiteSpace(galaxyClient))
                    {
                        UnifySteamDownloadStatusStore.Update(
                            storeId,
                            gameId,
                            "uninstall-failed",
                            0,
                            "This installation is not managed by OmniLibrary. Install GOG Galaxy to remove it safely.");
                        return 1;
                    }

                    OpenGogGalaxyGameView(galaxyClient, gameId);
                    GogOperationJournal.BeginUninstall(
                        gameId,
                        installRoot,
                        managedByOmniLibrary: false);
                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        gameId,
                        "uninstall-action-required",
                        0,
                        "Finish uninstalling this externally managed game in GOG Galaxy.");
                    return 0;
                }
                catch (Exception exception)
                {
                    GogOperationJournal.Fail(gameId, exception.Message);
                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        gameId,
                        "uninstall-failed",
                        0,
                        exception.Message);
                    return Fail($"The GOG uninstall failed: {exception.Message}");
                }
            });
    }

    private static int InstallXbox(string productId)
    {
        const string storeId = "xbox-game-pass";
        if (TryGetInstalledXboxGame(productId, out var alreadyInstalled))
        {
            UpdateXboxInstalledCache(productId, alreadyInstalled);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                productId,
                "completed",
                100,
                "Installed.",
                workerProcessId: 0);
            return 0;
        }

        var currentStatus = UnifySteamDownloadStatusStore.Get(storeId, productId);
        if (UnifySteamDownloadStatusStore.IsActivelyTransferring(currentStatus.Status) ||
            currentStatus.Status == "paused")
        {
            // Steam can invoke the shortcut again while the native button is
            // showing progress. Never queue a second Store request: Windows
            // treats that as a replacement and can cancel the first download.
            if (currentStatus.WorkerProcessId > 0 &&
                IsProcessRunning(currentStatus.WorkerProcessId))
            {
                return 0;
            }

            return TrackXboxInstall(
                storeId,
                productId,
                Math.Max(0, XboxInstallEventTracker.CaptureCursor() - 1));
        }

        if (XboxInstallEventTracker.TryGetRecentState(
                productId,
                TimeSpan.FromMinutes(3),
                out var existingObservation) &&
            existingObservation.Kind is
                XboxInstallEventKind.Queued or
                XboxInstallEventKind.Downloading or
                XboxInstallEventKind.Paused or
                XboxInstallEventKind.Finalizing)
        {
            // Recover an Xbox download that outlived a previous TFS worker or
            // was started in the Xbox app. Attaching is read-only and avoids a
            // duplicate installation request.
            UpdateXboxInstallProgress(storeId, productId, existingObservation);
            return TrackXboxInstall(
                storeId,
                productId,
                Math.Max(0, existingObservation.RecordId - 1));
        }

        var eventCursor = XboxInstallEventTracker.CaptureCursor();
        UnifySteamDownloadStatusStore.Update(
            storeId,
            productId,
            "preparing",
            currentStatus.ProgressPercent,
            "Contacting Windows and Xbox to prepare the installation request.",
            downloadedBytes: currentStatus.DownloadedBytes,
            totalBytes: currentStatus.TotalBytes,
            attempt: currentStatus.Attempt);

        XboxDirectInstallResult directInstall;
        if (XboxDirectInstallCapabilityStore.ShouldAttemptDirectInstall(out var cachedReason))
        {
            directInstall = XboxDirectInstallBroker.TryQueue(productId);
            if (directInstall.Accepted)
            {
                XboxDirectInstallCapabilityStore.MarkSupported();
            }
            else if (directInstall.IsMachineIncompatible)
            {
                XboxDirectInstallCapabilityStore.MarkUnsupported(directInstall.Reason);
            }
        }
        else
        {
            directInstall = new XboxDirectInstallResult(
                false,
                cachedReason,
                5);
            XboxDirectInstallBroker.WriteLog(
                productId,
                $"direct install skipped because this PC is temporarily marked unsupported: {cachedReason}");
        }

        if (directInstall.Accepted)
        {
            UnifySteamDownloadStatusStore.Update(
                storeId,
                productId,
                "queued",
                0,
                "Windows accepted the Xbox download.");
            return TrackXboxInstall(storeId, productId, eventCursor);
        }

        Debug.WriteLine(
            $"Xbox direct install unavailable exit={directInstall.ExitCode}: {directInstall.Reason}");
        XboxDirectInstallBroker.WriteLog(
            productId,
            $"opening assisted product page exit={directInstall.ExitCode} reason={directInstall.Reason}");

        if (TryOpenXboxProductPage(productId, out var productPage))
        {
            XboxDirectInstallBroker.WriteLog(
                productId,
                $"assisted product page opened target={productPage}");
            UnifySteamDownloadStatusStore.Update(
                storeId,
                productId,
                "action-required",
                0,
                "Choose Install on the opened Xbox product page. OmniLibrary will detect and track the download.",
                workerProcessId: 0);
            return 0;
        }

        UnifySteamDownloadStatusStore.Update(
            storeId,
            productId,
            "failed",
            0,
            "Direct installation is unavailable and the Xbox product page could not be opened.",
            workerProcessId: 0);
        return Fail("The Xbox product page could not be opened for this game.");
    }

    private static bool TryOpenXboxProductPage(string productId, out string openedTarget)
    {
        openedTarget = string.Empty;
        var escapedProductId = Uri.EscapeDataString(productId);
        var targets = new[]
        {
            $"msxbox://game/?productId={escapedProductId}",
            $"ms-windows-store://pdp/?ProductId={escapedProductId}",
            $"https://apps.microsoft.com/detail/{escapedProductId}",
        };
        foreach (var target in targets)
        {
            if (!TryOpenShellTarget(target))
            {
                continue;
            }

            openedTarget = target;
            return true;
        }

        return false;
    }

    private static int TrackXboxInstall(
        string storeId,
        string productId,
        long afterRecordId)
    {
        var tracking = XboxInstallEventTracker.Track(
            productId,
            afterRecordId,
            () => TryGetInstalledXboxGame(productId, out _),
            progress => UpdateXboxInstallProgress(storeId, productId, progress));
        if (!tracking.Completed)
        {
            var current = UnifySteamDownloadStatusStore.Get(storeId, productId);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                productId,
                tracking.Canceled ? "canceled" : "failed",
                tracking.Canceled ? 0 : current.ProgressPercent,
                tracking.Canceled
                    ? "Xbox download canceled. No failure occurred."
                    : tracking.Reason,
                workerProcessId: 0,
                downloadedBytes:
                    tracking.Canceled ? 0 : current.DownloadedBytes,
                totalBytes:
                    tracking.Canceled ? 0 : current.TotalBytes,
                attempt: current.Attempt);
            return 1;
        }

        if (!TryGetInstalledXboxGame(productId, out var installedGame))
        {
            UnifySteamDownloadStatusStore.Update(
                storeId,
                productId,
                "failed",
                99,
                "Xbox finished downloading, but the launch registration could not be confirmed.",
                workerProcessId: 0);
            return 1;
        }

        UpdateXboxInstalledCache(productId, installedGame);
        UnifySteamDownloadStatusStore.Update(
            storeId,
            productId,
            "completed",
            100,
            "Installed.",
            workerProcessId: 0);
        return 0;
    }

    private static int RunXbox(string productId)
    {
        if (TryGetInstalledXboxGame(productId, out var game))
        {
            if (!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                File.Exists(game.ExecutablePath))
            {
                return XboxStoreLaunchHost.Run(new XboxStoreLaunchHost.LaunchPayload(
                    game.ExecutablePath,
                    game.InstallPath));
            }

            return InstallXbox(productId);
        }

        var cachedGame = GetXboxCachedGame(productId);
        if (cachedGame?.CloudPlayable == true)
        {
            return TryOpenXboxCloudGame(cachedGame.Title, productId)
                ? 0
                : Fail("Xbox Cloud Gaming could not be opened for this game.");
        }

        return InstallXbox(productId);
    }

    private static void UpdateXboxInstallProgress(
        string storeId,
        string productId,
        XboxInstallEventObservation progress)
    {
        var state = progress.Stage.Trim();
        var status = progress.Kind switch
        {
            XboxInstallEventKind.Queued => "queued",
            XboxInstallEventKind.Paused => "paused",
            XboxInstallEventKind.Finalizing or XboxInstallEventKind.Completed => "finalizing",
            _ when state.Equals("Reconnecting", StringComparison.OrdinalIgnoreCase) =>
                "reconnecting",
            _ => "downloading",
        };
        var percent = progress.Kind is
            XboxInstallEventKind.Finalizing or
            XboxInstallEventKind.Completed
                ? 99
                : Math.Clamp(progress.ProgressPercent, 0, 99);
        var detail = progress.Kind switch
        {
            XboxInstallEventKind.Queued =>
                "Xbox download is queued.",
            XboxInstallEventKind.Paused =>
                $"Xbox download paused at {percent}%.",
            XboxInstallEventKind.Finalizing or XboxInstallEventKind.Completed =>
                "Finalizing Xbox installation.",
            _ when state.Equals("Reconnecting", StringComparison.OrdinalIgnoreCase) =>
                $"Reconnecting to Xbox download at {percent}%.",
            _ when state.Contains("Install", StringComparison.OrdinalIgnoreCase) =>
                $"Installing Xbox game ({percent}%).",
            _ when state.Contains("Restor", StringComparison.OrdinalIgnoreCase) =>
                $"Restoring Xbox game data ({percent}%).",
            _ when percent > 0 =>
                $"Downloading Xbox game ({percent}%).",
            _ =>
                "Xbox download is starting.",
        };

        UnifySteamDownloadStatusStore.Update(
            storeId,
            productId,
            status,
            percent,
            detail);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetInstalledXboxGame(
        string productId,
        out UnifySteamGameCacheEntry game)
    {
        var installedGames = UnifySteamService.LoadXboxInstalledGames(
            GetXboxPreferredInstallPath(),
            forceRefresh: true);
        var catalogGame = GetXboxCachedGame(productId) ??
                          new UnifySteamGameCacheEntry
                          {
                              Id = productId,
                              Title = productId,
                          };
        if (UnifySteamService.TryResolveXboxInstalledGame(
                catalogGame,
                installedGames,
                out var installedGame))
        {
            game = installedGame;
            return true;
        }

        game = default!;
        return false;
    }

    internal static bool TryOpenXboxCloudGame(string title, string productId)
    {
        return TryOpenShellTarget(BuildXboxCloudLaunchUrl(title, productId));
    }

    internal static string BuildXboxCloudLaunchUrl(string title, string productId)
    {
        var locale = CultureInfo.CurrentUICulture.Name;
        if (!Regex.IsMatch(
                locale,
                "^[a-z]{2}-[a-z]{2}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            locale = "en-US";
        }

        var slug = Regex.Replace(
                (title ?? string.Empty).Trim().ToLowerInvariant(),
                @"[^\p{L}\p{Nd}]+",
                "-",
                RegexOptions.CultureInvariant)
            .Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "game";
        }

        return $"https://www.xbox.com/{locale.ToLowerInvariant()}/play/launch/" +
               $"{Uri.EscapeDataString(slug)}/{Uri.EscapeDataString(productId)}";
    }

    private static UnifySteamGameCacheEntry? GetXboxCachedGame(string productId)
    {
        try
        {
            var configuration = LoadStoreSyncConfiguration();
            return configuration.UnifySteam.Stores.TryGetValue("xbox-game-pass", out var store)
                ? store?.Cache?.Games?.FirstOrDefault(game =>
                    game is not null &&
                    string.Equals(game.Id, productId, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetXboxPreferredInstallPath()
    {
        try
        {
            var settings = new StoreSyncSettingsStore(
                Path.Combine(AppContext.BaseDirectory, "data", "store-sync.json")).Load();
            return settings.UnifySteam.Stores.TryGetValue("xbox-game-pass", out var store)
                ? store?.InstallPath ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int RunEpic(string appName)
    {
        var legendary = ResolveLegendaryTool(installWhenMissing: true);
        var cachedGame = GetEpicCachedGame(appName);
        if (cachedGame?.DeliveryProvider == "ea-app")
        {
            return OpenEaAppAction(
                legendary,
                cachedGame,
                installationRequested: !cachedGame.Installed);
        }

        if (cachedGame?.IsPreloaded == true)
        {
            UnifySteamDownloadStatusStore.Update(
                "epic-games",
                appName,
                "action-required",
                100,
                "This preload is complete, but Epic has not unlocked the release yet.");
            return 0;
        }

        var epicManagedInstall = IsEpicGameInstalled(legendary, appName);
        if (ShouldEnsureUbisoftAccountLink(cachedGame) &&
            !EnsureUbisoftAccountLink(
                legendary,
                cachedGame!,
                out var accountLinkDetail))
        {
            UnifySteamDownloadStatusStore.Update(
                "epic-games",
                appName,
                "action-required",
                0,
                accountLinkDetail);
            return 0;
        }

        if (cachedGame?.RequiresExternalLauncher == true)
        {
            return OpenExternalPublisherAction(
                cachedGame,
                cachedGame.Installed
                    ? ExternalPublisherOperation.Launch
                    : ExternalPublisherOperation.Install);
        }

        if (!epicManagedInstall)
        {
            // The native Steam action currently reads Download. Finish that action
            // without launching the game automatically.
            return InstallEpic(appName);
        }

        if (cachedGame is not null &&
            !string.IsNullOrWhiteSpace(cachedGame.LatestVersion) &&
            !string.IsNullOrWhiteSpace(cachedGame.Version) &&
            !string.Equals(
                cachedGame.LatestVersion,
                cachedGame.Version,
                StringComparison.OrdinalIgnoreCase))
        {
            return InstallEpic(appName);
        }

        if (!CompleteEpicWindowsSetup(legendary, cachedGame, appName))
        {
            return 1;
        }
        UnifySteamDownloadStatusStore.Clear("epic-games", appName);
        TrySyncEpicCloudSaves(
            legendary,
            cachedGame,
            appName,
            "before launch");

        var executablePath = TryGetEpicExecutablePath(legendary, appName);
        var launchArguments = new List<string> { "launch", appName };
        var requiresEpicLauncherBridge =
            cachedGame?.RequiresEpicLauncherBridge == true ||
            EpicCompatibilityCatalog.Get(appName).FakeEpicLauncher;
        var epicLauncherBridgePath = string.Empty;
        if (requiresEpicLauncherBridge)
        {
            try
            {
                epicLauncherBridgePath = ManagedEpicLauncherBridge.EnsureInstalled();
            }
            catch (Exception exception)
            {
                return Fail(
                    "The Rockstar compatibility component could not be prepared. " +
                    $"Check your internet connection and try again. ({exception.Message})");
            }
        }

        // Legendary handles the Epic exchange token and publisher launch
        // arguments. Cloud saves remain a separate, conflict-sensitive action
        // and are never overwritten silently here.
        var launchResult = RunHiddenAndWait(
            legendary,
            launchArguments,
            waitForExit: false,
            epicLauncherBridgePath: epicLauncherBridgePath);
        if (launchResult != 0)
        {
            return Fail("Epic could not start this game. Check the Epic and Rockstar sign-ins and try again.");
        }

        // Keep this process alive while the game runs so Steam shows it as running.
        if (cachedGame is not null &&
            !string.IsNullOrWhiteSpace(cachedGame.InstallPath))
        {
            WaitForEpicGameSession(
                cachedGame.InstallPath,
                executablePath,
                cachedGame.ProcessNames);
        }
        else if (!string.IsNullOrWhiteSpace(executablePath))
        {
            WaitForProcessByPath(executablePath);
        }

        TrySyncEpicCloudSaves(
            legendary,
            cachedGame,
            appName,
            "after exit");
        return 0;
    }

    internal static bool RequiresEpicLauncherBridge(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        return GetEpicCachedGame(appName)?.RequiresEpicLauncherBridge == true ||
               EpicCompatibilityCatalog.Get(appName).FakeEpicLauncher;
    }

    private static bool CompleteEpicWindowsSetup(
        string legendary,
        UnifySteamGameCacheEntry? cachedGame,
        string appName)
    {
        UnifySteamDownloadStatusStore.Update(
            "epic-games",
            appName,
            "finalizing",
            99,
            "Finishing required Windows setup.");
        if (!EpicInstallPreparation.EnsureReady(
                legendary,
                cachedGame,
                appName,
                out var preparationSignature,
                out var error))
        {
            UnifySteamDownloadStatusStore.Update(
                "epic-games",
                appName,
                "action-required",
                99,
                error);
            ShowError(error);
            return false;
        }

        UpdateEpicInstalledCache(
            legendary,
            appName,
            preparationSignature);
        return true;
    }

    private static void TrySyncEpicCloudSaves(
        string legendary,
        UnifySteamGameCacheEntry? game,
        string appName,
        string phase)
    {
        if (game?.SupportsCloudSaves != true ||
            game.DeliveryProvider != "epic")
        {
            return;
        }

        try
        {
            var exitCode = RunHiddenAndWait(
                legendary,
                ["-y", "sync-saves", appName, "--accept-path"]);
            WriteEpicDownloadLog(
                appName,
                0,
                exitCode == 0
                    ? $"cloud save sync {phase} completed"
                    : $"cloud save sync {phase} returned exit code {exitCode}; launch continued");
        }
        catch (Exception exception)
        {
            // Saves must never prevent a game from launching. Legendary's
            // non-interactive mode already leaves timestamp conflicts untouched.
            WriteEpicDownloadLog(
                appName,
                0,
                $"cloud save sync {phase} skipped: {exception.Message}");
        }
    }

    private static UnifySteamGameCacheEntry? GetEpicCachedGame(string appName)
    {
        try
        {
            var configuration = LoadStoreSyncConfiguration();
            var game = configuration.UnifySteam.Stores.TryGetValue(
                    "epic-games",
                    out var store)
                ? store?.Cache?.Games?.FirstOrDefault(game =>
                    game is not null &&
                    string.Equals(
                        game.Id,
                        appName,
                        StringComparison.OrdinalIgnoreCase))
                : null;
            if (game is not null)
            {
                UnifySteamService.NormalizeEpicDeliveryCapabilities(game);
            }

            if (game?.RequiresExternalLauncher == true &&
                !string.IsNullOrWhiteSpace(game.RegistryPath))
            {
                game.Installed = UnifySteamService.TryReadEpicExternalInstallPath(
                    game.RegistryPath,
                    game.RegistryValueName,
                    out var installPath);
                if (game.Installed)
                {
                    game.InstallPath = installPath;
                }
                else
                {
                    game.InstallPath = string.Empty;
                    game.ExecutablePath = string.Empty;
                    game.Version = string.Empty;
                }
            }

            return game;
        }
        catch
        {
            return null;
        }
    }

    private static bool EnsureUbisoftAccountLink(
        string legendary,
        UnifySteamGameCacheEntry game,
        out string detail)
    {
        detail = string.Empty;
        try
        {
            var state = GetUbisoftAccountLinkState(
                RunHiddenAndCapture(
                    legendary,
                    "activate",
                    "--uplay",
                    "--summary",
                    "--json"),
                game.Id);
            if (state == UbisoftAccountLinkState.Activated)
            {
                return true;
            }

            if (state == UbisoftAccountLinkState.NotEligible)
            {
                detail =
                    "Epic is linked with Ubisoft Connect, but Ubisoft did not expose this title for activation. " +
                    "Open Ubisoft Connect and confirm that the same Ubisoft account is linked to Epic.";
                return false;
            }

            // With no linked account Legendary opens Epic's official Ubisoft-link page.
            // With a linked account it redeems the outstanding Ubisoft entitlements.
            var exitCode = RunHiddenAndWait(
                legendary,
                ["-y", "activate", "--uplay"]);
            if (exitCode != 0)
            {
                detail =
                    "Epic could not start Ubisoft account linking. Check the Epic sign-in and try again.";
                return false;
            }

            if (state == UbisoftAccountLinkState.LinkRequired)
            {
                detail =
                    "Complete the Epic-Ubisoft account link in the browser that just opened. " +
                    "Then select Link Ubisoft again; OmniLibrary will continue with this title.";
                return false;
            }

            // Ubisoft's redemption endpoint can be briefly eventually consistent.
            // Confirm this exact title instead of trusting Legendary's process exit code.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    Thread.Sleep(750);
                }

                var refreshedState = GetUbisoftAccountLinkState(
                    RunHiddenAndCapture(
                        legendary,
                        "activate",
                        "--uplay",
                        "--summary",
                        "--json"),
                    game.Id);
                if (refreshedState == UbisoftAccountLinkState.Activated)
                {
                    return true;
                }
            }

            detail =
                "Epic is linked with Ubisoft Connect, but Ubisoft is still confirming this title. " +
                "Wait a moment, then select Link Ubisoft again.";
            return false;
        }
        catch (Exception exception)
        {
            detail =
                $"Ubisoft account linking could not be checked: {exception.Message}";
            return false;
        }
    }

    internal static bool ShouldEnsureUbisoftAccountLink(
        UnifySteamGameCacheEntry? game)
    {
        return game?.DeliveryProvider == "ubisoft-connect" &&
               !game.Installed;
    }

    internal static UbisoftAccountLinkState GetUbisoftAccountLinkState(
        string? summary,
        string appName)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return UbisoftAccountLinkState.LinkRequired;
        }

        try
        {
            using var document = JsonDocument.Parse(summary);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return UbisoftAccountLinkState.LinkRequired;
            }

            if (ContainsUbisoftActivationEntry(
                    document.RootElement,
                    "activated",
                    appName))
            {
                return UbisoftAccountLinkState.Activated;
            }

            if (ContainsUbisoftActivationEntry(
                    document.RootElement,
                    "redeemable",
                    appName))
            {
                return UbisoftAccountLinkState.Redeemable;
            }

            return UbisoftAccountLinkState.NotEligible;
        }
        catch (JsonException)
        {
            return UbisoftAccountLinkState.LinkRequired;
        }
    }

    private static bool ContainsUbisoftActivationEntry(
        JsonElement summary,
        string propertyName,
        string appName)
    {
        if (!summary.TryGetProperty(propertyName, out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return entries.EnumerateArray().Any(entry =>
            entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty("app_name", out var appNameNode) &&
            appNameNode.ValueKind == JsonValueKind.String &&
            string.Equals(
                appNameNode.GetString(),
                appName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static int OpenEaAppAction(
        string legendary,
        UnifySteamGameCacheEntry game,
        bool installationRequested)
    {
        const string storeId = "epic-games";
        var availability =
            EaAppIntegration.GetAvailability(forceRefresh: true);
        if (!availability.IsAvailable)
        {
            var openedDownloadPage =
                TryOpenShellTarget(EaAppIntegration.OfficialDownloadUrl);
            UnifySteamDownloadStatusStore.Update(
                storeId,
                game.Id,
                openedDownloadPage ? "action-required" : "failed",
                0,
                openedDownloadPage
                    ? "Install the EA app from the official page that just opened. " +
                      "Sign in, then select Link EA again. Your Epic and EA passwords stay inside their official sign-in windows."
                    : "The EA app is required, but its official download page could not be opened.",
                workerProcessId: 0,
                gameTitle: game.Title,
                steamAppId: game.SteamAppId);
            return openedDownloadPage ? 0 : 1;
        }

        UnifySteamDownloadStatusStore.Update(
            storeId,
            game.Id,
            "preparing",
            0,
            "Creating a secure, short-lived Epic handoff for the EA app.",
            gameTitle: game.Title,
            steamAppId: game.SteamAppId);

        Uri handoffUri;
        try
        {
            var handoffJson = RunHiddenAndCapture(
                legendary,
                "launch",
                game.Id,
                "--origin",
                "--json");
            if (!EaAppIntegration.TryParseHandoffUri(
                    handoffJson,
                    game.Id,
                    out handoffUri))
            {
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    game.Id,
                    "failed",
                    0,
                    "Epic could not create the EA account handoff for this title. " +
                    "Reconnect Epic in OmniLibrary, verify that the game is still owned, and retry.",
                    workerProcessId: 0,
                    gameTitle: game.Title,
                    steamAppId: game.SteamAppId);
                return 1;
            }
        }
        catch (Exception exception)
        {
            UnifySteamDownloadStatusStore.Update(
                storeId,
                game.Id,
                "failed",
                0,
                "Epic could not create the EA account handoff. " +
                $"Check the Epic connection and retry. ({exception.Message})",
                workerProcessId: 0,
                gameTitle: game.Title,
                steamAppId: game.SteamAppId);
            return 1;
        }

        // The URI contains a short-lived Epic exchange code. It is handed
        // directly to Windows and is deliberately never persisted or logged.
        if (!TryOpenShellTarget(handoffUri.AbsoluteUri))
        {
            if (!string.IsNullOrWhiteSpace(availability.ExecutablePath))
            {
                RunVisibleAndWait(
                    availability.ExecutablePath,
                    [],
                    waitForExit: false);
            }

            UnifySteamDownloadStatusStore.Update(
                storeId,
                game.Id,
                "action-required",
                0,
                "The EA app is installed, but Windows did not accept its secure Epic link. " +
                "Repair or reinstall the EA app, then select Link EA again.",
                workerProcessId: 0,
                gameTitle: game.Title,
                steamAppId: game.SteamAppId);
            return 0;
        }

        if (installationRequested || !game.Installed)
        {
            UnifySteamDownloadStatusStore.Update(
                storeId,
                game.Id,
                "action-required",
                0,
                $"EA app opened for {game.Title}. Sign in with the EA account you want linked to Epic, " +
                "approve the first-time link if requested, then start or continue installation. " +
                "Progress remains visible in the EA app; OmniLibrary detects completion from the official EA registry entry.",
                workerProcessId: 0,
                gameTitle: game.Title,
                steamAppId: game.SteamAppId);
            return 0;
        }

        UnifySteamDownloadStatusStore.Clear(storeId, game.Id);
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            WaitForProcessByPath(game.ExecutablePath);
        }
        else
        {
            WaitForProcessNames(game.ProcessNames);
        }

        return 0;
    }

    private static int OpenEaAppForUninstall(
        UnifySteamGameCacheEntry game)
    {
        var eaApp = FindEaAppPath();
        if (string.IsNullOrWhiteSpace(eaApp))
        {
            UnifySteamDownloadStatusStore.Update(
                "epic-games",
                game.Id,
                "uninstall-failed",
                0,
                "The EA app was not found. Install or repair the EA app, then retry.");
            return 1;
        }

        RunVisibleAndWait(eaApp, [], waitForExit: false);
        UnifySteamDownloadStatusStore.Update(
            "epic-games",
            game.Id,
            "uninstall-action-required",
            0,
            "Finish uninstalling this Epic-owned game in the EA app. " +
            "OmniLibrary will detect its registry removal automatically.");
        return 0;
    }

    private static int OpenExternalPublisherAction(
        UnifySteamGameCacheEntry game,
        ExternalPublisherOperation operation)
    {
        var provider = UnifySteamService.GetEpicProviderDisplayName(
            game.DeliveryProvider);
        var opened = game.DeliveryProvider == "ubisoft-connect"
            ? OpenUbisoftConnect(
                game,
                openProduct: operation != ExternalPublisherOperation.Uninstall)
            : OpenEpicGamesLauncher(game.Id);
        if (!opened)
        {
            if (operation == ExternalPublisherOperation.Launch)
            {
                return Fail($"{provider} could not be opened.");
            }

            UnifySteamDownloadStatusStore.Update(
                "epic-games",
                game.Id,
                operation == ExternalPublisherOperation.Install
                    ? "failed"
                    : "uninstall-failed",
                0,
                $"{provider} could not be opened.");
            return 1;
        }

        if (operation == ExternalPublisherOperation.Launch)
        {
            UnifySteamDownloadStatusStore.Clear("epic-games", game.Id);
            return 0;
        }

        UnifySteamDownloadStatusStore.Update(
            "epic-games",
            game.Id,
            operation == ExternalPublisherOperation.Install
                ? "action-required"
                : "uninstall-action-required",
            0,
            operation == ExternalPublisherOperation.Install
                ? $"Continue installation in {provider}. OmniLibrary will detect the completed installation automatically."
                : $"Finish uninstalling this game in {provider}. OmniLibrary will detect its removal automatically.");
        return 0;
    }

    private static bool OpenUbisoftConnect(
        UnifySteamGameCacheEntry game,
        bool openProduct)
    {
        if (openProduct &&
            !string.IsNullOrWhiteSpace(game.ProviderGameId) &&
            TryOpenShellTarget(
                $"uplay://launch/{Uri.EscapeDataString(game.ProviderGameId)}/0"))
        {
            return true;
        }

        var client = FindUbisoftConnectPath();
        return !string.IsNullOrWhiteSpace(client) &&
               RunVisibleAndWait(client, [], waitForExit: false) == 0;
    }

    private static bool OpenEpicGamesLauncher(string appName)
    {
        var launcher = FindEpicGamesLauncherPath();
        return !string.IsNullOrWhiteSpace(launcher) &&
               RunEpicGamesLauncher(launcher, appName) == 0;
    }

    internal static void ConfigureEpicLauncherBridge(
        ProcessStartInfo startInfo,
        string bridgePath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (string.IsNullOrWhiteSpace(bridgePath))
        {
            throw new ArgumentException(
                "The Epic launcher bridge path is required.",
                nameof(bridgePath));
        }

        // Legendary deliberately handles this environment variable without
        // shlex parsing. Passing a Windows path through --wrapper instead strips
        // its backslashes and prevents the fake Epic parent process from starting.
        startInfo.Environment["LEGENDARY_WRAPPER_EXE"] =
            Path.GetFullPath(bridgePath);
        RemoveInheritedSteamLaunchContext(startInfo);
    }

    private static int RunEpicGamesLauncher(string epicLauncher, string appName)
    {
        var launchUri = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true";
        if (TryOpenShellTarget(launchUri))
        {
            return 0;
        }

        RunVisibleAndWait(epicLauncher, [launchUri], waitForExit: false);
        return 0;
    }

    private static int RunGog(string gameId)
    {
        var baseDirectory = GetGogPreferredInstallPath();
        Directory.CreateDirectory(baseDirectory);

        var installRoot = FindKnownGogInstallRoot(baseDirectory, gameId);
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
        {
            return InstallGog(gameId);
        }

        var configuration = LoadStoreSyncConfiguration();
        var updateAvailable =
            configuration.UnifySteam.Stores.TryGetValue(
                "gog-galaxy",
                out var configuredGogStore) &&
            configuredGogStore.Cache?.Games?.FirstOrDefault(game =>
                game is not null &&
                string.Equals(
                    game.Id,
                    gameId,
                    StringComparison.OrdinalIgnoreCase)) is { } configuredGame &&
            configuredGame.Installed &&
            !string.IsNullOrWhiteSpace(configuredGame.Version) &&
            !string.IsNullOrWhiteSpace(configuredGame.LatestVersion) &&
            !string.Equals(
                configuredGame.Version,
                configuredGame.LatestVersion,
                StringComparison.OrdinalIgnoreCase);
        if (updateAvailable)
        {
            return InstallGog(gameId);
        }

        var gogdl = ResolveGogTool(installWhenMissing: false);
        var authPath = FindGogAuthPath();
        if (IsManagedGogInstall(installRoot, gameId))
        {
            UnifySteamDownloadStatusStore.Update(
                "gog-galaxy",
                gameId,
                "downloading",
                99,
                "Finishing GOG Windows setup.");
            if (!GogInstallPreparation.EnsureReady(
                    gogdl,
                    authPath,
                    installRoot,
                    gameId,
                    message => WriteGogLaunchLog(gameId, message),
                    out var preparationError))
            {
                UnifySteamDownloadStatusStore.Update(
                    "gog-galaxy",
                    gameId,
                    "failed",
                    0,
                    preparationError);
                WriteGogLaunchLog(
                    gameId,
                    $"Windows setup failed error={NormalizeGogLaunchDiagnostic(preparationError)}");
                return Fail($"GOG could not finish preparing this game: {preparationError}");
            }

            UnifySteamDownloadStatusStore.Update(
                "gog-galaxy",
                gameId,
                "completed",
                100,
                "Installed.");
        }

        var launchTask = ResolveGogLaunchTask(installRoot, gameId);
        var executablePath = launchTask?.ExecutablePath ?? ResolveGogExecutablePath(installRoot, gameId);
        var preferGalaxy =
            configuration.UnifySteam.Stores.TryGetValue(
                "gog-galaxy",
                out var gogConfiguration) &&
            gogConfiguration.PreferGogGalaxyForLaunch;
        if (preferGalaxy)
        {
            var preferredGalaxyClient = FindGogGalaxyClientPath();
            if (!string.IsNullOrWhiteSpace(preferredGalaxyClient))
            {
                WriteGogLaunchLog(
                    gameId,
                    "delegating launch to GOG Galaxy for Galaxy features");
                return RunGogGalaxy(
                    preferredGalaxyClient,
                    gameId,
                    installRoot,
                    executablePath);
            }

            WriteGogLaunchLog(
                gameId,
                "GOG Galaxy launch preference is enabled, but Galaxy is unavailable; using direct launch");
        }
        string helperFailure = string.Empty;
        if (launchTask?.RequiresElevation == true)
        {
            WriteGogLaunchLog(
                gameId,
                $"elevated manifest launch requested task={launchTask.Index} executable={launchTask.ExecutablePath}");
            if (TryRunGogLaunchTask(launchTask, out var elevatedLaunchError))
            {
                WriteGogLaunchLog(
                    gameId,
                    $"elevated manifest launch completed executable={launchTask.ExecutablePath}");
                return 0;
            }

            helperFailure = NormalizeGogLaunchDiagnostic(elevatedLaunchError);
            WriteGogLaunchLog(
                gameId,
                $"elevated manifest launch failed error={helperFailure}");
        }

        if (!string.IsNullOrWhiteSpace(gogdl) &&
            !string.IsNullOrWhiteSpace(authPath) &&
            launchTask is not null &&
            !launchTask.RequiresElevation)
        {
            WriteGogLaunchLog(
                gameId,
                $"helper launch requested task={launchTask.Index} category={launchTask.Category} " +
                $"primary={launchTask.IsPrimary} executable={launchTask.ExecutablePath}");
            var helperResult = RunGogHelperAndTrack(
                gogdl,
                launchTask,
                [
                    "--auth-config-path",
                    authPath,
                    "launch",
                    installRoot,
                    gameId,
                    "--platform",
                    "windows",
                    "--prefer-task",
                    launchTask.Index.ToString(CultureInfo.InvariantCulture),
                ]);
            WriteGogLaunchLog(
                gameId,
                $"helper launch finished exit={helperResult.ExitCode} " +
                $"observed={helperResult.TargetProcessObserved} " +
                $"durationMs={(long)helperResult.Duration.TotalMilliseconds} " +
                $"output={NormalizeGogLaunchDiagnostic(helperResult.DiagnosticOutput)}");

            // gogdl waits for the selected task on Windows. Once the game was observed,
            // or the helper stayed alive long enough to represent a real game session,
            // a non-zero game exit code must not trigger a second launch.
            if (helperResult.ExitCode == 0 ||
                helperResult.TargetProcessObserved ||
                helperResult.Duration.TotalMilliseconds >= GogLaunchFallbackThresholdMilliseconds)
            {
                return 0;
            }

            helperFailure = NormalizeGogLaunchDiagnostic(helperResult.DiagnosticOutput);
            WriteGogLaunchLog(gameId, "helper failed before a game process was established; trying manifest fallback");
        }

        var manifestFailure = string.Empty;
        if (launchTask is not null &&
            !launchTask.RequiresElevation &&
            TryRunGogLaunchTask(launchTask, out manifestFailure))
        {
            WriteGogLaunchLog(gameId, $"manifest fallback completed executable={launchTask.ExecutablePath}");
            return 0;
        }
        else if (!string.IsNullOrWhiteSpace(manifestFailure))
        {
            WriteGogLaunchLog(
                gameId,
                $"manifest fallback failed executable={launchTask?.ExecutablePath ?? "<none>"} " +
                $"error={NormalizeGogLaunchDiagnostic(manifestFailure)}");
        }

        if (!string.IsNullOrWhiteSpace(executablePath) &&
            File.Exists(executablePath) &&
            launchTask?.RequiresElevation != true)
        {
            try
            {
                return RunGogExecutable(executablePath, installRoot);
            }
            catch (Exception exception)
            {
                WriteGogLaunchLog(
                    gameId,
                    $"resolved executable fallback failed executable={executablePath} error={exception.Message}");
            }
        }

        var galaxyClient = FindGogGalaxyClientPath();
        if (!string.IsNullOrWhiteSpace(galaxyClient))
        {
            return !string.IsNullOrWhiteSpace(installRoot)
                ? RunGogGalaxy(galaxyClient, gameId, installRoot, executablePath)
                : OpenGogGalaxyGameView(galaxyClient, gameId);
        }

        if (string.IsNullOrWhiteSpace(gogdl))
        {
            return Fail("The GOG helper is unavailable and no usable game executable was found.");
        }

        if (string.IsNullOrWhiteSpace(authPath))
        {
            return Fail("No GOG sign-in data was found. Connect GOG in OmniLibrary first.");
        }

        if (!string.IsNullOrWhiteSpace(helperFailure))
        {
            return Fail($"GOG could not start this game. Refresh OmniLibrary and try again. Details: {helperFailure}");
        }

        return Fail("The GOG game executable could not be resolved. Refresh OmniLibrary or start the title once in GOG Galaxy.");
    }

    private static GogHelperLaunchResult RunGogHelperAndTrack(
        string gogdl,
        GogLaunchTask launchTask,
        IReadOnlyList<string> arguments)
    {
        var diagnosticLines = new List<string>();
        var diagnosticLock = new object();
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(gogdl, arguments, visible: false, redirectOutput: true),
            EnableRaisingEvents = true,
        };

        void CaptureLine(string? value)
        {
            var line = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (diagnosticLock)
            {
                const int maximumLines = 40;
                if (diagnosticLines.Count >= maximumLines)
                {
                    diagnosticLines.RemoveAt(0);
                }

                diagnosticLines.Add(line);
            }
        }

        process.OutputDataReceived += (_, eventArgs) => CaptureLine(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureLine(eventArgs.Data);

        if (!process.Start())
        {
            return new GogHelperLaunchResult(1, false, stopwatch.Elapsed, "gogdl did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var targetProcessObserved = false;
        while (!process.WaitForExit(250))
        {
            targetProcessObserved |= IsProcessRunningByPath(launchTask.ExecutablePath);
        }

        // Flush asynchronous stdout/stderr events after process termination.
        process.WaitForExit();
        targetProcessObserved |= IsProcessRunningByPath(launchTask.ExecutablePath);
        stopwatch.Stop();

        string diagnosticOutput;
        lock (diagnosticLock)
        {
            diagnosticOutput = string.Join(" | ", diagnosticLines);
        }

        return new GogHelperLaunchResult(
            process.ExitCode,
            targetProcessObserved,
            stopwatch.Elapsed,
            diagnosticOutput);
    }

    private static bool TryRunGogLaunchTask(GogLaunchTask launchTask, out string error)
    {
        error = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = launchTask.ExecutablePath,
                WorkingDirectory = launchTask.WorkingDirectory,
                UseShellExecute = true,
            };
            if (launchTask.RequiresElevation)
            {
                startInfo.Verb = "runas";
            }

            if (launchTask.RawArguments is not null)
            {
                startInfo.Arguments = launchTask.RawArguments;
            }
            else
            {
                foreach (var argument in launchTask.ArgumentList)
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "The manifest executable did not create a process.";
                return false;
            }

            process.WaitForExit();
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static int RunGogExecutable(string executablePath, string? installRoot)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? installRoot ?? string.Empty,
            UseShellExecute = true,
        });
        process?.WaitForExit();
        return 0;
    }

    private static int RunGogGalaxy(string galaxyClient, string gameId, string? installRoot, string executablePath)
    {
        var arguments = new List<string>
        {
            "/command=runGame",
            $"/gameId={gameId}",
        };

        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            arguments.Add($"/path={installRoot}");
        }

        RunVisibleAndWait(galaxyClient, arguments, waitForExit: false);

        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            WaitForProcessByPath(executablePath);
        }

        return 0;
    }

    private static int OpenGogGalaxyGameView(string galaxyClient, string gameId)
    {
        var gameViewUrl = $"goggalaxy://openGameView/{gameId}";
        if (TryOpenShellTarget(gameViewUrl))
        {
            return 0;
        }

        // If the protocol registration is stale, invoke Galaxy's protocol bridge directly.
        RunVisibleAndWait(galaxyClient, [$"/urlProtocol={gameViewUrl}"], waitForExit: false);
        return 0;
    }

    private static bool IsEpicGameInstalled(string legendary, string appName)
    {
        try
        {
            var output = RunHiddenAndCapture(legendary, "list-installed", "--json");
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("app_name", out var appNameNode) &&
                    string.Equals(appNameNode.GetString(), appName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to install; legendary install verifies existing files anyway.
        }

        return false;
    }

    private static string TryGetEpicExecutablePath(string legendary, string appName)
    {
        try
        {
            var output = RunHiddenAndCapture(legendary, "list-installed", "--json");
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("app_name", out var appNameNode) ||
                    !string.Equals(appNameNode.GetString(), appName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var installPath = item.TryGetProperty("install_path", out var installPathNode)
                    ? installPathNode.GetString() ?? string.Empty
                    : string.Empty;
                var executable = item.TryGetProperty("executable", out var executableNode)
                    ? executableNode.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return string.Empty;
                }

                return Path.IsPathRooted(executable)
                    ? executable
                    : Path.Combine(installPath, executable);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string? FindGogInstallRoot(string baseDirectory, string gameId)
    {
        try
        {
            var infoFileName = $"goggame-{gameId}.info";
            var match = Directory
                .EnumerateFiles(baseDirectory, infoFileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            return match is null ? null : Path.GetDirectoryName(match);
        }
        catch
        {
            return null;
        }
    }

    internal static GogLaunchTask? ResolveGogLaunchTask(string installRoot, string gameId)
    {
        try
        {
            var normalizedInstallRoot = Path.GetFullPath(installRoot);
            var infoPath = Path.Combine(installRoot, $"goggame-{gameId}.info");
            if (!File.Exists(infoPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(infoPath));
            if (!document.RootElement.TryGetProperty("playTasks", out var playTasks) ||
                playTasks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var candidates = new List<GogLaunchTask>();
            var index = -1;
            foreach (var task in playTasks.EnumerateArray())
            {
                index += 1;
                if (task.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (task.TryGetProperty("type", out var typeNode) &&
                    typeNode.ValueKind == JsonValueKind.String &&
                    !string.Equals(typeNode.GetString(), "FileTask", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!task.TryGetProperty("path", out var pathNode) ||
                    pathNode.ValueKind != JsonValueKind.String ||
                    !TryResolvePathWithinDirectory(
                        normalizedInstallRoot,
                        pathNode.GetString(),
                        out var executablePath) ||
                    !File.Exists(executablePath))
                {
                    continue;
                }

                var workingDirectory = Path.GetDirectoryName(executablePath) ?? normalizedInstallRoot;
                if (task.TryGetProperty("workingDir", out var workingDirectoryNode) &&
                    workingDirectoryNode.ValueKind == JsonValueKind.String &&
                    TryResolvePathWithinDirectory(
                        normalizedInstallRoot,
                        workingDirectoryNode.GetString(),
                        out var resolvedWorkingDirectory) &&
                    Directory.Exists(resolvedWorkingDirectory))
                {
                    workingDirectory = resolvedWorkingDirectory;
                }

                string? rawArguments = null;
                IReadOnlyList<string> argumentList = [];
                if (task.TryGetProperty("arguments", out var argumentsNode))
                {
                    if (argumentsNode.ValueKind == JsonValueKind.String)
                    {
                        rawArguments = argumentsNode.GetString() ?? string.Empty;
                    }
                    else if (argumentsNode.ValueKind == JsonValueKind.Array)
                    {
                        argumentList = argumentsNode
                            .EnumerateArray()
                            .Where(argument => argument.ValueKind == JsonValueKind.String)
                            .Select(argument => argument.GetString() ?? string.Empty)
                            .ToArray();
                    }
                }

                var category = task.TryGetProperty("category", out var categoryNode) &&
                               categoryNode.ValueKind == JsonValueKind.String
                    ? categoryNode.GetString() ?? string.Empty
                    : string.Empty;
                var isPrimary = task.TryGetProperty("isPrimary", out var isPrimaryNode) &&
                                isPrimaryNode.ValueKind == JsonValueKind.True;
                var compatibilityFlags =
                    task.TryGetProperty("compatibilityFlags", out var compatibilityNode)
                        ? compatibilityNode.ValueKind switch
                        {
                            JsonValueKind.String =>
                                compatibilityNode.GetString() ?? string.Empty,
                            JsonValueKind.Array =>
                                string.Join(
                                    ' ',
                                    compatibilityNode
                                        .EnumerateArray()
                                        .Where(value => value.ValueKind == JsonValueKind.String)
                                        .Select(value => value.GetString() ?? string.Empty)),
                            _ => string.Empty,
                        }
                        : string.Empty;
                candidates.Add(
                    new GogLaunchTask(
                        index,
                        executablePath,
                        workingDirectory,
                        rawArguments,
                        argumentList,
                        category,
                        isPrimary,
                        compatibilityFlags));
            }

            // Some older GOG manifests mark a launcher as primary and keep the actual
            // game executable in a hidden "game" task. Prefer the real game task while
            // retaining manifest order so alternate/safe-mode tasks are never selected.
            return candidates
                .OrderByDescending(candidate =>
                    string.Equals(candidate.Category, "game", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(candidate => candidate.IsPrimary)
                .ThenBy(candidate => candidate.Index)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolvePathWithinDirectory(
        string directory,
        string? relativeOrAbsolutePath,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            return false;
        }

        try
        {
            var normalizedDirectory = Path.GetFullPath(directory);
            var normalizedCandidateValue = relativeOrAbsolutePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(
                Path.IsPathRooted(normalizedCandidateValue)
                    ? normalizedCandidateValue
                    : Path.Combine(normalizedDirectory, normalizedCandidateValue));
            var relative = Path.GetRelativePath(normalizedDirectory, candidate);
            if (Path.IsPathRooted(relative) ||
                string.Equals(relative, "..", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return false;
            }

            resolvedPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindKnownGogInstallRoot(string baseDirectory, string gameId)
    {
        return FindCachedGogInstallRoot(gameId)
               ?? FindGogInstallRoot(baseDirectory, gameId)
               ?? FindGogRegistryInstallRoot(gameId);
    }

    private static string? FindCachedGogInstallRoot(string gameId)
    {
        try
        {
            var configuration = LoadStoreSyncConfiguration();
            var cachedGame = configuration.UnifySteam.Stores
                .GetValueOrDefault("gog-galaxy")
                ?.Cache
                ?.Games
                ?.FirstOrDefault(game =>
                    game is not null &&
                    string.Equals(game.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (cachedGame is null ||
                string.IsNullOrWhiteSpace(cachedGame.InstallPath) ||
                !Directory.Exists(cachedGame.InstallPath))
            {
                return null;
            }

            var installRoot = Path.GetFullPath(cachedGame.InstallPath);
            var manifestExists = File.Exists(Path.Combine(installRoot, $"goggame-{gameId}.info"));
            var executableExists =
                !string.IsNullOrWhiteSpace(cachedGame.ExecutablePath) &&
                File.Exists(cachedGame.ExecutablePath);
            return manifestExists || executableExists
                ? installRoot
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindGogRegistryInstallRoot(string gameId)
    {
        try
        {
            foreach (var root in OpenGogGameRegistryRoots())
            {
                using (root)
                {
                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        using var gameKey = root.OpenSubKey(subKeyName);
                        if (gameKey is null)
                        {
                            continue;
                        }

                        var installPath = NormalizeLoosePath(
                            GetRegistryString(gameKey, "path")
                            ?? GetRegistryString(gameKey, "PATH")
                            ?? GetRegistryString(gameKey, "InstallLocation")
                            ?? string.Empty);

                        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                        {
                            continue;
                        }

                        if (RegistryKeyMatchesGogGame(gameKey, subKeyName, gameId) ||
                            File.Exists(Path.Combine(installPath, $"goggame-{gameId}.info")))
                        {
                            return installPath;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveGogExecutablePath(string? installRoot, string gameId)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
        {
            return string.Empty;
        }

        var manifestTask = ResolveGogLaunchTask(installRoot, gameId);
        if (manifestTask is not null && File.Exists(manifestTask.ExecutablePath))
        {
            return manifestTask.ExecutablePath;
        }

        var registryHint = FindGogRegistryExecutableHint(gameId, installRoot);
        var registryExecutable = ResolveExecutableHint(installRoot, registryHint);
        if (!string.IsNullOrWhiteSpace(registryExecutable) && File.Exists(registryExecutable))
        {
            return registryExecutable;
        }

        return FindBestExecutable(installRoot);
    }

    private static string FindGogRegistryExecutableHint(string gameId, string installRoot)
    {
        try
        {
            foreach (var root in OpenGogGameRegistryRoots())
            {
                using (root)
                {
                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        using var gameKey = root.OpenSubKey(subKeyName);
                        if (gameKey is null)
                        {
                            continue;
                        }

                        var keyInstallPath = NormalizeLoosePath(
                            GetRegistryString(gameKey, "path")
                            ?? GetRegistryString(gameKey, "PATH")
                            ?? GetRegistryString(gameKey, "InstallLocation")
                            ?? string.Empty);

                        var installPathMatches = !string.IsNullOrWhiteSpace(keyInstallPath) &&
                                                 string.Equals(
                                                     Path.GetFullPath(keyInstallPath),
                                                     Path.GetFullPath(installRoot),
                                                     StringComparison.OrdinalIgnoreCase);
                        if (!installPathMatches && !RegistryKeyMatchesGogGame(gameKey, subKeyName, gameId))
                        {
                            continue;
                        }

                        return GetRegistryString(gameKey, "exe")
                               ?? GetRegistryString(gameKey, "gameExe")
                               ?? GetRegistryString(gameKey, "launchCommand")
                               ?? string.Empty;
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static IEnumerable<RegistryKey> OpenGogGameRegistryRoots()
    {
        foreach (var keyPath in new[]
                 {
                     @"SOFTWARE\WOW6432Node\GOG.com\Games",
                     @"SOFTWARE\GOG.com\Games",
                 })
        {
            var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static bool RegistryKeyMatchesGogGame(RegistryKey gameKey, string subKeyName, string gameId)
    {
        if (string.Equals(subKeyName, gameId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var valueName in new[] { "gameID", "gameId", "productID", "productId" })
        {
            if (string.Equals(GetRegistryString(gameKey, valueName), gameId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveExecutableHint(string installRoot, string executableHint)
    {
        if (string.IsNullOrWhiteSpace(executableHint))
        {
            return string.Empty;
        }

        var extractedPath = ExtractRegistryExecutablePath(executableHint);
        if (string.IsNullOrWhiteSpace(extractedPath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(extractedPath)
            ? NormalizeLoosePath(extractedPath)
            : NormalizeLoosePath(Path.Combine(installRoot, extractedPath));
    }

    private static string FindBestExecutable(string installRoot)
    {
        try
        {
            var rootName = NormalizeExecutableName(Path.GetFileName(installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            return Directory
                .EnumerateFiles(installRoot, "*.exe", SearchOption.AllDirectories)
                .Where(path => !ShouldIgnoreGogExecutable(path))
                .OrderByDescending(path => NormalizeExecutableName(Path.GetFileNameWithoutExtension(path)).Contains(rootName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path.Length)
                .FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ShouldIgnoreGogExecutable(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("vcredist", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("vc_redist", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("dxsetup", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("directx", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("galaxy", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExecutableName(string value)
    {
        return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
    }

    private static void WaitForProcessByPath(string executablePath)
    {
        // Give the launcher a moment to spawn the game, then wait for the game to exit.
        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            var process = FindProcessByPath(executablePath);

            if (process is not null)
            {
                using (process)
                {
                    process.WaitForExit();
                }

                return;
            }

            Thread.Sleep(2000);
        }
    }

    private static void WaitForProcessNames(string processNames)
    {
        var names = Regex.Split(processNames ?? string.Empty, @"[;,|]")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            foreach (var name in names)
            {
                var process = Process.GetProcessesByName(name!).FirstOrDefault();
                if (process is null)
                {
                    continue;
                }

                using (process)
                {
                    process.WaitForExit();
                }

                return;
            }

            Thread.Sleep(2000);
        }
    }

    private static void WaitForEpicGameSession(
        string installPath,
        string executablePath,
        string processNames)
    {
        var normalizedRoot = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                normalizedRoot = Path.GetFullPath(installPath)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
            }
        }
        catch
        {
        }

        var expectedNames = Regex.Split(processNames ?? string.Empty, @"[;,|]")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignoredNames = new HashSet<string>(
            new[]
            {
                "UplayLaunch",
                "UbisoftConnect",
                "upc",
                "UbisoftGameLauncher",
                "CrashReporter",
            },
            StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 60; attempt += 1)
        {
            Process? match = null;
            foreach (var process in Process.GetProcesses())
            {
                if (match is not null)
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    var processName = process.ProcessName;
                    var processPath = process.MainModule?.FileName ?? string.Empty;
                    var exactExecutable =
                        !string.IsNullOrWhiteSpace(executablePath) &&
                        string.Equals(
                            Path.GetFullPath(processPath),
                            Path.GetFullPath(executablePath),
                            StringComparison.OrdinalIgnoreCase) &&
                        !ignoredNames.Contains(processName);
                    var namedProcess =
                        expectedNames.Contains(processName) &&
                        !ignoredNames.Contains(processName);
                    var containedProcess =
                        !string.IsNullOrWhiteSpace(normalizedRoot) &&
                        !string.IsNullOrWhiteSpace(processPath) &&
                        Path.GetFullPath(processPath).StartsWith(
                            normalizedRoot,
                            StringComparison.OrdinalIgnoreCase) &&
                        !ignoredNames.Contains(processName);
                    if (exactExecutable || namedProcess || containedProcess)
                    {
                        match = process;
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }

            if (match is not null)
            {
                using (match)
                {
                    match.WaitForExit();
                }

                return;
            }

            Thread.Sleep(2000);
        }
    }

    private static bool IsProcessRunningByPath(string executablePath)
    {
        using var process = FindProcessByPath(executablePath);
        return process is not null;
    }

    private static Process? FindProcessByPath(string executablePath)
    {
        try
        {
            var normalized = Path.GetFullPath(executablePath);
            var processName = Path.GetFileNameWithoutExtension(normalized);
            Process? match = null;
            foreach (var candidate in Process.GetProcessesByName(processName))
            {
                if (match is not null)
                {
                    candidate.Dispose();
                    continue;
                }

                try
                {
                    if (string.Equals(
                            Path.GetFullPath(candidate.MainModule?.FileName ?? string.Empty),
                            normalized,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        match = candidate;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
                catch
                {
                    candidate.Dispose();
                }
            }

            return match;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeGogLaunchDiagnostic(string value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        const int maximumLength = 1000;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    internal static void WriteGogLaunchLog(string gameId, string message)
    {
        try
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "omnilibrary-gog-launch.log");
            const long maximumLogBytes = 512 * 1024;
            if (File.Exists(logPath) && new FileInfo(logPath).Length >= maximumLogBytes)
            {
                File.WriteAllText(logPath, string.Empty);
            }

            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} game={gameId} {NormalizeGogLaunchDiagnostic(message)}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static int RunVisibleAndWait(string toolPath, IReadOnlyList<string> arguments, bool waitForExit = true)
    {
        using var process = Process.Start(CreateStartInfo(toolPath, arguments, visible: true, redirectOutput: false));

        if (process is null)
        {
            return 1;
        }

        if (!waitForExit)
        {
            return 0;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static int RunHiddenAndWait(
        string toolPath,
        IReadOnlyList<string> arguments,
        bool waitForExit = true,
        string? epicLauncherBridgePath = null)
    {
        var startInfo = CreateStartInfo(
            toolPath,
            arguments,
            visible: false,
            redirectOutput: false);
        if (!string.IsNullOrWhiteSpace(epicLauncherBridgePath))
        {
            ConfigureEpicLauncherBridge(startInfo, epicLauncherBridgePath);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 1;
        }

        if (!waitForExit)
        {
            return 0;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    internal static void RemoveInheritedSteamLaunchContext(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Remove("SteamAppId");
        startInfo.Environment.Remove("SteamGameId");
        startInfo.Environment.Remove("SteamOverlayGameId");
    }

    private static ManagedDownloadRunResult RunHiddenDownloadAndTrack(
        string toolPath,
        IReadOnlyList<string> arguments,
        string storeId,
        string gameId,
        GogDownloadPlan plan,
        int attempt,
        int inactivityTimeoutSeconds)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                toolPath,
                arguments,
                visible: false,
                redirectOutput: true),
            EnableRaisingEvents = true,
        };
        var progressGate = new object();
        var lastProgress = plan.ProgressPercent;
        var downloadedBytes = plan.CompletedBytes;
        var downloadBytesPerSecond = 0d;
        var decompressedBytesPerSecond = 0d;
        var diskWriteBytesPerSecond = 0d;
        var diskReadBytesPerSecond = 0d;
        var lastDiagnostic = string.Empty;
        var lastWriteAt = DateTimeOffset.MinValue;
        var lastTelemetryLogAt = DateTimeOffset.MinValue;
        var lastJournalWriteAt = DateTimeOffset.MinValue;
        var lastOutputTicks = DateTime.UtcNow.Ticks;

        void HandleLine(string? line)
        {
            var detail = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                return;
            }
            Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);

            var telemetryLine =
                detail.Contains("Progress:", StringComparison.OrdinalIgnoreCase) ||
                detail.StartsWith("Download -", StringComparison.OrdinalIgnoreCase) ||
                detail.StartsWith("Disk -", StringComparison.OrdinalIgnoreCase);
            var receivedAt = DateTimeOffset.UtcNow;
            if (!telemetryLine ||
                receivedAt - lastTelemetryLogAt >= TimeSpan.FromSeconds(2))
            {
                WriteGogDownloadLog(gameId, attempt, detail);
                if (telemetryLine)
                {
                    lastTelemetryLogAt = receivedAt;
                }
            }
            var match = Regex.Match(
                detail,
                @"(?<!\d)(?<value>\d{1,3}(?:\.\d+)?)\s*%|Progress:\s*(?<gogValue>\d{1,3}(?:\.\d+)?)",
                RegexOptions.IgnoreCase);
            var byteProgressMatch = Regex.Match(
                detail,
                @"Progress:\s*\d{1,3}(?:\.\d+)?\s+(?<current>\d+)\s*/\s*(?<total>\d+)",
                RegexOptions.IgnoreCase);
            var rawSpeedMatch = Regex.Match(
                detail,
                @"Download\s*-\s*(?<speed>\d+(?:\.\d+)?)\s*MiB/s\s*\(raw\)",
                RegexOptions.IgnoreCase);
            var decompressedSpeedMatch = Regex.Match(
                detail,
                @"/\s*(?<speed>\d+(?:\.\d+)?)\s*MiB/s\s*decompressed",
                RegexOptions.IgnoreCase);
            var diskWriteSpeedMatch = Regex.Match(
                detail,
                @"Disk\s*-\s*(?<speed>\d+(?:\.\d+)?)\s*MiB/s\s*write",
                RegexOptions.IgnoreCase);
            var diskReadSpeedMatch = Regex.Match(
                detail,
                @"/\s*(?<speed>\d+(?:\.\d+)?)\s*MiB/s\s*read",
                RegexOptions.IgnoreCase);
            var value = match.Groups["value"].Success
                ? match.Groups["value"].Value
                : match.Groups["gogValue"].Value;

            lock (progressGate)
            {
                var progress = lastProgress;
                if (match.Success &&
                    double.TryParse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsedProgress))
                {
                    var relativeProgress = Math.Clamp(parsedProgress, 0, 100);
                    if (byteProgressMatch.Success &&
                        long.TryParse(
                            byteProgressMatch.Groups["current"].Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var helperCompletedBytes))
                    {
                        downloadedBytes = Math.Max(
                            downloadedBytes,
                            Math.Max(0, helperCompletedBytes));
                    }
                    else if (plan.DiskSizeBytes > 0)
                    {
                        downloadedBytes = Math.Max(
                            downloadedBytes,
                            (long)Math.Floor(
                                plan.DiskSizeBytes *
                                relativeProgress /
                                100d));
                    }
                    progress = plan.DiskSizeBytes > 0
                        ? Math.Clamp(
                            (int)Math.Floor(
                                downloadedBytes * 100d / plan.DiskSizeBytes),
                            0,
                            99)
                        : Math.Clamp((int)Math.Floor(relativeProgress), 0, 99);
                    progress = Math.Max(lastProgress, progress);
                }
                if (rawSpeedMatch.Success)
                {
                    downloadBytesPerSecond = ParseMebibytesAsBytesDouble(
                        rawSpeedMatch.Groups["speed"].Value);
                }
                if (decompressedSpeedMatch.Success)
                {
                    decompressedBytesPerSecond = ParseMebibytesAsBytesDouble(
                        decompressedSpeedMatch.Groups["speed"].Value);
                }
                if (diskWriteSpeedMatch.Success)
                {
                    diskWriteBytesPerSecond = ParseMebibytesAsBytesDouble(
                        diskWriteSpeedMatch.Groups["speed"].Value);
                }
                if (diskReadSpeedMatch.Success)
                {
                    diskReadBytesPerSecond = ParseMebibytesAsBytesDouble(
                        diskReadSpeedMatch.Groups["speed"].Value);
                }
                if (!match.Success &&
                    !rawSpeedMatch.Success &&
                    !decompressedSpeedMatch.Success &&
                    !diskWriteSpeedMatch.Success &&
                    !diskReadSpeedMatch.Success)
                {
                    lastDiagnostic = NormalizeEpicDownloadDiagnostic(detail);
                }

                var now = DateTimeOffset.UtcNow;
                if (progress == lastProgress &&
                    now - lastWriteAt < TimeSpan.FromMilliseconds(900))
                {
                    return;
                }

                lastProgress = progress;
                lastWriteAt = now;
                if (now - lastJournalWriteAt >= TimeSpan.FromSeconds(5))
                {
                    GogOperationJournal.Advance(
                        gameId,
                        GogOperationPhases.Downloading,
                        downloadedBytes: downloadedBytes,
                        totalBytes: plan.DiskSizeBytes,
                        attempt: attempt,
                        detailText: downloadBytesPerSecond > 0
                            ? $"Downloading GOG game ({progress}%) · {FormatByteRate(downloadBytesPerSecond)}"
                            : $"Downloading GOG game ({progress}%).");
                    lastJournalWriteAt = now;
                }
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "downloading",
                    progress,
                    downloadBytesPerSecond > 0
                        ? $"Downloading GOG game ({progress}%) · " +
                          $"{FormatByteRate(downloadBytesPerSecond)}"
                        : progress > 0
                            ? $"Downloading GOG game ({progress}%)."
                            : "GOG download is starting.",
                    downloadedBytes: downloadedBytes,
                    totalBytes: plan.DiskSizeBytes,
                    downloadBytesPerSecond: downloadBytesPerSecond,
                    decompressedBytesPerSecond: decompressedBytesPerSecond,
                    diskWriteBytesPerSecond: diskWriteBytesPerSecond,
                    diskReadBytesPerSecond: diskReadBytesPerSecond,
                    attempt: attempt);
            }
        }

        process.OutputDataReceived += (_, args) => HandleLine(args.Data);
        process.ErrorDataReceived += (_, args) => HandleLine(args.Data);
        if (!process.Start())
        {
            return new ManagedDownloadRunResult(
                1,
                lastProgress,
                downloadedBytes,
                plan.DiskSizeBytes,
                0,
                0,
                0,
                0,
                "The GOG helper could not be started.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        while (!process.WaitForExit(1000))
        {
            var lastOutputAt = new DateTime(
                Interlocked.Read(ref lastOutputTicks),
                DateTimeKind.Utc);
            if (DateTime.UtcNow - lastOutputAt <=
                TimeSpan.FromSeconds(inactivityTimeoutSeconds))
            {
                continue;
            }

            lock (progressGate)
            {
                lastDiagnostic =
                    $"GOG CDN produced no progress for {inactivityTimeoutSeconds} seconds.";
            }
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            process.WaitForExit();
            break;
        }
        process.WaitForExit();
        lock (progressGate)
        {
            return new ManagedDownloadRunResult(
                process.ExitCode,
                lastProgress,
                downloadedBytes,
                plan.DiskSizeBytes,
                downloadBytesPerSecond,
                decompressedBytesPerSecond,
                diskWriteBytesPerSecond,
                diskReadBytesPerSecond,
                lastDiagnostic);
        }
    }

    private static GogDownloadPlan BuildGogDownloadPlan(
        string gogdl,
        string authPath,
        string gameId,
        string installDirectory,
        long persistedCompletedBytes,
        bool includeDlc)
    {
        long diskSizeBytes = 0;
        try
        {
            var output = RunHiddenAndCapture(
                gogdl,
                "--auth-config-path",
                authPath,
                "info",
                gameId,
                "--platform",
                "windows",
                includeDlc ? "--with-dlcs" : "--skip-dlcs");
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(output) ? "{}" : output);
            if (document.RootElement.TryGetProperty("size", out var sizes) &&
                sizes.ValueKind == JsonValueKind.Object)
            {
                long commonBytes = 0;
                long largestLanguageBytes = 0;
                foreach (var size in sizes.EnumerateObject())
                {
                    var bytes = size.Value.ValueKind == JsonValueKind.Object
                        ? ReadJsonInt64(size.Value, "disk_size")
                        : 0;
                    if (size.NameEquals("*"))
                    {
                        commonBytes = bytes;
                    }
                    else
                    {
                        largestLanguageBytes = Math.Max(
                            largestLanguageBytes,
                            bytes);
                    }
                }

                diskSizeBytes = checked(commonBytes + largestLanguageBytes);
            }
        }
        catch (Exception exception)
        {
            WriteGogDownloadLog(
                gameId,
                0,
                $"manifest size unavailable: {exception.Message}");
        }

        // gogdl's own manifest/checkpoint remains authoritative. Walking a
        // 100+ GB directory before every resume is both expensive and wrong
        // for sparse/preallocated files, so retain only persisted helper
        // progress here. gogdl verifies and reuses every completed chunk.
        var completedBytes = Math.Max(0, persistedCompletedBytes);
        if (diskSizeBytes > 0)
        {
            completedBytes = Math.Min(completedBytes, diskSizeBytes);
        }
        return new GogDownloadPlan(
            Path.GetFullPath(installDirectory),
            Math.Max(0, diskSizeBytes),
            Math.Max(0, completedBytes));
    }

    private static EpicDownloadPlan BuildEpicDownloadPlan(
        string legendary,
        string appName,
        string configuredBasePath,
        long persistedCompletedBytes)
    {
        var output = RunHiddenAndCapture(legendary, "info", appName, "--json");
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(output) ? "{}" : output);
        var root = document.RootElement;
        var manifest = root.TryGetProperty("manifest", out var manifestNode) &&
                       manifestNode.ValueKind == JsonValueKind.Object
            ? manifestNode
            : root;
        var downloadSizeBytes = ReadJsonInt64(manifest, "download_size");
        var diskSizeBytes = ReadJsonInt64(manifest, "disk_size");
        var basePath = string.IsNullOrWhiteSpace(configuredBasePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Games")
            : Path.GetFullPath(configuredBasePath);
        var folderName = TryReadEpicFolderName(appName);
        var installDirectory = ResolveContainedInstallDirectory(
            basePath,
            folderName,
            appName);
        var resumeCompletedBytes = GetEpicResumeCompletedBytes(
            appName,
            installDirectory);
        var completedBytes = Math.Max(
            Math.Max(0, persistedCompletedBytes),
            resumeCompletedBytes);
        if (completedBytes == 0 && Directory.Exists(installDirectory))
        {
            completedBytes = GetDirectoryFileBytes(installDirectory);
        }
        if (diskSizeBytes > 0)
        {
            completedBytes = Math.Min(completedBytes, diskSizeBytes);
        }

        return new EpicDownloadPlan(
            installDirectory,
            Math.Max(0, downloadSizeBytes),
            Math.Max(0, diskSizeBytes),
            Math.Max(0, completedBytes));
    }

    private static long ReadJsonInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return Math.Max(0, number);
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(
                   value.GetString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out number)
            ? Math.Max(0, number)
            : 0;
    }

    private static string TryReadEpicFolderName(string appName)
    {
        try
        {
            var metadataPath = Path.Combine(
                ManagedLegendaryHelper.ConfigDirectory,
                "metadata",
                $"{appName}.json");
            if (!File.Exists(metadataPath))
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            if (root.TryGetProperty("metadata", out var metadata))
            {
                root = metadata;
            }
            if (root.TryGetProperty("customAttributes", out var attributes) &&
                attributes.TryGetProperty("FolderName", out var folder) &&
                folder.TryGetProperty("value", out var value))
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ResolveContainedInstallDirectory(
        string basePath,
        string folderName,
        string appName)
    {
        var normalizedBasePath = Path.GetFullPath(basePath);
        var candidateFolder = string.IsNullOrWhiteSpace(folderName)
            ? appName
            : folderName;
        if (Path.IsPathRooted(candidateFolder))
        {
            candidateFolder = appName;
        }

        var installDirectory = Path.GetFullPath(
            Path.Combine(normalizedBasePath, candidateFolder));
        var basePrefix = normalizedBasePath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return installDirectory.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
            ? installDirectory
            : Path.Combine(normalizedBasePath, appName);
    }

    private static long GetEpicResumeCompletedBytes(
        string appName,
        string installDirectory)
    {
        try
        {
            var resumePath = Path.Combine(
                ManagedLegendaryHelper.ConfigDirectory,
                "tmp",
                $"{appName}.resume");
            if (!File.Exists(resumePath) || !Directory.Exists(installDirectory))
            {
                return 0;
            }

            var root = Path.GetFullPath(installDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long completedBytes = 0;
            foreach (var line in File.ReadLines(resumePath))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0 || separator >= line.Length - 1)
                {
                    continue;
                }

                var relativePath = line[(separator + 1)..]
                    .Replace('/', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);
                var filePath = Path.GetFullPath(
                    Path.Combine(installDirectory, relativePath));
                if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(filePath))
                {
                    continue;
                }

                completedBytes = checked(completedBytes + new FileInfo(filePath).Length);
            }

            return completedBytes;
        }
        catch
        {
            return 0;
        }
    }

    private static long GetDirectoryFileBytes(string directory)
    {
        try
        {
            long total = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var filePath in Directory.EnumerateFiles(directory, "*", options))
            {
                try
                {
                    total = checked(total + new FileInfo(filePath).Length);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or OverflowException)
                {
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static bool HasGogResumeState(string gameId, string installDirectory)
    {
        try
        {
            if (File.Exists(ManagedGogDlHelper.GetInstalledManifestPath(gameId)))
            {
                return true;
            }

            if (!Directory.Exists(installDirectory))
            {
                return false;
            }

            // One top-level entry is sufficient evidence that gogdl may have
            // checkpointed useful chunks. Never recursively enumerate a large
            // game merely to decide whether its own resume manifest should be
            // preserved.
            return Directory.EnumerateFileSystemEntries(
                    installDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Take(1)
                .Any();
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureEpicDownloadHasSpace(EpicDownloadPlan plan)
    {
        EnsureDownloadHasSpace(
            "Epic",
            plan.InstallDirectory,
            plan.DiskSizeBytes,
            plan.CompletedBytes);
    }

    private static void EnsureDownloadHasSpace(
        string storeName,
        string installDirectory,
        long diskSizeBytes,
        long completedBytes)
    {
        if (diskSizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(installDirectory))
        {
            return;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(installDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
        {
            return;
        }

        var remainingBytes = Math.Max(0, diskSizeBytes - completedBytes);
        var requiredBytes = checked(remainingBytes + EpicDiskSafetyReserveBytes);
        if (drive.AvailableFreeSpace >= requiredBytes)
        {
            return;
        }

        var missingBytes = requiredBytes - drive.AvailableFreeSpace;
        throw new InvalidOperationException(
            $"Not enough free space for this {storeName} download. Free at least " +
            $"{FormatByteSize(missingBytes)} more on {drive.Name} so the game can finish " +
            $"with a {FormatByteSize(EpicDiskSafetyReserveBytes)} safety reserve.");
    }

    private static EpicDownloadRunResult RunHiddenDownloadAndTrack(
        string toolPath,
        IReadOnlyList<string> arguments,
        string storeId,
        string gameId,
        EpicDownloadPlan plan,
        int attempt)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(toolPath, arguments, visible: false, redirectOutput: true),
            EnableRaisingEvents = true,
        };

        var progressGate = new object();
        var lastProgress = plan.ProgressPercent;
        var sessionWrittenBytes = 0L;
        var currentDownloadedBytes = plan.CompletedBytes;
        var downloadBytesPerSecond = 0d;
        var decompressedBytesPerSecond = 0d;
        var diskWriteBytesPerSecond = 0d;
        var diskReadBytesPerSecond = 0d;
        var lastDiagnostic = string.Empty;
        var lastWriteAt = DateTimeOffset.MinValue;

        void HandleLine(string? line)
        {
            var detail = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                return;
            }

            WriteEpicDownloadLog(gameId, attempt, detail);
            var progressMatch = Regex.Match(
                detail,
                @"(?:^|\s)Progress:\s*(?<value>\d{1,3}(?:\.\d+)?)\s*%?",
                RegexOptions.IgnoreCase);
            var writtenMatch = Regex.Match(
                detail,
                @"Downloaded:\s*(?<downloaded>\d+(?:\.\d+)?)\s*MiB,\s*Written:\s*(?<written>\d+(?:\.\d+)?)\s*MiB",
                RegexOptions.IgnoreCase);
            var speedMatch = Regex.Match(
                detail,
                @"Download\s*-\s*(?<raw>\d+(?:\.\d+)?)\s*MiB/s\s*\(raw\)\s*/\s*(?<decompressed>\d+(?:\.\d+)?)\s*MiB/s",
                RegexOptions.IgnoreCase);
            var diskMatch = Regex.Match(
                detail,
                @"Disk\s*-\s*(?<write>\d+(?:\.\d+)?)\s*MiB/s\s*\(write\)\s*/\s*(?<read>\d+(?:\.\d+)?)\s*MiB/s",
                RegexOptions.IgnoreCase);

            lock (progressGate)
            {
                if (writtenMatch.Success)
                {
                    sessionWrittenBytes = ParseMebibytesAsBytes(
                        writtenMatch.Groups["written"].Value);
                    currentDownloadedBytes = plan.DiskSizeBytes > 0
                        ? Math.Min(
                            plan.DiskSizeBytes,
                            plan.CompletedBytes + sessionWrittenBytes)
                        : plan.CompletedBytes + sessionWrittenBytes;
                }
                if (speedMatch.Success)
                {
                    downloadBytesPerSecond = ParseMebibytesAsBytesDouble(
                        speedMatch.Groups["raw"].Value);
                    decompressedBytesPerSecond = ParseMebibytesAsBytesDouble(
                        speedMatch.Groups["decompressed"].Value);
                }
                if (diskMatch.Success)
                {
                    diskWriteBytesPerSecond = ParseMebibytesAsBytesDouble(
                        diskMatch.Groups["write"].Value);
                    diskReadBytesPerSecond = ParseMebibytesAsBytesDouble(
                        diskMatch.Groups["read"].Value);
                }

                var relativeProgress = progressMatch.Success &&
                                       double.TryParse(
                                           progressMatch.Groups["value"].Value,
                                           NumberStyles.Float,
                                           CultureInfo.InvariantCulture,
                                           out var parsedProgress)
                    ? Math.Clamp((int)Math.Floor(parsedProgress), 0, 99)
                    : lastProgress;
                var progress = plan.DiskSizeBytes > 0
                    ? Math.Clamp(
                        (int)Math.Floor(
                            currentDownloadedBytes * 100d / plan.DiskSizeBytes),
                        0,
                        99)
                    : relativeProgress;
                progress = Math.Max(lastProgress, progress);

                if (!progressMatch.Success &&
                    !writtenMatch.Success &&
                    !speedMatch.Success &&
                    !diskMatch.Success)
                {
                    lastDiagnostic = NormalizeEpicDownloadDiagnostic(detail);
                }

                var now = DateTimeOffset.UtcNow;
                if (progress == lastProgress &&
                    now - lastWriteAt < TimeSpan.FromMilliseconds(900))
                {
                    return;
                }

                lastProgress = Math.Max(0, progress);
                lastWriteAt = now;
                UnifySteamDownloadStatusStore.Update(
                    storeId,
                    gameId,
                    "downloading",
                    lastProgress,
                    BuildEpicDownloadDetail(
                        lastProgress,
                        downloadBytesPerSecond,
                        diskWriteBytesPerSecond,
                        attempt),
                    downloadedBytes: currentDownloadedBytes,
                    totalBytes: plan.DiskSizeBytes,
                    downloadBytesPerSecond: downloadBytesPerSecond,
                    decompressedBytesPerSecond: decompressedBytesPerSecond,
                    diskWriteBytesPerSecond: diskWriteBytesPerSecond,
                    diskReadBytesPerSecond: diskReadBytesPerSecond,
                    attempt: attempt);
            }
        }

        process.OutputDataReceived += (_, args) => HandleLine(args.Data);
        process.ErrorDataReceived += (_, args) => HandleLine(args.Data);
        if (!process.Start())
        {
            return new EpicDownloadRunResult(
                1,
                lastProgress,
                currentDownloadedBytes,
                plan.DiskSizeBytes,
                0,
                0,
                0,
                0,
                "The Epic helper could not be started.");
        }
        WriteEpicDownloadLog(
            gameId,
            attempt,
            $"worker started pid={process.Id} workers={arguments.SkipWhile(value => value != "--max-workers").Skip(1).FirstOrDefault() ?? "?"}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        process.WaitForExit();
        lock (progressGate)
        {
            WriteEpicDownloadLog(
                gameId,
                attempt,
                $"worker exited code={process.ExitCode} progress={lastProgress}%");
            return new EpicDownloadRunResult(
                process.ExitCode,
                lastProgress,
                currentDownloadedBytes,
                plan.DiskSizeBytes,
                downloadBytesPerSecond,
                decompressedBytesPerSecond,
                diskWriteBytesPerSecond,
                diskReadBytesPerSecond,
                lastDiagnostic);
        }
    }

    private static long ParseMebibytesAsBytes(string value)
    {
        var bytes = ParseMebibytesAsBytesDouble(value);
        return bytes >= long.MaxValue ? long.MaxValue : (long)Math.Round(bytes);
    }

    private static double ParseMebibytesAsBytesDouble(string value)
    {
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var mebibytes)
            ? Math.Max(0, mebibytes) * 1024d * 1024d
            : 0;
    }

    private static string BuildEpicDownloadDetail(
        int progress,
        double downloadSpeed,
        double diskWriteSpeed,
        int attempt)
    {
        var parts = new List<string>
        {
            progress > 0 ? $"Downloading {progress}%" : "Downloading",
        };
        if (downloadSpeed > 0)
        {
            parts.Add($"{FormatByteRate(downloadSpeed)} network");
        }
        if (diskWriteSpeed > 0)
        {
            parts.Add($"{FormatByteRate(diskWriteSpeed)} disk");
        }
        if (attempt > 1)
        {
            parts.Add($"attempt {attempt}/{EpicMaximumDownloadAttempts}");
        }
        return string.Join(" · ", parts);
    }

    private static string FormatByteRate(double bytesPerSecond)
    {
        return bytesPerSecond >= 1024d * 1024d * 1024d
            ? $"{bytesPerSecond / (1024d * 1024d * 1024d):0.0} GiB/s"
            : bytesPerSecond >= 1024d * 1024d
                ? $"{bytesPerSecond / (1024d * 1024d):0.0} MiB/s"
                : $"{bytesPerSecond / 1024d:0.0} KiB/s";
    }

    private static string FormatByteSize(long bytes)
    {
        return bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024d * 1024d):0.0} GiB"
            : bytes >= 1024L * 1024
                ? $"{bytes / (1024d * 1024d):0.0} MiB"
                : $"{bytes / 1024d:0.0} KiB";
    }

    private static string NormalizeEpicDownloadDiagnostic(string? value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        const int maximumLength = 220;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static bool ShouldRetryEpicDownload(string diagnostic)
    {
        return ShouldRetryManagedDownload(diagnostic);
    }

    private static bool ShouldRetryManagedDownload(string diagnostic)
    {
        var normalized = diagnostic.ToLowerInvariant();
        return !new[]
        {
            "not enough",
            "no space",
            "not logged in",
            "authentication",
            "invalid credential",
            "not owned",
            "does not own",
            "invalid app",
            "manifest not found",
        }.Any(normalized.Contains);
    }

    private static void WriteGogDownloadLog(
        string gameId,
        int attempt,
        string message)
    {
        try
        {
            lock (GogDownloadLogGate)
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "omnilibrary-gog-download.log");
                const long maximumLogBytes = 4L * 1024 * 1024;
                if (File.Exists(path) && new FileInfo(path).Length >= maximumLogBytes)
                {
                    File.Move(path, $"{path}.old", overwrite: true);
                }

                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} game={gameId} attempt={attempt} " +
                    $"{NormalizeEpicDownloadDiagnostic(message)}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private static void WriteEpicDownloadLog(
        string gameId,
        int attempt,
        string message)
    {
        try
        {
            lock (EpicDownloadLogGate)
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "omnilibrary-epic-download.log");
                const long maximumLogBytes = 4L * 1024 * 1024;
                if (File.Exists(path) && new FileInfo(path).Length >= maximumLogBytes)
                {
                    File.Move(path, $"{path}.old", overwrite: true);
                }

                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} game={gameId} attempt={attempt} " +
                    $"{NormalizeEpicDownloadDiagnostic(message)}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private static bool TryOpenShellTarget(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    internal static string RunHiddenAndCapture(string toolPath, params string[] arguments)
    {
        using var process = Process.Start(CreateStartInfo(toolPath, arguments, visible: false, redirectOutput: true));

        if (process is null)
        {
            return string.Empty;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            return outputTask.Result;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
            }

            throw new TimeoutException(
                $"{Path.GetFileName(toolPath)} did not finish within 60 seconds.");
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string toolPath,
        IReadOnlyList<string> arguments,
        bool visible,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = !visible,
        };

        if (IsBatchLike(toolPath))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"{toolPath}\" {JoinCommandLine(arguments)}");
            return startInfo;
        }

        startInfo.FileName = toolPath;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (Path.GetFileName(toolPath).Equals("legendary.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(toolPath).Equals("legendary", StringComparison.OrdinalIgnoreCase))
        {
            ManagedLegendaryHelper.ConfigureEnvironment(startInfo);
        }
        else if (Path.GetFileName(toolPath).Equals("gogdl.exe", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileNameWithoutExtension(toolPath).Equals("gogdl", StringComparison.OrdinalIgnoreCase))
        {
            ManagedGogDlHelper.ConfigureEnvironment(startInfo);
        }

        return startInfo;
    }

    private static string JoinCommandLine(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
    }

    private static bool IsBatchLike(string toolPath)
    {
        var extension = Path.GetExtension(toolPath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeLauncherId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(character =>
                   char.IsLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
    }

    private static string FindTool(string executableName, string commandName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var name in new[] { executableName, commandName, commandName + ".exe", commandName + ".cmd", commandName + ".bat" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var heroicBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "heroic",
            "resources",
            "app.asar.unpacked",
            "build",
            "bin",
            "win32");
        foreach (var name in new[] { executableName, commandName })
        {
            var candidate = Path.Combine(heroicBase, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string ResolveLegendaryTool(bool installWhenMissing)
    {
        var configuration = LoadStoreSyncConfiguration();
        configuration.UnifySteam.Stores.TryGetValue("epic-games", out var store);
        var toolPath = ManagedLegendaryHelper.ResolveExistingToolPath(store?.ToolPath);
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            var externalTool = FindTool("legendary.exe", "legendary");
            toolPath = !string.IsNullOrWhiteSpace(externalTool)
                ? externalTool
                : installWhenMissing
                    ? ManagedLegendaryHelper.EnsureInstalled()
                    : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(toolPath))
        {
            throw new InvalidOperationException("The Epic download helper is unavailable.");
        }

        var resolvedToolPath = toolPath;
        GetStoreSyncSettingsStore().Update(latest =>
        {
            if (!latest.UnifySteam.Stores.TryGetValue("epic-games", out var latestStore) ||
                latestStore is null)
            {
                latestStore = new UnifySteamStoreConfiguration();
                latest.UnifySteam.Stores["epic-games"] = latestStore;
            }

            latestStore.ToolPath = resolvedToolPath;
            latestStore.AuthPath = ManagedLegendaryHelper.UserDataPath;
        });
        return toolPath;
    }

    private static string ResolveGogTool(bool installWhenMissing)
    {
        var configuration = LoadStoreSyncConfiguration();
        configuration.UnifySteam.Stores.TryGetValue("gog-galaxy", out var store);
        var toolPath = ManagedGogDlHelper.ResolveExistingToolPath(store?.ToolPath);
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            var externalTool = FindTool("gogdl.exe", "gogdl");
            toolPath = !string.IsNullOrWhiteSpace(externalTool)
                ? externalTool
                : installWhenMissing
                    ? ManagedGogDlHelper.EnsureInstalled()
                    : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(toolPath))
        {
            return string.Empty;
        }

        var resolvedToolPath = toolPath;
        GetStoreSyncSettingsStore().Update(latest =>
        {
            if (!latest.UnifySteam.Stores.TryGetValue("gog-galaxy", out var latestStore) ||
                latestStore is null)
            {
                latestStore = new UnifySteamStoreConfiguration();
                latest.UnifySteam.Stores["gog-galaxy"] = latestStore;
            }

            latestStore.ToolPath = resolvedToolPath;
            if (File.Exists(ManagedGogDlHelper.AuthPath))
            {
                latestStore.AuthPath = ManagedGogDlHelper.AuthPath;
            }
        });
        return toolPath;
    }

    private static string GetGogPreferredInstallPath()
    {
        var configuration = LoadStoreSyncConfiguration();
        configuration.UnifySteam.Stores.TryGetValue("gog-galaxy", out var store);
        if (!string.IsNullOrWhiteSpace(store?.InstallPath))
        {
            return Path.GetFullPath(store.InstallPath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolsForSteam",
            "OmniLibraryGames",
            "GOG");
    }

    private static bool IsManagedGogInstallRoot(
        string baseDirectory,
        string installRoot,
        string gameId)
    {
        try
        {
            var normalizedBase = Path.GetFullPath(baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var normalizedRoot = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedRoot.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase) &&
                   normalizedRoot.Length > normalizedBase.Length &&
                   File.Exists(Path.Combine(normalizedRoot, $"goggame-{gameId}.info")) &&
                   File.Exists(Path.Combine(normalizedRoot, GogManagedInstallMarkerFileName)) &&
                   string.Equals(
                       File.ReadAllText(
                           Path.Combine(normalizedRoot, GogManagedInstallMarkerFileName)).Trim(),
                       gameId,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsContainedGogOperationRoot(
        string baseDirectory,
        string? installRoot,
        string gameId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot) ||
                !IsSafeLauncherId(gameId))
            {
                return false;
            }

            var normalizedBase = Path.GetFullPath(baseDirectory)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var expectedRoot = Path.GetFullPath(Path.Combine(normalizedBase, gameId))
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(installRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            return normalizedRoot.Equals(
                       expectedRoot,
                       StringComparison.OrdinalIgnoreCase) ||
                   normalizedRoot.StartsWith(
                       expectedRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsManagedGogInstall(string? installRoot, string gameId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot) ||
                string.IsNullOrWhiteSpace(gameId))
            {
                return false;
            }

            var markerPath = Path.Combine(installRoot, GogManagedInstallMarkerFileName);
            return File.Exists(markerPath) &&
                   string.Equals(
                       File.ReadAllText(markerPath).Trim(),
                       gameId,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteGogManagedInstallMarker(string installRoot, string gameId)
    {
        if (string.IsNullOrWhiteSpace(installRoot) ||
            string.IsNullOrWhiteSpace(gameId) ||
            !Directory.Exists(installRoot))
        {
            throw new InvalidOperationException(
                "The completed GOG installation could not be marked as managed.");
        }

        File.WriteAllText(
            Path.Combine(installRoot, GogManagedInstallMarkerFileName),
            gameId.Trim());
    }

    private static StoreSyncConfiguration LoadStoreSyncConfiguration()
    {
        return GetStoreSyncSettingsStore().Load();
    }

    private static StoreSyncSettingsStore GetStoreSyncSettingsStore()
    {
        return new StoreSyncSettingsStore(
            Path.Combine(AppContext.BaseDirectory, "data", "store-sync.json"));
    }

    private static void UpdateEpicInstalledCache(
        string legendary,
        string appName,
        string preparationSignature)
    {
        try
        {
            var output = RunHiddenAndCapture(legendary, "list-installed", "--json");
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
            var installed = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault(item =>
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("app_name", out var appNameNode) &&
                    string.Equals(appNameNode.GetString(), appName, StringComparison.OrdinalIgnoreCase))
                : default;
            if (installed.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var installPath = installed.TryGetProperty("install_path", out var installPathNode)
                ? installPathNode.GetString() ?? string.Empty
                : string.Empty;
            var executable = installed.TryGetProperty("executable", out var executableNode)
                ? executableNode.GetString() ?? string.Empty
                : string.Empty;
            var executablePath = string.IsNullOrWhiteSpace(executable)
                ? string.Empty
                : Path.IsPathRooted(executable)
                    ? executable
                    : Path.Combine(installPath, executable);
            var version = installed.TryGetProperty("version", out var versionNode)
                ? versionNode.GetString() ?? string.Empty
                : string.Empty;
            GetStoreSyncSettingsStore().Update(configuration =>
            {
                if (!configuration.UnifySteam.Stores.TryGetValue("epic-games", out var store) ||
                    store?.Cache?.Games is null)
                {
                    return;
                }

                var game = store.Cache.Games.FirstOrDefault(candidate =>
                    candidate is not null &&
                    string.Equals(candidate.Id, appName, StringComparison.OrdinalIgnoreCase));
                if (game is null)
                {
                    return;
                }

                game.Installed = true;
                game.InstallPath = installPath;
                game.ExecutablePath = executablePath;
                game.Version = version;
                game.PreparationSignature = preparationSignature;
            });
        }
        catch
        {
            // The completed status still updates Steam immediately; the next quiet
            // store refresh repairs any missing install metadata.
        }
    }

    private static void UpdateEpicUninstalledCache(string appName)
    {
        try
        {
            GetStoreSyncSettingsStore().Update(configuration =>
            {
                if (!configuration.UnifySteam.Stores.TryGetValue("epic-games", out var store) ||
                    store?.Cache?.Games is null)
                {
                    return;
                }

                var game = store.Cache.Games.FirstOrDefault(candidate =>
                    candidate is not null &&
                    string.Equals(candidate.Id, appName, StringComparison.OrdinalIgnoreCase));
                if (game is null)
                {
                    return;
                }

                game.Installed = false;
                game.InstallPath = string.Empty;
                game.ExecutablePath = string.Empty;
                game.Version = string.Empty;
            });
        }
        catch
        {
            // The next lightweight Epic reconciliation repairs the persisted state.
        }
    }

    private static void UpdateGogInstalledCache(
        string gameId,
        string? installRoot,
        string? executablePath)
    {
        try
        {
            var normalizedInstallRoot = string.IsNullOrWhiteSpace(installRoot)
                ? string.Empty
                : Path.GetFullPath(installRoot);
            var normalizedExecutablePath =
                !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
                    ? Path.GetFullPath(executablePath)
                    : ResolveGogExecutablePath(normalizedInstallRoot, gameId);
            var installedBuildId = GogInstallPreparation.ResolvePlan(
                    ManagedGogDlHelper.RuntimeConfigPath,
                    normalizedInstallRoot,
                    gameId)
                ?.BuildId ?? string.Empty;
            GetStoreSyncSettingsStore().Update(configuration =>
            {
                if (!configuration.UnifySteam.Stores.TryGetValue("gog-galaxy", out var store) ||
                    store?.Cache?.Games is null)
                {
                    return;
                }

                var game = store.Cache.Games.FirstOrDefault(candidate =>
                    candidate is not null &&
                    string.Equals(candidate.Id, gameId, StringComparison.OrdinalIgnoreCase));
                if (game is null)
                {
                    return;
                }

                game.Installed = true;
                game.InstallPath = normalizedInstallRoot;
                game.ExecutablePath = normalizedExecutablePath;
                if (!string.IsNullOrWhiteSpace(installedBuildId))
                {
                    game.Version = installedBuildId;
                    if (string.IsNullOrWhiteSpace(game.LatestVersion) ||
                        string.Equals(
                            game.LatestVersion,
                            installedBuildId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        game.LatestVersion = installedBuildId;
                    }
                }
            });
        }
        catch
        {
            // The completed status updates Steam immediately; the next GOG
            // reconciliation repairs any missing install metadata.
        }
    }

    private static void UpdateGogUninstalledCache(string gameId)
    {
        try
        {
            GetStoreSyncSettingsStore().Update(configuration =>
            {
                if (!configuration.UnifySteam.Stores.TryGetValue("gog-galaxy", out var store) ||
                    store?.Cache?.Games is null)
                {
                    return;
                }

                var game = store.Cache.Games.FirstOrDefault(candidate =>
                    candidate is not null &&
                    string.Equals(candidate.Id, gameId, StringComparison.OrdinalIgnoreCase));
                if (game is null)
                {
                    return;
                }

                game.Installed = false;
                game.InstallPath = string.Empty;
                game.ExecutablePath = string.Empty;
                game.Version = string.Empty;
            });
        }
        catch
        {
            // The next GOG refresh repairs the persisted state.
        }
    }

    private static void ClearGogInstalledStateBestEffort(string gameId)
    {
        try
        {
            ManagedGogDlHelper.ClearInstalledState(gameId);
            WriteGogLaunchLog(
                gameId,
                "cleared per-game gogdl install state after uninstall");
        }
        catch (Exception exception)
        {
            // The game is already gone. A later Download performs the same
            // cleanup strictly before gogdl runs, so uninstall must not remain
            // stuck merely because an auxiliary file was temporarily busy.
            WriteGogLaunchLog(
                gameId,
                $"deferred gogdl install-state cleanup error={exception.Message}");
        }
    }

    private static void UpdateXboxInstalledCache(
        string productId,
        UnifySteamGameCacheEntry installedGame)
    {
        try
        {
            GetStoreSyncSettingsStore().Update(configuration =>
            {
                if (!configuration.UnifySteam.Stores.TryGetValue(
                        "xbox-game-pass",
                        out var store) ||
                    store?.Cache?.Games is null)
                {
                    return;
                }

                var game = store.Cache.Games.FirstOrDefault(candidate =>
                    candidate is not null &&
                    string.Equals(
                        candidate.Id,
                        productId,
                        StringComparison.OrdinalIgnoreCase));
                if (game is null)
                {
                    return;
                }

                game.Installed = true;
                game.InstallPath = installedGame.InstallPath;
                game.ExecutablePath = installedGame.ExecutablePath;
                game.Version = installedGame.Version;
                game.ProviderGameId = installedGame.Id;
            });
        }
        catch
        {
            // The dynamic installed-state scan still updates Steam. The next
            // library delta repairs the persisted metadata if this write races
            // another settings update.
        }
    }

    private static void UpdateXboxUninstalledCache(string productId)
    {
        try
        {
            GetStoreSyncSettingsStore().Update(configuration =>
            {
                if (!configuration.UnifySteam.Stores.TryGetValue(
                        "xbox-game-pass",
                        out var store) ||
                    store?.Cache?.Games is null)
                {
                    return;
                }

                var game = store.Cache.Games.FirstOrDefault(candidate =>
                    candidate is not null &&
                    string.Equals(candidate.Id, productId, StringComparison.OrdinalIgnoreCase));
                if (game is null)
                {
                    return;
                }

                game.Installed = false;
                game.InstallPath = string.Empty;
                game.ExecutablePath = string.Empty;
                game.Version = string.Empty;
            });
        }
        catch
        {
            // The dynamic Xbox package scan remains the source of truth.
        }
    }

    private static string FindGogGalaxyClientPath()
    {
        var candidates = GetGogGalaxyClientCandidates()
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindTool("GalaxyClient.exe", "GalaxyClient");
    }

    private static string FindEpicGamesLauncherPath()
    {
        foreach (var candidate in GetEpicGamesLauncherCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindTool("EpicGamesLauncher.exe", "EpicGamesLauncher");
    }

    private static string FindEaAppPath()
    {
        return EaAppIntegration.GetAvailability().ExecutablePath;
    }

    private static string FindUbisoftConnectPath()
    {
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher"),
                     (Registry.LocalMachine, @"SOFTWARE\Ubisoft\Launcher"),
                     (Registry.CurrentUser, @"SOFTWARE\Ubisoft\Launcher"),
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                var installDirectory =
                    key?.GetValue("InstallDir") as string ??
                    key?.GetValue("InstallDirLauncher") as string;
                if (!string.IsNullOrWhiteSpace(installDirectory))
                {
                    var candidate = Path.Combine(
                        installDirectory.Trim().Trim('"'),
                        "UbisoftConnect.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            var candidate = Path.Combine(
                root,
                "Ubisoft",
                "Ubisoft Game Launcher",
                "UbisoftConnect.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindTool("UbisoftConnect.exe", "UbisoftConnect");
    }

    private static IEnumerable<string> GetEpicGamesLauncherCandidates()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
                 })
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe");
        }
    }

    private static IEnumerable<string> GetGogGalaxyClientCandidates()
    {
        foreach (var candidate in GetGogGalaxyRegistryCandidates())
        {
            yield return candidate;
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            yield return Path.Combine(folder, "GOG Galaxy", "GalaxyClient.exe");
            yield return Path.Combine(folder, "GOG.com", "Galaxy", "GalaxyClient.exe");
        }
    }

    private static IEnumerable<string> GetGogGalaxyRegistryCandidates()
    {
        foreach (var root in OpenUninstallRegistryRoots())
        {
            using (root)
            {
                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using var appKey = root.OpenSubKey(subKeyName);
                    if (appKey is null)
                    {
                        continue;
                    }

                    var displayName = GetRegistryString(appKey, "DisplayName") ?? string.Empty;
                    if (!displayName.Contains("GOG", StringComparison.OrdinalIgnoreCase) ||
                        !displayName.Contains("GALAXY", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var installLocation = NormalizeLoosePath(GetRegistryString(appKey, "InstallLocation") ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        yield return Path.Combine(installLocation, "GalaxyClient.exe");
                    }

                    foreach (var executableValue in new[]
                             {
                                 GetRegistryString(appKey, "DisplayIcon"),
                                 GetRegistryString(appKey, "UninstallString"),
                             })
                    {
                        var extractedPath = ExtractRegistryExecutablePath(executableValue ?? string.Empty);
                        var directory = string.IsNullOrWhiteSpace(extractedPath)
                            ? string.Empty
                            : Path.GetDirectoryName(extractedPath) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            yield return Path.Combine(directory, "GalaxyClient.exe");
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<RegistryKey> OpenUninstallRegistryRoots()
    {
        foreach (var (hive, path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            var key = hive.OpenSubKey(path);
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static string FindGogAuthPath()
    {
        if (File.Exists(ManagedGogDlHelper.AuthPath))
        {
            return ManagedGogDlHelper.AuthPath;
        }

        var configuration = LoadStoreSyncConfiguration();
        if (configuration.UnifySteam.Stores.TryGetValue("gog-galaxy", out var store) &&
            !string.IsNullOrWhiteSpace(store?.AuthPath) &&
            File.Exists(store.AuthPath))
        {
            return store.AuthPath;
        }

        return string.Empty;
    }

    private static string? GetRegistryString(RegistryKey key, string valueName)
    {
        return key.GetValue(valueName) as string;
    }

    private static string ExtractRegistryExecutablePath(string value)
    {
        var trimmed = NormalizeLoosePath(value);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1
                ? NormalizeLoosePath(trimmed[1..closingQuote])
                : trimmed.Trim('"');
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0
            ? NormalizeLoosePath(trimmed[..(exeIndex + 4)])
            : trimmed;
    }

    private static string NormalizeLoosePath(string value)
    {
        var trimmed = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static int Fail(string message)
    {
        ShowError(message);
        return 1;
    }

    private static void ShowError(string message)
    {
        System.Windows.MessageBox.Show(
            message,
            "OmniLibrary",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
