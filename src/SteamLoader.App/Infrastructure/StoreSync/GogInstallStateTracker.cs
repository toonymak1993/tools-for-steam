using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record GogInstallProbe(
    bool Conclusive,
    bool Installed,
    string InstallPath,
    string ExecutablePath,
    string BuildId);

/// <summary>
/// Cheap, product-ID based GOG installation reconciliation. It probes only
/// cached installed games and games with an active operation, never the remote
/// catalog or artwork cache.
/// </summary>
internal static class GogInstallStateTracker
{
    private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInstalledProbeInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RegistrySnapshotLifetime = TimeSpan.FromSeconds(4);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset AtUtc, GogInstallProbe Probe)>
        ProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RegistryGate = new();
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> RegistrySnapshot =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset RegistrySnapshotAtUtc = DateTimeOffset.MinValue;
    private static readonly Dictionary<string, (int Count, DateTimeOffset FirstAtUtc)>
        MissingObservations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MissingGate = new();

    public static GogInstallProbe Probe(
        string gameId,
        string? configuredInstallRoot,
        string? cachedInstallPath,
        bool force = false)
    {
        if (!IsSafeGameId(gameId))
        {
            return new GogInstallProbe(false, false, string.Empty, string.Empty, string.Empty);
        }

        var cacheKey = string.Join(
            '\n',
            gameId.Trim().ToLowerInvariant(),
            NormalizePath(configuredInstallRoot),
            NormalizePath(cachedInstallPath));
        var now = DateTimeOffset.UtcNow;
        if (!force &&
            ProbeCache.TryGetValue(cacheKey, out var cached) &&
            now - cached.AtUtc < ProbeCacheLifetime)
        {
            return cached.Probe;
        }

        var transaction = GogOperationJournal.Get(gameId);
        var candidateRoots = new List<string>();
        AddCandidateRoot(candidateRoots, transaction?.InstallRoot);
        AddCandidateRoot(candidateRoots, cachedInstallPath);
        if (!string.IsNullOrWhiteSpace(configuredInstallRoot))
        {
            try
            {
                AddCandidateRoot(
                    candidateRoots,
                    Path.Combine(Path.GetFullPath(configuredInstallRoot), gameId));
            }
            catch
            {
            }
        }

        foreach (var registryRoot in GetRegistryInstallRoots(gameId))
        {
            AddCandidateRoot(candidateRoots, registryRoot);
        }

        var sawExistingDirectory = false;
        foreach (var root in candidateRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            sawExistingDirectory = true;
            var manifestPath = Path.Combine(root, $"goggame-{gameId}.info");
            var executablePath = ResolveExecutable(root, gameId);
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
            {
                continue;
            }

            var result = new GogInstallProbe(
                true,
                true,
                root,
                executablePath,
                ReadBuildId(gameId, manifestPath));
            ProbeCache[cacheKey] = (now, result);
            return result;
        }

        // An existing TFS transaction directory without a launchable manifest
        // is a partial installation, not evidence of an uninstall.
        var conclusive =
            !sawExistingDirectory ||
            transaction is null ||
            transaction.Phase is GogOperationPhases.Canceled or GogOperationPhases.Ready;
        var missing = new GogInstallProbe(
            conclusive,
            false,
            string.Empty,
            string.Empty,
            string.Empty);
        ProbeCache[cacheKey] = (now, missing);
        return missing;
    }

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = new StoreSyncSettingsStore(
            Path.Combine(AppContext.BaseDirectory, "data", "store-sync.json"));
        var lastIdleProbeAtUtc = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var configuration = settings.Load();
                if (!configuration.UnifySteam.Stores.TryGetValue(
                        "gog-galaxy",
                        out var store) ||
                    store?.Cache?.Games is null)
                {
                    continue;
                }

                var statuses = UnifySteamDownloadStatusStore.GetAll();
                var transactions = GogOperationJournal.GetAll()
                    .ToDictionary(
                        transaction => transaction.GameId,
                        StringComparer.OrdinalIgnoreCase);
                var now = DateTimeOffset.UtcNow;
                var includeIdleInstalled =
                    now - lastIdleProbeAtUtc >= IdleInstalledProbeInterval;
                if (includeIdleInstalled)
                {
                    lastIdleProbeAtUtc = now;
                }
                IReadOnlySet<string> registeredGameIds = includeIdleInstalled
                    ? GetRegistrySnapshot(force: true).Keys.ToHashSet(
                        StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var changed = new Dictionary<string, GogInstallProbe>(
                    StringComparer.OrdinalIgnoreCase);
                var cleared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var game in store.Cache.Games.Where(game =>
                             game is not null &&
                             !string.IsNullOrWhiteSpace(game.Id)))
                {
                    var status = UnifySteamDownloadStatusStore.Get(
                        statuses,
                        "gog-galaxy",
                        game.Id);
                    var operationRelevant =
                        transactions.ContainsKey(game.Id) ||
                        status.Status is
                            "action-required" or
                            "uninstall-action-required" or
                            "uninstalling" or
                            "finalizing" or
                            "failed";
                    if (!operationRelevant &&
                        !(
                            includeIdleInstalled &&
                            (
                                game.Installed ||
                                registeredGameIds.Contains(game.Id)
                            )
                        ))
                    {
                        continue;
                    }

                    var probe = Probe(
                        game.Id,
                        store.InstallPath,
                        game.InstallPath,
                        force: operationRelevant);
                    if (probe.Installed)
                    {
                        ResetMissing(game.Id);
                        if (!game.Installed ||
                            !string.Equals(
                                game.InstallPath,
                                probe.InstallPath,
                                StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(
                                game.ExecutablePath,
                                probe.ExecutablePath,
                                StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(probe.BuildId) &&
                             !string.Equals(
                                 game.Version,
                                 probe.BuildId,
                                 StringComparison.OrdinalIgnoreCase)))
                        {
                            changed[game.Id] = probe;
                        }

                        if (status.Status == "action-required" ||
                            transactions.TryGetValue(game.Id, out var installTransaction) &&
                            installTransaction.IsInstall &&
                            installTransaction.Phase == GogOperationPhases.Ready)
                        {
                            UnifySteamDownloadStatusStore.Update(
                                "gog-galaxy",
                                game.Id,
                                "completed",
                                100,
                                "Installed.",
                                workerProcessId: 0,
                                downloadedBytes: Math.Max(
                                    status.DownloadedBytes,
                                    status.TotalBytes),
                                totalBytes: status.TotalBytes,
                                gameTitle: game.Title,
                                steamAppId: game.SteamAppId);
                            GogOperationJournal.Clear(game.Id);
                        }
                        continue;
                    }

                    if (!probe.Conclusive ||
                        UnifySteamDownloadStatusStore.IsActivelyTransferring(status.Status) ||
                        status.Status == "paused")
                    {
                        ResetMissing(game.Id);
                        continue;
                    }

                    var uninstallPending =
                        status.Status is "uninstall-action-required" or "uninstalling" ||
                        transactions.TryGetValue(game.Id, out var uninstallTransaction) &&
                        uninstallTransaction.IsUninstall;
                    if ((game.Installed || uninstallPending) &&
                        ConfirmMissing(game.Id, now))
                    {
                        cleared.Add(game.Id);
                        UnifySteamDownloadStatusStore.Clear("gog-galaxy", game.Id);
                        GogOperationJournal.Clear(game.Id);
                    }
                }

                if (changed.Count == 0 && cleared.Count == 0)
                {
                    continue;
                }

                settings.Update(latest =>
                {
                    if (!latest.UnifySteam.Stores.TryGetValue(
                            "gog-galaxy",
                            out var latestStore) ||
                        latestStore?.Cache?.Games is null)
                    {
                        return;
                    }

                    foreach (var game in latestStore.Cache.Games.Where(game =>
                                 game is not null &&
                                 !string.IsNullOrWhiteSpace(game.Id)))
                    {
                        if (changed.TryGetValue(game.Id, out var installed))
                        {
                            game.Installed = true;
                            game.InstallPath = installed.InstallPath;
                            game.ExecutablePath = installed.ExecutablePath;
                            if (!string.IsNullOrWhiteSpace(installed.BuildId))
                            {
                                game.Version = installed.BuildId;
                            }
                        }
                        else if (cleared.Contains(game.Id))
                        {
                            game.Installed = false;
                            game.InstallPath = string.Empty;
                            game.ExecutablePath = string.Empty;
                            game.Version = string.Empty;
                        }
                    }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // The next two-second pass retries only the affected local
                // probes. No remote catalog or artwork operation is involved.
            }
        }
    }

    private static string ResolveExecutable(string installRoot, string gameId)
    {
        try
        {
            var task = UnifySteamLauncher.ResolveGogLaunchTask(installRoot, gameId);
            if (!string.IsNullOrWhiteSpace(task?.ExecutablePath) &&
                File.Exists(task.ExecutablePath))
            {
                return Path.GetFullPath(task.ExecutablePath);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ReadBuildId(string gameId, string manifestPath)
    {
        try
        {
            var helperManifest = ManagedGogDlHelper.GetInstalledManifestPath(gameId);
            foreach (var path in new[] { helperManifest, manifestPath })
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                foreach (var propertyName in new[]
                         {
                             "buildId",
                             "build_id",
                             "version",
                         })
                {
                    if (!root.TryGetProperty(propertyName, out var value))
                    {
                        continue;
                    }

                    var text = value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> GetRegistryInstallRoots(string gameId)
    {
        return GetRegistrySnapshot(force: false).TryGetValue(gameId, out var roots)
            ? roots
            : [];
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> GetRegistrySnapshot(
        bool force)
    {
        lock (RegistryGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && now - RegistrySnapshotAtUtc < RegistrySnapshotLifetime)
            {
                return RegistrySnapshot;
            }

            RegistrySnapshot = ReadRegistrySnapshot();
            RegistrySnapshotAtUtc = now;
            return RegistrySnapshot;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadRegistrySnapshot()
    {
        var results = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[]
                 {
                     RegistryHive.CurrentUser,
                     RegistryHive.LocalMachine,
                 })
        {
            foreach (var view in new[]
                     {
                         RegistryView.Registry64,
                         RegistryView.Registry32,
                     })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var gamesKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                    if (gamesKey is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in gamesKey.GetSubKeyNames())
                    {
                        using var gameKey = gamesKey.OpenSubKey(subKeyName);
                        if (gameKey is null)
                        {
                            continue;
                        }

                        var candidateId = FirstNonEmpty(
                            gameKey.GetValue("gameID")?.ToString(),
                            gameKey.GetValue("gameId")?.ToString(),
                            gameKey.GetValue("productID")?.ToString(),
                            gameKey.GetValue("productId")?.ToString(),
                            subKeyName);
                        if (!IsSafeGameId(candidateId))
                        {
                            continue;
                        }

                        var root = FirstNonEmpty(
                            gameKey.GetValue("path")?.ToString(),
                            gameKey.GetValue("PATH")?.ToString(),
                            gameKey.GetValue("InstallLocation")?.ToString());
                        if (!string.IsNullOrWhiteSpace(root))
                        {
                            if (!results.TryGetValue(candidateId, out var roots))
                            {
                                roots = [];
                                results[candidateId] = roots;
                            }

                            AddCandidateRoot(roots, root);
                        }
                    }
                }
                catch
                {
                    // Registry views can be inaccessible on locked-down
                    // machines. Other exact-ID probes remain authoritative.
                }
            }
        }

        return results.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool ConfirmMissing(string gameId, DateTimeOffset now)
    {
        lock (MissingGate)
        {
            if (!MissingObservations.TryGetValue(gameId, out var observation))
            {
                MissingObservations[gameId] = (1, now);
                return false;
            }

            observation = (observation.Count + 1, observation.FirstAtUtc);
            if (observation.Count >= 2 &&
                now - observation.FirstAtUtc >= TimeSpan.FromSeconds(1))
            {
                MissingObservations.Remove(gameId);
                return true;
            }

            MissingObservations[gameId] = observation;
            return false;
        }
    }

    private static void ResetMissing(string gameId)
    {
        lock (MissingGate)
        {
            MissingObservations.Remove(gameId);
        }
    }

    private static void AddCandidateRoot(ICollection<string> roots, string? path)
    {
        var normalized = NormalizePath(path);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(normalized);
        }
    }

    private static string NormalizePath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim() ?? string.Empty;
    }

    private static bool IsSafeGameId(string? gameId)
    {
        return !string.IsNullOrWhiteSpace(gameId) &&
               gameId.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
    }
}
