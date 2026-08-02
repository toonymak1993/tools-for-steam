using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryLifecycleTests
{
    [Fact]
    public void CatalogRefresh_IgnoresPercentageOnlyUpdatesButAcceptsInstallCompletion()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var downloading = new UnifySteamDownloadStatus(
            "downloading",
            81,
            "Downloading Xbox game (81%).",
            generatedAt.AddSeconds(1));
        var completed = new UnifySteamDownloadStatus(
            "completed",
            100,
            "Installed.",
            generatedAt.AddSeconds(2));

        Assert.False(StoreSyncService.RequiresLifecycleCatalogRefresh(
            generatedAt,
            [downloading]));
        Assert.True(StoreSyncService.RequiresLifecycleCatalogRefresh(
            generatedAt,
            [downloading, completed]));
    }

    [Fact]
    public void CatalogRefresh_IgnoresCompletionAlreadyReflectedBySnapshot()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var completed = new UnifySteamDownloadStatus(
            "completed",
            100,
            "Installed.",
            generatedAt.AddSeconds(-1));

        Assert.False(StoreSyncService.RequiresLifecycleCatalogRefresh(
            generatedAt,
            [completed]));
    }

    [Fact]
    public void Registry_HasStableUniqueStoreAndTabOrder()
    {
        Assert.Equal(
            ["xbox-game-pass", "epic-games", "gog-galaxy", "rom-library"],
            OmniLibraryStoreRegistry.All.Select(store => store.Id));
        Assert.Equal(
            ["tfs-xbox", "tfs-xbox-cloud", "tfs-epic", "tfs-gog"],
            OmniLibraryStoreRegistry.All.SelectMany(store => store.LibraryTabs).Select(tab => tab.Id));
        Assert.Equal(
            OmniLibraryStoreRegistry.All.Count,
            OmniLibraryStoreRegistry.All.Select(store => store.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            OmniLibraryStoreRegistry.All.Sum(store => store.LibraryTabs.Count),
            OmniLibraryStoreRegistry.All.SelectMany(store => store.LibraryTabs)
                .Select(tab => tab.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void RomLibrary_ExposesOneNativeTabOnlyForSystemsWithImportedGames()
    {
        var descriptor = OmniLibraryStoreRegistry.GetRequired(
            OmniLibraryRomSystemRegistry.StoreId);
        var tabs = OmniLibraryStoreRegistry.BuildLibraryTabSummaries(
            descriptor,
            [
                new OmniLibraryRomSystemState(
                    "psp",
                    "PSP",
                    "PPSSPP",
                    2,
                    "psp",
                    [123u, 456u]),
                new OmniLibraryRomSystemState(
                    "nintendo-64",
                    "Nintendo 64",
                    "",
                    0,
                    "nintendo-64",
                    []),
            ]);

        var tab = Assert.Single(tabs);
        Assert.Equal("tfs-emulation-psp", tab.Id);
        Assert.Equal("PSP", tab.Title);
        Assert.Equal("platform:psp", tab.Filter);
    }

    [Fact]
    public void RomLibrary_CreatesSystemFoldersAndAppliesOnlyFileDeltas()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tools-for-steam-rom-test-{Guid.NewGuid():N}");
        try
        {
            var store = new UnifySteamStoreConfiguration
            {
                Enabled = true,
                InstallPath = root,
            };
            var romPath = Path.Combine(root, "PSP", "Lumines (USA).iso");
            OmniLibraryRomSystemRegistry.EnsureFolderStructure(root);
            File.WriteAllBytes(romPath, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(romPath, DateTime.UtcNow.AddSeconds(-2));

            OmniLibraryRomLibrary.Refresh(store);

            var first = Assert.Single(store.Cache.Games);
            Assert.Equal("Lumines", first.Title);
            Assert.Equal("psp", first.PlatformId);
            Assert.Equal("PSP", first.PlatformTitle);
            Assert.Equal(Path.GetFullPath(romPath), first.RomPath);
            Assert.Contains("Sony%20-%20PlayStation%20Portable", first.ImageUrl);
            Assert.True(Directory.Exists(Path.Combine(root, "Nintendo 64")));
            Assert.True(Directory.Exists(Path.Combine(root, "BIOS")));

            var stableId = first.Id;
            var oldFingerprint = first.PreparationSignature;
            first.SteamAppId = 12345;
            File.AppendAllText(romPath, "delta");
            File.SetLastWriteTimeUtc(romPath, DateTime.UtcNow.AddSeconds(-2));

            OmniLibraryRomLibrary.Refresh(store);

            var changed = Assert.Single(store.Cache.Games);
            Assert.Equal(stableId, changed.Id);
            Assert.Equal((uint)12345, changed.SteamAppId);
            Assert.NotEqual(oldFingerprint, changed.PreparationSignature);

            var sidecarPath = Path.Combine(root, "PSP", "Lumines (USA).png");
            File.WriteAllBytes(sidecarPath, [5, 6, 7, 8]);
            File.SetLastWriteTimeUtc(sidecarPath, DateTime.UtcNow.AddSeconds(-2));
            OmniLibraryRomLibrary.Refresh(store);

            var withSidecar = Assert.Single(store.Cache.Games);
            Assert.StartsWith("file:", withSidecar.ImageUrl, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(changed.PreparationSignature, withSidecar.PreparationSignature);

            File.Delete(romPath);
            OmniLibraryRomLibrary.Refresh(store);
            Assert.Empty(store.Cache.Games);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RomRegistry_ExposesTheFourInitialSystemsWithStableEmulators()
    {
        Assert.Equal(
            ["psp", "gamecube", "game-boy-advance", "nintendo-64"],
            OmniLibraryRomSystemRegistry.Supported.Select(system => system.Id));

        Assert.Equal(
            "mGBA.exe",
            OmniLibraryRomSystemRegistry.GetRequired("game-boy-advance").EmulatorExecutableName);
        Assert.Equal(
            "Dolphin.exe",
            OmniLibraryRomSystemRegistry.GetRequired("gamecube").EmulatorExecutableName);
        Assert.Equal(
            "ares.exe",
            OmniLibraryRomSystemRegistry.GetRequired("nintendo-64").EmulatorExecutableName);
        Assert.Equal(
            "PPSSPPWindows64.exe",
            OmniLibraryRomSystemRegistry.GetRequired("psp").EmulatorExecutableName);
    }

    [Theory]
    [InlineData(
        "God of War - Ghost of Sparta (Europe, Australia) (En,Fr,De,Es,It).iso",
        "God of War - Ghost of Sparta")]
    [InlineData(
        "Prince of Persia - The Forgotten Sands (Europe) (En,Fr,De,Es,It).iso",
        "Prince of Persia - The Forgotten Sands")]
    public void RomRegistry_StripsAllTrailingCatalogTagsForArtworkSearch(
        string fileName,
        string expectedTitle)
    {
        Assert.Equal(
            expectedTitle,
            OmniLibraryRomSystemRegistry.BuildDisplayTitle(fileName));
    }

    [Theory]
    [InlineData("Game Boy Advance", "Advance Wars.gba", "game-boy-advance")]
    [InlineData("GameCube", "F-Zero GX.rvz", "gamecube")]
    [InlineData("Nintendo 64", "Super Mario 64.z64", "nintendo-64")]
    public void RomLibrary_ScansEachSupportedSystemFolder(
        string folder,
        string fileName,
        string expectedSystemId)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tools-for-steam-rom-system-test-{Guid.NewGuid():N}");
        try
        {
            var store = new UnifySteamStoreConfiguration
            {
                Enabled = true,
                InstallPath = root,
            };
            OmniLibraryRomSystemRegistry.EnsureFolderStructure(root);
            var romPath = Path.Combine(root, folder, fileName);
            File.WriteAllBytes(romPath, [1, 2, 3]);
            File.SetLastWriteTimeUtc(romPath, DateTime.UtcNow.AddSeconds(-2));

            OmniLibraryRomLibrary.Refresh(store);

            var game = Assert.Single(store.Cache.Games);
            Assert.Equal(expectedSystemId, game.PlatformId);
            Assert.Equal(folder, game.PlatformTitle);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RomLaunchTarget_AllowsOneValidatedPlatformSeparatorOnly()
    {
        Assert.True(UnifySteamLauncher.TryParseTarget(
            "rom-library:psp:13fc1f8fd8b48fe2edd4a3b9",
            out var storeId,
            out var gameId));
        Assert.Equal("rom-library", storeId);
        Assert.Equal("psp:13fc1f8fd8b48fe2edd4a3b9", gameId);

        Assert.False(UnifySteamLauncher.TryParseTarget(
            "rom-library:psp:unsafe:path",
            out _,
            out _));
        Assert.False(UnifySteamLauncher.TryParseTarget(
            "rom-library:psp:..\\unsafe",
            out _,
            out _));
        Assert.False(UnifySteamLauncher.TryParseTarget(
            "epic-games:game:unexpected",
            out _,
            out _));
    }

    [Fact]
    public void PpssppLaunch_UsesFrontendSafeFullscreenWithoutOverridingGraphics()
    {
        var romPath = Path.Combine(
            Path.GetTempPath(),
            "PSP Games",
            "Lumines.iso");

        var arguments = UnifySteamLauncher.BuildPpssppLaunchArguments(romPath);

        Assert.Equal("--fullscreen", arguments[0]);
        Assert.Equal("--pause-menu-exit", arguments[1]);
        Assert.Equal(Path.GetFullPath(romPath), arguments[2]);
        Assert.DoesNotContain("-vulkan", arguments);
        Assert.DoesNotContain("-direct3d11", arguments);
        Assert.DoesNotContain("-j", arguments);
    }

    [Theory]
    [InlineData("gamecube", "--batch", "--exec")]
    [InlineData("game-boy-advance", "-f", null)]
    [InlineData("nintendo-64", "--system", "--fullscreen")]
    public void RomLaunch_UsesSystemSpecificFullscreenArguments(
        string systemId,
        string expectedArgument,
        string? secondExpectedArgument)
    {
        var romPath = Path.Combine(Path.GetTempPath(), "ROMs", "game.rom");

        var arguments = UnifySteamLauncher.BuildRomLaunchArguments(
            systemId,
            romPath,
            fullscreen: true);

        Assert.Contains(expectedArgument, arguments);
        if (secondExpectedArgument is not null)
        {
            Assert.Contains(secondExpectedArgument, arguments);
        }
        Assert.Equal(Path.GetFullPath(romPath), arguments[^1]);
    }

    [Fact]
    public void Registry_GogUsesTheSameManagedStoreContractsAsEpic()
    {
        var gog = OmniLibraryStoreRegistry.GetRequired("gog-galaxy");

        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.ManagedWebSignIn));
        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.InstallPath));
        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.ManagedInstall));
        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.ManagedUninstall));
        Assert.Equal("tfs-gog", Assert.Single(gog.LibraryTabs).Id);
    }

    [Fact]
    public void Evaluate_ExpiredAuthenticationDoesNotHideEstablishedShortcuts()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: false,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("required", result.Lifecycle.Authentication);
        Assert.Equal("ready", result.Lifecycle.Shortcuts);
    }

    [Fact]
    public void Evaluate_FailedCatalogProbeIsIsolatedFromExistingLibraryTab()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: "Temporary store outage",
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("degraded", result.Lifecycle.Catalog);
        Assert.Equal("catalog", result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_IncompleteArtworkNeverBlocksPreparedLibraryTab()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);
        store.PreparationStatus = "artwork-pending";
        store.PreparationDetail = "One optional image is unavailable.";

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("degraded", result.Lifecycle.Artwork);
        Assert.Equal("artwork", result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_BackgroundArtworkDoesNotReversePreparedLibraryState()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);
        OmniLibraryLifecycle.SetStage(store, "artwork", "updating");
        store.PreparationStatus = "prepared";
        store.PreparationDetail =
            "Library ready. Loading optional artwork 1/10 in the background.";

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("updating", result.Lifecycle.Artwork);
        Assert.Equal("prepared", store.PreparationStatus);
        Assert.Empty(result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_FirstPreparationRequiresNewSteamSession()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var store = CreatePreparedStore(preparedAt);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(-1));

        Assert.False(result.ReadyForLibraryTab);
        Assert.True(result.SteamRestartRequired);
    }

    [Fact]
    public void Evaluate_ShortcutFailureIsNotMisreportedAsCatalogFailure()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);
        store.Cache.LastError = "Steam Library sync failed: Steam was temporarily unavailable.";
        OmniLibraryLifecycle.SetStage(
            store,
            "shortcuts",
            "degraded",
            store.Cache.LastError);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: store.Cache.LastError,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("ready", result.Lifecycle.Catalog);
        Assert.Equal("degraded", result.Lifecycle.Shortcuts);
        Assert.Equal("shortcuts", result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_OneStoreFailureDoesNotChangeAnotherStoreLifecycle()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var xbox = CreatePreparedStore(preparedAt);
        var epic = CreatePreparedStore(preparedAt);
        xbox.Cache.LastError = "Xbox catalog unavailable";
        OmniLibraryLifecycle.SetStage(
            xbox,
            "catalog",
            "degraded",
            xbox.Cache.LastError);

        var xboxResult = OmniLibraryLifecycle.Evaluate(
            xbox,
            true,
            true,
            1,
            true,
            xbox.PreparedCatalogSignature,
            xbox.Cache.LastError,
            preparedAt.AddMinutes(1));
        var epicResult = OmniLibraryLifecycle.Evaluate(
            epic,
            true,
            true,
            1,
            true,
            epic.PreparedCatalogSignature,
            string.Empty,
            preparedAt.AddMinutes(1));

        Assert.Equal("catalog", xboxResult.Lifecycle.FailureScope);
        Assert.Empty(epicResult.Lifecycle.FailureScope);
        Assert.True(xboxResult.ReadyForLibraryTab);
        Assert.True(epicResult.ReadyForLibraryTab);
    }

    [Fact]
    public void Evaluate_UnpreparedCatalogNeverCreatesTab()
    {
        var store = new UnifySteamStoreConfiguration
        {
            Enabled = true,
            Cache = new UnifySteamLibraryCache
            {
                Games =
                [
                    new UnifySteamGameCacheEntry
                    {
                        Id = "new-game",
                        Title = "New Game",
                    },
                ],
            },
        };

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: false,
            currentCatalogSignature: string.Empty,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: DateTimeOffset.UtcNow);

        Assert.False(result.ReadyForLibraryTab);
        Assert.False(result.SteamRestartRequired);
        Assert.Equal("pending", result.Lifecycle.Shortcuts);
    }

    private static UnifySteamStoreConfiguration CreatePreparedStore(DateTimeOffset preparedAt)
    {
        return new UnifySteamStoreConfiguration
        {
            Enabled = true,
            PreparedAtUtc = preparedAt,
            PreparedCatalogSignature = "CATALOG",
            PreparationStatus = "prepared",
            Cache = new UnifySteamLibraryCache
            {
                AccountName = "Test",
                Games =
                [
                    new UnifySteamGameCacheEntry
                    {
                        Id = "game",
                        Title = "Game",
                        SteamAppId = 123u,
                    },
                ],
            },
        };
    }
}
