using System.Diagnostics;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class UnifySteamEpicLaunchTests
{
    [Theory]
    [InlineData("Heather")]
    [InlineData("heather")]
    [InlineData("9d2d0eb64d5c44529cece33fe2a46482")]
    [InlineData("8769e24080ea413b8ebca3f1b8c50951")]
    [InlineData("c180bd9859624278aa20f1333918498a")]
    [InlineData("afd9f29c1af44610b9e0f9b69742e257")]
    [InlineData("e5f031908d2e460b8757387d1a136e48")]
    public void RequiresEpicLauncherBridge_RecognizesKnownRockstarEpicProducts(
        string appName)
    {
        Assert.True(UnifySteamLauncher.RequiresEpicLauncherBridge(appName));
    }

    [Theory]
    [InlineData("Fortnite")]
    [InlineData("SomeOrdinaryEpicGame")]
    [InlineData("")]
    public void RequiresEpicLauncherBridge_DoesNotChangeOrdinaryEpicLaunches(
        string appName)
    {
        Assert.False(UnifySteamLauncher.RequiresEpicLauncherBridge(appName));
    }

    [Fact]
    public void ConfigureEpicLauncherBridge_UsesLegendarysWindowsSafeEnvironmentContract()
    {
        var path = Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "Program Files",
            "Tools for Steam",
            "EpicGamesLauncher.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = "legendary.exe",
            UseShellExecute = false,
        };
        startInfo.Environment["SteamAppId"] = "123";
        startInfo.Environment["SteamGameId"] = "123";

        UnifySteamLauncher.ConfigureEpicLauncherBridge(startInfo, path);

        Assert.Equal(
            Path.GetFullPath(path),
            startInfo.Environment["LEGENDARY_WRAPPER_EXE"]);
        Assert.False(startInfo.Environment.ContainsKey("SteamAppId"));
        Assert.False(startInfo.Environment.ContainsKey("SteamGameId"));
    }

    [Fact]
    public void RemoveInheritedSteamLaunchContext_RemovesOnlyFalseSteamIdentity()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "legendary.exe",
            UseShellExecute = false,
        };
        startInfo.Environment["SteamAppId"] = "123";
        startInfo.Environment["SteamGameId"] = "123";
        startInfo.Environment["SteamOverlayGameId"] = "123";
        startInfo.Environment["LEGENDARY_CONFIG_PATH"] = "legendary-data";

        UnifySteamLauncher.RemoveInheritedSteamLaunchContext(startInfo);

        Assert.False(startInfo.Environment.ContainsKey("SteamAppId"));
        Assert.False(startInfo.Environment.ContainsKey("SteamGameId"));
        Assert.False(startInfo.Environment.ContainsKey("SteamOverlayGameId"));
        Assert.Equal("legendary-data", startInfo.Environment["LEGENDARY_CONFIG_PATH"]);
    }

    [Fact]
    public void ManagedEpicLauncherBridge_PinsOfficialReleaseAndHash()
    {
        Assert.Equal("v0.4", ManagedEpicLauncherBridge.Version);
        Assert.Equal(64, ManagedEpicLauncherBridge.Sha256.Length);
        Assert.StartsWith(
            "https://github.com/Etaash-mathamsetty/heroic-epic-integration/releases/",
            ManagedEpicLauncherBridge.DownloadUrl,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Origin", "", "", "ea-app")]
    [InlineData("The EA App", "", "", "ea-app")]
    [InlineData("", "UbisoftConnect", "", "ubisoft-connect")]
    [InlineData("", "", "ubisoft", "ubisoft-connect")]
    [InlineData("", "", "", "epic")]
    [InlineData("Another Publisher", "", "", "external")]
    public void ResolveEpicDeliveryProvider_SeparatesOwnershipFromDelivery(
        string thirdPartyApp,
        string thirdPartyProvider,
        string partnerLinkType,
        string expected)
    {
        Assert.Equal(
            expected,
            UnifySteamService.ResolveEpicDeliveryProvider(
                thirdPartyApp,
                thirdPartyProvider,
                partnerLinkType));
    }
}
