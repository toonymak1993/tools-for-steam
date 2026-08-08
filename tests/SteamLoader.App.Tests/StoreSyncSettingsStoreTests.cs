using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncSettingsStoreTests
{
    [Fact]
    public void Load_NormalizesEpicDownloadOptionsAndKeepsSafeDefaults()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            File.WriteAllText(settingsPath, """
                {
                  "unifySteam": {
                    "stores": {
                      "epic-games": {
                        "enabled": true,
                        "downloadWorkers": 99,
                        "downloadTimeoutSeconds": 5
                      },
                      "gog-galaxy": {
                        "enabled": false
                      }
                    }
                  }
                }
                """);
            var store = new StoreSyncSettingsStore(settingsPath);

            var configuration = store.Load();

            var epic = configuration.UnifySteam.Stores["epic-games"];
            Assert.Equal(32, epic.DownloadWorkers);
            Assert.Equal(15, epic.DownloadTimeoutSeconds);
            var gog = configuration.UnifySteam.Stores["gog-galaxy"];
            Assert.Equal(16, gog.DownloadWorkers);
            Assert.Equal(60, gog.DownloadTimeoutSeconds);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_MigratesLegacyConfigurationToNewSyncBehaviorDefaults()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            File.WriteAllText(settingsPath, """
                {
                  "steamGridDbApiKey": "",
                  "downloadArtwork": true,
                  "preferAnimatedArtwork": false,
                  "closeSteamBeforeSync": false,
                  "backupShortcuts": true,
                  "launchBigPictureAfterSync": true,
                  "syncBehaviorVersion": 1,
                  "stores": {
                    "epic-games": {
                      "enabled": true
                    }
                  }
                }
                """);

            var store = new StoreSyncSettingsStore(settingsPath);

            var configuration = store.Load();

            Assert.Equal(3, configuration.SyncBehaviorVersion);
            Assert.True(configuration.TakeOverExistingShortcuts);
            Assert.True(configuration.CleanupMissingTitles);
            Assert.NotNull(configuration.TitleOverrides);
            Assert.NotNull(configuration.Manifest);
            Assert.NotNull(configuration.ArtworkMatchCache);
            Assert.True(configuration.Stores.ContainsKey("ea-app"));
            Assert.True(configuration.Stores.ContainsKey("custom-locations"));
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void SaveAndLoad_PreservesOverridesManifestAndArtworkCache()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var store = new StoreSyncSettingsStore(settingsPath);
            var configuration = new StoreSyncConfiguration
            {
                TakeOverExistingShortcuts = true,
                CleanupMissingTitles = true,
                TitleOverrides = new Dictionary<string, StoreSyncTitleOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["epic-games-abc123"] = new()
                    {
                        Excluded = true,
                        TitleOverride = "My Steam Title",
                        ArtworkTitleOverride = "My Artwork Title",
                    },
                },
                Manifest = new Dictionary<string, StoreSyncManifestEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["epic-games-abc123"] = new()
                    {
                        TitleId = "epic-games-abc123",
                        StoreId = "epic-games",
                        StoreItemId = "epic|namespace|item",
                        Title = "Original Title",
                        EffectiveTitle = "My Steam Title",
                        ExecutablePath = @"D:\Games\Original\Game.exe",
                        AppId = 0x81234567,
                        ManagedShortcut = true,
                        AdoptedExistingShortcut = false,
                        LastAction = "Create",
                        LastDetail = "A new managed shortcut will be created.",
                        SteamGridDbGameId = 42,
                        ArtworkTitle = "My Artwork Title",
                        ArtworkLocked = true,
                        LastSeenAtUtc = DateTimeOffset.UtcNow,
                        LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                    },
                },
                ArtworkMatchCache = new Dictionary<string, StoreSyncArtworkCacheEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["epic-games-abc123"] = new()
                    {
                        GameId = 42,
                        MatchName = "My Artwork Title",
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    },
                },
            };

            store.Save(configuration);
            var reloaded = store.Load();

            var overrideState = Assert.Single(reloaded.TitleOverrides);
            Assert.True(overrideState.Value.Excluded);
            Assert.Equal("My Steam Title", overrideState.Value.TitleOverride);
            Assert.Equal("My Artwork Title", overrideState.Value.ArtworkTitleOverride);

            var manifestEntry = Assert.Single(reloaded.Manifest);
            Assert.Equal((uint)0x81234567, manifestEntry.Value.AppId);
            Assert.True(manifestEntry.Value.ManagedShortcut);
            Assert.Equal("epic|namespace|item", manifestEntry.Value.StoreItemId);
            Assert.Equal(42, manifestEntry.Value.SteamGridDbGameId);
            Assert.True(manifestEntry.Value.ArtworkLocked);

            var artworkCache = Assert.Single(reloaded.ArtworkMatchCache);
            Assert.Equal(42, artworkCache.Value.GameId);
            Assert.Equal("My Artwork Title", artworkCache.Value.MatchName);

            var persistedJson = File.ReadAllText(settingsPath);
            Assert.Contains("titleOverrides", persistedJson, StringComparison.Ordinal);
            Assert.Contains("artworkMatchCache", persistedJson, StringComparison.Ordinal);
            Assert.Contains("manifest", persistedJson, StringComparison.Ordinal);
            Assert.Contains("storeItemId", persistedJson, StringComparison.Ordinal);
            Assert.Contains("artworkLocked", persistedJson, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_InvalidJsonFallsBackToDefaultConfiguration()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            File.WriteAllText(settingsPath, "{ this-is-not-valid-json");
            var store = new StoreSyncSettingsStore(settingsPath);

            var configuration = store.Load();

            Assert.True(configuration.TakeOverExistingShortcuts);
            Assert.True(configuration.CleanupMissingTitles);
            Assert.Empty(configuration.TitleOverrides);
            Assert.Empty(configuration.Manifest);
            Assert.Empty(configuration.ArtworkMatchCache);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_XboxCloudDefaultsOffAndPersistsIndependentChoices()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            File.WriteAllText(settingsPath, """
                {
                  "unifySteam": {
                    "stores": {
                      "xbox-game-pass": {
                        "enabled": true,
                        "includeXboxCloudGaming": true
                      }
                    }
                  }
                }
                """);
            var store = new StoreSyncSettingsStore(settingsPath);

            var configuration = store.Load();
            var xbox = configuration.UnifySteam.Stores["xbox-game-pass"];

            Assert.Equal(6, configuration.OmniLibrarySettingsVersion);
            Assert.True(xbox.IncludeXboxPcGamePass);
            Assert.False(xbox.IncludeXboxCloudGaming);

            xbox.IncludeXboxPcGamePass = false;
            xbox.IncludeXboxCloudGaming = true;
            xbox.Cache.Games =
            [
                new UnifySteamGameCacheEntry
                {
                    Id = "9TESTCLOUD",
                    Title = "Cloud Test",
                    CloudPlayable = true,
                },
            ];
            store.Save(configuration);

            var reloaded = store.Load().UnifySteam.Stores["xbox-game-pass"];
            Assert.False(reloaded.IncludeXboxPcGamePass);
            Assert.True(reloaded.IncludeXboxCloudGaming);
            Assert.True(Assert.Single(reloaded.Cache.Games).CloudPlayable);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_MigratesArtworkBlockedXboxAndEpicStoresToReadyBackgroundRepair()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            File.WriteAllText(settingsPath, """
                {
                  "omniLibrarySettingsVersion": 1,
                  "unifySteam": {
                    "stores": {
                      "xbox-game-pass": {
                        "enabled": true,
                        "preparationStatus": "artwork",
                        "preparationDetail": "Preparing artwork 1/1. The store tab stays hidden.",
                        "cache": {
                          "refreshedAtUtc": "2026-07-27T20:00:00Z",
                          "games": [
                            {
                              "id": "9XBOXTEST",
                              "title": "Xbox Test",
                              "steamAppId": 3000000001
                            }
                          ]
                        }
                      },
                      "epic-games": {
                        "enabled": true,
                        "preparationStatus": "failed",
                        "preparationDetail": "Artwork preparation paused: hero missing.",
                        "cache": {
                          "refreshedAtUtc": "2026-07-27T20:01:00Z",
                          "games": [
                            {
                              "id": "EpicTest",
                              "title": "Epic Test",
                              "steamAppId": 3000000002
                            }
                          ]
                        }
                      }
                    }
                  }
                }
                """);

            var configuration = new StoreSyncSettingsStore(settingsPath).Load();

            Assert.Equal(6, configuration.OmniLibrarySettingsVersion);
            foreach (var storeId in new[] { "xbox-game-pass", "epic-games" })
            {
                var store = configuration.UnifySteam.Stores[storeId];
                Assert.Equal("prepared", store.PreparationStatus);
                Assert.NotNull(store.PreparedAtUtc);
                Assert.NotEmpty(store.PreparedCatalogSignature);
                Assert.StartsWith("Library ready.", store.PreparationDetail);
                Assert.Equal("ready", store.Lifecycle.Artwork);
                Assert.NotEqual("failed", store.Lifecycle.Shortcuts);
            }
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_FreshConfiguration_LeavesEveryOmniLibraryStoreDisabled()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var configuration = new StoreSyncSettingsStore(settingsPath).Load();

            Assert.False(configuration.UnifySteam.Stores["xbox-game-pass"].Enabled);
            Assert.False(configuration.UnifySteam.Stores["epic-games"].Enabled);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void SaveAndLoad_NormalizesAndDeduplicatesAdditionalScanPaths()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var store = new StoreSyncSettingsStore(settingsPath);
            var configuration = new StoreSyncConfiguration
            {
                Stores = new Dictionary<string, StoreSyncStoreConfiguration>(StringComparer.OrdinalIgnoreCase)
                {
                    ["custom-locations"] = new()
                    {
                        Enabled = true,
                        ScanPath = @" D:\Games\Primary ",
                        AdditionalScanPaths =
                        [
                            @" D:\Games\Extra ",
                            @"d:\games\extra",
                            @"E:\Portable Library\"
                        ],
                    },
                },
            };

            store.Save(configuration);
            var reloaded = store.Load();

            Assert.True(reloaded.Stores.TryGetValue("custom-locations", out var customStore));
            Assert.NotNull(customStore);

            Assert.Equal(Path.GetFullPath(@"D:\Games\Primary"), customStore.ScanPath);
            Assert.Equal(2, customStore.AdditionalScanPaths.Count);
            Assert.Equal(Path.GetFullPath(@"D:\Games\Extra"), customStore.AdditionalScanPaths[0]);
            Assert.Equal(Path.GetFullPath(@"E:\Portable Library\"), customStore.AdditionalScanPaths[1]);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Update_PreservesConcurrentNarrowChanges()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var store = new StoreSyncSettingsStore(settingsPath);
            store.Save(new StoreSyncConfiguration());

            Parallel.For(0, 24, index =>
            {
                new StoreSyncSettingsStore(settingsPath).Update(configuration =>
                {
                    configuration.TitleOverrides[$"title-{index}"] = new StoreSyncTitleOverride
                    {
                        TitleOverride = $"Title {index}",
                    };
                });
            });

            var reloaded = store.Load();
            Assert.Equal(24, reloaded.TitleOverrides.Count);
            for (var index = 0; index < 24; index++)
            {
                Assert.Equal(
                    $"Title {index}",
                    reloaded.TitleOverrides[$"title-{index}"].TitleOverride);
            }
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_UsesLastValidBackupWhenPrimaryFileIsDamaged()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var store = new StoreSyncSettingsStore(settingsPath);
            var first = new StoreSyncConfiguration { SteamGridDbApiKey = "last-valid" };
            store.Save(first);
            store.Update(configuration => configuration.SteamGridDbApiKey = "newest");
            File.WriteAllText(settingsPath, "{ damaged");

            var recovered = store.Load();

            Assert.Equal("last-valid", recovered.SteamGridDbApiKey);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_NormalizesGogRemoteMembershipForDeltaSync()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            File.WriteAllText(settingsPath, """
                {
                  "unifySteam": {
                    "stores": {
                      "gog-galaxy": {
                        "enabled": false,
                        "remoteCatalogItemIds": [
                          " 123 ",
                          "123",
                          "ABC",
                          "abc",
                          ""
                        ]
                      }
                    }
                  }
                }
                """);

            var configuration = new StoreSyncSettingsStore(settingsPath).Load();
            var gog = configuration.UnifySteam.Stores["gog-galaxy"];

            Assert.False(gog.Enabled);
            Assert.Equal(["123", "ABC"], gog.RemoteCatalogItemIds);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Save_ProtectsOpenXblKeyForCurrentWindowsUser()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            const string apiKey = "personal-openxbl-key-that-must-not-be-plaintext";
            var store = new StoreSyncSettingsStore(settingsPath);

            store.Update(configuration =>
                configuration.UnifySteam.Stores["xbox-game-pass"].OpenXblApiKey =
                    apiKey);

            var storedJson = File.ReadAllText(settingsPath);
            Assert.DoesNotContain(apiKey, storedJson, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", storedJson, StringComparison.Ordinal);
            Assert.Equal(
                apiKey,
                store.Load().UnifySteam.Stores["xbox-game-pass"].OpenXblApiKey);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Save_MigratesLegacyPlaintextOpenXblKeyWithoutPlaintextBackup()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            const string legacyKey = "legacy-plaintext-openxbl-key";
            File.WriteAllText(
                settingsPath,
                $$"""
                  {
                    "unifySteam": {
                      "stores": {
                        "xbox-game-pass": {
                          "openXblApiKey": "{{legacyKey}}"
                        }
                      }
                    }
                  }
                  """);
            var store = new StoreSyncSettingsStore(settingsPath);
            var configuration = store.Load();

            Assert.Equal(
                legacyKey,
                configuration.UnifySteam.Stores["xbox-game-pass"].OpenXblApiKey);

            store.Save(configuration);

            Assert.DoesNotContain(
                legacyKey,
                File.ReadAllText(settingsPath),
                StringComparison.Ordinal);
            var backupPath = settingsPath + ".bak";
            if (File.Exists(backupPath))
            {
                Assert.DoesNotContain(
                    legacyKey,
                    File.ReadAllText(backupPath),
                    StringComparison.Ordinal);
            }
            Assert.Equal(
                legacyKey,
                store.Load().UnifySteam.Stores["xbox-game-pass"].OpenXblApiKey);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    private static string CreateTempSettingsPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "store-sync.settings.json");
    }

    private static void DeleteTempSettingsPath(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
