namespace SteamLoader.App.Infrastructure.StoreSync;

public sealed class StoreSyncAutomationService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WatcherRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WatcherDebounceInterval = TimeSpan.FromMilliseconds(1400);

    private readonly StoreSyncService _storeSyncService;
    private readonly Func<bool> _isPluginEnabled;
    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _nextPollAtUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _nextWatcherRefreshAtUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastWatcherEventAtUtc;
    private string _pendingWatcherTriggerSource = string.Empty;

    public StoreSyncAutomationService(
        StoreSyncService storeSyncService,
        Func<bool>? isPluginEnabled = null)
    {
        _storeSyncService = storeSyncService;
        _isPluginEnabled = isPluginEnabled ?? (() => true);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            RefreshWatchers();

            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;

                if (now >= _nextWatcherRefreshAtUtc)
                {
                    RefreshWatchers();
                    _nextWatcherRefreshAtUtc = now.Add(WatcherRefreshInterval);
                }

                if (now >= _nextPollAtUtc)
                {
                    if (_isPluginEnabled())
                    {
                        _storeSyncService.TryRunAutomaticUnifySteamRefresh();
                    }

                    TryRunAutomaticSync("poll");
                    _nextPollAtUtc = now.Add(PollInterval);
                }

                TryFlushWatcherTrigger(now);
                await Task.Delay(LoopInterval, cancellationToken);
            }
        }
        finally
        {
            ClearWatchers();
            _storeSyncService.UpdateAutomationWatchers(0, false);
        }
    }

    private void TryRunAutomaticSync(string triggerSource)
    {
        try
        {
            if (!_isPluginEnabled())
            {
                return;
            }

            _storeSyncService.RecordAutomationCheck(triggerSource);
            _storeSyncService.TryRunAutomaticSync(triggerSource);
        }
        catch
        {
        }
    }

    private void TryFlushWatcherTrigger(DateTimeOffset now)
    {
        string triggerSource;
        DateTimeOffset? lastEventAtUtc;

        lock (_gate)
        {
            triggerSource = _pendingWatcherTriggerSource;
            lastEventAtUtc = _lastWatcherEventAtUtc;
        }

        if (string.IsNullOrWhiteSpace(triggerSource) ||
            !lastEventAtUtc.HasValue ||
            now - lastEventAtUtc.Value < WatcherDebounceInterval)
        {
            return;
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_pendingWatcherTriggerSource) ||
                !_lastWatcherEventAtUtc.HasValue ||
                now - _lastWatcherEventAtUtc.Value < WatcherDebounceInterval)
            {
                return;
            }

            triggerSource = _pendingWatcherTriggerSource;
            _pendingWatcherTriggerSource = string.Empty;
            _lastWatcherEventAtUtc = null;
        }

        TryRunAutomaticSync(triggerSource);
    }

    private void RefreshWatchers()
    {
        IReadOnlyList<StoreSyncService.StoreSyncWatchTarget> targets;

        try
        {
            targets = _storeSyncService.GetAutomationWatchTargets();
        }
        catch
        {
            targets = [];
        }

        var targetKeys = targets
            .Select(BuildWatcherKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var key in _watchers.Keys.Where(key => !targetKeys.Contains(key)).ToArray())
            {
                DisposeWatcher(_watchers[key]);
                _watchers.Remove(key);
            }

            foreach (var target in targets)
            {
                var key = BuildWatcherKey(target);
                if (_watchers.ContainsKey(key))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(target.DirectoryPath, target.Filter)
                    {
                        IncludeSubdirectories = target.IncludeSubdirectories,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true,
                    };
                    watcher.Created += OnWatcherChanged;
                    watcher.Deleted += OnWatcherChanged;
                    watcher.Changed += OnWatcherChanged;
                    watcher.Renamed += OnWatcherRenamed;
                    _watchers[key] = watcher;
                }
                catch
                {
                }
            }

            _storeSyncService.UpdateAutomationWatchers(_watchers.Count, _watchers.Count > 0);
        }
    }

    private void ClearWatchers()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                DisposeWatcher(watcher);
            }

            _watchers.Clear();
        }
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs eventArgs)
    {
        if (_storeSyncService.ShouldIgnoreAutomationWatcherEvent(eventArgs.FullPath))
        {
            return;
        }

        ScheduleWatcherTrigger("watch");
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs eventArgs)
    {
        if (_storeSyncService.ShouldIgnoreAutomationWatcherEvent(eventArgs.OldFullPath) ||
            _storeSyncService.ShouldIgnoreAutomationWatcherEvent(eventArgs.FullPath))
        {
            return;
        }

        ScheduleWatcherTrigger("watch");
    }

    private void ScheduleWatcherTrigger(string triggerSource)
    {
        lock (_gate)
        {
            _storeSyncService.InvalidateAutomaticSyncState();
            _pendingWatcherTriggerSource = string.IsNullOrWhiteSpace(triggerSource) ? "watch" : triggerSource;
            _lastWatcherEventAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private static string BuildWatcherKey(StoreSyncService.StoreSyncWatchTarget target)
    {
        return $"{target.DirectoryPath}|{target.Filter}|{target.IncludeSubdirectories}";
    }

    private static void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch
        {
        }

        try
        {
            watcher.Dispose();
        }
        catch
        {
        }
    }
}
