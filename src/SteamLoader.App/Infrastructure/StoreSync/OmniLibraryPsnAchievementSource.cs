using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// NPSSO-backed PlayStation trophy adapter. Tokens are renewed behind a
/// single-flight gate and only the refresh token is persisted (DPAPI protected).
/// Trophy title mapping is exact and cached per OmniLibrary game.
/// </summary>
internal sealed class OmniLibraryPsnAchievementSource(
    HttpClient httpClient,
    StoreSyncSettingsStore? settingsStore)
    : IOmniLibraryAchievementSource
{
    private const string TrophyApi = "https://m.np.playstation.com/api/trophy/v1";
    private const string MobileCodeUrl =
        "https://ca.account.sony.com/api/authz/v3/oauth/authorize?access_type=offline&client_id=09515159-7237-4370-9b40-3806e67c0891&redirect_uri=com.scee.psxandroid.scecompcall%3A%2F%2Fredirect&response_type=code&scope=psn%3Amobile.v2.core%20psn%3Aclientapp";
    private const string MobileTokenUrl =
        "https://ca.account.sony.com/api/authz/v3/oauth/token";
    private const string MobileTokenAuth =
        "MDk1MTUxNTktNzIzNy00MzcwLTliNDAtMzgwNmU2N2MwODkxOnVjUGprYTV0bnRCMktxc1A=";
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string _accessToken = string.Empty;
    private DateTimeOffset _accessTokenExpiresAtUtc = DateTimeOffset.MinValue;

    public string ProviderId => "playstation-network";

    public async Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken)
    {
        var npsso = context.Provider.Credential?.Trim() ?? string.Empty;
        var refreshToken = context.Provider.SecondaryCredential?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(npsso) && string.IsNullOrWhiteSpace(refreshToken))
        {
            return Unavailable(
                "setup-required",
                "Add an NPSSO token from the signed-in PlayStation account.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(
                npsso,
                refreshToken,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PsnAuthenticationException)
        {
            return Unavailable(
                "authentication-required",
                "PlayStation rejected the saved session. Add a fresh NPSSO token.",
                DateTimeOffset.UtcNow.AddHours(1));
        }

        var game = context.GameDetail.Game;
        var localGameId = game?.Id?.Trim() ?? string.Empty;
        var npCommunicationId = !string.IsNullOrWhiteSpace(localGameId) &&
                                context.Provider.GameIdOverrides.TryGetValue(
                                    localGameId,
                                    out var mapped)
            ? mapped?.Trim() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(npCommunicationId) &&
            (game?.StoreTitleId?.StartsWith("NPWR", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            npCommunicationId = game.StoreTitleId.Trim();
        }
        if (string.IsNullOrWhiteSpace(npCommunicationId))
        {
            npCommunicationId = await ResolveTitleAsync(
                accessToken,
                game?.Title ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(npCommunicationId))
            {
                PersistMapping(localGameId, npCommunicationId);
            }
        }
        if (string.IsNullOrWhiteSpace(npCommunicationId))
        {
            return Unavailable(
                "mapping-unavailable",
                "PlayStation did not expose one exact trophy-title match for this game.",
                DateTimeOffset.UtcNow.AddDays(7));
        }

        try
        {
            var details = await GetTrophyDocumentWithSuffixRetryAsync(
                accessToken,
                $"{TrophyApi}/npCommunicationIds/{Uri.EscapeDataString(npCommunicationId)}/trophyGroups/all/trophies",
                cancellationToken).ConfigureAwait(false);
            using (details)
            {
                JsonDocument? userProgress = null;
                try
                {
                    userProgress = await GetTrophyDocumentWithSuffixRetryAsync(
                        accessToken,
                        $"{TrophyApi}/users/me/npCommunicationIds/{Uri.EscapeDataString(npCommunicationId)}/trophyGroups/all/trophies",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    // A private profile still permits definition metadata.
                }
                using (userProgress)
                {
                    var items = ParseTrophies(
                        details.RootElement,
                        userProgress?.RootElement);
                    if (items.Count == 0)
                    {
                        return new OmniLibraryAchievementRefreshResult(
                            new OmniLibraryAchievementMetadata(
                                "PlayStation Network",
                                "no-achievements",
                                "PlayStation exposes no trophies for this title.",
                                0,
                                0,
                                []),
                            true,
                            true,
                            JsonSerializer.Serialize(new { npCommunicationId }),
                            null,
                            string.Empty);
                    }
                    return new OmniLibraryAchievementRefreshResult(
                        new OmniLibraryAchievementMetadata(
                            "PlayStation Network",
                            "ready",
                            "Verified PlayStation trophy progress.",
                            items.Count(item => item.Unlocked),
                            items.Count,
                            items),
                        true,
                        true,
                        JsonSerializer.Serialize(new { npCommunicationId }),
                        null,
                        string.Empty);
                }
            }
        }
        catch (PsnAuthenticationException)
        {
            _accessToken = string.Empty;
            _accessTokenExpiresAtUtc = DateTimeOffset.MinValue;
            return Unavailable(
                "authentication-required",
                "The PlayStation session expired. Add a fresh NPSSO token.",
                DateTimeOffset.UtcNow.AddHours(1));
        }
    }

    private async Task<string> GetAccessTokenAsync(
        string npsso,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            DateTimeOffset.UtcNow < _accessTokenExpiresAtUtc)
        {
            return _accessToken;
        }
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                DateTimeOffset.UtcNow < _accessTokenExpiresAtUtc)
            {
                return _accessToken;
            }

            PsnToken token;
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                try
                {
                    token = await RequestTokenAsync(
                        new Dictionary<string, string>
                        {
                            ["refresh_token"] = refreshToken,
                            ["grant_type"] = "refresh_token",
                            ["token_format"] = "jwt",
                            ["scope"] = "psn:mobile.v2.core psn:clientapp",
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (PsnAuthenticationException) when (!string.IsNullOrWhiteSpace(npsso))
                {
                    token = await BootstrapNpssoAsync(npsso, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                token = await BootstrapNpssoAsync(npsso, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new PsnAuthenticationException();
            }
            _accessToken = token.AccessToken;
            _accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(60, token.ExpiresIn - 300));
            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                PersistRefreshToken(token.RefreshToken);
            }
            return _accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<PsnToken> BootstrapNpssoAsync(
        string npsso,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(npsso))
        {
            throw new PsnAuthenticationException();
        }
        var cookies = new CookieContainer();
        cookies.Add(
            new Uri("https://ca.account.sony.com"),
            new Cookie("npsso", npsso, "/", "ca.account.sony.com"));
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var response = await client.GetAsync(
            MobileCodeUrl,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Redirect ||
            response.Headers.Location is null)
        {
            throw new PsnAuthenticationException();
        }
        var code = ParseQueryValue(response.Headers.Location, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new PsnAuthenticationException();
        }
        return await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["redirect_uri"] = "com.scee.psxandroid.scecompcall://redirect",
                ["grant_type"] = "authorization_code",
                ["token_format"] = "jwt",
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PsnToken> RequestTokenAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MobileTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            MobileTokenAuth);
        request.Content = new FormUrlEncodedContent(values);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden)
        {
            throw new PsnAuthenticationException();
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new PsnToken(
            String(document.RootElement, "access_token"),
            String(document.RootElement, "refresh_token"),
            Int(document.RootElement, "expires_in"));
    }

    private async Task<string> ResolveTitleAsync(
        string accessToken,
        string title,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return string.Empty;
        }
        var matches = new List<string>();
        var offset = 0;
        for (var page = 0; page < 10; page++)
        {
            using var document = await GetAuthorizedJsonAsync(
                accessToken,
                $"{TrophyApi}/users/me/trophyTitles?limit=200&offset={offset.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("trophyTitles", out var titles) ||
                titles.ValueKind != JsonValueKind.Array)
            {
                break;
            }
            var count = 0;
            foreach (var item in titles.EnumerateArray())
            {
                count++;
                if (NormalizeTitle(String(item, "trophyTitleName")).Equals(
                        normalizedTitle,
                        StringComparison.Ordinal))
                {
                    var id = String(item, "npCommunicationId");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        matches.Add(id);
                    }
                }
            }
            if (count < 200)
            {
                break;
            }
            offset += count;
        }
        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() == 1
            ? matches[0]
            : string.Empty;
    }

    private async Task<JsonDocument> GetTrophyDocumentWithSuffixRetryAsync(
        string accessToken,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        HttpRequestException? lastError = null;
        foreach (var suffix in new[] { string.Empty, "?npServiceName=trophy" })
        {
            try
            {
                return await GetAuthorizedJsonAsync(
                    accessToken,
                    baseUrl + suffix,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                lastError = error;
            }
        }
        throw lastError ?? new HttpRequestException("PlayStation trophy request failed.");
    }

    private async Task<JsonDocument> GetAuthorizedJsonAsync(
        string accessToken,
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PsnAuthenticationException();
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HttpRequestException("PlayStation trophy resource was not found.");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<OmniLibraryAchievementItemMetadata> ParseTrophies(
        JsonElement definitionsRoot,
        JsonElement? progressRoot)
    {
        var progress = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (progressRoot is { } progressOwner &&
            progressOwner.TryGetProperty("trophies", out var progressItems) &&
            progressItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in progressItems.EnumerateArray())
            {
                progress[TrophyKey(item)] = item.Clone();
            }
        }
        if (!definitionsRoot.TryGetProperty("trophies", out var definitions) ||
            definitions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new Dictionary<string, OmniLibraryAchievementItemMetadata>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions.EnumerateArray())
        {
            var id = TrophyKey(definition);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            var hasProgress = progress.TryGetValue(id, out var user);
            var unlocked = hasProgress && Bool(user, "earned");
            result[id] = new OmniLibraryAchievementItemMetadata(
                id,
                FirstNonEmpty(String(definition, "trophyName"), id),
                String(definition, "trophyDetail"),
                unlocked,
                Bool(definition, "hidden"),
                unlocked ? ParseDate(String(user, "earnedDateTime")) : null,
                String(definition, "trophyIconUrl"),
                unlocked ? 1 : 0,
                1);
        }
        return result.Values.ToArray();
    }

    private void PersistRefreshToken(string refreshToken)
    {
        if (settingsStore is null)
        {
            return;
        }
        settingsStore.Update(configuration =>
        {
            var provider = configuration.UnifySteam.GameData.Providers[ProviderId];
            provider.SecondaryCredential = refreshToken.Trim();
            provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
        });
    }

    private void PersistMapping(string localGameId, string npCommunicationId)
    {
        if (settingsStore is null || string.IsNullOrWhiteSpace(localGameId))
        {
            return;
        }
        settingsStore.Update(configuration =>
        {
            var provider = configuration.UnifySteam.GameData.Providers[ProviderId];
            provider.GameIdOverrides[localGameId.Trim()] = npCommunicationId.Trim();
            provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
        });
    }

    private static string ParseQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]).Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }
        return string.Empty;
    }

    private static string TrophyKey(JsonElement item) =>
        $"{FirstNonEmpty(String(item, "trophyGroupId"), "default")}:{Int(item, "trophyId").ToString(CultureInfo.InvariantCulture)}";

    private static string NormalizeTitle(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormD)
            .Where(character =>
                CharUnicodeInfo.GetUnicodeCategory(character) !=
                UnicodeCategory.NonSpacingMark &&
                char.IsLetterOrDigit(character)))
            .ToLowerInvariant();

    private static string String(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int Int(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var node) &&
        node.ValueKind == JsonValueKind.Number &&
        node.TryGetInt32(out var value)
            ? value
            : 0;

    private static bool Bool(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
        string.Empty;

    private static OmniLibraryAchievementRefreshResult Unavailable(
        string status,
        string detail,
        DateTimeOffset retryAfterUtc) =>
        new(
            new OmniLibraryAchievementMetadata(
                "PlayStation Network",
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

    private sealed class PsnAuthenticationException : Exception;

    private sealed record PsnToken(
        string AccessToken,
        string RefreshToken,
        int ExpiresIn);
}
