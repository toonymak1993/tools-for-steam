using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Store;

public sealed class StoreService
{
    private static readonly TimeSpan MinimumRefreshAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OfferCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AlertFlatSampleInterval = TimeSpan.FromDays(1);
    private const int MaxAlertHistoryPoints = 180;
    private const long MaxArtworkFileBytes = 12L * 1024 * 1024;
    private const long MaxArtworkCacheBytes = 256L * 1024 * 1024;
    private const long ArtworkCacheTrimTargetBytes = 192L * 1024 * 1024;
    private static readonly TimeSpan ArtworkCacheLifetime = TimeSpan.FromDays(45);
    private static readonly HashSet<string> AllowedArtworkHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "shared.fastly.steamstatic.com",
        "cdn.cloudflare.steamstatic.com",
        "shared.akamai.steamstatic.com",
        "steamcdn-a.akamaihd.net",
        "cdn.steamgriddb.com",
        "cdn2.steamgriddb.com",
        "images.gog.com",
        "store-images.s-microsoft.com"
    };
    private static readonly string[] AllowedArtworkHostSuffixes =
    [
        ".gog-statics.com",
        ".epicgames.com",
        ".unrealengine.com",
        ".s-microsoft.com"
    ];
    private static readonly IReadOnlyDictionary<string, string> ArtworkExtensionsByContentType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
            ["image/gif"] = ".gif",
            ["image/avif"] = ".avif"
        };
    private const string DirectPriceSource =
        "Direct regional prices · Steam · GOG · Xbox · Epic Games · Instant Gaming · no API key";

    private readonly HttpClient _httpClient;
    private readonly DirectStoreCatalogClient _catalogClient;
    private readonly StoreSettingsStore _settingsStore;
    private readonly Func<SteamProfileInfo?> _steamProfileProvider;
    private readonly ProcessWindowService? _processWindowService;
    private readonly string? _artworkCacheDirectory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artworkCacheLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedOffers> _offerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<StoreOfferState>>>> _offerFetches =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoreGameState> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _backgroundGate = new();
    private Task<StoreSnapshot>? _backgroundRefresh;

    public StoreService(
        HttpClient httpClient,
        StoreSettingsStore settingsStore,
        Func<SteamProfileInfo?> steamProfileProvider,
        ProcessWindowService? processWindowService = null,
        string? artworkCacheDirectory = null)
    {
        _httpClient = httpClient;
        _catalogClient = new DirectStoreCatalogClient(httpClient);
        _settingsStore = settingsStore;
        _steamProfileProvider = steamProfileProvider;
        _processWindowService = processWindowService;
        _artworkCacheDirectory = string.IsNullOrWhiteSpace(artworkCacheDirectory)
            ? null
            : Path.GetFullPath(artworkCacheDirectory);
        TrimArtworkCache();
    }

    public event Action<StorePriceAlertNotification>? PriceAlertReached;

    public async Task<StoreArtworkCacheFile?> GetCachedArtworkAsync(
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        if (_artworkCacheDirectory is null || !TryNormalizeArtworkUri(sourceUrl, out var sourceUri))
            return null;

        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUri.AbsoluteUri)))
            .ToLowerInvariant();
        var cached = FindCachedArtwork(cacheKey);
        if (cached is not null)
        {
            TouchArtwork(cached.Path);
            return cached;
        }

        var cacheLock = _artworkCacheLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            cached = FindCachedArtwork(cacheKey);
            if (cached is not null)
            {
                TouchArtwork(cached.Path);
                return cached;
            }

            Directory.CreateDirectory(_artworkCacheDirectory);
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            request.Headers.UserAgent.TryParseAdd("ToolsForSteam/1.0");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode ||
                !TryNormalizeArtworkUri(response.RequestMessage?.RequestUri?.AbsoluteUri, out _))
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!ArtworkExtensionsByContentType.TryGetValue(contentType, out var extension) ||
                response.Content.Headers.ContentLength is > MaxArtworkFileBytes)
                return null;

            var targetPath = Path.Combine(_artworkCacheDirectory, cacheKey + extension);
            var temporaryPath = Path.Combine(_artworkCacheDirectory, $"{cacheKey}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920];
                    long totalBytes = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        totalBytes += read;
                        if (totalBytes > MaxArtworkFileBytes) return null;
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }

                    await output.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, targetPath, overwrite: true);
                TouchArtwork(targetPath);
                TrimArtworkCache(targetPath);
                return new StoreArtworkCacheFile(targetPath, contentType);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Debug.WriteLine($"Store artwork cache failed for {sourceUri}: {exception.Message}");
            return null;
        }
        finally
        {
            cacheLock.Release();
        }
    }

    public StoreSnapshot GetCachedSnapshot()
    {
        var configuration = _settingsStore.Load();
        return AttachAlerts(
            SanitizeCachedSnapshot(configuration.CachedSnapshot) ?? EmptySnapshot("Store data has not been loaded yet."),
            configuration);
    }

    public async Task<StoreSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var configuration = _settingsStore.Load();
        var cached = SanitizeCachedSnapshot(configuration.CachedSnapshot);
        var refreshAge = configuration.LastRefreshUtc.HasValue
            ? DateTimeOffset.UtcNow - configuration.LastRefreshUtc.Value
            : TimeSpan.MaxValue;

        if (!forceRefresh && cached is not null && refreshAge < GetRefreshInterval(configuration))
        {
            return AttachAlerts(cached with { IsRefreshing = false }, configuration);
        }

        if (!forceRefresh && cached is not null)
        {
            StartBackgroundRefresh();
            return AttachAlerts(cached with
            {
                IsRefreshing = true,
                StatusText = "Cached direct prices are visible while the stores refresh in the background."
            }, configuration);
        }

        return await RefreshAsync(cancellationToken);
    }

    public async Task<StoreSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = _settingsStore.Load();
            var cached = SanitizeCachedSnapshot(configuration.CachedSnapshot);
            var profile = _steamProfileProvider();
            var region = StoreRegionCatalog.Resolve(configuration.StoreRegionCode);
            var cachedMatchesRegion = cached?.StoreRegionCode.Equals(
                region.Code,
                StringComparison.OrdinalIgnoreCase) == true;
            var staleSavedGameIds = cachedMatchesRegion
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : configuration.SavedGames.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var exchangeRateTask = FetchUsdPerEurAsync(cancellationToken);
            var trendingTask = _catalogClient.FetchTrendingAsync(region, cancellationToken);
            IReadOnlyList<DirectStoreProduct> wishlistProducts = [];
            string? wishlistError = null;
            if (!string.IsNullOrWhiteSpace(profile?.SteamId64))
            {
                try
                {
                    wishlistProducts = await _catalogClient.FetchSteamWishlistAsync(profile.SteamId64, region, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    wishlistError = exception.Message;
                }
            }

            var trendingProducts = await trendingTask;
            var usdPerEur = await exchangeRateTask ?? cached?.UsdPerEur;
            var steamWishlistGames = wishlistError is null
                ? BuildWishlistGames(wishlistProducts, usdPerEur)
                : cached?.Wishlist.Where(game => game.IsSteamWishlisted ||
                    game.IsWishlisted && !game.IsLocallyWishlisted)
                    .Select(game => cachedMatchesRegion ? game : ClearGamePrices(game))
                    .ToArray() ?? [];
            var savedGames = await RefreshSavedGamesAsync(
                configuration.SavedGames.Values
                    .Select(game => cachedMatchesRegion ? game : ClearGamePrices(game))
                    .ToArray(),
                usdPerEur,
                region,
                cancellationToken);
            var wishlistGames = MergeWishlistGames(steamWishlistGames, savedGames);
            var trendingGames = trendingProducts.Count > 0
                ? BuildTrendingGames(trendingProducts, wishlistGames, usdPerEur)
                : cachedMatchesRegion ? cached?.Trending ?? [] : [];

            wishlistGames = await EnrichWishlistGamesAsync(
                wishlistGames,
                usdPerEur,
                region,
                cancellationToken);
            var featuredDeals = trendingGames
                .Where(game => game.DiscountPercent >= 35)
                .OrderByDescending(game => game.DiscountPercent)
                .ThenByDescending(game => game.DealRating)
                .ThenBy(game => game.CheapestPrice)
                .Take(18)
                .ToArray();

            var wishlistAvailable = wishlistError is null && !string.IsNullOrWhiteSpace(profile?.SteamId64);
            var snapshot = new StoreSnapshot(
                StatusText: BuildStatus(wishlistGames.Count, trendingGames.Count, wishlistError, profile),
                ErrorMessage: wishlistError,
                RefreshedAtUtc: DateTimeOffset.UtcNow,
                IsRefreshing: false,
                SteamPersonaName: profile?.PersonaName ?? string.Empty,
                SteamId64: profile?.SteamId64 ?? string.Empty,
                WishlistAvailable: wishlistAvailable,
                CurrencyCode: "USD",
                DisplayCurrencyCode: NormalizeDisplayCurrency(configuration.DisplayCurrencyCode),
                StoreRegionCode: region.Code,
                StoreRegionName: region.Name,
                RegionalCurrencyCode: region.CurrencyCode,
                RegionalCurrencySymbol: region.CurrencySymbol,
                UsdPerEur: usdPerEur,
                ExchangeRateDateUtc: usdPerEur.HasValue ? DateTimeOffset.UtcNow : cached?.ExchangeRateDateUtc,
                PriceSource: DirectPriceSource,
                Wishlist: wishlistGames,
                Trending: trendingGames,
                FeaturedDeals: featuredDeals,
                Alerts: []);

            var commit = _settingsStore.Update(latest =>
            {
                if (!StoreRegionCatalog.Resolve(latest.StoreRegionCode).Code.Equals(
                    region.Code,
                    StringComparison.OrdinalIgnoreCase))
                {
                    var current = SanitizeCachedSnapshot(latest.CachedSnapshot) ??
                        EmptySnapshot("The store region changed while prices were refreshing.");
                    return new RefreshCommit(AttachAlerts(current, latest), []);
                }

                var latestSavedGames = ReconcileSavedGames(
                    latest.SavedGames.Values,
                    wishlistGames,
                    staleSavedGameIds);
                var latestSteamGames = wishlistGames
                    .Where(game => game.IsSteamWishlisted)
                    .Select(game => game with
                    {
                        IsWishlisted = true,
                        IsLocallyWishlisted = false
                    })
                    .ToArray();
                var committedWishlist = MergeWishlistGames(latestSteamGames, latestSavedGames);
                var committedSnapshot = snapshot with
                {
                    DisplayCurrencyCode = NormalizeDisplayCurrency(latest.DisplayCurrencyCode),
                    StatusText = BuildStatus(committedWishlist.Count, trendingGames.Count, wishlistError, profile),
                    Wishlist = committedWishlist
                };

                latest.SavedGames = latestSavedGames.ToDictionary(
                    game => game.Id,
                    game => game,
                    StringComparer.OrdinalIgnoreCase);
                latest.LastRefreshUtc = committedSnapshot.RefreshedAtUtc;
                latest.CachedSnapshot = committedSnapshot;
                var notifications = UpdateAlertState(latest, committedSnapshot);
                return new RefreshCommit(AttachAlerts(committedSnapshot, latest), notifications);
            });

            foreach (var notification in commit.Notifications)
            {
                PriceAlertReached?.Invoke(notification);
            }

            return commit.Snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var configuration = _settingsStore.Load();
            var cached = SanitizeCachedSnapshot(configuration.CachedSnapshot);
            if (cached is not null)
            {
                return AttachAlerts(cached with
                {
                    IsRefreshing = false,
                    StatusText = "The last successful direct-price cache is shown.",
                    ErrorMessage = exception.Message
                }, configuration);
            }

            return EmptySnapshot("Store data could not be loaded.") with { ErrorMessage = exception.Message };
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<IReadOnlyList<StoreOfferState>> GetOffersAsync(
        string priceProviderGameId,
        CancellationToken cancellationToken)
    {
        var normalizedId = (priceProviderGameId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedId)) return [];

        var configuration = _settingsStore.Load();
        var region = StoreRegionCatalog.Resolve(configuration.StoreRegionCode);
        var snapshot = SanitizeCachedSnapshot(configuration.CachedSnapshot);
        var game = FindGame(snapshot, normalizedId);
        if (game is null) _searchCache.TryGetValue(normalizedId, out game);
        if (game is null) return [];
        if (snapshot is not null &&
            !snapshot.StoreRegionCode.Equals(region.Code, StringComparison.OrdinalIgnoreCase))
        {
            game = ClearGamePrices(game);
        }

        var offers = await GetOrFetchOffersAsync(game, snapshot?.UsdPerEur, region, cancellationToken);
        PersistUpdatedGameOffers(normalizedId, offers, region.Code);
        return offers;
    }

    public async Task<IReadOnlyList<StoreGameState>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length < 2)
            throw new InvalidOperationException("Enter at least two characters to search the stores.");
        if (normalizedQuery.Length > 100)
            throw new InvalidOperationException("The store search is limited to 100 characters.");

        var configuration = _settingsStore.Load();
        var region = StoreRegionCatalog.Resolve(configuration.StoreRegionCode);
        var snapshot = SanitizeCachedSnapshot(configuration.CachedSnapshot);
        var products = await _catalogClient.SearchAsync(normalizedQuery, region, cancellationToken);
        var requested = DirectStoreCatalogClient.NormalizeTitle(normalizedQuery);
        var wishlist = snapshot?.Wishlist ?? [];
        var savedIds = configuration.SavedGames.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedTitles = configuration.SavedGames.Values
            .Select(game => DirectStoreCatalogClient.NormalizeTitle(game.Title))
            .ToHashSet(StringComparer.Ordinal);
        var steamIds = wishlist
            .Where(game => game.IsSteamWishlisted && game.SteamAppId is > 0)
            .Select(game => game.SteamAppId!.Value)
            .ToHashSet();

        var games = products
            .Where(product => !string.IsNullOrWhiteSpace(product.Title))
            .GroupBy(product => DirectStoreCatalogClient.NormalizeTitle(product.Title), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .GroupBy(product => $"{product.StoreName}:{product.ProductId}", StringComparer.OrdinalIgnoreCase)
                .Select(values => values.First())
                .ToArray())
            .Select(group =>
            {
                var preview = BuildGame(group, false, false, snapshot?.UsdPerEur);
                var isLocal = savedIds.Contains(preview.Id) || savedTitles.Contains(DirectStoreCatalogClient.NormalizeTitle(preview.Title));
                var isSteam = preview.SteamAppId is > 0 && steamIds.Contains(preview.SteamAppId.Value);
                return preview with
                {
                    IsWishlisted = isLocal || isSteam,
                    IsLocallyWishlisted = isLocal,
                    IsSteamWishlisted = isSteam
                };
            })
            .OrderBy(game => SearchRank(DirectStoreCatalogClient.NormalizeTitle(game.Title), requested))
            .ThenByDescending(game => game.DealRating)
            .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(40)
            .ToArray();

        foreach (var game in games)
        {
            _searchCache[game.Id] = game;
        }

        return games;
    }

    public StoreSnapshot SetLocalWishlist(StoreGameState game, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(game.Id) || game.Id.Length > 200 ||
            string.IsNullOrWhiteSpace(game.Title) || game.Title.Length > 240)
        {
            throw new InvalidOperationException("The selected store result is not valid.");
        }

        var normalized = SanitizeCachedGame(game) with
        {
            IsWishlisted = enabled || game.IsSteamWishlisted,
            IsLocallyWishlisted = enabled
        };
        _searchCache[normalized.Id] = normalized;
        return _settingsStore.Update(configuration =>
        {
            var normalizedTitle = DirectStoreCatalogClient.NormalizeTitle(normalized.Title);
            var matchingSavedKeys = configuration.SavedGames
                .Where(item => item.Key.Equals(normalized.Id, StringComparison.OrdinalIgnoreCase) ||
                    DirectStoreCatalogClient.NormalizeTitle(item.Value.Title).Equals(normalizedTitle, StringComparison.Ordinal))
                .Select(item => item.Key)
                .ToArray();
            foreach (var key in matchingSavedKeys) configuration.SavedGames.Remove(key);
            if (enabled)
            {
                configuration.SavedGames[normalized.Id] = normalized;
            }

            var cached = SanitizeCachedSnapshot(configuration.CachedSnapshot) ?? EmptySnapshot("TFS wishlist updated.");
            var wishlist = cached.Wishlist
                .Where(item => !item.Id.Equals(normalized.Id, StringComparison.OrdinalIgnoreCase) &&
                    !DirectStoreCatalogClient.NormalizeTitle(item.Title).Equals(normalizedTitle, StringComparison.Ordinal))
                .ToList();
            var existingSteamGame = cached.Wishlist.FirstOrDefault(item =>
                (item.Id.Equals(normalized.Id, StringComparison.OrdinalIgnoreCase) ||
                 DirectStoreCatalogClient.NormalizeTitle(item.Title).Equals(normalizedTitle, StringComparison.Ordinal)) &&
                item.IsSteamWishlisted);
            if (enabled)
            {
                wishlist.Add(existingSteamGame is null
                    ? normalized
                    : MergeWishlistGames([existingSteamGame], [normalized]).Single());
            }
            else if (existingSteamGame is not null)
            {
                wishlist.Add(existingSteamGame with { IsWishlisted = true, IsLocallyWishlisted = false });
            }

            configuration.CachedSnapshot = cached with
            {
                StatusText = enabled ? $"{normalized.Title} was added to the TFS wishlist." : $"{normalized.Title} was removed from the TFS wishlist.",
                Wishlist = wishlist
                    .OrderByDescending(item => item.IsOnSale)
                    .ThenByDescending(item => item.DiscountPercent)
                    .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()
            };
            return AttachAlerts(configuration.CachedSnapshot, configuration);
        });
    }

    public StoreSnapshot SetAlert(
        long steamAppId,
        string title,
        decimal targetPrice,
        string currencyCode,
        bool enabled) =>
        SetAlert(steamAppId, null, title, targetPrice, currencyCode, enabled);

    public StoreSnapshot SetAlert(
        long? steamAppId,
        string? gameId,
        string title,
        decimal targetPrice,
        string currencyCode,
        bool enabled)
    {
        var normalizedSteamAppId = steamAppId is > 0 ? steamAppId.Value : 0;
        var normalizedGameId = NormalizeAlertGameId(gameId, normalizedSteamAppId);
        if (normalizedSteamAppId <= 0 && string.IsNullOrWhiteSpace(normalizedGameId))
            throw new InvalidOperationException("A valid wishlist game is required for a price alert.");
        if (enabled && (targetPrice <= 0 || targetPrice > 10000))
            throw new InvalidOperationException("Choose a target price greater than 0.");

        var storageKey = normalizedSteamAppId > 0
            ? normalizedSteamAppId
            : CreateLocalAlertStorageKey(normalizedGameId);
        return _settingsStore.Update(configuration =>
        {
            if (!enabled)
            {
                configuration.Alerts.Remove(storageKey);
            }
            else
            {
                var normalizedCurrency = currencyCode?.Trim().ToUpperInvariant() == "USD" ? "USD" : "EUR";
                configuration.Alerts.TryGetValue(storageKey, out var alert);
                alert ??= new StorePriceAlertData();
                alert.SteamAppId = normalizedSteamAppId;
                alert.GameId = normalizedGameId;
                alert.Title = string.IsNullOrWhiteSpace(title)
                    ? normalizedSteamAppId > 0 ? $"Steam app {normalizedSteamAppId}" : "TFS wishlist game"
                    : title.Trim();
                alert.TargetPrice = decimal.Round(targetPrice, 2);
                alert.CurrencyCode = normalizedCurrency;
                alert.Enabled = true;
                alert.LastNotifiedPrice = null;
                alert.WasReached = false;
                var currentGame = SanitizeCachedSnapshot(configuration.CachedSnapshot)?.Wishlist
                    .FirstOrDefault(game => AlertMatchesGame(alert, game));
                TrackAlertPrice(alert, currentGame, DateTimeOffset.UtcNow);
                configuration.Alerts[storageKey] = alert;
            }

            return AttachAlerts(
                SanitizeCachedSnapshot(configuration.CachedSnapshot) ?? EmptySnapshot("Price alert saved."),
                configuration);
        });
    }

    public StoreSnapshot SetDisplayCurrency(string currencyCode)
    {
        return _settingsStore.Update(configuration =>
        {
            configuration.DisplayCurrencyCode = NormalizeDisplayCurrency(currencyCode);
            var cached = SanitizeCachedSnapshot(configuration.CachedSnapshot);
            if (cached is not null)
            {
                configuration.CachedSnapshot = cached with { DisplayCurrencyCode = configuration.DisplayCurrencyCode };
            }

            return AttachAlerts(
                configuration.CachedSnapshot ?? EmptySnapshot("Currency preference saved.") with
                {
                    DisplayCurrencyCode = configuration.DisplayCurrencyCode
                },
                configuration);
        });
    }

    public void SetStoreRegion(string regionCode)
    {
        var region = StoreRegionCatalog.Resolve(regionCode);
        _settingsStore.Update(configuration =>
        {
            configuration.StoreRegionCode = region.Code;
            configuration.DisplayCurrencyCode = "REGION";
            configuration.LastRefreshUtc = null;
            configuration.SavedGames = configuration.SavedGames.ToDictionary(
                item => item.Key,
                item => ClearGamePrices(item.Value),
                StringComparer.OrdinalIgnoreCase);
            var cached = SanitizeCachedSnapshot(configuration.CachedSnapshot);
            if (cached is not null)
            {
                configuration.CachedSnapshot = cached with
                {
                    StatusText = $"Refreshing direct prices for {region.Name}.",
                    RefreshedAtUtc = null,
                    IsRefreshing = true,
                    DisplayCurrencyCode = "REGION",
                    StoreRegionCode = region.Code,
                    StoreRegionName = region.Name,
                    RegionalCurrencyCode = region.CurrencyCode,
                    RegionalCurrencySymbol = region.CurrencySymbol,
                    Wishlist = cached.Wishlist.Select(ClearGamePrices).ToArray(),
                    Trending = [],
                    FeaturedDeals = []
                };
            }
            return true;
        });
        _offerCache.Clear();
        _offerFetches.Clear();
        _searchCache.Clear();
    }

    public void OpenDeal(string dealUrl)
    {
        if (!TryNormalizeAllowedDealUrl(dealUrl, out var allowedUrl))
            throw new InvalidOperationException("This link is not a supported direct store destination.");

        var windowsBeforeLaunch = _processWindowService?.GetSnapshot().Windows ?? [];
        Process.Start(new ProcessStartInfo(allowedUrl) { UseShellExecute = true })?.Dispose();
        _processWindowService?.ActivateUrlHandlerWhenReady(windowsBeforeLaunch);
    }

    public async Task RunRefreshLoopAsync(Func<bool> isEnabled, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (isEnabled())
            {
                var configuration = _settingsStore.Load();
                var stale = !configuration.LastRefreshUtc.HasValue ||
                    DateTimeOffset.UtcNow - configuration.LastRefreshUtc.Value >= GetRefreshInterval(configuration);
                if (stale) await RefreshAsync(cancellationToken);
            }

            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        }
    }

    private void StartBackgroundRefresh()
    {
        lock (_backgroundGate)
        {
            if (_backgroundRefresh is { IsCompleted: false }) return;
            _backgroundRefresh = Task.Run(() => RefreshAsync(CancellationToken.None));
        }
    }

    private async Task<IReadOnlyList<StoreOfferState>> GetOrFetchOffersAsync(
        StoreGameState game,
        decimal? usdPerEur,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildOfferCacheKey(region.Code, game.PriceProviderGameId ?? game.Id);
        if (_offerCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAtUtc < OfferCacheLifetime)
        {
            return cached.Offers;
        }

        var created = new Lazy<Task<IReadOnlyList<StoreOfferState>>>(
            async () =>
            {
                var products = await _catalogClient.FetchOffersAsync(
                    game.SteamAppId,
                    game.Title,
                    region,
                    CancellationToken.None);
                var offers = MergeOffers(game.Offers, products, usdPerEur);
                _offerCache[cacheKey] = new CachedOffers(DateTimeOffset.UtcNow, offers);
                return offers;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        var fetch = _offerFetches.GetOrAdd(cacheKey, created);
        if (ReferenceEquals(fetch, created))
        {
            _ = RemoveOfferFetchWhenCompletedAsync(cacheKey, fetch);
        }

        return await fetch.Value.WaitAsync(cancellationToken);
    }

    private async Task RemoveOfferFetchWhenCompletedAsync(
        string cacheKey,
        Lazy<Task<IReadOnlyList<StoreOfferState>>> fetch)
    {
        try
        {
            await fetch.Value;
        }
        catch
        {
            // The original caller observes the storefront error. This continuation
            // only releases the coalescing slot so a later request can retry.
        }
        finally
        {
            if (_offerFetches.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, fetch))
            {
                _offerFetches.TryRemove(cacheKey, out _);
            }
        }
    }

    private void PersistUpdatedGameOffers(
        string gameId,
        IReadOnlyList<StoreOfferState> offers,
        string regionCode)
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var notifications = _settingsStore.Update(configuration =>
        {
            if (!StoreRegionCatalog.Resolve(configuration.StoreRegionCode).Code.Equals(
                regionCode,
                StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<StorePriceAlertNotification>();
            }

            var snapshot = SanitizeCachedSnapshot(configuration.CachedSnapshot);
            var game = FindGame(snapshot, gameId);
            if (snapshot is null || game is null)
            {
                return Array.Empty<StorePriceAlertNotification>();
            }

            var updated = ApplyBestOffer(game, offers);
            var updatedSnapshot = ReplaceGame(snapshot, updated);
            configuration.CachedSnapshot = updatedSnapshot;
            configuration.SavedGames = configuration.SavedGames.ToDictionary(
                item => item.Key,
                item => GamesMatch(item.Value, updated)
                    ? MergeUpdatedGame(item.Value, updated)
                    : item.Value,
                StringComparer.OrdinalIgnoreCase);
            return UpdateAlertState(configuration, updatedSnapshot, observedAtUtc).ToArray();
        });

        foreach (var notification in notifications)
        {
            PriceAlertReached?.Invoke(notification);
        }
    }

    private static string BuildOfferCacheKey(string regionCode, string gameId) =>
        $"{StoreRegionCatalog.Resolve(regionCode).Code}:{gameId.Trim()}";

    private static IReadOnlyList<StoreGameState> ReconcileSavedGames(
        IEnumerable<StoreGameState> latestSavedGames,
        IReadOnlyList<StoreGameState> refreshedWishlist,
        IReadOnlySet<string> staleSavedGameIds)
    {
        return latestSavedGames
            .Select(saved =>
            {
                var refreshed = refreshedWishlist.FirstOrDefault(game => GamesMatch(saved, game));
                var current = refreshed?.Offers.Count > 0
                    ? MergeUpdatedGame(saved, refreshed)
                    : staleSavedGameIds.Contains(saved.Id) ? ClearGamePrices(saved) : saved;
                return current with { IsWishlisted = true, IsLocallyWishlisted = true };
            })
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static StoreSnapshot ReplaceGame(StoreSnapshot snapshot, StoreGameState updated) =>
        snapshot with
        {
            Wishlist = ReplaceGame(snapshot.Wishlist, updated),
            Trending = ReplaceGame(snapshot.Trending, updated),
            FeaturedDeals = ReplaceGame(snapshot.FeaturedDeals, updated)
        };

    private static IReadOnlyList<StoreGameState> ReplaceGame(
        IReadOnlyList<StoreGameState> games,
        StoreGameState updated) =>
        games.Select(game => GamesMatch(game, updated) ? MergeUpdatedGame(game, updated) : game).ToArray();

    private static StoreGameState MergeUpdatedGame(StoreGameState current, StoreGameState updated)
    {
        var merged = ApplyBestOffer(current, updated.Offers);
        return merged with
        {
            ImageUrl = FirstNotEmpty([updated.ImageUrl, current.ImageUrl]),
            HeaderImageUrl = FirstNotEmpty([updated.HeaderImageUrl, current.HeaderImageUrl]),
            FallbackImageUrl = FirstNotEmpty([updated.FallbackImageUrl, current.FallbackImageUrl]),
            IsWishlisted = current.IsWishlisted,
            IsSteamWishlisted = current.IsSteamWishlisted,
            IsLocallyWishlisted = current.IsLocallyWishlisted
        };
    }

    private static StoreGameState ClearGamePrices(StoreGameState game) => game with
    {
        CheapestPrice = null,
        RegularPrice = null,
        CheapestPriceEur = null,
        RegularPriceEur = null,
        RegionalPrice = null,
        RegionalRegularPrice = null,
        RegionalCurrencyCode = string.Empty,
        DiscountPercent = 0,
        BestStoreName = string.Empty,
        BestDealUrl = string.Empty,
        IsOnSale = false,
        Offers = []
    };

    private static bool GamesMatch(StoreGameState left, StoreGameState right)
    {
        if (left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(left.PriceProviderGameId) &&
            left.PriceProviderGameId.Equals(right.PriceProviderGameId, StringComparison.OrdinalIgnoreCase)) return true;
        if (left.SteamAppId is > 0 && left.SteamAppId == right.SteamAppId) return true;
        return DirectStoreCatalogClient.NormalizeTitle(left.Title)
            .Equals(DirectStoreCatalogClient.NormalizeTitle(right.Title), StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<StoreGameState>> EnrichWishlistGamesAsync(
        IReadOnlyList<StoreGameState> games,
        decimal? usdPerEur,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        if (games.Count == 0) return games;
        var enriched = new ConcurrentDictionary<string, StoreGameState>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            games,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (game, token) =>
            {
                try
                {
                    var offers = await GetOrFetchOffersAsync(game, usdPerEur, region, token);
                    enriched[game.Id] = ApplyBestOffer(game, offers);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    enriched[game.Id] = game;
                }
            });

        return games.Select(game => enriched.TryGetValue(game.Id, out var value)
                ? value
                : game)
            .ToArray();
    }

    private async Task<IReadOnlyList<StoreGameState>> RefreshSavedGamesAsync(
        IReadOnlyList<StoreGameState> savedGames,
        decimal? usdPerEur,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        if (savedGames.Count == 0) return [];

        var refreshed = new ConcurrentDictionary<string, StoreGameState>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            savedGames,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (saved, token) =>
            {
                var current = saved;
                try
                {
                    var offers = await GetOrFetchOffersAsync(saved, usdPerEur, region, token);
                    current = ApplyBestOffer(saved, offers);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A saved game remains visible with its last direct offers
                    // when one storefront is temporarily unavailable.
                }

                current = current with { IsWishlisted = true, IsLocallyWishlisted = true };
                refreshed[current.Id] = current;
            });

        return refreshed.Values.ToArray();
    }

    private static IReadOnlyList<StoreGameState> MergeWishlistGames(
        IReadOnlyList<StoreGameState> steamGames,
        IReadOnlyList<StoreGameState> savedGames)
    {
        return steamGames.Concat(savedGames)
            .GroupBy(game => DirectStoreCatalogClient.NormalizeTitle(game.Title), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var values = group.ToArray();
                var primary = values
                    .OrderByDescending(game => game.IsSteamWishlisted)
                    .ThenByDescending(game => game.SteamAppId.HasValue)
                    .ThenByDescending(game => game.DealRating)
                    .First();
                var offers = MarkBest(values
                    .SelectMany(game => game.Offers)
                    .Where(offer => TryNormalizeAllowedDealUrl(offer.DealUrl, out _))
                    .GroupBy(offer => offer.DealId, StringComparer.OrdinalIgnoreCase)
                    .Select(offers => offers.OrderBy(offer => offer.Price).First()));
                var merged = ApplyBestOffer(primary, offers);
                return merged with
                {
                    ImageUrl = FirstNotEmpty(values.Select(game => game.ImageUrl)),
                    HeaderImageUrl = FirstNotEmpty(values.Select(game => game.HeaderImageUrl)),
                    FallbackImageUrl = FirstNotEmpty(values.Select(game => game.FallbackImageUrl)),
                    IsWishlisted = true,
                    IsSteamWishlisted = values.Any(game => game.IsSteamWishlisted),
                    IsLocallyWishlisted = values.Any(game => game.IsLocallyWishlisted)
                };
            })
            .OrderByDescending(game => game.IsOnSale)
            .ThenByDescending(game => game.DiscountPercent)
            .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<StoreGameState> BuildWishlistGames(
        IReadOnlyList<DirectStoreProduct> products,
        decimal? usdPerEur)
    {
        return products
            .Where(product => product.SteamAppId is > 0)
            .Select(product => BuildGame([product], true, false, usdPerEur))
            .OrderByDescending(game => game.IsOnSale)
            .ThenByDescending(game => game.DiscountPercent)
            .ThenBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<StoreGameState> BuildTrendingGames(
        IReadOnlyList<DirectStoreProduct> products,
        IReadOnlyList<StoreGameState> wishlist,
        decimal? usdPerEur)
    {
        var wishlistIds = wishlist
            .Where(game => game.SteamAppId is > 0)
            .Select(game => game.SteamAppId!.Value)
            .ToHashSet();
        var localIds = wishlist
            .Where(game => game.IsLocallyWishlisted)
            .Select(game => game.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localTitles = wishlist
            .Where(game => game.IsLocallyWishlisted)
            .Select(game => DirectStoreCatalogClient.NormalizeTitle(game.Title))
            .ToHashSet(StringComparer.Ordinal);

        return products
            .Where(product => !string.IsNullOrWhiteSpace(product.Title))
            .GroupBy(product => DirectStoreCatalogClient.NormalizeTitle(product.Title), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group
                .GroupBy(product => $"{product.StoreName}:{product.ProductId}", StringComparer.OrdinalIgnoreCase)
                .Select(values => values.First())
                .ToArray())
            .Select(group =>
            {
                var steamWishlisted = group.Any(product => product.SteamAppId is > 0 && wishlistIds.Contains(product.SteamAppId.Value));
                var preview = BuildGame(group, steamWishlisted, false, usdPerEur);
                var locallyWishlisted = localIds.Contains(preview.Id) ||
                    localTitles.Contains(DirectStoreCatalogClient.NormalizeTitle(preview.Title));
                return preview with
                {
                    IsWishlisted = steamWishlisted || locallyWishlisted,
                    IsLocallyWishlisted = locallyWishlisted
                };
            })
            .OrderByDescending(game => game.DealRating)
            .ThenByDescending(game => game.DiscountPercent)
            .ThenBy(game => game.CheapestPrice)
            .Take(36)
            .ToArray();
    }

    private static StoreGameState BuildGame(
        IReadOnlyList<DirectStoreProduct> products,
        bool isSteamWishlisted,
        bool isLocallyWishlisted,
        decimal? usdPerEur)
    {
        var primary = products
            .OrderByDescending(product => product.SteamAppId.HasValue)
            .ThenByDescending(product => product.Popularity)
            .First();
        var steamAppId = products.Select(product => product.SteamAppId).FirstOrDefault(value => value is > 0);
        var offers = MarkBest(products
            .Select(product => MapOffer(product, usdPerEur))
            .OfType<StoreOfferState>()
            .GroupBy(offer => offer.DealId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(offer => offer.RegionalPrice ?? offer.Price).First())
            .ToArray());
        var best = offers.FirstOrDefault();
        var image = ResolveImage(steamAppId, products.Select(product => product.ImageUrl), poster: true);
        var header = ResolveImage(steamAppId, products.Select(product => product.HeaderImageUrl), poster: false);
        var id = steamAppId is > 0
            ? $"steam:{steamAppId.Value}"
            : $"direct:{primary.StoreName.ToLowerInvariant().Replace(' ', '-')}:{primary.ProductId}";
        return new StoreGameState(
            Id: id,
            SteamAppId: steamAppId,
            PriceProviderGameId: id,
            Title: primary.Title,
            ImageUrl: image,
            HeaderImageUrl: header,
            FallbackImageUrl: FirstNotEmpty(products.Select(product => product.ImageUrl)),
            CheapestPrice: best?.Price,
            RegularPrice: best?.RegularPrice,
            CheapestPriceEur: best?.PriceEur,
            RegularPriceEur: best?.RegularPriceEur,
            RegionalPrice: best?.RegionalPrice,
            RegionalRegularPrice: best?.RegionalRegularPrice,
            RegionalCurrencyCode: best?.RegionalCurrencyCode ?? primary.RegionalCurrencyCode,
            DiscountPercent: best?.DiscountPercent ?? primary.DiscountPercent,
            CurrencyCode: "USD",
            BestStoreName: best?.StoreName ?? primary.StoreName,
            BestDealUrl: best?.DealUrl ?? primary.DirectUrl,
            ReviewPercent: products.Select(product => product.ReviewPercent).FirstOrDefault(value => value.HasValue),
            DealRating: products.Max(product => product.Popularity),
            IsWishlisted: isSteamWishlisted || isLocallyWishlisted,
            IsOnSale: best is not null &&
                (best.RegionalRegularPrice ?? best.RegularPrice) > (best.RegionalPrice ?? best.Price),
            ReleaseText: products.Select(product => product.ReleaseText).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            Offers: offers)
        {
            IsSteamWishlisted = isSteamWishlisted,
            IsLocallyWishlisted = isLocallyWishlisted
        };
    }

    private static StoreOfferState? MapOffer(DirectStoreProduct product, decimal? usdPerEur)
    {
        if (!TryNormalizeAllowedDealUrl(product.DirectUrl, out var directUrl)) return null;
        var priceUsd = product.PriceUsd ?? ConvertEurToUsd(product.PriceEur, usdPerEur);
        var priceEur = product.PriceEur ?? ConvertUsdToEur(product.PriceUsd, usdPerEur);
        if (!priceUsd.HasValue && !priceEur.HasValue) return null;
        var resolvedPriceUsd = priceUsd ?? priceEur!.Value;
        var regularUsd = product.RegularPriceUsd ?? ConvertEurToUsd(product.RegularPriceEur, usdPerEur) ?? resolvedPriceUsd;
        var regularEur = product.RegularPriceEur ?? ConvertUsdToEur(product.RegularPriceUsd, usdPerEur) ?? priceEur;
        var regionalDiscount = product.RegularPriceRegional is > 0 &&
            product.PriceRegional < product.RegularPriceRegional
                ? Math.Clamp((int)decimal.Round(
                    (product.RegularPriceRegional.Value - product.PriceRegional!.Value) /
                    product.RegularPriceRegional.Value * 100m), 0, 100)
                : (int?)null;
        var discount = regionalDiscount ?? (regularUsd > 0 && resolvedPriceUsd < regularUsd
            ? Math.Clamp((int)decimal.Round((regularUsd - resolvedPriceUsd) / regularUsd * 100m), 0, 100)
            : product.DiscountPercent);
        return new StoreOfferState(
            product.StoreName,
            decimal.Round(resolvedPriceUsd, 2),
            decimal.Round(regularUsd, 2),
            priceEur.HasValue ? decimal.Round(priceEur.Value, 2) : null,
            regularEur.HasValue ? decimal.Round(regularEur.Value, 2) : null,
            product.PriceRegional.HasValue ? decimal.Round(product.PriceRegional.Value, 2) : null,
            product.RegularPriceRegional.HasValue ? decimal.Round(product.RegularPriceRegional.Value, 2) : null,
            product.RegionalCurrencyCode,
            discount,
            "USD",
            directUrl,
            $"{product.StoreName}:{product.ProductId}",
            false);
    }

    private static IReadOnlyList<StoreOfferState> MergeOffers(
        IReadOnlyList<StoreOfferState> existing,
        IReadOnlyList<DirectStoreProduct> products,
        decimal? usdPerEur)
    {
        var incoming = products.Select(product => MapOffer(product, usdPerEur)).OfType<StoreOfferState>().ToArray();
        var incomingDealIds = incoming.Select(offer => offer.DealId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MarkBest(existing
            .Where(offer => TryNormalizeAllowedDealUrl(offer.DealUrl, out _) &&
                !incomingDealIds.Contains(offer.DealId))
            .Concat(incoming)
            .GroupBy(offer => offer.DealId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(offer => offer.Price).First())
            .ToArray());
    }

    private static IReadOnlyList<StoreOfferState> MarkBest(IEnumerable<StoreOfferState> offers)
    {
        return offers
            .OrderBy(offer => offer.Price)
            .ThenByDescending(offer => offer.DiscountPercent)
            .Select((offer, index) => offer with { IsBest = index == 0 })
            .ToArray();
    }

    private static StoreGameState ApplyBestOffer(StoreGameState game, IReadOnlyList<StoreOfferState> offers)
    {
        var best = offers.FirstOrDefault();
        if (best is null) return game with { Offers = offers };
        return game with
        {
            CheapestPrice = best.Price,
            RegularPrice = best.RegularPrice,
            CheapestPriceEur = best.PriceEur,
            RegularPriceEur = best.RegularPriceEur,
            RegionalPrice = best.RegionalPrice,
            RegionalRegularPrice = best.RegionalRegularPrice,
            RegionalCurrencyCode = best.RegionalCurrencyCode,
            DiscountPercent = best.DiscountPercent,
            BestStoreName = best.StoreName,
            BestDealUrl = best.DealUrl,
            IsOnSale = (best.RegionalRegularPrice ?? best.RegularPrice) > (best.RegionalPrice ?? best.Price),
            Offers = offers
        };
    }

    private static StoreGameState? FindGame(StoreSnapshot? snapshot, string id)
    {
        if (snapshot is null) return null;
        return snapshot.Wishlist.Concat(snapshot.Trending).Concat(snapshot.FeaturedDeals)
            .FirstOrDefault(game => game.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                game.PriceProviderGameId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task<decimal?> FetchUsdPerEurAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(
                "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml",
                cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            var usdRate = document.Descendants()
                .Select(element => new
                {
                    Currency = element.Attributes().FirstOrDefault(attribute =>
                        attribute.Name.LocalName.Equals("currency", StringComparison.OrdinalIgnoreCase))?.Value,
                    Rate = element.Attributes().FirstOrDefault(attribute =>
                        attribute.Name.LocalName.Equals("rate", StringComparison.OrdinalIgnoreCase))?.Value
                })
                .FirstOrDefault(value => value.Currency?.Equals("USD", StringComparison.OrdinalIgnoreCase) == true);
            return decimal.TryParse(usdRate?.Rate, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ToolsForSteam", "0.4"));
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static string NormalizeAlertGameId(string? gameId, long steamAppId)
    {
        var normalized = gameId?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(normalized)
            ? normalized
            : steamAppId > 0 ? $"steam:{steamAppId}" : string.Empty;
    }

    private static long CreateLocalAlertStorageKey(string gameId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(gameId.ToUpperInvariant()));
        var value = BitConverter.ToInt64(hash, 0) & long.MaxValue;
        return -Math.Max(1, value);
    }

    private static bool AlertMatchesGame(StorePriceAlertData alert, StoreGameState game)
    {
        if (alert.SteamAppId > 0 && game.SteamAppId == alert.SteamAppId) return true;
        if (alert.SteamAppId > 0) return false;
        if (!string.IsNullOrWhiteSpace(alert.GameId) &&
            alert.GameId.Equals(game.Id, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(alert.Title) &&
            DirectStoreCatalogClient.NormalizeTitle(alert.Title)
                .Equals(DirectStoreCatalogClient.NormalizeTitle(game.Title), StringComparison.Ordinal);
    }

    private static IReadOnlyList<StorePriceAlertNotification> UpdateAlertState(
        StoreConfiguration configuration,
        StoreSnapshot snapshot,
        DateTimeOffset? observedAtUtc = null)
    {
        var notifications = new List<StorePriceAlertNotification>();

        foreach (var alert in configuration.Alerts.Values.Where(alert => alert.Enabled))
        {
            var game = snapshot.Wishlist.FirstOrDefault(candidate => AlertMatchesGame(alert, candidate));
            if (game is null) continue;
            TrackAlertPrice(alert, game, observedAtUtc ?? snapshot.RefreshedAtUtc ?? DateTimeOffset.UtcNow);
            var currentPrice = alert.CurrencyCode.Equals("USD", StringComparison.OrdinalIgnoreCase)
                ? game.CheapestPrice
                : game.CheapestPriceEur;
            if (!currentPrice.HasValue) continue;

            var reached = currentPrice.Value <= alert.TargetPrice;
            var shouldNotify = configuration.NotificationsEnabled && reached &&
                (!alert.WasReached || !alert.LastNotifiedPrice.HasValue || currentPrice < alert.LastNotifiedPrice);
            if (shouldNotify)
            {
                notifications.Add(new StorePriceAlertNotification(
                    $"Price alert · {game.Title}",
                    $"{game.BestStoreName}: {currentPrice.Value:0.00} {alert.CurrencyCode} reached your {alert.TargetPrice:0.00} {alert.CurrencyCode} target.",
                    game.BestDealUrl));
                alert.LastNotifiedPrice = currentPrice;
            }

            alert.WasReached = reached;
        }

        return notifications;
    }

    private static StoreSnapshot AttachAlerts(StoreSnapshot snapshot, StoreConfiguration configuration)
    {
        var alerts = configuration.Alerts.Values
            .OrderByDescending(alert => alert.WasReached)
            .ThenBy(alert => alert.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(alert =>
            {
                var game = snapshot.Wishlist.FirstOrDefault(candidate => AlertMatchesGame(alert, candidate));
                var current = alert.CurrencyCode.Equals("USD", StringComparison.OrdinalIgnoreCase)
                    ? game?.CheapestPrice
                    : game?.CheapestPriceEur;
                var history = (alert.PriceHistory ?? [])
                    .OrderBy(point => point.RecordedAtUtc)
                    .Select(point => new StorePriceHistoryPoint(
                        point.RecordedAtUtc,
                        NormalizeTrackedPrice(point.Price),
                        NormalizeTrackedPrice(point.PriceEur)))
                    .ToArray();
                var firstHistory = history.FirstOrDefault();
                return new StorePriceAlertState(
                    alert.SteamAppId,
                    alert.GameId,
                    string.IsNullOrWhiteSpace(alert.Title)
                        ? game?.Title ?? (alert.SteamAppId > 0 ? $"Steam app {alert.SteamAppId}" : "TFS wishlist game")
                        : alert.Title,
                    alert.TargetPrice,
                    alert.CurrencyCode,
                    game?.CheapestPrice,
                    game?.CheapestPriceEur,
                    NormalizeTrackedPrice(alert.OriginalPrice) ?? firstHistory?.Price ?? NormalizeTrackedPrice(game?.CheapestPrice),
                    NormalizeTrackedPrice(alert.OriginalPriceEur) ?? firstHistory?.PriceEur ?? NormalizeTrackedPrice(game?.CheapestPriceEur),
                    alert.CreatedAtUtc ?? firstHistory?.RecordedAtUtc,
                    "USD",
                    alert.Enabled,
                    current.HasValue && current.Value <= alert.TargetPrice,
                    game?.BestDealUrl ?? string.Empty,
                    game?.ImageUrl ?? string.Empty,
                    history);
            })
            .ToArray();
        return snapshot with { Alerts = alerts };
    }

    private static void TrackAlertPrice(
        StorePriceAlertData alert,
        StoreGameState? game,
        DateTimeOffset recordedAtUtc)
    {
        alert.PriceHistory ??= [];
        var price = NormalizeTrackedPrice(game?.CheapestPrice);
        var priceEur = NormalizeTrackedPrice(game?.CheapestPriceEur);
        if (!price.HasValue && !priceEur.HasValue) return;

        alert.CreatedAtUtc ??= recordedAtUtc;
        alert.OriginalPrice ??= price;
        alert.OriginalPriceEur ??= priceEur;

        var last = alert.PriceHistory.LastOrDefault();
        var changed = last is null ||
            NormalizeTrackedPrice(last.Price) != price ||
            NormalizeTrackedPrice(last.PriceEur) != priceEur;
        var dailySampleDue = last is null || recordedAtUtc - last.RecordedAtUtc >= AlertFlatSampleInterval;
        if (!changed && !dailySampleDue) return;

        alert.PriceHistory.Add(new StorePriceHistoryData
        {
            RecordedAtUtc = recordedAtUtc,
            Price = price,
            PriceEur = priceEur
        });
        if (alert.PriceHistory.Count > MaxAlertHistoryPoints)
        {
            alert.PriceHistory.RemoveRange(1, alert.PriceHistory.Count - MaxAlertHistoryPoints);
        }
    }

    private static decimal? NormalizeTrackedPrice(decimal? price) =>
        price is > 0 ? decimal.Round(price.Value, 2) : null;

    private static StoreSnapshot? SanitizeCachedSnapshot(StoreSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        var wishlist = snapshot.Wishlist.Select(SanitizeCachedGame).ToArray();
        var trending = snapshot.Trending
            .Where(IsDirectCachedGame)
            .Select(SanitizeCachedGame)
            .ToArray();
        var featured = snapshot.FeaturedDeals
            .Where(IsDirectCachedGame)
            .Select(SanitizeCachedGame)
            .ToArray();
        return snapshot with
        {
            PriceSource = DirectPriceSource,
            StoreRegionCode = StoreRegionCatalog.Resolve(snapshot.StoreRegionCode).Code,
            StoreRegionName = StoreRegionCatalog.Resolve(snapshot.StoreRegionCode).Name,
            RegionalCurrencyCode = StoreRegionCatalog.Resolve(snapshot.StoreRegionCode).CurrencyCode,
            RegionalCurrencySymbol = StoreRegionCatalog.Resolve(snapshot.StoreRegionCode).CurrencySymbol,
            Wishlist = wishlist,
            Trending = trending,
            FeaturedDeals = featured
        };
    }

    private static bool IsDirectCachedGame(StoreGameState game) =>
        game.PriceProviderGameId?.Equals(game.Id, StringComparison.OrdinalIgnoreCase) == true ||
        game.Offers.Any(offer => TryNormalizeAllowedDealUrl(offer.DealUrl, out _));

    private static StoreGameState SanitizeCachedGame(StoreGameState game)
    {
        var normalized = game with
        {
            IsSteamWishlisted = game.IsSteamWishlisted || game.IsWishlisted && !game.IsLocallyWishlisted,
            IsWishlisted = game.IsWishlisted || game.IsSteamWishlisted || game.IsLocallyWishlisted
        };
        var offers = MarkBest(normalized.Offers.Where(offer => TryNormalizeAllowedDealUrl(offer.DealUrl, out _)));
        if (offers.Count > 0) return ApplyBestOffer(normalized, offers);
        var fallbackUrl = game.SteamAppId is > 0
            ? $"https://store.steampowered.com/app/{game.SteamAppId.Value}/"
            : string.Empty;
        return normalized with
        {
            PriceProviderGameId = game.Id,
            CheapestPrice = null,
            RegularPrice = null,
            CheapestPriceEur = null,
            RegularPriceEur = null,
            RegionalPrice = null,
            RegionalRegularPrice = null,
            DiscountPercent = 0,
            BestStoreName = game.SteamAppId.HasValue ? "Steam" : string.Empty,
            BestDealUrl = fallbackUrl,
            IsOnSale = false,
            Offers = []
        };
    }

    private static bool TryNormalizeAllowedDealUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();
        var allowed = host switch
        {
            "store.steampowered.com" => path.StartsWith("/app/") || path.StartsWith("/search"),
            "www.gog.com" or "gog.com" => path.Contains("/game/") || path.StartsWith("/en/games"),
            "www.xbox.com" or "xbox.com" => path.Contains("/games/store/") || path.Contains("/search/results"),
            "store.epicgames.com" => path.Contains("/p/") || path.Contains("/browse"),
            "www.instant-gaming.com" or "instant-gaming.com" =>
                path.Contains("/search/") || IsInstantGamingProductPath(path),
            _ => false
        };
        if (!allowed) return false;
        normalized = uri.ToString();
        return true;
    }

    private static bool IsInstantGamingProductPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || segments[0].Length is < 2 or > 5) return false;
        var productSegment = segments[1];
        var digitCount = productSegment.TakeWhile(char.IsAsciiDigit).Count();
        return digitCount > 0 && digitCount < productSegment.Length && productSegment[digitCount] == '-';
    }

    private static string ResolveImage(long? steamAppId, IEnumerable<string> candidates, bool poster)
    {
        var supplied = FirstNotEmpty(candidates);
        if (!string.IsNullOrWhiteSpace(supplied)) return supplied;

        if (steamAppId is > 0)
        {
            return poster
                ? $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{steamAppId.Value}/library_600x900.jpg"
                : $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{steamAppId.Value}/header.jpg";
        }

        return string.Empty;
    }

    private StoreArtworkCacheFile? FindCachedArtwork(string cacheKey)
    {
        if (_artworkCacheDirectory is null) return null;
        foreach (var candidate in ArtworkExtensionsByContentType)
        {
            var path = Path.Combine(_artworkCacheDirectory, cacheKey + candidate.Value);
            if (File.Exists(path)) return new StoreArtworkCacheFile(path, candidate.Key);
        }

        return null;
    }

    private static bool TryNormalizeArtworkUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            (AllowedArtworkHosts.Contains(parsed.IdnHost) ||
                AllowedArtworkHostSuffixes.Any(parsed.IdnHost.EndsWith)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static void TouchArtwork(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TrimArtworkCache(string? preservedPath = null)
    {
        if (_artworkCacheDirectory is null || !Directory.Exists(_artworkCacheDirectory)) return;
        try
        {
            var files = Directory.EnumerateFiles(_artworkCacheDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray();
            var totalBytes = files.Sum(file => file.Length);
            var expirationThreshold = DateTime.UtcNow - ArtworkCacheLifetime;

            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc >= expirationThreshold) break;
                if (file.FullName.Equals(preservedPath, StringComparison.OrdinalIgnoreCase)) continue;
                var length = file.Length;
                file.Delete();
                totalBytes -= length;
            }

            if (totalBytes <= MaxArtworkCacheBytes) return;
            foreach (var file in files)
            {
                if (totalBytes <= ArtworkCacheTrimTargetBytes) break;
                if (!file.Exists) continue;
                if (file.FullName.Equals(preservedPath, StringComparison.OrdinalIgnoreCase)) continue;
                var length = file.Length;
                file.Delete();
                totalBytes -= length;
            }
        }
        catch (IOException exception)
        {
            Debug.WriteLine($"Store artwork cache cleanup failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Debug.WriteLine($"Store artwork cache cleanup failed: {exception.Message}");
        }
    }

    private static string FirstNotEmpty(IEnumerable<string> values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static TimeSpan GetRefreshInterval(StoreConfiguration configuration) =>
        TimeSpan.FromMinutes(Math.Clamp(configuration.RefreshIntervalMinutes, (int)MinimumRefreshAge.TotalMinutes, 240));

    private static string BuildStatus(
        int wishlistCount,
        int trendingCount,
        string? wishlistError,
        SteamProfileInfo? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.SteamId64))
            return $"{trendingCount} direct store deals loaded · Steam profile is not available yet.";
        if (!string.IsNullOrWhiteSpace(wishlistError))
            return $"{trendingCount} direct store deals loaded · Wishlist needs public Steam game details.";
        return $"{wishlistCount} wishlist games · {trendingCount} direct store deals · prices refresh automatically.";
    }

    private static int SearchRank(string candidate, string requested)
    {
        if (candidate.Equals(requested, StringComparison.Ordinal)) return 0;
        if (candidate.StartsWith(requested, StringComparison.Ordinal)) return 1;
        if (candidate.Contains(requested, StringComparison.Ordinal)) return 2;
        if (requested.StartsWith(candidate, StringComparison.Ordinal)) return 3;
        return 4;
    }

    private static StoreSnapshot EmptySnapshot(string status) => new(
        status,
        null,
        null,
        false,
        string.Empty,
        string.Empty,
        false,
        "USD",
        "USD",
        "US",
        "United States",
        "USD",
        "$",
        null,
        null,
        DirectPriceSource,
        [],
        [],
        [],
        []);

    private static string NormalizeDisplayCurrency(string? currencyCode) =>
        currencyCode?.Trim().ToUpperInvariant() switch
        {
            "EUR" => "EUR",
            "BOTH" => "BOTH",
            "REGION" => "REGION",
            _ => "USD"
        };

    private static decimal? ConvertUsdToEur(decimal? usd, decimal? usdPerEur) =>
        usd.HasValue && usdPerEur is > 0
            ? decimal.Round(usd.Value / usdPerEur.Value, 2)
            : null;

    private static decimal? ConvertEurToUsd(decimal? eur, decimal? usdPerEur) =>
        eur.HasValue && usdPerEur is > 0
            ? decimal.Round(eur.Value * usdPerEur.Value, 2)
            : null;

    private sealed record CachedOffers(
        DateTimeOffset FetchedAtUtc,
        IReadOnlyList<StoreOfferState> Offers);

    private sealed record RefreshCommit(
        StoreSnapshot Snapshot,
        IReadOnlyList<StorePriceAlertNotification> Notifications);
}

public sealed record StoreArtworkCacheFile(string Path, string ContentType);
