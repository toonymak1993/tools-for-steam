using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Title-scoped EA Juno adapter. It consumes a user-supplied EA bearer token,
/// resolves the player identity and owned offer IDs once per token, and stores
/// only exact title mappings. The short-lived token remains DPAPI protected by
/// the shared provider settings store.
/// </summary>
internal sealed class OmniLibraryEaAchievementSource(
    HttpClient httpClient,
    StoreSyncSettingsStore? settingsStore)
    : IOmniLibraryAchievementSource
{
    private const string GraphQlEndpoint =
        "https://service-aggregation-layer.juno.ea.com/graphql";
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private string _assetTokenFingerprint = string.Empty;
    private EaAccountSnapshot? _account;

    public string ProviderId => "ea";

    public async Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken)
    {
        var token = context.Provider.Credential?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unavailable(
                "setup-required",
                "Connect EA or add a current EA access token.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        EaAccountSnapshot account;
        try
        {
            account = await GetAccountAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (EaAuthenticationException)
        {
            return Unavailable(
                "authentication-required",
                "EA rejected the saved session. Reconnect the EA provider.",
                DateTimeOffset.UtcNow.AddHours(1));
        }

        var game = context.GameDetail.Game;
        var localGameId = game?.Id?.Trim() ?? string.Empty;
        var offerId = !string.IsNullOrWhiteSpace(localGameId) &&
                      context.Provider.GameIdOverrides.TryGetValue(localGameId, out var mapped)
            ? mapped?.Trim() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(offerId) &&
            (context.GameDetail.StoreId.Equals("ea-app", StringComparison.OrdinalIgnoreCase) ||
             (game?.DeliveryProvider?.Equals("ea-app", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            offerId = game?.StoreTitleId?.Trim() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(offerId))
        {
            var normalizedTitle = NormalizeTitle(game?.Title ?? string.Empty);
            var exact = account.Games
                .Where(item => NormalizeTitle(item.Title).Equals(
                    normalizedTitle,
                    StringComparison.Ordinal))
                .ToArray();
            if (exact.Length == 1)
            {
                offerId = exact[0].OfferId;
                PersistMapping(localGameId, offerId, account);
            }
        }
        if (string.IsNullOrWhiteSpace(offerId))
        {
            return Unavailable(
                "mapping-unavailable",
                "EA did not expose one exact owned-product match for this title. No fuzzy title guess is used.",
                DateTimeOffset.UtcNow.AddDays(7));
        }

        JsonDocument document;
        try
        {
            document = await SendGraphQlAsync(
                token,
                AchievementsQuery,
                new
                {
                    offerId,
                    playerPsd = account.PlayerSubId,
                    locale = NormalizeEaLocale(context.Provider.Locale),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (EaAuthenticationException)
        {
            return Unavailable(
                "authentication-required",
                "EA rejected the saved session. Reconnect the EA provider.",
                DateTimeOffset.UtcNow.AddHours(1));
        }
        using (document)
        {
            var items = ParseAchievements(document.RootElement);
            if (items.Count == 0)
            {
                return new OmniLibraryAchievementRefreshResult(
                    new OmniLibraryAchievementMetadata(
                        "EA",
                        "no-achievements",
                        "EA exposes no achievement set for this owned offer.",
                        0,
                        0,
                        []),
                    true,
                    true,
                    JsonSerializer.Serialize(new { offerId, account.PlayerSubId }),
                    null,
                    string.Empty);
            }

            return new OmniLibraryAchievementRefreshResult(
                new OmniLibraryAchievementMetadata(
                    "EA",
                    "ready",
                    string.IsNullOrWhiteSpace(account.DisplayName)
                        ? "Verified EA achievement progress."
                        : $"Verified EA achievement progress for {account.DisplayName}.",
                    items.Count(item => item.Unlocked),
                    items.Count,
                    items),
                true,
                true,
                JsonSerializer.Serialize(new { offerId, account.PlayerSubId }),
                null,
                string.Empty);
        }
    }

    private async Task<EaAccountSnapshot> GetAccountAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(token);
        if (_account is not null &&
            _assetTokenFingerprint.Equals(fingerprint, StringComparison.Ordinal))
        {
            return _account;
        }

        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_account is not null &&
                _assetTokenFingerprint.Equals(fingerprint, StringComparison.Ordinal))
            {
                return _account;
            }

            using var identity = await SendGraphQlAsync(
                token,
                IdentityQuery,
                variables: null,
                cancellationToken).ConfigureAwait(false);
            var player = identity.RootElement
                .GetProperty("data")
                .GetProperty("me")
                .GetProperty("player");
            var playerSubId = String(player, "psd");
            if (string.IsNullOrWhiteSpace(playerSubId))
            {
                throw new EaAuthenticationException();
            }

            using var library = await SendGraphQlAsync(
                token,
                OwnedGamesQuery,
                new
                {
                    locale = "DEFAULT",
                    entitlementEnabled = false,
                    storefronts = new[] { "EA" },
                    type = new[]
                    {
                        "DIGITAL_FULL_GAME",
                        "PACKAGED_FULL_GAME",
                        "DIGITAL_EXTRA_CONTENT",
                        "PACKAGED_EXTRA_CONTENT",
                    },
                    platforms = new[] { "PC" },
                    limit = 9999,
                },
                cancellationToken).ConfigureAwait(false);
            var games = ParseOwnedGames(library.RootElement);
            _account = new EaAccountSnapshot(
                playerSubId,
                String(player, "displayName"),
                games);
            _assetTokenFingerprint = fingerprint;
            PersistAccount(_account);
            return _account;
        }
        finally
        {
            _accountGate.Release();
        }
    }

    private async Task<JsonDocument> SendGraphQlAsync(
        string token,
        string query,
        object? variables,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        object body = variables is null
            ? new { query }
            : new { query, variables };
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new EaAuthenticationException();
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var messageNode)
                ? messageNode.GetString() ?? string.Empty
                : string.Empty;
            document.Dispose();
            if (message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            {
                throw new EaAuthenticationException();
            }
            throw new HttpRequestException("EA GraphQL rejected the achievement request.");
        }
        return document;
    }

    private void PersistAccount(EaAccountSnapshot account)
    {
        if (settingsStore is null)
        {
            return;
        }
        settingsStore.Update(configuration =>
        {
            var provider = configuration.UnifySteam.GameData.Providers[ProviderId];
            provider.AccountId = account.PlayerSubId;
            provider.AccountName = account.DisplayName;
            provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
        });
    }

    private void PersistMapping(
        string localGameId,
        string offerId,
        EaAccountSnapshot account)
    {
        if (settingsStore is null ||
            string.IsNullOrWhiteSpace(localGameId) ||
            string.IsNullOrWhiteSpace(offerId))
        {
            return;
        }
        settingsStore.Update(configuration =>
        {
            var provider = configuration.UnifySteam.GameData.Providers[ProviderId];
            provider.GameIdOverrides[localGameId.Trim()] = offerId.Trim();
            provider.AccountId = account.PlayerSubId;
            provider.AccountName = account.DisplayName;
            provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
        });
    }

    private static IReadOnlyList<EaOwnedGame> ParseOwnedGames(JsonElement root)
    {
        if (!TryNavigate(root, out var items, "data", "me", "ownedGameProducts", "items") ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new Dictionary<string, EaOwnedGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var offerId = String(item, "originOfferId");
            if (string.IsNullOrWhiteSpace(offerId) ||
                !item.TryGetProperty("product", out var product) ||
                product.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (product.TryGetProperty("baseItem", out var baseItem) &&
                baseItem.ValueKind == JsonValueKind.Object &&
                !String(baseItem, "gameType").Equals(
                    "BASE_GAME",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result[offerId] = new EaOwnedGame(offerId, String(product, "name"));
        }
        return result.Values.ToArray();
    }

    private static IReadOnlyList<OmniLibraryAchievementItemMetadata>
        ParseAchievements(JsonElement root)
    {
        if (!TryNavigate(root, out var sets, "data", "achievements") ||
            sets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new Dictionary<string, OmniLibraryAchievementItemMetadata>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets.EnumerateArray())
        {
            if (!set.TryGetProperty("achievements", out var achievements) ||
                achievements.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var item in achievements.EnumerateArray())
            {
                var id = String(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                var awardCount = Int(item, "awardCount");
                var unlockedAt = awardCount > 0 ? ParseEaDate(item, "date") : null;
                result[id] = new OmniLibraryAchievementItemMetadata(
                    id,
                    FirstNonEmpty(String(item, "name"), id),
                    String(item, "description"),
                    awardCount > 0,
                    false,
                    unlockedAt,
                    string.Empty,
                    awardCount > 0 ? 1 : 0,
                    1);
            }
        }
        return result.Values.ToArray();
    }

    private static bool TryNavigate(
        JsonElement root,
        out JsonElement result,
        params string[] path)
    {
        result = root;
        foreach (var part in path)
        {
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty(part, out result))
            {
                return false;
            }
        }
        return true;
    }

    private static DateTimeOffset? ParseEaDate(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var node))
        {
            return null;
        }
        if (node.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                node.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var unix))
        {
            try
            {
                return unix > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            catch
            {
            }
        }
        return null;
    }

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

    private static string NormalizeTitle(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormD)
            .Where(character =>
                CharUnicodeInfo.GetUnicodeCategory(character) !=
                UnicodeCategory.NonSpacingMark &&
                char.IsLetterOrDigit(character)))
            .ToLowerInvariant();

    private static string NormalizeEaLocale(string? locale) =>
        string.IsNullOrWhiteSpace(locale)
            ? "US"
            : locale.Trim().Replace('-', '_').ToUpperInvariant();

    private static string Fingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
        string.Empty;

    private static OmniLibraryAchievementRefreshResult Unavailable(
        string status,
        string detail,
        DateTimeOffset retryAfterUtc) =>
        new(
            new OmniLibraryAchievementMetadata("EA", status, detail, 0, 0, []),
            true,
            true,
            string.Empty,
            retryAfterUtc,
            string.Empty);

    private sealed class EaAuthenticationException : Exception;

    private sealed record EaOwnedGame(string OfferId, string Title);

    private sealed record EaAccountSnapshot(
        string PlayerSubId,
        string DisplayName,
        IReadOnlyList<EaOwnedGame> Games);

    private const string IdentityQuery =
        "query { me { player { pd psd displayName } } }";

    private const string OwnedGamesQuery = """
        query GetOwnedGameProducts(
          $locale: Locale!,
          $entitlementEnabled: Boolean!,
          $storefronts: [UserGameProductStorefront!]!,
          $type: [GameProductType!]!,
          $platforms: [GamePlatform!]!,
          $limit: Int!
        ) {
          me {
            ownedGameProducts(
              locale: $locale
              entitlementEnabled: $entitlementEnabled
              storefronts: $storefronts
              type: $type
              platforms: $platforms
              paging: { limit: $limit }
            ) {
              items {
                originOfferId
                product { name baseItem { gameType } }
              }
            }
          }
        }
        """;

    private const string AchievementsQuery = """
        query GetAchievements($offerId: String!, $playerPsd: String!, $locale: Locale!) {
          achievements(
            offerId: $offerId
            playerPsd: $playerPsd
            showHidden: true
            locale: $locale
          ) {
            id
            achievements { id name description awardCount date }
          }
        }
        """;
}
