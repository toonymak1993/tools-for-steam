using System.Globalization;
using System.Net;
using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed class OmniLibraryRetroAchievementsSource : IOmniLibraryAchievementSource
{
    private const string MediaBaseUrl = "https://media.retroachievements.org";
    private const string HashMappingPrefix = "rahash:v1:";
    private readonly HttpClient _httpClient;
    private readonly Action<string, string, string>? _persistProviderGameId;
    private readonly Func<string, string, CancellationToken, Task<string>> _hashRom;

    public OmniLibraryRetroAchievementsSource(
        HttpClient httpClient,
        Action<string, string, string>? persistProviderGameId = null,
        Func<string, string, CancellationToken, Task<string>>? hashRom = null)
    {
        _httpClient = httpClient;
        _persistProviderGameId = persistProviderGameId;
        _hashRom = hashRom ?? ManagedRetroAchievementsHasher.HashAsync;
    }

    public string ProviderId => "retroachievements";

    public async Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken)
    {
        var username = context.Provider.AccountName?.Trim() ?? string.Empty;
        var apiKey = context.Provider.Credential?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Unavailable(
                "setup-required",
                "Add a RetroAchievements username and personal web API key.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        var game = context.GameDetail.Game;
        var localId = game?.Id?.Trim() ?? string.Empty;
        var mappedId = !string.IsNullOrWhiteSpace(localId) &&
                       context.Provider.GameIdOverrides.TryGetValue(localId, out var mapped)
            ? mapped?.Trim() ?? string.Empty
            : string.Empty;
        var gameId = mappedId;
        var contentHash = string.Empty;
        if (TryResolveCachedHashMapping(game, mappedId, out var cachedHash, out var cachedGameId))
        {
            contentHash = cachedHash;
            gameId = cachedGameId;
            if (gameId == "0")
            {
                return UnsupportedRom(contentHash);
            }
        }
        else if (!uint.TryParse(
                     gameId,
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out _) &&
                 context.GameDetail.StoreId.Equals(
                     OmniLibraryRomSystemRegistry.StoreId,
                     StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(game?.RomPath))
        {
            contentHash = await _hashRom(
                game.PlatformId,
                game.RomPath,
                cancellationToken).ConfigureAwait(false);
            gameId = await ResolveGameIdAsync(contentHash, cancellationToken)
                .ConfigureAwait(false);
            var persisted = BuildHashMapping(game, contentHash, gameId);
            _persistProviderGameId?.Invoke(ProviderId, localId, persisted);
            if (gameId == "0")
            {
                return UnsupportedRom(contentHash);
            }
        }
        else if (string.IsNullOrWhiteSpace(gameId))
        {
            gameId = context.GameDetail.StoreId.Equals(
                "retroachievements",
                StringComparison.OrdinalIgnoreCase)
                ? localId
                : game?.StoreTitleId?.Trim() ?? string.Empty;
        }
        if (!uint.TryParse(
                gameId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
        {
            return Unavailable(
                "mapping-required",
                "This game needs an exact RetroAchievements game ID. Title-name guessing is intentionally disabled.",
                DateTimeOffset.UtcNow.AddDays(30));
        }

        var url =
            $"https://retroachievements.org/API/API_GetGameInfoAndUserProgress.php?g={Uri.EscapeDataString(gameId)}&u={Uri.EscapeDataString(username)}&y={Uri.EscapeDataString(apiKey)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Unavailable(
                "authentication-required",
                "RetroAchievements rejected the saved Web API key. Copy the Web API key from RetroAchievements Settings > Keys; do not enter the account password.",
                DateTimeOffset.UtcNow.AddHours(1));
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return NoAchievements(gameId);
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var achievements = ParseAchievements(document.RootElement);
        if (achievements.Count == 0)
        {
            return NoAchievements(gameId);
        }

        var unlocked = achievements.Count(item => item.Unlocked);
        return new OmniLibraryAchievementRefreshResult(
            new OmniLibraryAchievementMetadata(
                "RetroAchievements",
                "ready",
                $"Verified RetroAchievements progress for {username}.",
                unlocked,
                achievements.Count,
                achievements),
            true,
            true,
            JsonSerializer.Serialize(new { gameId, contentHash, username }),
            null,
            string.Empty);
    }

    private async Task<string> ResolveGameIdAsync(
        string contentHash,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://retroachievements.org/dorequest.php")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["r"] = "gameid",
                ["m"] = contentHash,
            }),
        };
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "ToolsForSteam-OmniLibrary/0.4.1");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("Success", out var success) ||
            success.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException(
                "RetroAchievements could not resolve the ROM hash.");
        }

        return document.RootElement.TryGetProperty("GameID", out var gameId) &&
               gameId.TryGetUInt32(out var numericGameId)
            ? numericGameId.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    internal static bool TryResolveCachedHashMapping(
        UnifySteamGameState? game,
        string value,
        out string contentHash,
        out string gameId)
    {
        return TryResolveCachedHashMapping(
            game?.RomPath,
            value,
            out contentHash,
            out gameId);
    }

    internal static bool TryResolveCachedHashMapping(
        string? romPath,
        string value,
        out string contentHash,
        out string gameId)
    {
        contentHash = string.Empty;
        gameId = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(HashMappingPrefix, StringComparison.OrdinalIgnoreCase) ||
            !TryGetRomFingerprint(romPath, out var fingerprint))
        {
            return false;
        }

        var parts = value[HashMappingPrefix.Length..].Split(':');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], fingerprint.LengthHex, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[1], fingerprint.WriteTicksHex, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                parts[2],
                "^[0-9a-f]{32}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !uint.TryParse(
                parts[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
        {
            return false;
        }

        contentHash = parts[2].ToLowerInvariant();
        gameId = parts[3];
        return true;
    }

    private static string BuildHashMapping(
        UnifySteamGameState? game,
        string contentHash,
        string gameId)
    {
        if (game is null || !TryGetRomFingerprint(game.RomPath, out var fingerprint))
        {
            return gameId;
        }

        return $"{HashMappingPrefix}{fingerprint.LengthHex}:" +
               $"{fingerprint.WriteTicksHex}:{contentHash}:{gameId}";
    }

    private static bool TryGetRomFingerprint(
        string? romPath,
        out (string LengthHex, string WriteTicksHex) fingerprint)
    {
        fingerprint = default;
        try
        {
            if (string.IsNullOrWhiteSpace(romPath))
            {
                return false;
            }
            var file = new FileInfo(romPath);
            if (!file.Exists || file.Length <= 0)
            {
                return false;
            }
            fingerprint = ($"{file.Length:x16}", $"{file.LastWriteTimeUtc.Ticks:x16}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<OmniLibraryAchievementItemMetadata>
        ParseAchievements(JsonElement root)
    {
        if (!root.TryGetProperty("Achievements", out var achievements) ||
            achievements.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        var result = new List<OmniLibraryAchievementItemMetadata>();
        foreach (var property in achievements.EnumerateObject())
        {
            var node = property.Value;
            var id = FirstString(node, "ID");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = property.Name;
            }
            var earnedHardcore = FirstString(node, "DateEarnedHardcore");
            var earned = FirstString(node, "DateEarned");
            var unlockedAt = ParseDate(earnedHardcore) ?? ParseDate(earned);
            var badgeName = FirstString(node, "BadgeName");
            var unlocked = unlockedAt.HasValue;
            var badgeSuffix = unlocked ? ".png" : "_lock.png";
            var icon = string.IsNullOrWhiteSpace(badgeName)
                ? string.Empty
                : $"{MediaBaseUrl}/Badge/{Uri.EscapeDataString(badgeName)}{badgeSuffix}";
            result.Add(new OmniLibraryAchievementItemMetadata(
                id,
                FirstNonEmpty(FirstString(node, "Title"), id),
                FirstString(node, "Description"),
                unlocked,
                false,
                unlockedAt,
                icon,
                unlocked ? 1 : 0,
                1));
        }
        return result;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string FirstString(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (node.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        return string.Empty;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
        string.Empty;

    private static OmniLibraryAchievementRefreshResult NoAchievements(string gameId) =>
        new(
            new OmniLibraryAchievementMetadata(
                "RetroAchievements",
                "no-achievements",
                "RetroAchievements exposes no achievements for this game ID.",
                0,
                0,
                []),
            true,
            true,
            JsonSerializer.Serialize(new { gameId }),
            null,
            string.Empty);

    private static OmniLibraryAchievementRefreshResult UnsupportedRom(string contentHash) =>
        new(
            new OmniLibraryAchievementMetadata(
                "RetroAchievements",
                "unsupported-rom",
                "RetroAchievements does not recognize this exact ROM revision. The emulator can still launch it, but achievements require a supported hash.",
                0,
                0,
                []),
            true,
            true,
            JsonSerializer.Serialize(new { contentHash, gameId = 0 }),
            DateTimeOffset.UtcNow.AddDays(30),
            string.Empty);

    private static OmniLibraryAchievementRefreshResult Unavailable(
        string status,
        string detail,
        DateTimeOffset? retryAfterUtc) =>
        new(
            new OmniLibraryAchievementMetadata(
                "RetroAchievements",
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
}
