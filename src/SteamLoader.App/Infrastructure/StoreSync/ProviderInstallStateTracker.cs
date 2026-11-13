namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// One reconciliation pass for providers whose local installation state is a
/// filesystem/registry scan. Cadence and provider serialization are owned by
/// <see cref="OmniLibraryInstallStateCoordinator"/> so this code never starts
/// an independent polling loop or reloads the full settings document itself.
/// </summary>
internal static class ProviderInstallStateTracker
{
    private static readonly Dictionary<string, (int Count, DateTimeOffset FirstAtUtc)>
        MissingObservations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MissingGate = new();

    /// <returns>
    /// True when a game was seen missing once and needs a confirming pass
    /// before it is marked uninstalled.
    /// </returns>
    public static bool Reconcile(
        string storeId,
        UnifySteamStoreConfiguration store,
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> statuses,
        Func<UnifySteamStoreConfiguration, Dictionary<string, UnifySteamGameCacheEntry>> loadInstalled,
        StoreSyncSettingsStore settings,
        Action<string, IReadOnlyCollection<string>>? onInstalledStateChanged,
        bool includeIdle)
    {
        if (!store.Enabled || store.Cache?.Games is null)
        {
            return false;
        }

        var games = store.Cache.Games
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .ToArray();
        if (!includeIdle && !games.Any(game =>
                IsOperationRelevant(
                    UnifySteamDownloadStatusStore.Get(statuses, storeId, game.Id).Status)))
        {
            return false;
        }

        // A loader throws when a launcher's install list exists but cannot be
        // read (for example while the launcher rewrites it). The pass then
        // fails as a whole and nothing is marked uninstalled.
        var installed = loadInstalled(store);
        var now = DateTimeOffset.UtcNow;
        var changed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var cleared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var confirmationPending = false;

        foreach (var game in games)
        {
            var status = UnifySteamDownloadStatusStore.Get(statuses, storeId, game.Id);
            if (!ShouldScheduleGameProbe(status.Status, includeIdle))
            {
                continue;
            }

            if (UnifySteamDownloadStatusStore.IsActivelyTransferring(status.Status) ||
                status.Status == "paused")
            {
                ResetMissing(storeId, game.Id);
                continue;
            }

            installed.TryGetValue(game.Id, out var installedGame);
            if (installedGame?.Installed == true)
            {
                ResetMissing(storeId, game.Id);
                if (!game.Installed ||
                    !string.Equals(game.InstallPath, installedGame.InstallPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(game.ExecutablePath, installedGame.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(installedGame.Version) &&
                     !string.Equals(game.Version, installedGame.Version, StringComparison.OrdinalIgnoreCase)))
                {
                    changed[game.Id] = installedGame;
                }

                if (status.Status is "action-required" or "failed")
                {
                    UnifySteamDownloadStatusStore.Update(
                        storeId,
                        game.Id,
                        "completed",
                        100,
                        "Installed.",
                        workerProcessId: 0,
                        gameTitle: game.Title,
                        steamAppId: game.SteamAppId);
                }

                continue;
            }

            if (!game.Installed)
            {
                continue;
            }

            // A game on a drive that is currently absent (SD card, USB drive,
            // network share) is not uninstalled; keep it until the drive is
            // back or its folder is gone from a present drive.
            if (IsOnUnavailableVolume(game.InstallPath))
            {
                ResetMissing(storeId, game.Id);
                continue;
            }

            if (ConfirmMissing(storeId, game.Id, now))
            {
                cleared.Add(game.Id);
                // The local provider is authoritative. Once removal is
                // confirmed, no old completed/failed/handoff state may keep
                // the game looking installed or busy in Steam.
                UnifySteamDownloadStatusStore.Clear(storeId, game.Id);
            }
            else
            {
                confirmationPending = true;
            }
        }

        if (changed.Count == 0 && cleared.Count == 0)
        {
            return confirmationPending;
        }

        settings.Update(latest =>
        {
            if (!latest.UnifySteam.Stores.TryGetValue(storeId, out var latestStore) ||
                latestStore?.Cache?.Games is null)
            {
                return;
            }

            foreach (var game in latestStore.Cache.Games.Where(game =>
                         game is not null && !string.IsNullOrWhiteSpace(game.Id)))
            {
                if (changed.TryGetValue(game.Id, out var installedGame))
                {
                    game.Installed = true;
                    game.InstallPath = installedGame.InstallPath;
                    game.ExecutablePath = installedGame.ExecutablePath;
                    if (!string.IsNullOrWhiteSpace(installedGame.Id))
                    {
                        game.ProviderGameId = installedGame.Id;
                    }
                    if (!string.IsNullOrWhiteSpace(installedGame.Version))
                    {
                        game.Version = installedGame.Version;
                    }
                }
                else if (cleared.Contains(game.Id))
                {
                    game.Installed = false;
                    game.InstallPath = string.Empty;
                    game.ExecutablePath = string.Empty;
                }
            }
        });

        try
        {
            onInstalledStateChanged?.Invoke(
                storeId,
                changed.Keys
                    .Concat(cleared)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }
        catch
        {
            // The local state is already persisted. A later startup or
            // periodic reconciliation can retry the optional shortcut sync.
        }

        return confirmationPending;
    }

    /// <summary>
    /// True when the path lives on a drive that is not present right now.
    /// Such a game cannot be judged installed or uninstalled.
    /// </summary>
    internal static bool IsOnUnavailableVolume(string? path)
    {
        var root = TryGetVolumeRoot(path);
        return !string.IsNullOrWhiteSpace(root) && !Directory.Exists(root);
    }

    /// <summary>
    /// True only when the folder is gone from a drive that is present: the
    /// signature of a game folder deleted outside its launcher.
    /// </summary>
    internal static bool IsMissingFromAvailableVolume(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return !Directory.Exists(path.Trim()) && !IsOnUnavailableVolume(path);
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetVolumeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var trimmed = path.Trim().Trim('"');
            return Path.IsPathFullyQualified(trimmed)
                ? Path.GetPathRoot(Path.GetFullPath(trimmed)) ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static bool IsOperationRelevant(string status)
    {
        return status is
            "action-required" or
            "uninstall-action-required" or
            "uninstall-failed" or
            "uninstalling" or
            "finalizing" or
            "failed";
    }

    /// <summary>
    /// An idle reconciliation is deliberately provider-wide. Restricting it
    /// to entries already marked Installed creates a blind spot for games
    /// installed directly in Xbox, Epic, EA, Ubisoft, or another publisher
    /// client. The provider loader performs the expensive scan once; each
    /// game check below is only a dictionary lookup.
    /// </summary>
    internal static bool ShouldScheduleGameProbe(string status, bool includeIdle)
    {
        return includeIdle || IsOperationRelevant(status);
    }

    private static bool ConfirmMissing(string storeId, string gameId, DateTimeOffset now)
    {
        var key = $"{storeId}\n{gameId}";
        lock (MissingGate)
        {
            if (!MissingObservations.TryGetValue(key, out var observation))
            {
                MissingObservations[key] = (1, now);
                return false;
            }

            observation = (observation.Count + 1, observation.FirstAtUtc);
            if (observation.Count >= 2 && now - observation.FirstAtUtc >= TimeSpan.FromSeconds(1))
            {
                MissingObservations.Remove(key);
                return true;
            }

            MissingObservations[key] = observation;
            return false;
        }
    }

    private static void ResetMissing(string storeId, string gameId)
    {
        lock (MissingGate)
        {
            MissingObservations.Remove($"{storeId}\n{gameId}");
        }
    }
}
