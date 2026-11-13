using System.Net;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Trailers;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class TrailerServiceTests
{
    private const string HadesAppDetails = """
        {"1145360":{"success":true,"data":{"movies":[
          {"id":256801288,"name":"Gameplay Showcase","thumbnail":"https://shared.akamai.steamstatic.com/a.jpg",
           "dash_h264":"https://video.akamai.steamstatic.com/store_trailers/1145360/2/dash_h264.mpd?t=1","hls_h264":"https://video.akamai.steamstatic.com/2.m3u8","highlight":false},
          {"id":256801252,"name":"Launch Trailer","thumbnail":"https://shared.akamai.steamstatic.com/b.jpg",
           "dash_h264":"https://video.akamai.steamstatic.com/store_trailers/1145360/1/dash_h264.mpd?t=1","hls_h264":"http://insecure.example/1.m3u8","highlight":true},
          {"id":5,"name":"No streams"}
        ]}}}
        """;

    [Fact]
    public void ParsesSteamMoviesAndPicksTheHighlightedOne()
    {
        var movies = SteamTrailerCatalog.ParseMovies(HadesAppDetails, 1145360);

        Assert.Equal(2, movies.Count);
        Assert.Equal(string.Empty, movies[1].HlsH264); // http is rejected
        Assert.Equal(256801252, SteamTrailerCatalog.PickMovie(movies, null)!.Id);
        Assert.Equal(256801288, SteamTrailerCatalog.PickMovie(movies, 256801288)!.Id);
        Assert.Equal(256801252, SteamTrailerCatalog.PickMovie(movies, 999)!.Id);
        Assert.Empty(SteamTrailerCatalog.ParseMovies("""{"1":{"success":false}}""", 1));
        Assert.Empty(SteamTrailerCatalog.ParseMovies("not json", 1));
    }

    [Theory]
    [InlineData("Hades", "Hades")]
    [InlineData("HADES™", "Hades")]
    [InlineData("The Witcher 3: Wild Hunt - Game of the Year Edition", "The Witcher 3: Wild Hunt")]
    [InlineData("Ratchet & Clank: Rift Apart", "Ratchet and Clank: Rift Apart")]
    [InlineData("Pokémon Legends", "Pokemon Legends")]
    public void MatchesTheSameGameDespiteSpellingAndEditions(string title, string storeName)
    {
        var results = new[]
        {
            new TrailerStoreSearchResult(1, "Something Else"),
            new TrailerStoreSearchResult(42, storeName),
        };

        Assert.Equal(42u, SteamTrailerCatalog.FindBestMatch(title, results)?.AppId);
    }

    [Fact]
    public void RejectsLooseMatches()
    {
        var results = new[] { new TrailerStoreSearchResult(7, "Hades II") };
        Assert.Null(SteamTrailerCatalog.FindBestMatch("Hades", results));
        Assert.Null(SteamTrailerCatalog.FindBestMatch("", results));
    }

    [Fact]
    public void ParsesStoreSearchAppsOnly()
    {
        var results = SteamTrailerCatalog.ParseStoreSearch("""
            {"total":2,"items":[{"type":"app","name":"Hades","id":1145360},{"type":"sub","name":"Bundle","id":9},{"type":"app","name":"","id":3}]}
            """);

        var result = Assert.Single(results);
        Assert.Equal(1145360u, result.AppId);
    }

    [Fact]
    public async Task SteamGamesUseTheirOwnTrailersAndOverridesPersist()
    {
        using var directory = new TempDirectory();
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("appdetails", StringComparison.Ordinal) ? HadesAppDetails : "{}");
        var service = new TrailerService(new HttpClient(handler), new TrailerSettingsStore(directory.File("trailers.json")));

        var game = await service.GetGameAsync(1145360, "Hades", isShortcut: false, CancellationToken.None);
        Assert.True(game.Enabled);
        Assert.Equal("steam", game.MatchSource);
        Assert.Equal(256801252, game.SelectedMovieId);

        service.SaveGame(1145360, new TrailerGameSettings { Mode = "OFF", MovieId = 256801288, TrimStartSeconds = 4.04, TrimEndSeconds = -1 });
        game = await service.GetGameAsync(1145360, "Hades", isShortcut: false, CancellationToken.None);
        Assert.False(game.Enabled);
        Assert.Equal(256801288, game.SelectedMovieId);
        Assert.Equal(4.0, game.Overrides.TrimStartSeconds);
        Assert.Null(game.Overrides.TrimEndSeconds);
        Assert.Equal([1145360u], service.GetSettings().DisabledAppIds);
        Assert.Equal(1, handler.Count("appdetails")); // cached

        service.ResetGame(1145360);
        Assert.Empty(service.GetSettings().DisabledAppIds);
    }

    [Fact]
    public async Task ShortcutsAreMatchedOnceAndTheMatchIsRemembered()
    {
        using var directory = new TempDirectory();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            var url when url.Contains("storesearch", StringComparison.Ordinal) =>
                """{"items":[{"type":"app","name":"Hades","id":1145360}]}""",
            var url when url.Contains("appdetails", StringComparison.Ordinal) => HadesAppDetails,
            _ => "{}",
        });
        var store = new TrailerSettingsStore(directory.File("trailers.json"));
        var service = new TrailerService(new HttpClient(handler), store);

        var game = await service.GetGameAsync(2_900_000_000, "Hades™", isShortcut: true, CancellationToken.None);
        Assert.Equal(1145360u, game.SteamAppId);
        Assert.Equal("search", game.MatchSource);
        Assert.Equal(2, game.Movies.Count);

        var again = new TrailerService(new HttpClient(handler), store);
        await again.GetGameAsync(2_900_000_000, "Hades", isShortcut: true, CancellationToken.None);
        Assert.Equal(1, handler.Count("storesearch"));

        again.SaveGame(2_900_000_000, new TrailerGameSettings { SteamAppId = 1145360 });
        var manual = await again.GetGameAsync(2_900_000_000, "Hades", isShortcut: true, CancellationToken.None);
        Assert.Equal("manual", manual.MatchSource);
    }

    [Fact]
    public async Task ShortcutMatchingCanBeTurnedOffAndOfflineIsNotRemembered()
    {
        using var directory = new TempDirectory();
        var handler = new RecordingHandler(_ => throw new HttpRequestException("offline"));
        var store = new TrailerSettingsStore(directory.File("trailers.json"));
        var service = new TrailerService(new HttpClient(handler), store);

        var offline = await service.GetGameAsync(3_000_000_000, "Hades", isShortcut: true, CancellationToken.None);
        Assert.Equal(0u, offline.SteamAppId);
        Assert.Empty(store.Load().TitleMatches);

        var settings = service.SaveSettings(new TrailerGlobalUpdate(null, 99, "4k", false, true, false));
        Assert.Equal(30, settings.DelaySeconds);
        Assert.Equal("1080", settings.Quality);
        Assert.False(settings.MoveLogo);
        Assert.True(settings.StartWithSound);

        var off = await service.GetGameAsync(3_000_000_000, "Hades", isShortcut: true, CancellationToken.None);
        Assert.Equal("none", off.MatchSource);
        Assert.Contains("turned off", off.Message);
    }

    [Fact]
    public void TrailersIsABuiltInPluginThatCanBeDisabled()
    {
        var plugin = Assert.Single(SteamLoaderPluginCatalog.BuildDefinitions(), definition => definition.Id == "trailers");
        Assert.True(plugin.CanDisable);
        Assert.True(plugin.DefaultEnabled);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        private readonly List<string> _urls = [];

        public int Count(string fragment)
        {
            lock (_urls)
            {
                return _urls.Count(url => url.Contains(fragment, StringComparison.Ordinal));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_urls)
            {
                _urls.Add(request.RequestUri!.AbsoluteUri);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tfs-trailer-tests-{Guid.NewGuid():N}");

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
