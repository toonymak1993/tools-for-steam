using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed class SteamGridDbArtworkDownloader
{
    public const string BuiltInApiKey = "96b06c7e805c21ee48af894587118c4c";
    private const long MaximumArtworkBytes = 32L * 1024 * 1024;
    private const int MinimumArtworkBytes = 128;
    private const int MaximumArtworkValidationEntries = 8192;
    private static readonly ConcurrentDictionary<string, ArtworkValidationEntry>
        ArtworkValidationCache = new(StringComparer.OrdinalIgnoreCase);
    private static long _lastStagingCleanupUtcTicks;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ArtworkSlot[] ArtworkSlots =
    [
        new(
            SlotName: "library capsule",
            RequestPaths:
            [
                "grids/game/{0}?types=static&dimensions=920x430&mimes=image/png,image/jpeg",
                "grids/game/{0}?types=static&mimes=image/png,image/jpeg"
            ],
            FileStemBuilder: gridId => gridId,
            PreferredWidth: 920,
            PreferredHeight: 430,
            SupportsBadge: true),
        new(
            SlotName: "portrait",
            RequestPaths:
            [
                "grids/game/{0}?types=static&dimensions=600x900&mimes=image/png,image/jpeg",
                "grids/game/{0}?types=static&mimes=image/png,image/jpeg"
            ],
            FileStemBuilder: gridId => $"{gridId}p",
            PreferredWidth: 600,
            PreferredHeight: 900,
            SupportsBadge: true),
        new(
            SlotName: "hero",
            RequestPaths:
            [
                "heroes/game/{0}?types=static&dimensions=1920x620&mimes=image/png,image/jpeg",
                "heroes/game/{0}?types=static&mimes=image/png,image/jpeg"
            ],
            FileStemBuilder: gridId => $"{gridId}_hero",
            PreferredWidth: 1920,
            PreferredHeight: 620,
            SupportsBadge: true),
        new(
            SlotName: "logo",
            RequestPaths:
            [
                "logos/game/{0}?types=static&mimes=image/png"
            ],
            FileStemBuilder: gridId => $"{gridId}_logo",
            PreferredWidth: null,
            PreferredHeight: null,
            SupportsBadge: false),
        new(
            SlotName: "icon",
            RequestPaths:
            [
                "icons/game/{0}?types=static&dimensions=256&mimes=image/png,image/vnd.microsoft.icon",
                "icons/game/{0}?types=static&mimes=image/png,image/vnd.microsoft.icon"
            ],
            FileStemBuilder: gridId => $"{gridId}-icon",
            PreferredWidth: 256,
            PreferredHeight: 256,
            SupportsBadge: false),
    ];

    private static readonly IReadOnlyDictionary<string, string[]> KnownTitleAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["speedwell"] = ["Metro: Last Light Redux", "Metro Last Light Redux"],
        };

    private static readonly HashSet<string> IgnoredSearchHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "app",
        "apps",
        "binaries",
        "binary",
        "bin",
        "content",
        "engine",
        "epic games",
        "games",
        "launcher",
        "program files",
        "program files x86",
        "redist",
        "redistributables",
        "shipping",
        "steamapps",
        "tools",
        "win64",
        "win32",
        "windows",
        "x64",
        "x86",
    };

    public async Task<StoreSyncArtworkSummary> DownloadAsync(
        string gridDirectory,
        IReadOnlyList<StoreSyncArtworkTarget> targets,
        string apiKey,
        bool preferAnimatedArtwork,
        CancellationToken cancellationToken)
    {
        _ = preferAnimatedArtwork;

        if (targets.Count == 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            return new StoreSyncArtworkSummary(0, 0, []);
        }

        Directory.CreateDirectory(gridDirectory);
        CleanupStaleArtworkStagingFiles(gridDirectory);

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var searchCache = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var updatedTitleCount = 0;
        var updatedFileCount = 0;
        var updatedTitleIds = new List<string>();

        foreach (var target in targets
                     .GroupBy(target => target.AppId)
                     .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var match = target.CachedGameId.HasValue && target.CachedGameId.Value > 0
                    ? new StoreSyncArtworkMatch(target.CachedGameId.Value, target.CachedMatchName)
                    : await FindGameMatchAsync(
                        httpClient,
                        target.Title,
                        target.SearchHints,
                        searchCache,
                        cancellationToken);
                if (match is null)
                {
                    continue;
                }

                var updatedFilesForTitle = await DownloadArtworkSetAsync(
                    httpClient,
                    gridDirectory,
                    SteamShortcutIds.BuildGridId(target.AppId),
                    match.GameId,
                    target.StoreId,
                    cancellationToken);

                if (updatedFilesForTitle > 0)
                {
                    updatedTitleCount++;
                    updatedFileCount += updatedFilesForTitle;
                    updatedTitleIds.Add(target.TitleId);
                }
            }
            catch
            {
            }
        }

        return new StoreSyncArtworkSummary(updatedTitleCount, updatedFileCount, updatedTitleIds);
    }

    /// <summary>
    /// Fills every Steam library artwork slot progressively. Sources are strictly
    /// ordered: assets already present on this PC, provider-owned/free sources,
    /// the public Steam catalog, and SteamGridDB only as the final remote fallback.
    /// </summary>
    public async Task<StoreSyncArtworkSummary> DownloadLocalFirstAsync(
        string gridDirectory,
        IReadOnlyList<StoreSyncArtworkTarget> targets,
        string apiKey,
        CancellationToken cancellationToken,
        Action<int, int>? progress = null)
    {
        if (targets.Count == 0)
        {
            return new StoreSyncArtworkSummary(0, 0, []);
        }

        Directory.CreateDirectory(gridDirectory);
        CleanupStaleArtworkStagingFiles(gridDirectory);
        using var steamClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        steamClient.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsForSteam-OmniLibrary/1.0");

        using var steamGridDbClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            steamGridDbClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var steamSearchCache = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var steamGridDbSearchCache = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var localArtworkResolver = new OmniLibraryLocalArtworkResolver();
        var updatedTitleIds = new List<string>();
        var updatedFileCount = 0;

        var uniqueTargets = targets
            .GroupBy(target => target.AppId)
            .Select(group => group
                .OrderByDescending(target => target.ForceReload)
                .First())
            .ToArray();
        var completedTargetCount = 0;

        // Deliberately sequential per store: images appear one title at a time without a
        // large first-sync bandwidth or CPU spike in Big Picture.
        foreach (var target in uniqueTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updatedForTitle = 0;
            var gridId = SteamShortcutIds.BuildGridId(target.AppId);
            var workingGridDirectory = gridDirectory;
            string? reloadStagingDirectory = null;
            try
            {
                if (target.ForceReload)
                {
                    reloadStagingDirectory = Path.Combine(
                        gridDirectory,
                        $".tfs-artwork-reload-{gridId}-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(reloadStagingDirectory);
                    workingGridDirectory = reloadStagingDirectory;
                }

                // Stage 1: never use the network while a usable asset is already
                // present in Steam's cache, the provider install, a ROM sidecar,
                // or an explicit file:// catalog reference.
                updatedForTitle += await ImportLocalArtworkSetAsync(
                    workingGridDirectory,
                    gridId,
                    target,
                    localArtworkResolver,
                    cancellationToken).ConfigureAwait(false);

                if (HasAnyPrimaryArtwork(workingGridDirectory, gridId))
                {
                    updatedForTitle += EnsureCompleteArtworkSetFromExistingAssets(
                        workingGridDirectory,
                        gridId,
                        target.Title);
                }

                // Stage 2: exact provider/free sources. RetroAchievements is an
                // exact content-hash source for ROMs; Xbox/Epic/GOG catalog URLs
                // are provider-owned and therefore precede title-based matching.
                if (!HasCompleteArtworkSet(workingGridDirectory, target.AppId) &&
                    target.StoreId.Equals(
                        OmniLibraryRomSystemRegistry.StoreId,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(target.RomPath) &&
                    !string.IsNullOrWhiteSpace(target.RomSystemId) &&
                    !string.IsNullOrWhiteSpace(target.RetroAchievementsApiKey))
                {
                    try
                    {
                        updatedForTitle += await DownloadRetroAchievementsArtworkSetAsync(
                            steamClient,
                            workingGridDirectory,
                            gridId,
                            target,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // RetroAchievements is the preferred exact-ROM source.
                        // Steam and SteamGridDB remain independent fallbacks.
                    }
                }

                if (!HasCompleteArtworkSet(workingGridDirectory, target.AppId))
                {
                    updatedForTitle += await DownloadRemoteStoreArtworkSetAsync(
                        steamClient,
                        workingGridDirectory,
                        gridId,
                        target,
                        cancellationToken).ConfigureAwait(false);
                }

                // Stage 3: the public Steam Store API/CDN is free and needs no
                // user key. It is title-matched, so exact provider sources above
                // remain preferable.
                if (!HasCompleteArtworkSet(workingGridDirectory, target.AppId))
                {
                    var steamAppId = await ResolvePublicSteamAppIdAsync(
                        steamClient,
                        target.Title,
                        target.SearchHints,
                        steamSearchCache,
                        cancellationToken).ConfigureAwait(false);
                    if (steamAppId.HasValue)
                    {
                        updatedForTitle += await DownloadPublicSteamArtworkSetAsync(
                            steamClient,
                            workingGridDirectory,
                            gridId,
                            steamAppId.Value,
                            target.StoreId,
                            cancellationToken).ConfigureAwait(false);
                    }

                }

                if (HasAnyPrimaryArtwork(workingGridDirectory, gridId))
                {
                    updatedForTitle += EnsureCompleteArtworkSetFromExistingAssets(
                        workingGridDirectory,
                        gridId,
                        target.Title);
                }

                // Stage 4: SteamGridDB is intentionally last. A missing key,
                // outage, or rate limit can no longer prevent local, provider,
                // RetroAchievements, or public Steam artwork from being used.
                if (!HasCompleteArtworkSet(workingGridDirectory, target.AppId) &&
                    !string.IsNullOrWhiteSpace(apiKey))
                {
                    try
                    {
                        var match = target.CachedGameId.HasValue && target.CachedGameId.Value > 0
                            ? new StoreSyncArtworkMatch(target.CachedGameId.Value, target.CachedMatchName)
                            : await FindGameMatchAsync(
                                steamGridDbClient,
                                target.Title,
                                target.SearchHints,
                                steamGridDbSearchCache,
                                cancellationToken).ConfigureAwait(false);
                        if (match is not null)
                        {
                            updatedForTitle += await DownloadArtworkSetAsync(
                                steamGridDbClient,
                                workingGridDirectory,
                                gridId,
                                match.GameId,
                                target.StoreId,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // SteamGridDB is optional and must never block completion
                        // from assets already obtained earlier in the pipeline.
                    }
                }

                // Some games have a real portrait and hero but no matching
                // horizontal grid (or vice versa). Derive only the missing
                // Steam slots from an already downloaded real asset so the
                // title never remains permanently half-populated.
                updatedForTitle += EnsureCompleteArtworkSetFromExistingAssets(
                    workingGridDirectory,
                    gridId,
                    target.Title);

                if (target.ForceReload)
                {
                    updatedForTitle = HasCompleteArtworkSet(
                            workingGridDirectory,
                            target.AppId)
                        ? PromoteReloadedArtworkSet(
                            workingGridDirectory,
                            gridDirectory,
                            gridId)
                        : 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One missing/broken source must not stop the progressive queue.
            }
            finally
            {
                DeleteArtworkReloadStagingDirectory(reloadStagingDirectory);
            }

            if (updatedForTitle > 0)
            {
                updatedFileCount += updatedForTitle;
                updatedTitleIds.Add(target.TitleId);
            }

            completedTargetCount++;
            progress?.Invoke(completedTargetCount, uniqueTargets.Length);
        }

        return new StoreSyncArtworkSummary(
            updatedTitleIds.Count,
            updatedFileCount,
            updatedTitleIds);
    }

    private static async Task<int> ImportLocalArtworkSetAsync(
        string gridDirectory,
        string gridId,
        StoreSyncArtworkTarget target,
        OmniLibraryLocalArtworkResolver resolver,
        CancellationToken cancellationToken)
    {
        var localArtwork = resolver.Resolve(target);
        var updated = 0;
        foreach (var slot in ArtworkSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, fileStem) ||
                !TryGetLocalSlotKind(slot, out var slotKind) ||
                !localArtwork.TryGetValue(slotKind, out var sourcePath) ||
                !OmniLibraryLocalArtworkResolver.TryResolveLocalReference(
                    sourcePath,
                    out var resolvedSourcePath))
            {
                continue;
            }

            var extension = Path.GetExtension(resolvedSourcePath).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".ico"))
            {
                continue;
            }

            if (PromoteStoreFallbackArtwork(
                    gridDirectory,
                    fileStem,
                    new DownloadedArtworkFile(resolvedSourcePath, extension),
                    slot))
            {
                updated++;
                await TryApplyStoreBadgeAsync(
                    gridDirectory,
                    gridId,
                    slot,
                    target.StoreId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var iconStem = $"{gridId}-icon";
        if (!HasArtworkVariant(gridDirectory, iconStem) &&
            TryExtractExecutableIcon(target.LocalExecutablePath, out var iconPath))
        {
            try
            {
                var iconSlot = ArtworkSlots.First(slot => slot.SlotName == "icon");
                if (PromoteStoreFallbackArtwork(
                        gridDirectory,
                        iconStem,
                        new DownloadedArtworkFile(iconPath, ".png"),
                        iconSlot))
                {
                    updated++;
                }
            }
            finally
            {
                DeleteFileIfExists(iconPath);
            }
        }

        return updated;
    }

    private static bool TryGetLocalSlotKind(
        ArtworkSlot slot,
        out OmniLibraryArtworkSlotKind slotKind)
    {
        slotKind = slot.SlotName switch
        {
            "library capsule" => OmniLibraryArtworkSlotKind.LibraryCapsule,
            "portrait" => OmniLibraryArtworkSlotKind.Portrait,
            "hero" => OmniLibraryArtworkSlotKind.Hero,
            "logo" => OmniLibraryArtworkSlotKind.Logo,
            "icon" => OmniLibraryArtworkSlotKind.Icon,
            _ => (OmniLibraryArtworkSlotKind)(-1),
        };
        return Enum.IsDefined(slotKind);
    }

    private static bool HasAnyPrimaryArtwork(string gridDirectory, string gridId) =>
        HasArtworkVariant(gridDirectory, gridId) ||
        HasArtworkVariant(gridDirectory, $"{gridId}p") ||
        HasArtworkVariant(gridDirectory, $"{gridId}_hero");

    private static bool TryExtractExecutableIcon(
        string? executablePath,
        out string iconPath)
    {
        iconPath = string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null)
            {
                return false;
            }

            iconPath = Path.Combine(
                Path.GetTempPath(),
                $"steamloader-local-icon-{Guid.NewGuid():N}.png");
            using var bitmap = icon.ToBitmap();
            bitmap.Save(iconPath, ImageFormat.Png);
            return IsUsableArtworkFile(iconPath);
        }
        catch
        {
            DeleteFileIfExists(iconPath);
            iconPath = string.Empty;
            return false;
        }
    }

    private static async Task<int> DownloadRetroAchievementsArtworkSetAsync(
        HttpClient httpClient,
        string gridDirectory,
        string gridId,
        StoreSyncArtworkTarget target,
        CancellationToken cancellationToken)
    {
        var gameId = target.RetroAchievementsGameId.GetValueOrDefault();
        if (gameId == 0)
        {
            var contentHash = await ManagedRetroAchievementsHasher.HashAsync(
                target.RomSystemId,
                target.RomPath,
                cancellationToken).ConfigureAwait(false);
            using var identifyRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "https://retroachievements.org/dorequest.php")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["r"] = "gameid",
                    ["m"] = contentHash,
                }),
            };
            identifyRequest.Headers.TryAddWithoutValidation(
                "User-Agent",
                "ToolsForSteam-OmniLibrary/0.4.1");
            identifyRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var identifyResponse = await httpClient.SendAsync(
                identifyRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            identifyResponse.EnsureSuccessStatusCode();
            await using var identifyStream = await identifyResponse.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var identifyDocument = await JsonDocument.ParseAsync(
                identifyStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!identifyDocument.RootElement.TryGetProperty("GameID", out var gameIdNode) ||
                !TryReadUInt32(gameIdNode, out gameId) ||
                gameId == 0)
            {
                return 0;
            }
        }

        var gameUrl =
            $"https://retroachievements.org/API/API_GetGame.php?i={gameId}&y={Uri.EscapeDataString(target.RetroAchievementsApiKey)}";
        using var gameResponse = await httpClient.GetAsync(
            gameUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        gameResponse.EnsureSuccessStatusCode();
        await using var gameStream = await gameResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var gameDocument = await JsonDocument.ParseAsync(
            gameStream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = gameDocument.RootElement;
        var imageByStem = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [gridId] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageTitle", "imageTitle")),
            [$"{gridId}p"] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageBoxArt", "imageBoxArt")),
            [$"{gridId}_hero"] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageIngame", "imageIngame")),
            [$"{gridId}-icon"] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageIcon", "imageIcon", "GameIcon", "gameIcon")),
        };

        var updated = 0;
        foreach (var slot in ArtworkSlots)
        {
            var fileStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, fileStem) ||
                !imageByStem.TryGetValue(fileStem, out var imageUrl) ||
                string.IsNullOrWhiteSpace(imageUrl))
            {
                continue;
            }

            var downloaded = await DownloadAssetToTemporaryFileAsync(
                httpClient,
                imageUrl,
                cancellationToken).ConfigureAwait(false);
            if (downloaded is null)
            {
                continue;
            }
            try
            {
                if (new FileInfo(downloaded.TempPath).Length >= 1024 &&
                    PromoteStoreFallbackArtwork(
                        gridDirectory,
                        fileStem,
                        downloaded,
                        slot))
                {
                    updated++;
                    await TryApplyStoreBadgeAsync(
                        gridDirectory,
                        gridId,
                        slot,
                        target.StoreId,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                DeleteFileIfExists(downloaded.TempPath);
            }
        }

        return updated;
    }

    private static string ReadJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var node) &&
                node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        return string.Empty;
    }

    private static bool TryReadUInt32(JsonElement element, out uint value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetUInt32(out value);
        }
        return element.ValueKind == JsonValueKind.String &&
            uint.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }

    private static string ResolveRetroAchievementsMediaUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            absolute.Scheme == Uri.UriSchemeHttps &&
            IsRetroAchievementsHost(absolute.Host))
        {
            return absolute.AbsoluteUri;
        }
        return value.StartsWith("/", StringComparison.Ordinal)
            ? $"https://media.retroachievements.org{value}"
            : string.Empty;
    }

    private static bool IsRetroAchievementsHost(string host) =>
        host.Equals("retroachievements.org", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".retroachievements.org", StringComparison.OrdinalIgnoreCase);

    private static async Task<int?> ResolvePublicSteamAppIdAsync(
        HttpClient httpClient,
        string title,
        IReadOnlyList<string> searchHints,
        IDictionary<string, int?> cache,
        CancellationToken cancellationToken)
    {
        var requestedTitles = BuildSearchTerms(title, searchHints)
            .Select(NormalizeTitle)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var searchTerm in BuildSearchTerms(title, searchHints).Take(3))
        {
            if (cache.TryGetValue(searchTerm, out var cached))
            {
                if (cached.HasValue)
                {
                    return cached;
                }
                continue;
            }

            try
            {
                var uri =
                    $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(searchTerm)}&l=english&cc=US";
                using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    cache[searchTerm] = null;
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    cache[searchTerm] = null;
                    continue;
                }

                var selected = items.EnumerateArray()
                    .Select(item => new
                    {
                        Id = item.TryGetProperty("id", out var idNode) && idNode.TryGetInt32(out var id) ? id : 0,
                        Name = item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty,
                    })
                    .Where(item => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name))
                    .Select(item => new
                    {
                        item.Id,
                        Score = requestedTitles
                            .Select(requested => ScoreMatch(requested, NormalizeTitle(item.Name)))
                            .DefaultIfEmpty(3)
                            .Min(),
                    })
                    .OrderBy(item => item.Score)
                    .FirstOrDefault();
                cache[searchTerm] = selected?.Score <= 1 ? selected.Id : null;
                if (cache[searchTerm].HasValue)
                {
                    return cache[searchTerm];
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                cache[searchTerm] = null;
            }
        }

        return null;
    }

    private static async Task<int> DownloadPublicSteamArtworkSetAsync(
        HttpClient httpClient,
        string gridDirectory,
        string gridId,
        int steamAppId,
        string? storeId,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [gridId] =
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/header.jpg",
            ],
            [$"{gridId}p"] =
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/library_600x900_2x.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/library_600x900.jpg",
            ],
            [$"{gridId}_hero"] =
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/library_hero.jpg",
            ],
            [$"{gridId}_logo"] =
            [
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/logo_2x.png",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/logo.png",
            ],
        };

        var updated = 0;
        foreach (var slot in ArtworkSlots.Where(slot =>
                     candidates.ContainsKey(slot.FileStemBuilder(gridId))))
        {
            var fileStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, fileStem))
            {
                continue;
            }

            foreach (var url in candidates[fileStem])
            {
                try
                {
                    var downloaded = await DownloadAssetToTemporaryFileAsync(
                        httpClient,
                        url,
                        cancellationToken).ConfigureAwait(false);
                    if (downloaded is null)
                    {
                        continue;
                    }

                    try
                    {
                        var length = new FileInfo(downloaded.TempPath).Length;
                        if (length < 1024 ||
                            !PromoteDownloadedArtwork(gridDirectory, fileStem, downloaded))
                        {
                            continue;
                        }

                        updated++;
                        await TryApplyStoreBadgeAsync(
                            gridDirectory,
                            gridId,
                            slot,
                            storeId,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    finally
                    {
                        DeleteFileIfExists(downloaded.TempPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
        }

        return updated;
    }

    private static int EnsureCompleteArtworkSetFromExistingAssets(
        string gridDirectory,
        string gridId,
        string title)
    {
        var updated = EnsurePrimaryArtworkSetFromExistingAssets(
            gridDirectory,
            gridId);

        var iconStem = $"{gridId}-icon";
        if (!HasArtworkVariant(gridDirectory, iconStem))
        {
            var iconSource = new[] { $"{gridId}p", gridId, $"{gridId}_hero" }
                .Select(stem => FindArtworkVariant(gridDirectory, stem))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            var iconSlot = ArtworkSlots.First(slot => slot.SlotName == "icon");
            if (!string.IsNullOrWhiteSpace(iconSource) &&
                PromoteStoreFallbackArtwork(
                    gridDirectory,
                    iconStem,
                    new DownloadedArtworkFile(
                        iconSource,
                        Path.GetExtension(iconSource).ToLowerInvariant()),
                    iconSlot))
            {
                updated++;
            }
        }

        var logoStem = $"{gridId}_logo";
        if (!HasArtworkVariant(gridDirectory, logoStem) &&
            GenerateTitleLogo(gridDirectory, logoStem, title))
        {
            updated++;
        }

        return updated;
    }

    private static bool GenerateTitleLogo(
        string gridDirectory,
        string fileStem,
        string title)
    {
        var normalizedTitle = Regex.Replace(
            title?.Trim() ?? string.Empty,
            "\\s+",
            " ");
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"steamloader-generated-logo-{Guid.NewGuid():N}.png");
        try
        {
            using var image = new Bitmap(1024, 320, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var fontSize = 112f;
            Font? font = null;
            try
            {
                while (fontSize >= 34f)
                {
                    font?.Dispose();
                    font = new Font(
                        "Segoe UI",
                        fontSize,
                        FontStyle.Bold,
                        GraphicsUnit.Pixel);
                    var measured = graphics.MeasureString(normalizedTitle, font);
                    if (measured.Width <= 940 && measured.Height <= 250)
                    {
                        break;
                    }
                    fontSize -= 6f;
                }

                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                };
                var bounds = new RectangleF(22, 20, 980, 280);
                using var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                using var foreground = new SolidBrush(Color.White);
                graphics.DrawString(
                    normalizedTitle,
                    font!,
                    shadow,
                    new RectangleF(bounds.X + 5, bounds.Y + 6, bounds.Width, bounds.Height),
                    format);
                graphics.DrawString(normalizedTitle, font!, foreground, bounds, format);
                image.Save(temporaryPath, ImageFormat.Png);
            }
            finally
            {
                font?.Dispose();
            }

            return PromoteDownloadedArtwork(
                gridDirectory,
                fileStem,
                new DownloadedArtworkFile(temporaryPath, ".png"));
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteFileIfExists(temporaryPath);
        }
    }

    private static int EnsurePrimaryArtworkSetFromExistingAssets(
        string gridDirectory,
        string gridId)
    {
        var slots = ArtworkSlots.Where(slot =>
                slot.SlotName is "library capsule" or "portrait" or "hero")
            .ToArray();
        var updated = 0;
        foreach (var slot in slots)
        {
            var targetStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, targetStem))
            {
                continue;
            }

            var source = slots
                .Where(candidate => !ReferenceEquals(candidate, slot))
                .Select(candidate => FindArtworkVariant(
                    gridDirectory,
                    candidate.FileStemBuilder(gridId)))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            {
                continue;
            }

            if (PromoteStoreFallbackArtwork(
                    gridDirectory,
                    targetStem,
                    new DownloadedArtworkFile(source, extension),
                    slot))
            {
                updated++;
            }
        }

        return updated;
    }

    private static string? FindArtworkVariant(
        string gridDirectory,
        string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var path = Path.Combine(gridDirectory, fileStem + extension);
            if (IsUsableArtworkFile(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task<int> DownloadRemoteStoreArtworkSetAsync(
        HttpClient httpClient,
        string gridDirectory,
        string gridId,
        StoreSyncArtworkTarget target,
        CancellationToken cancellationToken)
    {
        var portraitUrl = target.FallbackPortraitUrl?.Trim() ?? string.Empty;
        var heroUrl = target.FallbackHeroUrl?.Trim() ?? string.Empty;
        var candidates = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [gridId] = [heroUrl, portraitUrl],
            [$"{gridId}p"] = [portraitUrl, heroUrl],
            [$"{gridId}_hero"] = [heroUrl, portraitUrl],
        };

        var updated = 0;
        foreach (var slot in ArtworkSlots.Where(slot =>
                     candidates.ContainsKey(slot.FileStemBuilder(gridId))))
        {
            var fileStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, fileStem))
            {
                continue;
            }

            foreach (var url in candidates[fileStem]
                         .Where(url => !string.IsNullOrWhiteSpace(url))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // file:// and rooted references were consumed by the local stage.
                // This stage performs network requests only.
                if (OmniLibraryLocalArtworkResolver.TryResolveLocalReference(url, out _))
                {
                    continue;
                }

                try
                {
                    var downloaded = await DownloadAssetToTemporaryFileAsync(
                        httpClient,
                        url,
                        cancellationToken).ConfigureAwait(false);
                    if (downloaded is null)
                    {
                        continue;
                    }

                    try
                    {
                        var length = new FileInfo(downloaded.TempPath).Length;
                        if (length < 1024 ||
                            !PromoteStoreFallbackArtwork(
                                gridDirectory,
                                fileStem,
                                downloaded,
                                slot))
                        {
                            continue;
                        }

                        updated++;
                        await TryApplyStoreBadgeAsync(
                            gridDirectory,
                            gridId,
                            slot,
                            target.StoreId,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    finally
                    {
                        DeleteFileIfExists(downloaded.TempPath);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
        }

        return updated;
    }

    public async Task<StoreSyncArtworkMatch?> ResolveMatchAsync(
        string title,
        IReadOnlyList<string> searchHints,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return await FindGameMatchAsync(
            httpClient,
            title,
            searchHints,
            new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
    }

    public async Task<StoreSyncArtworkPreview?> ResolvePreviewAsync(
        string title,
        IReadOnlyList<string> searchHints,
        string apiKey,
        int? cachedGameId,
        string? cachedMatchName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var match = cachedGameId.HasValue && cachedGameId.Value > 0
            ? new StoreSyncArtworkMatch(cachedGameId.Value, cachedMatchName ?? title)
            : await FindGameMatchAsync(
                httpClient,
                title,
                searchHints,
                new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        if (match is null)
        {
            return null;
        }

        var assetUrl = await FindPreviewAssetUrlAsync(httpClient, match.GameId, cancellationToken);
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return null;
        }

        var previewAsset = await DownloadAssetContentAsync(httpClient, assetUrl, cancellationToken);
        if (previewAsset is null)
        {
            return null;
        }

        return new StoreSyncArtworkPreview(
            previewAsset.DataUri,
            match.GameId,
            string.IsNullOrWhiteSpace(match.MatchName) ? title : match.MatchName);
    }

    public string GetEffectiveApiKey(string? configuredApiKey)
    {
        return string.IsNullOrWhiteSpace(configuredApiKey)
            ? BuiltInApiKey
            : configuredApiKey.Trim();
    }

    public string GetPreview(string? configuredApiKey)
    {
        if (string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return "Built in";
        }

        var trimmedApiKey = configuredApiKey.Trim();
        return trimmedApiKey.Length <= 6
            ? "Configured"
            : $"Configured ({trimmedApiKey[^4..]})";
    }

    private async Task<StoreSyncArtworkMatch?> FindGameMatchAsync(
        HttpClient httpClient,
        string title,
        IReadOnlyList<string> searchHints,
        IDictionary<string, int?> searchCache,
        CancellationToken cancellationToken)
    {
        var searchTerms = BuildSearchTerms(title, searchHints).ToList();
        var comparisonTitles = searchTerms
            .Select(NormalizeTitle)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var term in searchTerms)
        {
            if (searchCache.TryGetValue(term, out var cachedValue))
            {
                if (cachedValue.HasValue)
                {
                    return new StoreSyncArtworkMatch(cachedValue.Value, term);
                }

                continue;
            }

            using var response = await httpClient.GetAsync(
                $"search/autocomplete/{Uri.EscapeDataString(term)}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                searchCache[term] = null;
                continue;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SteamGridDbListResponse<SteamGridDbGameMatch>>(
                responseStream,
                JsonOptions,
                cancellationToken);

            var selectedMatch = SelectBestMatch(comparisonTitles, payload?.Data);
            searchCache[term] = selectedMatch?.Id;

            if (selectedMatch is not null)
            {
                return new StoreSyncArtworkMatch(selectedMatch.Id, selectedMatch.Name);
            }
        }

        return null;
    }

    private async Task<int> DownloadArtworkSetAsync(
        HttpClient httpClient,
        string gridDirectory,
        string gridId,
        int gameId,
        string? storeId,
        CancellationToken cancellationToken)
    {
        var updatedFileCount = 0;

        foreach (var slot in ArtworkSlots)
        {
            var fileStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, fileStem))
            {
                continue;
            }

            try
            {
                if (await DownloadArtworkSlotAsync(httpClient, gridDirectory, gridId, slot, gameId, cancellationToken))
                {
                    updatedFileCount++;
                    await TryApplyStoreBadgeAsync(gridDirectory, gridId, slot, storeId, cancellationToken);
                }
            }
            catch
            {
            }
        }

        if (!HasArtworkVariant(gridDirectory, gridId) || !HasArtworkVariant(gridDirectory, $"{gridId}p"))
        {
            updatedFileCount += await DownloadMissingPrimaryArtworkAsync(
                httpClient,
                gridDirectory,
                gridId,
                gameId,
                storeId,
                cancellationToken);
        }

        return updatedFileCount;
    }

    private async Task<int> DownloadMissingPrimaryArtworkAsync(
        HttpClient httpClient,
        string gridDirectory,
        string gridId,
        int gameId,
        string? storeId,
        CancellationToken cancellationToken)
    {
        var updatedFileCount = 0;
        foreach (var slot in ArtworkSlots.Take(2))
        {
            var fileStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, fileStem))
            {
                continue;
            }

            try
            {
                if (await DownloadArtworkSlotAsync(httpClient, gridDirectory, gridId, slot, gameId, cancellationToken))
                {
                    updatedFileCount++;
                    await TryApplyStoreBadgeAsync(gridDirectory, gridId, slot, storeId, cancellationToken);
                }
            }
            catch
            {
            }
        }

        return updatedFileCount;
    }

    private static async Task TryApplyStoreBadgeAsync(
        string gridDirectory,
        string gridId,
        ArtworkSlot slot,
        string? storeId,
        CancellationToken cancellationToken)
    {
        if (!slot.SupportsBadge || string.IsNullOrWhiteSpace(storeId))
        {
            return;
        }

        var fileStem = slot.FileStemBuilder(gridId);
        var imagePath = FindArtworkFile(gridDirectory, fileStem);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            await StoreBadgeCompositor.ApplyBadgeAsync(imagePath, storeId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? FindArtworkFile(string gridDirectory, string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var path = Path.Combine(gridDirectory, fileStem + extension);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task<bool> DownloadArtworkSlotAsync(
        HttpClient httpClient,
        string gridDirectory,
        string gridId,
        ArtworkSlot slot,
        int gameId,
        CancellationToken cancellationToken)
    {
        var assetUrl = await FindTopAssetUrlAsync(httpClient, slot, gameId, cancellationToken);
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return false;
        }

        var downloadedAsset = await DownloadAssetToTemporaryFileAsync(
            httpClient,
            assetUrl,
            cancellationToken);
        if (downloadedAsset is null)
        {
            return false;
        }

        try
        {
            var fileStem = slot.FileStemBuilder(gridId);
            return PromoteDownloadedArtwork(gridDirectory, fileStem, downloadedAsset);
        }
        finally
        {
            DeleteFileIfExists(downloadedAsset.TempPath);
        }
    }

    private static async Task<string?> FindTopAssetUrlAsync(
        HttpClient httpClient,
        ArtworkSlot slot,
        int gameId,
        CancellationToken cancellationToken)
    {
        foreach (var requestPath in slot.RequestPaths)
        {
            using var response = await httpClient.GetAsync(
                string.Format(requestPath, gameId),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SteamGridDbListResponse<SteamGridDbAsset>>(
                responseStream,
                JsonOptions,
                cancellationToken);

            var assetUrl = SelectTopAssetUrl(slot, payload?.Data);
            if (!string.IsNullOrWhiteSpace(assetUrl))
            {
                return assetUrl;
            }
        }

        return null;
    }

    private static async Task<string?> FindPreviewAssetUrlAsync(
        HttpClient httpClient,
        int gameId,
        CancellationToken cancellationToken)
    {
        foreach (var requestPath in new[]
                 {
                     $"grids/game/{gameId}?types=static&dimensions=460x215,920x430&mimes=image/png,image/jpeg,image/webp",
                     $"grids/game/{gameId}?types=static&mimes=image/png,image/jpeg,image/webp",
                 })
        {
            var response = await httpClient.GetAsync(requestPath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SteamGridDbListResponse<SteamGridDbAsset>>(
                responseStream,
                JsonOptions,
                cancellationToken);

            var assetUrl = payload?.Data?
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Url))
                .OrderBy(asset => ScorePreviewAsset(asset))
                .Select(asset => asset.Url)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(assetUrl))
            {
                return assetUrl;
            }
        }

        return null;
    }

    private static async Task<DownloadedArtworkFile?> DownloadAssetToTemporaryFileAsync(
        HttpClient httpClient,
        string assetUrl,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(assetUrl, UriKind.Absolute, out var localUri) &&
            localUri.IsFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = localUri.LocalPath;
            if (!File.Exists(localPath))
            {
                return null;
            }

            var localExtension = ResolveFileExtension(localPath, null);
            var localLength = new FileInfo(localPath).Length;
            if (localExtension is null ||
                localLength < MinimumArtworkBytes ||
                localLength > MaximumArtworkBytes)
            {
                return null;
            }

            var localTempPath = Path.Combine(
                Path.GetTempPath(),
                $"steamloader-grid-{Guid.NewGuid():N}{localExtension}");
            try
            {
                await using var localInput = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                await using (var localOutput = File.Create(localTempPath))
                {
                    await CopyArtworkWithLimitAsync(
                        localInput,
                        localOutput,
                        cancellationToken).ConfigureAwait(false);
                    await localOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (!IsUsableArtworkFile(localTempPath))
                {
                    DeleteFileIfExists(localTempPath);
                    return null;
                }
                return new DownloadedArtworkFile(localTempPath, localExtension);
            }
            catch
            {
                DeleteFileIfExists(localTempPath);
                throw;
            }
        }

        using var response = await httpClient.GetAsync(
            assetUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArtworkBytes)
        {
            return null;
        }

        var extension = ResolveFileExtension(assetUrl, response.Content.Headers.ContentType?.MediaType);
        if (extension is null)
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"steamloader-grid-{Guid.NewGuid():N}{extension}");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = File.Create(tempPath))
            {
                await CopyArtworkWithLimitAsync(input, output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!IsUsableArtworkFile(tempPath))
            {
                DeleteFileIfExists(tempPath);
                return null;
            }
            return new DownloadedArtworkFile(tempPath, extension);
        }
        catch
        {
            DeleteFileIfExists(tempPath);
            throw;
        }
    }

    private static async Task<DownloadedArtworkContent?> DownloadAssetContentAsync(
        HttpClient httpClient,
        string assetUrl,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            assetUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArtworkBytes)
        {
            return null;
        }

        var mimeType = ResolveMimeType(assetUrl, response.Content.Headers.ContentType?.MediaType);
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        await CopyArtworkWithLimitAsync(input, output, cancellationToken).ConfigureAwait(false);
        return new DownloadedArtworkContent(
            $"data:{mimeType};base64,{Convert.ToBase64String(output.ToArray())}");
    }

    private static bool PromoteDownloadedArtwork(
        string gridDirectory,
        string fileStem,
        DownloadedArtworkFile asset)
    {
        if (!IsUsableArtworkFile(asset.TempPath))
        {
            return false;
        }

        var targetPath = Path.Combine(gridDirectory, fileStem + asset.Extension);
        Directory.CreateDirectory(gridDirectory);

        if (File.Exists(targetPath) && FilesAreEqual(asset.TempPath, targetPath))
        {
            RemoveSlotVariantsExcept(gridDirectory, fileStem, targetPath);
            return false;
        }

        var stagingPath = Path.Combine(
            gridDirectory,
            $".tfs-artwork-{fileStem}-{Guid.NewGuid():N}{asset.Extension}.tmp");
        try
        {
            File.Copy(asset.TempPath, stagingPath, overwrite: false);
            File.Move(stagingPath, targetPath, overwrite: true);
            ArtworkValidationCache.TryRemove(targetPath, out _);
            RemoveSlotVariantsExcept(gridDirectory, fileStem, targetPath);
            return true;
        }
        finally
        {
            DeleteFileIfExists(stagingPath);
        }
    }

    private static bool PromoteStoreFallbackArtwork(
        string gridDirectory,
        string fileStem,
        DownloadedArtworkFile asset,
        ArtworkSlot slot)
    {
        if (!slot.PreferredWidth.HasValue || !slot.PreferredHeight.HasValue)
        {
            return PromoteDownloadedArtwork(gridDirectory, fileStem, asset);
        }

        var outputExtension = asset.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                              asset.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : ".png";
        var resizedPath = Path.Combine(
            Path.GetTempPath(),
            $"steamloader-grid-fallback-{Guid.NewGuid():N}{outputExtension}");
        try
        {
            using var inputStream = new FileStream(
                asset.TempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var source = Image.FromStream(inputStream);
            var targetWidth = slot.PreferredWidth.Value;
            var targetHeight = slot.PreferredHeight.Value;
            using var output = new Bitmap(
                targetWidth,
                targetHeight,
                PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.Clear(Color.Black);
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;

                var scale = Math.Max(
                    (double)targetWidth / Math.Max(1, source.Width),
                    (double)targetHeight / Math.Max(1, source.Height));
                var scaledWidth = Math.Max(1, (int)Math.Ceiling(source.Width * scale));
                var scaledHeight = Math.Max(1, (int)Math.Ceiling(source.Height * scale));
                var x = (targetWidth - scaledWidth) / 2;
                var y = (targetHeight - scaledHeight) / 2;
                graphics.DrawImage(source, x, y, scaledWidth, scaledHeight);
            }

            output.Save(
                resizedPath,
                outputExtension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? ImageFormat.Jpeg
                    : ImageFormat.Png);
            return PromoteDownloadedArtwork(
                gridDirectory,
                fileStem,
                new DownloadedArtworkFile(resizedPath, outputExtension));
        }
        catch
        {
            // A catalog image is still preferable to an empty Steam slot if the
            // platform image decoder cannot resize a particular source format.
            return PromoteDownloadedArtwork(gridDirectory, fileStem, asset);
        }
        finally
        {
            DeleteFileIfExists(resizedPath);
        }
    }

    private static SteamGridDbGameMatch? SelectBestMatch(
        IReadOnlyList<string> normalizedRequestedTitles,
        IReadOnlyList<SteamGridDbGameMatch>? matches)
    {
        if (matches is null || matches.Count == 0 || normalizedRequestedTitles.Count == 0)
        {
            return null;
        }

        var selected = matches
            .Select(match => new
            {
                Match = match,
                Score = normalizedRequestedTitles
                    .Select(requestedTitle => ScoreMatch(requestedTitle, NormalizeTitle(match.Name)))
                    .DefaultIfEmpty(3)
                    .Min()
            })
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Match.Verified ? 0 : 1)
            .ThenBy(item => Math.Abs((item.Match.Name ?? string.Empty).Length - normalizedRequestedTitles[0].Length))
            .FirstOrDefault();

        return selected?.Score <= 2
            ? selected.Match
            : null;
    }

    private static IEnumerable<string> BuildSearchTerms(string title, IReadOnlyList<string> searchHints)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedTerms = new List<string>();

        void AddTerm(string? value)
        {
            var trimmedValue = value?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedValue))
            {
                var cleanedValue = Regex.Replace(trimmedValue, @"\s{2,}", " ");
                if (terms.Add(cleanedValue))
                {
                    seedTerms.Add(cleanedValue);
                }
            }
        }

        AddTerm(title);
        AddKnownAliases(title, AddTerm);
        foreach (var searchHint in searchHints)
        {
            foreach (var extractedHint in ExtractSearchHints(searchHint))
            {
                AddTerm(extractedHint);
                AddKnownAliases(extractedHint, AddTerm);
            }
        }

        foreach (var seedTerm in seedTerms.ToArray())
        {
            AddTitleVariants(seedTerm, AddTerm);
        }

        return terms.Take(18);
    }

    private static void AddKnownAliases(string value, Action<string?> addTerm)
    {
        if (KnownTitleAliases.TryGetValue(NormalizeTitle(value), out var aliases))
        {
            foreach (var alias in aliases)
            {
                addTerm(alias);
            }
        }
    }

    private static void AddTitleVariants(string title, Action<string?> addTerm)
    {
        var withoutBrackets = Regex.Replace(title, @"\s*[\(\[].*?[\)\]]\s*", " ").Trim();
        addTerm(withoutBrackets);

        var readableTitle = PrettifySearchHint(withoutBrackets);
        addTerm(readableTitle);

        if (readableTitle.Contains('&', StringComparison.Ordinal))
        {
            addTerm(readableTitle.Replace("&", "and", StringComparison.Ordinal));
        }

        if (Regex.IsMatch(readableTitle, @"\band\b", RegexOptions.IgnoreCase))
        {
            addTerm(Regex.Replace(readableTitle, @"\band\b", "&", RegexOptions.IgnoreCase));
        }

        var withoutEditionSuffix = Regex.Replace(
            readableTitle,
            @"\b(game of the year|goty|ultimate|definitive|complete|deluxe|enhanced|remastered|anniversary|collector'?s|director'?s cut|edition)\b",
            string.Empty,
            RegexOptions.IgnoreCase);
        addTerm(Regex.Replace(withoutEditionSuffix, @"\s{2,}", " ").Trim(' ', '-', ':'));

        if (withoutEditionSuffix.Contains(" - ", StringComparison.Ordinal))
        {
            addTerm(withoutEditionSuffix.Split(" - ", 2, StringSplitOptions.TrimEntries)[0]);
        }

        AddColonVariants(readableTitle, addTerm);
    }

    private static void AddColonVariants(string title, Action<string?> addTerm)
    {
        if (title.Contains(':', StringComparison.Ordinal))
        {
            return;
        }

        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 3)
        {
            return;
        }

        addTerm($"{words[0]}: {string.Join(' ', words.Skip(1))}");
        addTerm($"{words[0]} {words[1]}: {string.Join(' ', words.Skip(2))}");
    }

    private static IEnumerable<string> ExtractSearchHints(string value)
    {
        var cleanedValue = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(cleanedValue))
        {
            yield break;
        }

        if (Path.HasExtension(cleanedValue))
        {
            var fileName = Path.GetFileNameWithoutExtension(cleanedValue);
            if (IsUsefulSearchHint(fileName))
            {
                yield return PrettifySearchHint(fileName);
            }

            cleanedValue = Path.GetDirectoryName(cleanedValue) ?? string.Empty;
        }

        for (var index = 0; index < 5 && !string.IsNullOrWhiteSpace(cleanedValue); index++)
        {
            var directoryName = Path.GetFileName(cleanedValue.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (IsUsefulSearchHint(directoryName))
            {
                yield return PrettifySearchHint(directoryName);
            }

            cleanedValue = Path.GetDirectoryName(cleanedValue) ?? string.Empty;
        }
    }

    private static bool IsUsefulSearchHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hint = PrettifySearchHint(value);
        return hint.Length >= 3 && !IgnoredSearchHints.Contains(hint);
    }

    private static string PrettifySearchHint(string value)
    {
        var cleaned = Regex.Replace(value, @"[_\.-]+", " ");
        cleaned = Regex.Replace(cleaned, "(?<=[a-z])(?=[A-Z0-9])", " ");
        cleaned = Regex.Replace(cleaned, "(?<=[0-9])(?=[A-Za-z])", " ");
        cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
        return cleaned;
    }

    private static string NormalizeTitle(string? value)
    {
        return Regex.Replace(value ?? string.Empty, @"[^a-z0-9]+", string.Empty, RegexOptions.IgnoreCase)
            .ToLowerInvariant();
    }

    private static int ScoreMatch(string requestedTitle, string candidateTitle)
    {
        if (candidateTitle == requestedTitle)
        {
            return 0;
        }

        if (candidateTitle.StartsWith(requestedTitle, StringComparison.Ordinal) ||
            requestedTitle.StartsWith(candidateTitle, StringComparison.Ordinal))
        {
            return 1;
        }

        if (candidateTitle.Contains(requestedTitle, StringComparison.Ordinal) ||
            requestedTitle.Contains(candidateTitle, StringComparison.Ordinal))
        {
            return 2;
        }

        return 3;
    }

    private static string? SelectTopAssetUrl(ArtworkSlot slot, IReadOnlyList<SteamGridDbAsset>? assets)
    {
        return assets?
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Url))
            .OrderBy(asset => ScoreAsset(slot, asset))
            .Select(asset => asset.Url)
            .FirstOrDefault();
    }

    private static double ScoreAsset(ArtworkSlot slot, SteamGridDbAsset asset)
    {
        if (!slot.PreferredWidth.HasValue ||
            !slot.PreferredHeight.HasValue ||
            !asset.Width.HasValue ||
            !asset.Height.HasValue ||
            asset.Height.Value <= 0)
        {
            return 0;
        }

        var preferredRatio = (double)slot.PreferredWidth.Value / slot.PreferredHeight.Value;
        var actualRatio = (double)asset.Width.Value / asset.Height.Value;
        var ratioScore = Math.Abs(preferredRatio - actualRatio) * 1000;
        var preferredArea = slot.PreferredWidth.Value * slot.PreferredHeight.Value;
        var actualArea = Math.Max(1, asset.Width.Value * asset.Height.Value);
        var areaScore = Math.Abs(Math.Log((double)actualArea / preferredArea));

        return ratioScore + areaScore;
    }

    private static string? ResolveFileExtension(string assetUrl, string? contentType)
    {
        var contentTypeExtension = contentType?.Trim().ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/vnd.microsoft.icon" => ".ico",
            "image/x-icon" => ".ico",
            "image/webp" => ".webp",
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(contentTypeExtension))
        {
            return contentTypeExtension;
        }

        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri))
        {
            return null;
        }

        var extension = Path.GetExtension(assetUri.AbsolutePath).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".ico" or ".webp"
            ? extension
            : null;
    }

    private static string? ResolveMimeType(string assetUrl, string? contentType)
    {
        var normalizedContentType = contentType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedContentType) &&
            normalizedContentType.StartsWith("image/", StringComparison.Ordinal))
        {
            return normalizedContentType;
        }

        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri))
        {
            return null;
        }

        return Path.GetExtension(assetUri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            _ => null,
        };
    }

    private static double ScorePreviewAsset(SteamGridDbAsset asset)
    {
        if (!asset.Width.HasValue || !asset.Height.HasValue || asset.Height.Value <= 0)
        {
            return 0;
        }

        const double preferredRatio = 460d / 215d;
        var actualRatio = (double)asset.Width.Value / asset.Height.Value;
        var ratioScore = Math.Abs(preferredRatio - actualRatio) * 1000d;
        var preferredArea = 460d * 215d;
        var actualArea = Math.Max(1d, asset.Width.Value * asset.Height.Value);
        var areaScore = Math.Abs(Math.Log(actualArea / preferredArea));
        return ratioScore + areaScore;
    }

    private static void RemoveSlotVariantsExcept(string gridDirectory, string fileStem, string keepPath)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".ico", ".webp" })
        {
            var path = Path.Combine(gridDirectory, fileStem + extension);
            if (string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    ArtworkValidationCache.TryRemove(path, out _);
                }
                catch
                {
                    // Steam may briefly hold an older extension open. The new
                    // atomic target is already valid; a later pass can remove it.
                }
            }
        }
    }

    private static int PromoteReloadedArtworkSet(
        string stagingDirectory,
        string gridDirectory,
        string gridId)
    {
        var updated = 0;
        foreach (var fileStem in GetArtworkFileStems(gridId))
        {
            var sourcePath = FindArtworkFile(stagingDirectory, fileStem);
            if (sourcePath is null)
            {
                return 0;
            }

            var extension = Path.GetExtension(sourcePath);
            if (PromoteDownloadedArtwork(
                    gridDirectory,
                    fileStem,
                    new DownloadedArtworkFile(sourcePath, extension)))
            {
                updated++;
            }
        }

        return updated;
    }

    private static IReadOnlyList<string> GetArtworkFileStems(string gridId) =>
    [
        gridId,
        $"{gridId}p",
        $"{gridId}_hero",
        $"{gridId}_logo",
        $"{gridId}-icon",
    ];

    private static void DeleteArtworkReloadStagingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Name.StartsWith(
                    ".tfs-artwork-reload-",
                    StringComparison.OrdinalIgnoreCase))
            {
                directory.Delete(recursive: true);
            }
        }
        catch
        {
            // A later stale-staging cleanup can remove a briefly locked file.
        }
    }

    private static bool FilesAreEqual(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = File.OpenRead(leftPath);
        using var rightStream = File.OpenRead(rightPath);
        using var leftHash = SHA256.Create();
        using var rightHash = SHA256.Create();
        return leftHash.ComputeHash(leftStream).SequenceEqual(rightHash.ComputeHash(rightStream));
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    internal static bool HasPrimaryArtworkSet(string gridDirectory, uint appId)
    {
        return GetMissingPrimaryArtworkSlots(gridDirectory, appId).Count == 0;
    }

    internal static bool HasCompleteArtworkSet(string gridDirectory, uint appId)
    {
        return GetMissingArtworkSlots(gridDirectory, appId).Count == 0;
    }

    internal static IReadOnlyList<string> GetMissingArtworkSlots(
        string gridDirectory,
        uint appId)
    {
        var gridId = SteamShortcutIds.BuildGridId(appId);
        var missing = GetMissingPrimaryArtworkSlots(gridDirectory, appId).ToList();
        if (!HasArtworkVariant(gridDirectory, $"{gridId}_logo"))
        {
            missing.Add("logo");
        }
        if (!HasArtworkVariant(gridDirectory, $"{gridId}-icon"))
        {
            missing.Add("icon");
        }
        return missing;
    }

    internal static IReadOnlyList<string> GetMissingPrimaryArtworkSlots(
        string gridDirectory,
        uint appId)
    {
        var gridId = SteamShortcutIds.BuildGridId(appId);
        var missing = new List<string>(3);
        if (!HasArtworkVariant(gridDirectory, gridId))
        {
            missing.Add("library capsule");
        }
        if (!HasArtworkVariant(gridDirectory, $"{gridId}p"))
        {
            missing.Add("portrait");
        }
        if (!HasArtworkVariant(gridDirectory, $"{gridId}_hero"))
        {
            missing.Add("hero");
        }

        return missing;
    }

    private static bool HasArtworkVariant(string gridDirectory, string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico" })
        {
            if (IsUsableArtworkFile(Path.Combine(gridDirectory, fileStem + extension)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task CopyArtworkWithLimitAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            total += read;
            if (total > MaximumArtworkBytes)
            {
                throw new InvalidOperationException(
                    "The artwork download exceeded its 32 MB safety limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsUsableArtworkFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists ||
                file.Length < MinimumArtworkBytes)
            {
                return false;
            }

            if (ArtworkValidationCache.TryGetValue(path, out var cached) &&
                cached.Length == file.Length &&
                cached.LastWriteTicks == file.LastWriteTimeUtc.Ticks)
            {
                return cached.Usable;
            }

            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            var png = header[0] == 0x89 && header[1] == 0x50 &&
                header[2] == 0x4e && header[3] == 0x47 &&
                header[4] == 0x0d && header[5] == 0x0a &&
                header[6] == 0x1a && header[7] == 0x0a;
            var jpeg = header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
            var webp = header[0] == (byte)'R' && header[1] == (byte)'I' &&
                header[2] == (byte)'F' && header[3] == (byte)'F' &&
                header[8] == (byte)'W' && header[9] == (byte)'E' &&
                header[10] == (byte)'B' && header[11] == (byte)'P';
            var icon = header[0] == 0 && header[1] == 0 &&
                header[2] == 1 && header[3] == 0;
            var usable = png || jpeg || webp || icon;
            if (ArtworkValidationCache.Count >= MaximumArtworkValidationEntries)
            {
                ArtworkValidationCache.Clear();
            }
            ArtworkValidationCache[path] = new ArtworkValidationEntry(
                file.Length,
                file.LastWriteTimeUtc.Ticks,
                usable);
            return usable;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupStaleArtworkStagingFiles(string gridDirectory)
    {
        var now = DateTime.UtcNow;
        var previousTicks = Interlocked.Read(ref _lastStagingCleanupUtcTicks);
        if (previousTicks > 0 &&
            now - new DateTime(previousTicks, DateTimeKind.Utc) < TimeSpan.FromHours(1))
        {
            return;
        }
        if (Interlocked.CompareExchange(
                ref _lastStagingCleanupUtcTicks,
                now.Ticks,
                previousTicks) != previousTicks)
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         gridDirectory,
                         ".tfs-artwork-*.tmp",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < now.Subtract(TimeSpan.FromHours(1)))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(
                         gridDirectory,
                         ".tfs-artwork-reload-*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) <
                        now.Subtract(TimeSpan.FromHours(1)))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private sealed record ArtworkSlot(
        string SlotName,
        IReadOnlyList<string> RequestPaths,
        Func<string, string> FileStemBuilder,
        int? PreferredWidth,
        int? PreferredHeight,
        bool SupportsBadge = false);

    private readonly record struct ArtworkValidationEntry(
        long Length,
        long LastWriteTicks,
        bool Usable);

    private sealed record SteamGridDbListResponse<T>(
        bool Success,
        IReadOnlyList<T> Data);

    private sealed record SteamGridDbGameMatch(
        int Id,
        string Name,
        bool Verified);

    private sealed record SteamGridDbAsset(
        string Url,
        int? Width,
        int? Height);

    private sealed record DownloadedArtworkFile(
        string TempPath,
        string Extension);

    private sealed record DownloadedArtworkContent(
        string DataUri);
}

internal sealed record StoreSyncArtworkTarget(
    string TitleId,
    string Title,
    uint AppId,
    IReadOnlyList<string> SearchHints,
    int? CachedGameId,
    string CachedMatchName,
    string StoreId = "",
    string FallbackPortraitUrl = "",
    string FallbackHeroUrl = "",
    string RomPath = "",
    string RomSystemId = "",
    string RetroAchievementsApiKey = "",
    uint? RetroAchievementsGameId = null,
    string LocalInstallPath = "",
    string LocalExecutablePath = "",
    bool ForceReload = false);

internal sealed record StoreSyncArtworkSummary(
    int UpdatedTitleCount,
    int UpdatedFileCount,
    IReadOnlyList<string> UpdatedTitleIds);

internal sealed record StoreSyncArtworkMatch(
    int GameId,
    string MatchName);

internal sealed record StoreSyncArtworkPreview(
    string DataUri,
    int GameId,
    string MatchName);
