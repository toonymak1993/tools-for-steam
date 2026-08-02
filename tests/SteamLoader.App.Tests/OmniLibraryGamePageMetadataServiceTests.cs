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
            Assert.Equal(2, handler.CallCount);
            Assert.All(
                handler.RequestUris,
                uri => Assert.Contains("storesearch", uri.AbsoluteUri));
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

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUris.Add(request.RequestUri!);
            var uri = request.RequestUri!.AbsoluteUri;
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
