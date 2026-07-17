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
}
