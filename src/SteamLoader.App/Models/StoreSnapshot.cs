namespace SteamLoader.App.Models;

public sealed record StoreSnapshot(
    string StatusText,
    string? ErrorMessage,
    DateTimeOffset? RefreshedAtUtc,
    bool IsRefreshing,
    string SteamPersonaName,
    string SteamId64,
    bool WishlistAvailable,
    string CurrencyCode,
    string DisplayCurrencyCode,
    string StoreRegionCode,
    string StoreRegionName,
    string RegionalCurrencyCode,
    string RegionalCurrencySymbol,
    decimal? UsdPerEur,
    DateTimeOffset? ExchangeRateDateUtc,
    string PriceSource,
    IReadOnlyList<StoreGameState> Wishlist,
    IReadOnlyList<StoreGameState> Trending,
    IReadOnlyList<StoreGameState> FeaturedDeals,
    IReadOnlyList<StorePriceAlertState> Alerts)
{
    public int UnseenChangeCount { get; init; }

    public bool IncludeKeyshops { get; init; } = true;

    public bool NotificationsEnabled { get; init; } = true;

    public int RefreshIntervalMinutes { get; init; } = 30;

    public StoreArtworkCacheState ArtworkCache { get; init; } = StoreArtworkCacheState.Empty;
}

public sealed record StoreGameState(
    string Id,
    long? SteamAppId,
    string? PriceProviderGameId,
    string Title,
    string ImageUrl,
    string HeaderImageUrl,
    string FallbackImageUrl,
    decimal? CheapestPrice,
    decimal? RegularPrice,
    decimal? CheapestPriceEur,
    decimal? RegularPriceEur,
    decimal? RegionalPrice,
    decimal? RegionalRegularPrice,
    string RegionalCurrencyCode,
    int DiscountPercent,
    string CurrencyCode,
    string BestStoreName,
    string BestDealUrl,
    int? ReviewPercent,
    decimal DealRating,
    bool IsWishlisted,
    bool IsOnSale,
    string ReleaseText,
    IReadOnlyList<StoreOfferState> Offers)
{
    public bool IsSteamWishlisted { get; init; }

    public bool IsLocallyWishlisted { get; init; }

    public bool IsPinned { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public DateTimeOffset? AddedAtUtc { get; init; }

    public DateTimeOffset? PriceCheckedAtUtc { get; init; }

    public DateTimeOffset? TrackingStartedAtUtc { get; init; }

    public decimal? TrackingStartPrice { get; init; }

    public decimal? TrackingStartPriceEur { get; init; }

    public decimal? TrackedLowPrice { get; init; }

    public decimal? TrackedLowPriceEur { get; init; }

    public IReadOnlyList<StorePriceHistoryPoint> PriceHistory { get; init; } = [];

    public string ChangeKind { get; init; } = string.Empty;

    public DateTimeOffset? ChangedAtUtc { get; init; }

    public bool HasUnseenChange { get; init; }

    public string MatchConfidence { get; init; } = "exact";

    public string MatchNote { get; init; } = "Exact title and platform match";

    public bool IsUnreleased { get; init; }
}

public sealed record StoreOfferState(
    string StoreName,
    decimal Price,
    decimal RegularPrice,
    decimal? PriceEur,
    decimal? RegularPriceEur,
    decimal? RegionalPrice,
    decimal? RegionalRegularPrice,
    string RegionalCurrencyCode,
    int DiscountPercent,
    string CurrencyCode,
    string DealUrl,
    string DealId,
    bool IsBest)
{
    public string StoreKind { get; init; } = "official";

    public string MatchConfidence { get; init; } = "exact";

    public DateTimeOffset? CheckedAtUtc { get; init; }
}

public sealed record StorePriceAlertState(
    long SteamAppId,
    string GameId,
    string Title,
    decimal TargetPrice,
    string TargetCurrencyCode,
    decimal? CurrentPrice,
    decimal? CurrentPriceEur,
    decimal? OriginalPrice,
    decimal? OriginalPriceEur,
    DateTimeOffset? CreatedAtUtc,
    string CurrencyCode,
    bool Enabled,
    bool Reached,
    string DealUrl,
    string ImageUrl,
    IReadOnlyList<StorePriceHistoryPoint> PriceHistory)
{
    public string Mode { get; init; } = "price";

    public int TargetDiscountPercent { get; init; }

    public DateTimeOffset? SnoozedUntilUtc { get; init; }
}

public sealed record StorePriceHistoryPoint(
    DateTimeOffset RecordedAtUtc,
    decimal? Price,
    decimal? PriceEur);

public sealed record StorePriceAlertNotification(
    string Title,
    string Message,
    string DealUrl);

public sealed record StoreArtworkCacheState(
    int FileCount,
    long TotalBytes,
    int MaximumMegabytes,
    int RetentionDays)
{
    public static StoreArtworkCacheState Empty { get; } = new(0, 0, 256, 45);
}
