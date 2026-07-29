using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.Store;

internal sealed partial class DirectStoreCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan EpicCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstantGamingCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> InstantGamingRegionalCurrencies =
        new(StringComparer.OrdinalIgnoreCase) { "USD", "EUR", "GBP", "CAD", "AUD", "BRL" };
    private const long MaxInstantGamingSearchBytes = 5L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CachedProducts> _epicCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedProducts> _instantGamingCache = new(StringComparer.OrdinalIgnoreCase);

    public DirectStoreCatalogClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<DirectStoreProduct>> FetchSteamWishlistAsync(
        string steamId64,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid={Uri.EscapeDataString(steamId64)}";
        using var response = await SendAsync(url, cancellationToken, ensureSuccess: false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "Steam could not expose this wishlist. Set the Steam profile and game details to Public, then refresh.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("response", out var responseNode) ||
            !responseNode.TryGetProperty("items", out var itemsNode) ||
            itemsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var appIds = itemsNode.EnumerateArray()
            .Select(item => ReadLong(item, "appid"))
            .Where(appId => appId is > 0)
            .Select(appId => appId!.Value)
            .Distinct()
            .Take(80)
            .ToArray();
        var products = new ConcurrentBag<DirectStoreProduct>();
        await Parallel.ForEachAsync(
            appIds,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
            async (appId, token) =>
            {
                try
                {
                    var product = await FetchSteamProductAsync(appId, region, token);
                    if (product is not null) products.Add(product);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // One delisted or temporarily unavailable app must not make
                    // the entire public wishlist unusable.
                }
            });
        return products
            .OrderBy(product => product.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<DirectStoreProduct>> FetchTrendingAsync(
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var steamTask = SafeAsync(token => FetchSteamTrendingAsync(region, token), cancellationToken);
        var gogTask = SafeAsync(token => FetchGogTrendingAsync(region, token), cancellationToken);
        var epicTask = SafeAsync(token => FetchEpicPromotionsAsync(region, token), cancellationToken);
        await Task.WhenAll(steamTask, gogTask, epicTask);
        return (await steamTask)
            .Concat(await gogTask)
            .Concat(await epicTask)
            .ToArray();
    }

    public async Task<IReadOnlyList<DirectStoreProduct>> FetchOffersAsync(
        long? steamAppId,
        string title,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task<IReadOnlyList<DirectStoreProduct>>>
        {
            SafeAsync(token => FetchGogMatchAsync(title, region, token), cancellationToken),
            SafeAsync(token => FetchXboxMatchAsync(title, region, token), cancellationToken),
            SafeAsync(token => FetchEpicMatchAsync(title, region, token), cancellationToken),
            SafeAsync(token => FetchInstantGamingMatchAsync(title, region, token), cancellationToken)
        };
        if (steamAppId is > 0)
        {
            tasks.Insert(0, SafeAsync(async token =>
            {
                var product = await FetchSteamProductAsync(steamAppId.Value, region, token);
                return product is null ? [] : [product];
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        return tasks.SelectMany(task => task.Result).ToArray();
    }

    public async Task<IReadOnlyList<DirectStoreProduct>> SearchAsync(
        string query,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length < 2) return [];

        var tasks = new[]
        {
            SafeAsync(token => FetchSteamSearchAsync(normalizedQuery, region, token), cancellationToken),
            SafeAsync(async token =>
            {
                var usdTask = FetchGogCatalogAsync(normalizedQuery, "US", "USD", 20, token);
                var eurTask = FetchGogCatalogAsync(normalizedQuery, "DE", "EUR", 20, token);
                var regionalTask = region.CountryCode switch
                {
                    "US" => usdTask,
                    "DE" => eurTask,
                    _ => FetchGogCatalogAsync(normalizedQuery, region.CountryCode, region.CurrencyCode, 20, token)
                };
                await Task.WhenAll(usdTask, eurTask, regionalTask);
                return MergeRegionalLists(await usdTask, await eurTask, await regionalTask);
            }, cancellationToken),
            SafeAsync(token => FetchXboxSearchAsync(normalizedQuery, region, token), cancellationToken),
            SafeAsync(async token =>
            {
                var requested = NormalizeTitle(normalizedQuery);
                return (await FetchEpicPromotionsAsync(region, token))
                    .Where(product => NormalizeTitle(product.Title).Contains(requested, StringComparison.Ordinal))
                    .Take(20)
                    .ToArray();
            }, cancellationToken),
            SafeAsync(token => FetchInstantGamingSearchAsync(normalizedQuery, region, token), cancellationToken)
        };

        await Task.WhenAll(tasks);
        return tasks
            .SelectMany(task => task.Result)
            .GroupBy(product => $"{product.StoreName}:{product.ProductId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(80)
            .ToArray();
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchSteamSearchAsync(
        string query,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var usdTask = FetchSteamSearchMarketAsync(query, "US", cancellationToken);
        var eurTask = FetchSteamSearchMarketAsync(query, "DE", cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchSteamSearchMarketAsync(query, region.CountryCode, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        return MergeRegionalLists(await usdTask, await eurTask, await regionalTask);
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchSteamSearchMarketAsync(
        string query,
        string countryCode,
        CancellationToken cancellationToken)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc={countryCode}";
        using var response = await SendAsync(url, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("items", out var itemsNode) ||
            itemsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return itemsNode.EnumerateArray()
            .Take(30)
            .Select((item, index) =>
            {
                var appId = ReadLong(item, "id");
                var price = item.TryGetProperty("price", out var priceNode) ? priceNode : default;
                return appId is > 0
                    ? CreateRegionalProduct(
                        "Steam",
                        appId.Value.ToString(CultureInfo.InvariantCulture),
                        appId,
                        ReadString(item, "name"),
                        $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId.Value}/library_600x900.jpg",
                        ReadString(item, "tiny_image"),
                        ReadString(price, "currency"),
                        MinorPrice(price, "final"),
                        MinorPrice(price, "initial"),
                        $"https://store.steampowered.com/app/{appId.Value}/",
                        null,
                        100m - index,
                        string.Empty)
                    : null;
            })
            .OfType<DirectStoreProduct>()
            .Where(product => !string.IsNullOrWhiteSpace(product.Title))
            .ToArray();
    }

    private async Task<DirectStoreProduct?> FetchSteamProductAsync(
        long appId,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var usdTask = FetchSteamProductMarketAsync(appId, "US", cancellationToken);
        var eurTask = FetchSteamProductMarketAsync(appId, "DE", cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchSteamProductMarketAsync(appId, region.CountryCode, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        return MergeRegional(await usdTask, await eurTask, await regionalTask);
    }

    private async Task<DirectStoreProduct?> FetchSteamProductMarketAsync(
        long appId,
        string countryCode,
        CancellationToken cancellationToken)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc={countryCode}&l=english&filters=basic,price_overview,release_date";
        using var response = await SendAsync(url, cancellationToken, ensureSuccess: false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var appNode) ||
            !appNode.TryGetProperty("data", out var dataNode) ||
            dataNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = ReadString(dataNode, "name");
        var priceNode = dataNode.TryGetProperty("price_overview", out var parsedPrice) ? parsedPrice : default;
        var currency = priceNode.ValueKind == JsonValueKind.Object ? ReadString(priceNode, "currency") : string.Empty;
        var final = priceNode.ValueKind == JsonValueKind.Object ? MinorPrice(priceNode, "final") : null;
        var initial = priceNode.ValueKind == JsonValueKind.Object ? MinorPrice(priceNode, "initial") : null;
        var releaseText = dataNode.TryGetProperty("release_date", out var releaseNode)
            ? ReadString(releaseNode, "date")
            : string.Empty;
        return CreateRegionalProduct(
            "Steam",
            appId.ToString(CultureInfo.InvariantCulture),
            appId,
            title,
            $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
            ReadString(dataNode, "header_image"),
            currency,
            final,
            initial,
            $"https://store.steampowered.com/app/{appId}/",
            null,
            0,
            releaseText);
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchSteamTrendingAsync(
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var usdTask = FetchSteamTrendingMarketAsync("US", cancellationToken);
        var eurTask = FetchSteamTrendingMarketAsync("DE", cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchSteamTrendingMarketAsync(region.CountryCode, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        return MergeRegionalLists(await usdTask, await eurTask, await regionalTask).Take(30).ToArray();
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchSteamTrendingMarketAsync(
        string countryCode,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            $"https://store.steampowered.com/api/featuredcategories?cc={countryCode}&l=english",
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var items = new List<JsonElement>();
        foreach (var sectionName in new[] { "specials", "top_sellers" })
        {
            if (document.RootElement.TryGetProperty(sectionName, out var section) &&
                section.TryGetProperty("items", out var sectionItems) &&
                sectionItems.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(sectionItems.EnumerateArray().Select(item => item.Clone()));
            }
        }

        return items
            .Where(item => ReadLong(item, "id") is > 0)
            .GroupBy(item => ReadLong(item, "id")!.Value)
            .Select(group => group.First())
            .Select((item, index) =>
            {
                var appId = ReadLong(item, "id")!.Value;
                return CreateRegionalProduct(
                    "Steam",
                    appId.ToString(CultureInfo.InvariantCulture),
                    appId,
                    ReadString(item, "name"),
                    ReadString(item, "large_capsule_image"),
                    ReadString(item, "header_image"),
                    ReadString(item, "currency"),
                    MinorPrice(item, "final_price"),
                    MinorPrice(item, "original_price"),
                    $"https://store.steampowered.com/app/{appId}/",
                    null,
                    100m - index,
                    string.Empty);
            })
            .Where(product => !string.IsNullOrWhiteSpace(product.Title))
            .ToArray();
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchGogTrendingAsync(
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var usdTask = FetchGogCatalogAsync(null, "US", "USD", 30, cancellationToken);
        var eurTask = FetchGogCatalogAsync(null, "DE", "EUR", 30, cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchGogCatalogAsync(null, region.CountryCode, region.CurrencyCode, 30, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        return MergeRegionalLists(await usdTask, await eurTask, await regionalTask);
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchGogMatchAsync(
        string title,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var usdTask = FetchGogCatalogAsync(title, "US", "USD", 10, cancellationToken);
        var eurTask = FetchGogCatalogAsync(title, "DE", "EUR", 10, cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchGogCatalogAsync(title, region.CountryCode, region.CurrencyCode, 10, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        var products = MergeRegionalLists(await usdTask, await eurTask, await regionalTask);
        var match = SelectBestTitleMatch(products, title);
        return match is null ? [] : [match];
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchGogCatalogAsync(
        string? query,
        string countryCode,
        string currencyCode,
        int limit,
        CancellationToken cancellationToken)
    {
        var queryPart = string.IsNullOrWhiteSpace(query)
            ? ""
            : $"&query={Uri.EscapeDataString(query)}";
        var url = $"https://catalog.gog.com/v1/catalog?limit={limit}&order=desc%3Atrending&productType=in%3Agame%2Cpack&countryCode={countryCode}&locale=en-US&currencyCode={currencyCode}{queryPart}";
        using var response = await SendAsync(url, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("products", out var productsNode) ||
            productsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return productsNode.EnumerateArray().Select((product, index) =>
        {
            var price = product.TryGetProperty("price", out var priceNode) ? priceNode : default;
            var finalMoney = price.ValueKind == JsonValueKind.Object && price.TryGetProperty("finalMoney", out var finalNode)
                ? finalNode
                : default;
            var baseMoney = price.ValueKind == JsonValueKind.Object && price.TryGetProperty("baseMoney", out var baseNode)
                ? baseNode
                : default;
            return CreateRegionalProduct(
                "GOG",
                ReadString(product, "id"),
                null,
                ReadString(product, "title"),
                ReadString(product, "coverVertical"),
                ReadString(product, "coverHorizontal"),
                ReadString(finalMoney, "currency"),
                ReadDecimal(finalMoney, "amount"),
                ReadDecimal(baseMoney, "amount"),
                ReadString(product, "storeLink"),
                ReadInteger(product, "reviewsRating"),
                80m - index,
                ReadString(product, "releaseDate"));
        }).Where(product =>
            !string.IsNullOrWhiteSpace(product.ProductId) &&
            !string.IsNullOrWhiteSpace(product.Title) &&
            Uri.TryCreate(product.DirectUrl, UriKind.Absolute, out _)).ToArray();
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchXboxMatchAsync(
        string title,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var searchUrl = $"https://www.xbox.com/{NormalizeWebLocale(region.Locale)}/search/results?q={Uri.EscapeDataString(title)}";
        using var searchResponse = await SendAsync(searchUrl, cancellationToken);
        var html = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
        var productIds = ExtractXboxProductIds(html).Take(12).ToArray();
        if (productIds.Length == 0) return [];

        var usdTask = FetchXboxProductsMarketAsync(productIds, "US", "en-us", cancellationToken);
        var eurTask = FetchXboxProductsMarketAsync(productIds, "DE", "de-de", cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchXboxProductsMarketAsync(productIds, region.CountryCode, region.XboxLanguage, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        var match = SelectBestTitleMatch(MergeRegionalLists(await usdTask, await eurTask, await regionalTask), title);
        return match is null ? [] : [match];
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchXboxSearchAsync(
        string title,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var searchUrl = $"https://www.xbox.com/{NormalizeWebLocale(region.Locale)}/search/results?q={Uri.EscapeDataString(title)}";
        using var searchResponse = await SendAsync(searchUrl, cancellationToken);
        var html = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
        var productIds = ExtractXboxProductIds(html).Take(20).ToArray();
        if (productIds.Length == 0) return [];

        var usdTask = FetchXboxProductsMarketAsync(productIds, "US", "en-us", cancellationToken);
        var eurTask = FetchXboxProductsMarketAsync(productIds, "DE", "de-de", cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchXboxProductsMarketAsync(productIds, region.CountryCode, region.XboxLanguage, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        return MergeRegionalLists(await usdTask, await eurTask, await regionalTask);
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchXboxProductsMarketAsync(
        IReadOnlyList<string> productIds,
        string market,
        string language,
        CancellationToken cancellationToken)
    {
        var ids = string.Join(',', productIds.Select(Uri.EscapeDataString));
        var url = $"https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={ids}&market={market}&languages={language}";
        using var response = await SendAsync(url, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("Products", out var productsNode) ||
            productsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var products = new List<DirectStoreProduct>();
        foreach (var product in productsNode.EnumerateArray())
        {
            var productId = ReadString(product, "ProductId");
            var localized = FirstArrayObject(product, "LocalizedProperties");
            var title = ReadString(localized, "ProductTitle");
            var productProperties = product.TryGetProperty("Properties", out var parsedProperties)
                ? parsedProperties
                : default;
            var isWindowsPackage = !string.IsNullOrWhiteSpace(ReadString(productProperties, "PackageFamilyName"));
            if (!ReadString(product, "ProductKind").Equals("Game", StringComparison.OrdinalIgnoreCase) ||
                !isWindowsPackage)
            {
                continue;
            }
            var price = FindXboxPurchasePrice(product);
            if (price is null || string.IsNullOrWhiteSpace(title)) continue;
            var images = localized.TryGetProperty("Images", out var imagesNode) && imagesNode.ValueKind == JsonValueKind.Array
                ? imagesNode.EnumerateArray().Select(image => image.Clone()).ToArray()
                : [];
            var poster = ResolveXboxImage(images, "Poster", "BoxArt", "BrandedKeyArt");
            var header = ResolveXboxImage(images, "SuperHeroArt", "TitledHeroArt", "Screenshot");
            products.Add(CreateRegionalProduct(
                "Xbox",
                productId,
                null,
                title,
                poster,
                header,
                price.Value.CurrencyCode,
                price.Value.Price,
                price.Value.RegularPrice,
                $"https://www.xbox.com/{NormalizeWebLocale(language)}/games/store/-/{Uri.EscapeDataString(productId)}",
                ResolveXboxRating(product),
                60m,
                ReadString(FirstArrayObject(product, "MarketProperties"), "OriginalReleaseDate")));
        }

        return products;
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchEpicPromotionsAsync(
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var usdTask = FetchEpicPromotionsMarketAsync("US", "en-US", cancellationToken);
        var eurTask = FetchEpicPromotionsMarketAsync("DE", "de-DE", cancellationToken);
        var regionalTask = region.CountryCode switch
        {
            "US" => usdTask,
            "DE" => eurTask,
            _ => FetchEpicPromotionsMarketAsync(region.CountryCode, region.Locale, cancellationToken)
        };
        await Task.WhenAll(usdTask, eurTask, regionalTask);
        return MergeRegionalLists(await usdTask, await eurTask, await regionalTask);
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchEpicMatchAsync(
        string title,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var match = SelectBestTitleMatch(await FetchEpicPromotionsAsync(region, cancellationToken), title);
        return match is null ? [] : [match];
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchEpicPromotionsMarketAsync(
        string country,
        string locale,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{country}:{locale}";
        if (_epicCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAtUtc < EpicCacheLifetime)
        {
            return cached.Products;
        }

        var url = $"https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale={locale}&country={country}&allowCountries={country}";
        using var response = await SendAsync(url, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryGetPath(document.RootElement, out var elements, "data", "Catalog", "searchStore", "elements") ||
            elements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = elements.EnumerateArray().Select((element, index) =>
        {
            var price = TryGetPath(element, out var totalPrice, "price", "totalPrice") ? totalPrice : default;
            var decimals = TryGetPath(price, out var currencyInfo, "currencyInfo")
                ? ReadInteger(currencyInfo, "decimals") ?? 2
                : 2;
            var divisor = (decimal)Math.Pow(10, Math.Clamp(decimals, 0, 4));
            var pageSlug = element.TryGetProperty("offerMappings", out var mappings) && mappings.ValueKind == JsonValueKind.Array
                ? mappings.EnumerateArray().Select(mapping => ReadString(mapping, "pageSlug")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                : string.Empty;
            if (string.IsNullOrWhiteSpace(pageSlug)) pageSlug = ReadString(element, "productSlug");
            var images = element.TryGetProperty("keyImages", out var imagesNode) && imagesNode.ValueKind == JsonValueKind.Array
                ? imagesNode.EnumerateArray().Select(image => image.Clone()).ToArray()
                : [];
            return CreateRegionalProduct(
                "Epic Games",
                ReadString(element, "id"),
                null,
                ReadString(element, "title"),
                ResolveEpicImage(images, "OfferImageTall", "Thumbnail"),
                ResolveEpicImage(images, "OfferImageWide", "DieselStoreFrontWide", "Thumbnail"),
                ReadString(price, "currencyCode"),
                ReadDecimal(price, "discountPrice") / divisor,
                ReadDecimal(price, "originalPrice") / divisor,
                string.IsNullOrWhiteSpace(pageSlug)
                    ? string.Empty
                    : $"https://store.epicgames.com/p/{Uri.EscapeDataString(pageSlug)}?lang={Uri.EscapeDataString(NormalizeEpicStoreLanguage(locale))}",
                null,
                70m - index,
                ReadString(element, "effectiveDate"));
        }).Where(product =>
            !string.IsNullOrWhiteSpace(product.Title) &&
            !string.IsNullOrWhiteSpace(product.DirectUrl)).ToArray();
        _epicCache[cacheKey] = new CachedProducts(DateTimeOffset.UtcNow, result);
        return result;
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchInstantGamingMatchAsync(
        string title,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        var products = await FetchInstantGamingProductsAsync(title, region, cancellationToken);
        var requested = NormalizeTitle(title);
        var match = products.FirstOrDefault(product =>
            NormalizeTitle(product.Title).Equals(requested, StringComparison.Ordinal));
        return match is null ? [] : [match];
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchInstantGamingSearchAsync(
        string query,
        StoreRegionDefinition region,
        CancellationToken cancellationToken) =>
        (await FetchInstantGamingProductsAsync(query, region, cancellationToken)).Take(20).ToArray();

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchInstantGamingProductsAsync(
        string query,
        StoreRegionDefinition region,
        CancellationToken cancellationToken)
    {
        if (!InstantGamingRegionalCurrencies.Contains(region.CurrencyCode)) return [];

        var usdTask = FetchInstantGamingMarketAsync(query, region.CountryCode, "USD", cancellationToken);
        if (region.CurrencyCode.Equals("USD", StringComparison.OrdinalIgnoreCase))
        {
            return await usdTask;
        }

        if (region.CurrencyCode.Equals("EUR", StringComparison.OrdinalIgnoreCase))
        {
            return (await usdTask)
                .Select(product => SetInstantGamingRegionalCurrency(product, "EUR"))
                .Where(product => product.PriceRegional is > 0)
                .ToArray();
        }

        var regionalTask = FetchInstantGamingMarketAsync(
            query,
            region.CountryCode,
            region.CurrencyCode,
            cancellationToken);
        await Task.WhenAll(usdTask, regionalTask);
        return MergeRegionalLists(await usdTask, await usdTask, await regionalTask);
    }

    private async Task<IReadOnlyList<DirectStoreProduct>> FetchInstantGamingMarketAsync(
        string query,
        string countryCode,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        var normalizedCurrency = currencyCode.Trim().ToUpperInvariant();
        var cacheKey = $"{countryCode}:{normalizedCurrency}:{NormalizeTitle(query)}";
        if (_instantGamingCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAtUtc < InstantGamingCacheLifetime)
        {
            return cached.Products;
        }

        var url = $"https://www.instant-gaming.com/en/search/?query={Uri.EscapeDataString(query)}&currency={Uri.EscapeDataString(normalizedCurrency)}";
        using var response = await SendHtmlAsync(url, cancellationToken);
        if (response.Content.Headers.ContentLength is > MaxInstantGamingSearchBytes) return [];
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (html.Length > MaxInstantGamingSearchBytes) return [];
        var match = InstantGamingSearchResultsRegex().Match(html);
        if (!match.Success) return [];

        using var document = JsonDocument.Parse(match.Groups["json"].Value);
        if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return [];

        var products = hits.EnumerateArray()
            .Where(hit => IsInstantGamingProductAvailable(hit, countryCode))
            .Select((hit, index) => MapInstantGamingProduct(hit, normalizedCurrency, index))
            .OfType<DirectStoreProduct>()
            .Take(40)
            .ToArray();
        _instantGamingCache[cacheKey] = new CachedProducts(DateTimeOffset.UtcNow, products);
        return products;
    }

    private static DirectStoreProduct? MapInstantGamingProduct(
        JsonElement hit,
        string currencyCode,
        int index)
    {
        var productId = ReadLong(hit, "prod_id");
        var title = ReadString(hit, "name").Trim();
        var slug = ReadString(hit, "seo_name").Trim().ToLowerInvariant();
        if (productId is not > 0 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(slug) ||
            slug.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            return null;
        }

        var priceEur = ReadDecimal(hit, "price_eur") ?? ReadDecimal(hit, "price");
        var regularEur = ReadString(hit, "default_retail_currency").Equals("EUR", StringComparison.OrdinalIgnoreCase)
            ? ReadDecimal(hit, "default_retail")
            : null;
        var regionalPrice = currencyCode.Equals("EUR", StringComparison.OrdinalIgnoreCase)
            ? priceEur
            : ReadDecimal(hit, "price_converted");
        var regionalRegular = ReadString(hit, "retail_currency").Equals(currencyCode, StringComparison.OrdinalIgnoreCase)
            ? ReadDecimal(hit, "retail")
            : null;
        if (regionalPrice is not > 0) return null;

        var discount = regionalRegular is > 0 && regionalPrice < regionalRegular
            ? Math.Clamp((int)decimal.Round(
                (regionalRegular.Value - regionalPrice.Value) / regionalRegular.Value * 100m), 0, 100)
            : ReadInteger(hit, "discount") ?? 0;
        var directUrl = $"https://www.instant-gaming.com/en/{productId.Value.ToString(CultureInfo.InvariantCulture)}-buy-{slug}/?currency={Uri.EscapeDataString(currencyCode)}";
        return new DirectStoreProduct(
            "Instant Gaming",
            productId.Value.ToString(CultureInfo.InvariantCulture),
            null,
            title,
            string.Empty,
            string.Empty,
            currencyCode.Equals("USD", StringComparison.OrdinalIgnoreCase) ? regionalPrice : null,
            currencyCode.Equals("USD", StringComparison.OrdinalIgnoreCase) ? regionalRegular : null,
            priceEur,
            regularEur,
            regionalPrice,
            regionalRegular,
            currencyCode,
            discount,
            directUrl,
            ReadInteger(hit, "reviews_avg"),
            50m - index,
            string.Empty);
    }

    private static DirectStoreProduct SetInstantGamingRegionalCurrency(
        DirectStoreProduct product,
        string currencyCode)
    {
        var price = currencyCode.Equals("EUR", StringComparison.OrdinalIgnoreCase)
            ? product.PriceEur
            : product.PriceUsd;
        var regular = currencyCode.Equals("EUR", StringComparison.OrdinalIgnoreCase)
            ? product.RegularPriceEur
            : product.RegularPriceUsd;
        var discount = price is > 0 && regular is > 0 && price < regular
            ? Math.Clamp((int)decimal.Round((regular.Value - price.Value) / regular.Value * 100m), 0, 100)
            : product.DiscountPercent;
        var directUrl = product.DirectUrl.Split('?', 2)[0] + $"?currency={Uri.EscapeDataString(currencyCode)}";
        return product with
        {
            PriceRegional = price,
            RegularPriceRegional = regular,
            RegionalCurrencyCode = currencyCode,
            DiscountPercent = discount,
            DirectUrl = directUrl
        };
    }

    private static bool IsInstantGamingProductAvailable(JsonElement hit, string countryCode)
    {
        if (ReadInteger(hit, "has_stock") != 1 || ReadInteger(hit, "is_draft") == 1) return false;
        var platforms = ReadStringArray(hit, "platform_names");
        if (!platforms.Contains("PC", StringComparer.OrdinalIgnoreCase)) return false;
        var blacklist = ReadStringArray(hit, "country_blacklist");
        if (blacklist.Contains(countryCode, StringComparer.OrdinalIgnoreCase)) return false;
        var whitelist = ReadStringArray(hit, "country_whitelist");
        return whitelist.Count == 0 ||
            whitelist.Contains("worldwide", StringComparer.OrdinalIgnoreCase) ||
            whitelist.Contains(countryCode, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ToolsForSteam", "0.4"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url,
        CancellationToken cancellationToken,
        bool ensureSuccess = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ToolsForSteam", "0.4"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (ensureSuccess) response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<IReadOnlyList<DirectStoreProduct>> SafeAsync(
        Func<CancellationToken, Task<IReadOnlyList<DirectStoreProduct>>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static DirectStoreProduct CreateRegionalProduct(
        string storeName,
        string productId,
        long? steamAppId,
        string title,
        string imageUrl,
        string headerImageUrl,
        string currency,
        decimal? price,
        decimal? regularPrice,
        string directUrl,
        int? reviewPercent,
        decimal popularity,
        string releaseText)
    {
        var normalizedCurrency = currency.ToUpperInvariant();
        var discount = price.HasValue && regularPrice is > 0 && price < regularPrice
            ? Math.Clamp((int)decimal.Round((regularPrice.Value - price.Value) / regularPrice.Value * 100m), 0, 100)
            : 0;
        return new DirectStoreProduct(
            storeName,
            productId,
            steamAppId,
            title,
            imageUrl,
            headerImageUrl,
            normalizedCurrency == "USD" ? price : null,
            normalizedCurrency == "USD" ? regularPrice : null,
            normalizedCurrency == "EUR" ? price : null,
            normalizedCurrency == "EUR" ? regularPrice : null,
            price,
            regularPrice,
            normalizedCurrency,
            discount,
            directUrl,
            reviewPercent,
            popularity,
            releaseText);
    }

    private static DirectStoreProduct? MergeRegional(
        DirectStoreProduct? usd,
        DirectStoreProduct? eur,
        DirectStoreProduct? regional)
    {
        var primary = regional ?? usd ?? eur;
        if (primary is null) return null;
        return primary with
        {
            PriceUsd = usd?.PriceUsd ?? eur?.PriceUsd,
            RegularPriceUsd = usd?.RegularPriceUsd ?? eur?.RegularPriceUsd,
            PriceEur = eur?.PriceEur ?? usd?.PriceEur,
            RegularPriceEur = eur?.RegularPriceEur ?? usd?.RegularPriceEur,
            PriceRegional = regional?.PriceRegional ?? primary.PriceRegional,
            RegularPriceRegional = regional?.RegularPriceRegional ?? primary.RegularPriceRegional,
            RegionalCurrencyCode = FirstNotEmpty(regional?.RegionalCurrencyCode, primary.RegionalCurrencyCode),
            DiscountPercent = regional?.DiscountPercent ?? Math.Max(usd?.DiscountPercent ?? 0, eur?.DiscountPercent ?? 0),
            ImageUrl = FirstNotEmpty(primary.ImageUrl, regional?.ImageUrl, usd?.ImageUrl, eur?.ImageUrl),
            HeaderImageUrl = FirstNotEmpty(primary.HeaderImageUrl, regional?.HeaderImageUrl, usd?.HeaderImageUrl, eur?.HeaderImageUrl)
        };
    }

    private static IReadOnlyList<DirectStoreProduct> MergeRegionalLists(
        IReadOnlyList<DirectStoreProduct> usd,
        IReadOnlyList<DirectStoreProduct> eur,
        IReadOnlyList<DirectStoreProduct> regional)
    {
        var keys = usd.Concat(eur).Concat(regional)
            .Select(product => $"{product.StoreName}:{product.ProductId}")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return keys.Select(key => MergeRegional(
                usd.FirstOrDefault(product => key.Equals($"{product.StoreName}:{product.ProductId}", StringComparison.OrdinalIgnoreCase)),
                eur.FirstOrDefault(product => key.Equals($"{product.StoreName}:{product.ProductId}", StringComparison.OrdinalIgnoreCase)),
                regional.FirstOrDefault(product => key.Equals($"{product.StoreName}:{product.ProductId}", StringComparison.OrdinalIgnoreCase))))
            .OfType<DirectStoreProduct>()
            .ToArray();
    }

    private static DirectStoreProduct? SelectBestTitleMatch(
        IReadOnlyList<DirectStoreProduct> products,
        string requestedTitle)
    {
        var requested = NormalizeTitle(requestedTitle);
        return products
            .Select(product => new { Product = product, Score = ScoreTitle(NormalizeTitle(product.Title), requested) })
            .Where(item => item.Score < int.MaxValue)
            .OrderBy(item => item.Score)
            .ThenByDescending(item => item.Product.Popularity)
            .Select(item => item.Product)
            .FirstOrDefault();
    }

    private static int ScoreTitle(string candidate, string requested)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(requested)) return int.MaxValue;
        if (candidate.Equals(requested, StringComparison.Ordinal)) return 0;
        if (requested.Length >= 5 && candidate.StartsWith(requested, StringComparison.Ordinal))
            return 10 + candidate.Length - requested.Length;
        if (candidate.Length >= 5 && requested.StartsWith(candidate, StringComparison.Ordinal))
            return 20 + requested.Length - candidate.Length;
        return int.MaxValue;
    }

    internal static string NormalizeTitle(string? value)
    {
        return new string((value ?? string.Empty)
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static IReadOnlyList<string> ExtractXboxProductIds(string html)
    {
        var channel = XboxSearchChannelRegex().Match(html);
        if (!channel.Success) return [];
        return XboxProductIdRegex().Matches(channel.Groups["products"].Value)
            .Select(match => match.Groups["id"].Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static XboxPrice? FindXboxPurchasePrice(JsonElement product)
    {
        if (!product.TryGetProperty("DisplaySkuAvailabilities", out var skuNodes) ||
            skuNodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var prices = new List<XboxPrice>();
        foreach (var skuNode in skuNodes.EnumerateArray())
        {
            var sku = skuNode.TryGetProperty("Sku", out var parsedSku) ? parsedSku : default;
            var skuProperties = sku.TryGetProperty("Properties", out var parsedProperties)
                ? parsedProperties
                : default;
            if (ReadBoolean(sku, "IsTrial") ||
                ReadBoolean(skuProperties, "IsTrial") ||
                ReadString(sku, "SkuType").Equals("trial", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!skuNode.TryGetProperty("Availabilities", out var availabilities) || availabilities.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var availability in availabilities.EnumerateArray())
            {
                if (!availability.TryGetProperty("Actions", out var actions) ||
                    actions.ValueKind != JsonValueKind.Array ||
                    !actions.EnumerateArray().Any(action => action.GetString()?.Equals("Purchase", StringComparison.OrdinalIgnoreCase) == true))
                    continue;
                if (!TryGetPath(availability, out var priceNode, "OrderManagementData", "Price")) continue;
                var price = ReadDecimal(priceNode, "ListPrice");
                var regular = ReadDecimal(priceNode, "MSRP") ?? price;
                var currency = ReadString(priceNode, "CurrencyCode");
                if (price.HasValue && !string.IsNullOrWhiteSpace(currency))
                    prices.Add(new XboxPrice(price.Value, regular ?? price.Value, currency));
            }
        }

        if (prices.Count == 0) return null;
        var paidPrices = prices.Where(price => price.Price > 0).ToArray();
        IEnumerable<XboxPrice> candidates = paidPrices.Length > 0 ? paidPrices : prices;
        return candidates
            .OrderBy(price => price.Price)
            .First();
    }

    private static int? ResolveXboxRating(JsonElement product)
    {
        var market = FirstArrayObject(product, "MarketProperties");
        if (!market.TryGetProperty("UsageData", out var usage) || usage.ValueKind != JsonValueKind.Array) return null;
        var allTime = usage.EnumerateArray().FirstOrDefault(item =>
            ReadString(item, "AggregateTimeSpan").Equals("AllTime", StringComparison.OrdinalIgnoreCase));
        var average = ReadDecimal(allTime, "AverageRating");
        return average.HasValue ? Math.Clamp((int)decimal.Round(average.Value / 5m * 100m), 0, 100) : null;
    }

    private static string ResolveXboxImage(IReadOnlyList<JsonElement> images, params string[] purposes)
    {
        foreach (var purpose in purposes)
        {
            var value = images.FirstOrDefault(image =>
                ReadString(image, "ImagePurpose").Equals(purpose, StringComparison.OrdinalIgnoreCase));
            var uri = ReadString(value, "Uri");
            if (!string.IsNullOrWhiteSpace(uri)) return uri.StartsWith("//", StringComparison.Ordinal) ? $"https:{uri}" : uri;
        }
        return string.Empty;
    }

    private static string ResolveEpicImage(IReadOnlyList<JsonElement> images, params string[] types)
    {
        foreach (var type in types)
        {
            var value = images.FirstOrDefault(image =>
                ReadString(image, "type").Equals(type, StringComparison.OrdinalIgnoreCase));
            var uri = ReadString(value, "url");
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        return string.Empty;
    }

    private static JsonElement FirstArrayObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array) return default;
        return array.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
    }

    private static bool TryGetPath(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (var part in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(part, out result)) return false;
        }
        return true;
    }

    private static decimal? MinorPrice(JsonElement element, string propertyName)
    {
        var value = ReadDecimal(element, propertyName);
        return value.HasValue ? decimal.Round(value.Value / 100m, 2) : null;
    }

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizeWebLocale(string? locale)
    {
        var parts = (locale ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            ? $"{parts[0].ToLowerInvariant()}-{parts[1].ToUpperInvariant()}"
            : "en-US";
    }

    private static string NormalizeEpicStoreLanguage(string? locale)
    {
        var normalized = NormalizeWebLocale(locale);
        var language = normalized.Split('-', 2)[0];
        return language switch
        {
            "en" => normalized.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "en",
            "es" => normalized.Equals("es-MX", StringComparison.OrdinalIgnoreCase) ? "es-MX" : "es-ES",
            "pt" => "pt-BR",
            "zh" => "zh-CN",
            _ => language
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString()
            : string.Empty;
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number)) return number;
        return decimal.TryParse(property.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? ReadInteger(JsonElement element, string propertyName)
    {
        var value = ReadDecimal(element, propertyName);
        return value.HasValue ? (int)decimal.Round(value.Value) : null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number)) return number;
        return long.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property)) return false;
        return property.ValueKind == JsonValueKind.True ||
            bool.TryParse(property.ToString(), out var parsed) && parsed;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    [GeneratedRegex("SEARCH_GAMES_SEARCHQUERY=.*?\\\"products\\\":\\[(?<products>.*?)\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XboxSearchChannelRegex();

    [GeneratedRegex("\\\"productId\\\":\\\"(?<id>[A-Z0-9]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex XboxProductIdRegex();

    [GeneratedRegex("window\\.searchResults\\s*=\\s*(?<json>\\{[\\s\\S]*?\\});\\s*(?:\\r?\\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex InstantGamingSearchResultsRegex();

    private sealed record CachedProducts(DateTimeOffset FetchedAtUtc, IReadOnlyList<DirectStoreProduct> Products);
    private readonly record struct XboxPrice(decimal Price, decimal RegularPrice, string CurrencyCode);
}

internal sealed record DirectStoreProduct(
    string StoreName,
    string ProductId,
    long? SteamAppId,
    string Title,
    string ImageUrl,
    string HeaderImageUrl,
    decimal? PriceUsd,
    decimal? RegularPriceUsd,
    decimal? PriceEur,
    decimal? RegularPriceEur,
    decimal? PriceRegional,
    decimal? RegularPriceRegional,
    string RegionalCurrencyCode,
    int DiscountPercent,
    string DirectUrl,
    int? ReviewPercent,
    decimal Popularity,
    string ReleaseText);
