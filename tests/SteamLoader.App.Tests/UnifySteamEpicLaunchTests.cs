using System.Diagnostics;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class UnifySteamEpicLaunchTests
{
    [Theory]
    [InlineData("xbox-game-pass", false, "", false, true)]
    [InlineData("xbox-game-pass", true, "", false, false)]
    [InlineData("gog-galaxy", false, "", false, true)]
    [InlineData("epic-games", false, "epic", true, true)]
    [InlineData("epic-games", false, "ea-app", false, false)]
    [InlineData("epic-games", false, "ubisoft-connect", true, false)]
    public void CanInstallDirectly_UsesStoreSpecificCapabilities(
        string storeId,
        bool cloudPlayable,
        string deliveryProvider,
        bool hasInstallableAsset,
        bool expected)
    {
        Assert.Equal(
            expected,
            UnifySteamService.CanInstallDirectly(
                storeId,
                cloudPlayable,
                deliveryProvider,
                hasInstallableAsset));
    }

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
    [InlineData("Rockstar Games Launcher", "", "", "rockstar-games-launcher")]
    [InlineData("", "GOG Galaxy", "", "gog-galaxy")]
    [InlineData("", "Blizzard", "", "battle-net")]
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

    [Theory]
    [InlineData("ubisoft-connect", true, true, false)]
    [InlineData("ubisoft-connect", false, true, false)]
    [InlineData("ea-app", true, true, false)]
    [InlineData("rockstar-games-launcher", true, true, false)]
    [InlineData("rockstar-games-launcher", false, true, false)]
    [InlineData("gog-galaxy", true, true, false)]
    [InlineData("battle-net", true, true, false)]
    [InlineData("external", true, true, false)]
    [InlineData("epic", true, false, true)]
    [InlineData("epic", false, false, false)]
    public void EpicDeliveryCapabilities_KeepPublisherLaunchersExternal(
        string provider,
        bool hasInstallableAsset,
        bool requiresExternalLauncher,
        bool canInstallDirectly)
    {
        Assert.Equal(
            requiresExternalLauncher,
            UnifySteamService.RequiresEpicExternalLauncher(
                provider,
                hasInstallableAsset));
        Assert.Equal(
            canInstallDirectly,
            UnifySteamService.CanInstallEpicDirectly(
                provider,
                hasInstallableAsset));
    }

    [Theory]
    [InlineData(
        "",
        "LinkRequired")]
    [InlineData(
        "not-json",
        "LinkRequired")]
    [InlineData(
        "{\"activated\":[{\"app_name\":\"crew\"}],\"redeemable\":[]}",
        "Activated")]
    [InlineData(
        "{\"activated\":[],\"redeemable\":[{\"app_name\":\"crew\"}]}",
        "Redeemable")]
    [InlineData(
        "{\"activated\":[],\"redeemable\":[]}",
        "NotEligible")]
    public void UbisoftAccountLinkSummary_TracksTheExactEpicTitle(
        string summary,
        string expected)
    {
        Assert.Equal(
            expected,
            UnifySteamLauncher
                .GetUbisoftAccountLinkState(summary, "crew")
                .ToString());
    }

    [Theory]
    [InlineData("ubisoft-connect", false, true)]
    [InlineData("ubisoft-connect", true, false)]
    [InlineData("epic", false, false)]
    public void UbisoftAccountLink_IsRequiredOnlyBeforeThePublisherInstall(
        string provider,
        bool installed,
        bool expected)
    {
        Assert.Equal(
            expected,
            UnifySteamLauncher.ShouldEnsureUbisoftAccountLink(
                new UnifySteamGameCacheEntry
                {
                    DeliveryProvider = provider,
                    Installed = installed,
                }));
    }

    [Fact]
    public void NormalizeEpicDeliveryCapabilities_AlwaysHandsUbisoftInstallToPublisher()
    {
        var game = new UnifySteamGameCacheEntry
        {
            DeliveryProvider = "ubisoft-connect",
            HasInstallableAsset = true,
            RequiresAccountLink = false,
            RequiresExternalLauncher = false,
        };

        UnifySteamService.NormalizeEpicDeliveryCapabilities(game);

        Assert.True(game.RequiresAccountLink);
        Assert.True(game.RequiresExternalLauncher);
        Assert.False(UnifySteamService.CanInstallEpicDirectly(
            game.DeliveryProvider,
            game.HasInstallableAsset));
    }

    [Fact]
    public void EaHandoff_AcceptsOnlyTheExpectedLegendaryLink2EaTarget()
    {
        const string json =
            """{"uri":"link2ea://launchgame/bobcat?AUTH_PASSWORD=short-lived-secret"}""";

        Assert.True(EaAppIntegration.TryParseHandoffUri(
            json,
            "bobcat",
            out var handoffUri));
        Assert.Equal("link2ea", handoffUri.Scheme);
        Assert.Equal("launchgame", handoffUri.Host);
        Assert.Equal("/bobcat", handoffUri.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("""{"uri":"https://example.com/bobcat"}""")]
    [InlineData("""{"uri":"link2ea://other/bobcat?AUTH_PASSWORD=secret"}""")]
    [InlineData("""{"uri":"link2ea://launchgame/another-game?AUTH_PASSWORD=secret"}""")]
    [InlineData("""{"uri":"link2ea://launchgame/bobcat"}""")]
    [InlineData("""{"uri":"link2ea://launchgame/bobcat?AUTH_PASSWORD="}""")]
    public void EaHandoff_RejectsMalformedOrMisdirectedTargets(string json)
    {
        Assert.False(EaAppIntegration.TryParseHandoffUri(
            json,
            "bobcat",
            out _));
    }

    [Theory]
    [InlineData(
        "\"C:\\Program Files\\Electronic Arts\\EA Desktop\\EA Desktop\\EADesktop.exe\" \"%1\"",
        "C:\\Program Files\\Electronic Arts\\EA Desktop\\EA Desktop\\EADesktop.exe")]
    [InlineData(
        "C:\\EA\\EADesktop.exe %1",
        "C:\\EA\\EADesktop.exe")]
    [InlineData("", "")]
    public void EaProtocolCommand_ExtractsOnlyItsExecutableToken(
        string command,
        string expected)
    {
        Assert.Equal(
            expected,
            EaAppIntegration.ExtractExecutablePathFromCommand(command));
    }

    [Theory]
    [InlineData(true, false, "idle", "")]
    [InlineData(false, false, "idle", "install-client")]
    [InlineData(false, true, "idle", "link-account")]
    [InlineData(false, true, "action-required", "continue-provider")]
    [InlineData(false, true, "failed", "link-account")]
    public void EaExternalAction_ExposesTheNextHonestUserStep(
        bool installed,
        bool eaAppAvailable,
        string operationStatus,
        string expected)
    {
        Assert.Equal(
            expected,
            EaAppIntegration.GetExternalAction(
                installed,
                eaAppAvailable,
                operationStatus));
    }
}
