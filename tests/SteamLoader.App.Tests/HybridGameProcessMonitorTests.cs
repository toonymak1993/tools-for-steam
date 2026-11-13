using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Performance;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class HybridGameProcessMonitorTests
{
    [Fact]
    public void Poll_CreatesStableExecutableProfileForNonSteamGame()
    {
        var candidate = new ForegroundTargetCandidate(
            4242,
            "Game",
            "A Non-Steam Game",
            @"C:\Games\Example\Game.exe");
        var monitor = new HybridGameProcessMonitor(null, () => candidate, processId => processId == 4242);

        var running = monitor.Poll();

        Assert.NotNull(running);
        Assert.Equal(@"exe:c:\games\example\game.exe", running.Key);
        Assert.Equal(string.Empty, running.AppId);
        Assert.Equal("A Non-Steam Game", running.Title);
        Assert.Equal(@"C:\Games\Example\Game.exe", running.ExecutablePath);
    }

    [Fact]
    public void Poll_KeepsObscuredNonSteamGameUntilItsProcessExits()
    {
        ForegroundTargetCandidate? candidate = new(
            4242,
            "Game",
            "A Non-Steam Game",
            @"C:\Games\Example\Game.exe");
        var processAlive = true;
        var monitor = new HybridGameProcessMonitor(null, () => candidate, _ => processAlive);

        var detected = monitor.Poll();
        candidate = null;
        var obscured = monitor.Poll();
        processAlive = false;
        var exited = monitor.Poll();

        Assert.Same(detected, obscured);
        Assert.Null(exited);
    }

    [Fact]
    public void Poll_FollowsHandAddedAppProfiles_EvenForDesktopApps()
    {
        var candidate = new ForegroundTargetCandidate(7, "obs64", "OBS", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe");
        var key = HybridGameProcessMonitor.KeyForExecutable(candidate.ExecutablePath);

        var withoutProfile = new HybridGameProcessMonitor(null, () => candidate, _ => true, _ => false, _ => false);
        var withProfile = new HybridGameProcessMonitor(null, () => candidate, _ => true, _ => false, value => value == key);

        Assert.Null(withoutProfile.Poll());
        Assert.Equal(key, withProfile.Poll()?.Key);
        Assert.Equal(@"exe:c:\program files\obs-studio\bin\64bit\obs64.exe", key);
    }

    [Theory]
    // Desktop apps never get game profiles, even fullscreen (the old monitor made profiles for Claude and the Snipping Tool).
    [InlineData("Claude", @"C:\Program Files\WindowsApps\Claude_1.0_x64\app\claude.exe", true, false)]
    [InlineData("SnippingTool", @"C:\Program Files\WindowsApps\Microsoft.ScreenSketch\SnippingTool\SnippingTool.exe", true, false)]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", true, false)]
    [InlineData("notepad", @"C:\Windows\System32\notepad.exe", true, false)]
    // Windowed apps outside store folders are not games.
    [InlineData("SomeTool", @"C:\Tools\SomeTool.exe", false, false)]
    // Fullscreen unknown exe, or anything from a store folder, is a game.
    [InlineData("Game", @"C:\Games\Example\Game.exe", true, true)]
    [InlineData("Witcher", @"D:\SteamLibrary\steamapps\common\Witcher\witcher.exe", false, true)]
    [InlineData("Halo", @"C:\XboxGames\Halo\Content\halo.exe", false, true)]
    public void LooksLikeGame_SkipsDesktopApps(string processName, string path, bool fullscreen, bool expected)
    {
        var candidate = new ForegroundTargetCandidate(1, processName, "Window", path);

        Assert.Equal(expected, HybridGameProcessMonitor.LooksLikeGame(candidate, _ => fullscreen));
    }
}
