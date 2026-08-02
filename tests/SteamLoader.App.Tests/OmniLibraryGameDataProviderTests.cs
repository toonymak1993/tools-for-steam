using System.Net;
using System.Text;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryGameDataProviderTests
{
    [Fact]
    public void Registry_PrefersDeliveryProviderOverOuterStore()
    {
        var provider = OmniLibraryGameDataProviderRegistry.ResolveForStore(
            "epic-games",
            "ea-app");

        Assert.NotNull(provider);
        Assert.Equal("ea", provider.Id);
    }

    [Theory]
    [InlineData("origin", "ea")]
    [InlineData("blizzard", "battle-net")]
    [InlineData("uplay", "ubisoft-connect")]
    [InlineData("playstation", "playstation-network")]
    [InlineData("android", "google-play-games")]
    public void Registry_ResolvesStableAliasesForFutureStores(
        string storeId,
        string expectedProviderId)
    {
        var provider = OmniLibraryGameDataProviderRegistry.ResolveForStore(storeId);

        Assert.NotNull(provider);
        Assert.Equal(expectedProviderId, provider.Id);
    }

    [Fact]
    public void Registry_ContainsStoreNetworkAndEmulatorProviders()
    {
        var ids = OmniLibraryGameDataProviderRegistry.Ids;

        Assert.Contains("xbox-live", ids);
        Assert.Contains("epic-games", ids);
        Assert.Contains("gog", ids);
        Assert.Contains("battle-net", ids);
        Assert.Contains("ea", ids);
        Assert.Contains("playstation-network", ids);
        Assert.Contains("retroachievements", ids);
        Assert.Contains("rpcs3", ids);
        Assert.Contains("shadps4", ids);
        Assert.Contains("xenia", ids);
        Assert.True(OmniLibraryGameDataProviderRegistry.GetRequired("ea").RuntimeAvailable);
        Assert.True(OmniLibraryGameDataProviderRegistry.GetRequired("battle-net").RuntimeAvailable);
        Assert.True(OmniLibraryGameDataProviderRegistry.GetRequired("ffxiv").RuntimeAvailable);
        Assert.False(OmniLibraryGameDataProviderRegistry.GetRequired("apple-game-center").RuntimeAvailable);
    }

    [Fact]
    public void Registry_MapsTheLocalRomLibraryToRetroAchievements()
    {
        var provider = OmniLibraryGameDataProviderRegistry.ResolveForStore(
            OmniLibraryRomSystemRegistry.StoreId);

        Assert.NotNull(provider);
        Assert.Equal("retroachievements", provider.Id);
    }

    [Fact]
    public async Task RetroAchievements_UsesExactMappedIdAndParsesProgress()
    {
        var handler = new RetroAchievementsHandler();
        using var client = new HttpClient(handler);
        var source = new OmniLibraryRetroAchievementsSource(client);
        var provider = new OmniLibraryGameDataProviderConfiguration
        {
            Enabled = true,
            AccountName = "test-user",
            Credential = "test-key",
            GameIdOverrides = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["local-game"] = "1234",
            },
        };
        var game = new UnifySteamGameState(
            "local-game",
            "Retro Test",
            true,
            false,
            true,
            "Installed",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "1",
            123,
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
                1));
        var context = new OmniLibraryAchievementSourceContext(
            new UnifySteamGameDetailSnapshot(1, "epic-games", game),
            new UnifySteamStoreConfiguration(),
            provider,
            EmptyAchievements(),
            string.Empty,
            true,
            true);

        var result = await source.RefreshAsync(context, CancellationToken.None);

        Assert.Equal("ready", result.Metadata?.Status);
        Assert.Equal(1, result.Metadata?.UnlockedCount);
        var achievement = Assert.Single(result.Metadata!.Items);
        Assert.Equal("First Badge", achievement.Name);
        Assert.True(achievement.Unlocked);
        Assert.Contains("g=1234", handler.RequestUri?.Query);
        Assert.Contains("u=test-user", handler.RequestUri?.Query);
    }

    [Fact]
    public async Task RetroAchievements_IdentifiesRomByOfficialContentHashAndCachesMapping()
    {
        var romPath = Path.Combine(
            Path.GetTempPath(),
            $"tfs-ra-rom-{Guid.NewGuid():N}.iso");
        await File.WriteAllBytesAsync(romPath, [1, 2, 3, 4]);
        try
        {
            var handler = new AutomaticRetroAchievementsHandler();
            using var client = new HttpClient(handler);
            var persistedMapping = string.Empty;
            var hashCalls = 0;
            var source = new OmniLibraryRetroAchievementsSource(
                client,
                (_, gameId, mappedId) =>
                {
                    Assert.Equal("local-rom", gameId);
                    persistedMapping = mappedId;
                },
                (platformId, path, _) =>
                {
                    hashCalls++;
                    Assert.Equal("psp", platformId);
                    Assert.Equal(romPath, path);
                    return Task.FromResult("42c08dd581fca2db6dbe3cbe3ef6703a");
                });
            var provider = new OmniLibraryGameDataProviderConfiguration
            {
                Enabled = true,
                AccountName = "test-user",
                Credential = "test-key",
            };
            var game = CreateGame("local-rom", "God of War") with
            {
                PlatformId = "psp",
                PlatformTitle = "PSP",
                RomPath = romPath,
            };

            var first = await source.RefreshAsync(
                CreateContext(OmniLibraryRomSystemRegistry.StoreId, game, provider),
                CancellationToken.None);

            Assert.Equal("ready", first.Metadata?.Status);
            Assert.Equal(1, hashCalls);
            Assert.StartsWith("rahash:v1:", persistedMapping);
            Assert.EndsWith(":3538", persistedMapping);
            Assert.Equal(1, handler.ResolveHashRequests);

            provider.GameIdOverrides[game.Id] = persistedMapping;
            var second = await source.RefreshAsync(
                CreateContext(OmniLibraryRomSystemRegistry.StoreId, game, provider),
                CancellationToken.None);

            Assert.Equal("ready", second.Metadata?.Status);
            Assert.Equal(1, hashCalls);
            Assert.Equal(1, handler.ResolveHashRequests);
        }
        finally
        {
            File.Delete(romPath);
        }
    }

    [Fact]
    public void SettingsStore_ProtectsGenericProviderCredentialsAtRest()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tfs-game-data-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "store-sync.json");
            var settings = new StoreSyncSettingsStore(path);
            settings.Update(configuration =>
            {
                var provider = configuration.UnifySteam.GameData
                    .Providers["retroachievements"];
                provider.Enabled = true;
                provider.AccountName = "test-user";
                provider.Credential = "super-secret-api-key";
            });

            Assert.DoesNotContain(
                "super-secret-api-key",
                File.ReadAllText(path),
                StringComparison.Ordinal);
            Assert.Equal(
                "super-secret-api-key",
                settings.Load().UnifySteam.GameData
                    .Providers["retroachievements"].Credential);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gog_ResolvesClientIdOnceAndUsesConnectedAccountProgress()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tfs-gog-achievements-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var authPath = Path.Combine(root, "auth.json");
            await File.WriteAllTextAsync(
                authPath,
                $$"""
                {
                  "gog": {
                    "access_token": "gog-access",
                    "refresh_token": "gog-refresh",
                    "user_id": "gog-user",
                    "expires_in": 7200,
                    "loginTime": {{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}
                  }
                }
                """);
            var settings = new StoreSyncSettingsStore(
                Path.Combine(root, "store-sync.json"));
            var providerConfig = settings.Update(configuration =>
            {
                configuration.UnifySteam.GameData.Providers["gog"].Enabled = true;
                configuration.UnifySteam.Stores["gog-galaxy"].AchievementsEnabled = true;
            }).UnifySteam.GameData.Providers["gog"];
            var handler = new GogHandler();
            using var client = new HttpClient(handler);
            var source = new OmniLibraryGogAchievementSource(
                client,
                settings,
                authPath);
            var game = new UnifySteamGameState(
                "777",
                "GOG Test",
                true,
                false,
                true,
                "Installed",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "1",
                777,
                new UnifySteamDownloadState(
                    "completed", 100, string.Empty,
                    0, 0, 0, 0, 0, 0, 1));
            var context = new OmniLibraryAchievementSourceContext(
                new UnifySteamGameDetailSnapshot(1, "gog-galaxy", game),
                new UnifySteamStoreConfiguration(),
                providerConfig,
                EmptyAchievements(),
                string.Empty,
                true,
                true);

            var result = await source.RefreshAsync(context, CancellationToken.None);

            Assert.Equal("ready", result.Metadata?.Status);
            Assert.Equal(1, result.Metadata?.UnlockedCount);
            Assert.Equal(
                "client-777",
                settings.Load().UnifySteam.GameData.Providers["gog"]
                    .GameIdOverrides["777"]);
            Assert.Contains(
                handler.Requests,
                uri => uri.Host.Equals("www.gogdb.org", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                handler.Requests,
                uri => uri.AbsolutePath.Contains(
                    "/clients/client-777/users/gog-user/achievements",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Ea_ResolvesExactOwnedOfferAndParsesUserProgress()
    {
        var handler = new EaHandler();
        using var client = new HttpClient(handler);
        var source = new OmniLibraryEaAchievementSource(client, settingsStore: null);
        var provider = new OmniLibraryGameDataProviderConfiguration
        {
            Enabled = true,
            Credential = "ea-access-token",
        };
        var game = CreateGame("ea-local", "EA Test Game") with
        {
            DeliveryProvider = "ea-app",
        };
        var result = await source.RefreshAsync(
            CreateContext("epic-games", game, provider),
            CancellationToken.None);

        Assert.Equal("ready", result.Metadata?.Status);
        Assert.Equal(1, result.Metadata?.UnlockedCount);
        Assert.Equal(2, result.Metadata?.TotalCount);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Ffxiv_SharesCatalogAndMapsCharacterUnlocks()
    {
        var handler = new FfxivHandler();
        using var client = new HttpClient(handler);
        var source = new OmniLibraryFfxivAchievementSource(client);
        var provider = new OmniLibraryGameDataProviderConfiguration
        {
            Enabled = true,
            AccountId = "12345678",
        };
        var context = CreateContext(
            "ffxiv",
            CreateGame("ffxiv", "Final Fantasy XIV"),
            provider);

        var first = await source.RefreshAsync(context, CancellationToken.None);
        var second = await source.RefreshAsync(context, CancellationToken.None);

        Assert.Equal("ready", first.Metadata?.Status);
        Assert.Equal(1, first.Metadata?.UnlockedCount);
        Assert.Equal(2, first.Metadata?.TotalCount);
        Assert.Equal(1, handler.CatalogRequestCount);
        Assert.Equal(2, handler.CharacterRequestCount);
        Assert.Contains("format=png", first.Metadata!.Items[0].IconUrl);
        Assert.Contains("Test Character", first.Metadata.DetailText);
    }

    [Fact]
    public async Task Psn_RefreshesTokenAndUsesExactTrophyTitleMapping()
    {
        var handler = new PsnHandler();
        using var client = new HttpClient(handler);
        var source = new OmniLibraryPsnAchievementSource(client, settingsStore: null);
        var provider = new OmniLibraryGameDataProviderConfiguration
        {
            Enabled = true,
            SecondaryCredential = "psn-refresh-token",
        };
        var result = await source.RefreshAsync(
            CreateContext(
                "playstation-network",
                CreateGame("psn-local", "PSN Test Game"),
                provider),
            CancellationToken.None);

        Assert.Equal("ready", result.Metadata?.Status);
        Assert.Equal(1, result.Metadata?.UnlockedCount);
        Assert.Equal(2, result.Metadata?.TotalCount);
        Assert.Contains(handler.Requests, request => request.Contains("/oauth/token"));
        Assert.Contains(handler.Requests, request => request.Contains("/users/me/trophyTitles"));
        Assert.Contains(handler.Requests, request => request.Contains("NPWR12345_00"));
    }

    [Fact]
    public async Task BattleNet_Sc2CombinesCachedDefinitionsWithProfileProgress()
    {
        var handler = new BattleNetHandler();
        using var client = new HttpClient(handler);
        var source = new OmniLibraryBattleNetAchievementSource(client);
        var provider = new OmniLibraryGameDataProviderConfiguration
        {
            Enabled = true,
            Credential = "client-id",
            SecondaryCredential = "client-secret",
            AccountName = "2/1/123456",
            Region = "eu",
            Locale = "en_US",
        };
        var context = CreateContext(
            "battle-net",
            CreateGame("sc2", "StarCraft II"),
            provider);

        var first = await source.RefreshAsync(context, CancellationToken.None);
        var second = await source.RefreshAsync(context, CancellationToken.None);

        Assert.Equal("ready", first.Metadata?.Status);
        Assert.Equal(1, first.Metadata?.UnlockedCount);
        Assert.Equal(2, first.Metadata?.TotalCount);
        Assert.Equal(1, handler.TokenRequestCount);
        Assert.Equal(1, handler.DefinitionRequestCount);
        Assert.Equal(2, handler.ProfileRequestCount);
        Assert.Contains("SC2 User", first.Metadata!.DetailText);
        Assert.Equal("ready", second.Metadata?.Status);
    }

    private static UnifySteamGameState CreateGame(string id, string title) =>
        new(
            id,
            title,
            true,
            false,
            true,
            "Installed",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "1",
            123,
            new UnifySteamDownloadState(
                "completed", 100, string.Empty,
                0, 0, 0, 0, 0, 0, 1));

    private static OmniLibraryAchievementSourceContext CreateContext(
        string storeId,
        UnifySteamGameState game,
        OmniLibraryGameDataProviderConfiguration provider) =>
        new(
            new UnifySteamGameDetailSnapshot(1, storeId, game),
            new UnifySteamStoreConfiguration(),
            provider,
            EmptyAchievements(),
            string.Empty,
            true,
            true);

    private static OmniLibraryAchievementMetadata EmptyAchievements() =>
        new("RetroAchievements", "empty", string.Empty, 0, 0, []);

    private sealed class RetroAchievementsHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "Achievements": {
                        "1": {
                          "ID": 1,
                          "Title": "First Badge",
                          "Description": "Finish the first stage.",
                          "BadgeName": "12345",
                          "DateEarned": "2026-07-31 10:00:00"
                        }
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class AutomaticRetroAchievementsHandler : HttpMessageHandler
    {
        public int ResolveHashRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                ResolveHashRequests++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("r=gameid", body);
                Assert.Contains("m=42c08dd581fca2db6dbe3cbe3ef6703a", body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"Success\":true,\"GameID\":3538}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            Assert.Contains("g=3538", request.RequestUri?.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"Achievements":{"1":{"ID":1,"Title":"Ready","Description":"Done","BadgeName":"1","DateEarned":"2026-08-01 10:00:00"}}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class GogHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var json = request.RequestUri!.Host.Equals(
                "www.gogdb.org",
                StringComparison.OrdinalIgnoreCase)
                ? """
                  {
                    "id": 777,
                    "client_id": "client-777",
                    "builds": []
                  }
                  """
                : """
                  {
                    "items": [
                      {
                        "achievement_key": "GOG_FIRST",
                        "name": "GOG First",
                        "description": "Finish once.",
                        "image_url_unlocked": "https://images.gog.com/unlocked.png",
                        "image_url_locked": "https://images.gog.com/locked.png",
                        "date_unlocked": "2026-07-31T10:00:00Z",
                        "visible": true
                      }
                    ]
                  }
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class EaHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = body.Contains("ownedGameProducts", StringComparison.Ordinal)
                ? """
                  {"data":{"me":{"ownedGameProducts":{"items":[
                    {"originOfferId":"OFFER-1","product":{"name":"EA Test Game","baseItem":{"gameType":"BASE_GAME"}}}
                  ]}}}}
                  """
                : body.Contains("GetAchievements", StringComparison.Ordinal)
                    ? """
                      {"data":{"achievements":[{"id":"set","achievements":[
                        {"id":"a1","name":"Unlocked","description":"Done","awardCount":1,"date":"2026-07-31T10:00:00Z"},
                        {"id":"a2","name":"Locked","description":"Later","awardCount":0}
                      ]}]}}
                      """
                    : """
                      {"data":{"me":{"player":{"pd":"1","psd":"player-sub","displayName":"EA User"}}}}
                      """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FfxivHandler : HttpMessageHandler
    {
        public int CatalogRequestCount { get; private set; }

        public int CharacterRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isCharacter = request.RequestUri!.AbsolutePath.Contains(
                "/characters/",
                StringComparison.Ordinal);
            if (isCharacter)
            {
                CharacterRequestCount++;
            }
            else
            {
                CatalogRequestCount++;
            }
            var json = isCharacter
                ? """
                  {"id":12345678,"name":"Test Character","achievements":{"public":true,"obtained":[
                    {"id":1,"time":"2026-07-31T10:00:00Z"}
                  ]}}
                  """
                : """
                  {"results":[
                    {"id":1,"name":"First","description":"First description","icon":"https://example.test/1?format=webp"},
                    {"id":2,"name":"Second","description":"Second description","icon":"https://example.test/2?format=webp"}
                  ]}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class PsnHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            string json;
            if (url.Contains("/oauth/token", StringComparison.Ordinal))
            {
                json = """
                  {"access_token":"psn-access","refresh_token":"psn-refresh-2","expires_in":3600}
                  """;
            }
            else if (url.Contains("/users/me/trophyTitles", StringComparison.Ordinal))
            {
                json = """
                  {"trophyTitles":[{"npCommunicationId":"NPWR12345_00","trophyTitleName":"PSN Test Game"}]}
                  """;
            }
            else if (url.Contains("/users/me/npCommunicationIds/", StringComparison.Ordinal))
            {
                json = """
                  {"trophies":[
                    {"trophyGroupId":"default","trophyId":1,"earned":true,"earnedDateTime":"2026-07-31T10:00:00Z"}
                  ]}
                  """;
            }
            else
            {
                json = """
                  {"trophies":[
                    {"trophyGroupId":"default","trophyId":1,"trophyName":"First Trophy","trophyDetail":"Done","trophyIconUrl":"https://example.test/1.png","hidden":false},
                    {"trophyGroupId":"default","trophyId":2,"trophyName":"Second Trophy","trophyDetail":"Later","trophyIconUrl":"https://example.test/2.png","hidden":false}
                  ]}
                  """;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class BattleNetHandler : HttpMessageHandler
    {
        public int TokenRequestCount { get; private set; }
        public int DefinitionRequestCount { get; private set; }
        public int ProfileRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            string json;
            if (url.Contains("/oauth/token", StringComparison.Ordinal))
            {
                TokenRequestCount++;
                json = """{"access_token":"battle-token","expires_in":3600}""";
            }
            else if (url.Contains("/data/achievements/", StringComparison.Ordinal))
            {
                DefinitionRequestCount++;
                json = """
                  {"achievements":[
                    {"id":"1","title":"SC2 First","description":"Done","imageUrl":"https://example.test/sc2-1.png"},
                    {"id":"2","title":"SC2 Second","description":"Later","imageUrl":"https://example.test/sc2-2.png"}
                  ]}
                  """;
            }
            else
            {
                ProfileRequestCount++;
                json = """
                  {"summary":{"displayName":"SC2 User"},"earnedAchievements":[
                    {"achievementId":"1","completionDate":"2026-07-31T10:00:00Z","isComplete":true}
                  ]}
                  """;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
