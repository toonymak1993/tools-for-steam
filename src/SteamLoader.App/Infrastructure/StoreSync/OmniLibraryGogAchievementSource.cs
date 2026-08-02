using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Title-scoped GOG achievement adapter. The client-id resolution and gameplay
/// endpoint flow are adapted from the MIT-licensed PlayniteAchievements GOG
/// provider. OmniLibrary reuses only its own isolated GOG session.
/// </summary>
internal sealed class OmniLibraryGogAchievementSource : IOmniLibraryAchievementSource
{
    private const string GogClientId = "46899977096215655";
    private const string GogClientSecret =
        "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    private const string DefaultLocale = "en-US";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 OmniLibrary/1.0";
    private static readonly SemaphoreSlim CredentialGate = new(1, 1);

    private readonly HttpClient _httpClient;
    private readonly StoreSyncSettingsStore? _settingsStore;
    private readonly string _authPath;

    public OmniLibraryGogAchievementSource(
        HttpClient httpClient,
        StoreSyncSettingsStore? settingsStore,
        string? authPath = null)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
        _authPath = string.IsNullOrWhiteSpace(authPath)
            ? ManagedGogDlHelper.AuthPath
            : Path.GetFullPath(authPath);
    }

    public string ProviderId => "gog";

    public async Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken)
    {
        var productId = context.GameDetail.Game?.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(productId))
        {
            return Unavailable(
                "mapping-unavailable",
                "GOG did not include a product ID for this library entry.",
                DateTimeOffset.UtcNow.AddDays(7));
        }

        var credential = await LoadCredentialAsync(cancellationToken)
            .ConfigureAwait(false);
        if (credential is null || string.IsNullOrWhiteSpace(credential.UserId))
        {
            return Unavailable(
                "not-connected",
                "Connect GOG in OmniLibrary to show verified achievement progress.",
                DateTimeOffset.UtcNow.AddHours(1));
        }

        var clientId = context.Provider.GameIdOverrides.TryGetValue(
            productId,
            out var cachedClientId)
            ? cachedClientId?.Trim() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = await ResolveClientIdAsync(productId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                PersistClientId(productId, clientId);
            }
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Unavailable(
                "no-achievements",
                "GOG exposes no achievement client for this title.",
                DateTimeOffset.UtcNow.AddDays(7));
        }

        var locale = NormalizeLocale(context.Provider.Locale);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gameplay.gog.com/clients/{Uri.EscapeDataString(clientId)}/users/{Uri.EscapeDataString(credential.UserId)}/achievements?locale={Uri.EscapeDataString(locale)}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            credential.AccessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", locale);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return NoAchievements(clientId);
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Unavailable(
                "authentication-required",
                "The saved GOG session expired. Reconnect GOG in OmniLibrary.",
                DateTimeOffset.UtcNow.AddHours(1));
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GOG achievements returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = ParseAchievements(document.RootElement);
        if (items.Count == 0)
        {
            return NoAchievements(clientId);
        }

        var unlocked = items.Count(item => item.Unlocked);
        return new OmniLibraryAchievementRefreshResult(
            new OmniLibraryAchievementMetadata(
                "GOG",
                "ready",
                "Verified achievement progress from the connected GOG account.",
                unlocked,
                items.Count,
                items),
            DefinitionsRefreshed: true,
            ProgressRefreshed: true,
            ProviderState: JsonSerializer.Serialize(new
            {
                productId,
                clientId,
                userId = credential.UserId,
            }),
            RetryAfterUtc: null,
            Error: string.Empty);
    }

    private async Task<GogCredential?> LoadCredentialAsync(
        CancellationToken cancellationToken)
    {
        await CredentialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _authPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var root = JsonNode.Parse(
                await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false)) as JsonObject;
            var node = root?[GogClientId] as JsonObject ??
                       root?.FirstOrDefault().Value as JsonObject;
            var credential = ParseCredential(node);
            if (credential is null ||
                DateTimeOffset.UtcNow < credential.ExpiresAtUtc.AddMinutes(-2))
            {
                return credential;
            }

            if (string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                return null;
            }

            var tokenUrl =
                $"https://auth.gog.com/token?client_id={GogClientId}&client_secret={GogClientSecret}&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(credential.RefreshToken)}";
            using var response = await _httpClient.GetAsync(tokenUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var refreshedNode = JsonNode.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false)) as JsonObject;
            var refreshed = ParseCredential(refreshedNode, credential.UserId);
            if (refreshed is null)
            {
                return null;
            }

            root ??= new JsonObject();
            root[GogClientId] = BuildCredentialNode(refreshed);
            await SaveJsonAtomicallyAsync(path, root, cancellationToken)
                .ConfigureAwait(false);
            return refreshed;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CredentialGate.Release();
        }
    }

    private async Task<string> ResolveClientIdAsync(
        string productId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.gogdb.org/data/products/{Uri.EscapeDataString(productId)}/product.json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return string.Empty;
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var direct = FirstString(document.RootElement, "client_id", "clientId");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var buildUrl = SelectBuildMetadataUrl(document.RootElement);
        return string.IsNullOrWhiteSpace(buildUrl)
            ? string.Empty
            : await ResolveBuildClientIdAsync(buildUrl, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<string> ResolveBuildClientIdAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return string.Empty;
        }
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var json = DecodeBuildMetadata(payload);
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }
        using var document = JsonDocument.Parse(json);
        return FirstString(document.RootElement, "clientId", "client_id");
    }

    private void PersistClientId(string productId, string clientId)
    {
        _settingsStore?.Update(configuration =>
        {
            if (!configuration.UnifySteam.GameData.Providers.TryGetValue(
                    ProviderId,
                    out var provider) || provider is null)
            {
                return;
            }
            provider.GameIdOverrides[productId] = clientId;
            provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
        });
    }

    private static IReadOnlyList<OmniLibraryAchievementItemMetadata> ParseAchievements(
        JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<OmniLibraryAchievementItemMetadata>();
        foreach (var item in items.EnumerateArray())
        {
            var id = FirstString(item, "achievement_key", "achievement_id", "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            var name = FirstString(item, "name", "achievement_key", "achievement_id", "id");
            var unlockedAt = ParseDate(item, "date_unlocked");
            var unlocked = unlockedAt.HasValue || FirstBool(item, "unlocked", "is_unlocked");
            var visible = !item.TryGetProperty("visible", out var visibleNode) ||
                          visibleNode.ValueKind != JsonValueKind.False;
            result.Add(new OmniLibraryAchievementItemMetadata(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name,
                FirstString(item, "description"),
                unlocked,
                !visible,
                unlockedAt,
                unlocked
                    ? FirstString(item, "image_url_unlocked", "imageUrlUnlocked", "image_url_locked", "imageUrlLocked")
                    : FirstString(item, "image_url_locked", "imageUrlLocked", "image_url_unlocked", "imageUrlUnlocked"),
                unlocked ? 1 : 0,
                1));
        }
        return result;
    }

    private static string SelectBuildMetadataUrl(JsonElement root)
    {
        if (!root.TryGetProperty("builds", out var builds) ||
            builds.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return builds.EnumerateArray()
            .Select(build => new
            {
                Link = FirstString(build, "link"),
                Listed = FirstBool(build, "listed"),
                Published = ParseDate(build, "date_published") ?? DateTimeOffset.MinValue,
            })
            .Where(build => Uri.TryCreate(build.Link, UriKind.Absolute, out _))
            .OrderByDescending(build => build.Listed)
            .ThenByDescending(build => build.Published)
            .Select(build => build.Link)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string DecodeBuildMetadata(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return string.Empty;
        }
        var direct = Encoding.UTF8.GetString(payload);
        if (LooksLikeJson(direct))
        {
            return direct;
        }

        try
        {
            using var input = new MemoryStream(payload);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.UTF8);
            var value = reader.ReadToEnd();
            return LooksLikeJson(value) ? value : string.Empty;
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
    }

    private static bool LooksLikeJson(string? value)
    {
        var trimmed = value?.TrimStart() ?? string.Empty;
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static GogCredential? ParseCredential(
        JsonObject? node,
        string fallbackUserId = "")
    {
        if (node is null)
        {
            return null;
        }
        var accessToken = NodeString(node, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }
        var loginTime = NodeDouble(node, "loginTime") is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(seconds))
            : DateTimeOffset.UtcNow;
        var expiresIn = NodeDouble(node, "expires_in") is { } expiry
            ? Math.Max(1, (int)expiry)
            : 3600;
        return new GogCredential(
            accessToken,
            NodeString(node, "refresh_token"),
            FirstNonEmpty(NodeString(node, "user_id"), fallbackUserId),
            loginTime,
            loginTime.AddSeconds(expiresIn));
    }

    private static JsonObject BuildCredentialNode(GogCredential credential) => new()
    {
        ["access_token"] = credential.AccessToken,
        ["refresh_token"] = credential.RefreshToken,
        ["user_id"] = credential.UserId,
        ["expires_in"] = Math.Max(
            1,
            (int)(credential.ExpiresAtUtc - credential.LoginTimeUtc).TotalSeconds),
        ["loginTime"] = credential.LoginTimeUtc.ToUnixTimeSeconds(),
        ["token_type"] = "bearer",
    };

    private static async Task SaveJsonAtomicallyAsync(
        string path,
        JsonObject root,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                }),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, path + ".bak", true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static OmniLibraryAchievementRefreshResult NoAchievements(string clientId) =>
        new(
            new OmniLibraryAchievementMetadata(
                "GOG",
                "no-achievements",
                "GOG exposes no achievements for this title.",
                0,
                0,
                []),
            true,
            true,
            JsonSerializer.Serialize(new { clientId }),
            null,
            string.Empty);

    private static OmniLibraryAchievementRefreshResult Unavailable(
        string status,
        string detail,
        DateTimeOffset? retryAfterUtc) =>
        new(
            new OmniLibraryAchievementMetadata(
                "GOG",
                status,
                detail,
                0,
                0,
                []),
            true,
            true,
            string.Empty,
            retryAfterUtc,
            string.Empty);

    private static string NormalizeLocale(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultLocale;
        }
        try
        {
            return CultureInfo.GetCultureInfo(value.Trim()).Name;
        }
        catch (CultureNotFoundException)
        {
            return DefaultLocale;
        }
    }

    private static string FirstString(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }
        return string.Empty;
    }

    private static bool FirstBool(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value))
            {
                continue;
            }
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number != 0;
            }
        }
        return false;
    }

    private static DateTimeOffset? ParseDate(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var date))
        {
            return date.ToUniversalTime();
        }
        return null;
    }

    private static string NodeString(JsonObject node, string name) =>
        node[name]?.GetValue<string?>()?.Trim() ?? string.Empty;

    private static double? NodeDouble(JsonObject node, string name)
    {
        var value = node[name];
        if (value is null)
        {
            return null;
        }
        return value is JsonValue jsonValue &&
               jsonValue.TryGetValue<double>(out var number)
            ? number
            : null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ??
        string.Empty;

    private sealed record GogCredential(
        string AccessToken,
        string RefreshToken,
        string UserId,
        DateTimeOffset LoginTimeUtc,
        DateTimeOffset ExpiresAtUtc);
}
