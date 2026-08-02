using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Services;

public sealed class SteamClientLaunchService
{
    private const string BigPictureProtocolUri = "steam://open/bigpicture";
    private const string SteamStartupMutexName = @"Local\ToolsForSteam.SteamStartup";
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(8);

    // Automatic startup recovery is deliberately a single hard restart. A
    // persistent cooldown below also prevents a newly compiled/relaunched TFS
    // process from resetting the budget and creating a kill/relaunch loop.
    private const int MaxAutomaticRecoveryAttempts = 1;

    public static SteamClientLaunchState PrepareConsoleStartup(
        SteamInstallationService steamInstallationService)
    {
        var steamExePath = steamInstallationService.ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            SteamStartupDiagnostics.Write("console bootstrap failed: steam.exe was not found");
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        using var startupMutex = new Mutex(false, SteamStartupMutexName);
        var ownsMutex = false;
        try
        {
            ownsMutex = startupMutex.WaitOne(TimeSpan.FromSeconds(15));
            if (!ownsMutex)
            {
                SteamStartupDiagnostics.Write("console bootstrap skipped: another startup owner holds the mutex");
                return new SteamClientLaunchState(false, "Another Tools for Steam startup owner is preparing Steam.");
            }

            return PrepareOwnedConsoleStartup(steamExePath);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
            SteamStartupDiagnostics.Write("console bootstrap recovered an abandoned startup mutex");
            return PrepareOwnedConsoleStartup(steamExePath);
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"console bootstrap failed: {exception}");
            return new SteamClientLaunchState(false, $"Steam could not be prepared: {exception.Message}");
        }
        finally
        {
            if (ownsMutex)
            {
                startupMutex.ReleaseMutex();
            }
        }
    }

    private static SteamClientLaunchState PrepareOwnedConsoleStartup(string steamExePath)
    {
        var steamRunning = IsSteamRunning();
        var commandLine = TryGetSteamMainProcessCommandLine();
        SteamStartupDiagnostics.Write(
            $"console bootstrap inspect running={steamRunning} commandLine={commandLine ?? "<missing>"}");

        if (!steamRunning)
        {
            LaunchSteam(steamExePath);
            SteamStartupDiagnostics.Write("console bootstrap launched one clean Steam process");
            return new SteamClientLaunchState(false, "Starting Steam in Gamepad UI with DevTools enabled.");
        }

        if (HasRequiredConsoleLaunchArguments(commandLine))
        {
            SteamStartupDiagnostics.Write("console bootstrap kept the existing compatible Steam startup");
            return new SteamClientLaunchState(false, "Steam is already starting with the required TFS arguments.");
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            // WMI can briefly return no command line for a freshly created or
            // elevated Steam process. Missing evidence is not sufficient reason
            // to terminate it; the endpoint monitor will perform bounded recovery
            // later only if Steam truly fails to become ready.
            SteamStartupDiagnostics.Write(
                "console bootstrap could not verify Steam arguments yet; keeping the current process");
            return new SteamClientLaunchState(
                false,
                "Steam is running; its startup arguments could not be verified yet.");
        }

        var safety = new SteamStartupEnvironmentProbe(Path.GetDirectoryName(steamExePath))
            .CaptureRecoverySafety();
        if (safety.BlockRecovery)
        {
            var message = $"Kept the existing Steam session because recovery is protected: {safety.DescribeReason()}";
            SteamStartupDiagnostics.Write($"console bootstrap {message}");
            return new SteamClientLaunchState(
                false,
                message,
                safety.UpdateInProgress
                    ? SteamClientStartupStage.Updating
                    : SteamClientStartupStage.Protected);
        }

        var recoveryGuard = new SteamAutomaticRecoveryGuard();
        if (!recoveryGuard.TryBegin(DateTimeOffset.UtcNow, out var retryAfter))
        {
            var retryMinutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
            var message =
                $"Kept the existing Steam session because an automatic recovery already ran. " +
                $"The recovery budget resets in about {retryMinutes} minute(s) or when Steam becomes healthy.";
            SteamStartupDiagnostics.Write($"console bootstrap {message}");
            return new SteamClientLaunchState(
                false,
                message,
                SteamClientStartupStage.Failed);
        }

        SteamStartupDiagnostics.Write("console bootstrap found an incompatible or orphaned Steam startup; restarting once");
        if (!RestartSteamForConsoleBootstrap(steamExePath))
        {
            SteamStartupDiagnostics.Write("console bootstrap could not stop the conflicting Steam process tree");
            return new SteamClientLaunchState(false, "The conflicting Steam startup could not be stopped safely.");
        }

        SteamStartupDiagnostics.Write("console bootstrap replaced the conflicting startup with one clean Steam process");
        return new SteamClientLaunchState(false, "Restarted Steam once with the required Gamepad UI and DevTools arguments.");
    }

    internal static bool HasRequiredConsoleLaunchArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return commandLine.Contains("-gamepadui", StringComparison.OrdinalIgnoreCase) &&
            commandLine.Contains("-dev", StringComparison.OrdinalIgnoreCase) &&
            commandLine.Contains("-cef-enable-debugging", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSteamRunningForStartup() => IsSteamRunning();

    private readonly HttpClient _httpClient;
    private readonly Uri _debugEndpoint;
    private readonly SteamInstallationService _steamInstallationService;
    private readonly TimeSpan _startupGracePeriod;
    private readonly TimeSpan _invisibleUiGracePeriod;
    private readonly object _recoverySync = new();
    private readonly SteamAutomaticRecoveryGuard _automaticRecoveryGuard = new();
    private readonly SteamStartupEnvironmentProbe _environmentProbe;
    private readonly SteamStartupHistoryStore _historyStore = new();
    private readonly DateTimeOffset _startupSessionStartedAt = DateTimeOffset.UtcNow;

    private DateTimeOffset? _firstUnavailableAt;
    private DateTimeOffset? _firstInvisibleUiAt;
    private DateTimeOffset _lastActionAt = DateTimeOffset.MinValue;
    private int _recoveryAttempts;
    private int _launchAttempts;
    private bool _steamUiWasVisible;
    private bool _historyOutcomeRecorded;
    private string _lastProtectedRecoveryMessage = string.Empty;

    public SteamClientLaunchService(
        HttpClient httpClient,
        Uri debugEndpoint,
        SteamInstallationService steamInstallationService,
        bool isHandheld = false)
    {
        _httpClient = httpClient;
        _debugEndpoint = debugEndpoint;
        _steamInstallationService = steamInstallationService;
        _environmentProbe = new SteamStartupEnvironmentProbe(
            steamInstallationService.ResolveSteamRootPath());
        var timing = _historyStore.GetTimingPolicy(isHandheld);
        _startupGracePeriod = timing.StartupGracePeriod;
        _invisibleUiGracePeriod = TimeSpan.FromSeconds(Math.Clamp(
            timing.StartupGracePeriod.TotalSeconds * 0.75,
            30,
            90));
        SteamStartupDiagnostics.Write(
            $"startup timing grace={_startupGracePeriod.TotalSeconds:F0}s " +
            $"invisibleUi={_invisibleUiGracePeriod.TotalSeconds:F0}s adaptive={timing.IsAdaptive}");
    }

    public static SteamClientLaunchState RequestSteamStartForTools(SteamInstallationService steamInstallationService)
    {
        var steamExePath = steamInstallationService.ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        try
        {
            if (IsSteamRunning())
            {
                RequestSteamGamepadUi();
                return new SteamClientLaunchState(
                    false,
                    "Steam is already running. Requesting the Gamepad UI and waiting for the DevTools endpoint.");
            }

            LaunchSteam(steamExePath);
            return new SteamClientLaunchState(
                false,
                "Starting Steam in Gamepad UI with DevTools enabled.");
        }
        catch (Exception exception)
        {
            return new SteamClientLaunchState(
                false,
                $"Steam could not be started: {exception.Message}");
        }
    }

    public static SteamClientLaunchState RequestSteamAttention(SteamInstallationService steamInstallationService)
    {
        var steamExePath = steamInstallationService.ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        try
        {
            if (!IsSteamRunning())
            {
                LaunchSteam(steamExePath);
                return new SteamClientLaunchState(
                    false,
                    "Starting Steam in Gamepad UI with DevTools enabled.");
            }

            RequestSteamGamepadUi();
            return new SteamClientLaunchState(
                false,
                "Requesting the Steam Gamepad UI.");
        }
        catch (Exception exception)
        {
            return new SteamClientLaunchState(
                false,
                $"Steam could not be activated: {exception.Message}");
        }
    }

    public async Task<SteamClientLaunchState> EnsureDevToolsReadyAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var launchAttemptInFlight = _firstUnavailableAt.HasValue;
        if (await IsDebugEndpointAvailableAsync(cancellationToken))
        {
            if (IsSteamUiVisible())
            {
                _steamUiWasVisible = true;
                _firstInvisibleUiAt = null;
                _firstUnavailableAt = null;
                _recoveryAttempts = 0;
                _launchAttempts = 0;
                _automaticRecoveryGuard.MarkHealthy();
                RecordStartupOutcome(SteamStartupOutcome.Ready, "Steam DevTools and UI became ready.");
                return new SteamClientLaunchState(
                    true,
                    "Steam DevTools endpoint and UI are ready.",
                    SteamClientStartupStage.Ready);
            }

            if (_steamUiWasVisible)
            {
                // Steam may hide its main window while a game is active. Once
                // the UI was confirmed during this session, DevTools alone is
                // enough and we must never restart Steam underneath a game.
                _firstUnavailableAt = null;
                _launchAttempts = 0;
                _automaticRecoveryGuard.MarkHealthy();
                RecordStartupOutcome(
                    SteamStartupOutcome.Ready,
                    "Steam DevTools remained ready while its UI was hidden by a game.");
                return new SteamClientLaunchState(
                    true,
                    "Steam DevTools endpoint is ready.",
                    SteamClientStartupStage.Ready);
            }

            _firstUnavailableAt = null;
            _firstInvisibleUiAt ??= now;
            if (launchAttemptInFlight || now - _lastActionAt >= ActionCooldown)
            {
                RequestSteamGamepadUi();
                _lastActionAt = now;
                SteamStartupDiagnostics.Write("Steam DevTools is ready but no UI is visible; requested Gamepad UI attention");
            }

            if (now - _firstInvisibleUiAt.Value < _invisibleUiGracePeriod)
            {
                return new SteamClientLaunchState(
                    true,
                    "Steam is running, but its UI is still opening. Requesting Gamepad UI attention.");
            }

            if (_recoveryAttempts >= MaxAutomaticRecoveryAttempts)
            {
                RecordStartupOutcome(SteamStartupOutcome.Failed, "Steam UI remained invisible after recovery.");
                return new SteamClientLaunchState(
                    false,
                    "Steam DevTools responds, but the Steam UI is still invisible after the one permitted automatic restart.",
                    SteamClientStartupStage.Failed);
            }

            var repairSteamExePath = ResolveSteamExecutablePath();
            if (repairSteamExePath is null)
            {
                RecordStartupOutcome(
                    SteamStartupOutcome.Failed,
                    "steam.exe could not be resolved for invisible-UI recovery.");
                return new SteamClientLaunchState(
                    false,
                    "Steam is running without a visible UI, but steam.exe could not be resolved for recovery.",
                    SteamClientStartupStage.Failed);
            }

            if (TryGetProtectedRecoveryState(now, out var protectedInvisibleUiState))
            {
                return protectedInvisibleUiState;
            }

            if (!TryBeginAutomaticRecovery(now, out var guardMessage))
            {
                RecordStartupOutcome(SteamStartupOutcome.Failed, guardMessage);
                return new SteamClientLaunchState(
                    false,
                    guardMessage,
                    SteamClientStartupStage.Failed);
            }

            _recoveryAttempts++;

            var uiRecovered = RecoverHungSteam(
                repairSteamExePath,
                forceKill: true,
                clearWebCache: false,
                disableCefGpu: false);
            _lastActionAt = now;
            _firstUnavailableAt = now;
            _firstInvisibleUiAt = null;
            _steamUiWasVisible = false;
            _launchAttempts = 1;
            SteamStartupDiagnostics.Write(
                $"Steam UI stayed invisible although DevTools responded; hard recovery attempt={_recoveryAttempts} result={uiRecovered}");
            return new SteamClientLaunchState(
                false,
                uiRecovered
                    ? "Steam was running without a visible UI. Its process tree was stopped and restarted cleanly."
                    : "Steam UI is invisible and its stale process tree could not be stopped yet.",
                uiRecovered
                    ? SteamClientStartupStage.Recovering
                    : SteamClientStartupStage.Failed);
        }

        _firstInvisibleUiAt = null;
        _firstUnavailableAt ??= now;

        if (now - _lastActionAt < ActionCooldown)
        {
            return new SteamClientLaunchState(
                false,
                "Steam is starting with the DevTools endpoint enabled.");
        }

        var steamExePath = ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            RecordStartupOutcome(SteamStartupOutcome.Failed, "steam.exe was not found.");
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.",
                SteamClientStartupStage.Failed);
        }

        if (!IsSteamRunning())
        {
            if (_launchAttempts > 0)
            {
                if (_recoveryAttempts >= MaxAutomaticRecoveryAttempts)
                {
                    RecordStartupOutcome(
                        SteamStartupOutcome.Failed,
                        "steam.exe exited again after its automatic relaunch.");
                    return new SteamClientLaunchState(
                        false,
                        "steam.exe exited again after the one permitted automatic relaunch. " +
                        "Automatic recovery is stopped to prevent a launch loop.",
                        SteamClientStartupStage.Failed);
                }

                if (TryGetProtectedRecoveryState(now, out var protectedMissingProcessState))
                {
                    return protectedMissingProcessState;
                }

                if (!TryBeginAutomaticRecovery(now, out var missingProcessGuardMessage))
                {
                    RecordStartupOutcome(SteamStartupOutcome.Failed, missingProcessGuardMessage);
                    return new SteamClientLaunchState(
                        false,
                        missingProcessGuardMessage,
                        SteamClientStartupStage.Failed);
                }

                _recoveryAttempts++;
                ForceKillSteamProcesses();
                LaunchSteam(steamExePath);
                _launchAttempts++;
                _lastActionAt = now;
                _firstUnavailableAt = now;
                SteamStartupDiagnostics.Write(
                    "steam.exe did not remain running; performed the one permitted clean relaunch");
                return new SteamClientLaunchState(
                    false,
                    "Steam closed during startup. All remaining Steam processes were stopped and Steam is relaunching once.",
                    SteamClientStartupStage.Recovering);
            }

            // Fresh boot: start once. If steam.exe immediately exits, the branch
            // above consumes the guarded recovery budget instead of launching it
            // every time the injector polls.
            LaunchSteam(steamExePath);
            _launchAttempts = 1;
            _lastActionAt = now;
            _firstUnavailableAt = now;
            _steamUiWasVisible = false;
            return new SteamClientLaunchState(
                false,
                "Starting Steam in Gamepad UI with DevTools enabled.");
        }

        // Steam is running but the DevTools endpoint never came up. Give it a
        // short grace window in case it is simply slow to finish loading.
        if (now - _firstUnavailableAt.Value < _startupGracePeriod)
        {
            return new SteamClientLaunchState(
                false,
                "Steam is running without the DevTools endpoint. Preparing a controlled restart.");
        }

        if (_recoveryAttempts >= MaxAutomaticRecoveryAttempts)
        {
            RecordStartupOutcome(
                SteamStartupOutcome.Failed,
                "Steam did not expose DevTools after its automatic restart.");
            return new SteamClientLaunchState(
                false,
                "Steam still did not finish loading after the one permitted automatic restart. " +
                "Automatic recovery is stopped to prevent a restart loop.",
                SteamClientStartupStage.Failed);
        }

        if (TryGetProtectedRecoveryState(now, out var protectedUnavailableState))
        {
            return protectedUnavailableState;
        }

        if (!TryBeginAutomaticRecovery(now, out var recoveryGuardMessage))
        {
            RecordStartupOutcome(SteamStartupOutcome.Failed, recoveryGuardMessage);
            return new SteamClientLaunchState(
                false,
                recoveryGuardMessage,
                SteamClientStartupStage.Failed);
        }

        _recoveryAttempts++;
        _lastActionAt = now;

        // The grace period has already separated a slow start from a stuck one.
        // Request Steam's official shutdown first; if it does not exit, terminate
        // the complete Steam process tree and relaunch once.
        var recovered = RecoverHungSteam(
            steamExePath,
            forceKill: true,
            clearWebCache: false,
            disableCefGpu: false);
        _launchAttempts = recovered ? 1 : 0;

        if (!recovered)
        {
            RecordStartupOutcome(
                SteamStartupOutcome.Failed,
                "The stale Steam process tree could not be stopped.");
            return new SteamClientLaunchState(
                false,
                "Steam's stale process tree could not be stopped. Automatic recovery will not loop.",
                SteamClientStartupStage.Failed);
        }

        return new SteamClientLaunchState(
            false,
            "Steam looked stuck. Its process tree was stopped and Steam is restarting once with the required Gamepad UI arguments.",
            SteamClientStartupStage.Recovering);
    }

    private bool TryGetProtectedRecoveryState(
        DateTimeOffset now,
        out SteamClientLaunchState launchState)
    {
        var safety = _environmentProbe.CaptureRecoverySafety();
        if (!safety.BlockRecovery)
        {
            _lastProtectedRecoveryMessage = string.Empty;
            launchState = default!;
            return false;
        }

        // When protected activity ends, Steam receives a fresh grace period
        // instead of being killed immediately on the next injector pass.
        _firstUnavailableAt = now;
        _firstInvisibleUiAt = now;
        var stage = safety.UpdateInProgress || safety.DownloadInProgress
            ? SteamClientStartupStage.Updating
            : SteamClientStartupStage.Protected;
        var message = $"Automatic Steam recovery is paused: {safety.DescribeReason()}";
        if (!string.Equals(message, _lastProtectedRecoveryMessage, StringComparison.Ordinal))
        {
            _lastProtectedRecoveryMessage = message;
            SteamStartupDiagnostics.Write(message);
        }
        launchState = new SteamClientLaunchState(false, message, stage);
        return true;
    }

    private void RecordStartupOutcome(SteamStartupOutcome outcome, string detail)
    {
        if (_historyOutcomeRecorded)
        {
            return;
        }

        _historyOutcomeRecorded = true;
        _historyStore.Record(
            _startupSessionStartedAt,
            outcome,
            recoveryUsed: _recoveryAttempts > 0,
            detail);
    }

    private bool TryBeginAutomaticRecovery(DateTimeOffset now, out string message)
    {
        if (_automaticRecoveryGuard.TryBegin(now, out var retryAfter))
        {
            message = string.Empty;
            return true;
        }

        var retryMinutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
        message =
            $"Automatic Steam recovery is paused for about {retryMinutes} more minute(s) " +
            "to prevent a kill/relaunch loop.";
        SteamStartupDiagnostics.Write(message);
        return false;
    }

    public static string BuildSteamLaunchArguments(bool launchBigPicture, bool disableCefGpu = false)
    {
        var arguments = new List<string>();
        if (launchBigPicture)
        {
            arguments.Add("-gamepadui");
        }

        arguments.Add("-dev");
        arguments.Add("-cef-enable-debugging");

        if (disableCefGpu)
        {
            // Renders the CEF-based Steam UI without GPU acceleration. This is a
            // known workaround for the client hanging on a blank splash screen
            // when a GPU/driver problem stalls the browser layer.
            arguments.Add("-cef-disable-gpu");
        }

        return string.Join(' ', arguments);
    }

    public SteamClientLaunchState RestartSteamForSteamTools(
        bool clearWebCache = true,
        bool respectProtectedActivity = false)
    {
        var steamExePath = ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        if (respectProtectedActivity &&
            TryGetProtectedRecoveryState(DateTimeOffset.UtcNow, out var protectedState))
        {
            return protectedState;
        }

        // User-initiated restarts first try a graceful shutdown and force-kill
        // only when Steam will not exit. The Power repair action also clears the
        // web cache; the post-driver-update action deliberately preserves it.
        var restartRequested = RecoverHungSteam(
            steamExePath,
            forceKill: true,
            clearWebCache,
            disableCefGpu: false);

        _lastActionAt = DateTimeOffset.UtcNow;
        _firstUnavailableAt = _lastActionAt;
        _firstInvisibleUiAt = null;
        _steamUiWasVisible = false;
        _recoveryAttempts = restartRequested ? 1 : 0;
        _launchAttempts = restartRequested ? 1 : 0;

        return new SteamClientLaunchState(
            false,
            restartRequested
                ? clearWebCache
                    ? "Steam is restarting in Gamepad UI with a freshly cleared web cache."
                    : "Steam is restarting in Gamepad UI so its GPU processes load the updated driver."
                : "Waiting for Steam to finish its official shutdown before restarting.");
    }

    private async Task<bool> IsDebugEndpointAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_debugEndpoint, "/json/list"));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private bool RecoverHungSteam(
        string steamExePath,
        bool forceKill,
        bool clearWebCache,
        bool disableCefGpu)
    {
        using var startupMutex = new Mutex(false, SteamStartupMutexName);
        var ownsStartupMutex = false;
        try
        {
            try
            {
                ownsStartupMutex = startupMutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                ownsStartupMutex = true;
            }

            if (!ownsStartupMutex)
            {
                SteamStartupDiagnostics.Write("Steam recovery skipped because another startup repair owns the mutex");
                return false;
            }

            // Serialize both threads and processes so the Xbox host watchdog,
            // the background injector and a manual restart cannot overlap.
            lock (_recoverySync)
            {
                if (!CloseSteam(forceIfUnresponsive: forceKill))
                {
                    return false;
                }

                if (clearWebCache)
                {
                    ClearSteamWebCache();
                }

                LaunchSteam(steamExePath, disableCefGpu);
                return true;
            }
        }
        finally
        {
            if (ownsStartupMutex)
            {
                startupMutex.ReleaseMutex();
            }
        }
    }

    private static void LaunchSteam(string steamExePath, bool disableCefGpu = false)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = steamExePath,
            Arguments = BuildSteamLaunchArguments(launchBigPicture: true, disableCefGpu: disableCefGpu),
            WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    private static bool RestartSteamForConsoleBootstrap(string steamExePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExePath,
                Arguments = "-shutdown",
                WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"official Steam shutdown request failed: {exception.Message}");
        }

        if (!WaitForSteamToExit(TimeSpan.FromSeconds(20)))
        {
            SteamStartupDiagnostics.Write("official Steam shutdown timed out; terminating the stale boot process tree");
            ForceKillSteamProcesses();
            if (!WaitForSteamToExit(ForceKillTimeout))
            {
                return false;
            }
        }

        LaunchSteam(steamExePath);
        return true;
    }

    private static string? TryGetSteamMainProcessCommandLine()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'steam.exe'");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                var commandLine = Convert.ToString(item["CommandLine"])?.Trim();
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    return commandLine;
                }
            }
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"Steam command-line inspection failed: {exception.Message}");
        }

        return null;
    }

    private bool CloseSteam(bool forceIfUnresponsive)
    {
        var steamProcesses = GetSteamProcesses();
        if (steamProcesses.Count == 0)
        {
            return true;
        }

        try
        {
            foreach (var process in steamProcesses)
            {
                try
                {
                    if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch
                {
                }
            }

            var steamExePath = ResolveSteamExecutablePath();
            if (steamExePath is not null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = steamExePath,
                        Arguments = "-shutdown",
                        WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    })?.Dispose();
                }
                catch
                {
                }
            }

            if (WaitForSteamToExit(ShutdownTimeout))
            {
                return true;
            }

            if (!forceIfUnresponsive)
            {
                return !IsSteamRunning();
            }

            // The official "-shutdown" did not take within the timeout - the client
            // is wedged (typically a hung steamwebhelper on the splash screen).
            // Terminate the whole Steam process tree so we can relaunch cleanly.
            ForceKillSteamProcesses();
            return WaitForSteamToExit(ForceKillTimeout);
        }
        finally
        {
            DisposeProcesses(steamProcesses);
        }
    }

    private static bool WaitForSteamToExit(TimeSpan timeout)
    {
        var timeoutAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (!IsSteamRunning())
            {
                return true;
            }

            Thread.Sleep(300);
        }

        return !IsSteamRunning();
    }

    private static void ForceKillSteamProcesses()
    {
        var steamProcesses = GetSteamProcesses();
        try
        {
            foreach (var process in steamProcesses)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }
        finally
        {
            DisposeProcesses(steamProcesses);
        }
    }

    private void ClearSteamWebCache()
    {
        foreach (var cacheDirectory in EnumerateWebCacheDirectories())
        {
            try
            {
                if (Directory.Exists(cacheDirectory))
                {
                    Directory.Delete(cacheDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort: a locked file just means a slightly staler cache,
                // which is still fine for the next launch.
            }
        }
    }

    private IEnumerable<string> EnumerateWebCacheDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            // Current Steam clients keep the CEF/browser cache here.
            yield return Path.Combine(localAppData, "Steam", "htmlcache");
        }

        var steamRoot = _steamInstallationService.ResolveSteamRootPath();
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            // Older layouts (and some installs) also keep one under the install dir.
            yield return Path.Combine(steamRoot, "config", "htmlcache");
        }
    }

    private string? ResolveSteamExecutablePath()
    {
        return _steamInstallationService.ResolveSteamExecutablePath();
    }

    private static bool IsSteamRunning()
    {
        var steamProcesses = GetSteamProcesses();
        try
        {
            return steamProcesses.Count > 0;
        }
        finally
        {
            DisposeProcesses(steamProcesses);
        }
    }

    private static IReadOnlyList<Process> GetSteamProcesses()
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                keep = !process.HasExited &&
                    (string.Equals(process.ProcessName, "steam", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(process.ProcessName, "steamwebhelper", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(process.ProcessName, "GameOverlayUI", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(process.ProcessName, "steamerrorreporter", StringComparison.OrdinalIgnoreCase));
                if (keep)
                {
                    matches.Add(process);
                }
            }
            catch
            {
            }
            finally
            {
                if (!keep)
                {
                    process.Dispose();
                }
            }
        }

        return matches;
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private static void RequestSteamGamepadUi()
    {
        if (IsSteamUiVisible())
        {
            return;
        }

        if (TryFocusSteamWindow())
        {
            return;
        }

        TryOpenBigPictureProtocol();
    }

    private static bool IsBigPictureWindowVisible()
    {
        var processes = Process.GetProcessesByName("steamwebhelper");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited &&
                        process.MainWindowHandle != IntPtr.Zero &&
                        IsBigPictureWindowTitle(process.MainWindowTitle))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsSteamUiVisible() =>
        SteamBigPictureForegroundDetector.Capture().HasVisibleSteamWindow;

    private static bool IsBigPictureWindowTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return title.Replace('-', ' ').Contains("Big Picture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFocusSteamWindow()
    {
        var handle = FindSteamWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(handle, ShowWindowRestore);
        SetForegroundWindow(handle);
        return true;
    }

    private static IntPtr FindSteamWindowHandle()
    {
        var preferredHandle = FindSteamWindowHandle("steamwebhelper", preferSteamTitle: true);
        if (preferredHandle != IntPtr.Zero)
        {
            return preferredHandle;
        }

        var steamHandle = FindSteamWindowHandle("steam", preferSteamTitle: false);
        if (steamHandle != IntPtr.Zero)
        {
            return steamHandle;
        }

        return FindSteamWindowHandle("steamwebhelper", preferSteamTitle: false);
    }

    private static IntPtr FindSteamWindowHandle(string processName, bool preferSteamTitle)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            var fallbackHandle = IntPtr.Zero;
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    fallbackHandle = process.MainWindowHandle;
                    if (!preferSteamTitle ||
                        process.MainWindowTitle.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
                        IsBigPictureWindowTitle(process.MainWindowTitle))
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch
                {
                }
            }

            return fallbackHandle;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void TryOpenBigPictureProtocol()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BigPictureProtocolUri,
                UseShellExecute = true
            })?.Dispose();
        }
        catch
        {
        }
    }

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

public enum SteamClientStartupStage
{
    Starting,
    Updating,
    Protected,
    Recovering,
    Ready,
    Failed
}

public sealed record SteamClientLaunchState(
    bool DevToolsReady,
    string Message,
    SteamClientStartupStage StartupStage = SteamClientStartupStage.Starting);
