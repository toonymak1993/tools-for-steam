using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;
using System.Text.Json;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class UnifySteamXboxTests
{
    [Fact]
    public void TryExtractProductRelation_MapsCatalogBundleToDownloadablePackage()
    {
        const string message = """
            CatalogId = StoreId:9N683TDT5M7R, ContentId:halo,
            Data = {"ParentBundleId":"9N6J4RNFPKKL","ProductId":"9N683TDT5M7R"}
            """;

        var parsed = XboxInstallEventTracker.TryExtractProductRelation(
            message,
            out var catalogProductId,
            out var packageProductId);

        Assert.True(parsed);
        Assert.Equal("9N6J4RNFPKKL", catalogProductId);
        Assert.Equal("9N683TDT5M7R", packageProductId);
    }

    [Theory]
    [InlineData(255, 249, false)]
    [InlineData(256, 0, true)]
    [InlineData(1, 250, true)]
    [InlineData(1024, 1000, true)]
    public void ShouldYieldEventBatch_BoundsBusyXboxEventStreams(
        int recordsRead,
        int elapsedMilliseconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            XboxInstallEventTracker.ShouldYieldEventBatch(
                recordsRead,
                TimeSpan.FromMilliseconds(elapsedMilliseconds)));
    }

    [Fact]
    public void MergeXboxInstalledGames_KeepsInstalledTitleNotInCurrentCatalog()
    {
        var games = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var installed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["9NPURCHASEDOUTRIGHT"] = new UnifySteamGameCacheEntry
            {
                Id = "9NPURCHASEDOUTRIGHT",
                Title = "Purchased Outright Game",
                Installed = true,
                InstallPath = @"C:\XboxGames\Purchased Outright Game\Content",
                ExecutablePath = @"C:\XboxGames\Purchased Outright Game\Content\game.exe",
            },
        };

        UnifySteamService.MergeXboxInstalledGames(
            games,
            installed,
            pcProductIdSet: [],
            cloudProductIdSet: [],
            previousSteamAppIds: new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));

        var survivor = Assert.Single(games.Values);
        Assert.Equal("Purchased Outright Game", survivor.Title);
        Assert.True(survivor.Installed);
    }

    [Fact]
    public void MergeXboxInstalledGames_PreservesExistingSteamAppIdForOffCatalogTitle()
    {
        var games = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var installed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["9NROTATEDOUTOFGAMEPASS"] = new UnifySteamGameCacheEntry
            {
                Id = "9NROTATEDOUTOFGAMEPASS",
                Title = "Rotated Out Game",
                Installed = true,
            },
        };
        var previousSteamAppIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["9NROTATEDOUTOFGAMEPASS"] = 123456789u,
        };

        UnifySteamService.MergeXboxInstalledGames(
            games,
            installed,
            pcProductIdSet: [],
            cloudProductIdSet: [],
            previousSteamAppIds);

        var survivor = Assert.Single(games.Values);
        Assert.Equal(123456789u, survivor.SteamAppId);
    }

    [Fact]
    public void TryParseXboxAppxManifest_RecognizesLegacyXboxUwpGame()
    {
        var packageRoot = Path.Combine(
            Path.GetTempPath(),
            $"tfs-xbox-appx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageRoot);
        try
        {
            File.WriteAllText(
                Path.Combine(packageRoot, "AppxManifest.xml"),
                """
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="Microsoft.OriandtheBlindForestDefinitiveEdition" Version="1.1.29.0" />
                  <Properties><DisplayName>Ori and the Blind Forest: Definitive Edition</DisplayName></Properties>
                  <Applications><Application Id="App" Executable="Ori.exe" /></Applications>
                  <Extensions><Extension Category="Microsoft.Xbox.Services" /></Extensions>
                </Package>
                """);

            var parsed = UnifySteamService.TryParseXboxAppxManifest(
                "Microsoft.OriandtheBlindForestDefinitiveEdition_1.1.29.0_x64__8wekyb3d8bbwe",
                packageRoot,
                "Ori and the Blind Forest: Definitive Edition",
                out var game);

            Assert.True(parsed);
            Assert.True(game.Installed);
            Assert.Equal("Ori and the Blind Forest: Definitive Edition", game.Title);
            Assert.Equal("xbox-appx", game.StoreNamespace);
            Assert.Equal(
                "shell:AppsFolder\\Microsoft.OriandtheBlindForestDefinitiveEdition_8wekyb3d8bbwe!App",
                game.ExecutablePath);
        }
        finally
        {
            Directory.Delete(packageRoot, recursive: true);
        }
    }

    [Fact]
    public void MergeXboxInstalledGames_MapsLegacyAppxPackageByUniqueCatalogTitle()
    {
        var catalog = new UnifySteamGameCacheEntry
        {
            Id = "9NBLGGH1Z6FB",
            Title = "Ori and the Blind Forest: Definitive Edition",
        };
        var games = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [catalog.Id] = catalog,
        };
        var appx = new UnifySteamGameCacheEntry
        {
            Id = "Microsoft.OriandtheBlindForestDefinitiveEdition",
            Title = catalog.Title,
            Installed = true,
            InstallPath = @"C:\Program Files\WindowsApps\Ori",
            ExecutablePath =
                "shell:AppsFolder\\Microsoft.OriandtheBlindForestDefinitiveEdition_8wekyb3d8bbwe!App",
            StoreNamespace = "xbox-appx",
        };

        UnifySteamService.MergeXboxInstalledGames(
            games,
            new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [appx.Id] = appx,
            },
            [catalog.Id],
            [],
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));

        Assert.Single(games);
        Assert.True(catalog.Installed);
        Assert.Equal(appx.ExecutablePath, catalog.ExecutablePath);
    }

    [Fact]
    public void MergeXboxInstalledGames_DoesNotImportUnmatchedXboxRelatedAppAsGame()
    {
        var games = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var xboxApp = new UnifySteamGameCacheEntry
        {
            Id = "Microsoft.GamingApp",
            Title = "Xbox",
            Installed = true,
            InstallPath = @"C:\Program Files\WindowsApps\Microsoft.GamingApp",
            ExecutablePath = "shell:AppsFolder\\Microsoft.GamingApp_8wekyb3d8bbwe!App",
            StoreNamespace = "xbox-appx",
        };

        UnifySteamService.MergeXboxInstalledGames(
            games,
            new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [xboxApp.Id] = xboxApp,
            },
            [],
            [],
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));

        Assert.Empty(games);
    }

    [Fact]
    public void CollapseRelatedXboxProductEntries_KeepsCatalogEditionAndRemovesPackageAlias()
    {
        const string catalogId = "9N6J4RNFPKKL";
        const string packageId = "9N683TDT5M7R";
        var catalog = new UnifySteamGameCacheEntry
        {
            Id = catalogId,
            Title = "Halo: Campaign Evolved – Standard Edition",
            SteamAppId = 111u,
        };
        var package = new UnifySteamGameCacheEntry
        {
            Id = packageId,
            Title = "Halo: Campaign Evolved",
            Installed = true,
            InstallPath = @"C:\XboxGames\Halo\Content",
            ExecutablePath = @"C:\XboxGames\Halo\Content\Halo.exe",
            SteamAppId = 222u,
        };
        var games = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [catalog.Id] = catalog,
            [package.Id] = package,
        };

        var removed = UnifySteamService.CollapseRelatedXboxProductEntries(
            games,
            id => id == catalogId
                ? new HashSet<string>([catalogId, packageId], StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>([id], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(packageId, Assert.Single(removed));
        var survivor = Assert.Single(games.Values);
        Assert.Equal(catalogId, survivor.Id);
        Assert.Equal(111u, survivor.SteamAppId);
        Assert.True(survivor.Installed);
        Assert.Equal(package.ExecutablePath, survivor.ExecutablePath);
        Assert.Equal(packageId, survivor.ProviderGameId);
    }

    [Fact]
    public void DedupeLibraryGames_CollapsesSameTitleAcrossPcAndCloudProductIds()
    {
        var pcEntry = new UnifySteamGameCacheEntry
        {
            Id = "9NPCPRODUCTID",
            Title = "Forza Horizon 5",
            CloudPlayable = false,
        };
        var cloudEntry = new UnifySteamGameCacheEntry
        {
            Id = "9NCLOUDPRODUCTID",
            Title = "Forza Horizon 5",
            CloudPlayable = true,
        };

        var deduped = UnifySteamService.DedupeLibraryGames([cloudEntry, pcEntry]);

        var survivor = Assert.Single(deduped);
        Assert.Equal("9NPCPRODUCTID", survivor.Id);
        Assert.False(survivor.CloudPlayable);
    }

    [Fact]
    public void DedupeLibraryGames_KeepsCloudOnlyEntryWhenNoPcCounterpartExists()
    {
        var cloudOnlyEntry = new UnifySteamGameCacheEntry
        {
            Id = "9NCLOUDONLYPRODUCT",
            Title = "Cloud Exclusive Game",
            CloudPlayable = true,
        };

        var deduped = UnifySteamService.DedupeLibraryGames([cloudOnlyEntry]);

        var survivor = Assert.Single(deduped);
        Assert.Equal("9NCLOUDONLYPRODUCT", survivor.Id);
    }

    [Fact]
    public void TryResolveXboxInstalledGame_MatchesUniqueStandardEditionPackage()
    {
        var catalogGame = new UnifySteamGameCacheEntry
        {
            Id = "9NTESTHALOPARENT",
            Title = "Halo: Campaign Evolved – Standard Edition",
        };
        var installedPackage = new UnifySteamGameCacheEntry
        {
            Id = "9NTESTHALOCHILD",
            Title = "Halo: Campaign Evolved",
            Installed = true,
            ExecutablePath = @"C:\XboxGames\Halo\Content\Halo.exe",
        };

        var resolved = UnifySteamService.TryResolveXboxInstalledGame(
            catalogGame,
            new Dictionary<string, UnifySteamGameCacheEntry>(
                StringComparer.OrdinalIgnoreCase)
            {
                [installedPackage.Id] = installedPackage,
            },
            out var installedGame);

        Assert.True(resolved);
        Assert.Same(installedPackage, installedGame);
    }

    [Fact]
    public void TryResolveXboxInstalledGame_MatchesExactStoreId()
    {
        var catalogGame = new UnifySteamGameCacheEntry
        {
            Id = "9NTESTEXACTPACKAGE",
            Title = "Exact package",
        };
        var installedPackage = new UnifySteamGameCacheEntry
        {
            Id = catalogGame.Id,
            Title = catalogGame.Title,
            Installed = true,
            ExecutablePath = @"C:\XboxGames\Exact\Content\Game.exe",
        };

        var resolved = UnifySteamService.TryResolveXboxInstalledGame(
            catalogGame,
            new Dictionary<string, UnifySteamGameCacheEntry>(
                StringComparer.OrdinalIgnoreCase)
            {
                [installedPackage.Id] = installedPackage,
            },
            out var installedGame);

        Assert.True(resolved);
        Assert.Same(installedPackage, installedGame);
    }

    [Fact]
    public void ResolveXboxManifestDisplayName_IgnoresResourcePlaceholder()
    {
        var title = UnifySteamService.ResolveXboxManifestDisplayName(
            "ms-resource:AppDisplayName",
            "Halo: Campaign Evolved",
            "Halo- Campaign Evolved",
            "9N683TDT5M7R");

        Assert.Equal("Halo: Campaign Evolved", title);
    }

    [Fact]
    public void TryResolveXboxInstalledGame_DoesNotGuessBetweenDuplicateTitles()
    {
        var catalogGame = new UnifySteamGameCacheEntry
        {
            Id = "9NTESTAMBIGUOUSPARENT",
            Title = "Example Game – Standard Edition",
        };
        var installedGames = new Dictionary<string, UnifySteamGameCacheEntry>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["9NTESTAMBIGUOUSONE"] = new()
            {
                Id = "9NTESTAMBIGUOUSONE",
                Title = "Example Game",
                Installed = true,
                ExecutablePath = @"C:\XboxGames\Example1\Game.exe",
            },
            ["9NTESTAMBIGUOUSTWO"] = new()
            {
                Id = "9NTESTAMBIGUOUSTWO",
                Title = "Example Game",
                Installed = true,
                ExecutablePath = @"D:\XboxGames\Example2\Game.exe",
            },
        };

        var resolved = UnifySteamService.TryResolveXboxInstalledGame(
            catalogGame,
            installedGames,
            out _);

        Assert.False(resolved);
    }

    [Fact]
    public void MergeXboxInstallState_PreservesStoreMetadataIdentifiers()
    {
        var catalogGame = new UnifySteamGameCacheEntry
        {
            Id = "9NTESTMETADATA",
            Title = "Metadata game",
            StoreTitleId = "1668241504",
            StoreNamespace = "xbox-retail",
        };

        var merged = UnifySteamService.MergeXboxInstallState(
            catalogGame,
            new Dictionary<string, UnifySteamGameCacheEntry>(
                StringComparer.OrdinalIgnoreCase),
            new UnifySteamDownloadStatus(
                "idle",
                0,
                string.Empty,
                DateTimeOffset.MinValue));

        Assert.Equal(catalogGame.StoreTitleId, merged.StoreTitleId);
        Assert.Equal(catalogGame.StoreNamespace, merged.StoreNamespace);
    }

    [Fact]
    public void MergeXboxInstallState_UsesInstalledManifestTitleIdForCatalogBundle()
    {
        var catalogGame = new UnifySteamGameCacheEntry
        {
            Id = "9NTESTHALOPARENT",
            ProviderGameId = "9NTESTHALOCHILD",
            Title = "Halo bundle",
        };
        var installedPackage = new UnifySteamGameCacheEntry
        {
            Id = catalogGame.ProviderGameId,
            Title = "Halo package",
            Installed = true,
            StoreTitleId = "2082978535",
            ExecutablePath = @"C:\XboxGames\Halo\Content\Halo.exe",
        };

        var merged = UnifySteamService.MergeXboxInstallState(
            catalogGame,
            new Dictionary<string, UnifySteamGameCacheEntry>(
                StringComparer.OrdinalIgnoreCase)
            {
                [installedPackage.Id] = installedPackage,
            },
            new UnifySteamDownloadStatus(
                "idle",
                0,
                string.Empty,
                DateTimeOffset.MinValue));

        Assert.True(merged.Installed);
        Assert.Equal(installedPackage.StoreTitleId, merged.StoreTitleId);
    }

    [Theory]
    [InlineData("7c27bae7", "2082978535")]
    [InlineData("0x7C27BAE7", "2082978535")]
    public void TryNormalizeXboxManifestTitleId_ConvertsOfficialHexValue(
        string input,
        string expected)
    {
        var parsed = UnifySteamService.TryNormalizeXboxManifestTitleId(
            input,
            out var titleId);

        Assert.True(parsed);
        Assert.Equal(expected, titleId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FFFFFFFF")]
    [InlineData("not-a-title")]
    [InlineData("123")]
    public void TryNormalizeXboxManifestTitleId_RejectsMissingOrPlaceholderValues(
        string input)
    {
        Assert.False(UnifySteamService.TryNormalizeXboxManifestTitleId(
            input,
            out var titleId));
        Assert.Empty(titleId);
    }

    [Fact]
    public void TryProbeXboxGameInstallation_ReportsRemovedContentWithoutScanningCatalog()
    {
        var missingContentPath = Path.Combine(
            Path.GetTempPath(),
            $"tfs-missing-xbox-{Guid.NewGuid():N}",
            "Content");
        var game = new UnifySteamGameState(
            "9NTESTREMOVED",
            "Removed Xbox game",
            Installed: true,
            CloudPlayable: false,
            SyncedToSteam: true,
            "Installed + Synced",
            missingContentPath,
            string.Empty,
            missingContentPath,
            Path.Combine(missingContentPath, "Game.exe"),
            "1.0.0.0",
            123,
            new UnifySteamDownloadState(
                "uninstall-action-required",
                0,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                0,
                0));

        var conclusive = UnifySteamService.TryProbeXboxGameInstallation(
            game,
            out var installed);

        Assert.True(conclusive);
        Assert.False(installed);
    }

    [Fact]
    public void TryProbeXboxGameInstallation_DoesNotGuessWhenInstalledMetadataIsMissing()
    {
        var game = new UnifySteamGameState(
            "9NTESTUNKNOWN",
            "Unknown Xbox game",
            Installed: true,
            CloudPlayable: false,
            SyncedToSteam: true,
            "Installed + Synced",
            "Installed locally.",
            string.Empty,
            string.Empty,
            string.Empty,
            "1.0.0.0",
            124,
            new UnifySteamDownloadState(
                "uninstall-action-required",
                0,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                0,
                0));

        var conclusive = UnifySteamService.TryProbeXboxGameInstallation(
            game,
            out var installed);

        Assert.False(conclusive);
        Assert.True(installed);
    }

    [Fact]
    public void ExtractXboxCloudProductIds_ReadsAndDeduplicatesPublicCatalogMembership()
    {
        const string page = """
            <script>
            {"v3_af206485-e87d-4624-9007-cb7f6d0cc42e__Cloud:XGPUWEB":
              {"type":2,"data":{"title":"All games","products":
                ["9NPDN9R45JX4","BT5P2X999VH2","9NPDN9R45JX4"]}}}
            </script>
            """;

        var productIds = UnifySteamService.ExtractXboxCloudProductIds(page);

        Assert.Equal(["9NPDN9R45JX4", "BT5P2X999VH2"], productIds);
    }

    [Fact]
    public void BuildXboxCloudLaunchUrl_UsesProductIdAndStableSlug()
    {
        var url = UnifySteamLauncher.BuildXboxCloudLaunchUrl(
            "A Plague Tale: Requiem",
            "9ND0JVB184XL");

        Assert.Contains("/play/launch/a-plague-tale-requiem/9ND0JVB184XL", url);
        Assert.StartsWith("https://www.xbox.com/", url);
    }

    [Fact]
    public void IsXboxConsoleCatalogProduct_RejectsPcVariantAndKeepsXboxPackage()
    {
        using var pc = JsonDocument.Parse("""
            {
              "Properties": {
                "XboxConsoleGenCompatible": [],
                "XboxConsoleGenOptimized": []
              },
              "DisplaySkuAvailabilities": [{
                "Sku": { "Properties": { "Packages": [] } }
              }]
            }
            """);
        using var xbox = JsonDocument.Parse("""
            {
              "Properties": {
                "XboxConsoleGenCompatible": []
              },
              "DisplaySkuAvailabilities": [{
                "Sku": {
                  "Properties": {
                    "Packages": [{
                      "PlatformDependencies": [{
                        "PlatformName": "Windows.Xbox"
                      }]
                    }]
                  }
                }
              }]
            }
            """);

        Assert.False(UnifySteamService.IsXboxConsoleCatalogProduct(pc.RootElement));
        Assert.True(UnifySteamService.IsXboxConsoleCatalogProduct(xbox.RootElement));
    }
}
