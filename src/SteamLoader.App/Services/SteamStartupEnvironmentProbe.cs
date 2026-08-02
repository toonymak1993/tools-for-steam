using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Services;

internal sealed class SteamStartupEnvironmentProbe
{
    private static readonly string[][] SteamDownloadDirectories =
    [
        ["steamapps", "downloading"],
        ["steamapps", "workshop", "downloads"]
    ];

    private static readonly string[] SteamProcessNames =
    [
        "steam",
        "steamwebhelper",
        "GameOverlayUI",
        "steamerrorreporter"
    ];

    private static readonly HashSet<string> KnownSteamDescendants = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameOverlayUI.exe",
        "steam.exe",
        "steamerrorreporter.exe",
        "steamservice.exe",
        "steamwebhelper.exe",
        "streaming_client.exe",
        "vrcompositor.exe",
        "vrdashboard.exe",
        "vrmonitor.exe",
        "vrserver.exe"
    };

    private readonly string? _steamRoot;
    private DateTime _bootstrapLogWriteUtc;
    private string _bootstrapLogTail = string.Empty;

    public SteamStartupEnvironmentProbe(string? steamRoot)
    {
        _steamRoot = string.IsNullOrWhiteSpace(steamRoot) ? null : steamRoot;
    }

    public SteamRuntimeObservation Capture()
    {
        var processes = CaptureSteamProcessState();
        var windows = SteamBigPictureForegroundDetector.Capture(_steamRoot);
        var updateInProgress = IsRecentBootstrapUpdateActivity();

        return new SteamRuntimeObservation(
            SteamRunning: processes.SteamRunning,
            WebHelperRunning: processes.WebHelperRunning,
            ErrorReporterRunning: processes.ErrorReporterRunning,
            GameOrOverlayRunning: processes.GameOverlayRunning,
            UpdateInProgress: updateInProgress,
            Windows: windows);
    }

    public SteamRecoverySafetySnapshot CaptureRecoverySafety()
    {
        var observation = Capture();
        var downloadActive = HasRecentDownloadActivity(out var downloadInspectionFailed);
        var childProcess = FindPotentialGameOrLauncherProcess();
        return new SteamRecoverySafetySnapshot(
            BlockRecovery: observation.UpdateInProgress ||
                observation.GameOrOverlayRunning ||
                downloadActive ||
                downloadInspectionFailed ||
                childProcess is not null,
            UpdateInProgress: observation.UpdateInProgress,
            DownloadInProgress: downloadActive,
            DownloadInspectionFailed: downloadInspectionFailed,
            GameOrOverlayRunning: observation.GameOrOverlayRunning,
            PotentialGameProcess: childProcess ?? string.Empty);
    }

    private bool IsRecentBootstrapUpdateActivity()
    {
        if (_steamRoot is null)
        {
            return false;
        }

        var logPath = Path.Combine(_steamRoot, "logs", "bootstrap_log.txt");
        try
        {
            if (!File.Exists(logPath))
            {
                return false;
            }

            var writeUtc = File.GetLastWriteTimeUtc(logPath);
            if (DateTime.UtcNow - writeUtc > TimeSpan.FromMinutes(2))
            {
                return false;
            }

            if (writeUtc != _bootstrapLogWriteUtc)
            {
                _bootstrapLogWriteUtc = writeUtc;
                _bootstrapLogTail = ReadFileTail(logPath, 32 * 1024);
            }

            return IsBootstrapUpdateActive(_bootstrapLogTail);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsBootstrapUpdateActive(string logTail)
    {
        if (string.IsNullOrWhiteSpace(logTail))
        {
            return false;
        }

        var lastActivity = LastIndexOfAny(
            logTail,
            "checking for update",
            "checking for available updates",
            "downloading update",
            "downloading manifest",
            "applying update",
            "extracting package",
            "installing update",
            "verifying installation",
            "self-update",
            "self update");
        if (lastActivity < 0)
        {
            return false;
        }

        var lastCompletion = LastIndexOfAny(
            logTail,
            "update complete",
            "update completed",
            "nothing to do",
            "download skipped",
            "already up to date",
            "already up-to-date",
            "no update required",
            "launching steam");
        return lastActivity > lastCompletion;
    }

    private bool HasRecentDownloadActivity(out bool inspectionFailed)
    {
        inspectionFailed = false;
        if (_steamRoot is null)
        {
            return false;
        }

        try
        {
            var libraryRoots = ReadSteamLibraryRoots(_steamRoot, out inspectionFailed);
            foreach (var libraryRoot in libraryRoots)
            {
                foreach (var relativeDirectory in SteamDownloadDirectories)
                {
                    var downloadDirectory = Path.Combine([libraryRoot, .. relativeDirectory]);
                    if (HasRecentActivity(downloadDirectory, out var scanWasIncomplete))
                    {
                        return true;
                    }

                    inspectionFailed |= scanWasIncomplete;
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            inspectionFailed = true;
            SteamStartupDiagnostics.Write(
                $"Steam download safety inspection failed closed: {exception.Message}");
            return false;
        }
    }

    private static bool HasRecentActivity(string directoryPath, out bool scanWasIncomplete)
    {
        scanWasIncomplete = false;
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        var recentThreshold = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        if (Directory.GetLastWriteTimeUtc(directoryPath) >= recentThreshold)
        {
            return true;
        }

        // This path is only inspected immediately before destructive recovery.
        // Bound each library scan so an unusually large or stale download tree
        // cannot delay startup noticeably.
        var inspectedEntries = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directoryPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (++inspectedEntries > 256)
            {
                // Keep the pre-recovery check cheap, but never treat a truncated
                // scan as proof that terminating Steam is safe.
                scanWasIncomplete = true;
                return false;
            }

            if (GetLastWriteTimeUtc(path) >= recentThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime GetLastWriteTimeUtc(string path) =>
        Directory.Exists(path)
            ? Directory.GetLastWriteTimeUtc(path)
            : File.GetLastWriteTimeUtc(path);

    private static IReadOnlyList<string> ReadSteamLibraryRoots(
        string steamRoot,
        out bool inspectionFailed)
    {
        inspectionFailed = false;
        var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            return NormalizeSteamLibraryRoots(steamRoot, string.Empty);
        }

        try
        {
            using var stream = new FileStream(
                libraryFoldersPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new char[256 * 1024];
            var characterCount = reader.ReadBlock(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, characterCount);
            if (reader.Peek() >= 0)
            {
                inspectionFailed = true;
                SteamStartupDiagnostics.Write(
                    "Steam library safety inspection was truncated and failed closed");
            }

            return NormalizeSteamLibraryRoots(steamRoot, content);
        }
        catch (Exception exception)
        {
            inspectionFailed = true;
            SteamStartupDiagnostics.Write(
                $"Steam library safety inspection failed closed: {exception.Message}");
            return NormalizeSteamLibraryRoots(steamRoot, string.Empty);
        }
    }

    internal static IReadOnlyList<string> NormalizeSteamLibraryRoots(
        string steamRoot,
        string libraryFoldersContent)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLibraryRoot(roots, steamRoot);

        foreach (Match match in Regex.Matches(
                     libraryFoldersContent ?? string.Empty,
                     "\"path\"\\s+\"(?<path>[^\"]+)\"",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddLibraryRoot(roots, match.Groups["path"].Value.Replace("\\\\", "\\"));
        }

        return roots.ToArray();
    }

    private static void AddLibraryRoot(HashSet<string> roots, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var candidate = path.Trim();
            if (!Path.IsPathFullyQualified(candidate))
            {
                return;
            }

            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            roots.Add(normalized);
        }
        catch
        {
            // Ignore malformed third-party library entries. The primary Steam
            // root remains covered and valid secondary roots are still checked.
        }
    }

    private static string? FindPotentialGameOrLauncherProcess()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");
            using var results = searcher.Get();
            var processes = results.Cast<ManagementObject>()
                .Select(item => new ProcessNode(
                    Convert.ToInt32(item["ProcessId"]),
                    Convert.ToInt32(item["ParentProcessId"]),
                    Convert.ToString(item["Name"]) ?? string.Empty))
                .Where(node => node.ProcessId > 0)
                .ToDictionary(node => node.ProcessId);

            var steamProcessIds = processes.Values
                .Where(node => SteamProcessNames.Any(name =>
                    node.Name.Equals($"{name}.exe", StringComparison.OrdinalIgnoreCase)))
                .Select(node => node.ProcessId)
                .ToHashSet();
            if (steamProcessIds.Count == 0)
            {
                return null;
            }

            foreach (var node in processes.Values)
            {
                if (KnownSteamDescendants.Contains(node.Name) || node.ProcessId == Environment.ProcessId)
                {
                    continue;
                }

                var ancestorId = node.ParentProcessId;
                for (var depth = 0; depth < 8 && ancestorId > 0; depth++)
                {
                    if (steamProcessIds.Contains(ancestorId))
                    {
                        return $"{node.Name} ({node.ProcessId})";
                    }

                    if (!processes.TryGetValue(ancestorId, out var parent))
                    {
                        break;
                    }

                    ancestorId = parent.ParentProcessId;
                }
            }
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"Steam child-process safety inspection failed closed: {exception.Message}");
            return "unknown-process-state";
        }

        return null;
    }

    private static SteamProcessState CaptureSteamProcessState()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"Steam process snapshot failed: {exception.Message}");
            return new SteamProcessState(false, false, false, false);
        }

        var steamRunning = false;
        var webHelperRunning = false;
        var gameOverlayRunning = false;
        var errorReporterRunning = false;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var processName = process.ProcessName;
                    if (processName.Equals("steam", StringComparison.OrdinalIgnoreCase))
                    {
                        steamRunning = true;
                    }
                    else if (processName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase))
                    {
                        webHelperRunning = true;
                    }
                    else if (processName.Equals("GameOverlayUI", StringComparison.OrdinalIgnoreCase))
                    {
                        gameOverlayRunning = true;
                    }
                    else if (processName.Equals("steamerrorreporter", StringComparison.OrdinalIgnoreCase))
                    {
                        errorReporterRunning = true;
                    }
                }
                catch
                {
                }

                if (steamRunning && webHelperRunning && gameOverlayRunning && errorReporterRunning)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return new SteamProcessState(
            steamRunning,
            webHelperRunning,
            gameOverlayRunning,
            errorReporterRunning);
    }

    private static string ReadFileTail(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(maximumBytes, stream.Length);
        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        _ = stream.Read(buffer, 0, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    private static int LastIndexOfAny(string value, params string[] tokens)
    {
        var lastIndex = -1;
        foreach (var token in tokens)
        {
            lastIndex = Math.Max(
                lastIndex,
                value.LastIndexOf(token, StringComparison.OrdinalIgnoreCase));
        }

        return lastIndex;
    }

    private sealed record ProcessNode(int ProcessId, int ParentProcessId, string Name);

    private sealed record SteamProcessState(
        bool SteamRunning,
        bool WebHelperRunning,
        bool GameOverlayRunning,
        bool ErrorReporterRunning);
}

internal sealed record SteamRuntimeObservation(
    bool SteamRunning,
    bool WebHelperRunning,
    bool ErrorReporterRunning,
    bool GameOrOverlayRunning,
    bool UpdateInProgress,
    SteamWindowSnapshot Windows);

internal sealed record SteamRecoverySafetySnapshot(
    bool BlockRecovery,
    bool UpdateInProgress,
    bool DownloadInProgress,
    bool DownloadInspectionFailed,
    bool GameOrOverlayRunning,
    string PotentialGameProcess)
{
    public string DescribeReason() =>
        UpdateInProgress ? "Steam is installing an update." :
        DownloadInProgress ? "Steam has an active download." :
        DownloadInspectionFailed ? "Steam library download activity could not be inspected safely." :
        GameOrOverlayRunning ? "A Steam game or overlay is active." :
        !string.IsNullOrWhiteSpace(PotentialGameProcess)
            ? $"Steam child process {PotentialGameProcess} may be a running game or launcher."
            : "Steam activity could not be classified safely.";
}
