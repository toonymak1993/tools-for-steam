namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Generic, cheap local-only installation reconciliation for providers whose
/// "is this game installed" check is a pure filesystem/registry read (Xbox's
/// XboxGames drive scan, Epic's own LauncherInstalled.dat manifest) rather
/// than a helper-process invocation. Mirrors GogInstallStateTracker's cadence
/// so an install/uninstall made outside OmniLibrary (directly in the Xbox
/// app or Epic Games Launcher) is reflected within seconds instead of
/// waiting for the next catalog refresh.
/// </summary>
internal static class ProviderInstallStateTracker
{
    private static readonly TimeSpan IdleInstalledProbeInterval = TimeSpan.FromSeconds(8);
    private static readonly Dictionary<string, (int Count, DateTimeOffset FirstAtUtc)>
        MissingObservations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MissingGate = new();

    public static async Task RunAsync(
        string storeId,
        Func<Dictionary<string, UnifySteamGameCacheEntry>> loadInstalled,
        CancellationToken cancellationToken)
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
                if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                    store?.Cache?.Games is null)
                {
                    continue;
                }

                var statuses = UnifySteamDownloadStatusStore.GetAll();
                var now = DateTimeOffset.UtcNow;
                var includeIdle = now - lastIdleProbeAtUtc >= IdleInstalledProbeInterval;
                var games = store.Cache.Games
                    .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
                    .ToArray();
                var hasOperationRelevantGame = games.Any(game =>
                    IsOperationRelevant(UnifySteamDownloadStatusStore.Get(statuses, storeId, game.Id).Status));

                if (!includeIdle && !hasOperationRelevantGame)
                {
                    continue;
                }

                if (includeIdle)
                {
                    lastIdleProbeAtUtc = now;
                }

                var installed = loadInstalled();
                var changed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
                var cleared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    if (game.Installed && ConfirmMissing(storeId, game.Id, now))
                    {
                        cleared.Add(game.Id);
                        // The local provider is authoritative. Once removal is
                        // confirmed, no old completed/failed/handoff state may
                        // keep the game looking installed or busy in Steam.
                        UnifySteamDownloadStatusStore.Clear(storeId, game.Id);
                    }
                }

                if (changed.Count == 0 && cleared.Count == 0)
                {
                    continue;
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // The next two-second pass retries; no remote catalog call is involved.
            }
        }
    }

    private static bool IsOperationRelevant(string status)
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
