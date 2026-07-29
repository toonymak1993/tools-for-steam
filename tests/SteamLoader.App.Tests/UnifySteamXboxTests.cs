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
