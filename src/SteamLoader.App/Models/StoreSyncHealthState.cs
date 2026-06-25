namespace SteamLoader.App.Models;

public sealed record StoreSyncHealthState(
    string Summary,
    string Detail,
    string Automation,
    int EnabledStoreCount,
    int ReadyStoreCount,
    int OfflineStoreCount,
    int DeferredCleanupCount,
    int WatcherCount,
    bool WatchersActive,
    int ConsecutiveAutomaticFailures,
    DateTimeOffset? LastAutomaticCheckAtUtc,
    DateTimeOffset? LastAutomaticTriggerAtUtc,
    string LastAutomaticTriggerSource,
    string LastJournalSummary);
