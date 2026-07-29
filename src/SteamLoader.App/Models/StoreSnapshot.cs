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
    IReadOnlyList<StorePriceAlertState> Alerts);

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
    bool IsBest);

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
    IReadOnlyList<StorePriceHistoryPoint> PriceHistory);

public sealed record StorePriceHistoryPoint(
    DateTimeOffset RecordedAtUtc,
    decimal? Price,
    decimal? PriceEur);

public sealed record StorePriceAlertNotification(
    string Title,
    string Message,
    string DealUrl);
