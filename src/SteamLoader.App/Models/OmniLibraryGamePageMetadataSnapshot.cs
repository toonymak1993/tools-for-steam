namespace SteamLoader.App.Models;

public sealed record OmniLibraryGamePageMetadataSnapshot(
    long Revision,
    uint SteamAppId,
    string StoreId,
    string GameId,
    string Title,
    string Status,
    bool Cached,
    bool Refreshing,
    DateTimeOffset? RefreshedAtUtc,
    DateTimeOffset? NextRefreshAtUtc,
    int? SourceSteamAppId,
    string SourceLabel,
    string MatchMode,
    string Warning,
    OmniLibraryGameInfoMetadata GameInfo,
    IReadOnlyList<OmniLibraryActivityMetadata> Activity,
    OmniLibraryAchievementMetadata Achievements,
    IReadOnlyList<OmniLibraryCommunityMetadata> Community)
{
    /// <summary>Game info fields the user replaced by hand.</summary>
    public IReadOnlyList<string> EditedFields { get; init; } = [];
}

/// <summary>
/// Hand-made game info for one game. A null field keeps the automatic value;
/// the edits always win over any metadata source.
/// </summary>
public sealed record GameMetadataEdits
{
    public string? Description { get; init; }

    public IReadOnlyList<string>? Developers { get; init; }

    public IReadOnlyList<string>? Publishers { get; init; }

    public IReadOnlyList<string>? Genres { get; init; }

    public string? ReleaseDate { get; init; }

    public int? Rating { get; init; }
}

/// <summary>What the metadata editor page shows for one game.</summary>
public sealed record GameMetadataEditorSnapshot(
    OmniLibraryGamePageMetadataSnapshot Metadata,
    OmniLibraryGameInfoMetadata Automatic,
    GameMetadataEdits Edits,
    bool ManagedByOmniLibrary,
    string IgnPage);

public sealed record OmniLibraryMetadataSearchResult(
    int SteamAppId,
    string Title,
    string ImageUrl,
    string Price,
    int MatchScore);

public sealed record SetOmniLibraryMetadataMatchRequest(
    int SteamAppId);

public sealed record OmniLibraryGameInfoMetadata(
    string ShortDescription,
    string Description,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Features,
    string ReleaseDate,
    int? Rating,
    string RatingLabel,
    string HeaderImageUrl,
    string StoreUrl,
    IReadOnlyList<OmniLibraryScreenshotMetadata> Screenshots);

public sealed record OmniLibraryScreenshotMetadata(
    string Id,
    string ThumbnailUrl,
    string FullImageUrl,
    string Caption);

public sealed record OmniLibraryActivityMetadata(
    string Id,
    string Title,
    string Summary,
    string Url,
    string ImageUrl,
    string Author,
    string FeedLabel,
    DateTimeOffset? PublishedAtUtc);

public sealed record OmniLibraryAchievementMetadata(
    string Provider,
    string Status,
    string DetailText,
    int UnlockedCount,
    int TotalCount,
    IReadOnlyList<OmniLibraryAchievementItemMetadata> Items);

public sealed record OmniLibraryAchievementItemMetadata(
    string Id,
    string Name,
    string Description,
    bool Unlocked,
    bool Hidden,
    DateTimeOffset? UnlockedAtUtc,
    string IconUrl,
    int CurrentProgress,
    int TargetProgress);

public sealed record OmniLibraryCommunityMetadata(
    string Id,
    string Kind,
    string Title,
    string Url,
    string ThumbnailUrl,
    string Source);
