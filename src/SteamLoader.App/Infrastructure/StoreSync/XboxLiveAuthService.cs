using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// First-party Xbox account authentication shared by the isolated sign-in
/// window and the title-scoped achievement provider. The Microsoft client ID
/// is public desktop-app configuration; no client secret is used. Only the
/// refresh token is persisted, through StoreSyncSettingsStore's DPAPI layer.
/// </summary>
internal static class XboxLiveAuthService
{
    internal const string ClientId = "3785b570-8c14-4c8a-acf8-2cae0220960f";
    internal const string RedirectUri =
        "https://login.microsoftonline.com/common/oauth2/nativeclient";
    internal const string Scope = "XboxLive.signin XboxLive.offline_access";
    private const string XboxUserRelyingParty = "http://auth.xboxlive.com";
    private const string XboxLiveRelyingParty = "http://xboxlive.com";

    private static readonly string[] TrustedLoginDomains =
    [
        "live.com",
        "microsoft.com",
        "microsoftonline.com",
        "microsoftonline-p.com",
    ];

    public static XboxAuthorizationRequest CreateAuthorizationRequest()
    {
        var state = Guid.NewGuid().ToString("N");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["response_mode"] = "query",
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };
        var queryString = string.Join(
            "&",
            query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new XboxAuthorizationRequest(
            $"https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?{queryString}",
            state,
            verifier);
    }

    public static bool IsRedirect(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals(
            "/common/oauth2/nativeclient",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsTrustedLoginUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var host = uri.Host.ToLowerInvariant();
        return TrustedLoginDomains.Any(domain =>
            host.Equals(domain, StringComparison.Ordinal) ||
            host.EndsWith('.' + domain, StringComparison.Ordinal));
    }

    public static string ReadQueryValue(Uri uri, string name)
    {
        foreach (var segment in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2 &&
                Uri.UnescapeDataString(parts[0]).Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1].Replace('+', ' ')).Trim();
            }
        }
        return string.Empty;
    }

    public static async Task<XboxLiveSession> CompleteLoginAsync(
        string authorizationCode,
        string verifier,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var tokens = await ExchangeOAuthTokenAsync(
            httpClient,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = RedirectUri,
            },
            fallbackRefreshToken: string.Empty,
            cancellationToken).ConfigureAwait(false);
        return await ExchangeXboxTokensAsync(
            httpClient,
            tokens.AccessToken,
            tokens.RefreshToken,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<XboxLiveSession> RefreshSessionAsync(
        string refreshToken,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Connect your Xbox account first.");
        }
        var tokens = await ExchangeOAuthTokenAsync(
            httpClient,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken.Trim(),
            },
            refreshToken.Trim(),
            cancellationToken).ConfigureAwait(false);
        return await ExchangeXboxTokensAsync(
            httpClient,
            tokens.AccessToken,
            tokens.RefreshToken,
            cancellationToken).ConfigureAwait(false);
    }

    public static void PersistSession(
        StoreSyncSettingsStore settingsStore,
        XboxLiveSession session)
    {
        settingsStore.Update(configuration =>
        {
            var provider = configuration.UnifySteam.GameData.Providers["xbox-live"];
            provider.SecondaryCredential = session.RefreshToken;
            provider.AccountId = session.Xuid;
            provider.AccountName = session.Gamertag;
            provider.ConnectionStatus = "ready";
            provider.ConnectionDetail = $"Connected as {session.Gamertag}.";
            provider.ConnectionCheckedAtUtc = DateTimeOffset.UtcNow;
            provider.UpdatedAtUtc = DateTimeOffset.UtcNow;

            var store = configuration.UnifySteam.Stores["xbox-game-pass"];
            store.OpenXblAccountId = session.Xuid;
            store.OpenXblAccountName = session.Gamertag;
            store.OpenXblAccountCheckedAtUtc = DateTimeOffset.UtcNow;
        });
    }

    private static async Task<OAuthTokens> ExchangeOAuthTokenAsync(
        HttpClient httpClient,
        IReadOnlyDictionary<string, string> fields,
        string fallbackRefreshToken,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string>(fields)
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(body),
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Microsoft sign-in failed (HTTP {(int)response.StatusCode}).");
        }
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var accessToken = RequiredText(document.RootElement, "access_token", "access token");
        var refreshToken = OptionalText(document.RootElement, "refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            refreshToken = fallbackRefreshToken;
        }
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Microsoft returned no refresh token.");
        }
        return new OAuthTokens(accessToken, refreshToken);
    }

    private static async Task<XboxLiveSession> ExchangeXboxTokensAsync(
        HttpClient httpClient,
        string accessToken,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        string userToken = string.Empty;
        var lastStatus = 0;
        foreach (var prefix in new[] { "d=", "t=" })
        {
            using var request = JsonRequest(
                "https://user.auth.xboxlive.com/user/authenticate",
                new
                {
                    Properties = new
                    {
                        AuthMethod = "RPS",
                        SiteName = "user.auth.xboxlive.com",
                        RpsTicket = prefix + accessToken,
                    },
                    RelyingParty = XboxUserRelyingParty,
                    TokenType = "JWT",
                });
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            lastStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            userToken = RequiredText(document.RootElement, "Token", "user token");
            break;
        }
        if (string.IsNullOrWhiteSpace(userToken))
        {
            throw new InvalidOperationException(
                $"Xbox user authentication failed (HTTP {lastStatus}).");
        }

        using var xstsRequest = JsonRequest(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { userToken },
                },
                RelyingParty = XboxLiveRelyingParty,
                TokenType = "JWT",
            });
        using var xstsResponse = await httpClient.SendAsync(
            xstsRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!xstsResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Xbox security authorization failed (HTTP {(int)xstsResponse.StatusCode}).");
        }
        using var xstsDocument = JsonDocument.Parse(
            await xstsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var root = xstsDocument.RootElement;
        var token = RequiredText(root, "Token", "security token");
        var expiryText = OptionalText(root, "NotAfter");
        if (!root.TryGetProperty("DisplayClaims", out var displayClaims) ||
            !displayClaims.TryGetProperty("xui", out var users) ||
            users.ValueKind != JsonValueKind.Array ||
            users.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Xbox account identity is missing.");
        }
        var account = users[0];
        var userHash = RequiredText(account, "uhs", "user hash");
        var xuid = RequiredText(account, "xid", "account ID");
        var gamertag = OptionalText(account, "gtg");
        if (string.IsNullOrWhiteSpace(gamertag))
        {
            gamertag = $"Xbox {xuid[^Math.Min(6, xuid.Length)..]}";
        }
        return new XboxLiveSession(
            xuid,
            gamertag,
            userHash,
            token,
            DateTimeOffset.TryParse(expiryText, out var expiry)
                ? expiry
                : DateTimeOffset.UtcNow.AddHours(1),
            refreshToken);
    }

    private static HttpRequestMessage JsonRequest(string uri, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        return request;
    }

    private static string RequiredText(JsonElement owner, string name, string label)
    {
        var value = OptionalText(owner, name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16_384)
        {
            throw new InvalidOperationException($"Xbox returned an invalid {label}.");
        }
        return value;
    }

    private static string OptionalText(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record OAuthTokens(string AccessToken, string RefreshToken);
}

internal sealed record XboxAuthorizationRequest(
    string Url,
    string State,
    string CodeVerifier);

internal sealed record XboxLiveSession(
    string Xuid,
    string Gamertag,
    string UserHash,
    string Token,
    DateTimeOffset ExpiresAtUtc,
    string RefreshToken);
