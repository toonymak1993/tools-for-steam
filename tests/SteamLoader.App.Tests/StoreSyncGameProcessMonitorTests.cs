using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncGameProcessMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tfs-store-game-monitor-{Guid.NewGuid():N}");

    [Fact]
    public void TryMatch_BindsManagedNonSteamGameButRejectsUnrelatedGpuWindow()
    {
        var gameExecutable = Path.Combine(_root, "Games", "Example", "ExampleGame.exe");
        var settingsStore = new StoreSyncSettingsStore(Path.Combine(_root, "store-sync.json"));
        settingsStore.Save(new StoreSyncConfiguration
        {
            Manifest = new Dictionary<string, StoreSyncManifestEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["epic-games:example"] = new()
                {
                    TitleId = "epic-games:example",
                    StoreId = "epic-games",
                    Title = "Example Non-Steam Game",
                    ExecutablePath = gameExecutable,
                    ManagedShortcut = true,
                    LastSeenAtUtc = DateTimeOffset.UtcNow,
                },
            },
        });
        var monitor = new StoreSyncGameProcessMonitor(settingsStore);

        var game = monitor.TryMatch(new ForegroundTargetCandidate(
            4242,
            "ExampleGame",
            "Example Non-Steam Game",
            gameExecutable));
        var browser = monitor.TryMatch(new ForegroundTargetCandidate(
            9000,
            "browser",
            "Video",
            Path.Combine(_root, "Browser", "browser.exe")));

        Assert.NotNull(game);
        Assert.Equal("store-sync:epic-games:example", game.Key);
        Assert.Equal(4242, game.ProcessId);
        Assert.Null(browser);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
