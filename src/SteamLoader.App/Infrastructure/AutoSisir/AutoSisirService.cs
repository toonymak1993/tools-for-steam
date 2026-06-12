using System.Diagnostics;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.AutoSisir;

public sealed class AutoSisirService
{
    private static readonly TimeSpan MissingGameStopGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MarkerLaunchCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExistingSisirCloseWait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SisirRestartDelay = TimeSpan.FromMilliseconds(1600);
    private const long MaximumLogBytes = 512 * 1024;
    private const int MaximumRecentLogLines = 80;

    private readonly AutoSisirSettingsStore _settingsStore;
    private readonly StoreSyncService _storeSyncService;
    private readonly Func<bool> _isPluginEnabled;
    private readonly string _logPath;
    private readonly object _gate = new();
    private Process? _markerProcess;
    private IReadOnlyList<StoreSyncDetectedTitleState> _cachedDetectedTitles = [];
    private DateTimeOffset _cachedDetectedTitlesAt = DateTimeOffset.MinValue;
    private string _statusText = "Auto SISR is disabled.";
    private string _activeGameTitle = string.Empty;
    private int? _activeGameProcessId;
    private string _activeGameExecutablePath = string.Empty;
    private DateTimeOffset? _missingGameSinceUtc;
    private DateTimeOffset _lastMarkerLaunchAttemptAt = DateTimeOffset.MinValue;
    private int? _markerStartedForProcessId;
    private string _markerStartedForTitle = string.Empty;
    private string _markerStartedForExecutablePath = string.Empty;
    private readonly Dictionary<string, string> _lastStateLogKeys = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastMissingGraceLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastScanSummaryLogAt = DateTimeOffset.MinValue;
    private string _lastMarkerExitLogKey = string.Empty;

    public AutoSisirService(
        AutoSisirSettingsStore settingsStore,
        StoreSyncService storeSyncService,
        string logPath,
        Func<bool>? isPluginEnabled = null)
    {
        _settingsStore = settingsStore;
        _storeSyncService = storeSyncService;
        _logPath = logPath;
        _isPluginEnabled = isPluginEnabled ?? (() => true);
    }

    public AutoSisirSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            return BuildSnapshot(configuration);
        }
    }

    public AutoSisirSnapshot ToggleSetting(string key)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            switch (key.Trim().ToLowerInvariant())
            {
                case "enabled":
                    configuration.Enabled = !configuration.Enabled;
                    if (configuration.Enabled)
                    {
                        configuration.AutoStartForGamePass = true;
                    }
                    break;
                case "auto-start-game-pass":
                    configuration.AutoStartForGamePass = !configuration.AutoStartForGamePass;
                    break;
                default:
                    throw new InvalidOperationException("Unknown Auto SISR setting.");
            }

            _settingsStore.Save(configuration);
            LogEvent(
                "SETTING",
                $"Toggled {key.Trim()} -> enabled={configuration.Enabled}, autoGamePass={configuration.AutoStartForGamePass}.");
            if (!configuration.Enabled)
            {
                StopMarkerProcess();
                ClearActiveGameState();
                _statusText = "Auto SISR is disabled.";
            }

            return BuildSnapshot(configuration);
        }
    }

    public AutoSisirSnapshot SetExecutablePath(string path)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.ExecutablePath = path.Trim().Trim('"');
            _settingsStore.Save(configuration);
            _statusText = "Auto SISR path saved.";
            LogEvent("SETTING", $"SISR path saved: {ResolveExecutablePath(configuration)}");
            return BuildSnapshot(configuration);
        }
    }

    public AutoSisirSnapshot ResetExecutablePath()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.ExecutablePath = string.Empty;
            _settingsStore.Save(configuration);
            _statusText = "Auto SISR path reset to the default location.";
            LogEvent("SETTING", $"SISR path reset: {ResolveExecutablePath(configuration)}");
            return BuildSnapshot(configuration);
        }
    }

    public AutoSisirSnapshot ToggleWatchedTitle(string titleId)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            var normalizedId = titleId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                throw new InvalidOperationException("A title id is required.");
            }

            var watchedTitleIds = configuration.WatchedTitleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!watchedTitleIds.Add(normalizedId))
            {
                watchedTitleIds.Remove(normalizedId);
            }

            configuration.WatchedTitleIds = watchedTitleIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _settingsStore.Save(configuration);
            _statusText = "Auto SISR watch list updated.";
            LogEvent(
                "WATCHLIST",
                $"Toggled title {normalizedId}. Manual watch count={configuration.WatchedTitleIds.Count}.");
            return BuildSnapshot(configuration);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        LogEvent("SERVICE", "Auto SISR background watcher started.");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _statusText = $"Auto SISR recovered from an error: {exception.Message}";
                    LogEvent("ERROR", $"Tick recovered from {exception.GetType().Name}: {exception.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            LogEvent("SERVICE", "Auto SISR service stop requested.");
            StopMarkerProcess();
            ClearActiveGameState();
            _statusText = "Auto SISR stopped.";
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            if (!_isPluginEnabled())
            {
                LogStateOnce("plugin-disabled", "STATE", "Plugin is globally disabled; marker will be stopped.");
                StopMarkerProcess();
                ClearActiveGameState();
                _statusText = "Auto SISR plugin is disabled.";
                return;
            }

            if (!configuration.Enabled)
            {
                LogStateOnce("integration-disabled", "STATE", "Integration is disabled inside Auto SISR settings.");
                StopMarkerProcess();
                ClearActiveGameState();
                _statusText = "Auto SISR is disabled.";
                return;
            }

            var executablePath = ResolveExecutablePath(configuration);
            if (!File.Exists(executablePath))
            {
                LogStateOnce($"sisr-missing:{executablePath}", "STATE", $"SISR.exe missing at {executablePath}.");
                StopMarkerProcess();
                ClearActiveGameState();
                _statusText = $"SISR.exe was not found at {executablePath}.";
                return;
            }

            var runningGame = FindRunningWatchedTitle(configuration) ?? TryRecoverActiveGameByProcessId();
            if (runningGame is null)
            {
                if (_activeGameProcessId is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (_missingGameSinceUtc is null)
                    {
                        _missingGameSinceUtc = now;
                        LogEvent(
                            "LOST",
                            $"No matching process found for active game '{_activeGameTitle}' pid={_activeGameProcessId} exe='{_activeGameExecutablePath}'. Starting {MissingGameStopGrace.TotalSeconds:0}s grace.");
                    }

                    var remainingSeconds = Math.Max(1, (int)Math.Ceiling((MissingGameStopGrace - (now - _missingGameSinceUtc.Value)).TotalSeconds));
                    if (now - _missingGameSinceUtc.Value < MissingGameStopGrace)
                    {
                        if (now - _lastMissingGraceLogAt > TimeSpan.FromSeconds(5))
                        {
                            _lastMissingGraceLogAt = now;
                            LogEvent("GRACE", $"Waiting {remainingSeconds}s before stopping SISR for '{_activeGameTitle}'.");
                        }

                        _statusText = $"Lost {_activeGameTitle}, waiting {remainingSeconds}s before stopping SISR.";
                        return;
                    }

                    LogEvent("STOP", $"Grace expired for '{_activeGameTitle}'. Stopping marker.");
                    StopMarkerProcess();
                    ClearActiveGameState();
                }

                var watchedCount = GetWatchableTitles(configuration).Count(title => title.Watched);
                LogStateOnce($"watching:{watchedCount}", "STATE", $"No running watched title. watchedCount={watchedCount}.");
                _statusText = watchedCount > 0
                    ? $"Watching {watchedCount} selected non-Steam title{(watchedCount == 1 ? string.Empty : "s")}."
                    : "Auto SISR is enabled, but no titles are selected.";
                return;
            }

            _missingGameSinceUtc = null;
            _lastMissingGraceLogAt = DateTimeOffset.MinValue;
            _activeGameTitle = runningGame.Title;
            _activeGameProcessId = runningGame.ProcessId;
            _activeGameExecutablePath = runningGame.ExecutablePath;
            LogStateOnce(
                $"matched:{runningGame.ProcessId}:{runningGame.ExecutablePath}",
                "MATCH",
                $"Detected '{runningGame.Title}' pid={runningGame.ProcessId} expectedExe='{runningGame.ExecutablePath}' processPath='{runningGame.ProcessPath}' exactPath={runningGame.MatchedByExactPath}.");

            var attemptedLaunch = false;
            if (ShouldLaunchMarkerFor(runningGame))
            {
                attemptedLaunch = true;
                StartMarkerProcess(configuration, executablePath);
            }

            if (IsMarkerProcessAlive())
            {
                _statusText = $"SISR marker is running for {runningGame.Title}.";
            }
            else if (WasMarkerLaunchedFor(runningGame))
            {
                _statusText = $"SISR marker was launched for {runningGame.Title}.";
            }
            else if (!attemptedLaunch)
            {
                _statusText = $"Detected {runningGame.Title}; waiting before retrying SISR.";
            }
        }
    }

    private RunningGame? FindRunningWatchedTitle(AutoSisirConfiguration configuration)
    {
        var titles = GetCachedDetectedTitles()
            .Where(title => ShouldWatchTitle(configuration, title))
            .ToArray();
        LogScanSummary(titles);
        if (titles.Length == 0)
        {
            return null;
        }

        var titlesByExecutableName = titles
            .Where(title => !string.IsNullOrWhiteSpace(title.ExecutablePath))
            .GroupBy(
                title => Path.GetFileName(title.ExecutablePath),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var processExecutableName = $"{process.ProcessName}.exe";
                if (!titlesByExecutableName.TryGetValue(processExecutableName, out var matchingTitles))
                {
                    continue;
                }

                var processPath = TryGetProcessPath(process);
                var exactTitle = matchingTitles.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(processPath) &&
                    string.Equals(
                        NormalizePathForComparison(candidate.ExecutablePath),
                        NormalizePathForComparison(processPath),
                        StringComparison.OrdinalIgnoreCase));
                var title = exactTitle ?? matchingTitles.FirstOrDefault();

                if (title is not null)
                {
                    return new RunningGame(
                        title.Title,
                        process.Id,
                        title.ExecutablePath,
                        processPath ?? string.Empty,
                        exactTitle is not null);
                }
            }
        }

        return null;
    }

    private RunningGame? TryRecoverActiveGameByProcessId()
    {
        if (_activeGameProcessId is null ||
            string.IsNullOrWhiteSpace(_activeGameTitle) ||
            string.IsNullOrWhiteSpace(_activeGameExecutablePath))
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(_activeGameProcessId.Value);
            if (process.HasExited)
            {
                LogStateOnce(
                    $"active-exited:{_activeGameProcessId}",
                    "LOST_DIAGNOSTIC",
                    $"Active game pid={_activeGameProcessId} has exited.");
                return null;
            }

            var processName = TryGetProcessName(process);
            var processPath = TryGetProcessPath(process) ?? string.Empty;
            var expectedProcessName = Path.GetFileNameWithoutExtension(_activeGameExecutablePath);
            var matchesExpectedName = string.Equals(processName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
            var matchesExpectedPath = string.Equals(
                NormalizePathForComparison(processPath),
                NormalizePathForComparison(_activeGameExecutablePath),
                StringComparison.OrdinalIgnoreCase);

            if (matchesExpectedName || matchesExpectedPath)
            {
                LogStateOnce(
                    $"active-recovered:{_activeGameProcessId}:{processName}:{processPath}",
                    "RECOVER",
                    $"Active game pid={_activeGameProcessId} is still alive. processName='{processName}' processPath='{processPath}' nameMatch={matchesExpectedName} pathMatch={matchesExpectedPath}.");
                return new RunningGame(
                    _activeGameTitle,
                    _activeGameProcessId.Value,
                    _activeGameExecutablePath,
                    processPath,
                    matchesExpectedPath);
            }

            LogStateOnce(
                $"active-mismatch:{_activeGameProcessId}:{processName}:{processPath}",
                "LOST_DIAGNOSTIC",
                $"Active pid={_activeGameProcessId} still exists but no longer matches '{_activeGameExecutablePath}'. processName='{processName}' processPath='{processPath}'.");
            return null;
        }
        catch (ArgumentException)
        {
            LogStateOnce(
                $"active-missing:{_activeGameProcessId}",
                "LOST_DIAGNOSTIC",
                $"Active game pid={_activeGameProcessId} no longer exists.");
            return null;
        }
        catch (Exception exception)
        {
            LogStateOnce(
                $"active-error:{_activeGameProcessId}:{exception.GetType().Name}",
                "LOST_DIAGNOSTIC",
                $"Could not inspect active game pid={_activeGameProcessId}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private bool ShouldLaunchMarkerFor(RunningGame runningGame)
    {
        if (WasMarkerLaunchedFor(runningGame))
        {
            var markerState = IsMarkerProcessAlive() ? "alive" : "not alive";
            LogStateOnce(
                $"skip-already:{runningGame.ProcessId}:{runningGame.ExecutablePath}:{markerState}",
                "SKIP",
                $"Marker was already launched for '{runningGame.Title}' pid={runningGame.ProcessId}; markerState={markerState}.");
            return false;
        }

        if (TryReuseLiveMarkerFor(runningGame))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastMarkerLaunchAttemptAt < MarkerLaunchCooldown)
        {
            var remainingSeconds = Math.Max(1, (int)Math.Ceiling((MarkerLaunchCooldown - (now - _lastMarkerLaunchAttemptAt)).TotalSeconds));
            LogStateOnce(
                $"skip-cooldown:{runningGame.ProcessId}:{remainingSeconds}",
                "SKIP",
                $"Launch cooldown active for '{runningGame.Title}' pid={runningGame.ProcessId}; retry in about {remainingSeconds}s.");
            return false;
        }

        return true;
    }

    private bool WasMarkerLaunchedFor(RunningGame runningGame)
    {
        return _markerStartedForProcessId == runningGame.ProcessId &&
            string.Equals(
                NormalizePathForComparison(_markerStartedForExecutablePath),
                NormalizePathForComparison(runningGame.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
    }

    private bool TryReuseLiveMarkerFor(RunningGame runningGame)
    {
        if (!IsMarkerProcessAlive())
        {
            return false;
        }

        var sameTitle = !string.IsNullOrWhiteSpace(_markerStartedForTitle) &&
            string.Equals(_markerStartedForTitle, runningGame.Title, StringComparison.OrdinalIgnoreCase);
        var sameExecutable = !string.IsNullOrWhiteSpace(_markerStartedForExecutablePath) &&
            string.Equals(
                NormalizePathForComparison(_markerStartedForExecutablePath),
                NormalizePathForComparison(runningGame.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);

        if (!sameTitle && !sameExecutable)
        {
            return false;
        }

        var previousProcessId = _markerStartedForProcessId;
        _markerStartedForProcessId = runningGame.ProcessId;
        _markerStartedForTitle = runningGame.Title;
        _markerStartedForExecutablePath = runningGame.ExecutablePath;

        LogStateOnce(
            $"reassociate:{runningGame.Title}:{previousProcessId}:{runningGame.ProcessId}:{runningGame.ExecutablePath}",
            "REASSOCIATE",
            $"Keeping existing SISR marker alive for '{runningGame.Title}' after game pid changed from {previousProcessId?.ToString() ?? "unknown"} to {runningGame.ProcessId}.");
        return true;
    }

    private IReadOnlyList<StoreSyncDetectedTitleState> GetCachedDetectedTitles()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedDetectedTitles.Count > 0 && now - _cachedDetectedTitlesAt < TimeSpan.FromSeconds(30))
        {
            return _cachedDetectedTitles;
        }

        _cachedDetectedTitles = _storeSyncService.GetDetectedTitles();
        _cachedDetectedTitlesAt = now;
        return _cachedDetectedTitles;
    }

    private void StartMarkerProcess(AutoSisirConfiguration configuration, string executablePath)
    {
        _lastMarkerLaunchAttemptAt = DateTimeOffset.UtcNow;
        LogEvent(
            "MARKER_START",
            $"Starting SISR for '{_activeGameTitle}' gamePid={_activeGameProcessId} gameExe='{_activeGameExecutablePath}' command='\"{executablePath}\" {configuration.LaunchArguments}'.");

        try
        {
            PrepareSisirForMarkerStart(executablePath);

            var workingDirectory = Path.GetDirectoryName(executablePath);
            _markerProcess = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = configuration.LaunchArguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    : workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (_markerProcess is not null)
            {
                _markerStartedForProcessId = _activeGameProcessId;
                _markerStartedForTitle = _activeGameTitle;
                _markerStartedForExecutablePath = _activeGameExecutablePath;
                LogEvent(
                    "MARKER_STARTED",
                    $"SISR process started pid={_markerProcess.Id} for gamePid={_markerStartedForProcessId}.");
            }
            else
            {
                _statusText = "SISR marker could not be started.";
                LogEvent("MARKER_START_FAILED", "Process.Start returned null.");
            }
        }
        catch (Exception exception)
        {
            _markerProcess = null;
            _statusText = $"SISR marker could not be started: {exception.Message}";
            LogEvent("MARKER_START_FAILED", $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void PrepareSisirForMarkerStart(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            LogEvent("SISR_PREPARE", $"Cannot derive a process name from '{executablePath}'.");
            return;
        }

        var stoppedAnyProcess = false;
        var hadMarkerReference = _markerProcess is not null && IsMarkerProcessAlive();
        if (_markerProcess is not null)
        {
            LogEvent("SISR_PREPARE", "Clearing the previous TFS-started SISR reference before marker restart.");
            StopMarkerProcess();
            stoppedAnyProcess = hadMarkerReference;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                var processPath = TryGetProcessPath(process) ?? string.Empty;
                if (!ShouldStopSisirProcess(processPath, executablePath))
                {
                    LogEvent(
                        "SISR_PREPARE_SKIP",
                        $"Leaving existing {processName} pid={TryGetProcessId(process)} alone because path='{processPath}' does not match '{executablePath}'.");
                    continue;
                }

                stoppedAnyProcess = StopExistingSisirProcess(process, processPath) || stoppedAnyProcess;
            }
        }

        if (!stoppedAnyProcess)
        {
            LogEvent("SISR_PREPARE", $"No existing {processName} process had to be stopped before marker start.");
            return;
        }

        LogEvent(
            "SISR_RESTART_DELAY",
            $"Waiting {SisirRestartDelay.TotalMilliseconds:0}ms before starting SISR marker mode.");
        Thread.Sleep(SisirRestartDelay);
    }

    private bool StopExistingSisirProcess(Process process, string processPath)
    {
        var processId = TryGetProcessId(process);
        try
        {
            if (process.HasExited)
            {
                LogEvent("SISR_PREPARE", $"Existing SISR pid={processId} already exited.");
                return false;
            }

            var readablePath = string.IsNullOrWhiteSpace(processPath) ? "unknown" : processPath;
            LogEvent("SISR_PREPARE", $"Stopping existing SISR pid={processId} path='{readablePath}'.");

            try
            {
                process.CloseMainWindow();
            }
            catch (Exception exception)
            {
                LogEvent("SISR_PREPARE", $"CloseMainWindow failed for SISR pid={processId}: {exception.Message}");
            }

            if (process.WaitForExit((int)ExistingSisirCloseWait.TotalMilliseconds))
            {
                LogEvent("SISR_PREPARE", $"Existing SISR pid={processId} exited before marker restart.");
                return true;
            }

            LogEvent("SISR_PREPARE", $"Existing SISR pid={processId} did not exit in time; killing process tree.");
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)ExistingSisirCloseWait.TotalMilliseconds);
            return true;
        }
        catch (Exception exception)
        {
            LogEvent("SISR_PREPARE_FAILED", $"Could not stop existing SISR pid={processId}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static bool ShouldStopSisirProcess(string processPath, string executablePath)
    {
        return string.IsNullOrWhiteSpace(processPath) ||
            string.Equals(
                NormalizePathForComparison(processPath),
                NormalizePathForComparison(executablePath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathForComparison(string path)
    {
        var normalized = path.Trim().Trim('"');
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            normalized = normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        return normalized
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private bool IsMarkerProcessAlive()
    {
        try
        {
            if (_markerProcess is null)
            {
                return false;
            }

            if (!_markerProcess.HasExited)
            {
                return true;
            }

            var exitKey = $"{_markerProcess.Id}:{_markerProcess.ExitCode}";
            if (!string.Equals(_lastMarkerExitLogKey, exitKey, StringComparison.OrdinalIgnoreCase))
            {
                _lastMarkerExitLogKey = exitKey;
                LogEvent(
                    "MARKER_EXITED",
                    $"TFS-started SISR pid={_markerProcess.Id} exited with code {_markerProcess.ExitCode}.");
            }

            return false;
        }
        catch (Exception exception)
        {
            LogEvent("MARKER_STATE_ERROR", $"{exception.GetType().Name}: {exception.Message}");
            _markerProcess = null;
            return false;
        }
    }

    private void StopMarkerProcess()
    {
        try
        {
            if (_markerProcess is null)
            {
                return;
            }

            if (!_markerProcess.HasExited)
            {
                LogEvent("MARKER_STOP", $"Stopping TFS-started SISR pid={_markerProcess.Id}.");
                try
                {
                    _markerProcess.CloseMainWindow();
                    if (!_markerProcess.WaitForExit(1200))
                    {
                        LogEvent("MARKER_STOP", $"SISR pid={_markerProcess.Id} did not exit after CloseMainWindow; killing process tree.");
                        _markerProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    try
                    {
                        LogEvent("MARKER_STOP", $"CloseMainWindow failed for SISR pid={_markerProcess.Id}; killing process tree.");
                        _markerProcess.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }
                }
            }
            else
            {
                LogEvent("MARKER_STOP", $"TFS-started SISR pid={_markerProcess.Id} was already exited.");
            }
        }
        finally
        {
            _markerProcess?.Dispose();
            _markerProcess = null;
        }
    }

    private void ClearActiveGameState()
    {
        _activeGameTitle = string.Empty;
        _activeGameProcessId = null;
        _activeGameExecutablePath = string.Empty;
        _missingGameSinceUtc = null;
        _markerStartedForProcessId = null;
        _markerStartedForTitle = string.Empty;
        _markerStartedForExecutablePath = string.Empty;
    }

    private void LogScanSummary(IReadOnlyList<StoreSyncDetectedTitleState> titles)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastScanSummaryLogAt < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastScanSummaryLogAt = now;
        var summary = titles.Count == 0
            ? "No watched candidates."
            : string.Join(
                " | ",
                titles.Take(8).Select(title =>
                    $"{title.Title} [{title.StoreId}] exe='{title.ExecutablePath}'"));
        LogEvent("SCAN", $"Watched candidates={titles.Count}. {summary}");
    }

    private void LogStateOnce(string stateKey, string eventName, string message)
    {
        if (_lastStateLogKeys.TryGetValue(eventName, out var previousKey) &&
            string.Equals(previousKey, stateKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastStateLogKeys[eventName] = stateKey;
        LogEvent(eventName, message);
    }

    private void LogEvent(string eventName, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {eventName} | {message}{Environment.NewLine}";
            File.AppendAllText(_logPath, line);
            TrimLogFileIfNeeded();
        }
        catch
        {
            // Logging must never break the marker watcher.
        }
    }

    private void TrimLogFileIfNeeded()
    {
        try
        {
            var fileInfo = new FileInfo(_logPath);
            if (!fileInfo.Exists || fileInfo.Length <= MaximumLogBytes)
            {
                return;
            }

            var lines = File.ReadAllLines(_logPath)
                .TakeLast(300)
                .ToArray();
            File.WriteAllLines(_logPath, lines);
        }
        catch
        {
        }
    }

    private IReadOnlyList<string> ReadRecentLogLines()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return [];
            }

            return File.ReadAllLines(_logPath)
                .TakeLast(MaximumRecentLogLines)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private AutoSisirSnapshot BuildSnapshot(AutoSisirConfiguration configuration)
    {
        var executablePath = ResolveExecutablePath(configuration);
        var defaultPath = AutoSisirSettingsStore.GetDefaultExecutablePath();
        var statusText = ResolveSnapshotStatusText(configuration);
        return new AutoSisirSnapshot(
            new AutoSisirSettingsState(
                configuration.Enabled,
                configuration.AutoStartForGamePass,
                executablePath,
                defaultPath,
                configuration.LaunchArguments,
                string.IsNullOrWhiteSpace(configuration.ExecutablePath)),
            statusText,
            File.Exists(executablePath),
            IsMarkerProcessAlive(),
            _activeGameTitle,
            _activeGameProcessId,
            _logPath,
            ReadRecentLogLines(),
            GetWatchableTitles(configuration));
    }

    private string ResolveSnapshotStatusText(AutoSisirConfiguration configuration)
    {
        if (!_isPluginEnabled())
        {
            return "Auto SISR plugin is disabled.";
        }

        if (!configuration.Enabled)
        {
            return "Auto SISR is disabled.";
        }

        return string.Equals(_statusText, "Auto SISR plugin is disabled.", StringComparison.OrdinalIgnoreCase)
            ? "Auto SISR is waiting for the next scan."
            : _statusText;
    }

    private IReadOnlyList<AutoSisirWatchTitleState> GetWatchableTitles(AutoSisirConfiguration configuration)
    {
        return GetCachedDetectedTitles()
            .Select(title =>
            {
                var selected = configuration.WatchedTitleIds.Contains(title.Id, StringComparer.OrdinalIgnoreCase);
                var automatic = IsAutomaticGamePassTitle(configuration, title);
                return new AutoSisirWatchTitleState(
                    title.Id,
                    title.StoreId,
                    title.StoreTitle,
                    title.Title,
                    title.ExecutablePath,
                    selected,
                    automatic,
                    selected || automatic);
            })
            .OrderByDescending(title => title.Watched)
            .ThenBy(title => title.StoreTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(title => title.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldWatchTitle(AutoSisirConfiguration configuration, StoreSyncDetectedTitleState title)
    {
        return IsAutomaticGamePassTitle(configuration, title) ||
            configuration.WatchedTitleIds.Contains(title.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAutomaticGamePassTitle(AutoSisirConfiguration configuration, StoreSyncDetectedTitleState title)
    {
        return configuration.AutoStartForGamePass &&
            string.Equals(title.StoreId, "xbox-game-pass", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExecutablePath(AutoSisirConfiguration configuration)
    {
        return string.IsNullOrWhiteSpace(configuration.ExecutablePath)
            ? AutoSisirSettingsStore.GetDefaultExecutablePath()
            : configuration.ExecutablePath.Trim().Trim('"');
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record RunningGame(
        string Title,
        int ProcessId,
        string ExecutablePath,
        string ProcessPath,
        bool MatchedByExactPath);
}
