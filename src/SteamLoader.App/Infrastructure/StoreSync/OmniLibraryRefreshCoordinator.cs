using System.Collections.Concurrent;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Coordinates cheap local convergence and remote catalog checks separately.
/// Every store runs its own check and owns its retry schedule, so a slow or
/// hung network request cannot stall another store. Watcher-triggered checks
/// enter through <see cref="RequestCheck"/> and share that schedule. While a
/// game runs no new work starts; due and requested checks run once afterwards.
/// </summary>
internal sealed class OmniLibraryRefreshCoordinator
{
    internal static readonly TimeSpan LocalStartupDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan RemoteStartupDelay = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan CatalogCheckInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumFailureBackoff = TimeSpan.FromHours(1);
    internal static readonly TimeSpan SchedulerWakeInterval = TimeSpan.FromMinutes(1);
    // How often a paused scheduler looks whether the game has ended, and how
    // long it lets Steam settle (cloud saves, overlay, library) afterwards.
    internal static readonly TimeSpan GameActivityPollInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan GameEndSettleDelay = TimeSpan.FromSeconds(15);

    private readonly StoreSyncService _storeSyncService;
    private readonly Func<bool> _isEnabled;
    private readonly Action<string> _writeDiagnostic;
    private readonly Func<double> _nextJitter;
    private readonly Func<bool> _isGameActive;
    private readonly ConcurrentDictionary<string, string> _requestedChecks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public OmniLibraryRefreshCoordinator(
        StoreSyncService storeSyncService,
        Func<bool> isEnabled,
        Action<string> writeDiagnostic,
        Func<double>? nextJitter = null,
        Func<bool>? isGameActive = null)
    {
        _storeSyncService = storeSyncService;
        _isEnabled = isEnabled;
        _writeDiagnostic = writeDiagnostic;
        _nextJitter = nextJitter ?? (() => Random.Shared.NextDouble());
        _isGameActive = isGameActive ?? OmniLibraryGameActivity.IsGameActive;
    }

    /// <summary>
    /// Asks for a remote check of one store as soon as the scheduler may run
    /// it. Repeated requests before that coalesce into one check, and a
    /// successful check restarts the store's regular interval.
    /// </summary>
    public void RequestCheck(string storeId, string trigger)
    {
        var normalizedStoreId = storeId?.Trim() ?? string.Empty;
        if (!OmniLibraryStoreRegistry.TryGet(normalizedStoreId, out _))
        {
            return;
        }

        _requestedChecks[normalizedStoreId] = string.IsNullOrWhiteSpace(trigger)
            ? "request"
            : trigger.Trim();
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // The scheduler is already due to wake.
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var processStartedAtUtc = DateTimeOffset.UtcNow;
        var remoteChecksBeginAtUtc = processStartedAtUtc.Add(RemoteStartupDelay);
        await Task.Delay(LocalStartupDelay, cancellationToken);

        var schedules = new Dictionary<string, StoreSchedule>(StringComparer.OrdinalIgnoreCase);
        var locallyReconciledStoreIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inFlight = new Dictionary<string, Task<StoreCheckOutcome>>(StringComparer.OrdinalIgnoreCase);
        var gameWasActive = false;
        var resumeAtUtc = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_isEnabled())
            {
                // A running check notices the disabled store itself; its
                // result no longer schedules anything.
                foreach (var completed in inFlight.Where(pair => pair.Value.IsCompleted).ToArray())
                {
                    inFlight.Remove(completed.Key);
                }

                schedules.Clear();
                locallyReconciledStoreIds.Clear();
                _requestedChecks.Clear();
                _storeSyncService.SetOmniLibraryBackgroundPause(false, string.Empty, null);
                remoteChecksBeginAtUtc = DateTimeOffset.UtcNow.Add(RemoteStartupDelay);
                await WaitForWorkAsync(TimeSpan.FromSeconds(10), [], cancellationToken);
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
                _requestedChecks.TryRemove(staleStoreId, out _);
            }

            var now = DateTimeOffset.UtcNow;
            var gameActive = IsGameActiveSafely();
            if (gameActive != gameWasActive)
            {
                gameWasActive = gameActive;
                if (gameActive)
                {
                    _writeDiagnostic("Game running: background OmniLibrary checks are paused.");
                }
                else
                {
                    resumeAtUtc = now.Add(GameEndSettleDelay);
                    _writeDiagnostic(
                        $"Game ended: background OmniLibrary checks resume in {GameEndSettleDelay.TotalSeconds:0} s.");
                }
            }

            var paused = IsPausedForGame(gameActive, now, resumeAtUtc);
            _storeSyncService.SetOmniLibraryBackgroundPause(
                paused,
                gameActive ? "game" : "settling",
                gameActive ? null : resumeAtUtc);
            foreach (var storeId in enabledStoreIds)
            {
                // The local pass may rewrite shortcuts and fetch artwork, so
                // it waits for the game like the remote checks do.
                if (!paused && locallyReconciledStoreIds.Add(storeId))
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

            if (!paused)
            {
                foreach (var storeId in enabledStoreIds)
                {
                    if (inFlight.ContainsKey(storeId) ||
                        !schedules.TryGetValue(storeId, out var schedule))
                    {
                        continue;
                    }

                    // A request waits for the startup delay like a scheduled
                    // check, so TFS's own start does not hit every store twice.
                    var requestTrigger = string.Empty;
                    var requested =
                        now >= remoteChecksBeginAtUtc &&
                        _requestedChecks.TryRemove(storeId, out requestTrigger);
                    if (requested || now >= schedule.NextCheckAtUtc)
                    {
                        _storeSyncService.RecordOmniLibraryRemoteCheckStarted(
                            storeId,
                            requested ? requestTrigger ?? "request" : "scheduled");
                        inFlight[storeId] = CheckStoreAsync(storeId, cancellationToken);
                    }
                }
            }

            foreach (var (storeId, task) in inFlight.Where(pair => pair.Value.IsCompleted).ToArray())
            {
                inFlight.Remove(storeId);
                if (!task.IsCompletedSuccessfully || !enabledStoreSet.Contains(storeId))
                {
                    continue;
                }

                schedules[storeId] = RecordOutcome(
                    task.Result,
                    schedules.GetValueOrDefault(storeId),
                    DateTimeOffset.UtcNow);
            }

            var delay = paused
                ? ComputePausedDelay(gameActive, DateTimeOffset.UtcNow, resumeAtUtc)
                : ComputeSchedulerDelay(
                    schedules
                        .Where(pair => !inFlight.ContainsKey(pair.Key))
                        .Select(pair => pair.Value.NextCheckAtUtc),
                    DateTimeOffset.UtcNow);
            await WaitForWorkAsync(delay, inFlight.Values, cancellationToken);
        }
    }

    private StoreSchedule RecordOutcome(
        StoreCheckOutcome outcome,
        StoreSchedule? previous,
        DateTimeOffset now)
    {
        if (outcome.Result is { Checked: true, Succeeded: true } result)
        {
            var nextCheck = now.Add(AddSuccessJitter(CatalogCheckInterval, _nextJitter()));
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

            return new StoreSchedule(0, nextCheck);
        }

        if (outcome.Result is { Checked: false, Succeeded: true } skipped)
        {
            var nextCheck = now.Add(CatalogCheckInterval);
            _storeSyncService.RecordOmniLibraryRemoteCheck(
                outcome.StoreId,
                succeeded: true,
                skipped.Detail,
                nextCheck);
            return new StoreSchedule(0, nextCheck);
        }

        var failureCount = Math.Min((previous?.FailureCount ?? 0) + 1, 8);
        var backoff = ComputeFailureBackoff(failureCount, 0.85 + (_nextJitter() * 0.3));
        var retryAtUtc = now.Add(backoff);
        var detail = outcome.Result?.Detail ?? outcome.ExceptionDetail;
        _storeSyncService.RecordOmniLibraryRemoteCheck(
            outcome.StoreId,
            succeeded: false,
            detail,
            retryAtUtc);
        _writeDiagnostic(
            $"{outcome.StoreId} scheduled remote check failed; retry in " +
            $"{backoff.TotalMinutes:0.0} min: {detail}");
        return new StoreSchedule(failureCount, retryAtUtc);
    }

    internal static bool IsPausedForGame(
        bool gameActive,
        DateTimeOffset now,
        DateTimeOffset resumeAtUtc) =>
        gameActive || now < resumeAtUtc;

    internal static TimeSpan ComputePausedDelay(
        bool gameActive,
        DateTimeOffset now,
        DateTimeOffset resumeAtUtc)
    {
        if (gameActive)
        {
            return GameActivityPollInterval;
        }

        var untilResume = resumeAtUtc - now;
        return untilResume <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : untilResume > GameActivityPollInterval
                ? GameActivityPollInterval
                : untilResume;
    }

    internal static TimeSpan ComputeSchedulerDelay(
        IEnumerable<DateTimeOffset> nextCheckTimesUtc,
        DateTimeOffset now)
    {
        var times = nextCheckTimesUtc.ToArray();
        var delay = times.Length == 0
            ? SchedulerWakeInterval
            : times.Min() - now;
        return delay < TimeSpan.FromSeconds(2)
            ? TimeSpan.FromSeconds(2)
            : delay > SchedulerWakeInterval
                ? SchedulerWakeInterval
                : delay;
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

    private bool IsGameActiveSafely()
    {
        try
        {
            return _isGameActive();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sleeps until the delay passes, a check is requested or a running store
    /// check finishes, whichever comes first.
    /// </summary>
    private async Task WaitForWorkAsync(
        TimeSpan delay,
        IEnumerable<Task> inFlight,
        CancellationToken cancellationToken)
    {
        using var waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waits = new List<Task>
        {
            Task.Delay(delay, waitCancellation.Token),
            _wake.WaitAsync(waitCancellation.Token),
        };
        waits.AddRange(inFlight);
        await Task.WhenAny(waits);
        waitCancellation.Cancel();
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
