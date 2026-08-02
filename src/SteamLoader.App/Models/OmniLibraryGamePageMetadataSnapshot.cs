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
    string Warning,
    OmniLibraryGameInfoMetadata GameInfo,
    IReadOnlyList<OmniLibraryActivityMetadata> Activity,
    OmniLibraryAchievementMetadata Achievements,
    IReadOnlyList<OmniLibraryCommunityMetadata> Community);

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
