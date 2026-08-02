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
