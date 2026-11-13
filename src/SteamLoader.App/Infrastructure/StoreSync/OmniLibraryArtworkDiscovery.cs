using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal enum ArtworkAttemptState
{
    Success,
    Missing,
    Unavailable,
}

internal readonly record struct ArtworkAttempt<T>(ArtworkAttemptState State, T? Value)
{
    public static ArtworkAttempt<T> Success(T value) => new(ArtworkAttemptState.Success, value);
    public static ArtworkAttempt<T> Missing => new(ArtworkAttemptState.Missing, default);
    public static ArtworkAttempt<T> Unavailable => new(ArtworkAttemptState.Unavailable, default);
}

internal sealed record GameTdbCoverCandidate(string Url, double? CropFrontCover = null);

/// <summary>
/// Network discovery for automatic artwork, ported from ORBIT
/// (artworkFallback.ts, retro/gameTdbArtwork.ts, steamGridDb.ts). Lookups are
/// cached in memory per process; outages go through the shared circuit breaker
/// and are never cached, so they retry instead of being remembered as missing.
/// </summary>
internal static class OmniLibraryArtworkDiscovery
{
    private const int MaximumDiscoveryBytes = 4 * 1024 * 1024;
    private const int MaximumGameTdbBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan MissingCacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan SteamGridDbFoundLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan GameTdbDatabaseLifetime = TimeSpan.FromDays(7);
    private const int MaximumSteamGridDbAssets = 24;

    private static readonly ConcurrentDictionary<string, CachedAttempt<int>> SteamStoreCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedAttempt<string[]>> LibretroIndexCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedAttempt<int>> SteamGridDbSearchCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, GameTdbDatabase> GameTdbDatabases = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim GameTdbGate = new(1, 1);

    internal static string GameTdbCacheDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "data", "artwork-cache", "gametdb");

    internal static void ClearCaches()
    {
        SteamStoreCache.Clear();
        LibretroIndexCache.Clear();
        SteamGridDbSearchCache.Clear();
        GameTdbDatabases.Clear();
    }

    public static string UiLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();

    internal static async Task<ArtworkAttempt<T>> RunAsync<T>(
        string scope,
        Func<Task<ArtworkAttempt<T>>> attempt)
    {
        if (!OmniLibraryArtworkNetworkPolicy.TryBegin(scope))
        {
            return ArtworkAttempt<T>.Unavailable;
        }

        try
        {
            var result = await attempt().ConfigureAwait(false);
            if (result.State == ArtworkAttemptState.Unavailable)
            {
                OmniLibraryArtworkNetworkPolicy.RecordUnavailable(scope);
            }
            else
            {
                OmniLibraryArtworkNetworkPolicy.RecordAvailable(scope);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            // Timeouts surface as TimeoutException; this is the caller stopping
            // the work, which says nothing about the source's health.
            OmniLibraryArtworkNetworkPolicy.RecordAvailable(scope);
            throw;
        }
        catch
        {
            OmniLibraryArtworkNetworkPolicy.RecordUnavailable(scope);
            return ArtworkAttempt<T>.Unavailable;
        }
    }

    private static ArtworkAttempt<T>? ReadCache<T>(
        ConcurrentDictionary<string, CachedAttempt<T>> cache,
        string key)
    {
        return cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow
            ? cached.Result
            : null;
    }

    private static void WriteCache<T>(
        ConcurrentDictionary<string, CachedAttempt<T>> cache,
        string key,
        ArtworkAttempt<T> result,
        TimeSpan? successLifetime = null)
    {
        if (result.State == ArtworkAttemptState.Unavailable)
        {
            return;
        }
        if (cache.Count > 2048)
        {
            cache.Clear();
        }
        cache[key] = new CachedAttempt<T>(
            DateTime.UtcNow + (result.State == ArtworkAttemptState.Success
                ? successLifetime ?? SuccessCacheLifetime
                : MissingCacheLifetime),
            result);
    }

    private static async Task<string?> ReadTextLimitedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } announced && announced > maximumBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        try
        {
            return await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Artwork discovery timed out: {new Uri(url).Host}");
        }
    }

    private static ArtworkAttemptState FailureState(HttpStatusCode status) =>
        OmniLibraryArtworkRules.IsTransientStatus((int)status)
            ? ArtworkAttemptState.Unavailable
            : ArtworkAttemptState.Missing;

    /// <summary>
    /// Finds a Steam app for non-Steam records through Steam's public store
    /// search. Only an exact canonical title is accepted, so editions with
    /// similar names never receive an automatic false-positive cover.
    /// </summary>
    public static async Task<ArtworkAttempt<int>> FindExactSteamStoreAppAsync(
        HttpClient client,
        string title,
        CancellationToken cancellationToken)
    {
        var normalized = OmniLibraryArtworkRules.CanonicalArtworkTitle(title);
        if (normalized.Length < 3)
        {
            return ArtworkAttempt<int>.Missing;
        }

        var key = $"US:{normalized}";
        if (ReadCache(SteamStoreCache, key) is { } cached)
        {
            return cached;
        }

        var result = await RunAsync("artwork-discovery:store.steampowered.com", async () =>
        {
            var term = title.Trim();
            if (term.Length > 120)
            {
                term = term[..120];
            }
            using var response = await GetAsync(
                client,
                $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ArtworkAttempt<int>(FailureState(response.StatusCode), 0);
            }

            var text = await ReadTextLimitedAsync(response, MaximumDiscoveryBytes, cancellationToken)
                .ConfigureAwait(false);
            if (text is null)
            {
                return ArtworkAttempt<int>.Missing;
            }

            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return ArtworkAttempt<int>.Missing;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "app" &&
                    item.TryGetProperty("id", out var idNode) &&
                    idNode.TryGetInt32(out var id) &&
                    id > 0 &&
                    item.TryGetProperty("name", out var nameNode) &&
                    nameNode.ValueKind == JsonValueKind.String &&
                    OmniLibraryArtworkRules.CanonicalArtworkTitle(nameNode.GetString()) == normalized)
                {
                    return ArtworkAttempt<int>.Success(id);
                }
            }
            return ArtworkAttempt<int>.Missing;
        }).ConfigureAwait(false);
        WriteCache(SteamStoreCache, key, result);
        return result;
    }

    /// <summary>Official Steam asset URLs for an exact store match, best first.</summary>
    public static IReadOnlyList<string> SteamStoreArtworkUrls(
        int appId,
        OmniLibraryArtworkOrientation orientation)
    {
        var fastly = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}";
        var cloudflare = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}";
        return orientation switch
        {
            OmniLibraryArtworkOrientation.Portrait =>
            [
                $"{fastly}/library_600x900_2x.jpg",
                $"{fastly}/library_600x900.jpg",
                $"{cloudflare}/library_600x900.jpg",
            ],
            OmniLibraryArtworkOrientation.Hero =>
            [
                $"{fastly}/library_hero.jpg",
                $"{cloudflare}/library_hero.jpg",
                $"https://store.akamai.steamstatic.com/images/storepagebackground/app/{appId}",
                $"{fastly}/header.jpg",
                $"{cloudflare}/header.jpg",
            ],
            // Title-bearing wide Store headers (ORBIT steamStoreHeaderUrls).
            OmniLibraryArtworkOrientation.Capsule =>
            [
                $"{fastly}/header_2x.jpg",
                $"{fastly}/header.jpg",
                $"{cloudflare}/header.jpg",
            ],
            OmniLibraryArtworkOrientation.Logo =>
            [
                $"{fastly}/library_logo.png",
                $"{fastly}/logo.png",
                $"{cloudflare}/library_logo.png",
                $"{cloudflare}/logo.png",
            ],
            _ => [],
        };
    }

    public static async Task<ArtworkAttempt<string>> FindLibretroArtworkAsync(
        HttpClient client,
        string playlist,
        LibretroArtworkFolder folder,
        string gameName,
        string? romPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlist))
        {
            return ArtworkAttempt<string>.Missing;
        }

        var index = await FetchLibretroIndexAsync(client, playlist, folder, cancellationToken)
            .ConfigureAwait(false);
        if (index.State != ArtworkAttemptState.Success)
        {
            return new ArtworkAttempt<string>(index.State, null);
        }

        var fileName = OmniLibraryArtworkRules.MatchLibretroThumbnail(
            index.Value!,
            gameName,
            RomFileTitle(romPath),
            UiLanguage);
        return fileName is null
            ? ArtworkAttempt<string>.Missing
            : ArtworkAttempt<string>.Success(
                $"https://thumbnails.libretro.com/{Uri.EscapeDataString(playlist)}/{folder}/{Uri.EscapeDataString(fileName)}");
    }

    private static string? RomFileTitle(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
        {
            return null;
        }
        var title = Path.GetFileNameWithoutExtension(romPath);
        return title.Equals("EBOOT", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(romPath))
            : title;
    }

    private static async Task<ArtworkAttempt<string[]>> FetchLibretroIndexAsync(
        HttpClient client,
        string playlist,
        LibretroArtworkFolder folder,
        CancellationToken cancellationToken)
    {
        var key = $"{playlist}:{folder}";
        if (ReadCache(LibretroIndexCache, key) is { } cached)
        {
            return cached;
        }

        var result = await RunAsync("artwork-discovery:thumbnails.libretro.com", async () =>
        {
            using var response = await GetAsync(
                client,
                $"https://thumbnails.libretro.com/{Uri.EscapeDataString(playlist)}/{folder}/",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ArtworkAttempt<string[]>(FailureState(response.StatusCode), null);
            }
            var html = await ReadTextLimitedAsync(response, MaximumDiscoveryBytes, cancellationToken)
                .ConfigureAwait(false);
            if (html is null)
            {
                return ArtworkAttempt<string[]>.Missing;
            }
            var files = ParseLibretroIndex(html);
            return files.Length > 0
                ? ArtworkAttempt<string[]>.Success(files)
                : ArtworkAttempt<string[]>.Missing;
        }).ConfigureAwait(false);
        WriteCache(LibretroIndexCache, key, result);
        return result;
    }

    internal static string[] ParseLibretroIndex(string html)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(html, "href=\"([^\"]+\\.png)\"", RegexOptions.IgnoreCase))
        {
            var decoded = match.Groups[1].Value
                .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase)
                .Replace("&apos;", "'", StringComparison.OrdinalIgnoreCase)
                .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase);
            try
            {
                decoded = Uri.UnescapeDataString(decoded);
            }
            catch
            {
            }
            if (decoded.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !decoded.Contains('/') &&
                !decoded.Contains('\\') &&
                !decoded.Contains("..", StringComparison.Ordinal))
            {
                files.Add(decoded);
            }
        }
        return files.ToArray();
    }

    private static string? GameTdbPlatform(string? systemId) => systemId?.Trim().ToLowerInvariant() switch
    {
        "gamecube" or "wii" => "wii",
        "wii-u" => "wiiu",
        "switch" => "switch",
        _ => null,
    };

    public static bool IsGameTdbSystem(string? systemId) => GameTdbPlatform(systemId) is not null;

    /// <summary>
    /// Official retail cover scans for GameCube, Wii and Wii U from GameTDB.
    /// A disc/product ID read from the ROM is exact; other images fall back to
    /// an exact compact title match against GameTDB's public title list.
    /// </summary>
    public static async Task<ArtworkAttempt<IReadOnlyList<GameTdbCoverCandidate>>> FindGameTdbCoversAsync(
        HttpClient client,
        string? systemId,
        string gameName,
        string? romPath,
        CancellationToken cancellationToken)
    {
        var platform = GameTdbPlatform(systemId);
        if (platform is null)
        {
            return ArtworkAttempt<IReadOnlyList<GameTdbCoverCandidate>>.Missing;
        }

        var database = await LoadGameTdbDatabaseAsync(client, platform, cancellationToken).ConfigureAwait(false);
        if (database.State != ArtworkAttemptState.Success)
        {
            return new ArtworkAttempt<IReadOnlyList<GameTdbCoverCandidate>>(database.State, null);
        }

        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(romPath) && File.Exists(romPath))
        {
            if (platform == "wii" && DiscImageId(romPath) is { } discId)
            {
                ids.Add(discId);
            }
            else if (platform == "wiiu" && WiiUProductCode(romPath) is { } productCode)
            {
                ids.AddRange(database.Value!.Ids.Where(id => id.StartsWith(productCode, StringComparison.Ordinal)));
            }
        }

        if (ids.Count == 0)
        {
            var keys = new[] { RomFileTitle(romPath), gameName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(OmniLibraryArtworkRules.CompactCatalogTitleKey)
                .Where(key => key.Length >= 3)
                .Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (database.Value!.ByTitle.TryGetValue(key, out var matches))
                {
                    ids.AddRange(matches);
                }
            }
        }

        if (ids.Count == 0)
        {
            return ArtworkAttempt<IReadOnlyList<GameTdbCoverCandidate>>.Missing;
        }

        var candidates = RankGameTdbIds(ids.Distinct(StringComparer.Ordinal), platform, romPath ?? gameName)
            .Take(2)
            .SelectMany(id => GameTdbCoverCandidates(id, platform, systemId!))
            .ToArray();
        return ArtworkAttempt<IReadOnlyList<GameTdbCoverCandidate>>.Success(candidates);
    }

    private static IEnumerable<string> RankGameTdbIds(IEnumerable<string> ids, string platform, string romPath)
    {
        var tags = Path.GetFileNameWithoutExtension(romPath).ToLowerInvariant();
        var preferred = Regex.IsMatch(tags, @"\b(europe|germany|france|spain|italy|uk)\b")
            ? 'P'
            : Regex.IsMatch(tags, @"\busa\b")
                ? 'E'
                : Regex.IsMatch(tags, @"\bjapan\b")
                    ? 'J'
                    : UiLanguage == "en" ? 'E' : 'P';
        // Switch title IDs carry no region letter.
        char RegionOf(string id) => platform != "switch" && id.Length > 3 ? id[3] : ' ';
        int Score(string id) => RegionOf(id) == preferred ? 2 : RegionOf(id) == 'E' ? 1 : 0;
        return ids.OrderByDescending(Score);
    }

    private static IEnumerable<GameTdbCoverCandidate> GameTdbCoverCandidates(
        string id,
        string platform,
        string systemId)
    {
        var languageRegion = UiLanguage switch
        {
            "de" => "DE",
            "es" => "ES",
            "fr" => "FR",
            "ru" => "RU",
            _ => null,
        };
        var regionCode = platform != "switch" && id.Length > 3 ? id[3] : ' ';
        IEnumerable<string?> primary = regionCode switch
        {
            'E' => ["US"],
            'J' => ["JA"],
            'K' => ["KO"],
            _ => [languageRegion, "EN"],
        };
        var regions = primary
            .Concat(["EN", "US", "JA"])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (platform == "wii")
        {
            // GameTDB's full case scans place the front cover right of the spine.
            double? frontCoverStart = systemId.Equals("gamecube", StringComparison.OrdinalIgnoreCase)
                ? 546d / 1024d
                : 531d / 1024d;
            foreach (var region in regions)
            {
                yield return new($"https://art.gametdb.com/wii/coverfullHQ/{region}/{id}.png", frontCoverStart);
            }
            foreach (var region in regions)
            {
                yield return new($"https://art.gametdb.com/wii/cover/{region}/{id}.png");
            }
            yield break;
        }

        foreach (var kind in new[] { "coverHQ", "coverM" })
        {
            foreach (var region in regions)
            {
                yield return new($"https://art.gametdb.com/{platform}/{kind}/{region}/{id}.jpg");
            }
        }
    }

    private static async Task<ArtworkAttempt<GameTdbDatabase>> LoadGameTdbDatabaseAsync(
        HttpClient client,
        string platform,
        CancellationToken cancellationToken)
    {
        if (GameTdbDatabases.TryGetValue(platform, out var loaded))
        {
            return ArtworkAttempt<GameTdbDatabase>.Success(loaded);
        }

        await GameTdbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (GameTdbDatabases.TryGetValue(platform, out loaded))
            {
                return ArtworkAttempt<GameTdbDatabase>.Success(loaded);
            }

            var databaseName = platform switch
            {
                "wiiu" => "wiiutdb",
                "switch" => "switchtdb",
                _ => "wiitdb",
            };
            var path = Path.Combine(GameTdbCacheDirectory, $"{databaseName}.txt");
            var fresh = ReadCachedGameTdbDatabase(path, allowStale: false);
            if (fresh is not null)
            {
                GameTdbDatabases[platform] = fresh;
                return ArtworkAttempt<GameTdbDatabase>.Success(fresh);
            }

            var downloaded = await RunAsync("artwork-discovery:www.gametdb.com", async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await client.GetAsync(
                    $"https://www.gametdb.com/{databaseName}.txt?LANG=EN",
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new ArtworkAttempt<GameTdbDatabase>(FailureState(response.StatusCode), null);
                }
                var text = await ReadTextLimitedAsync(response, MaximumGameTdbBytes, timeout.Token)
                    .ConfigureAwait(false);
                if (text is null)
                {
                    return ArtworkAttempt<GameTdbDatabase>.Missing;
                }
                var database = ParseGameTdbDatabase(text);
                if (database.Ids.Count == 0)
                {
                    return ArtworkAttempt<GameTdbDatabase>.Missing;
                }
                try
                {
                    Directory.CreateDirectory(GameTdbCacheDirectory);
                    await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
                return ArtworkAttempt<GameTdbDatabase>.Success(database);
            }).ConfigureAwait(false);

            // An outdated list still identifies games; it only lacks newer releases.
            var result = downloaded.State == ArtworkAttemptState.Success
                ? downloaded
                : ReadCachedGameTdbDatabase(path, allowStale: true) is { } stale
                    ? ArtworkAttempt<GameTdbDatabase>.Success(stale)
                    : downloaded;
            if (result.State == ArtworkAttemptState.Success)
            {
                GameTdbDatabases[platform] = result.Value!;
            }
            return result;
        }
        finally
        {
            GameTdbGate.Release();
        }
    }

    private static GameTdbDatabase? ReadCachedGameTdbDatabase(string path, bool allowStale)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists ||
                (!allowStale && DateTime.UtcNow - info.LastWriteTimeUtc > GameTdbDatabaseLifetime))
            {
                return null;
            }
            var database = ParseGameTdbDatabase(File.ReadAllText(path));
            return database.Ids.Count > 0 ? database : null;
        }
        catch
        {
            return null;
        }
    }

    internal static GameTdbDatabase ParseGameTdbDatabase(string text)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var byTitle = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), "^([A-Z0-9]{4,6}) = (.+)$");
            if (!match.Success)
            {
                continue;
            }
            var id = match.Groups[1].Value;
            ids.Add(id);
            var key = OmniLibraryArtworkRules.CompactCatalogTitleKey(match.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            if (!byTitle.TryGetValue(key, out var list))
            {
                list = [];
                byTitle[key] = list;
            }
            list.Add(id);
        }
        return new GameTdbDatabase(ids, byTitle);
    }

    private static string? ReadHeader(string path, long offset, int length)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < offset + length)
            {
                return null;
            }
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return Encoding.Latin1.GetString(buffer, 0, read);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Disc ID stored in plain text by GameCube/Wii images and their containers.</summary>
    internal static string? DiscImageId(string romPath)
    {
        var magic = ReadHeader(romPath, 0, 4);
        if (magic is null)
        {
            return null;
        }
        var offset = magic switch
        {
            "RVZ\u0001" or "WIA\u0001" => 0x58,
            "WBFS" => 0x200,
            "CISO" => 0x8000,
            _ => 0,
        };
        var id = ReadHeader(romPath, offset, 6);
        return id is not null && Regex.IsMatch(id, "^[A-Z0-9]{6}$") ? id : null;
    }

    private static string? WiiUProductCode(string romPath)
    {
        var extension = Path.GetExtension(romPath).ToLowerInvariant();
        if (extension == ".wud")
        {
            var header = ReadHeader(romPath, 0, 10);
            var match = header is null ? Match.Empty : Regex.Match(header, "^WUP-[A-Z]-([A-Z0-9]{4})$");
            return match.Success ? match.Groups[1].Value : null;
        }
        if (extension == ".rpx")
        {
            try
            {
                var metaPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetDirectoryName(romPath)) ?? string.Empty,
                    "meta",
                    "meta.xml");
                var match = Regex.Match(
                    File.ReadAllText(metaPath),
                    @"<product_code[^>]*>\s*WUP-[A-Z]-([A-Z0-9]{4})\s*<");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public static bool IsSteamGridDbAssetUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            url.Port != 443)
        {
            return false;
        }
        var host = url.Host.ToLowerInvariant();
        return host is "cdn.steamgriddb.com" or "cdn2.steamgriddb.com" or "steamgriddb.s3.amazonaws.com" ||
               (host == "s3.amazonaws.com" && url.AbsolutePath.StartsWith("/steamgriddb/", StringComparison.Ordinal));
    }

    /// <summary>Searches SteamGridDB with the title and an edition-stripped alias, exact matches only.</summary>
    public static async Task<ArtworkAttempt<SteamGridDbGameHit>> FindSteamGridDbGameAsync(
        HttpClient steamGridDbClient,
        string title,
        CancellationToken cancellationToken)
    {
        var key = OmniLibraryArtworkRules.SteamGridDbSearchKey(title);
        if (string.IsNullOrWhiteSpace(key))
        {
            return ArtworkAttempt<SteamGridDbGameHit>.Missing;
        }
        if (ReadCache(SteamGridDbSearchCache, key) is { } cached)
        {
            return cached.State == ArtworkAttemptState.Success
                ? ArtworkAttempt<SteamGridDbGameHit>.Success(new SteamGridDbGameHit(cached.Value, title))
                : ArtworkAttempt<SteamGridDbGameHit>.Missing;
        }

        var queries = new[] { title.Trim(), OmniLibraryArtworkRules.StripSteamGridDbEditionWords(title) }
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var query in queries)
        {
            var result = await RunAsync(SteamGridDbScope, async () =>
            {
                using var response = await GetAsync(
                    steamGridDbClient,
                    $"search/autocomplete/{Uri.EscapeDataString(query)}",
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new ArtworkAttempt<SteamGridDbGameHit>(
                        OmniLibraryArtworkRules.IsTransientStatus((int)response.StatusCode) ||
                        response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            ? ArtworkAttemptState.Unavailable
                            : ArtworkAttemptState.Missing,
                        null);
                }
                var text = await ReadTextLimitedAsync(response, MaximumDiscoveryBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (text is null)
                {
                    return ArtworkAttempt<SteamGridDbGameHit>.Missing;
                }
                using var document = JsonDocument.Parse(text);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return ArtworkAttempt<SteamGridDbGameHit>.Missing;
                }
                var games = data.EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out var id) && id.TryGetInt32(out _))
                    .Select(item => new SteamGridDbGameHit(
                        item.GetProperty("id").GetInt32(),
                        item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                            ? name.GetString() ?? string.Empty
                            : string.Empty))
                    .ToArray();
                var match = OmniLibraryArtworkRules.SelectSteamGridDbGame(title, games, game => game.Name);
                return match is null
                    ? ArtworkAttempt<SteamGridDbGameHit>.Missing
                    : ArtworkAttempt<SteamGridDbGameHit>.Success(match);
            }).ConfigureAwait(false);
            if (result.State == ArtworkAttemptState.Unavailable)
            {
                return result;
            }
            if (result.State == ArtworkAttemptState.Success)
            {
                WriteCache(
                    SteamGridDbSearchCache,
                    key,
                    ArtworkAttempt<int>.Success(result.Value!.GameId),
                    SteamGridDbFoundLifetime);
                return result;
            }
        }

        WriteCache(SteamGridDbSearchCache, key, ArtworkAttempt<int>.Missing);
        return ArtworkAttempt<SteamGridDbGameHit>.Missing;
    }

    private const string SteamGridDbScope = "steamgriddb";

    /// <summary>
    /// SteamGridDB assets for one Steam slot in SteamGridDB's relevance order.
    /// Dimensions match ORBIT; the PNG/JPEG restriction exists because the
    /// Windows image decoder used for validation cannot read WebP.
    /// </summary>
    public static async Task<ArtworkAttempt<IReadOnlyList<string>>> FindSteamGridDbAssetsAsync(
        HttpClient steamGridDbClient,
        int gameId,
        OmniLibraryArtworkOrientation orientation,
        CancellationToken cancellationToken)
    {
        var (asset, dimensions) = orientation switch
        {
            OmniLibraryArtworkOrientation.Portrait => ("grids", "600x900"),
            OmniLibraryArtworkOrientation.Capsule => ("grids", "920x430,460x215"),
            OmniLibraryArtworkOrientation.Hero => ("heroes", "1920x620,3840x1240"),
            OmniLibraryArtworkOrientation.Logo => ("logos", string.Empty),
            _ => (string.Empty, string.Empty),
        };
        if (string.IsNullOrWhiteSpace(asset) || gameId <= 0)
        {
            return ArtworkAttempt<IReadOnlyList<string>>.Missing;
        }

        var mimes = orientation == OmniLibraryArtworkOrientation.Logo ? "image/png" : "image/png,image/jpeg";
        var query = $"{asset}/game/{gameId}?types=static&nsfw=false&epilepsy=false&mimes={Uri.EscapeDataString(mimes)}" +
            (string.IsNullOrWhiteSpace(dimensions) ? string.Empty : $"&dimensions={Uri.EscapeDataString(dimensions)}");
        return await RunAsync<IReadOnlyList<string>>(SteamGridDbScope, async () =>
        {
            using var response = await GetAsync(steamGridDbClient, query, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ArtworkAttempt<IReadOnlyList<string>>(FailureState(response.StatusCode), null);
            }
            var text = await ReadTextLimitedAsync(response, MaximumDiscoveryBytes, cancellationToken)
                .ConfigureAwait(false);
            if (text is null)
            {
                return ArtworkAttempt<IReadOnlyList<string>>.Missing;
            }
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return ArtworkAttempt<IReadOnlyList<string>>.Missing;
            }
            var seen = new HashSet<int>();
            var urls = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idNode) ||
                    !idNode.TryGetInt32(out var id) ||
                    id <= 0 ||
                    !seen.Add(id) ||
                    !item.TryGetProperty("url", out var urlNode) ||
                    urlNode.ValueKind != JsonValueKind.String ||
                    !IsSteamGridDbAssetUrl(urlNode.GetString()))
                {
                    continue;
                }
                urls.Add(urlNode.GetString()!);
                if (urls.Count >= MaximumSteamGridDbAssets)
                {
                    break;
                }
            }
            return urls.Count > 0
                ? ArtworkAttempt<IReadOnlyList<string>>.Success(urls)
                : ArtworkAttempt<IReadOnlyList<string>>.Missing;
        }).ConfigureAwait(false);
    }

    private sealed record CachedAttempt<T>(DateTime ExpiresAtUtc, ArtworkAttempt<T> Result);

    internal sealed record GameTdbDatabase(
        IReadOnlySet<string> Ids,
        IReadOnlyDictionary<string, List<string>> ByTitle);
}

internal sealed record SteamGridDbGameHit(int GameId, string Name);
