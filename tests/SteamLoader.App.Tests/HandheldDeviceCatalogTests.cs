using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Settings;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class HandheldDeviceCatalogTests
{
    [Fact]
    public void MsiClawA8Profile_UsesVerifiedIdentityAndTdpLimits()
    {
        var profile = HandheldDeviceCatalog.CreateMsiClawA8();

        Assert.Equal("msi-claw-a8", profile.Id);
        Assert.Equal("MICRO-STAR INTERNATIONAL CO., LTD.", profile.Manufacturer);
        Assert.Equal("MS-1T8K", profile.ProductCode);
        Assert.Equal("MSI Claw A8", profile.DisplayName);
        Assert.Equal(15, profile.MinimumTdpWatts);
        Assert.Equal(35, profile.MaximumTdpWatts);
    }

    [Fact]
    public void PluginCatalog_HidesHandheldPluginWithoutDetectedDevice()
    {
        var definitions = SteamLoaderPluginCatalog.BuildDefinitions();

        Assert.DoesNotContain(definitions, plugin => plugin.Id == "handheld-performance");
    }

    [Fact]
    public void PluginCatalog_DoesNotTreatHandheldPerformanceAsAPlugin()
    {
        var definitions = SteamLoaderPluginCatalog.BuildDefinitions();

        Assert.DoesNotContain(definitions, plugin => plugin.Id == "handheld-performance");
    }

    [Fact]
    public void PluginCatalog_KeepsFpsOverlaySeparateFromHandheldPerformance()
    {
        var definitions = SteamLoaderPluginCatalog.BuildDefinitions();
        var plugin = Assert.Single(definitions, candidate => candidate.Id == "performance");

        Assert.Equal("FPS Overlay", plugin.Title);
        Assert.DoesNotContain(definitions, candidate => candidate.Id == "handheld-performance");
    }

    [Fact]
    public void HandheldPerformanceModels_DoNotExposeRgbProperties()
    {
        var modelTypes = new[]
        {
            typeof(HandheldPerformanceSnapshot),
            typeof(HandheldPerformanceSettings),
            typeof(HandheldHardwareCommand)
        };

        Assert.All(
            modelTypes,
            modelType => Assert.DoesNotContain(
                modelType.GetProperties(),
                property => property.Name.Contains("Rgb", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("battery", 15)]
    [InlineData("balanced", 20)]
    [InlineData("performance", 28)]
    public void MsiClawA8Profile_ExposesVerifiedModes(string modeId, int watts)
    {
        var profile = HandheldDeviceCatalog.CreateMsiClawA8();
        var mode = Assert.Single(profile.Modes, candidate => candidate.Id == modeId);

        Assert.Equal(watts, mode.Watts);
    }

    [Fact]
    public void LegacyGlobalTdp_IsUsedForBothPowerSources()
    {
        var settings = new HandheldPerformanceSettings(TdpWatts: 24);

        Assert.Equal(24, HandheldPerformanceService.ResolveSettingsTdp(settings, "ac"));
        Assert.Equal(24, HandheldPerformanceService.ResolveSettingsTdp(settings, "battery"));
    }

    [Fact]
    public void SeparateGlobalTdp_ResolvesCurrentPowerSource()
    {
        var settings = new HandheldPerformanceSettings(
            TdpWatts: 20,
            AcTdpWatts: 30,
            BatteryTdpWatts: 17);

        Assert.Equal(30, HandheldPerformanceService.ResolveSettingsTdp(settings, "ac"));
        Assert.Equal(17, HandheldPerformanceService.ResolveSettingsTdp(settings, "battery"));
    }

    [Fact]
    public void LegacyGameProfile_IsUsedForBothPowerSources()
    {
        var profile = new HandheldGameTdpProfile(
            "game-1",
            "1",
            "Game",
            "game.exe",
            22,
            DateTimeOffset.UtcNow);

        Assert.Equal(22, HandheldPerformanceService.ResolveProfileTdp(profile, "ac"));
        Assert.Equal(22, HandheldPerformanceService.ResolveProfileTdp(profile, "battery"));
    }
}
