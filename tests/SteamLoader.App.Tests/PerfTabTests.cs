using SteamLoader.App.Infrastructure.SystemTools;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class PerfTabTests
{
    [Fact]
    public void PowerModesMapToWindowsOverlaySchemes()
    {
        Assert.Equal("efficiency", WindowsPowerModeService.ToMode(new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")));
        Assert.Equal("performance", WindowsPowerModeService.ToMode(new Guid("ded574b5-45a0-4f42-8737-46345c09c238")));
        Assert.Equal("balanced", WindowsPowerModeService.ToMode(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPowerModeService.SetMode("turbo"));
    }

    [Fact]
    public void FrameLimitStepsStartWithOff()
    {
        var steps = SteamLoader.App.Infrastructure.PerfTab.PerfTabService.FrameLimitSteps;
        Assert.Equal(0, steps[0]);
        Assert.Contains(60, steps);
        Assert.Equal(steps.Order(), steps);
    }

    [Fact]
    public void ContextMenuProfilesUseTheSameKeyAsTheRunningGame()
    {
        Assert.Equal("steam:1297900", SteamLoader.App.Infrastructure.PerfTab.PerfTabService.SteamProfileKey(1297900));

        // Real line from Steam's gameprocess_log: a non-Steam shortcut runs as AppID 9589381822639243264.
        const ulong loggedGameId = 9589381822639243264UL;
        var shortcutAppId = (uint)(loggedGameId >> 32);
        Assert.True(shortcutAppId >= 0x80000000);
        Assert.Equal($"steam:{loggedGameId}", SteamLoader.App.Infrastructure.PerfTab.PerfTabService.SteamProfileKey(shortcutAppId));
    }

    [Fact]
    public void AudioDevicesUseFriendlyNamesAndMarkTheDefault()
    {
        var devices = SteamLoader.App.Infrastructure.PerfTab.QuickSettingsService.ToDevices(
        [
            new SteamLoader.App.Models.AudioOutputDeviceInfo("a", "Lautsprecher", true, "Lautsprecher (Realtek)"),
            new SteamLoader.App.Models.AudioOutputDeviceInfo("b", "Kopfhörer", false),
        ]);

        Assert.Equal(["Lautsprecher (Realtek)", "Kopfhörer"], devices.Select(device => device.Name));
        Assert.Equal([true, false], devices.Select(device => device.Selected));
    }
}
