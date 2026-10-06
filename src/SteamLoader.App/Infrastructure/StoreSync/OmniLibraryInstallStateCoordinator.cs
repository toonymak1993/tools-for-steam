namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Owns all local provider installation checks. Only one provider scan may run
/// at a time, settings/status documents are cached by file revision, disabled
/// providers are dormant, and actionable download hand-offs temporarily use a
/// faster cadence than the provider-wide idle safety check.
/// </summary>
internal sealed class OmniLibraryInstallStateCoordinator
{
    internal static readonly TimeSpan SchedulerWakeInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ActiveCheckInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan ActiveStatusLifetime = TimeSpan.FromMinutes(2);

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
                        cachedStore.Cache?.Games ?? []),
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
    private readonly Dictionary<string, DateTimeOffset> _nextIdleCheckAtUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextActiveCheckAtUtc =
        new(StringComparer.OrdinalIgnoreCase);

    private StoreSyncConfiguration? _configuration;
    private IReadOnlyDictionary<string, UnifySteamDownloadStatus> _statuses =
        new Dictionary<string, UnifySteamDownloadStatus>(StringComparer.OrdinalIgnoreCase);
    private long _settingsRevision = long.MinValue;
    private long _downloadsRevision = long.MinValue;
    private long _gogJournalRevision = long.MinValue;

    public OmniLibraryInstallStateCoordinator(
        StoreSyncSettingsStore settings,
        Func<bool> isEnabled,
        Action<string, IReadOnlyCollection<string>>? onInstalledStateChanged,
        Action<string>? diagnosticLog = null)
    {
        _settings = settings;
        _isEnabled = isEnabled;
        _onInstalledStateChanged = onInstalledStateChanged;
        _diagnosticLog = diagnosticLog;
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
                var due = SelectDueProvider(now, gogJournalChanged);
                if (due is null)
                {
                    await Task.Delay(SchedulerWakeInterval, cancellationToken);
                    continue;
                }

                var succeeded = RunProvider(
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
                        completedAtUtc.Add(succeeded
                            ? IdleCheckInterval
                            : TimeSpan.FromSeconds(5));
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

    private DueProvider? SelectDueProvider(DateTimeOffset now, bool gogJournalChanged)
    {
        if (_configuration is null)
        {
            return null;
        }

        DueProvider? selected = null;
        foreach (var provider in Providers)
        {
            if (!_configuration.UnifySteam.Stores.TryGetValue(provider.StoreId, out var store) ||
                store?.Enabled != true ||
                store.Cache?.Games is null)
            {
                continue;
            }

            var idleDue = !_nextIdleCheckAtUtc.TryGetValue(provider.StoreId, out var idleAt) ||
                now >= idleAt;
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

    private bool RunProvider(
        ProviderDefinition provider,
        UnifySteamStoreConfiguration store,
        bool includeIdle)
    {
        try
        {
            provider.Reconcile(
                store,
                _statuses,
                _settings,
                _onInstalledStateChanged,
                includeIdle);
            return true;
        }
        catch (Exception exception)
        {
            Log(
                $"provider={provider.StoreId} includeIdle={includeIdle} " +
                $"failed={exception.GetType().Name}:{exception.Message}");
            return false;
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
        Action<
            UnifySteamStoreConfiguration,
            IReadOnlyDictionary<string, UnifySteamDownloadStatus>,
            StoreSyncSettingsStore,
            Action<string, IReadOnlyCollection<string>>?,
            bool> Reconcile);

    private readonly record struct DueProvider(
        ProviderDefinition Provider,
        UnifySteamStoreConfiguration Store,
        bool IncludeIdle,
        DateTimeOffset DueAtUtc);
}
