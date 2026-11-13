using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class GameBarControllerAutomationTests
{
    [Theory]
    [InlineData(true, false, true, false)]   // Big Picture: Xbox button stays with Steam
    [InlineData(true, true, true, true)]     // Xbox game running: Game Bar allowed
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, null)]   // desktop without Big Picture: user's own value
    [InlineData(false, true, true, null)]    // automation off: never touch Windows
    public void DecidesTheWindowsValue(bool enabled, bool xboxGame, bool bigPicture, bool? expected)
    {
        Assert.Equal(expected, GameBarControllerAutomationService.Decide(enabled, xboxGame, bigPicture));
    }

    [Theory]
    [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe", true)]
    [InlineData(@"D:\XboxGames\Halo Infinite\Content\HaloInfinite.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.254428597CFE2_1.0.0.0_x64__8wekyb3d8bbwe\ForzaHorizon5.exe", true)]
    [InlineData(@"C:\Program Files\ModifiableWindowsApps\Starfield\Starfield.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.GamingApp_2410.1001.23.0_x64__8wekyb3d8bbwe\XboxPcApp.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21_x64__8wekyb3d8bbwe\WindowsTerminal.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Hades\Hades.exe", false)]
    [InlineData(null, false)]
    public void RecognisesXboxAndStoreGames(string? path, bool expected)
    {
        // The WindowsApps Forza package ships MicrosoftGame.config like every GDK game.
        Assert.Equal(expected, GameBarControllerAutomationService.IsXboxStoreGamePath(
            path,
            packageRoot => packageRoot.EndsWith("Microsoft.254428597CFE2_1.0.0.0_x64__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    // Ordinary MSIX apps from WindowsApps are not games (2026-11-11: Claude.exe
    // turned the Game Bar Xbox button on while Steam Big Picture was open).
    [InlineData(@"C:\Program Files\WindowsApps\Claude_4.23118.1.0_x64__pzs8sxrjxfjjc\app\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2450_x64__cv1g1gvanyjgm\WhatsApp.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\SomeVendor.SomeTool_1.0.0.0_x64__abc\tool.exe")]
    public void MsixAppsWithoutGameConfigAreNotGames(string path)
    {
        Assert.False(GameBarControllerAutomationService.IsXboxStoreGamePath(path, _ => false));
    }

    [Fact]
    public void WindowsAppsPackageCountsAsGameOnlyWithMicrosoftGameConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"), "WindowsApps");
        var game = Path.Combine(root, "Publisher.Game_1.0.0.0_x64__abc");
        var app = Path.Combine(root, "Claude_4.23118.1.0_x64__pzs8sxrjxfjjc");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(game, "MicrosoftGame.config"), "<Game />");

        try
        {
            Assert.True(GameBarControllerAutomationService.IsXboxStoreGamePath(Path.Combine(game, "Game.exe")));
            Assert.False(GameBarControllerAutomationService.IsXboxStoreGamePath(Path.Combine(app, "app", "claude.exe")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }
}
