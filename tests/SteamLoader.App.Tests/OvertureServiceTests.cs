using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Overture;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OvertureServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tfs-overture-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private static OvertureSearchResult Result(string title, double? seconds, string id = "dQw4w9WgXcQ") =>
        new(id, title, "Channel", seconds);

    [Fact]
    public void AutomaticPickNeedsTheGameNameAndAThemeLikeTitle()
    {
        var results = new[]
        {
            Result("Hades - Gameplay Walkthrough Part 1", 1800, "aaaaaaaaaaa"),
            Result("Hades OST - No Escape (piano cover)", 200, "bbbbbbbbbbb"),
            Result("Hades Main Theme - No Escape", 180, "ccccccccccc"),
            Result("Hades Original Soundtrack", 160, "ddddddddddd"),
        };

        Assert.Equal("ccccccccccc", OvertureService.PickAutomatic("HADES™", results)!.VideoId);
        Assert.Null(OvertureService.PickAutomatic("Celeste", results));
        Assert.Null(OvertureService.PickAutomatic("Hades", [Result("Hades Main Theme 10 Hours", 36000)]));
        Assert.Null(OvertureService.PickAutomatic("Hades", [Result("Hades Main Theme", null)]));
        Assert.Null(OvertureService.PickAutomatic("Hades", [Result("Hades the best moments", 200)]));
        Assert.Equal(
            "eeeeeeeeeee",
            OvertureService.PickAutomatic("The Witcher 3: Wild Hunt", [Result("The Witcher 3 Wild Hunt - Main Menu Theme", 240, "eeeeeeeeeee")])!.VideoId);
    }

    [Fact]
    public void ReadsChecksumListsInEveryPublishedLayout()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var list = $"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff  yt-dlp\n{hash}  yt-dlp.exe\n";
        Assert.Equal(hash.ToUpperInvariant(), OvertureYoutubeTools.FindChecksum(list, "yt-dlp.exe"));
        Assert.Null(OvertureYoutubeTools.FindChecksum(list, "yt-dlp_x86.exe"));

        var powershell = $"\r\nAlgorithm       Hash                                                                   Path\r\n---------       ----                                                                   ----\r\nSHA256          {hash.ToUpperInvariant()}       D:\\a\\deno.zip\r\n";
        Assert.Equal(hash.ToUpperInvariant(), OvertureYoutubeTools.FindChecksum(powershell, null));
        Assert.Null(OvertureYoutubeTools.FindChecksum("nothing here", null));
    }

    [Fact]
    public void ParsesYtDlpSearchJsonAndDropsInvalidEntries()
    {
        const string json = """
            {"_type":"playlist","entries":[
              {"id":"ccccccccccc","title":"Hades Main Theme","duration":181.0,"channel":"Supergiant Games"},
              {"id":"bad id","title":"broken"},
              {"id":"ddddddddddd","title":"No duration","uploader":"Someone"}
            ]}
            """;
        var results = OvertureYoutubeTools.ParseSearchResults(json);

        Assert.Equal(2, results.Count);
        Assert.Equal(181, results[0].DurationSeconds);
        Assert.Equal("Supergiant Games", results[0].Channel);
        Assert.Null(results[1].DurationSeconds);
        Assert.Equal("Someone", results[1].Channel);
        Assert.Empty(OvertureYoutubeTools.ParseSearchResults("not json"));
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, true, 0, 99)]
    [InlineData("bytes=500-", 1000, true, 500, 999)]
    [InlineData("bytes=-100", 1000, true, 900, 999)]
    [InlineData("bytes=900-5000", 1000, true, 900, 999)]
    [InlineData("bytes=1000-", 1000, false, 0, 999)]
    [InlineData("bytes=0-1,5-9", 1000, false, 0, 999)]
    [InlineData("items=0-1", 1000, false, 0, 999)]
    public void ParsesSingleByteRanges(string header, long length, bool valid, long start, long end)
    {
        Assert.Equal(valid, OvertureApiEndpoints.TryParseRange(header, length, out var actualStart, out var actualEnd));
        if (valid)
        {
            Assert.Equal(start, actualStart);
            Assert.Equal(end, actualEnd);
        }
    }

    [Fact]
    public void NormalizationDropsUnknownTracksAndInvalidKeys()
    {
        var configuration = new OvertureConfiguration
        {
            MasterVolume = 400,
            FadeSeconds = double.NaN,
            MusicFolders = ["relative\\folder", "C:\\Music", "c:\\music"],
            Tracks =
            {
                ["yt-ccccccccccc"] = new OvertureTrack { Id = "yt-ccccccccccc", Kind = "youtube", Source = "ccccccccccc", Title = "Theme" },
                ["yt-wrong"] = new OvertureTrack { Id = "yt-wrong", Kind = "youtube", Source = "ccccccccccc" },
                ["local-1"] = new OvertureTrack { Id = "local-1", Kind = "local", Source = "relative.mp3" },
            },
            Playlists =
            {
                ["440"] = new OverturePlaylist { TrackIds = ["yt-ccccccccccc", "yt-wrong", "yt-ccccccccccc"], Volume = 100, Loop = true },
                ["0440"] = new OverturePlaylist { TrackIds = ["yt-ccccccccccc"] },
                ["ambient"] = new OverturePlaylist(),
                ["store"] = new OverturePlaylist { Mode = "off" },
            },
        };

        OvertureSettingsStore.Normalize(configuration);

        Assert.Equal(100, configuration.MasterVolume);
        Assert.Equal(1.5, configuration.FadeSeconds);
        Assert.Equal(["C:\\Music"], configuration.MusicFolders);
        Assert.Equal(["yt-ccccccccccc"], configuration.Tracks.Keys);
        Assert.Equal(["440"], configuration.Playlists.Keys);
        Assert.Equal(["yt-ccccccccccc"], configuration.Playlists["440"].TrackIds);
        Assert.Null(configuration.Playlists["440"].Volume);
        Assert.Null(configuration.Playlists["440"].Loop);
    }

    [Fact]
    public void LocalMusicIsLimitedToTheMusicFoldersAndServedOnlyWhenKnown()
    {
        var music = Directory.CreateDirectory(Path.Combine(_root, "music-folder")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var song = Path.Combine(music, "Album", "Main Theme.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        File.WriteAllBytes(song, [1, 2, 3]);
        var secret = Path.Combine(outside, "secret.mp3");
        File.WriteAllBytes(secret, [1]);
        File.WriteAllText(Path.Combine(music, "notes.txt"), "not audio");

        var service = CreateService();
        service.SaveSettings(new OvertureGlobalUpdate(null, null, null, null, null, null, null, null, [music]));

        var files = service.ListLocalFiles("main theme", 10);
        Assert.Single(files);
        Assert.False(files[0].Assigned);
        Assert.Empty(service.ListLocalFiles("notes", 10));

        Assert.Throws<ArgumentException>(() => service.AddLocal("440", secret, "Game"));
        Assert.Throws<ArgumentException>(() => service.AddLocal("440", Path.Combine(music, "notes.txt"), "Game"));

        var playlist = service.AddLocal("440", song, "Game");
        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("local", track.Kind);
        Assert.False(track.Missing);
        Assert.Equal("manual", playlist.Source);
        Assert.Equal(song, service.ResolveAudioFile(track.Id));
        Assert.Null(service.ResolveAudioFile("local-unknown"));
        Assert.Contains(440u, service.GetSettings().AppIdsWithMusic);

        // Adding twice keeps one entry; removing keeps the user's file.
        service.AddLocal("440", song, "Game");
        Assert.Single(service.GetPlaylist("440", "Game").Tracks);
        Assert.Empty(service.RemoveTrack("440", track.Id, "Game").Tracks);
        Assert.True(File.Exists(song));
    }

    [Fact]
    public void SilencingAGameAndReorderingThePlaylist()
    {
        var music = Directory.CreateDirectory(Path.Combine(_root, "music")).FullName;
        var first = Path.Combine(music, "a.mp3");
        var second = Path.Combine(music, "b.ogg");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [1]);
        var service = CreateService();
        service.SaveSettings(new OvertureGlobalUpdate(null, null, null, null, null, null, null, null, [music]));
        service.AddLocal("440", first, "Game");
        var playlist = service.AddLocal("440", second, "Game");
        var ids = playlist.Tracks.Select(track => track.Id).ToList();

        playlist = service.SavePlaylist("440", new OverturePlaylistUpdate(null, null, 40, 3, false, [ids[1], ids[0], "foreign"]), "Game");
        Assert.Equal([ids[1], ids[0]], playlist.Tracks.Select(track => track.Id));
        Assert.Equal(40, playlist.Volume);
        Assert.Equal(3, playlist.StartSeconds);
        Assert.False(playlist.Loop);

        playlist = service.SavePlaylist("440", new OverturePlaylistUpdate("off", null, null, null, null, null), "Game");
        Assert.True(playlist.Silent);
        Assert.False(playlist.Enabled);
        Assert.Contains(440u, service.GetSettings().SilentAppIds);

        playlist = service.SavePlaylist("440", new OverturePlaylistUpdate(null, true, null, null, null, null), "Game");
        Assert.False(playlist.Silent);

        Assert.Empty(service.ResetPlaylist("440", "Game").Tracks);
    }

    [Fact]
    public void AutomaticSearchOnlyForEmptyGamesWhileTurnedOn()
    {
        var service = CreateService();
        // YouTube support installs itself on first use, so a fresh game qualifies.
        Assert.True(service.GetPlaylist("440", "Hades").CanAutoFind);
        Assert.False(service.GetPlaylist("440", "").CanAutoFind);
        Assert.False(service.GetPlaylist("ambient", null).CanAutoFind);

        service.SavePlaylist("440", new OverturePlaylistUpdate("off", null, null, null, null, null), "Hades");
        Assert.False(service.GetPlaylist("440", "Hades").CanAutoFind);

        service.SaveSettings(new OvertureGlobalUpdate(null, null, null, null, false, null, null, null, null));
        Assert.False(service.GetPlaylist("570", "Dota 2").CanAutoFind);
        // Nothing qualifies, so the prefetch does not start (and downloads nothing).
        var status = service.StartPrefetch([(570u, "Dota 2"), (440u, "Hades")]);
        Assert.False(status.Running);
        Assert.Equal(0, status.Total);
    }

    [Fact]
    public void CleanupDeletesOnlyUnusedDownloadsInTheOwnFolder()
    {
        var data = Path.Combine(_root, "overture");
        var downloads = Directory.CreateDirectory(Path.Combine(data, "downloads")).FullName;
        var used = Path.Combine(downloads, "yt-aaaaaaaaaaa.m4a");
        var unused = Path.Combine(downloads, "yt-bbbbbbbbbbb.m4a");
        var stray = Path.Combine(downloads, "yt-ccccccccccc.m4a.part");
        File.WriteAllBytes(used, new byte[10]);
        File.WriteAllBytes(unused, new byte[20]);
        File.WriteAllBytes(stray, new byte[5]);
        var store = new OvertureSettingsStore(Path.Combine(_root, "overture.json"));
        store.Save(new OvertureConfiguration
        {
            Tracks =
            {
                ["yt-aaaaaaaaaaa"] = new OvertureTrack { Id = "yt-aaaaaaaaaaa", Kind = "youtube", Source = "aaaaaaaaaaa", File = used, Title = "Used" },
                ["yt-bbbbbbbbbbb"] = new OvertureTrack { Id = "yt-bbbbbbbbbbb", Kind = "youtube", Source = "bbbbbbbbbbb", File = unused, Title = "Unused" },
            },
            Playlists = { ["440"] = new OverturePlaylist { TrackIds = ["yt-aaaaaaaaaaa"] } },
        });
        var service = new OvertureService(store, data);

        var before = service.GetStorage();
        Assert.Equal(3, before.DownloadCount);
        Assert.Equal(2, before.UnusedCount);
        Assert.Equal(used, service.ResolveAudioFile("yt-aaaaaaaaaaa"));

        var after = service.DeleteUnusedDownloads();
        Assert.True(File.Exists(used));
        Assert.False(File.Exists(unused));
        Assert.False(File.Exists(stray));
        Assert.Equal(1, after.DownloadCount);
        Assert.Equal(0, after.UnusedCount);
    }

    [Fact]
    public void DownloadedTracksOutsideTheOwnFolderAreNotServed()
    {
        var data = Path.Combine(_root, "overture");
        var elsewhere = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "other")).FullName, "x.m4a");
        File.WriteAllBytes(elsewhere, [1]);
        var store = new OvertureSettingsStore(Path.Combine(_root, "overture.json"));
        store.Save(new OvertureConfiguration
        {
            Tracks = { ["yt-aaaaaaaaaaa"] = new OvertureTrack { Id = "yt-aaaaaaaaaaa", Kind = "youtube", Source = "aaaaaaaaaaa", File = elsewhere } },
        });

        Assert.Null(new OvertureService(store, data).ResolveAudioFile("yt-aaaaaaaaaaa"));
    }

    [Theory]
    [InlineData("440", true)]
    [InlineData("ambient", true)]
    [InlineData("0", false)]
    [InlineData("0440", false)]
    [InlineData("4294967296", false)]
    [InlineData("-1", false)]
    [InlineData("../x", false)]
    [InlineData(null, false)]
    public void ValidatesPlaylistKeys(string? key, bool valid) =>
        Assert.Equal(valid, OvertureSettingsStore.IsValidKey(key));

    private OvertureService CreateService() =>
        new(new OvertureSettingsStore(Path.Combine(_root, "overture.json")), Path.Combine(_root, "overture"));
}
