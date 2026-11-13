using System.Net;
using System.Text;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryGamePageMetadataServiceTests
{
    [Fact]
    public async Task GetAsync_FirstLoadCachesSections_AndSecondLoadDoesNotRefetch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var first = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);
            var callsAfterFirstLoad = handler.CallCount;
            var second = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);

            Assert.NotNull(first);
            Assert.Equal("ready", first.Status);
            Assert.Equal(990080, first.SourceSteamAppId);
            Assert.Equal("Test Studio", Assert.Single(first.GameInfo.Developers));
            Assert.Equal("A reliable cached description.", first.GameInfo.Description);
            Assert.Single(first.Activity);
            Assert.Equal(2, first.Community.Count);
            Assert.Equal("not-configured", first.Achievements.Status);
            Assert.Equal("Epic Games", first.Achievements.Provider);
            Assert.Empty(first.Achievements.Items);
            Assert.NotNull(second);
            Assert.Equal(first.Revision, second.Revision);
            Assert.Equal(callsAfterFirstLoad, handler.CallCount);
            Assert.True(File.Exists(Path.Combine(root, "metadata.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_ForceRefreshWithIdenticalContent_KeepsRevision()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var first = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);
            var second = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: true,
                CancellationToken.None);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first.Revision, second.Revision);
            Assert.Equal("ready", second.Status);
            Assert.Equal(6, handler.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_RefreshFailure_KeepsLastGoodMetadata()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);
            var first = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);
            handler.FailMetadataRequests = true;

            var degraded = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: true,
                CancellationToken.None);

            Assert.NotNull(first);
            Assert.NotNull(degraded);
            Assert.Equal("degraded", degraded.Status);
            Assert.Equal(first.GameInfo.Description, degraded.GameInfo.Description);
            Assert.Equal(first.Activity, degraded.Activity);
            Assert.Contains("Cached data is kept", degraded.Warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_AmbiguousNumberedTitle_DoesNotUseWrongSteamGame()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler
            {
                SearchJson =
                    """{"total":1,"items":[{"id":990080,"name":"Example Game 3"}]}""",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var snapshot = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal("unmatched", snapshot.Status);
            Assert.Null(snapshot.SourceSteamAppId);
            Assert.Empty(snapshot.Activity);
            // Two Steam searches, then only the IGN fallback (no page found).
            Assert.Equal(2, handler.RequestUris.Count(uri => uri.AbsoluteUri.Contains("storesearch")));
            Assert.All(
                handler.RequestUris,
                uri => Assert.True(
                    uri.AbsoluteUri.Contains("storesearch") ||
                    uri.AbsoluteUri.StartsWith("https://www.ign.com/games/", StringComparison.Ordinal),
                    uri.AbsoluteUri));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_XboxTitleWithoutSteamMatch_UsesOfficialXboxMetadata()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler
            {
                SearchJson = """{"total":0,"items":[]}""",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(
                root,
                httpClient,
                storeId: "xbox-game-pass",
                gameId: "9NTESTPRODUCT");

            var snapshot = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);
            var callsAfterFirstLoad = handler.CallCount;
            var cached = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal("ready", snapshot.Status);
            Assert.Equal("Xbox", snapshot.SourceLabel);
            Assert.Equal("Official Xbox description.", snapshot.GameInfo.Description);
            Assert.Equal("Xbox Studio", Assert.Single(snapshot.GameInfo.Developers));
            Assert.Single(snapshot.GameInfo.Screenshots);
            Assert.Single(snapshot.Community);
            Assert.NotNull(cached);
            Assert.Equal(callsAfterFirstLoad, handler.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_ForceRefresh_RetriesPreviouslyUnmatchedSteamSource()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler
            {
                SearchJson = """{"total":0,"items":[]}""",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(
                root,
                httpClient,
                storeId: "xbox-game-pass",
                gameId: "9NTESTPRODUCT");

            var unmatched = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                CancellationToken.None);
            handler.SearchJson =
                """{"total":1,"items":[{"id":990080,"name":"Example Game 2"}]}""";
            var refreshed = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: true,
                CancellationToken.None);

            Assert.NotNull(unmatched);
            Assert.Null(unmatched.SourceSteamAppId);
            Assert.Empty(unmatched.Activity);
            Assert.NotNull(refreshed);
            Assert.Equal(990080, refreshed.SourceSteamAppId);
            Assert.Single(refreshed.Activity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_EditionSuffixSearch_AlsoUsesCleanTitleWithoutDanglingSeparator()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler
            {
                RequiredSearchTerm = "halo campaign evolved",
                SearchJson =
                    """{"total":1,"items":[{"id":990080,"name":"Halo: Campaign Evolved"}]}""",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(
                root,
                httpClient,
                storeId: "xbox-game-pass",
                gameId: "9NTESTPRODUCT",
                title: "Halo: Campaign Evolved – Standard Edition");

            var snapshot = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: true,
                CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal(990080, snapshot.SourceSteamAppId);
            Assert.Single(snapshot.Activity);
            Assert.Contains(
                handler.RequestUris,
                uri => Uri.UnescapeDataString(uri.Query)
                    .Contains(
                        "term=halo campaign evolved",
                        StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_OpenReturnsPersistedMetadataBeforeBackgroundRefresh()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using (var seedClient = new HttpClient(new MetadataHttpHandler()))
            using (var seedService = CreateService(root, seedClient))
            {
                var seeded = await seedService.GetAsync(
                    ShortcutAppId,
                    forceRefresh: false,
                    CancellationToken.None);
                Assert.Equal("A reliable cached description.", seeded?.GameInfo.Description);
            }

            var refreshHandler = new MetadataHttpHandler
            {
                FailMetadataRequests = true,
            };
            using var refreshClient = new HttpClient(refreshHandler);
            using var refreshService = CreateService(root, refreshClient);

            var immediate = await refreshService.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                refreshOnOpen: true,
                CancellationToken.None);

            Assert.NotNull(immediate);
            Assert.True(immediate.Cached);
            Assert.True(immediate.Refreshing);
            Assert.Equal("updating", immediate.Status);
            Assert.Equal(
                "A reliable cached description.",
                immediate.GameInfo.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_FirstOpenReturnsLoadingSnapshotAndFillsCacheAsynchronously()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var immediate = await service.GetAsync(
                ShortcutAppId,
                forceRefresh: false,
                refreshOnOpen: true,
                CancellationToken.None);

            Assert.NotNull(immediate);
            Assert.False(immediate.Cached);
            Assert.True(immediate.Refreshing);
            Assert.Equal("updating", immediate.Status);
            Assert.True(File.Exists(Path.Combine(root, "metadata.json")));

            OmniLibraryGamePageMetadataSnapshot? completed = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                completed = await service.GetAsync(
                    ShortcutAppId,
                    forceRefresh: false,
                    CancellationToken.None);
                if (completed is { Refreshing: false, Status: "ready" })
                {
                    break;
                }
                await Task.Delay(10);
            }

            Assert.NotNull(completed);
            Assert.Equal("ready", completed.Status);
            Assert.Equal(
                "A reliable cached description.",
                completed.GameInfo.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchSteamMatchesAsync_ReturnsRankedCandidatesForManualCorrection()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler
            {
                SearchJson =
                    """{"total":2,"items":[{"id":990081,"name":"Example Game 3","tiny_image":"https://cdn.example/3.jpg"},{"id":990080,"name":"Example Game 2","tiny_image":"https://cdn.example/2.jpg","price":{"final_formatted":"19,99€"}}]}""",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var matches = await service.SearchSteamMatchesAsync(
                ShortcutAppId,
                "Example Game 2",
                CancellationToken.None);

            Assert.Equal(2, matches.Count);
            Assert.Equal(990080, matches[0].SteamAppId);
            Assert.Equal(0, matches[0].MatchScore);
            Assert.Equal("19,99€", matches[0].Price);
            Assert.Equal(990081, matches[1].SteamAppId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SetSteamMatchAsync_PersistsManualChoiceUntilItIsCleared()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstHandler = new MetadataHttpHandler();
            using (var firstClient = new HttpClient(firstHandler))
            using (var firstService = CreateService(root, firstClient))
            {
                var selected = await firstService.SetSteamMatchAsync(
                    ShortcutAppId,
                    990080,
                    CancellationToken.None);

                Assert.NotNull(selected);
                Assert.Equal(990080, selected.SourceSteamAppId);
                Assert.Equal("manual", selected.MatchMode);
                Assert.Equal("Steam (selected)", selected.SourceLabel);
            }

            var secondHandler = new MetadataHttpHandler
            {
                SearchJson = """{"total":0,"items":[]}""",
            };
            using var secondClient = new HttpClient(secondHandler);
            using var secondService = CreateService(root, secondClient);
            var refreshed = await secondService.GetAsync(
                ShortcutAppId,
                forceRefresh: true,
                CancellationToken.None);

            Assert.NotNull(refreshed);
            Assert.Equal(990080, refreshed.SourceSteamAppId);
            Assert.Equal("manual", refreshed.MatchMode);
            Assert.DoesNotContain(
                secondHandler.RequestUris,
                uri => uri.AbsoluteUri.Contains("storesearch", StringComparison.OrdinalIgnoreCase));

            var automatic = await secondService.ClearSteamMatchAsync(
                ShortcutAppId,
                CancellationToken.None);

            Assert.NotNull(automatic);
            Assert.Null(automatic.SourceSteamAppId);
            Assert.Equal("automatic", automatic.MatchMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_PlainShortcutWithTitle_UsesSteamMatchWithoutAchievementProvider()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);
            const uint plainShortcut = 0x9def5678;

            var first = await service.GetAsync(
                plainShortcut,
                "  Example Game 2\t",
                forceRefresh: false,
                refreshOnOpen: false,
                CancellationToken.None);
            var callsAfterFirstLoad = handler.CallCount;
            var second = await service.GetAsync(
                plainShortcut,
                "Example Game 2",
                forceRefresh: false,
                refreshOnOpen: false,
                CancellationToken.None);

            Assert.NotNull(first);
            Assert.Equal(OmniLibraryGamePageMetadataService.ShortcutStoreId, first.StoreId);
            Assert.Equal(plainShortcut, first.SteamAppId);
            Assert.Equal("Example Game 2", first.Title);
            Assert.Equal(990080, first.SourceSteamAppId);
            Assert.Equal("Test Studio", Assert.Single(first.GameInfo.Developers));
            Assert.Equal("none", first.Achievements.Status);
            Assert.Empty(first.Achievements.Items);
            Assert.DoesNotContain(
                handler.RequestUris,
                uri => uri.AbsoluteUri.Contains("/stats/", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(second);
            Assert.Equal(first.Revision, second.Revision);
            Assert.Equal(callsAfterFirstLoad, handler.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_ShortcutWithoutTitleOrSteamAppId_ReturnsNull()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var untitled = await service.GetAsync(
                0x9def5678,
                "   ",
                forceRefresh: false,
                refreshOnOpen: false,
                CancellationToken.None);
            var steamApp = await service.GetAsync(
                440,
                "Team Fortress 2",
                forceRefresh: false,
                refreshOnOpen: false,
                CancellationToken.None);

            Assert.Null(untitled);
            Assert.Null(steamApp);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_OmniLibraryGame_KeepsOmniLibraryIdentityWhenTitleIsPassed()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var snapshot = await service.GetAsync(
                ShortcutAppId,
                "Some Other Title",
                forceRefresh: false,
                refreshOnOpen: false,
                CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal("epic-games", snapshot.StoreId);
            Assert.Equal("example-game", snapshot.GameId);
            Assert.Equal("Epic Games", snapshot.Achievements.Provider);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveEditsAsync_EditsWinOverSourceAndSurviveRestartUntilReset()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler();
            using var httpClient = new HttpClient(handler);
            const uint plainShortcut = 0x9def5678;
            long revisionBefore;
            using (var service = CreateService(root, httpClient))
            {
                var before = await service.GetAsync(
                    plainShortcut,
                    "Example Game 2",
                    forceRefresh: false,
                    refreshOnOpen: false,
                    CancellationToken.None);
                revisionBefore = before!.Revision;

                var saved = await service.SaveEditsAsync(
                    plainShortcut,
                    "Example Game 2",
                    new GameMetadataEdits
                    {
                        Description = "  My own words.  ",
                        Developers = ["Indie Team", " ", "indie team"],
                        Rating = 140,
                    },
                    CancellationToken.None);

                Assert.NotNull(saved);
                Assert.Equal("My own words.", saved.Metadata.GameInfo.Description);
                Assert.Equal("Indie Team", Assert.Single(saved.Metadata.GameInfo.Developers));
                Assert.Equal(100, saved.Metadata.GameInfo.Rating);
                Assert.Equal("Test Publisher", Assert.Single(saved.Metadata.GameInfo.Publishers));
                Assert.Equal("Test Studio", Assert.Single(saved.Automatic.Developers));
                Assert.Contains("description", saved.Metadata.EditedFields);
                Assert.False(saved.ManagedByOmniLibrary);
                Assert.NotEqual(revisionBefore, saved.Metadata.Revision);
            }

            using (var restarted = CreateService(root, httpClient))
            {
                var page = await restarted.GetAsync(
                    plainShortcut,
                    "Example Game 2",
                    forceRefresh: false,
                    refreshOnOpen: false,
                    CancellationToken.None);
                Assert.Equal("My own words.", page!.GameInfo.Description);

                var reset = await restarted.SaveEditsAsync(
                    plainShortcut,
                    "Example Game 2",
                    edits: null,
                    CancellationToken.None);
                Assert.Equal("A reliable cached description.", reset!.Metadata.GameInfo.Description);
                Assert.Empty(reset.Metadata.EditedFields);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyUserEdits_UnmatchedShortcutWithEditsBecomesReady()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler
            {
                SearchJson = """{"total":0,"items":[]}""",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var saved = await service.SaveEditsAsync(
                0x9def5678,
                "Homebrew Thing",
                new GameMetadataEdits { ReleaseDate = "2024" },
                CancellationToken.None);

            Assert.NotNull(saved);
            Assert.Null(saved.Metadata.SourceSteamAppId);
            Assert.Equal("ready", saved.Metadata.Status);
            Assert.Equal("2024", saved.Metadata.GameInfo.ReleaseDate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static string IgnPage(string name) =>
        """
        <html><body><script id="__NEXT_DATA__" type="application/json">
        {"props":{"pageProps":{"page":{"type":"Game","name":"__NAME__",
          "metadata":{"names":{"name":"__NAME__"},"descriptions":{"short":"A short IGN summary.","long":"A long IGN description."}},
          "genreNames":["Action","Platformer"],
          "producers":{"name":"Little Studio","slug":"little-studio"},
          "publishers":[{"name":"Little Studio"},{"name":"Big Publisher"}],
          "releaseDate":"Feb 24, 2017",
          "primaryReview":{"score":9.4},
          "primaryImage":{"url":"https://assets-prd.ignimgs.com/hero.png"}}}}}
        </script></body></html>
        """.Replace("__NAME__", name, StringComparison.Ordinal);

    [Fact]
    public async Task GetAsync_ShortcutWithoutSteamMatch_FallsBackToMatchingIgnPage()
    {
        var root = CreateTemporaryDirectory();
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de-DE");
            var handler = new MetadataHttpHandler
            {
                SearchJson = """{"total":0,"items":[]}""",
                IgnHtml = IgnPage("Hollow Knight"),
                IgnSlug = "hollow-knight",
                TranslateJson = """[[["Eine lange IGN-Beschreibung.","A long IGN description.",null,null,10]],null,"en"]""",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var snapshot = await service.GetAsync(
                0x9def5678,
                "Hollow Knight™",
                forceRefresh: false,
                refreshOnOpen: false,
                CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal("ready", snapshot.Status);
            Assert.Equal("IGN", snapshot.SourceLabel);
            Assert.Null(snapshot.SourceSteamAppId);
            Assert.Equal("Eine lange IGN-Beschreibung.", snapshot.GameInfo.Description);
            Assert.Equal("Little Studio", Assert.Single(snapshot.GameInfo.Developers));
            Assert.Equal(["Little Studio", "Big Publisher"], snapshot.GameInfo.Publishers);
            Assert.Equal(94, snapshot.GameInfo.Rating);
            Assert.Equal("https://www.ign.com/games/hollow-knight", snapshot.GameInfo.StoreUrl);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = culture;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SetIgnPageAsync_ChosenPageWinsOverSteamUntilCleared()
    {
        var root = CreateTemporaryDirectory();
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            var handler = new MetadataHttpHandler
            {
                IgnHtml = IgnPage("Some Other Name"),
                IgnSlug = "some-other-name",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);
            const uint plainShortcut = 0x9def5678;

            await Assert.ThrowsAsync<ArgumentException>(() => service.SetIgnPageAsync(
                plainShortcut,
                "Example Game 2",
                "https://example.com/games/x",
                CancellationToken.None));

            var chosen = await service.SetIgnPageAsync(
                plainShortcut,
                "Example Game 2",
                "https://www.ign.com/games/some-other-name",
                CancellationToken.None);

            Assert.NotNull(chosen);
            Assert.Equal("https://www.ign.com/games/some-other-name", chosen.IgnPage);
            Assert.Equal("IGN", chosen.Metadata.SourceLabel);
            Assert.Equal("A long IGN description.", chosen.Metadata.GameInfo.Description);

            var cleared = await service.SetIgnPageAsync(
                plainShortcut,
                "Example Game 2",
                " ",
                CancellationToken.None);

            Assert.Empty(cleared!.IgnPage);
            Assert.Equal("A reliable cached description.", cleared.Metadata.GameInfo.Description);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = culture;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_IgnPageForAnotherGame_IsNotUsed()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var handler = new MetadataHttpHandler
            {
                SearchJson = """{"total":0,"items":[]}""",
                IgnHtml = IgnPage("Hollow Knight: Silksong"),
                IgnSlug = "hollow-knight",
            };
            using var httpClient = new HttpClient(handler);
            using var service = CreateService(root, httpClient);

            var snapshot = await service.GetAsync(
                0x9def5678,
                "Hollow Knight",
                forceRefresh: false,
                refreshOnOpen: false,
                CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal("unmatched", snapshot.Status);
            Assert.Empty(snapshot.GameInfo.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanEdits_BlankFieldsMeanAutomatic()
    {
        Assert.Null(OmniLibraryGamePageMetadataService.CleanEdits(new GameMetadataEdits
        {
            Description = "   ",
            Developers = ["", "  "],
            ReleaseDate = "\t",
        }));
        var clean = OmniLibraryGamePageMetadataService.CleanEdits(new GameMetadataEdits
        {
            Genres = Enumerable.Range(0, 20).Select(index => $"Genre {index}").ToArray(),
            Description = new string('d', 9000),
        });
        Assert.NotNull(clean);
        Assert.Equal(8, clean.Genres!.Count);
        Assert.Equal(8000, clean.Description!.Length);
    }

    [Fact]
    public void CleanShortcutTitle_StripsControlCharactersAndCapsLength()
    {
        Assert.Equal("A B", OmniLibraryGamePageMetadataService.CleanShortcutTitle(" A\u0000\r\nB "));
        Assert.Equal(200, OmniLibraryGamePageMetadataService.CleanShortcutTitle(new string('x', 500)).Length);
        Assert.Equal(string.Empty, OmniLibraryGamePageMetadataService.CleanShortcutTitle(null));
    }

    private const uint ShortcutAppId = 0x8abc1234;

    private static OmniLibraryGamePageMetadataService CreateService(
        string root,
        HttpClient httpClient,
        string storeId = "epic-games",
        string gameId = "example-game",
        string title = "Example Game 2 - Windows")
    {
        return new OmniLibraryGamePageMetadataService(
            appId => appId == ShortcutAppId
                ? new UnifySteamGameDetailSnapshot(
                    1,
                    storeId,
                    new UnifySteamGameState(
                        gameId,
                        title,
                        Installed: true,
                        CloudPlayable: false,
                        SyncedToSteam: true,
                        "Installed",
                        string.Empty,
                        string.Empty,
                        @"C:\XboxGames\Example",
                        @"C:\XboxGames\Example\game.exe",
                        "1.0",
                        ShortcutAppId,
                        new UnifySteamDownloadState(
                            "completed",
                            100,
                            string.Empty,
                            0,
                            0,
                            0,
                            0,
                            0,
                            0,
                            1)))
                : new UnifySteamGameDetailSnapshot(1, string.Empty, null),
            Path.Combine(root, "metadata.json"),
            httpClient);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tfs-metadata-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MetadataHttpHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public bool FailMetadataRequests { get; set; }

        public string SearchJson { get; set; } =
            """{"total":1,"items":[{"id":990080,"name":"Example Game 2"}]}""";

        public string RequiredSearchTerm { get; set; } = string.Empty;

        public string? IgnHtml { get; set; }

        public string IgnSlug { get; set; } = string.Empty;

        public string? TranslateJson { get; set; }

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUris.Add(request.RequestUri!);
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.StartsWith("https://www.ign.com/games/", StringComparison.OrdinalIgnoreCase))
            {
                return IgnHtml is { } ignHtml &&
                       uri.EndsWith("/" + IgnSlug, StringComparison.OrdinalIgnoreCase)
                    ? Html(ignHtml)
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (uri.StartsWith("https://translate.googleapis.com/", StringComparison.OrdinalIgnoreCase))
            {
                return TranslateJson is { } translateJson
                    ? Json(translateJson)
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (uri.Contains("storesearch", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(RequiredSearchTerm) &&
                    !Uri.UnescapeDataString(request.RequestUri.Query)
                        .Contains(
                            $"term={RequiredSearchTerm}",
                            StringComparison.OrdinalIgnoreCase))
                {
                    return Json("""{"total":0,"items":[]}""");
                }
                return Json(SearchJson);
            }

            if (FailMetadataRequests)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.ServiceUnavailable));
            }

            if (uri.Contains("appdetails", StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {
                      "990080": {
                        "success": true,
                        "data": {
                          "short_description": "Cached summary.",
                          "detailed_description": "<p>A reliable <b>cached</b> description.</p>",
                          "header_image": "https://cdn.cloudflare.steamstatic.com/steam/apps/990080/header.jpg",
                          "developers": ["Test Studio"],
                          "publishers": ["Test Publisher"],
                          "genres": [{"description":"Action"}],
                          "categories": [{"description":"Full controller support"}],
                          "release_date": {"date":"1 Jan, 2026"},
                          "metacritic": {"score":88},
                          "screenshots": [
                            {
                              "id": 1,
                              "path_thumbnail": "https://cdn.cloudflare.steamstatic.com/shot-thumb.jpg",
                              "path_full": "https://cdn.cloudflare.steamstatic.com/shot.jpg"
                            }
                          ],
                          "movies": [
                            {
                              "name":"Launch Trailer",
                              "thumbnail":"https://cdn.cloudflare.steamstatic.com/trailer.jpg",
                              "mp4":{"max":"https://cdn.cloudflare.steamstatic.com/trailer.mp4"}
                            }
                          ]
                        }
                      }
                    }
                    """);
            }

            if (uri.Contains(
                    "displaycatalog.mp.microsoft.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {
                      "Products": [
                        {
                          "ProductId": "9NTESTPRODUCT",
                          "LocalizedProperties": [
                            {
                              "ProductTitle": "Example Game 2",
                              "ShortDescription": "Official Xbox summary.",
                              "ProductDescription": "Official Xbox description.",
                              "DeveloperName": "Xbox Studio",
                              "PublisherName": "Xbox Publishing",
                              "Images": [
                                {
                                  "Uri": "//store-images.s-microsoft.com/image/apps.1.test.jpg",
                                  "ImagePurpose": "Screenshot",
                                  "Caption": "Official screenshot",
                                  "Width": 1920,
                                  "Height": 1080
                                },
                                {
                                  "Uri": "//store-images.s-microsoft.com/image/apps.2.test.jpg",
                                  "ImagePurpose": "SuperHeroArt",
                                  "Width": 3840,
                                  "Height": 2160
                                }
                              ]
                            }
                          ],
                          "MarketProperties": [
                            {
                              "OriginalReleaseDate": "2026-07-28T15:00:00Z",
                              "UsageData": [
                                {
                                  "AggregateTimeSpan": "AllTime",
                                  "AverageRating": 4.5
                                }
                              ]
                            }
                          ],
                          "Properties": {
                            "Category": "Shooter",
                            "Attributes": [
                              { "Name": "SinglePlayer" },
                              { "Name": "PcGamePad" }
                            ]
                          }
                        }
                      ]
                    }
                    """);
            }

            if (uri.Contains("ISteamNews", StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {
                      "appnews": {
                        "newsitems": [
                          {
                            "gid":"news-1",
                            "title":"A stable update",
                            "url":"https://store.steampowered.com/news/app/990080/view/1",
                            "contents":"<p>Patch notes are available.</p>",
                            "date":1785369600,
                            "feedlabel":"Community Announcements",
                            "author":"Test Studio"
                          }
                        ]
                      }
                    }
                    """);
            }

            if (uri.Contains(
                    "steamcommunity.com/stats",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Html(
                    """
                    <html>
                      <body>
                        <div class="achieveRow ">
                          <div class="achieveImgHolder">
                            <img src="https://shared.fastly.steamstatic.com/community_assets/images/apps/990080/first.jpg" />
                          </div>
                          <div class="achieveTxtHolder">
                            <div class="achieveTxt">
                              <h3>First Steps</h3>
                              <h5>Complete the introduction.</h5>
                            </div>
                          </div>
                        </div>
                        <div class="achieveRow ">
                          <div class="achieveImgHolder">
                            <img src="https://shared.fastly.steamstatic.com/community_assets/images/apps/990080/second.jpg" />
                          </div>
                          <div class="achieveTxtHolder">
                            <div class="achieveTxt">
                              <h3>Explorer</h3>
                              <h5>Find the hidden path.</h5>
                            </div>
                          </div>
                        </div>
                      </body>
                    </html>
                    """);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string content)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }

        private static Task<HttpResponseMessage> Html(string content)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/html"),
            });
        }
    }
}
