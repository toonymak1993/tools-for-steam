using SteamLoader.App.Infrastructure.Handheld;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamGameProcessMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tfs-steam-monitor-{Guid.NewGuid():N}");

    [Fact]
    public void Poll_TracksSteamAppAndResolvesManifestTitleUntilRemoval()
    {
        var logPath = Path.Combine(_root, "logs", "gameprocess_log.txt");
        var steamAppsPath = Path.Combine(_root, "steamapps");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        Directory.CreateDirectory(steamAppsPath);
        File.WriteAllText(
            Path.Combine(steamAppsPath, "appmanifest_1297900.acf"),
            "\"AppState\"\n{\n  \"appid\" \"1297900\"\n  \"name\" \"Test Handheld Game\"\n}");
        File.WriteAllText(
            logPath,
            "[2026-07-11 12:00:00] AppID 1297900 adding PID 7408 as a tracked process \"\"C:\\Games\\Test Game\\Game.exe\"\"\n");
        var monitor = new SteamGameProcessMonitor(_root, logPath, processId => processId == 7408);

        var running = monitor.Poll();

        Assert.NotNull(running);
        Assert.Equal("steam:1297900", running.Key);
        Assert.Equal("Test Handheld Game", running.Title);
        Assert.Equal(7408, running.ProcessId);

        File.AppendAllText(
            logPath,
            "[2026-07-11 12:01:00] AppID 1297900 no longer tracking PID 7408\n" +
            "[2026-07-11 12:01:00] Remove 1297900 from running list\n");

        Assert.Null(monitor.Poll());
    }

    [Fact]
    public void PollForProcess_BindsOnlyProcessesTrackedByTheSteamApp()
    {
        var logPath = Path.Combine(_root, "logs", "gameprocess_log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(
            logPath,
            "[2026-07-17 12:00:00] AppID 1234 adding PID 4242 as a tracked process \"C:\\Games\\TestGame.exe\"\n");
        var monitor = new SteamGameProcessMonitor(_root, logPath, processId => processId is 4242 or 9000);

        var steamGame = monitor.PollForProcess(4242);
        var unrelatedGpuWindow = monitor.PollForProcess(9000);

        Assert.NotNull(steamGame);
        Assert.Equal("steam:1234", steamGame.Key);
        Assert.Null(unrelatedGpuWindow);
        Assert.True(monitor.IsAppRunning("steam:1234"));
        Assert.True(monitor.IsProcessTrackedByApp("steam:1234", 4242));
        Assert.False(monitor.IsProcessTrackedByApp("steam:1234", 9000));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
