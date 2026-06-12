using System.Net.Http.Headers;
using System.Text.Json;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Artwork;

public sealed class SteamGridDbManualArtworkService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, ArtworkSlot> ArtworkSlots =
        new Dictionary<string, ArtworkSlot>(StringComparer.OrdinalIgnoreCase)
        {
            ["grid_p"] = new(
                Id: "grid_p",
                ApiPath: "grids/game/{0}?types=static&dimensions=600x900,342x482,660x930&mimes=image/png,image/jpeg,image/webp&page={1}",
                FileStemBuilder: appId => $"{appId}p",
                SteamAssetType: 0,
                DefaultExtension: ".png",
                DefaultMime: "image/png"),
            ["grid_l"] = new(
                Id: "grid_l",
                ApiPath: "grids/game/{0}?types=static&dimensions=920x430,460x215&mimes=image/png,image/jpeg,image/webp&page={1}",
                FileStemBuilder: appId => appId.ToString(),
                SteamAssetType: 3,
                DefaultExtension: ".png",
                DefaultMime: "image/png"),
            ["hero"] = new(
                Id: "hero",
                ApiPath: "heroes/game/{0}?types=static&dimensions=1920x620,3840x1240,1600x650&mimes=image/png,image/jpeg,image/webp&page={1}",
                FileStemBuilder: appId => $"{appId}_hero",
                SteamAssetType: 1,
                DefaultExtension: ".png",
                DefaultMime: "image/png"),
            ["logo"] = new(
                Id: "logo",
                ApiPath: "logos/game/{0}?types=static&mimes=image/png,image/webp&page={1}",
                FileStemBuilder: appId => $"{appId}_logo",
                SteamAssetType: 2,
                DefaultExtension: ".png",
                DefaultMime: "image/png"),
            ["icon"] = new(
                Id: "icon",
                ApiPath: "icons/game/{0}?types=static&mimes=image/png,image/vnd.microsoft.icon&page={1}",
                FileStemBuilder: appId => $"{appId}-icon",
                SteamAssetType: 4,
                DefaultExtension: ".png",
                DefaultMime: "image/png"),
        };

    private readonly string _steamRootPath;
    private readonly ArtworkSettingsStore _settingsStore;
    private readonly object _gate = new();

    public SteamGridDbManualArtworkService(
        string steamRootPath,
        ArtworkSettingsStore settingsStore)
    {
        _steamRootPath = steamRootPath;
        _settingsStore = settingsStore;
    }

    public ArtworkSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(_settingsStore.Load());
        }
    }

    public ArtworkSnapshot ToggleSetting(string key)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            switch (key.Trim().ToLowerInvariant())
            {
                case "context-menu-enabled":
                    configuration.ContextMenuEnabled = !configuration.ContextMenuEnabled;
                    break;
                case "prefer-verified-matches":
                    configuration.PreferVerifiedMatches = !configuration.PreferVerifiedMatches;
                    break;
                default:
                    throw new InvalidOperationException("The requested SteamGridDB setting could not be found.");
            }

            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration);
        }
    }

    public ArtworkSnapshot SetApiKey(string value)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.SteamGridDbApiKey = value.Trim();
            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration);
        }
    }

    public ArtworkSnapshot ClearApiKey()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.SteamGridDbApiKey = string.Empty;
            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration);
        }
    }

    public ArtworkSnapshot SetResultLimit(int value)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.ResultLimit = Math.Clamp(value, 12, 72);
            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration);
        }
    }

    public bool IsContextMenuEnabled()
    {
        lock (_gate)
        {
            return _settingsStore.Load().ContextMenuEnabled;
        }
    }

    public async Task<IReadOnlyList<ArtworkGameSearchResult>> SearchGamesAsync(
        string term,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        ArtworkConfiguration configuration;
        lock (_gate)
        {
            configuration = _settingsStore.Load();
        }

        using var httpClient = CreateApiClient(configuration);
        using var response = await httpClient.GetAsync(
            $"search/autocomplete/{Uri.EscapeDataString(term.Trim())}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<SteamGridDbListResponse<SteamGridDbGameMatch>>(
            responseStream,
            JsonOptions,
            cancellationToken);

        var matches = payload?.Data?
            .Where(match => match.Id > 0 && !string.IsNullOrWhiteSpace(match.Name))
            .OrderByDescending(match => configuration.PreferVerifiedMatches && match.Verified)
            .Select(match => new ArtworkGameSearchResult(match.Id, match.Name, match.Verified))
            .Take(12);

        return matches?.ToArray() ?? [];
    }

    public async Task<IReadOnlyList<ArtworkAssetResult>> SearchAssetsAsync(
        int gameId,
        string assetType,
        int page,
        CancellationToken cancellationToken)
    {
        if (gameId <= 0 || !ArtworkSlots.TryGetValue(assetType, out var slot))
        {
            return [];
        }

        ArtworkConfiguration configuration;
        lock (_gate)
        {
            configuration = _settingsStore.Load();
        }

        using var httpClient = CreateApiClient(configuration);
        using var response = await httpClient.GetAsync(
            string.Format(slot.ApiPath, gameId, Math.Max(0, page)),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<SteamGridDbListResponse<SteamGridDbAsset>>(
            responseStream,
            JsonOptions,
            cancellationToken);

        return payload?.Data?
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Url))
            .Select(asset => new ArtworkAssetResult(
                asset.Id?.ToString() ?? asset.Url,
                asset.Url,
                string.IsNullOrWhiteSpace(asset.Thumb) ? asset.Url : asset.Thumb,
                asset.Width,
                asset.Height,
                asset.Mime ?? string.Empty,
                asset.Style ?? string.Empty))
            .Take(configuration.ResultLimit)
            .ToArray() ?? [];
    }

    public async Task<ArtworkApplyResult> ApplyAssetAsync(
        long appId,
        string assetType,
        string assetUrl,
        CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            return CreateFailedResult(appId, assetType, "A valid Steam app id is required.");
        }

        if (!ArtworkSlots.TryGetValue(assetType, out var slot))
        {
            return CreateFailedResult(appId, assetType, "The selected artwork type is not supported.");
        }

        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri))
        {
            return CreateFailedResult(appId, assetType, "A valid artwork URL is required.");
        }

        ArtworkConfiguration configuration;
        lock (_gate)
        {
            configuration = _settingsStore.Load();
        }

        using var httpClient = CreateAssetDownloadClient();
        var downloadedArtwork = await DownloadAssetAsync(httpClient, assetUri, cancellationToken);
        if (downloadedArtwork is null)
        {
            return CreateFailedResult(appId, assetType, "The selected artwork could not be downloaded. Please try another image or set your own SteamGridDB API key in Settings.");
        }

        var bytes = downloadedArtwork.Bytes;
        if (bytes.Length == 0)
        {
            return CreateFailedResult(appId, assetType, "The selected artwork download was empty.");
        }

        var extension = ResolveExtension(assetUri, downloadedArtwork.Mime, slot.DefaultExtension);
        var mime = downloadedArtwork.Mime ?? ResolveMime(extension, slot.DefaultMime);
        var writtenPath = TryWriteSteamGridFile(appId, slot, extension, bytes);

        return new ArtworkApplyResult(
            true,
            writtenPath is null
                ? "Artwork downloaded. Steam will apply it through the active Big Picture session."
                : "Artwork applied and written to Steam's grid folder.",
            appId,
            slot.Id,
            slot.SteamAssetType,
            extension.TrimStart('.'),
            mime,
            Convert.ToBase64String(bytes),
            writtenPath);
    }

    private HttpClient CreateApiClient(ArtworkConfiguration configuration)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(25),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ToolsForSteam/1.0 (Windows; Steam Big Picture)");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            GetEffectiveApiKey(configuration));
        return httpClient;
    }

    private static HttpClient CreateAssetDownloadClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(35),
        };

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 ToolsForSteam/1.0");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.steamgriddb.com/");
        return httpClient;
    }

    private static async Task<DownloadedArtwork?> DownloadAssetAsync(
        HttpClient httpClient,
        Uri assetUri,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(
                    assetUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode is >= 400 and < 500 and not 429)
                    {
                        return null;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250 + attempt * 400), cancellationToken);
                    continue;
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var memory = new MemoryStream();
                await input.CopyToAsync(memory, cancellationToken);
                var bytes = memory.ToArray();
                if (bytes.Length > 0)
                {
                    return new DownloadedArtwork(bytes, response.Content.Headers.ContentType?.MediaType);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 + attempt * 400), cancellationToken);
            }
        }

        return null;
    }

    private static ArtworkSnapshot BuildSnapshot(ArtworkConfiguration configuration)
    {
        var apiKey = GetEffectiveApiKey(configuration);
        var settings = new ArtworkSettingsState(
            ContextMenuEnabled: configuration.ContextMenuEnabled,
            SteamGridDbApiKeyConfigured: !string.IsNullOrWhiteSpace(apiKey),
            SteamGridDbApiKeyPreview: GetApiKeyPreview(configuration.SteamGridDbApiKey),
            PreferVerifiedMatches: configuration.PreferVerifiedMatches,
            ResultLimit: configuration.ResultLimit);

        var status = configuration.ContextMenuEnabled
            ? "Context menu entry is enabled."
            : "Context menu entry is hidden.";

        return new ArtworkSnapshot(settings, status);
    }

    private static string GetEffectiveApiKey(ArtworkConfiguration configuration)
    {
        return string.IsNullOrWhiteSpace(configuration.SteamGridDbApiKey)
            ? SteamGridDbArtworkDownloader.BuiltInApiKey
            : configuration.SteamGridDbApiKey.Trim();
    }

    private static string GetApiKeyPreview(string apiKey)
    {
        var trimmed = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Built-in key";
        }

        return trimmed.Length <= 8
            ? "Custom key"
            : $"{trimmed[..4]}...{trimmed[^4..]}";
    }

    private string? TryWriteSteamGridFile(long appId, ArtworkSlot slot, string extension, byte[] bytes)
    {
        try
        {
            var gridDirectory = ResolveGridDirectory();
            if (string.IsNullOrWhiteSpace(gridDirectory))
            {
                return null;
            }

            Directory.CreateDirectory(gridDirectory);
            var fileStem = slot.FileStemBuilder(appId);
            RemoveSlotVariants(gridDirectory, fileStem);

            var path = Path.Combine(gridDirectory, fileStem + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveGridDirectory()
    {
        var userdataPath = Path.Combine(_steamRootPath, "userdata");
        if (!Directory.Exists(userdataPath))
        {
            return null;
        }

        var accountId = ResolveLastUsedAccountId(userdataPath);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            accountId = Directory.GetDirectories(userdataPath)
                .Select(Path.GetFileName)
                .Where(value => !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit))
                .OrderByDescending(value =>
                {
                    try
                    {
                        return Directory.GetLastWriteTimeUtc(Path.Combine(userdataPath, value!));
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .FirstOrDefault();
        }

        return string.IsNullOrWhiteSpace(accountId)
            ? null
            : Path.Combine(userdataPath, accountId, "config", "grid");
    }

    private string? ResolveLastUsedAccountId(string userdataPath)
    {
        var loginUsersPath = Path.Combine(_steamRootPath, "config", "loginusers.vdf");
        if (!File.Exists(loginUsersPath))
        {
            return null;
        }

        var text = File.ReadAllText(loginUsersPath);
        var match = System.Text.RegularExpressions.Regex.Matches(
                text,
                "\"(?<steamId64>\\d{17})\"\\s*\\{(?<body>.*?)\\}",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Cast<System.Text.RegularExpressions.Match>()
            .FirstOrDefault(item => System.Text.RegularExpressions.Regex.IsMatch(
                item.Groups["body"].Value,
                "\"MostRecent\"\\s*\"1\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        if (match is null)
        {
            return null;
        }

        var steamId64 = match.Groups["steamId64"].Value;
        if (!ulong.TryParse(steamId64, out var steamId64Value))
        {
            return null;
        }

        var accountId = (steamId64Value & 0xFFFFFFFFUL).ToString();
        return Directory.Exists(Path.Combine(userdataPath, accountId))
            ? accountId
            : null;
    }

    private static string ResolveExtension(Uri assetUri, string? mime, string fallback)
    {
        var extension = Path.GetExtension(assetUri.AbsolutePath).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".ico")
        {
            return extension;
        }

        return mime?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/vnd.microsoft.icon" => ".ico",
            "image/x-icon" => ".ico",
            "image/png" => ".png",
            _ => fallback,
        };
    }

    private static string ResolveMime(string extension, string fallback)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".ico" => "image/vnd.microsoft.icon",
            ".png" => "image/png",
            _ => fallback,
        };
    }

    private static void RemoveSlotVariants(string gridDirectory, string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico" })
        {
            var path = Path.Combine(gridDirectory, fileStem + extension);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static ArtworkApplyResult CreateFailedResult(long appId, string assetType, string message)
    {
        return new ArtworkApplyResult(
            false,
            message,
            appId,
            assetType,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            null);
    }

    private sealed record ArtworkSlot(
        string Id,
        string ApiPath,
        Func<long, string> FileStemBuilder,
        int SteamAssetType,
        string DefaultExtension,
        string DefaultMime);

    private sealed record DownloadedArtwork(
        byte[] Bytes,
        string? Mime);

    private sealed record SteamGridDbListResponse<T>(
        bool Success,
        IReadOnlyList<T> Data);

    private sealed record SteamGridDbGameMatch(
        int Id,
        string Name,
        bool Verified);

    private sealed record SteamGridDbAsset(
        int? Id,
        string Url,
        string? Thumb,
        int? Width,
        int? Height,
        string? Mime,
        string? Style);
}
