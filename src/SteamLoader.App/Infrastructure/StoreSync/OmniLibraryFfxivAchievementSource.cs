using System.Globalization;
using System.Net;
using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// FFXIV Collect-backed, character-scoped achievement source. The large public
/// definition catalog is shared for the process lifetime and refreshed at most
/// once per day; character progress remains title-scoped and is fetched only
/// when the game page cache requests it.
/// </summary>
internal sealed class OmniLibraryFfxivAchievementSource(HttpClient httpClient)
    : IOmniLibraryAchievementSource
{
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private IReadOnlyList<FfxivDefinition> _catalog = [];
    private DateTimeOffset _catalogRefreshedAtUtc = DateTimeOffset.MinValue;

    public string ProviderId => "ffxiv";

    public async Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken)
    {
        var characterId = context.Provider.AccountId?.Trim() ?? string.Empty;
        if (!long.TryParse(
                characterId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedCharacterId) ||
            parsedCharacterId <= 0)
        {
            return Unavailable(
                "setup-required",
                "Add the numeric Lodestone character ID from the character profile URL.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (catalog.Count == 0)
        {
            return Unavailable(
                "temporarily-unavailable",
                "FFXIV Collect returned no achievement definitions. The last good cache is kept.",
                DateTimeOffset.UtcNow.AddMinutes(30));
        }

        using var characterResponse = await httpClient.GetAsync(
            $"https://ffxivcollect.com/api/characters/{parsedCharacterId.ToString(CultureInfo.InvariantCulture)}?times=true",
            cancellationToken).ConfigureAwait(false);
        if (characterResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return Unavailable(
                "mapping-unavailable",
                "FFXIV Collect could not find this Lodestone character ID.",
                DateTimeOffset.UtcNow.AddDays(1));
        }
        characterResponse.EnsureSuccessStatusCode();
        await using var characterStream = await characterResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var characterDocument = await JsonDocument.ParseAsync(
            characterStream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = characterDocument.RootElement;
        var characterName = String(root, "name");
        if (!root.TryGetProperty("achievements", out var achievements) ||
            achievements.ValueKind != JsonValueKind.Object)
        {
            return Unavailable(
                "temporarily-unavailable",
                "FFXIV Collect returned no character achievement data.",
                DateTimeOffset.UtcNow.AddMinutes(30));
        }
        if (achievements.TryGetProperty("public", out var publicNode) &&
            publicNode.ValueKind is JsonValueKind.False)
        {
            return Unavailable(
                "profile-private",
                "This character hides achievements on the Lodestone. Make them public to sync progress.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        var unlocked = new Dictionary<int, DateTimeOffset?>();
        if (achievements.TryGetProperty("obtained", out var obtained) &&
            obtained.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in obtained.EnumerateArray())
            {
                if (!TryInt(item, "id", out var id) || id <= 0)
                {
                    continue;
                }
                unlocked[id] = ParseDate(String(item, "time"));
            }
        }

        var items = catalog.Select(definition =>
        {
            var isUnlocked = unlocked.TryGetValue(definition.Id, out var unlockedAt);
            return new OmniLibraryAchievementItemMetadata(
                definition.Id.ToString(CultureInfo.InvariantCulture),
                definition.Name,
                definition.Description,
                isUnlocked,
                false,
                unlockedAt,
                definition.IconUrl,
                isUnlocked ? 1 : 0,
                1);
        }).ToArray();

        return new OmniLibraryAchievementRefreshResult(
            new OmniLibraryAchievementMetadata(
                "Final Fantasy XIV",
                "ready",
                string.IsNullOrWhiteSpace(characterName)
                    ? "Verified FFXIV character achievement progress."
                    : $"Verified FFXIV character achievement progress for {characterName}.",
                unlocked.Count,
                items.Length,
                items),
            DefinitionsRefreshed: true,
            ProgressRefreshed: true,
            ProviderState: JsonSerializer.Serialize(new
            {
                characterId = parsedCharacterId,
                catalogRefreshedAtUtc = _catalogRefreshedAtUtc,
            }),
            RetryAfterUtc: null,
            Error: string.Empty);
    }

    private async Task<IReadOnlyList<FfxivDefinition>> GetCatalogAsync(
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_catalog.Count > 0 && now - _catalogRefreshedAtUtc < CatalogLifetime)
        {
            return _catalog;
        }

        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_catalog.Count > 0 && now - _catalogRefreshedAtUtc < CatalogLifetime)
            {
                return _catalog;
            }

            using var response = await httpClient.GetAsync(
                "https://ffxivcollect.com/api/achievements",
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return _catalog;
            }

            var next = new Dictionary<int, FfxivDefinition>();
            foreach (var item in results.EnumerateArray())
            {
                if (!TryInt(item, "id", out var id) || id <= 0)
                {
                    continue;
                }
                var name = String(item, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                next[id] = new FfxivDefinition(
                    id,
                    name,
                    String(item, "description"),
                    NormalizeIconUrl(String(item, "icon")));
            }
            if (next.Count > 0)
            {
                _catalog = next.Values.OrderBy(item => item.Id).ToArray();
                _catalogRefreshedAtUtc = now;
            }
            return _catalog;
        }
        catch when (_catalog.Count > 0)
        {
            return _catalog;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private static bool TryInt(JsonElement owner, string name, out int value)
    {
        value = 0;
        return owner.TryGetProperty(name, out var node) &&
               (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out value) ||
                node.ValueKind == JsonValueKind.String &&
                int.TryParse(node.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value));
    }

    private static string String(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string NormalizeIconUrl(string value) =>
        value.Replace("format=webp", "format=png", StringComparison.OrdinalIgnoreCase);

    private static OmniLibraryAchievementRefreshResult Unavailable(
        string status,
        string detail,
        DateTimeOffset retryAfterUtc) =>
        new(
            new OmniLibraryAchievementMetadata(
                "Final Fantasy XIV",
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

    private sealed record FfxivDefinition(
        int Id,
        string Name,
        string Description,
        string IconUrl);
}
