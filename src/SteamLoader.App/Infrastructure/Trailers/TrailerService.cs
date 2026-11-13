using System.Collections.Concurrent;
using System.Globalization;

namespace SteamLoader.App.Infrastructure.Trailers;

/// <summary>
/// TFS Trailers backend: settings, Steam trailer lists and Steam store matches
/// for non-Steam games. Playback happens in Steam's own Big Picture window
/// (trailer-core.js streams Steam's DASH videos directly from Steam's CDN);
/// this service only decides which video belongs to which game.
/// </summary>
/// <remarks>Inspired by TrailerHero for Playhub (MIT, LoZazaMastro); independent code.</remarks>
public sealed class TrailerService
{
    private static readonly TimeSpan MovieCacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailedLookupLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NoMatchRetryAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _httpClient;
    private readonly TrailerSettingsStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<uint, CachedMovies> _movieCache = new();
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<TrailerStoreSearchResult>>> _searches = new(StringComparer.Ordinal);

    public TrailerService(HttpClient httpClient, TrailerSettingsStore store, Func<DateTimeOffset>? clock = null)
    {
        _httpClient = httpClient;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TrailerGlobalSnapshot GetSettings() => ToGlobal(_store.Load());

    public TrailerGlobalSnapshot SaveSettings(TrailerGlobalUpdate update)
    {
        var configuration = _store.Update(configuration =>
        {
            if (update.Enabled is { } enabled) configuration.Enabled = enabled;
            if (update.DelaySeconds is { } delay) configuration.DelaySeconds = delay;
            if (update.Quality is { } quality) configuration.Quality = quality;
            if (update.MoveLogo is { } moveLogo) configuration.MoveLogo = moveLogo;
            if (update.StartWithSound is { } sound) configuration.StartWithSound = sound;
            if (update.MatchShortcuts is { } match) configuration.MatchShortcuts = match;
        });
        return ToGlobal(configuration);
    }

    /// <summary>
    /// Everything the game page needs: whether to play, which Steam app the
    /// videos come from, the video list and the per-game overrides.
    /// </summary>
    public async Task<TrailerGameSnapshot> GetGameAsync(
        uint appId,
        string? title,
        bool isShortcut,
        CancellationToken cancellationToken)
    {
        var configuration = _store.Load();
        var overrides = GetOverrides(configuration, appId);
        var global = ToGlobal(configuration);
        var cleanTitle = (title ?? string.Empty).Trim();
        var enabled = configuration.Enabled && overrides.Mode != "off";

        uint steamAppId;
        string matchSource;
        string? matchedName = null;
        if (overrides.SteamAppId is { } chosen)
        {
            steamAppId = chosen;
            matchSource = "manual";
        }
        else if (!isShortcut)
        {
            steamAppId = appId;
            matchSource = "steam";
        }
        else if (!configuration.MatchShortcuts)
        {
            return new TrailerGameSnapshot(appId, cleanTitle, isShortcut, enabled, 0, "none", null, [], null, overrides, global,
                "Trailers for non-Steam games are turned off.");
        }
        else
        {
            var match = await MatchTitleAsync(cleanTitle, cancellationToken).ConfigureAwait(false);
            steamAppId = match?.SteamAppId ?? 0;
            matchedName = match?.MatchedName;
            matchSource = steamAppId > 0 ? "search" : "none";
        }

        if (steamAppId == 0)
        {
            return new TrailerGameSnapshot(appId, cleanTitle, isShortcut, enabled, 0, matchSource, matchedName, [], null, overrides, global,
                string.IsNullOrWhiteSpace(cleanTitle) ? "No title to search for." : "No matching Steam store page was found.");
        }

        var (movies, message) = await GetMoviesAsync(steamAppId, cancellationToken).ConfigureAwait(false);
        var selected = SteamTrailerCatalog.PickMovie(movies, overrides.MovieId);
        return new TrailerGameSnapshot(
            appId,
            cleanTitle,
            isShortcut,
            enabled,
            steamAppId,
            matchSource,
            matchedName,
            movies,
            selected?.Id,
            overrides,
            global,
            movies.Count == 0 ? message ?? "Steam has no trailer for this game." : null);
    }

    public TrailerGameSettings SaveGame(uint appId, TrailerGameSettings settings)
    {
        var normalized = TrailerSettingsStore.NormalizeGame(settings);
        _store.Update(configuration =>
        {
            var key = appId.ToString(CultureInfo.InvariantCulture);
            if (TrailerSettingsStore.IsEmpty(normalized))
            {
                configuration.Games.Remove(key);
            }
            else
            {
                configuration.Games[key] = normalized;
            }
        });
        return normalized;
    }

    public void ResetGame(uint appId)
    {
        _store.Update(configuration => configuration.Games.Remove(appId.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Store search for the per-game page ("use the trailers of …").</summary>
    public async Task<IReadOnlyList<TrailerStoreSearchResult>> SearchStoreAsync(string term, CancellationToken cancellationToken)
    {
        var cleanTerm = term.Trim();
        if (cleanTerm.Length is < 2 or > 120)
        {
            return [];
        }

        return await SearchCachedAsync(cleanTerm, cancellationToken).ConfigureAwait(false);
    }

    internal static TrailerGameSettings GetOverrides(TrailerConfiguration configuration, uint appId) =>
        configuration.Games.TryGetValue(appId.ToString(CultureInfo.InvariantCulture), out var settings)
            ? TrailerSettingsStore.NormalizeGame(settings)
            : new TrailerGameSettings();

    private async Task<TrailerTitleMatch?> MatchTitleAsync(string title, CancellationToken cancellationToken)
    {
        var key = SteamTrailerCatalog.NormalizeTitle(title);
        if (key.Length < 2)
        {
            return null;
        }

        var remembered = _store.Load().TitleMatches.GetValueOrDefault(key);
        if (remembered is not null &&
            (remembered.SteamAppId > 0 || _clock() - remembered.CheckedAt < NoMatchRetryAfter))
        {
            return remembered.SteamAppId > 0 ? remembered : null;
        }

        IReadOnlyList<TrailerStoreSearchResult> results;
        try
        {
            results = await SearchCachedAsync(title, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null; // offline: try again next time, remember nothing
        }

        var best = SteamTrailerCatalog.FindBestMatch(title, results);
        var match = new TrailerTitleMatch
        {
            SteamAppId = best?.AppId ?? 0,
            MatchedName = best?.Name,
            CheckedAt = _clock(),
        };
        _store.Update(configuration => configuration.TitleMatches[key] = match);
        return match.SteamAppId > 0 ? match : null;
    }

    private Task<IReadOnlyList<TrailerStoreSearchResult>> SearchCachedAsync(string term, CancellationToken cancellationToken)
    {
        var key = term.ToLowerInvariant();
        var task = _searches.GetOrAdd(key, _ => SearchRemoteAsync(term, cancellationToken));
        _ = task.ContinueWith(_ => _searches.TryRemove(key, out var _), TaskScheduler.Default);
        return task;
    }

    private async Task<IReadOnlyList<TrailerStoreSearchResult>> SearchRemoteAsync(string term, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(SteamTrailerCatalog.BuildStoreSearchUrl(term), cancellationToken).ConfigureAwait(false);
        return SteamTrailerCatalog.ParseStoreSearch(json);
    }

    private async Task<(IReadOnlyList<TrailerMovie> Movies, string? Message)> GetMoviesAsync(uint steamAppId, CancellationToken cancellationToken)
    {
        var now = _clock();
        if (_movieCache.TryGetValue(steamAppId, out var cached) && cached.ExpiresAt > now)
        {
            return (cached.Movies, cached.Message);
        }

        try
        {
            var json = await GetStringAsync(SteamTrailerCatalog.BuildAppDetailsUrl(steamAppId), cancellationToken).ConfigureAwait(false);
            var movies = SteamTrailerCatalog.ParseMovies(json, steamAppId);
            _movieCache[steamAppId] = new CachedMovies(movies, null, now + MovieCacheLifetime);
            return (movies, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            const string message = "Steam could not be reached for trailers.";
            _movieCache[steamAppId] = new CachedMovies([], message, now + FailedLookupLifetime);
            return ([], message);
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _httpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    private static TrailerGlobalSnapshot ToGlobal(TrailerConfiguration configuration) =>
        new(
            configuration.Enabled,
            configuration.DelaySeconds,
            configuration.Quality,
            configuration.MoveLogo,
            configuration.StartWithSound,
            configuration.MatchShortcuts,
            configuration.Games
                .Where(entry => entry.Value.Mode == "off")
                .Select(entry => uint.Parse(entry.Key, CultureInfo.InvariantCulture))
                .Order()
                .ToArray());

    private sealed record CachedMovies(IReadOnlyList<TrailerMovie> Movies, string? Message, DateTimeOffset ExpiresAt);
}
