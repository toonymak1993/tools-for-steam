using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Battle.net adapter for the two achievement-bearing PC titles exposed by
/// Blizzard's public APIs: World of Warcraft and StarCraft II. Client tokens
/// and static definition catalogs are single-flight cached; account progress
/// remains title-scoped.
/// </summary>
internal sealed class OmniLibraryBattleNetAchievementSource(HttpClient httpClient)
    : IOmniLibraryAchievementSource
{
    private static readonly TimeSpan TokenSafetyMargin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefinitionLifetime = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly SemaphoreSlim _definitionGate = new(1, 1);
    private string _tokenFingerprint = string.Empty;
    private string _accessToken = string.Empty;
    private DateTimeOffset _tokenExpiresAtUtc = DateTimeOffset.MinValue;
    private string _definitionKey = string.Empty;
    private DateTimeOffset _definitionsRefreshedAtUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<BattleNetDefinition> _definitions = [];

    public string ProviderId => "battle-net";

    public async Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken)
    {
        var clientId = context.Provider.Credential?.Trim() ?? string.Empty;
        var clientSecret = context.Provider.SecondaryCredential?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return Unavailable(
                "setup-required",
                "Add a Battle.net API client ID and client secret.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        var gameKind = ResolveGameKind(context);
        if (gameKind == BattleNetGameKind.None)
        {
            return Unavailable(
                "mapping-unavailable",
                "Battle.net achievements are available for World of Warcraft and StarCraft II. Select one exact provider mapping.",
                DateTimeOffset.UtcNow.AddDays(30));
        }

        var region = NormalizeRegion(context.Provider.Region);
        var locale = NormalizeLocale(context.Provider.Locale);
        string token;
        try
        {
            token = await GetClientTokenAsync(
                region,
                clientId,
                clientSecret,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BattleNetAuthenticationException)
        {
            return Unavailable(
                "authentication-required",
                "Battle.net rejected the API client credentials.",
                DateTimeOffset.UtcNow.AddHours(1));
        }

        return gameKind == BattleNetGameKind.Wow
            ? await RefreshWowAsync(
                context.Provider.AccountName,
                region,
                locale,
                token,
                cancellationToken).ConfigureAwait(false)
            : await RefreshSc2Async(
                context.Provider.AccountName,
                locale,
                clientId,
                clientSecret,
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<OmniLibraryAchievementRefreshResult> RefreshWowAsync(
        string? accountName,
        string region,
        string locale,
        string token,
        CancellationToken cancellationToken)
    {
        var identity = (accountName ?? string.Empty).Split('@', 2);
        if (identity.Length != 2 ||
            string.IsNullOrWhiteSpace(identity[0]) ||
            string.IsNullOrWhiteSpace(identity[1]))
        {
            return Unavailable(
                "setup-required",
                "Enter the WoW identity as Character@realm-slug.",
                DateTimeOffset.UtcNow.AddDays(1));
        }
        var character = Slug(identity[0]);
        var realm = Slug(identity[1]);
        var definitions = await GetWowDefinitionsAsync(
            region,
            locale,
            token,
            cancellationToken).ConfigureAwait(false);
        using var progress = await GetAuthorizedAsync(
            $"https://{region}.api.blizzard.com/profile/wow/character/{Uri.EscapeDataString(realm)}/{Uri.EscapeDataString(character)}/achievements?namespace=profile-{region}&locale={Uri.EscapeDataString(locale)}",
            token,
            cancellationToken).ConfigureAwait(false);
        var completed = new Dictionary<int, DateTimeOffset?>();
        if (progress.RootElement.TryGetProperty("achievements", out var achievements) &&
            achievements.ValueKind == JsonValueKind.Array)
        {
            foreach (var achievement in achievements.EnumerateArray())
            {
                var id = Int(achievement, "id");
                if (id <= 0 &&
                    achievement.TryGetProperty("achievement", out var reference))
                {
                    id = Int(reference, "id");
                }
                if (id <= 0)
                {
                    continue;
                }
                completed[id] = achievement.TryGetProperty(
                        "completed_timestamp",
                        out var timestamp) &&
                    timestamp.TryGetInt64(out var unixMilliseconds)
                        ? FromUnixMilliseconds(unixMilliseconds)
                        : null;
            }
        }
        var items = definitions.Select(definition =>
        {
            var unlocked = completed.TryGetValue(definition.Id, out var unlockedAt);
            return new OmniLibraryAchievementItemMetadata(
                definition.Id.ToString(CultureInfo.InvariantCulture),
                definition.Name,
                definition.Description,
                unlocked,
                definition.Hidden,
                unlockedAt,
                definition.IconUrl,
                unlocked ? 1 : 0,
                1);
        }).ToArray();
        return Result(
            "Battle.net / World of Warcraft",
            $"Verified World of Warcraft achievement progress for {identity[0]} on {identity[1]}.",
            items,
            new { kind = "wow", character, realm, region });
    }

    private async Task<OmniLibraryAchievementRefreshResult> RefreshSc2Async(
        string? accountName,
        string locale,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var parts = (accountName ?? string.Empty).Split('/', 3);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var regionId) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var realmId) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var profileId) ||
            regionId <= 0 || realmId <= 0 || profileId <= 0)
        {
            return Unavailable(
                "setup-required",
                "Enter the StarCraft II profile as regionId/realmId/profileId.",
                DateTimeOffset.UtcNow.AddDays(1));
        }
        var apiRegion = regionId switch
        {
            2 => "eu",
            3 => "kr",
            5 => "cn",
            _ => "us",
        };
        var token = await GetClientTokenAsync(
            apiRegion,
            clientId,
            clientSecret,
            cancellationToken).ConfigureAwait(false);
        var definitions = await GetSc2DefinitionsAsync(
            apiRegion,
            regionId,
            locale,
            token,
            cancellationToken).ConfigureAwait(false);
        using var profile = await GetAuthorizedAsync(
            $"https://{apiRegion}.api.blizzard.com/sc2/legacy/profile/{regionId.ToString(CultureInfo.InvariantCulture)}/{realmId.ToString(CultureInfo.InvariantCulture)}/{profileId.ToString(CultureInfo.InvariantCulture)}?locale={Uri.EscapeDataString(locale)}",
            token,
            cancellationToken).ConfigureAwait(false);
        var earned = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        if (profile.RootElement.TryGetProperty("earnedAchievements", out var earnedItems) &&
            earnedItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in earnedItems.EnumerateArray())
            {
                var id = FirstNonEmpty(String(item, "achievementId"), String(item, "id"));
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                earned[id] = ParseDate(String(item, "completionDate"));
            }
        }
        var items = definitions.Select(definition =>
        {
            var unlocked = earned.TryGetValue(definition.StringId, out var unlockedAt);
            return new OmniLibraryAchievementItemMetadata(
                definition.StringId,
                definition.Name,
                definition.Description,
                unlocked,
                false,
                unlockedAt,
                definition.IconUrl,
                unlocked ? 1 : 0,
                1);
        }).ToArray();
        var displayName = profile.RootElement.TryGetProperty("summary", out var summary)
            ? String(summary, "displayName")
            : string.Empty;
        return Result(
            "Battle.net / StarCraft II",
            string.IsNullOrWhiteSpace(displayName)
                ? "Verified StarCraft II achievement progress."
                : $"Verified StarCraft II achievement progress for {displayName}.",
            items,
            new { kind = "sc2", regionId, realmId, profileId });
    }

    private async Task<IReadOnlyList<BattleNetDefinition>> GetWowDefinitionsAsync(
        string region,
        string locale,
        string token,
        CancellationToken cancellationToken)
    {
        var key = $"wow:{region}:{locale}";
        var now = DateTimeOffset.UtcNow;
        if (_definitions.Count > 0 &&
            _definitionKey.Equals(key, StringComparison.Ordinal) &&
            now - _definitionsRefreshedAtUtc < DefinitionLifetime)
        {
            return _definitions;
        }
        await _definitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_definitions.Count > 0 &&
                _definitionKey.Equals(key, StringComparison.Ordinal) &&
                now - _definitionsRefreshedAtUtc < DefinitionLifetime)
            {
                return _definitions;
            }
            using var document = await GetAuthorizedAsync(
                $"https://{region}.api.blizzard.com/data/wow/achievement/index?namespace=static-{region}&locale={Uri.EscapeDataString(locale)}",
                token,
                cancellationToken).ConfigureAwait(false);
            var next = new List<BattleNetDefinition>();
            if (document.RootElement.TryGetProperty("achievements", out var achievements) &&
                achievements.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in achievements.EnumerateArray())
                {
                    var id = Int(item, "id");
                    var name = String(item, "name");
                    if (id <= 0 || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    next.Add(new BattleNetDefinition(
                        id,
                        id.ToString(CultureInfo.InvariantCulture),
                        name,
                        String(item, "description"),
                        string.Empty,
                        Bool(item, "is_hidden")));
                }
            }
            if (next.Count > 0)
            {
                _definitions = next;
                _definitionKey = key;
                _definitionsRefreshedAtUtc = now;
            }
            return _definitions;
        }
        catch when (_definitions.Count > 0 &&
                    _definitionKey.Equals(key, StringComparison.Ordinal))
        {
            return _definitions;
        }
        finally
        {
            _definitionGate.Release();
        }
    }

    private async Task<IReadOnlyList<BattleNetDefinition>> GetSc2DefinitionsAsync(
        string apiRegion,
        int regionId,
        string locale,
        string token,
        CancellationToken cancellationToken)
    {
        var key = $"sc2:{apiRegion}:{regionId}:{locale}";
        var now = DateTimeOffset.UtcNow;
        if (_definitions.Count > 0 &&
            _definitionKey.Equals(key, StringComparison.Ordinal) &&
            now - _definitionsRefreshedAtUtc < DefinitionLifetime)
        {
            return _definitions;
        }
        await _definitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_definitions.Count > 0 &&
                _definitionKey.Equals(key, StringComparison.Ordinal) &&
                now - _definitionsRefreshedAtUtc < DefinitionLifetime)
            {
                return _definitions;
            }
            using var document = await GetAuthorizedAsync(
                $"https://{apiRegion}.api.blizzard.com/sc2/legacy/data/achievements/{regionId.ToString(CultureInfo.InvariantCulture)}?locale={Uri.EscapeDataString(locale)}",
                token,
                cancellationToken).ConfigureAwait(false);
            var next = ParseSc2Definitions(document.RootElement);
            if (next.Count > 0)
            {
                _definitions = next;
                _definitionKey = key;
                _definitionsRefreshedAtUtc = now;
            }
            return _definitions;
        }
        catch when (_definitions.Count > 0 &&
                    _definitionKey.Equals(key, StringComparison.Ordinal))
        {
            return _definitions;
        }
        finally
        {
            _definitionGate.Release();
        }
    }

    private async Task<string> GetClientTokenAsync(
        string region,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var fingerprint = $"{region}:{clientId}:{clientSecret}";
        if (_tokenFingerprint.Equals(fingerprint, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_accessToken) &&
            DateTimeOffset.UtcNow < _tokenExpiresAtUtc)
        {
            return _accessToken;
        }
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenFingerprint.Equals(fingerprint, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_accessToken) &&
                DateTimeOffset.UtcNow < _tokenExpiresAtUtc)
            {
                return _accessToken;
            }
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://{region}.battle.net/oauth/token");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
            request.Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or
                HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden)
            {
                throw new BattleNetAuthenticationException();
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _accessToken = String(document.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(_accessToken))
            {
                throw new BattleNetAuthenticationException();
            }
            var expiresIn = Math.Max(300, Int(document.RootElement, "expires_in"));
            _tokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
                .Subtract(TokenSafetyMargin);
            _tokenFingerprint = fingerprint;
            return _accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<JsonDocument> GetAuthorizedAsync(
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new BattleNetAuthenticationException();
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<BattleNetDefinition> ParseSc2Definitions(JsonElement root)
    {
        if (!root.TryGetProperty("achievements", out var achievements) ||
            achievements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<BattleNetDefinition>();
        foreach (var item in achievements.EnumerateArray())
        {
            var stringId = FirstNonEmpty(String(item, "id"), String(item, "achievementId"));
            var name = String(item, "title");
            if (string.IsNullOrWhiteSpace(stringId) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            _ = int.TryParse(stringId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
            result.Add(new BattleNetDefinition(
                id,
                stringId,
                name,
                String(item, "description"),
                String(item, "imageUrl"),
                false));
        }
        return result;
    }

    private static BattleNetGameKind ResolveGameKind(
        OmniLibraryAchievementSourceContext context)
    {
        var game = context.GameDetail.Game;
        var localId = game?.Id?.Trim() ?? string.Empty;
        var mapped = !string.IsNullOrWhiteSpace(localId) &&
                     context.Provider.GameIdOverrides.TryGetValue(localId, out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;
        var candidate = FirstNonEmpty(mapped, game?.Title ?? string.Empty);
        var normalized = new string(candidate
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (normalized is "wow" || normalized.Contains("worldofwarcraft"))
        {
            return BattleNetGameKind.Wow;
        }
        if (normalized is "sc2" ||
            normalized.Contains("starcraftii") ||
            normalized.Contains("starcraft2"))
        {
            return BattleNetGameKind.Sc2;
        }
        return BattleNetGameKind.None;
    }

    private static OmniLibraryAchievementRefreshResult Result(
        string provider,
        string detail,
        IReadOnlyList<OmniLibraryAchievementItemMetadata> items,
        object state) =>
        new(
            new OmniLibraryAchievementMetadata(
                provider,
                items.Count > 0 ? "ready" : "no-achievements",
                items.Count > 0 ? detail : "Battle.net exposes no achievements for this title.",
                items.Count(item => item.Unlocked),
                items.Count,
                items),
            true,
            true,
            JsonSerializer.Serialize(state),
            null,
            string.Empty);

    private static OmniLibraryAchievementRefreshResult Unavailable(
        string status,
        string detail,
        DateTimeOffset retryAfterUtc) =>
        new(
            new OmniLibraryAchievementMetadata("Battle.net", status, detail, 0, 0, []),
            true,
            true,
            string.Empty,
            retryAfterUtc,
            string.Empty);

    private static string NormalizeRegion(string? region) =>
        region?.Trim().ToLowerInvariant() switch
        {
            "eu" => "eu",
            "kr" => "kr",
            "tw" => "tw",
            "cn" => "cn",
            _ => "us",
        };

    private static string NormalizeLocale(string? locale) =>
        string.IsNullOrWhiteSpace(locale)
            ? "en_US"
            : locale.Trim().Replace('-', '_');

    private static string Slug(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '-');

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static DateTimeOffset? FromUnixMilliseconds(long value)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch
        {
            return null;
        }
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

    private static bool Bool(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.True;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
        string.Empty;

    private enum BattleNetGameKind
    {
        None,
        Wow,
        Sc2,
    }

    private sealed class BattleNetAuthenticationException : Exception;

    private sealed record BattleNetDefinition(
        int Id,
        string StringId,
        string Name,
        string Description,
        string IconUrl,
        bool Hidden);
}
