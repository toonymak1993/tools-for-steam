using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncSettingsStoreTests
{
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
