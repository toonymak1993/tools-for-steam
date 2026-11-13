using System.Globalization;
using System.Text.Json;
using SteamLoader.App.Infrastructure.StoreSync;

namespace SteamLoader.App.Infrastructure.Artwork;

/// <summary>
/// Backend of the TFS Artworks page: which SteamGridDB game and which Steam
/// store app belong to a library entry, plus the official Steam artwork.
/// Manual SteamGridDB matches are remembered in data\artwork-matches.json.
/// </summary>
/// <remarks>Inspired by Playhub Artworks (MIT, LoZazaMastro); independent code.</remarks>
public sealed class ArtworkPickerService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SteamGridDbManualArtworkService _steamGridDb;
    private readonly SteamOfficialArtworkService _official;
    private readonly HttpClient _httpClient;
    private readonly string _matchesPath;
    private readonly object _gate = new();

    public ArtworkPickerService(
        SteamGridDbManualArtworkService steamGridDb,
        SteamOfficialArtworkService official,
        HttpClient httpClient,
        string matchesPath)
    {
        _steamGridDb = steamGridDb;
        _official = official;
        _httpClient = httpClient;
        _matchesPath = matchesPath;
    }

    public SteamGridDbManualArtworkService SteamGridDb => _steamGridDb;

    public async Task<ArtworkPickerGame> GetGameAsync(long appId, string? title, bool isShortcut, CancellationToken cancellationToken)
    {
        var cleanTitle = (title ?? string.Empty).Trim();
        uint steamAppId = 0;
        if (!isShortcut && appId is > 0 and <= uint.MaxValue)
        {
            steamAppId = (uint)appId;
        }
        else if (cleanTitle.Length >= 3)
        {
            try
            {
                var store = await OmniLibraryArtworkDiscovery.FindExactSteamStoreAppAsync(_httpClient, cleanTitle, cancellationToken);
                if (store.State == ArtworkAttemptState.Success && store.Value > 0)
                {
                    steamAppId = (uint)store.Value;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        var gameTask = ResolveSteamGridDbGameAsync(appId, cleanTitle, steamAppId, cancellationToken);
        var officialTask = _official.GetAsync(steamAppId, cancellationToken);
        var (game, match) = await gameTask;
        var official = await officialTask;
        return new ArtworkPickerGame(
            appId,
            cleanTitle,
            isShortcut,
            steamAppId,
            game?.Id ?? 0,
            game?.Name,
            game?.Verified ?? false,
            match,
            _steamGridDb.IsApiKeyConfigured(),
            official);
    }

    /// <summary>Remembers (or with 0 forgets) the SteamGridDB game chosen on the page.</summary>
    public void SaveMatch(long appId, int steamGridDbGameId, string? name)
    {
        lock (_gate)
        {
            var matches = LoadMatches();
            var key = appId.ToString(CultureInfo.InvariantCulture);
            if (steamGridDbGameId > 0)
            {
                matches[key] = new ArtworkMatch(steamGridDbGameId, (name ?? string.Empty).Trim());
            }
            else
            {
                matches.Remove(key);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_matchesPath)!);
            var temporary = $"{_matchesPath}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(matches, JsonOptions));
            File.Move(temporary, _matchesPath, overwrite: true);
        }
    }

    private async Task<(Models.ArtworkGameSearchResult? Game, string Match)> ResolveSteamGridDbGameAsync(
        long appId,
        string title,
        uint steamAppId,
        CancellationToken cancellationToken)
    {
        ArtworkMatch? remembered;
        lock (_gate)
        {
            remembered = LoadMatches().GetValueOrDefault(appId.ToString(CultureInfo.InvariantCulture));
        }

        if (remembered is { GameId: > 0 })
        {
            return (new Models.ArtworkGameSearchResult(remembered.GameId, remembered.Name, false), "manual");
        }

        if (steamAppId > 0)
        {
            var exact = await _steamGridDb.FindGameBySteamAppIdAsync(steamAppId, cancellationToken);
            if (exact is not null)
            {
                return (exact, "steam");
            }
        }

        if (title.Length < 2)
        {
            return (null, "none");
        }

        try
        {
            var results = await _steamGridDb.SearchGamesAsync(title, cancellationToken);
            var wanted = OmniLibraryArtworkRules.CanonicalArtworkTitle(title);
            var best = results.FirstOrDefault(result => OmniLibraryArtworkRules.CanonicalArtworkTitle(result.Name) == wanted)
                ?? results.FirstOrDefault();
            return best is null ? (null, "none") : (best, "search");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return (null, "none");
        }
    }

    private Dictionary<string, ArtworkMatch> LoadMatches()
    {
        try
        {
            return File.Exists(_matchesPath)
                ? JsonSerializer.Deserialize<Dictionary<string, ArtworkMatch>>(File.ReadAllText(_matchesPath), JsonOptions)
                  ?? new Dictionary<string, ArtworkMatch>(StringComparer.Ordinal)
                : new Dictionary<string, ArtworkMatch>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, ArtworkMatch>(StringComparer.Ordinal);
        }
    }

    private sealed record ArtworkMatch(int GameId, string Name);
}
