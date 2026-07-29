using System.Net;
using System.Text;
using System.Collections.Concurrent;
using SteamLoader.App.Infrastructure.Store;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreServiceTests
{
    [Fact]
    public void DisplayCurrency_DefaultsToUsd_AndPersistsEverySupportedMode()
    {
        var root = CreateTempRoot();
        try
        {
            var settingsPath = Path.Combine(root, "store.json");
            var service = new StoreService(
                new HttpClient(new RejectingHandler()),
                new StoreSettingsStore(settingsPath),
                () => null);

            Assert.Equal("USD", service.GetCachedSnapshot().DisplayCurrencyCode);
            Assert.Equal("EUR", service.SetDisplayCurrency("eur").DisplayCurrencyCode);
            Assert.Equal("BOTH", service.SetDisplayCurrency("both").DisplayCurrencyCode);
            Assert.Equal("USD", service.SetDisplayCurrency("unsupported").DisplayCurrencyCode);

            var saved = new StoreSettingsStore(settingsPath).Load();
            Assert.Equal("USD", saved.DisplayCurrencyCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StoreRegion_UsesTheSelectedLocalCurrencyAndPersistsIt()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(new KeylessStoreHandler()),
                settings,
                () => new SteamProfileInfo("Test", "test", "76561198000000000", "1", "shortcuts.vdf"));

            service.SetStoreRegion("ca");
            var snapshot = await service.RefreshAsync(CancellationToken.None);

            Assert.Equal("CA", snapshot.StoreRegionCode);
            Assert.Equal("Canada", snapshot.StoreRegionName);
            Assert.Equal("CAD", snapshot.RegionalCurrencyCode);
            Assert.Equal("REGION", snapshot.DisplayCurrencyCode);
            var game = Assert.Single(snapshot.Wishlist);
            Assert.Equal(1.50m, game.RegionalPrice);
            Assert.Equal("Xbox", game.BestStoreName);
            Assert.Equal("https://www.xbox.com/en-CA/games/store/-/PORTAL2XBOX", game.BestDealUrl);
            Assert.Equal("CA", settings.Load().StoreRegionCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PriceAlerts_PreserveTheirChosenCurrency_AndCanBeRemoved()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new StoreService(
                new HttpClient(new RejectingHandler()),
                new StoreSettingsStore(Path.Combine(root, "store.json")),
                () => null);

            var withAlert = service.SetAlert(620, "Portal 2", 4.99m, "EUR", enabled: true);
            var alert = Assert.Single(withAlert.Alerts);
            Assert.Equal("EUR", alert.TargetCurrencyCode);
            Assert.Equal(4.99m, alert.TargetPrice);

            var withoutAlert = service.SetAlert(620, "Portal 2", 4.99m, "EUR", enabled: false);
            Assert.Empty(withoutAlert.Alerts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PriceAlerts_SupportStoreIndependentTfsWishlistGamesWithoutASteamAppId()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(new RejectingHandler()),
                settings,
                () => null);
            var game = new StoreGameState(
                Id: "gog:local-wishlist-game",
                SteamAppId: null,
                PriceProviderGameId: "gog:local-wishlist-game",
                Title: "Local Wishlist Game",
                ImageUrl: "https://images.gog.com/local.jpg",
                HeaderImageUrl: "https://images.gog.com/local-wide.jpg",
                FallbackImageUrl: string.Empty,
                CheapestPrice: 12.99m,
                RegularPrice: 19.99m,
                CheapestPriceEur: 11.49m,
                RegularPriceEur: 17.99m,
                RegionalPrice: 11.49m,
                RegionalRegularPrice: 17.99m,
                RegionalCurrencyCode: "EUR",
                DiscountPercent: 35,
                CurrencyCode: "USD",
                BestStoreName: "GOG",
                BestDealUrl: "https://www.gog.com/en/game/local_wishlist_game",
                ReviewPercent: null,
                DealRating: 80m,
                IsWishlisted: false,
                IsOnSale: true,
                ReleaseText: string.Empty,
                Offers:
                [
                    new StoreOfferState(
                        StoreName: "GOG",
                        Price: 12.99m,
                        RegularPrice: 19.99m,
                        PriceEur: 11.49m,
                        RegularPriceEur: 17.99m,
                        RegionalPrice: 11.49m,
                        RegionalRegularPrice: 17.99m,
                        RegionalCurrencyCode: "EUR",
                        DiscountPercent: 35,
                        CurrencyCode: "USD",
                        DealUrl: "https://www.gog.com/en/game/local_wishlist_game",
                        DealId: "GOG:local-wishlist-game",
                        IsBest: true)
                ]);

            service.SetLocalWishlist(game, enabled: true);
            var withAlert = service.SetAlert(null, game.Id, game.Title, 9.99m, "EUR", enabled: true);
            var alert = Assert.Single(withAlert.Alerts);

            Assert.Equal(0, alert.SteamAppId);
            Assert.Equal(game.Id, alert.GameId);
            Assert.Equal(11.49m, alert.CurrentPriceEur);
            Assert.Single(alert.PriceHistory);
            var saved = Assert.Single(settings.Load().Alerts);
            Assert.True(saved.Key < 0);
            Assert.Equal(game.Id, saved.Value.GameId);

            var withoutAlert = service.SetAlert(null, game.Id, game.Title, 9.99m, "EUR", enabled: false);
            Assert.Empty(withoutAlert.Alerts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PriceAlerts_PreserveTheirOriginalPrice_AndBuildAChangedPriceHistory()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(new KeylessStoreHandler()),
                settings,
                () => new SteamProfileInfo("Test", "test", "76561198000000000", "1", "shortcuts.vdf"));

            await service.RefreshAsync(CancellationToken.None);
            var created = Assert.Single(service.SetAlert(620, "Portal 2", 1.00m, "EUR", enabled: true).Alerts);

            Assert.Equal(1.25m, created.OriginalPrice);
            Assert.Equal(1.10m, created.OriginalPriceEur);
            Assert.NotNull(created.CreatedAtUtc);
            var initialPoint = Assert.Single(created.PriceHistory);
            Assert.Equal(1.25m, initialPoint.Price);
            Assert.Equal(1.10m, initialPoint.PriceEur);

            var configuration = settings.Load();
            var game = Assert.Single(configuration.CachedSnapshot!.Wishlist);
            var changedOffers = game.Offers
                .Select(offer => offer with { Price = 1.50m, PriceEur = 1.25m })
                .ToArray();
            configuration.CachedSnapshot = configuration.CachedSnapshot with
            {
                Wishlist = [game with
                {
                    CheapestPrice = 1.50m,
                    CheapestPriceEur = 1.25m,
                    Offers = changedOffers
                }]
            };
            settings.Save(configuration);

            var updated = Assert.Single(service.SetAlert(620, "Portal 2", 0.99m, "EUR", enabled: true).Alerts);
            Assert.Equal(0.99m, updated.TargetPrice);
            Assert.Equal(1.10m, updated.OriginalPriceEur);
            Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
            Assert.Equal(2, updated.PriceHistory.Count);
            Assert.Equal(1.25m, updated.PriceHistory[^1].PriceEur);

            var saved = Assert.Single(settings.Load().Alerts).Value;
            Assert.Equal(1.10m, saved.OriginalPriceEur);
            Assert.Equal(2, saved.PriceHistory.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenDeal_RejectsUntrustedUrlsBeforeLaunchingAnything()
    {
        var root = CreateTempRoot();
        try
        {
            var service = new StoreService(
                new HttpClient(new RejectingHandler()),
                new StoreSettingsStore(Path.Combine(root, "store.json")),
                () => null);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                service.OpenDeal("https://example.test/not-a-store"));

            Assert.Contains("direct store", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_UsesTheCheapestDirectStorePriceEverywhereWithoutAnApiKey()
    {
        var root = CreateTempRoot();
        var handler = new KeylessStoreHandler();
        try
        {
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(handler),
                settings,
                () => new SteamProfileInfo("Test", "test", "76561198000000000", "1", "shortcuts.vdf"));

            var snapshot = await service.RefreshAsync(CancellationToken.None);

            var game = Assert.Single(snapshot.Wishlist);
            Assert.Equal("Portal 2", game.Title);
            Assert.Equal(1.25m, game.CheapestPrice);
            Assert.Equal(1.10m, game.CheapestPriceEur);
            Assert.Equal(87, game.DiscountPercent);
            Assert.Equal("Xbox", game.BestStoreName);
            Assert.Equal("https://www.xbox.com/en-US/games/store/-/PORTAL2XBOX", game.BestDealUrl);
            Assert.Equal("https://example.test/portal.jpg", game.HeaderImageUrl);
            Assert.Equal("USD", snapshot.DisplayCurrencyCode);
            Assert.Equal(1.2m, snapshot.UsdPerEur);
            Assert.Contains("Direct regional prices", snapshot.PriceSource, StringComparison.Ordinal);
            var requestsAfterRefresh = handler.Requests.Count;
            var offers = await service.GetOffersAsync(game.PriceProviderGameId!, CancellationToken.None);
            Assert.Equal(requestsAfterRefresh, handler.Requests.Count);
            Assert.Equal(["Xbox", "GOG", "Epic Games", "Steam"], offers.Select(offer => offer.StoreName).ToArray());
            Assert.Equal(1.25m, offers[0].Price);
            Assert.All(offers, offer =>
            {
                Assert.StartsWith("https://", offer.DealUrl, StringComparison.Ordinal);
                Assert.DoesNotContain("cheapshark", offer.DealUrl, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Contains(offers, offer => offer.DealUrl.Equals("https://www.xbox.com/en-US/games/store/-/PORTAL2XBOX", StringComparison.Ordinal));
            Assert.Contains(offers, offer => offer.DealUrl.Equals("https://www.gog.com/en/game/portal_2", StringComparison.Ordinal));
            Assert.Contains(offers, offer => offer.DealUrl.Equals("https://store.epicgames.com/p/portal-2?lang=en-US", StringComparison.Ordinal));
            Assert.All(handler.Requests, request =>
            {
                Assert.DoesNotContain("key=", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
                Assert.False(request.HasApiKeyHeader);
                Assert.DoesNotContain("cheapshark", request.Uri.Host, StringComparison.OrdinalIgnoreCase);
            });

            var reopenedHandler = new KeylessStoreHandler();
            var reopened = new StoreService(
                new HttpClient(reopenedHandler),
                settings,
                () => new SteamProfileInfo("Test", "test", "76561198000000000", "1", "shortcuts.vdf"));
            await Task.WhenAll(
                reopened.GetOffersAsync(game.PriceProviderGameId!, CancellationToken.None),
                reopened.GetOffersAsync(game.PriceProviderGameId!, CancellationToken.None));
            Assert.Equal(1, reopenedHandler.Requests.Count(request =>
                request.Uri.AbsoluteUri.Contains("xbox.com/en-US/search/results", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_PreservesConcurrentRegionAndAlertChangesWithoutCachingStalePrices()
    {
        var root = CreateTempRoot();
        var handler = new KeylessStoreHandler(pauseFirstWishlistRequest: true);
        try
        {
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(handler),
                settings,
                () => new SteamProfileInfo("Test", "test", "76561198000000000", "1", "shortcuts.vdf"));

            var staleRefresh = service.RefreshAsync(CancellationToken.None);
            await handler.WaitForPausedWishlistAsync().WaitAsync(TimeSpan.FromSeconds(5));
            service.SetStoreRegion("DE");
            service.SetAlert(620, "Portal 2", 1.05m, "EUR", enabled: true);
            handler.ReleaseWishlist();
            await staleRefresh;

            var afterStaleRefresh = settings.Load();
            Assert.Equal("DE", afterStaleRefresh.StoreRegionCode);
            Assert.Null(afterStaleRefresh.LastRefreshUtc);
            Assert.Null(afterStaleRefresh.CachedSnapshot);
            Assert.Equal(1.05m, Assert.Single(afterStaleRefresh.Alerts).Value.TargetPrice);

            var current = await service.RefreshAsync(CancellationToken.None);
            Assert.Equal("DE", current.StoreRegionCode);
            Assert.Equal(1.10m, Assert.Single(current.Wishlist).CheapestPriceEur);
            Assert.Equal(
                "https://www.xbox.com/de-DE/games/store/-/PORTAL2XBOX",
                Assert.Single(current.Wishlist).BestDealUrl);
            Assert.Equal(1.05m, Assert.Single(current.Alerts).TargetPrice);
            Assert.Equal("DE", settings.Load().CachedSnapshot?.StoreRegionCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Search_CanSaveAndRemoveAStoreIndependentTfsWishlistGame()
    {
        var root = CreateTempRoot();
        try
        {
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(new KeylessStoreHandler()),
                settings,
                () => null);

            var results = await service.SearchAsync("Portal 2", CancellationToken.None);
            var game = Assert.Single(results, item => item.Title == "Portal 2");
            Assert.Contains(game.Offers, offer => offer.StoreName == "Steam");
            Assert.Contains(game.Offers, offer => offer.StoreName == "GOG");
            Assert.Contains(game.Offers, offer => offer.StoreName == "Xbox");

            var saved = service.SetLocalWishlist(game, enabled: true);
            var wishlistGame = Assert.Single(saved.Wishlist);
            Assert.True(wishlistGame.IsWishlisted);
            Assert.True(wishlistGame.IsLocallyWishlisted);
            Assert.Single(settings.Load().SavedGames);

            var removed = service.SetLocalWishlist(wishlistGame, enabled: false);
            Assert.Empty(removed.Wishlist);
            Assert.Empty(settings.Load().SavedGames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstantGaming_AddsOnlyExactInStockRegionCompatiblePcPricesWithoutAnApiKey()
    {
        var root = CreateTempRoot();
        try
        {
            var handler = new InstantGamingHandler();
            var service = new StoreService(
                new HttpClient(handler),
                new StoreSettingsStore(Path.Combine(root, "store.json")),
                () => null);

            service.SetStoreRegion("DE");
            var european = Assert.Single(await service.SearchAsync("RoboCop Rogue City", CancellationToken.None));
            Assert.Equal("RoboCop: Rogue City", european.Title);
            Assert.Equal("Instant Gaming", european.BestStoreName);
            Assert.Equal(3.75m, european.CheapestPrice);
            Assert.Equal(3.29m, european.CheapestPriceEur);
            Assert.Equal(3.29m, european.RegionalPrice);
            Assert.Equal("EUR", european.RegionalCurrencyCode);
            Assert.Equal(
                "https://www.instant-gaming.com/en/9229-buy-robocop-rogue-city-pc-steam/?currency=EUR",
                european.BestDealUrl);
            Assert.All(handler.Requests, uri =>
            {
                Assert.DoesNotContain("key=", uri.Query, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("api", uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            });

            service.SetStoreRegion("BR");
            Assert.Empty(await service.SearchAsync("RoboCop Rogue City", CancellationToken.None));

            var instantRequestsBeforeUnsupportedRegion = handler.Requests.Count;
            service.SetStoreRegion("JP");
            Assert.Empty(await service.SearchAsync("RoboCop Rogue City", CancellationToken.None));
            Assert.Equal(instantRequestsBeforeUnsupportedRegion, handler.Requests.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtworkCache_PersistsTrustedImagesAndDoesNotRedownloadThem()
    {
        var root = CreateTempRoot();
        var source = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/header.jpg";
        try
        {
            var cacheDirectory = Path.Combine(root, "artwork-cache");
            var handler = new ArtworkHandler();
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(handler),
                settings,
                () => null,
                null,
                cacheDirectory);

            var first = await service.GetCachedArtworkAsync(source, CancellationToken.None);
            var second = await service.GetCachedArtworkAsync(source, CancellationToken.None);

            Assert.NotNull(first);
            Assert.True(File.Exists(first.Path));
            Assert.Equal("image/jpeg", first.ContentType);
            Assert.Equal(first.Path, second?.Path);
            Assert.Equal(1, handler.RequestCount);

            var reopenedService = new StoreService(
                new HttpClient(new RejectingHandler()),
                settings,
                () => null,
                null,
                cacheDirectory);
            var persisted = await reopenedService.GetCachedArtworkAsync(source, CancellationToken.None);
            Assert.Equal(first.Path, persisted?.Path);
            Assert.Null(await service.GetCachedArtworkAsync(
                "https://example.test/untrusted.jpg",
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtworkCache_RemovesExpiredUnusedImagesButKeepsActiveArtwork()
    {
        var root = CreateTempRoot();
        var source = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/header.jpg";
        try
        {
            var cacheDirectory = Path.Combine(root, "artwork-cache");
            Directory.CreateDirectory(cacheDirectory);
            var expiredPath = Path.Combine(cacheDirectory, "expired.jpg");
            await File.WriteAllBytesAsync(expiredPath, [0xff, 0xd8, 0xff, 0xd9]);
            File.SetLastWriteTimeUtc(expiredPath, DateTime.UtcNow.AddDays(-46));

            var handler = new ArtworkHandler();
            var settings = new StoreSettingsStore(Path.Combine(root, "store.json"));
            var service = new StoreService(
                new HttpClient(handler),
                settings,
                () => null,
                null,
                cacheDirectory);

            Assert.False(File.Exists(expiredPath));

            var active = await service.GetCachedArtworkAsync(source, CancellationToken.None);
            Assert.NotNull(active);
            Assert.True(File.Exists(active.Path));

            var reopened = new StoreService(
                new HttpClient(new RejectingHandler()),
                settings,
                () => null,
                null,
                cacheDirectory);
            var cached = await reopened.GetCachedArtworkAsync(source, CancellationToken.None);

            Assert.Equal(active.Path, cached?.Path);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Network access was not expected.");
        }
    }

    private sealed class ArtworkHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var content = new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    private sealed class KeylessStoreHandler : HttpMessageHandler
    {
        private readonly bool _pauseFirstWishlistRequest;
        private readonly TaskCompletionSource _wishlistPaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _wishlistReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _wishlistPauseUsed;

        public KeylessStoreHandler(bool pauseFirstWishlistRequest = false)
        {
            _pauseFirstWishlistRequest = pauseFirstWishlistRequest;
        }

        public ConcurrentBag<RequestObservation> Requests { get; } = [];

        public Task WaitForPausedWishlistAsync() => _wishlistPaused.Task;

        public void ReleaseWishlist() => _wishlistReleased.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            Requests.Add(new RequestObservation(uri, request.Headers.Contains("ITAD-API-Key")));
            if (_pauseFirstWishlistRequest &&
                uri.AbsoluteUri.Contains("IWishlistService/GetWishlist", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _wishlistPauseUsed, 1) == 0)
            {
                _wishlistPaused.TrySetResult();
                await _wishlistReleased.Task.WaitAsync(cancellationToken);
            }

            var content = uri.AbsoluteUri switch
            {
                var value when value.Contains("IWishlistService/GetWishlist", StringComparison.Ordinal) =>
                    "{\"response\":{\"items\":[{\"appid\":\"620\",\"priority\":\"0\",\"date_added\":\"1\"}]}}",
                var value when value.Contains("store.steampowered.com/api/storesearch", StringComparison.Ordinal) &&
                    value.Contains("cc=DE", StringComparison.Ordinal) => SteamSearch("EUR", 167, 835),
                var value when value.Contains("store.steampowered.com/api/storesearch", StringComparison.Ordinal) =>
                    SteamSearch(value.Contains("cc=CA", StringComparison.Ordinal) ? "CAD" : "USD", 200, 1000),
                var value when value.Contains("store.steampowered.com/api/appdetails", StringComparison.Ordinal) &&
                    value.Contains("cc=US", StringComparison.Ordinal) => SteamAppDetails("USD", 200, 1000),
                var value when value.Contains("store.steampowered.com/api/appdetails", StringComparison.Ordinal) &&
                    value.Contains("cc=DE", StringComparison.Ordinal) => SteamAppDetails("EUR", 167, 835),
                var value when value.Contains("store.steampowered.com/api/appdetails", StringComparison.Ordinal) &&
                    value.Contains("cc=CA", StringComparison.Ordinal) => SteamAppDetails("CAD", 250, 1000),
                var value when value.Contains("store.steampowered.com/api/featuredcategories", StringComparison.Ordinal) =>
                    "{\"specials\":{\"items\":[]},\"top_sellers\":{\"items\":[]}}",
                var value when value.Contains("catalog.gog.com/v1/catalog", StringComparison.Ordinal) &&
                    value.Contains("query=Portal%202", StringComparison.Ordinal) && value.Contains("currencyCode=USD", StringComparison.Ordinal) =>
                    GogProduct("USD", "1.50", "10.00"),
                var value when value.Contains("catalog.gog.com/v1/catalog", StringComparison.Ordinal) &&
                    value.Contains("query=Portal%202", StringComparison.Ordinal) && value.Contains("currencyCode=EUR", StringComparison.Ordinal) =>
                    GogProduct("EUR", "1.40", "9.50"),
                var value when value.Contains("catalog.gog.com/v1/catalog", StringComparison.Ordinal) =>
                    "{\"products\":[]}",
                var value when value.Contains("freeGamesPromotions", StringComparison.Ordinal) =>
                    EpicProduct(value.Contains("country=DE", StringComparison.Ordinal) ? "EUR" : "USD"),
                var value when value.Contains("xbox.com/", StringComparison.Ordinal) &&
                    value.Contains("/search/results", StringComparison.Ordinal) =>
                    "<html><script>SEARCH_GAMES_SEARCHQUERY={\"products\":[{\"productId\":\"PORTAL2XBOX\"}]}</script></html>",
                var value when value.Contains("displaycatalog.mp.microsoft.com", StringComparison.Ordinal) =>
                    XboxProduct(value.Contains("market=DE", StringComparison.Ordinal)
                        ? "EUR"
                        : value.Contains("market=CA", StringComparison.Ordinal) ? "CAD" : "USD"),
                var value when value.Contains("ecb.europa.eu", StringComparison.Ordinal) =>
                    "<?xml version=\"1.0\"?><gesmes:Envelope xmlns:gesmes=\"http://www.gesmes.org/xml/2002-08-01\" xmlns=\"http://www.ecb.int/vocabulary/2002-08-01/eurofxref\"><Cube><Cube time=\"2026-07-20\"><Cube currency=\"USD\" rate=\"1.2000\"/></Cube></Cube></gesmes:Envelope>",
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
            var mediaType = content.StartsWith("<?xml", StringComparison.Ordinal) ? "text/xml" : "application/json";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            };
        }

        private static string SteamAppDetails(string currency, int final, int initial) =>
            $"{{\"620\":{{\"success\":true,\"data\":{{\"name\":\"Portal 2\",\"header_image\":\"https://example.test/portal.jpg\",\"price_overview\":{{\"currency\":\"{currency}\",\"final\":{final},\"initial\":{initial},\"discount_percent\":80}},\"release_date\":{{\"date\":\"18 Apr, 2011\"}}}}}}}}";

        private static string SteamSearch(string currency, int final, int initial) =>
            $"{{\"total\":1,\"items\":[{{\"type\":\"app\",\"name\":\"Portal 2\",\"id\":620,\"tiny_image\":\"https://example.test/portal-small.jpg\",\"price\":{{\"currency\":\"{currency}\",\"initial\":{initial},\"final\":{final}}}}}]}}";

        private static string GogProduct(string currency, string final, string regular) =>
            $"{{\"products\":[{{\"id\":\"gog-portal-2\",\"title\":\"Portal 2\",\"coverVertical\":\"https://images.gog.com/portal.jpg\",\"coverHorizontal\":\"https://images.gog.com/portal-wide.jpg\",\"price\":{{\"finalMoney\":{{\"amount\":\"{final}\",\"currency\":\"{currency}\"}},\"baseMoney\":{{\"amount\":\"{regular}\",\"currency\":\"{currency}\"}}}},\"storeLink\":\"https://www.gog.com/en/game/portal_2\",\"reviewsRating\":95}}]}}";

        private static string EpicProduct(string currency)
        {
            var price = currency == "EUR" ? 145 : 175;
            return $"{{\"data\":{{\"Catalog\":{{\"searchStore\":{{\"elements\":[{{\"id\":\"epic-portal-2\",\"title\":\"Portal 2\",\"offerMappings\":[{{\"pageSlug\":\"portal-2\"}}],\"keyImages\":[],\"price\":{{\"totalPrice\":{{\"currencyCode\":\"{currency}\",\"discountPrice\":{price},\"originalPrice\":1000,\"currencyInfo\":{{\"decimals\":2}}}}}}}}]}}}}}}}}";
        }

        private static string XboxProduct(string currency)
        {
            var price = currency switch
            {
                "EUR" => "1.10",
                "CAD" => "1.50",
                _ => "1.25"
            };
            const string unavailable = "{\"ProductId\":\"PORTALEXTRAS\",\"ProductKind\":\"Game\",\"Properties\":{\"PackageFamilyName\":\"Valve.PortalExtras_test\"},\"LocalizedProperties\":[{\"ProductTitle\":\"Portal Extras\",\"Images\":[]}],\"MarketProperties\":[{}],\"DisplaySkuAvailabilities\":[{\"Sku\":{\"Properties\":{\"IsTrial\":false}},\"Availabilities\":[{\"Actions\":[\"Details\"]}]}]}";
            return $"{{\"Products\":[{unavailable},{{\"ProductId\":\"PORTAL2XBOX\",\"ProductKind\":\"Game\",\"Properties\":{{\"PackageFamilyName\":\"Valve.Portal2_test\"}},\"LocalizedProperties\":[{{\"ProductTitle\":\"Portal 2\",\"Images\":[]}}],\"MarketProperties\":[{{\"OriginalReleaseDate\":\"2011-04-18\",\"UsageData\":[]}}],\"DisplaySkuAvailabilities\":[{{\"Sku\":{{\"Properties\":{{\"IsTrial\":false}},\"SkuType\":\"Full\"}},\"Availabilities\":[{{\"Actions\":[\"Purchase\"],\"OrderManagementData\":{{\"Price\":{{\"ListPrice\":0,\"MSRP\":0,\"CurrencyCode\":\"{currency}\"}}}}}},{{\"Actions\":[\"Purchase\"],\"OrderManagementData\":{{\"Price\":{{\"ListPrice\":{price},\"MSRP\":9.99,\"CurrencyCode\":\"{currency}\"}}}}}}]}}]}}]}}";
        }

        public sealed record RequestObservation(Uri Uri, bool HasApiKeyHeader);
    }

    private sealed class InstantGamingHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            if (!uri.Host.Equals("www.instant-gaming.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected request: {uri}");

            Requests.Add(uri);
            var currency = uri.Query.Contains("currency=BRL", StringComparison.OrdinalIgnoreCase) ? "BRL" : "USD";
            var converted = currency == "BRL" ? "20.10" : "3.75";
            var retail = currency == "BRL" ? "244.50" : "45.58";
            var html = $$"""
                <html><script>
                window.searchResults = {"hits":[
                  {"prod_id":9229,"name":"RoboCop: Rogue City","seo_name":"robocop-rogue-city-pc-steam","price_eur":"3.29","price_converted":{{converted}},"default_retail":"39.99","default_retail_currency":"EUR","retail":{{retail}},"retail_currency":"{{currency}}","discount":91,"has_stock":1,"is_draft":0,"reviews_avg":90,"platform_names":["PC"],"country_whitelist":["worldwide"],"country_blacklist":["BR"]},
                  {"prod_id":16164,"name":"RoboCop: Rogue City","seo_name":"robocop-rogue-city-playstation-5","price_eur":"2.99","price_converted":3.40,"default_retail":"39.99","default_retail_currency":"EUR","retail":45.00,"retail_currency":"{{currency}}","discount":92,"has_stock":1,"is_draft":0,"reviews_avg":90,"platform_names":["PS5"],"country_whitelist":["worldwide"],"country_blacklist":[]},
                  {"prod_id":9999,"name":"RoboCop: Rogue City","seo_name":"robocop-rogue-city-out-of-stock-pc-steam","price_eur":"1.00","price_converted":1.10,"default_retail":"39.99","default_retail_currency":"EUR","retail":45.00,"retail_currency":"{{currency}}","discount":97,"has_stock":0,"is_draft":0,"reviews_avg":90,"platform_names":["PC"],"country_whitelist":["worldwide"],"country_blacklist":[]}
                ]};
                </script></html>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            });
        }
    }
}
