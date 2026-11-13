using SteamLoader.App.Infrastructure.Curtain;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class CurtainLaunchTrackerTests
{
    private static readonly CurtainRect Monitor = new(0, 0, 1920, 1080);
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 2, 0, 0, TimeSpan.Zero);

    private static CurtainWindowInfo Window(
        int handle,
        uint processId,
        CurtainRect bounds,
        string title = "",
        bool foreground = false,
        bool toolWindow = false) =>
        new(new IntPtr(handle), processId, title, bounds, Monitor, false, toolWindow, false, foreground);

    [Fact]
    public void ClassifiesStoreLaunchersNewGamesAndOldProcesses()
    {
        var baselinePids = new HashSet<uint> { 10, 20 };
        var baselineWindows = new HashSet<IntPtr> { new(500) };

        Assert.Equal(
            CurtainWindowRole.Launcher,
            CurtainLaunchTracker.Classify(Window(1, 10, Monitor), new(10, "EADesktop", Start), baselinePids, baselineWindows, 99));
        Assert.Equal(
            CurtainWindowRole.Game,
            CurtainLaunchTracker.Classify(Window(2, 30, Monitor), new(30, "Cyberpunk2077", Start), baselinePids, baselineWindows, 99));
        Assert.Equal(
            CurtainWindowRole.Ignore,
            CurtainLaunchTracker.Classify(Window(3, 20, Monitor), new(20, "notepad", Start), baselinePids, baselineWindows, 99));
        Assert.Equal(
            CurtainWindowRole.Ignore,
            CurtainLaunchTracker.Classify(Window(4, 40, Monitor), new(40, "steamwebhelper", Start), baselinePids, baselineWindows, 99));
        Assert.Equal(
            CurtainWindowRole.Ignore,
            CurtainLaunchTracker.Classify(Window(5, 99, Monitor), new(99, "ToolsForSteam", Start), baselinePids, baselineWindows, 99));
        // Store games draw into ApplicationFrameHost, which ran before the launch.
        Assert.Equal(
            CurtainWindowRole.Game,
            CurtainLaunchTracker.Classify(Window(6, 20, Monitor), new(20, "ApplicationFrameHost", Start), baselinePids, baselineWindows, 99));
        Assert.Equal(
            CurtainWindowRole.Ignore,
            CurtainLaunchTracker.Classify(Window(500, 20, Monitor), new(20, "ApplicationFrameHost", Start), baselinePids, baselineWindows, 99));
    }

    [Fact]
    public void FullscreenSurfaceIsReadyOnlyAfterItStoppedMoving()
    {
        var tracker = new CurtainLaunchTracker("Cyberpunk 2077");
        var surface = new CurtainGameSurface(Window(1, 30, Monitor), "Cyberpunk2077", Start);

        Assert.Null(tracker.Observe([surface], Start.AddSeconds(2), TimeSpan.FromSeconds(2)));
        Assert.Null(tracker.Observe([surface], Start.AddSeconds(2.5), TimeSpan.FromSeconds(2.5)));
        var ready = tracker.Observe([surface], Start.AddSeconds(2.9), TimeSpan.FromSeconds(2.9));

        Assert.NotNull(ready);
        Assert.Equal("fullscreen", ready!.Reason);
    }

    [Fact]
    public void GeometryChangesRestartTheStabilityClock()
    {
        var tracker = new CurtainLaunchTracker("Game");
        var small = new CurtainGameSurface(Window(1, 30, new CurtainRect(600, 300, 1320, 780)), "game", Start);
        var full = new CurtainGameSurface(Window(1, 30, Monitor), "game", Start);

        Assert.Null(tracker.Observe([small], Start.AddSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Null(tracker.Observe([full], Start.AddSeconds(3), TimeSpan.FromSeconds(3)));
        Assert.Null(tracker.Observe([full], Start.AddSeconds(3.5), TimeSpan.FromSeconds(3.5)));
        Assert.NotNull(tracker.Observe([full], Start.AddSeconds(3.9), TimeSpan.FromSeconds(3.9)));
    }

    [Fact]
    public void SmallSplashWindowsNeverEndTheCurtain()
    {
        var tracker = new CurtainLaunchTracker("Elden Ring");
        var splash = new CurtainGameSurface(Window(1, 30, new CurtainRect(760, 390, 1160, 690), "Splash"), "eldenring", Start);

        for (var second = 1; second <= 30; second++)
        {
            Assert.Null(tracker.Observe([splash], Start.AddSeconds(second), TimeSpan.FromSeconds(second)));
        }
    }

    [Fact]
    public void LargeWindowNeedsAMatureProcessAndStableGeometry()
    {
        var tracker = new CurtainLaunchTracker("Some Game");
        var bounds = new CurtainRect(160, 90, 1760, 990);
        var processStart = Start.AddSeconds(1);
        var surface = new CurtainGameSurface(Window(1, 30, bounds), "somegame", processStart);

        Assert.Null(tracker.Observe([surface], Start.AddSeconds(2), TimeSpan.FromSeconds(2)));
        Assert.Null(tracker.Observe([surface], Start.AddSeconds(4.6), TimeSpan.FromSeconds(4.6)));
        var ready = tracker.Observe([surface], Start.AddSeconds(6.2), TimeSpan.FromSeconds(6.2));
        Assert.Equal("large-window", ready?.Reason);
    }

    [Fact]
    public void InteractiveGameLauncherAsksForAttentionInsteadOfBeingCovered()
    {
        var tracker = new CurtainLaunchTracker("Stellaris");
        var launcher = new CurtainGameSurface(
            Window(1, 30, new CurtainRect(400, 200, 1520, 880), "Paradox Launcher", foreground: true),
            "Paradox Launcher",
            Start);

        Assert.Null(tracker.Observe([launcher], Start.AddSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Null(tracker.AttentionSurface);
        Assert.Null(tracker.Observe([launcher], Start.AddSeconds(3.6), TimeSpan.FromSeconds(3.6)));
        Assert.Equal("interactive-launcher", tracker.AttentionSurface?.Reason);
    }

    [Fact]
    public void LibraryCacheHashedFoldersAndGridOverridesResolve()
    {
        var steamRoot = Path.Combine(Path.GetTempPath(), $"tfs-curtain-steam-{Guid.NewGuid():N}");
        try
        {
            var hashed = Path.Combine(steamRoot, "appcache", "librarycache", "1091500", "ef6cf6830951af90787f1e83f840b669a8b3e395");
            Directory.CreateDirectory(hashed);
            File.WriteAllBytes(Path.Combine(hashed, "library_hero.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(hashed, "library_hero_blur.jpg"), [1]);
            var logoFolder = Path.Combine(steamRoot, "appcache", "librarycache", "1091500", "0f2a2565");
            Directory.CreateDirectory(logoFolder);
            File.WriteAllBytes(Path.Combine(logoFolder, "logo.png"), [1]);

            Assert.EndsWith("library_hero.jpg", CurtainArtworkResolver.ResolveBackground(steamRoot, 1091500, "auto", null));
            Assert.EndsWith("logo.png", CurtainArtworkResolver.ResolveLogo(steamRoot, 1091500, "auto", null));
            Assert.Equal(string.Empty, CurtainArtworkResolver.ResolveBackground(steamRoot, 1091500, "none", null));

            var grid = Path.Combine(steamRoot, "userdata", "12345", "config", "grid");
            Directory.CreateDirectory(grid);
            File.WriteAllBytes(Path.Combine(grid, "1091500_hero.png"), [1]);
            Assert.EndsWith("1091500_hero.png", CurtainArtworkResolver.ResolveBackground(steamRoot, 1091500, "auto", null));
            Assert.EndsWith("library_hero.jpg", CurtainArtworkResolver.ResolveBackground(steamRoot, 1091500, "cache-hero", null));

            var candidates = CurtainArtworkResolver.FindCandidates(steamRoot, 1091500);
            Assert.Contains(candidates, candidate => candidate.Id == "grid-hero" && candidate.Kind == "background");
            Assert.Contains(candidates, candidate => candidate.Id == "cache-logo" && candidate.Kind == "logo");

            // A custom file that went missing falls back to Steam's artwork.
            Assert.EndsWith(
                "1091500_hero.png",
                CurtainArtworkResolver.ResolveBackground(steamRoot, 1091500, "custom", Path.Combine(steamRoot, "missing.png")));
        }
        finally
        {
            if (Directory.Exists(steamRoot))
            {
                Directory.Delete(steamRoot, recursive: true);
            }
        }
    }
}
