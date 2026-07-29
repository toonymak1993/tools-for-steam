using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal enum XboxInstallEventKind
{
    Queued,
    Downloading,
    Paused,
    Finalizing,
    Completed,
    Canceled,
    Failed,
}

internal sealed record XboxInstallEventObservation(
    XboxInstallEventKind Kind,
    int ProgressPercent,
    string Stage,
    long RecordId,
    DateTimeOffset CreatedAtUtc);

internal sealed record XboxInstallTrackingResult(
    bool Completed,
    string Reason,
    bool Canceled = false);

internal sealed record XboxInstallEventBatch(
    long Cursor,
    IReadOnlyList<XboxInstallEventObservation> Observations);

internal sealed record XboxInstallEventRecordSnapshot(
    string Message,
    long RecordId,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Observes the persistent Windows Store installation queue rather than the
/// process-scoped AppInstallItem returned to the queueing caller.
/// </summary>
internal static partial class XboxInstallEventTracker
{
    private const string LogName = "Microsoft-Windows-Store/Operational";
    private const string ProviderName = "Microsoft-Windows-Install-Agent";
    private const int InstallAgentEventId = 2006;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan InstalledProbeInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TerminalGracePeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumTrackingTime = TimeSpan.FromHours(24);
    private static readonly object ReconcileGate = new();
    private static readonly Dictionary<string, DateTimeOffset> LastInstalledProbeAtUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _lastReconcileAtUtc = DateTimeOffset.MinValue;

    [GeneratedRegex(
        @"Progress Update:\s*Item\s*=\s*(?<id>[A-Za-z0-9_.-]+).*?Progress Stage\s*=\s*(?<stage>[^,]+),\s*Completed\s*=\s*(?<completed>\d+),\s*Total\s*=\s*(?<total>\d+),\s*Total\s*=\s*(?<percent>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(
        @"InstallQueueHeartbeat\s*::\s*ProductId\s*=\s*(?<id>[A-Za-z0-9_.-]+).*?CurrentPercentDownloaded\s*=\s*(?<percent>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex HeartbeatRegex();

    [GeneratedRegex(
        @"StateTransition\s*::\s*ProductId\s*=\s*(?<id>[A-Za-z0-9_.-]+).*?NewState\s*=\s*(?<state>[A-Za-z]+).*?HResult\s*=\s*(?<result>-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex StateTransitionRegex();

    [GeneratedRegex(
        @"FulfillmentComplete\s*::\s*ProductId\s*=\s*(?<id>[A-Za-z0-9_.-]+).*?HResult\s*=\s*(?<result>-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FulfillmentRegex();

    [GeneratedRegex(
        @"Item execution canceledItem\s*=\s*(?<id>[A-Za-z0-9_.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CanceledRegex();

    [GeneratedRegex(
        @"CatalogId\s*=\s*StoreId:(?<id>[A-Za-z0-9_.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CatalogPackageRegex();

    [GeneratedRegex(
        @"(?:\\?""ParentBundleId\\?""|ParentBundleId)\s*(?::|=)\s*\\?""?(?<id>[A-Za-z0-9_.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParentBundleRegex();

    public static long CaptureCursor()
    {
        try
        {
            var query = CreateQuery(reverseDirection: true);
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return record?.RecordId ?? 0;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Xbox event-log cursor failed: {exception.Message}");
            return 0;
        }
    }

    public static bool TryGetRecentState(
        string productId,
        TimeSpan maximumAge,
        out XboxInstallEventObservation observation)
    {
        observation = default!;
        if (!IsSafeProductId(productId))
        {
            return false;
        }

        var cutoff = DateTimeOffset.UtcNow - maximumAge;
        try
        {
            var acceptedProductIds = XboxProductRelationStore
                .GetRelatedProductIds(productId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var records = new List<XboxInstallEventRecordSnapshot>();
            var query = CreateQuery(reverseDirection: true);
            using var reader = new EventLogReader(query);
            for (var index = 0; index < 1024; index++)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                var createdAt = record.TimeCreated.HasValue
                    ? new DateTimeOffset(record.TimeCreated.Value.ToUniversalTime(), TimeSpan.Zero)
                    : DateTimeOffset.MinValue;
                if (createdAt < cutoff)
                {
                    break;
                }

                var snapshot = CreateSnapshot(record);
                records.Add(snapshot);
                if (TryExtractProductRelation(
                        snapshot.Message,
                        out var catalogProductId,
                        out var packageProductId) &&
                    string.Equals(
                        catalogProductId,
                        productId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    XboxProductRelationStore.Register(catalogProductId, packageProductId);
                    acceptedProductIds.Add(packageProductId);
                }
            }

            foreach (var snapshot in records)
            {
                if (TryParse(snapshot, acceptedProductIds, out observation))
                {
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Xbox event-log lookup failed: {exception.Message}");
        }

        return false;
    }

    public static bool ReconcileStatusStore(
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> statuses,
        Func<string, bool> isInstalled)
    {
        var now = DateTimeOffset.UtcNow;
        lock (ReconcileGate)
        {
            if (now - _lastReconcileAtUtc < PollInterval)
            {
                return false;
            }

            _lastReconcileAtUtc = now;
        }

        var changed = false;
        const string keyPrefix = "xbox-game-pass:";
        foreach (var (key, status) in statuses)
        {
            if (!key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) ||
                key.Length <= keyPrefix.Length ||
                status.Status == "completed")
            {
                continue;
            }

            var shouldInspect =
                UnifySteamDownloadStatusStore.IsActivelyTransferring(status.Status) ||
                status.Status is "paused" or "action-required" ||
                status.Status == "failed";
            if (!shouldInspect)
            {
                continue;
            }

            var productId = key[keyPrefix.Length..];
            var installedProbeInterval =
                UnifySteamDownloadStatusStore.IsActivelyTransferring(status.Status)
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(5);
            var shouldProbeInstalled = false;
            lock (ReconcileGate)
            {
                if (!LastInstalledProbeAtUtc.TryGetValue(productId, out var lastProbe) ||
                    now - lastProbe >= installedProbeInterval)
                {
                    LastInstalledProbeAtUtc[productId] = now;
                    shouldProbeInstalled = true;
                }
            }

            if (shouldProbeInstalled)
            {
                try
                {
                    if (isInstalled(productId))
                    {
                        UnifySteamDownloadStatusStore.Update(
                            "xbox-game-pass",
                            productId,
                            "completed",
                            100,
                            "Installed.",
                            workerProcessId: 0);
                        changed = true;
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(
                        $"Xbox installed-state reconciliation failed: {exception.Message}");
                }
            }

            if (status.Status == "failed" &&
                now - status.UpdatedAtUtc >= TimeSpan.FromHours(24))
            {
                // Keep the cheap installed probe for delayed Xbox resumes, but
                // old event-log transitions are no longer useful.
                continue;
            }

            if (!TryGetRecentState(
                    productId,
                    TimeSpan.FromMinutes(30),
                    out var observation))
            {
                continue;
            }

            var observationIsNew =
                observation.CreatedAtUtc >= status.UpdatedAtUtc - TimeSpan.FromSeconds(2);
            switch (observation.Kind)
            {
                case XboxInstallEventKind.Queued:
                case XboxInstallEventKind.Downloading:
                case XboxInstallEventKind.Paused:
                case XboxInstallEventKind.Finalizing:
                    if (!observationIsNew &&
                        status.Status is ("failed" or "action-required"))
                    {
                        break;
                    }

                    var nextStatus = observation.Kind switch
                    {
                        XboxInstallEventKind.Queued => "queued",
                        XboxInstallEventKind.Paused => "paused",
                        XboxInstallEventKind.Finalizing => "finalizing",
                        _ when observation.Stage.Equals(
                            "Reconnecting",
                            StringComparison.OrdinalIgnoreCase) =>
                            "reconnecting",
                        _ => "downloading",
                    };
                    var nextPercent = observation.Kind == XboxInstallEventKind.Finalizing
                        ? 99
                        : Math.Clamp(observation.ProgressPercent, 0, 99);
                    if (!string.Equals(status.Status, nextStatus, StringComparison.Ordinal) ||
                        status.ProgressPercent != nextPercent ||
                        status.WorkerProcessId <= 0 ||
                        !IsProcessRunning(status.WorkerProcessId))
                    {
                        var workerProcessId =
                            status.WorkerProcessId > 0 &&
                            IsProcessRunning(status.WorkerProcessId)
                                ? status.WorkerProcessId
                                : Environment.ProcessId;
                        UnifySteamDownloadStatusStore.Update(
                            "xbox-game-pass",
                            productId,
                            nextStatus,
                            nextPercent,
                            BuildDetail(observation, nextPercent),
                            workerProcessId);
                        changed = true;
                    }

                    break;
                case XboxInstallEventKind.Completed:
                    if (observationIsNew)
                    {
                        var liveWorkerOwnsCompletion =
                            status.WorkerProcessId > 0 &&
                            status.WorkerProcessId != Environment.ProcessId &&
                            IsProcessRunning(status.WorkerProcessId);
                        if (liveWorkerOwnsCompletion)
                        {
                            if (status.Status != "finalizing" ||
                                status.ProgressPercent != 99)
                            {
                                UnifySteamDownloadStatusStore.Update(
                                    "xbox-game-pass",
                                    productId,
                                    "finalizing",
                                    99,
                                    "Finalizing Xbox installation.",
                                    status.WorkerProcessId);
                                changed = true;
                            }

                            break;
                        }

                        var installationReady = false;
                        try
                        {
                            installationReady = isInstalled(productId);
                        }
                        catch (Exception exception)
                        {
                            Debug.WriteLine(
                                $"Xbox completion reconciliation failed: {exception.Message}");
                        }

                        if (installationReady)
                        {
                            UnifySteamDownloadStatusStore.Update(
                                "xbox-game-pass",
                                productId,
                                "completed",
                                100,
                                "Installed.",
                                workerProcessId: 0);
                        }
                        else if (status.Status != "finalizing" ||
                                 status.ProgressPercent != 99 ||
                                 status.WorkerProcessId <= 0 ||
                                 !IsProcessRunning(status.WorkerProcessId))
                        {
                            UnifySteamDownloadStatusStore.Update(
                                "xbox-game-pass",
                                productId,
                                "finalizing",
                                99,
                                "Finalizing Xbox installation.",
                                Environment.ProcessId);
                        }

                        changed = true;
                    }

                    break;
                case XboxInstallEventKind.Canceled:
                case XboxInstallEventKind.Failed:
                    if ((UnifySteamDownloadStatusStore.IsActivelyTransferring(status.Status) ||
                         status.Status == "paused") &&
                        observationIsNew &&
                        DateTimeOffset.UtcNow - observation.CreatedAtUtc >= TerminalGracePeriod)
                    {
                        UnifySteamDownloadStatusStore.Update(
                            "xbox-game-pass",
                            productId,
                            observation.Kind == XboxInstallEventKind.Canceled
                                ? "canceled"
                                : "failed",
                            observation.Kind == XboxInstallEventKind.Canceled
                                ? 0
                                : status.ProgressPercent,
                            observation.Kind == XboxInstallEventKind.Canceled
                                ? "Xbox download canceled. No failure occurred."
                                : "Windows reported that the Xbox installation failed.",
                            workerProcessId: 0);
                        changed = true;
                    }

                    break;
            }
        }

        return changed;
    }

    public static XboxInstallTrackingResult Track(
        string productId,
        long afterRecordId,
        Func<bool> isInstalled,
        Action<XboxInstallEventObservation> onProgress)
    {
        var cursor = Math.Max(0, afterRecordId);
        var startedAt = DateTimeOffset.UtcNow;
        var lastInstalledProbe = DateTimeOffset.MinValue;
        var lastProgress = 0;
        var lastPublishedSignature = string.Empty;
        DateTimeOffset? terminalAt = null;
        DateTimeOffset? completionAt = null;
        XboxInstallEventKind terminalKind = XboxInstallEventKind.Canceled;
        var acceptedProductIds = XboxProductRelationStore
            .GetRelatedProductIds(productId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (TryGetRecentState(productId, TimeSpan.FromMinutes(10), out var initial) &&
            initial.RecordId > cursor)
        {
            acceptedProductIds.UnionWith(
                XboxProductRelationStore.GetRelatedProductIds(productId));
            cursor = initial.RecordId;
            ApplyObservation(initial);
        }

        while (DateTimeOffset.UtcNow - startedAt < MaximumTrackingTime)
        {
            var batch = ReadAfter(productId, cursor, acceptedProductIds);
            cursor = Math.Max(cursor, batch.Cursor);
            foreach (var observation in batch.Observations)
            {
                ApplyObservation(observation);
            }

            var now = DateTimeOffset.UtcNow;
            if (now - lastInstalledProbe >= InstalledProbeInterval)
            {
                lastInstalledProbe = now;
                try
                {
                    if (isInstalled())
                    {
                        Publish(new XboxInstallEventObservation(
                            XboxInstallEventKind.Completed,
                            100,
                            "Installed",
                            cursor,
                            now));
                        return new XboxInstallTrackingResult(
                            true,
                            "Windows completed the Xbox installation.");
                    }
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Xbox installed-state probe failed: {exception.Message}");
                }
            }

            if (completionAt.HasValue &&
                now - completionAt.Value >= RegistrationTimeout)
            {
                return new XboxInstallTrackingResult(
                    false,
                    "Xbox finished downloading, but Gaming Services did not register the game as launchable.");
            }

            if (terminalAt.HasValue &&
                now - terminalAt.Value >= TerminalGracePeriod)
            {
                return new XboxInstallTrackingResult(
                    false,
                    terminalKind == XboxInstallEventKind.Canceled
                        ? "The Xbox installation was canceled."
                        : "Windows reported that the Xbox installation failed.",
                    Canceled: terminalKind == XboxInstallEventKind.Canceled);
            }

            Thread.Sleep(PollInterval);
        }

        return new XboxInstallTrackingResult(
            false,
            "The Xbox installation did not finish within 24 hours.");

        void ApplyObservation(XboxInstallEventObservation observation)
        {
            switch (observation.Kind)
            {
                case XboxInstallEventKind.Queued:
                case XboxInstallEventKind.Downloading:
                case XboxInstallEventKind.Paused:
                    terminalAt = null;
                    completionAt = null;
                    lastProgress = Math.Max(lastProgress, observation.ProgressPercent);
                    Publish(observation with { ProgressPercent = lastProgress });
                    break;
                case XboxInstallEventKind.Finalizing:
                    terminalAt = null;
                    completionAt ??= DateTimeOffset.UtcNow;
                    lastProgress = Math.Max(lastProgress, Math.Min(99, observation.ProgressPercent));
                    Publish(observation with { ProgressPercent = lastProgress });
                    break;
                case XboxInstallEventKind.Completed:
                    terminalAt = null;
                    completionAt ??= DateTimeOffset.UtcNow;
                    Publish(observation);
                    break;
                case XboxInstallEventKind.Canceled:
                case XboxInstallEventKind.Failed:
                    completionAt = null;
                    terminalAt ??= DateTimeOffset.UtcNow;
                    terminalKind = observation.Kind;
                    // Store/Gaming Services can replace one work item with
                    // another during handoff. Keep the last valid percentage
                    // visible during the grace window instead of flashing
                    // Retry Download.
                    Publish(new XboxInstallEventObservation(
                        XboxInstallEventKind.Downloading,
                        lastProgress,
                        "Reconnecting",
                        observation.RecordId,
                        observation.CreatedAtUtc));
                    break;
            }
        }

        void Publish(XboxInstallEventObservation observation)
        {
            var signature =
                $"{observation.Kind}|{observation.ProgressPercent}|{observation.Stage}";
            if (string.Equals(
                    signature,
                    lastPublishedSignature,
                    StringComparison.Ordinal))
            {
                return;
            }

            lastPublishedSignature = signature;
            try
            {
                onProgress(observation);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Xbox progress callback failed: {exception.Message}");
            }
        }
    }

    private static XboxInstallEventBatch ReadAfter(
        string productId,
        long afterRecordId,
        HashSet<string> acceptedProductIds)
    {
        var observations = new List<XboxInstallEventObservation>();
        var cursor = afterRecordId;
        try
        {
            var query = CreateQuery(reverseDirection: false, afterRecordId);
            using var reader = new EventLogReader(query);
            while (true)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                cursor = Math.Max(cursor, record.RecordId ?? 0);
                var snapshot = CreateSnapshot(record);
                if (TryExtractProductRelation(
                        snapshot.Message,
                        out var catalogProductId,
                        out var packageProductId) &&
                    string.Equals(
                        catalogProductId,
                        productId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    XboxProductRelationStore.Register(catalogProductId, packageProductId);
                    acceptedProductIds.Add(packageProductId);
                }

                if (TryParse(snapshot, acceptedProductIds, out var observation))
                {
                    observations.Add(observation);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Xbox event-log poll failed: {exception.Message}");
        }

        return new XboxInstallEventBatch(cursor, observations);
    }

    private static EventLogQuery CreateQuery(
        bool reverseDirection,
        long afterRecordId = 0)
    {
        var recordFilter = afterRecordId > 0
            ? $" and (EventRecordID > {afterRecordId.ToString(CultureInfo.InvariantCulture)})"
            : string.Empty;
        var xpath =
            $"*[System[Provider[@Name='{ProviderName}'] and " +
            $"(EventID={InstallAgentEventId}){recordFilter}]]";
        return new EventLogQuery(LogName, PathType.LogName, xpath)
        {
            ReverseDirection = reverseDirection,
            TolerateQueryErrors = true,
        };
    }

    internal static bool TryExtractProductRelation(
        string message,
        out string catalogProductId,
        out string packageProductId)
    {
        catalogProductId = string.Empty;
        packageProductId = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var packageMatch = CatalogPackageRegex().Match(message);
        var parentMatch = ParentBundleRegex().Match(message);
        if (!packageMatch.Success || !parentMatch.Success)
        {
            return false;
        }

        packageProductId = packageMatch.Groups["id"].Value.Trim();
        catalogProductId = parentMatch.Groups["id"].Value.Trim();
        return IsSafeProductId(catalogProductId) &&
               IsSafeProductId(packageProductId) &&
               !string.Equals(
                   catalogProductId,
                   packageProductId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static XboxInstallEventRecordSnapshot CreateSnapshot(EventRecord record)
    {
        var message = record.Properties.Count > 0
            ? Convert.ToString(
                record.Properties[0].Value,
                CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
        var createdAt = record.TimeCreated.HasValue
            ? new DateTimeOffset(record.TimeCreated.Value.ToUniversalTime(), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        return new XboxInstallEventRecordSnapshot(
            message,
            record.RecordId ?? 0,
            createdAt);
    }

    private static bool TryParse(
        XboxInstallEventRecordSnapshot record,
        IReadOnlySet<string> acceptedProductIds,
        out XboxInstallEventObservation observation)
    {
        observation = default!;
        var message = record.Message;
        if (string.IsNullOrWhiteSpace(message) ||
            !acceptedProductIds.Any(productId =>
                message.Contains(productId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var recordId = record.RecordId;
        var createdAt = record.CreatedAtUtc;

        var progressMatch = ProgressRegex().Match(message);
        if (MatchesProduct(progressMatch, acceptedProductIds) &&
            int.TryParse(
                progressMatch.Groups["percent"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var progressPercent))
        {
            var stage = progressMatch.Groups["stage"].Value.Trim();
            var kind = progressPercent >= 100 ||
                       stage.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
                       stage.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                ? XboxInstallEventKind.Finalizing
                : XboxInstallEventKind.Downloading;
            observation = new XboxInstallEventObservation(
                kind,
                Math.Clamp(progressPercent, 0, 100),
                stage,
                recordId,
                createdAt);
            return true;
        }

        var heartbeatMatch = HeartbeatRegex().Match(message);
        if (MatchesProduct(heartbeatMatch, acceptedProductIds) &&
            int.TryParse(
                heartbeatMatch.Groups["percent"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var heartbeatPercent))
        {
            observation = new XboxInstallEventObservation(
                XboxInstallEventKind.Downloading,
                Math.Clamp(heartbeatPercent, 0, 99),
                "DownloadingProduct",
                recordId,
                createdAt);
            return true;
        }

        var fulfillmentMatch = FulfillmentRegex().Match(message);
        if (MatchesProduct(fulfillmentMatch, acceptedProductIds) &&
            long.TryParse(
                fulfillmentMatch.Groups["result"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var fulfillmentResult))
        {
            observation = new XboxInstallEventObservation(
                fulfillmentResult == 0
                    ? XboxInstallEventKind.Completed
                    : fulfillmentResult == -2147467260
                        ? XboxInstallEventKind.Canceled
                        : XboxInstallEventKind.Failed,
                fulfillmentResult == 0 ? 100 : 0,
                fulfillmentResult == 0 ? "Completed" : "FulfillmentFailed",
                recordId,
                createdAt);
            return true;
        }

        var transitionMatch = StateTransitionRegex().Match(message);
        if (MatchesProduct(transitionMatch, acceptedProductIds))
        {
            var state = transitionMatch.Groups["state"].Value.Trim();
            var kind = state.ToLowerInvariant() switch
            {
                "working" => XboxInstallEventKind.Downloading,
                "paused" => XboxInstallEventKind.Paused,
                "completed" => XboxInstallEventKind.Completed,
                "canceled" or "cancelled" => XboxInstallEventKind.Canceled,
                "error" or "failed" => XboxInstallEventKind.Failed,
                _ => XboxInstallEventKind.Queued,
            };
            observation = new XboxInstallEventObservation(
                kind,
                kind == XboxInstallEventKind.Completed ? 100 : 0,
                state,
                recordId,
                createdAt);
            return true;
        }

        var canceledMatch = CanceledRegex().Match(message);
        if (MatchesProduct(canceledMatch, acceptedProductIds))
        {
            observation = new XboxInstallEventObservation(
                XboxInstallEventKind.Canceled,
                0,
                "Canceled",
                recordId,
                createdAt);
            return true;
        }

        return false;
    }

    private static bool MatchesProduct(
        Match match,
        IReadOnlySet<string> acceptedProductIds)
    {
        return match.Success &&
               acceptedProductIds.Contains(match.Groups["id"].Value);
    }

    private static bool IsSafeProductId(string productId)
    {
        return !string.IsNullOrWhiteSpace(productId) &&
               productId.All(character =>
                   char.IsLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
    }

    private static string BuildDetail(
        XboxInstallEventObservation observation,
        int percent)
    {
        return observation.Kind switch
        {
            XboxInstallEventKind.Queued => "Xbox download is queued.",
            XboxInstallEventKind.Paused => $"Xbox download paused at {percent}%.",
            XboxInstallEventKind.Finalizing => "Finalizing Xbox installation.",
            _ when observation.Stage.Equals(
                "Reconnecting",
                StringComparison.OrdinalIgnoreCase) =>
                $"Reconnecting to Xbox download at {percent}%.",
            _ when percent > 0 => $"Downloading Xbox game ({percent}%).",
            _ => "Xbox download is starting.",
        };
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
