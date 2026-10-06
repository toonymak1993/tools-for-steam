using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Coordinates cheap local convergence and remote catalog checks separately.
/// Remote providers are checked concurrently and each provider owns its retry
/// schedule, so a slow network request cannot stall another store.
/// </summary>
internal sealed class OmniLibraryRefreshCoordinator
{
    internal static readonly TimeSpan LocalStartupDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan RemoteStartupDelay = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan CatalogCheckInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumFailureBackoff = TimeSpan.FromHours(1);
    internal static readonly TimeSpan SchedulerWakeInterval = TimeSpan.FromMinutes(1);

    private readonly StoreSyncService _storeSyncService;
    private readonly Func<bool> _isEnabled;
    private readonly Action<string> _writeDiagnostic;
    private readonly Func<double> _nextJitter;

    public OmniLibraryRefreshCoordinator(
        StoreSyncService storeSyncService,
        Func<bool> isEnabled,
        Action<string> writeDiagnostic,
        Func<double>? nextJitter = null)
    {
        _storeSyncService = storeSyncService;
        _isEnabled = isEnabled;
        _writeDiagnostic = writeDiagnostic;
        _nextJitter = nextJitter ?? (() => Random.Shared.NextDouble());
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var processStartedAtUtc = DateTimeOffset.UtcNow;
        var remoteChecksBeginAtUtc = processStartedAtUtc.Add(RemoteStartupDelay);
        await Task.Delay(LocalStartupDelay, cancellationToken);

        var schedules = new Dictionary<string, StoreSchedule>(StringComparer.OrdinalIgnoreCase);
        var locallyReconciledStoreIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_isEnabled())
            {
                schedules.Clear();
                locallyReconciledStoreIds.Clear();
                remoteChecksBeginAtUtc = DateTimeOffset.UtcNow.Add(RemoteStartupDelay);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                continue;
            }

            var enabledStoreIds = _storeSyncService.GetEnabledUnifySteamStoreIds();
            var enabledStoreSet = enabledStoreIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleStoreId in schedules.Keys
                         .Where(storeId => !enabledStoreSet.Contains(storeId))
                         .ToArray())
            {
                schedules.Remove(staleStoreId);
                locallyReconciledStoreIds.Remove(staleStoreId);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var storeId in enabledStoreIds)
            {
                if (locallyReconciledStoreIds.Add(storeId))
                {
                    var queuedCount = _storeSyncService.QueueOmniLibraryLocalReconciliation(
                        storeId,
                        "omnilibrary-startup");
                    if (queuedCount > 0)
                    {
                        _writeDiagnostic(
                            $"{storeId} startup local check: queued {queuedCount} repair title(s).");
                    }
                }

                if (!schedules.ContainsKey(storeId))
                {
                    schedules[storeId] = new StoreSchedule(0, remoteChecksBeginAtUtc);
                    _storeSyncService.RecordOmniLibraryRemoteSchedule(
                        storeId,
                        remoteChecksBeginAtUtc,
                        "startup-delay");
                }
            }

            var dueStoreIds = enabledStoreIds
                .Where(storeId =>
                    schedules.TryGetValue(storeId, out var schedule) &&
                    now >= schedule.NextCheckAtUtc)
                .ToArray();
            if (dueStoreIds.Length > 0)
            {
                var results = await Task.WhenAll(dueStoreIds.Select(storeId =>
                    CheckStoreAsync(storeId, cancellationToken)));
                now = DateTimeOffset.UtcNow;
                foreach (var outcome in results)
                {
                    schedules.TryGetValue(outcome.StoreId, out var previous);
                    if (outcome.Result is { Checked: true, Succeeded: true } result)
                    {
                        var nextCheck = now.Add(AddSuccessJitter(CatalogCheckInterval, _nextJitter()));
                        schedules[outcome.StoreId] = new StoreSchedule(0, nextCheck);
                        _storeSyncService.RecordOmniLibraryRemoteCheck(
                            outcome.StoreId,
                            succeeded: true,
                            result.Detail,
                            nextCheck);
                        if (result.CatalogChanged || result.StateChanged)
                        {
                            _writeDiagnostic(
                                $"{outcome.StoreId} scheduled remote check: {result.Detail}");
                        }
                        continue;
                    }

                    if (outcome.Result is { Checked: false, Succeeded: true } skipped)
                    {
                        var nextCheck = now.Add(CatalogCheckInterval);
                        schedules[outcome.StoreId] = new StoreSchedule(0, nextCheck);
                        _storeSyncService.RecordOmniLibraryRemoteCheck(
                            outcome.StoreId,
                            succeeded: true,
                            skipped.Detail,
                            nextCheck);
                        continue;
                    }

                    var failureCount = Math.Min((previous?.FailureCount ?? 0) + 1, 8);
                    var backoff = ComputeFailureBackoff(failureCount, 0.85 + (_nextJitter() * 0.3));
                    var retryAtUtc = now.Add(backoff);
                    schedules[outcome.StoreId] = new StoreSchedule(failureCount, retryAtUtc);
                    var detail = outcome.Result?.Detail ?? outcome.ExceptionDetail;
                    _storeSyncService.RecordOmniLibraryRemoteCheck(
                        outcome.StoreId,
                        succeeded: false,
                        detail,
                        retryAtUtc);
                    _writeDiagnostic(
                        $"{outcome.StoreId} scheduled remote check failed; retry in " +
                        $"{backoff.TotalMinutes:0.0} min: {detail}");
                }
            }

            var nextCheckAtUtc = schedules.Count == 0
                ? DateTimeOffset.UtcNow.Add(SchedulerWakeInterval)
                : schedules.Values.Min(schedule => schedule.NextCheckAtUtc);
            var delay = nextCheckAtUtc - DateTimeOffset.UtcNow;
            delay = delay < TimeSpan.FromSeconds(2)
                ? TimeSpan.FromSeconds(2)
                : delay > SchedulerWakeInterval
                    ? SchedulerWakeInterval
                    : delay;
            await Task.Delay(delay, cancellationToken);
        }
    }

    internal static TimeSpan ComputeFailureBackoff(int failureCount, double jitterMultiplier = 1)
    {
        var multiplier = Math.Pow(2, Math.Max(0, failureCount - 1));
        var baseTicks = Math.Min(
            MaximumFailureBackoff.Ticks,
            (long)(CatalogCheckInterval.Ticks * multiplier));
        return TimeSpan.FromTicks(Math.Clamp(
            (long)(baseTicks * Math.Clamp(jitterMultiplier, 0.75, 1.25)),
            TimeSpan.FromSeconds(5).Ticks,
            MaximumFailureBackoff.Ticks));
    }

    internal static TimeSpan AddSuccessJitter(TimeSpan interval, double jitterSample)
    {
        var extra = TimeSpan.FromSeconds(30 * Math.Clamp(jitterSample, 0, 1));
        return interval.Add(extra);
    }

    private async Task<StoreCheckOutcome> CheckStoreAsync(
        string storeId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(
                () => _storeSyncService.CheckUnifySteamStoreForChanges(storeId),
                cancellationToken);
            return new StoreCheckOutcome(storeId, result, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new StoreCheckOutcome(
                storeId,
                null,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private sealed record StoreSchedule(int FailureCount, DateTimeOffset NextCheckAtUtc);

    private sealed record StoreCheckOutcome(
        string StoreId,
        OmniLibraryStoreCheckResult? Result,
        string ExceptionDetail);
}
