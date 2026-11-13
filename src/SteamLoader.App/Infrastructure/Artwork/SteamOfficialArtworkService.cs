using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Artwork;

/// <summary>
/// Official Steam store artwork for the artwork page, read from Steam's public
/// <c>IStoreBrowseService/GetItems</c> (asset file names carry content hashes,
/// so new and seasonal "alt" assets resolve correctly where fixed CDN paths 404).
/// </summary>
public sealed class SteamOfficialArtworkService
{
    private const string AssetBase = "https://shared.akamai.steamstatic.com/store_item_assets/";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<uint, (DateTimeOffset At, IReadOnlyDictionary<string, IReadOnlyList<ArtworkPickerAsset>> Assets)> _cache = new();

    public SteamOfficialArtworkService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ArtworkPickerAsset>>> GetAsync(
        uint steamAppId,
        CancellationToken cancellationToken)
    {
        if (steamAppId == 0)
        {
            return Empty();
        }

        if (_cache.TryGetValue(steamAppId, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheLifetime)
        {
            return cached.Assets;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _httpClient.GetAsync(BuildUrl(steamAppId), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Empty();
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var assets = Parse(json, steamAppId);
            _cache[steamAppId] = (DateTimeOffset.UtcNow, assets);
            return assets;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Empty();
        }
    }

    internal static string BuildUrl(uint steamAppId)
    {
        var input = JsonSerializer.Serialize(new
        {
            ids = new[] { new { appid = steamAppId } },
            context = new { language = "english", country_code = "US" },
            data_request = new { include_assets = true },
        });
        return $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json={Uri.EscapeDataString(input)}";
    }

    /// <summary>Maps Steam's asset list to the artwork page's types, best first.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<ArtworkPickerAsset>> Parse(string json, uint steamAppId)
    {
        var result = new Dictionary<string, List<ArtworkPickerAsset>>(StringComparer.Ordinal)
        {
            ["grid_p"] = [], ["grid_l"] = [], ["hero"] = [], ["logo"] = [], ["icon"] = [],
        };

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("store_items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0 ||
                !items[0].TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Object)
            {
                return Freeze(result);
            }

            var format = Read(assets, "asset_url_format");
            if (!format.Contains("${FILENAME}", StringComparison.Ordinal))
            {
                format = $"steam/apps/{steamAppId.ToString(CultureInfo.InvariantCulture)}/${{FILENAME}}";
            }

            void Add(string type, string key, int width, int height, string label)
            {
                var file = Read(assets, key);
                if (file.Length == 0 || file.Contains("..", StringComparison.Ordinal))
                {
                    return;
                }

                var url = AssetBase + format.Replace("${FILENAME}", file, StringComparison.Ordinal);
                if (result[type].Any(asset => asset.Url == url))
                {
                    return;
                }

                var mime = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                result[type].Add(new ArtworkPickerAsset(
                    $"steam:{key}", "steam", url, url, width, height, mime, "official", false, "Steam", 0, false, false, label));
            }

            var alt = Read(assets, "library_capsule").Contains("alt_assets", StringComparison.Ordinal) ? " (event)" : string.Empty;
            Add("grid_p", "library_capsule_2x", 1200, 1800, $"Library cover{alt}");
            Add("grid_p", "library_capsule", 600, 900, $"Library cover{alt}");
            Add("grid_l", "header_2x", 920, 430, "Store header");
            Add("grid_l", "header", 460, 215, "Store header");
            Add("grid_l", "main_capsule_2x", 1232, 706, "Store capsule");
            Add("grid_l", "main_capsule", 616, 353, "Store capsule");
            Add("hero", "library_hero_2x", 3840, 1240, "Library hero");
            Add("hero", "library_hero", 1920, 620, "Library hero");
            // GetItems does not list the library logo (the plain logo.png path is often an
            // opaque store image, so it is not offered); logos come from SteamGridDB.
            Add("logo", "library_logo_2x", 1280, 720, "Library logo");
            Add("logo", "library_logo", 640, 360, "Library logo");

            var icon = Read(assets, "community_icon");
            if (icon.Length > 0 && icon.All(char.IsAsciiLetterOrDigit))
            {
                var url = $"https://shared.akamai.steamstatic.com/community_assets/images/apps/{steamAppId.ToString(CultureInfo.InvariantCulture)}/{icon}.jpg";
                result["icon"].Add(new ArtworkPickerAsset("steam:community_icon", "steam", url, url, 32, 32, "image/jpeg", "official", false, "Steam", 0, false, false, "Community icon"));
            }
        }
        catch (JsonException)
        {
        }

        return Freeze(result);
    }

    private static string Read(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static IReadOnlyDictionary<string, IReadOnlyList<ArtworkPickerAsset>> Freeze(Dictionary<string, List<ArtworkPickerAsset>> source) =>
        source.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<ArtworkPickerAsset>)entry.Value.ToArray(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<ArtworkPickerAsset>> Empty() =>
        Freeze(new Dictionary<string, List<ArtworkPickerAsset>>(StringComparer.Ordinal)
        {
            ["grid_p"] = [], ["grid_l"] = [], ["hero"] = [], ["logo"] = [], ["icon"] = [],
        });
}
