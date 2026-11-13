using System.Collections.Concurrent;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Owns all local provider installation checks. Only one provider scan may run
/// at a time, settings/status documents are cached by file revision, disabled
/// providers are dormant, and actionable download hand-offs temporarily use a
/// faster cadence than the provider-wide idle safety check. Launcher folder
/// watchers ask for an immediate provider pass through <see cref="RequestCheck"/>,
/// so installs and uninstalls done in Epic, GOG Galaxy or the Xbox app show up
/// within seconds. Provider-wide passes wait while a game runs.
/// </summary>
internal sealed class OmniLibraryInstallStateCoordinator
{
    internal static readonly TimeSpan SchedulerWakeInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ActiveCheckInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan ActiveStatusLifetime = TimeSpan.FromMinutes(2);
    // A game seen missing once is only marked uninstalled by a second pass;
    // this keeps that confirmation close instead of a full idle interval away.
    internal static readonly TimeSpan MissingConfirmationInterval = TimeSpan.FromSeconds(15);
    // Watcher bursts (a launcher writing many files) settle before the scan.
    internal static readonly TimeSpan RequestSettleDelay = TimeSpan.FromSeconds(2);

    private static readonly ProviderDefinition[] Providers =
    [
        new(
            "xbox-game-pass",
            TimeSpan.FromSeconds(2),
            (store, statuses, settings, changed, includeIdle) =>
                ProviderInstallStateTracker.Reconcile(
                    "xbox-game-pass",
                    store,
                    statuses,
                    cachedStore => UnifySteamService.LoadXboxInstalledGamesForReconciliation(
                        cachedStore.Cache?.Games ?? [],
                        cachedStore.InstallPath),
                    settings,
                    changed,
                    includeIdle)),
        new(
            "gog-galaxy",
            TimeSpan.FromSeconds(8),
            (store, statuses, settings, changed, includeIdle) =>
                GogInstallStateTracker.Reconcile(
                    store,
                    statuses,
                    settings,
                    changed,
                    includeIdle)),
        new(
            "epic-games",
            TimeSpan.FromSeconds(14),
            (store, statuses, settings, changed, includeIdle) =>
                ProviderInstallStateTracker.Reconcile(
                    "epic-games",
                    store,
                    statuses,
                    cachedStore => UnifySteamService.LoadEpicInstalledGamesForReconciliation(
                        cachedStore.Cache?.Games ?? []),
                    settings,
                    changed,
                    includeIdle)),
    ];

    private readonly StoreSyncSettingsStore _settings;
    private readonly Func<bool> _isEnabled;
    private readonly Action<string, IReadOnlyCollection<string>>? _onInstalledStateChanged;
    private readonly Action<string>? _diagnosticLog;
    private readonly Func<bool> _isGameActive;
    private readonly Dictionary<string, DateTimeOffset> _nextIdleCheckAtUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextActiveCheckAtUtc =
        new(StringComparer.OrdinalIgnoreCase);
    // Store id -> earliest time a requested provider pass may run.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _requestedChecks =
        new(StringComparer.OrdinalIgnoreCase);

    private StoreSyncConfiguration? _configuration;
    private IReadOnlyDictionary<string, UnifySteamDownloadStatus> _statuses =
        new Dictionary<string, UnifySteamDownloadStatus>(StringComparer.OrdinalIgnoreCase);
    private long _settingsRevision = long.MinValue;
    private long _downloadsRevision = long.MinValue;
    private long _gogJournalRevision = long.MinValue;
    private bool _pausedForGame;

    public OmniLibraryInstallStateCoordinator(
        StoreSyncSettingsStore settings,
        Func<bool> isEnabled,
        Action<string, IReadOnlyCollection<string>>? onInstalledStateChanged,
        Action<string>? diagnosticLog = null,
        Func<bool>? isGameActive = null)
    {
        _settings = settings;
        _isEnabled = isEnabled;
        _onInstalledStateChanged = onInstalledStateChanged;
        _diagnosticLog = diagnosticLog;
        _isGameActive = isGameActive ?? OmniLibraryGameActivity.IsGameActive;
    }

    /// <summary>
    /// Asks for a provider-wide install pass soon, for example because a
    /// launcher folder changed. Repeated requests coalesce into one pass.
    /// </summary>
    public void RequestCheck(string storeId)
    {
        var normalizedStoreId = storeId?.Trim() ?? string.Empty;
        if (!Providers.Any(provider =>
                provider.StoreId.Equals(normalizedStoreId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _requestedChecks[normalizedStoreId] = DateTimeOffset.UtcNow.Add(RequestSettleDelay);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_isEnabled())
                {
                    await Task.Delay(SchedulerWakeInterval, cancellationToken);
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                RefreshSettingsIfChanged(now);
                RefreshStatusesIfChanged();
                var gogJournalChanged = RefreshGogJournalRevision();
                var due = SelectDueProvider(now, gogJournalChanged, IsIdlePausedForGame);
                if (due is null)
                {
                    await Task.Delay(SchedulerWakeInterval, cancellationToken);
                    continue;
                }

                if (due.Value.IncludeIdle)
                {
                    // Taken before the scan: a watcher event during the scan
                    // asks again and gets its own pass.
                    _requestedChecks.TryRemove(due.Value.Provider.StoreId, out _);
                }

                var (succeeded, confirmationPending) = RunProvider(
                    due.Value.Provider,
                    due.Value.Store,
                    due.Value.IncludeIdle);
                var completedAtUtc = DateTimeOffset.UtcNow;
                _nextActiveCheckAtUtc[due.Value.Provider.StoreId] =
                    completedAtUtc.Add(succeeded
                        ? ActiveCheckInterval
                        : TimeSpan.FromSeconds(5));
                if (due.Value.IncludeIdle)
                {
                    _nextIdleCheckAtUtc[due.Value.Provider.StoreId] =
                        completedAtUtc.Add(NextIdleDelay(succeeded, confirmationPending));
                }

                // A successful reconciliation may have persisted install-state
                // changes. The revision check keeps the cached object coherent
                // without forcing another parse when nothing changed.
                RefreshSettingsIfChanged(completedAtUtc);
                RefreshStatusesIfChanged();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log($"coordinator-error type={exception.GetType().Name} message={exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private void RefreshSettingsIfChanged(DateTimeOffset now)
    {
        var revision = _settings.GetRevision();
        if (_configuration is not null && revision == _settingsRevision)
        {
            return;
        }

        _configuration = _settings.Load();
        _settingsRevision = revision;
        foreach (var provider in Providers)
        {
            if (!_nextIdleCheckAtUtc.ContainsKey(provider.StoreId))
            {
                _nextIdleCheckAtUtc[provider.StoreId] = now.Add(provider.StartupDelay);
            }
        }
    }

    private void RefreshStatusesIfChanged()
    {
        var revision = UnifySteamDownloadStatusStore.GetRevision();
        if (revision == _downloadsRevision)
        {
            return;
        }

        _statuses = UnifySteamDownloadStatusStore.GetAll();
        _downloadsRevision = UnifySteamDownloadStatusStore.GetRevision();
    }

    private bool RefreshGogJournalRevision()
    {
        var revision = GogOperationJournal.GetRevision();
        if (revision == _gogJournalRevision)
        {
            return false;
        }

        var changed = _gogJournalRevision != long.MinValue;
        _gogJournalRevision = revision;
        return changed;
    }

    internal static TimeSpan NextIdleDelay(bool succeeded, bool confirmationPending)
    {
        if (!succeeded)
        {
            return TimeSpan.FromSeconds(5);
        }

        return confirmationPending ? MissingConfirmationInterval : IdleCheckInterval;
    }

    /// <summary>
    /// Provider-wide passes (scheduled or requested) wait while a game runs,
    /// like the catalog checks; they run once the game has ended. Checks for
    /// an install or uninstall the player started keep running.
    /// </summary>
    private bool IsIdlePausedForGame()
    {
        bool gameActive;
        try
        {
            gameActive = _isGameActive();
        }
        catch
        {
            gameActive = false;
        }

        if (gameActive != _pausedForGame)
        {
            _pausedForGame = gameActive;
            Log(gameActive
                ? "game-running: provider-wide install checks paused"
                : "game-ended: provider-wide install checks resumed");
        }

        return gameActive;
    }

    private DueProvider? SelectDueProvider(
        DateTimeOffset now,
        bool gogJournalChanged,
        Func<bool> isIdlePaused)
    {
        if (_configuration is null)
        {
            return null;
        }

        DueProvider? selected = null;
        bool? idlePaused = null;
        foreach (var provider in Providers)
        {
            if (!_configuration.UnifySteam.Stores.TryGetValue(provider.StoreId, out var store) ||
                store?.Enabled != true ||
                store.Cache?.Games is null)
            {
                continue;
            }

            var scheduledIdleDue =
                !_nextIdleCheckAtUtc.TryGetValue(provider.StoreId, out var idleAt) ||
                now >= idleAt;
            if (_requestedChecks.TryGetValue(provider.StoreId, out var requestedAt) &&
                now >= requestedAt)
            {
                scheduledIdleDue = true;
                idleAt = requestedAt < idleAt ? requestedAt : idleAt;
            }

            // The game check reads the registry, so it only runs when a
            // provider-wide pass is actually due.
            var idleDue = scheduledIdleDue && !(idlePaused ??= isIdlePaused());
            var active = HasRelevantStatus(_statuses, provider.StoreId, now) ||
                provider.StoreId.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase) &&
                gogJournalChanged;
            var activeDue = active &&
                (!_nextActiveCheckAtUtc.TryGetValue(provider.StoreId, out var activeAt) ||
                 now >= activeAt);
            if (!idleDue && !activeDue)
            {
                continue;
            }

            var dueAt = activeDue
                ? _nextActiveCheckAtUtc.GetValueOrDefault(provider.StoreId, DateTimeOffset.MinValue)
                : idleAt;
            if (selected is null || dueAt < selected.Value.DueAtUtc)
            {
                selected = new DueProvider(provider, store, idleDue, dueAt);
            }
        }

        return selected;
    }

    private (bool Succeeded, bool ConfirmationPending) RunProvider(
        ProviderDefinition provider,
        UnifySteamStoreConfiguration store,
        bool includeIdle)
    {
        try
        {
            var confirmationPending = provider.Reconcile(
                store,
                _statuses,
                _settings,
                _onInstalledStateChanged,
                includeIdle);
            return (true, confirmationPending);
        }
        catch (Exception exception)
        {
            Log(
                $"provider={provider.StoreId} includeIdle={includeIdle} " +
                $"failed={exception.GetType().Name}:{exception.Message}");
            return (false, false);
        }
    }

    internal static bool HasRelevantStatus(
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> statuses,
        string storeId,
        DateTimeOffset now)
    {
        var prefix = $"{storeId.Trim().ToLowerInvariant()}:";
        return statuses.Any(pair =>
            pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            now - pair.Value.UpdatedAtUtc <= ActiveStatusLifetime &&
            ProviderInstallStateTracker.IsOperationRelevant(pair.Value.Status));
    }

    private void Log(string message)
    {
        try
        {
            _diagnosticLog?.Invoke(message);
        }
        catch
        {
        }
    }

    private sealed record ProviderDefinition(
        string StoreId,
        TimeSpan StartupDelay,
        Func<
            UnifySteamStoreConfiguration,
            IReadOnlyDictionary<string, UnifySteamDownloadStatus>,
            StoreSyncSettingsStore,
            Action<string, IReadOnlyCollection<string>>?,
            bool,
            bool> Reconcile);

    private readonly record struct DueProvider(
        ProviderDefinition Provider,
        UnifySteamStoreConfiguration Store,
        bool IncludeIdle,
        DateTimeOffset DueAtUtc);
}
