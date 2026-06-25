namespace SteamLoader.App.Models;

public sealed record StoreSyncStoreState(
    string Id,
    string Title,
    string Description,
    bool Enabled,
    bool IsReady,
    bool CanCleanupMissingTitles,
    string StatusText,
    string DetailText,
    string PathValue,
    bool SupportsCustomPath,
    bool SupportsAdditionalPaths,
    IReadOnlyList<string> AdditionalPaths,
    int AvailablePathCount,
    int MissingPathCount,
    int DetectedTitleCount,
    IReadOnlyList<StoreSyncDetectedTitleState> DetectedTitles);
