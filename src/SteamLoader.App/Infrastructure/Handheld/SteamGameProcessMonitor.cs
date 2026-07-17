using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class SteamGameProcessMonitor
{
    private const int InitialTailBytes = 2 * 1024 * 1024;
    private static readonly Regex AddingProcessPattern = new(
        @"AppID (?<appId>\d+) adding PID (?<pid>\d+) as a tracked process",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RemovingProcessPattern = new(
        @"AppID (?<appId>\d+) no longer tracking PID (?<pid>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RemovingAppPattern = new(
        @"Remove (?<appId>\d+) from running list",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExecutablePattern = new(
        "(?<path>[A-Za-z]:\\\\[^\"\\r\\n]+?\\.exe)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _steamRootPath;
    private readonly string _logPath;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Dictionary<string, TrackedApp> _apps = new(StringComparer.OrdinalIgnoreCase);
    private long _position;
    private long _sequence;

    public SteamGameProcessMonitor(string steamRootPath)
        : this(
            steamRootPath,
            Path.Combine(steamRootPath, "logs", "gameprocess_log.txt"),
            IsProcessAlive)
    {
    }

    internal SteamGameProcessMonitor(string steamRootPath, string logPath, Func<int, bool> isProcessAlive)
    {
        _steamRootPath = steamRootPath;
        _logPath = logPath;
        _isProcessAlive = isProcessAlive;
    }

    public HandheldRunningGame? Poll()
    {
        RefreshTrackedApps();

        var current = _apps.Values
            .Where(app => app.ProcessIds.Count > 0)
            .OrderByDescending(app => app.Sequence)
            .FirstOrDefault();
        if (current is null)
        {
            return null;
        }

        return BuildRunningGame(current, current.ProcessIds.LastOrDefault());
    }

    internal HandheldRunningGame? PollForProcess(int processId)
    {
        RefreshTrackedApps();
        var current = _apps.Values.FirstOrDefault(app => app.ProcessIds.Contains(processId));
        return current is null ? null : BuildRunningGame(current, processId);
    }

    internal bool IsAppRunning(string key)
    {
        RefreshTrackedApps();
        return _apps.Values.Any(app =>
            app.ProcessIds.Count > 0 &&
            string.Equals(BuildAppKey(app), key, StringComparison.OrdinalIgnoreCase));
    }

    internal bool IsProcessTrackedByApp(string key, int processId)
    {
        RefreshTrackedApps();
        return _apps.Values.Any(app =>
            app.ProcessIds.Contains(processId) &&
            string.Equals(BuildAppKey(app), key, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshTrackedApps()
    {
        ReadNewLines();
        foreach (var app in _apps.Values)
        {
            app.ProcessIds.RemoveWhere(processId => !_isProcessAlive(processId));
        }
    }

    private HandheldRunningGame BuildRunningGame(TrackedApp current, int processId)
    {
        var executablePath = current.ExecutablePath;
        var title = ResolveAppTitle(current.AppId, executablePath);
        return new HandheldRunningGame(BuildAppKey(current), current.AppId, title, executablePath, processId);
    }

    private static string BuildAppKey(TrackedApp app) =>
        !string.IsNullOrWhiteSpace(app.AppId)
            ? $"steam:{app.AppId}"
            : $"exe:{app.ExecutablePath.ToLowerInvariant()}";

    private void ReadNewLines()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return;
            }

            using var stream = new FileStream(
                _logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (_position > stream.Length)
            {
                _position = 0;
                _apps.Clear();
            }

            var discardPartialLine = false;
            if (_position == 0 && stream.Length > InitialTailBytes)
            {
                stream.Position = stream.Length - InitialTailBytes;
                discardPartialLine = true;
            }
            else
            {
                stream.Position = _position;
            }

            using var reader = new StreamReader(stream, leaveOpen: true);
            if (discardPartialLine)
            {
                _ = reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
            {
                ProcessLine(line);
            }

            _position = stream.Position;
        }
        catch
        {
        }
    }

    private void ProcessLine(string line)
    {
        var adding = AddingProcessPattern.Match(line);
        if (adding.Success && int.TryParse(adding.Groups["pid"].Value, out var processId))
        {
            var appId = adding.Groups["appId"].Value;
            if (!_apps.TryGetValue(appId, out var app))
            {
                app = new TrackedApp(appId);
                _apps[appId] = app;
            }

            app.ProcessIds.Add(processId);
            app.Sequence = ++_sequence;
            var executable = ExecutablePattern.Match(line).Groups["path"].Value;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                app.ExecutablePath = executable;
            }

            return;
        }

        var removingProcess = RemovingProcessPattern.Match(line);
        if (removingProcess.Success &&
            int.TryParse(removingProcess.Groups["pid"].Value, out processId) &&
            _apps.TryGetValue(removingProcess.Groups["appId"].Value, out var trackedApp))
        {
            trackedApp.ProcessIds.Remove(processId);
            return;
        }

        var removingApp = RemovingAppPattern.Match(line);
        if (removingApp.Success)
        {
            _apps.Remove(removingApp.Groups["appId"].Value);
        }
    }

    private string ResolveAppTitle(string appId, string executablePath)
    {
        try
        {
            foreach (var steamAppsPath in EnumerateSteamAppsPaths())
            {
                var manifestPath = Path.Combine(steamAppsPath, $"appmanifest_{appId}.acf");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                foreach (var line in File.ReadLines(manifestPath))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"name\"", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var values = Regex.Matches(trimmed, "\"(?<value>[^\"]*)\"");
                    if (values.Count >= 2 && !string.IsNullOrWhiteSpace(values[1].Groups["value"].Value))
                    {
                        return values[1].Groups["value"].Value;
                    }
                }
            }
        }
        catch
        {
        }

        var executableTitle = Path.GetFileNameWithoutExtension(executablePath);
        return string.IsNullOrWhiteSpace(executableTitle) ? $"Steam app {appId}" : executableTitle;
    }

    private IEnumerable<string> EnumerateSteamAppsPaths()
    {
        yield return Path.Combine(_steamRootPath, "steamapps");
        var libraryFoldersPath = Path.Combine(_steamRootPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(libraryFoldersPath))
        {
            var match = Regex.Match(line, "\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                yield return Path.Combine(match.Groups["path"].Value.Replace("\\\\", "\\"), "steamapps");
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TrackedApp(string appId)
    {
        public string AppId { get; } = appId;
        public HashSet<int> ProcessIds { get; } = [];
        public string ExecutablePath { get; set; } = string.Empty;
        public long Sequence { get; set; }
    }
}
