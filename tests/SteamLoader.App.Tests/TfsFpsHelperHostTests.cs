using System.Reflection;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class TfsFpsHelperHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tfs-fps-helper-{Guid.NewGuid():N}");

    [Fact]
    public void ManagementTick_DisablesOverlayOnlyAfterTrackedSteamGameExitIsConfirmed()
    {
        var logPath = Path.Combine(_root, "steam", "logs", "gameprocess_log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(
            logPath,
            "[2026-07-17 12:00:00] AppID 1234 adding PID 4242 as a tracked process \"C:\\Games\\TestGame.exe\"\n");
        var settingsStore = new PerformanceSettingsStore(Path.Combine(_root, "performance.json"));
        var statusStore = new PerformanceStatusStore(Path.Combine(_root, "performance-status.json"));
        settingsStore.Save(new PerformanceSettingsConfiguration { OverlayEnabled = true });
        var processAlive = true;
        var steamMonitor = new SteamGameProcessMonitor(
            Path.Combine(_root, "steam"),
            logPath,
            processId => processId == 4242 && processAlive);

        var host = new TfsFpsHelperHost(settingsStore, statusStore, steamMonitor, null);
        typeof(TfsFpsHelperHost)
            .GetField("_trackedSteamGameKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, "steam:1234");

        var shouldStopMethod = typeof(TfsFpsHelperHost)
            .GetMethod("ShouldStopForClosedGame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)shouldStopMethod.Invoke(host, null)!);

        processAlive = false;
        File.AppendAllText(
            logPath,
            "[2026-07-17 12:01:00] AppID 1234 no longer tracking PID 4242\n" +
            "[2026-07-17 12:01:00] Remove 1234 from running list\n");
        Assert.False((bool)shouldStopMethod.Invoke(host, null)!);
        typeof(TfsFpsHelperHost)
            .GetField("_trackedGameMissingSince", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3));

        typeof(TfsFpsHelperHost)
            .GetMethod("OnManagementTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, null);

        Assert.False(settingsStore.Load().OverlayEnabled);
        Assert.Contains("game session closed", statusStore.Load().DetailText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagementTick_DisablesOverlayWhenManagedNonSteamGameHasExited()
    {
        var settingsStore = new PerformanceSettingsStore(Path.Combine(_root, "non-steam-performance.json"));
        var statusStore = new PerformanceStatusStore(Path.Combine(_root, "non-steam-performance-status.json"));
        settingsStore.Save(new PerformanceSettingsConfiguration { OverlayEnabled = true });
        var storeSyncMonitor = new StoreSyncGameProcessMonitor(
            new StoreSyncSettingsStore(Path.Combine(_root, "store-sync.json")));
        var host = new TfsFpsHelperHost(settingsStore, statusStore, null, storeSyncMonitor);

        typeof(TfsFpsHelperHost)
            .GetField("_trackedStoreSyncGameKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, "store-sync:epic-games:example");
        typeof(TfsFpsHelperHost)
            .GetField("_trackedStoreSyncGameProcessId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, int.MaxValue);
        typeof(TfsFpsHelperHost)
            .GetField("_trackedGameMissingSince", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3));

        typeof(TfsFpsHelperHost)
            .GetMethod("OnManagementTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, null);

        Assert.False(settingsStore.Load().OverlayEnabled);
        Assert.Contains("game session closed", statusStore.Load().DetailText, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
