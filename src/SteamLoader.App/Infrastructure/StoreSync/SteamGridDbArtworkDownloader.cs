using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed class SteamGridDbArtworkDownloader
{
    public const string BuiltInApiKey = "96b06c7e805c21ee48af894587118c4c";

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
            PreferredHeight: 430),
        new(
            SlotName: "portrait",
            RequestPaths:
            [
                "grids/game/{0}?types=static&dimensions=600x900&mimes=image/png,image/jpeg",
                "grids/game/{0}?types=static&mimes=image/png,image/jpeg"
            ],
            FileStemBuilder: gridId => $"{gridId}p",
            PreferredWidth: 600,
            PreferredHeight: 900),
        new(
            SlotName: "hero",
            RequestPaths:
            [
                "heroes/game/{0}?types=static&dimensions=1920x620&mimes=image/png,image/jpeg",
                "heroes/game/{0}?types=static&mimes=image/png,image/jpeg"
            ],
            FileStemBuilder: gridId => $"{gridId}_hero",
            PreferredWidth: 1920,
            PreferredHeight: 620),
        new(
            SlotName: "logo",
            RequestPaths:
            [
                "logos/game/{0}?types=static&mimes=image/png"
            ],
            FileStemBuilder: gridId => $"{gridId}_logo",
            PreferredWidth: null,
            PreferredHeight: null),
        new(
            SlotName: "icon",
            RequestPaths:
            [
                "icons/game/{0}?types=static&dimensions=256&mimes=image/png,image/vnd.microsoft.icon",
                "icons/game/{0}?types=static&mimes=image/png,image/vnd.microsoft.icon"
            ],
            FileStemBuilder: gridId => $"{gridId}-icon",
            PreferredWidth: 256,
            PreferredHeight: 256),
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

            var response = await httpClient.GetAsync(
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
        CancellationToken cancellationToken)
    {
        var updatedFileCount = 0;

        foreach (var slot in ArtworkSlots)
        {
            try
            {
                if (await DownloadArtworkSlotAsync(httpClient, gridDirectory, gridId, slot, gameId, cancellationToken))
                {
                    updatedFileCount++;
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
                cancellationToken);
        }

        return updatedFileCount;
    }

    private async Task<int> DownloadMissingPrimaryArtworkAsync(
        HttpClient httpClient,
        string gridDirectory,
        string gridId,
        int gameId,
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
                }
            }
            catch
            {
            }
        }

        return updatedFileCount;
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
            var response = await httpClient.GetAsync(
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
        using var response = await httpClient.GetAsync(
            assetUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var extension = ResolveFileExtension(assetUrl, response.Content.Headers.ContentType?.MediaType);
        if (extension is null)
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"steamloader-grid-{Guid.NewGuid():N}{extension}");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(tempPath);
        await input.CopyToAsync(output, cancellationToken);
        return new DownloadedArtworkFile(tempPath, extension);
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

        var mimeType = ResolveMimeType(assetUrl, response.Content.Headers.ContentType?.MediaType);
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, cancellationToken);
        return new DownloadedArtworkContent(
            $"data:{mimeType};base64,{Convert.ToBase64String(output.ToArray())}");
    }

    private static bool PromoteDownloadedArtwork(
        string gridDirectory,
        string fileStem,
        DownloadedArtworkFile asset)
    {
        var targetPath = Path.Combine(gridDirectory, fileStem + asset.Extension);
        Directory.CreateDirectory(gridDirectory);

        if (File.Exists(targetPath) && FilesAreEqual(asset.TempPath, targetPath))
        {
            RemoveSlotVariantsExcept(gridDirectory, fileStem, targetPath);
            return false;
        }

        RemoveSlotVariantsExcept(gridDirectory, fileStem, targetPath);
        File.Copy(asset.TempPath, targetPath, overwrite: true);
        return true;
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
        return extension is ".png" or ".jpg" or ".jpeg" or ".ico"
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
                File.Delete(path);
            }
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

    private static bool HasArtworkVariant(string gridDirectory, string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico" })
        {
            if (File.Exists(Path.Combine(gridDirectory, fileStem + extension)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ArtworkSlot(
        string SlotName,
        IReadOnlyList<string> RequestPaths,
        Func<string, string> FileStemBuilder,
        int? PreferredWidth,
        int? PreferredHeight);

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
    string CachedMatchName);

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
