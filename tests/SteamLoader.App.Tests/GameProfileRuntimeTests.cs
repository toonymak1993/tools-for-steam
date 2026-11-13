using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.PerfTab;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class GameProfileRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tfs-game-profile-{Guid.NewGuid():N}");

    public GameProfileRuntimeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
        }
    }

    private sealed class FakeTargets : IGameProfileTargets
    {
        public int FrameLimit = 0;
        public int RefreshRate = 120;
        public int RtssLevel = 0;
        public string? PowerMode = "balanced";
        public bool? CpuBoost = true;

        public int GetFrameLimit() => FrameLimit;
        public void SetFrameLimit(int value) => FrameLimit = value;
        public int GetRefreshRate() => RefreshRate;
        public void SetRefreshRate(int hertz) => RefreshRate = hertz;
        public int GetRtssLevel() => RtssLevel;
        public void SetRtssLevel(int level) => RtssLevel = level;
        public string? GetPowerMode() => PowerMode;
        public void SetPowerMode(string mode) => PowerMode = mode;
        public bool? GetCpuBoost() => CpuBoost;
        public void SetCpuBoost(bool enabled) => CpuBoost = enabled;
    }

    [Fact]
    public void ProfileValuesApply_AndTheUsersOwnValuesComeBack()
    {
        var targets = new FakeTargets();
        var runtime = new GameProfileRuntime(targets, _root);

        runtime.Apply(new GameProfileExtras(FrameLimit: 40, RefreshRate: 60, RtssLevel: 2, PowerMode: "efficiency", CpuBoost: false));
        Assert.Equal((40, 60, 2, "efficiency", (bool?)false), (targets.FrameLimit, targets.RefreshRate, targets.RtssLevel, targets.PowerMode, targets.CpuBoost));

        runtime.Apply(null);
        Assert.Equal((0, 120, 0, "balanced", (bool?)true), (targets.FrameLimit, targets.RefreshRate, targets.RtssLevel, targets.PowerMode, targets.CpuBoost));
        Assert.True(runtime.Baseline.IsEmpty);
        Assert.False(File.Exists(Path.Combine(_root, "game-profile-baseline.json")));
    }

    [Fact]
    public void GlobalFieldsStayUntouched_AndSwitchingProfilesKeepsTheOriginalBaseline()
    {
        var targets = new FakeTargets();
        var runtime = new GameProfileRuntime(targets, _root);

        runtime.Apply(new GameProfileExtras(FrameLimit: 30));
        Assert.Equal(120, targets.RefreshRate);
        runtime.Apply(new GameProfileExtras(FrameLimit: 45, RtssLevel: 1));
        Assert.Equal((45, 1), (targets.FrameLimit, targets.RtssLevel));

        runtime.Apply(new GameProfileExtras(RtssLevel: 3));
        Assert.Equal(0, targets.FrameLimit); // the first profile's 30 is not the user's value

        runtime.Apply(null);
        Assert.Equal((0, 0), (targets.FrameLimit, targets.RtssLevel));
    }

    [Fact]
    public void CrashMidGame_RestoresAtNextStart()
    {
        var targets = new FakeTargets();
        new GameProfileRuntime(targets, _root).Apply(new GameProfileExtras(FrameLimit: 40, PowerMode: "performance"));
        Assert.True(File.Exists(Path.Combine(_root, "game-profile-baseline.json")));

        var restarted = new GameProfileRuntime(targets, _root);
        restarted.RestoreLeftovers();

        Assert.Equal((0, "balanced"), (targets.FrameLimit, targets.PowerMode));
    }

    [Fact]
    public void ChangeInPerfTabWhileProfileActive_RemembersTheUsersValueOnce()
    {
        var targets = new FakeTargets();
        var runtime = new GameProfileRuntime(targets, _root);
        runtime.Apply(new GameProfileExtras());

        runtime.RememberBeforeProfileChange("frame-limit");
        targets.SetFrameLimit(60);
        runtime.RememberBeforeProfileChange("frame-limit");
        targets.SetFrameLimit(90);
        runtime.Apply(null);

        Assert.Equal(0, targets.FrameLimit);
    }

    [Fact]
    public void NormalizeExtras_DropsInvalidValues()
    {
        Assert.Null(HandheldPerformanceService.NormalizeExtras(new GameProfileExtras()));
        Assert.Null(HandheldPerformanceService.NormalizeExtras(new GameProfileExtras(PowerMode: "turbo", RefreshRate: 0)));
        var normalized = HandheldPerformanceService.NormalizeExtras(new GameProfileExtras(FrameLimit: 9999, PowerMode: "Performance"));
        Assert.Equal(500, normalized!.FrameLimit);
        Assert.Equal("performance", normalized.PowerMode);
    }
}
