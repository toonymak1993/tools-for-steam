using System.Text.Json;

namespace SteamLoader.App.Services;

internal sealed class SteamStartupHistoryStore
{
    private const string HistoryMutexName = @"Local\ToolsForSteam.SteamStartupHistory";
    private const int MaximumEntries = 24;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _historyPath;

    public SteamStartupHistoryStore(string? historyPath = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "steam-startup-history.json");
    }

    public SteamStartupTimingPolicy GetTimingPolicy(bool isHandheld)
    {
        var defaultGrace = isHandheld ? TimeSpan.FromSeconds(150) : TimeSpan.FromSeconds(30);
        var successes = ReadEntries()
            .Where(entry => entry.Outcome == SteamStartupOutcome.Ready && entry.DurationSeconds >= 3)
            .OrderBy(entry => entry.DurationSeconds)
            .Select(entry => TimeSpan.FromSeconds(entry.DurationSeconds))
            .ToArray();

        if (successes.Length < 3)
        {
            return SteamStartupTimingPolicy.FromGracePeriod(defaultGrace, isAdaptive: false);
        }

        var percentileIndex = (int)Math.Ceiling((successes.Length - 1) * 0.8);
        var observedP80 = successes[Math.Clamp(percentileIndex, 0, successes.Length - 1)];
        var adaptiveSeconds = observedP80.TotalSeconds * 1.6 + 8;
        var minimum = isHandheld ? 75 : 25;
        var maximum = isHandheld ? 210 : 90;
        var grace = TimeSpan.FromSeconds(Math.Clamp(adaptiveSeconds, minimum, maximum));
        return SteamStartupTimingPolicy.FromGracePeriod(grace, isAdaptive: true);
    }

    public void Record(
        DateTimeOffset startedAtUtc,
        SteamStartupOutcome outcome,
        bool recoveryUsed,
        string detail)
    {
        var now = DateTimeOffset.UtcNow;
        var duration = Math.Max(0, (now - startedAtUtc).TotalSeconds);
        WithHistoryLock(() =>
        {
            var data = ReadDataWithoutLock();
            data.Entries.Add(new SteamStartupHistoryEntry
            {
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = now,
                DurationSeconds = Math.Round(duration, 1),
                Outcome = outcome,
                RecoveryUsed = recoveryUsed,
                Detail = detail
            });
            data.Entries = data.Entries
                .OrderByDescending(entry => entry.CompletedAtUtc)
                .Take(MaximumEntries)
                .OrderBy(entry => entry.CompletedAtUtc)
                .ToList();
            WriteDataWithoutLock(data);
            return true;
        });
    }

    private IReadOnlyList<SteamStartupHistoryEntry> ReadEntries() =>
        WithHistoryLock(() => (IReadOnlyList<SteamStartupHistoryEntry>)ReadDataWithoutLock().Entries.ToArray()) ?? [];

    private T? WithHistoryLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, HistoryMutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            return ownsMutex ? action() : default;
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"Steam startup history operation failed: {exception.Message}");
            return default;
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private SteamStartupHistoryData ReadDataWithoutLock()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return new SteamStartupHistoryData();
            }

            return JsonSerializer.Deserialize<SteamStartupHistoryData>(
                File.ReadAllText(_historyPath),
                JsonOptions) ?? new SteamStartupHistoryData();
        }
        catch
        {
            return new SteamStartupHistoryData();
        }
    }

    private void WriteDataWithoutLock(SteamStartupHistoryData data)
    {
        var directory = Path.GetDirectoryName(_historyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _historyPath + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporaryPath, _historyPath, overwrite: true);
    }

    private sealed class SteamStartupHistoryData
    {
        public List<SteamStartupHistoryEntry> Entries { get; set; } = [];
    }

    private sealed class SteamStartupHistoryEntry
    {
        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset CompletedAtUtc { get; set; }

        public double DurationSeconds { get; set; }

        public SteamStartupOutcome Outcome { get; set; }

        public bool RecoveryUsed { get; set; }

        public string Detail { get; set; } = string.Empty;
    }
}

internal sealed record SteamStartupTimingPolicy(
    TimeSpan StartupGracePeriod,
    TimeSpan ShowRecoveryActionsAfter,
    TimeSpan SplashSafetyTimeout,
    bool IsAdaptive)
{
    public static SteamStartupTimingPolicy FromGracePeriod(TimeSpan gracePeriod, bool isAdaptive)
    {
        var actionsAfter = TimeSpan.FromSeconds(Math.Clamp(
            gracePeriod.TotalSeconds * 0.65,
            20,
            90));
        var safetyTimeout = TimeSpan.FromSeconds(Math.Clamp(
            gracePeriod.TotalSeconds * 2.2 + 60,
            180,
            600));
        return new SteamStartupTimingPolicy(
            gracePeriod,
            actionsAfter,
            safetyTimeout,
            isAdaptive);
    }
}

internal enum SteamStartupOutcome
{
    Ready,
    ProtectedActivity,
    Failed,
    DesktopFallback
}
