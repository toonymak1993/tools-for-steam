using SteamLoader.App.Infrastructure.StoreSync;

namespace SteamLoader.App.Infrastructure.Performance;

internal sealed class StoreSyncGameProcessMonitor
{
    private readonly StoreSyncSettingsStore _settingsStore;

    public StoreSyncGameProcessMonitor(StoreSyncSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public StoreSyncGameSession? TryMatch(ForegroundTargetCandidate target)
    {
        var normalizedExecutablePath = NormalizePath(target.ExecutablePath);
        if (string.IsNullOrWhiteSpace(normalizedExecutablePath) && string.IsNullOrWhiteSpace(target.ProcessName))
        {
            return null;
        }

        var candidates = _settingsStore.Load().Manifest.Values
            .Where(entry =>
                entry is not null &&
                !string.IsNullOrWhiteSpace(entry.TitleId) &&
                (entry.ManagedShortcut || entry.AdoptedExistingShortcut))
            .Select(entry => new
            {
                Entry = entry,
                ExecutablePath = NormalizePath(entry.ExecutablePath),
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            .Select(candidate => new
            {
                candidate.Entry,
                candidate.ExecutablePath,
                MatchScore = GetMatchScore(
                    normalizedExecutablePath,
                    candidate.ExecutablePath,
                    target.ProcessName),
            })
            .Where(candidate => candidate.MatchScore < int.MaxValue)
            .OrderBy(candidate => candidate.MatchScore)
            .ThenByDescending(candidate => candidate.Entry.LastSeenAtUtc)
            .ToArray();

        var match = candidates.FirstOrDefault();
        if (match is null ||
            (match.MatchScore >= 10 && candidates.Count(candidate => candidate.MatchScore == match.MatchScore) != 1))
        {
            return null;
        }

        return new StoreSyncGameSession(
            $"store-sync:{match.Entry.TitleId}",
            match.Entry.StoreId,
            FirstNonEmpty(match.Entry.EffectiveTitle, match.Entry.Title, match.Entry.TitleId),
            match.ExecutablePath,
            target.ProcessId);
    }

    public bool IsProcessForGame(string gameKey, ForegroundTargetCandidate target) =>
        string.Equals(TryMatch(target)?.Key, gameKey, StringComparison.OrdinalIgnoreCase);

    private static int GetMatchScore(
        string runningExecutablePath,
        string managedExecutablePath,
        string processName)
    {
        if (!string.IsNullOrWhiteSpace(runningExecutablePath) &&
            string.Equals(runningExecutablePath, managedExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(runningExecutablePath))
        {
            try
            {
                var managedDirectory = Path.GetDirectoryName(managedExecutablePath);
                if (!string.IsNullOrWhiteSpace(managedDirectory))
                {
                    var normalizedDirectory = Path.GetFullPath(managedDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (Path.GetFullPath(runningExecutablePath)
                        .StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return 1;
                    }
                }
            }
            catch
            {
            }
        }

        var managedProcessName = Path.GetFileNameWithoutExtension(managedExecutablePath);
        return !string.IsNullOrWhiteSpace(processName) &&
               string.Equals(processName.Trim(), managedProcessName, StringComparison.OrdinalIgnoreCase)
            ? 10
            : int.MaxValue;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

internal sealed record StoreSyncGameSession(
    string Key,
    string StoreId,
    string Title,
    string ExecutablePath,
    int ProcessId);
