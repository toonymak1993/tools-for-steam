namespace SteamLoader.App.Models;

public sealed record StoreSyncPreviewState(
    int CreateCount,
    int RefreshCount,
    int AdoptCount,
    int SkipCount,
    int ExcludedCount,
    int CleanupCount,
    int DeferredCleanupCount,
    IReadOnlyList<StoreSyncPreviewItemState> Items);

public sealed record StoreSyncPreviewItemState(
    string Id,
    string Title,
    string StoreTitle,
    string SyncAction,
    string SyncDetail,
    string ArtworkState,
    uint TargetAppId,
    bool HasOverrides,
    bool HasArtworkMatchCache,
    IReadOnlyList<string> DebugLines);
