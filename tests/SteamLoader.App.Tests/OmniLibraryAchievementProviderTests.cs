using System.Net;
using System.Text;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryAchievementProviderTests
{
    [Fact]
    public async Task Xbox_UsesCatalogTitleId_WithoutRequiringAccountLookup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            settings.Update(configuration =>
            {
                var xbox = configuration.UnifySteam.Stores["xbox-game-pass"];
                xbox.Enabled = true;
                xbox.AchievementsEnabled = true;
                xbox.OpenXblApiKey = "test-openxbl-key";
            });
            var handler = new AchievementHttpHandler();
            using var client = new HttpClient(handler);
            var provider = new OmniLibraryAchievementProvider(
                settings,
                client,
                Path.Combine(root, "epic-user.json"));

            var result = await provider.RefreshAsync(
                CreateGame(
                    "xbox-game-pass",
                    storeTitleId: "1777860928"),
                EmptyAchievements("OpenXBL"),
                string.Empty,
                refreshDefinitions: true,
                refreshProgress: true,
                CancellationToken.None);

            Assert.NotNull(result.Metadata);
            Assert.Equal("ready", result.Metadata.Status);
            Assert.Equal(1, result.Metadata.UnlockedCount);
            var achievement = Assert.Single(result.Metadata.Items);
            Assert.Equal("First Win", achievement.Name);
            Assert.True(achievement.Unlocked);
            Assert.Equal(10, achievement.CurrentProgress);
            Assert.Equal(10, achievement.TargetProgress);
            Assert.Contains(
                handler.Requests,
                request => request.RequestUri!.AbsolutePath.EndsWith(
                    "/api/v2/achievements/title/1777860928",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                handler.Requests,
                request => request.RequestUri!.AbsolutePath.Equals(
                    "/api/v2/account",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Xbox_UsesLocalMicrosoftGameConfigBeforeNetworkHistory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "MicrosoftGame.config"),
                "<Game><Identity><TitleId>69F80140</TitleId></Identity></Game>");
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            settings.Update(configuration =>
            {
                var xbox = configuration.UnifySteam.Stores["xbox-game-pass"];
                xbox.Enabled = true;
                xbox.AchievementsEnabled = true;
                xbox.OpenXblApiKey = "test-openxbl-key";
            });
            var handler = new AchievementHttpHandler();
            using var client = new HttpClient(handler);
            var provider = new OmniLibraryAchievementProvider(
                settings,
                client,
                Path.Combine(root, "epic-user.json"));

            var result = await provider.RefreshAsync(
                CreateGame("xbox-game-pass", installPath: root),
                EmptyAchievements("OpenXBL"),
                string.Empty,
                true,
                true,
                CancellationToken.None);

            Assert.Equal("ready", result.Metadata?.Status);
            Assert.Contains(
                handler.Requests,
                request => request.RequestUri!.AbsolutePath.EndsWith(
                    "/api/v2/achievements/title/1777860928",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                handler.Requests,
                request => request.RequestUri!.AbsolutePath.Contains(
                    "titleHistory",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Xbox_MissingCatalogTitleId_UsesAccountHistoryAndCachesExactMatch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            settings.Update(configuration =>
            {
                var xbox = configuration.UnifySteam.Stores["xbox-game-pass"];
                xbox.Enabled = true;
                xbox.AchievementsEnabled = true;
                xbox.OpenXblApiKey = "test-openxbl-key";
                xbox.OpenXblAccountId = "281463901";
                xbox.OpenXblAccountName = "TestPlayer";
            });
            var handler = new AchievementHttpHandler();
            using var client = new HttpClient(handler);
            var provider = new OmniLibraryAchievementProvider(
                settings,
                client,
                Path.Combine(root, "epic-user.json"));
            var game = CreateGame("xbox-game-pass");

            var result = await provider.RefreshAsync(
                game,
                EmptyAchievements("OpenXBL"),
                string.Empty,
                refreshDefinitions: true,
                refreshProgress: true,
                CancellationToken.None);

            Assert.Equal("ready", result.Metadata?.Status);
            Assert.Contains(
                handler.Requests,
                request => request.RequestUri!.AbsolutePath.Equals(
                    "/api/v2/player/titleHistory/281463901",
                    StringComparison.Ordinal));
            Assert.Equal(
                "1777860928",
                settings.Load()
                    .UnifySteam.Stores["xbox-game-pass"]
                    .OpenXblTitleIds["test-game"]);

            handler.Requests.Clear();
            result = await provider.RefreshAsync(
                game,
                EmptyAchievements("OpenXBL"),
                string.Empty,
                refreshDefinitions: true,
                refreshProgress: true,
                CancellationToken.None);

            Assert.Equal("ready", result.Metadata?.Status);
            Assert.DoesNotContain(
                handler.Requests,
                request => request.RequestUri!.AbsolutePath.Contains(
                    "titleHistory",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Xbox_RateLimit_ReturnsRetryWithoutReplacingMetadata()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            settings.Update(configuration =>
            {
                var xbox = configuration.UnifySteam.Stores["xbox-game-pass"];
                xbox.Enabled = true;
                xbox.AchievementsEnabled = true;
                xbox.OpenXblApiKey = "rate-limited-key";
                xbox.OpenXblAccountId = "281463901";
                xbox.OpenXblAccountName = "TestPlayer";
            });
            var handler = new AchievementHttpHandler
            {
                RateLimitXboxAchievements = true,
            };
            using var client = new HttpClient(handler);
            var provider = new OmniLibraryAchievementProvider(
                settings,
                client,
                Path.Combine(root, "epic-user.json"));

            var result = await provider.RefreshAsync(
                CreateGame(
                    "xbox-game-pass",
                    storeTitleId: "1777860928"),
                EmptyAchievements("OpenXBL"),
                string.Empty,
                refreshDefinitions: true,
                refreshProgress: true,
                CancellationToken.None);

            Assert.Null(result.Metadata);
            Assert.NotNull(result.RetryAfterUtc);
            Assert.Contains("rate-limited", result.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Xbox_MissingAchievementSet_IsAStableEmptyResult()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            settings.Update(configuration =>
            {
                var xbox = configuration.UnifySteam.Stores["xbox-game-pass"];
                xbox.Enabled = true;
                xbox.AchievementsEnabled = true;
                xbox.OpenXblApiKey = "test-openxbl-key";
                xbox.OpenXblAccountId = "281463901";
            });
            var handler = new AchievementHttpHandler
            {
                XboxAchievementsNotFound = true,
            };
            using var client = new HttpClient(handler);
            var provider = new OmniLibraryAchievementProvider(
                settings,
                client,
                Path.Combine(root, "epic-user.json"));

            var result = await provider.RefreshAsync(
                CreateGame(
                    "xbox-game-pass",
                    storeTitleId: "1777860928"),
                EmptyAchievements("OpenXBL"),
                string.Empty,
                refreshDefinitions: true,
                refreshProgress: true,
                CancellationToken.None);

            Assert.NotNull(result.Metadata);
            Assert.Equal("no-achievements", result.Metadata.Status);
            Assert.Empty(result.Metadata.Items);
            Assert.True(result.DefinitionsRefreshed);
            Assert.True(result.ProgressRefreshed);
            Assert.Null(result.RetryAfterUtc);
            Assert.Empty(result.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Epic_UsesNamespaceAndAccountToken_ThenMergesPersonalProgress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            settings.Update(configuration =>
            {
                var epic = configuration.UnifySteam.Stores["epic-games"];
                epic.Enabled = true;
                epic.AchievementsEnabled = true;
            });
            var epicCredentialPath = Path.Combine(root, "epic-user.json");
            await File.WriteAllTextAsync(
                epicCredentialPath,
                $$"""
                {
                  "access_token": "test-access-token",
                  "refresh_token": "test-refresh-token",
                  "account_id": "epic-account-1",
                  "displayName": "EpicTester",
                  "expires_in": 7200,
                  "loginTime": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}
                }
                """);
            var handler = new AchievementHttpHandler();
            using var client = new HttpClient(handler);
            var provider = new OmniLibraryAchievementProvider(
                settings,
                client,
                epicCredentialPath);

            var result = await provider.RefreshAsync(
                CreateGame(
                    "epic-games",
                    storeNamespace: "test-sandbox"),
                EmptyAchievements("Epic Games"),
                string.Empty,
                refreshDefinitions: true,
                refreshProgress: true,
                CancellationToken.None);

            Assert.NotNull(result.Metadata);
            Assert.Equal("ready", result.Metadata.Status);
            Assert.Equal(1, result.Metadata.UnlockedCount);
            var achievement = Assert.Single(result.Metadata.Items);
            Assert.Equal("Epic First Win", achievement.Name);
            Assert.True(achievement.Unlocked);
            Assert.Equal(
                2,
                handler.Requests.Count(request =>
                    request.RequestUri!.Host.Equals(
                        "launcher.store.epicgames.com",
                        StringComparison.OrdinalIgnoreCase)));
            Assert.All(
                handler.Requests.Where(request =>
                    request.RequestUri!.Host.Equals(
                        "launcher.store.epicgames.com",
                        StringComparison.OrdinalIgnoreCase)),
                request => Assert.Contains(
                    request.Headers.UserAgent,
                    value => value.Product?.Name?.Contains(
                        "EpicGamesLauncher",
                        StringComparison.OrdinalIgnoreCase) == true ||
                             value.Comment?.Contains(
                                 "EpicGamesLauncher",
                                 StringComparison.OrdinalIgnoreCase) == true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Epic_DemoSharingFullGameSandbox_DoesNotBorrowFullGameAchievements()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            settings.Update(configuration =>
            {
                var epic = configuration.UnifySteam.Stores["epic-games"];
                epic.Enabled = true;
                epic.AchievementsEnabled = true;
            });
            var handler = new AchievementHttpHandler();
            using var client = new HttpClient(handler);
            var provider = new OmniLibraryAchievementProvider(
                settings,
                client,
                Path.Combine(root, "epic-user.json"));

            var result = await provider.RefreshAsync(
                CreateGame(
                    "epic-games",
                    storeTitleId: "demo-catalog-item",
                    storeNamespace: "shared-full-game-sandbox",
                    title: "PC Building Simulator 2 - Demo"),
                EmptyAchievements("Epic Games"),
                string.Empty,
                refreshDefinitions: true,
                refreshProgress: true,
                CancellationToken.None);

            Assert.NotNull(result.Metadata);
            Assert.Equal("no-achievements", result.Metadata.Status);
            Assert.Empty(result.Metadata.Items);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static UnifySteamGameDetailSnapshot CreateGame(
        string storeId,
        string storeTitleId = "",
        string storeNamespace = "",
        string title = "Test Game",
        string installPath = "")
    {
        return new UnifySteamGameDetailSnapshot(
            1,
            storeId,
            new UnifySteamGameState(
                "test-game",
                title,
                Installed: true,
                CloudPlayable: false,
                SyncedToSteam: true,
                "Installed",
                string.Empty,
                string.Empty,
                installPath,
                string.Empty,
                "1.0",
                0x8abc1234,
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
                    1))
            {
                StoreTitleId = storeTitleId,
                StoreNamespace = storeNamespace,
            });
    }

    private static OmniLibraryAchievementMetadata EmptyAchievements(
        string provider) =>
        new(
            provider,
            "empty",
            string.Empty,
            0,
            0,
            []);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tfs-achievement-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AchievementHttpHandler : HttpMessageHandler
    {
        public bool RateLimitXboxAchievements { get; set; }

        public bool XboxAchievementsNotFound { get; set; }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(
                request.Method,
                request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            Requests.Add(clone);

            var path = request.RequestUri!.AbsolutePath;
            if (path.Equals("/api/v2/account", StringComparison.Ordinal))
            {
                return Json(
                    """
                    {
                      "profileUsers": [
                        {
                          "id": "281463901",
                          "settings": [
                            { "id": "Gamertag", "value": "TestPlayer" }
                          ]
                        }
                      ]
                    }
                    """);
            }
            if (path.Contains(
                    "/api/v2/player/titleHistory/",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {
                      "titles": [
                        {
                          "titleId": "1777860928",
                          "name": "Test Game - Standard Edition",
                          "productId": "test-game"
                        }
                      ]
                    }
                    """);
            }
            if (path.Contains(
                    "/api/v2/achievements/title/",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (RateLimitXboxAchievements)
                {
                    var response = new HttpResponseMessage(
                        HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter =
                        new System.Net.Http.Headers.RetryConditionHeaderValue(
                            TimeSpan.FromMinutes(10));
                    return response;
                }
                if (XboxAchievementsNotFound)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                return Json(
                    """
                    {
                      "achievements": [
                        {
                          "id": "xbox-achievement-1",
                          "name": "First Win",
                          "description": "Win once.",
                          "progressState": "Achieved",
                          "progression": {
                            "timeUnlocked": "2026-07-31T10:00:00Z",
                            "requirements": [
                              { "current": "10", "target": "10" }
                            ]
                          },
                          "mediaAssets": [
                            {
                              "type": "Icon",
                              "url": "https://images-eds-ssl.xboxlive.com/test.png"
                            }
                          ]
                        }
                      ]
                    }
                    """);
            }
            if (request.RequestUri.Host.Equals(
                    "launcher.store.epicgames.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                var body = await request.Content!.ReadAsStringAsync(
                    cancellationToken);
                if (body.Contains(
                        "productAchievementsRecordBySandbox",
                        StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {
                          "data": {
                            "Achievement": {
                              "productAchievementsRecordBySandbox": {
                                "productId": "epic-product-1",
                                "sandboxId": "test-sandbox",
                                "achievements": [
                                  {
                                    "achievement": {
                                      "name": "EPIC_FIRST_WIN",
                                      "hidden": false,
                                      "unlockedDisplayName": "Epic First Win",
                                      "lockedDisplayName": "Hidden",
                                      "unlockedDescription": "Win once.",
                                      "lockedDescription": "Keep playing.",
                                      "unlockedIconLink": "https://cdn1.epicgames.com/achievement.png",
                                      "lockedIconLink": "https://cdn1.epicgames.com/achievement-locked.png",
                                      "XP": 10
                                    }
                                  }
                                ]
                              }
                            }
                          }
                        }
                        """);
                }
                return Json(
                    """
                    {
                      "data": {
                        "PlayerProfile": {
                          "playerProfile": {
                            "productAchievements": {
                              "data": {
                                "totalXP": 10,
                                "totalUnlocked": 1,
                                "playerAchievements": [
                                  {
                                    "playerAchievement": {
                                      "achievementName": "EPIC_FIRST_WIN",
                                      "progress": 1,
                                      "unlocked": true,
                                      "unlockDate": "2026-07-31T10:00:00.000Z",
                                      "XP": 10
                                    }
                                  }
                                ]
                              }
                            }
                          }
                        }
                      }
                    }
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
