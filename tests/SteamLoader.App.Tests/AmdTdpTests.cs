using SteamLoader.App.Infrastructure.Handheld;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class AmdTdpTests
{
    [Theory]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M           ", "StrixPoint", 15, 54)]
    [InlineData("AMD Ryzen AI 9 365 w/ Radeon 880M", "StrixPoint", 15, 54)]
    [InlineData("AMD Ryzen AI 7 350 w/ Radeon 860M", "KrackanPoint", 15, 54)]
    [InlineData("AMD Ryzen AI Max+ 395 w/ Radeon 8060S", "StrixHalo", 45, 120)]
    [InlineData("AMD Ryzen Z1 Extreme", "Phoenix", 9, 30)]
    [InlineData("AMD Ryzen Z1", "Phoenix2", 9, 30)]
    [InlineData("AMD Ryzen Z2 Extreme", "StrixPoint", 15, 35)]
    [InlineData("AMD Ryzen Z2 A", "VanGogh", 6, 20)]
    [InlineData("AMD Ryzen Z2 Go", "Rembrandt", 15, 30)]
    [InlineData("AMD Ryzen Z2", "HawkPoint", 15, 30)]
    [InlineData("AMD Custom APU 0405", "VanGogh", 3, 15)]
    [InlineData("AMD Ryzen 7 7840U w/ Radeon 780M Graphics", "Phoenix", 15, 30)]
    [InlineData("AMD Ryzen 7 8840U w/ Radeon 780M Graphics", "Phoenix", 15, 30)]
    [InlineData("AMD Ryzen 9 7940HS w/ Radeon 780M Graphics", "Phoenix", 15, 54)]
    [InlineData("AMD Ryzen 7 6800U with Radeon Graphics", "Rembrandt", 15, 28)]
    [InlineData("AMD Ryzen 7 7735U with Radeon Graphics", "Rembrandt", 15, 28)]
    [InlineData("AMD Ryzen 7 5800U with Radeon Graphics", "Renoir", 10, 25)]
    [InlineData("AMD Ryzen 7 7730U with Radeon Graphics", "Renoir", 10, 25)]
    [InlineData("AMD Ryzen 7 4800U with Radeon Graphics", "Renoir", 10, 25)]
    [InlineData("AMD Ryzen 5 7520U with Radeon Graphics", "Mendocino", 8, 15)]
    [InlineData("AMD Ryzen 7 3700U with Radeon Vega Mobile Gfx", "Raven", 12, 25)]
    public void Catalog_ResolvesKnownAmdApus(string cpuName, string platform, int minimum, int maximum)
    {
        var tdp = AmdTdpCatalog.Resolve(cpuName);

        Assert.NotNull(tdp);
        Assert.Equal(platform, tdp!.Platform);
        Assert.Equal(minimum, tdp.MinimumWatts);
        Assert.Equal(maximum, tdp.MaximumWatts);
        Assert.InRange(tdp.DefaultWatts, minimum, maximum);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Intel(R) Core(TM) Ultra 7 258V")]
    [InlineData("AMD Ryzen 9 7945HX with Radeon Graphics")]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor")]
    [InlineData("AMD Ryzen 9 5950X 16-Core Processor")]
    public void Catalog_LeavesUnknownProcessorsWithoutTdp(string cpuName)
    {
        Assert.Null(AmdTdpCatalog.Resolve(cpuName));
    }

    [Fact]
    public void GenericLimits_NeverExceedChipMaximum()
    {
        var tdp = AmdTdpCatalog.Resolve("AMD Ryzen AI 9 HX 370 w/ Radeon 890M")!;

        Assert.Equal(20, tdp.ResolveSppt(15));
        Assert.Equal(25, tdp.ResolveFppt(15));
        Assert.Equal(54, tdp.ResolveSppt(54));
        Assert.Equal(54, tdp.ResolveFppt(54));
        Assert.Equal(54, tdp.ResolveSppt(50));
        Assert.Equal(54, tdp.ResolveFppt(50));
        for (var watts = tdp.MinimumWatts; watts <= tdp.MaximumWatts; watts++)
        {
            AmdTdpController.ValidateLimits(tdp, watts, tdp.ResolveSppt(watts), tdp.ResolveFppt(watts));
        }
    }

    [Fact]
    public void ClawA8_KeepsOemBoostLimits()
    {
        var claw = HandheldDeviceCatalog.CreateMsiClawA8(detected: true);

        Assert.True(HandheldDeviceCatalog.SupportsTdp(claw));
        Assert.True(HandheldDeviceCatalog.IsSupported(claw));
        Assert.Equal(20, claw.Tdp!.ResolveSppt(15));
        Assert.Equal(28, claw.Tdp.ResolveFppt(15));
        Assert.Equal(40, claw.Tdp.ResolveSppt(35));
        Assert.Equal(48, claw.Tdp.ResolveFppt(35));
        Assert.Equal(33, claw.Tdp.ResolveSppt(28));
        Assert.Equal(41, claw.Tdp.ResolveFppt(28));
    }

    [Fact]
    public void ValidateLimits_RejectsOutOfRangeAndInvertedLimits()
    {
        var tdp = AmdTdpCatalog.Resolve("AMD Ryzen Z1 Extreme")!;

        Assert.Throws<ArgumentOutOfRangeException>(() => AmdTdpController.ValidateLimits(tdp, 8, 8, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmdTdpController.ValidateLimits(tdp, 31, 31, 31));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmdTdpController.ValidateLimits(tdp, 20, 18, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmdTdpController.ValidateLimits(tdp, 20, 25, 22));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmdTdpController.ValidateLimits(tdp, 28, 30, 40));
    }

    [Fact]
    public void Resolve_AyaneoWithStrixPoint_IsTdpOnlyGenericDevice()
    {
        var device = HandheldDeviceCatalog.Resolve("AYANEO", "AYANEO 3", "AMD Ryzen AI 9 HX 370 w/ Radeon 890M");

        Assert.Equal(HandheldDeviceCatalog.GenericAmdDeviceId, device.Id);
        Assert.Equal("AYANEO 3", device.DisplayName);
        Assert.True(HandheldDeviceCatalog.SupportsTdp(device));
        Assert.False(HandheldDeviceCatalog.IsSupported(device));
        Assert.False(device.Lighting.Supported);
        Assert.False(device.OemSoftware.Supported);
        Assert.False(device.Controller.VibrationSupported);
        // AYASpace's own manual range on the AYANEO 3, not AMD's 15-54 W.
        Assert.Equal(5, device.MinimumTdpWatts);
        Assert.Equal(35, device.MaximumTdpWatts);
        Assert.Equal(HandheldTdpLimitSource.Manufacturer, device.Tdp!.LimitSource);
        Assert.Equal(35, device.Tdp.ResolveFppt(35));
        Assert.Equal(["battery", "balanced", "performance"], device.Modes.Select(mode => mode.Id));
        Assert.All(device.Modes, mode => Assert.InRange(mode.Watts, device.MinimumTdpWatts, device.MaximumTdpWatts));
    }

    [Fact]
    public void UnknownMaker_LaptopChipStopsAtAmdDefault_HandheldChipKeepsItsRange()
    {
        var laptopChip = HandheldDeviceCatalog.Resolve("Some Vendor", "X1", "AMD Ryzen AI 9 HX 370 w/ Radeon 890M");
        Assert.Equal(15, laptopChip.MinimumTdpWatts);
        Assert.Equal(28, laptopChip.MaximumTdpWatts);
        Assert.Equal(28, laptopChip.Tdp!.ResolveFppt(28));
        Assert.Equal(HandheldTdpLimitSource.AmdDefault, laptopChip.Tdp.LimitSource);

        var handheldChip = HandheldDeviceCatalog.Resolve("Some Vendor", "X2", "AMD Ryzen Z1 Extreme");
        Assert.Equal((9, 30), (handheldChip.MinimumTdpWatts, handheldChip.MaximumTdpWatts));
        Assert.Equal(HandheldTdpLimitSource.AmdChip, handheldChip.Tdp!.LimitSource);
    }

    [Theory]
    [InlineData("Valve", "Jupiter", "", "AMD Custom APU 0405", 3, 15, 15)]
    [InlineData("Valve", "Galileo", "", "AMD Custom APU 0932", 3, 15, 15)]
    [InlineData("ASUSTeK COMPUTER INC.", "RC72LA", "", "AMD Ryzen Z1 Extreme", 7, 30, 25)]
    [InlineData("ASUSTeK COMPUTER INC.", "RC73XA", "", "AMD Ryzen AI Z2 Extreme", 7, 35, 25)]
    [InlineData("ASUSTeK COMPUTER INC.", "RC73YA", "", "AMD Ryzen Z2 A", 7, 20, 20)]
    [InlineData("LENOVO", "LNVNB161216", "83E1", "AMD Ryzen Z1 Extreme", 5, 30, 30)]
    [InlineData("LENOVO", "LNVNB161216", "83N0", "AMD Ryzen Z2 Extreme", 5, 35, 35)]
    [InlineData("LENOVO", "LNVNB161216", "83L3", "AMD Ryzen Z2 Go", 5, 30, 30)]
    public void KnownHandhelds_UseManufacturerLimits(string manufacturer, string product, string model, string cpu, int min, int ac, int battery)
    {
        var device = HandheldDeviceCatalog.Resolve(manufacturer, product, cpu, null, model);

        Assert.True(HandheldDeviceCatalog.SupportsTdp(device));
        Assert.Equal(HandheldTdpLimitSource.Manufacturer, device.Tdp!.LimitSource);
        Assert.Equal(min, device.MinimumTdpWatts);
        Assert.Equal(ac, device.Tdp.MaximumFor("ac"));
        Assert.Equal(battery, device.Tdp.MaximumFor("battery"));
        Assert.True(device.Tdp.ResolveFppt(ac, "ac") <= ac);
        Assert.True(device.Tdp.ResolveFppt(battery, "battery") <= battery);
    }

    [Fact]
    public void VendorAppConfigWinsAndBatteryLimitCapsBoost()
    {
        const string ayaSpace = """
            [ { "id": 0, "tdp": { "curTDP": 28, "maxTDP": 35, "minTDP": 5 } },
              { "id": 1, "tdp": { "curTDP": 35, "maxTDP": 35, "minTDP": 5 } } ]
            """;
        var vendor = HandheldManufacturerLimits.ParseAyaSpace(ayaSpace);
        Assert.NotNull(vendor);
        Assert.Equal((5, 35), (vendor!.MinimumWatts, vendor.MaximumWatts));

        var other = HandheldDeviceCatalog.Resolve("AYANEO", "AYANEO 2S", "AMD Ryzen 7 7840U w/ Radeon 780M Graphics", vendor);
        Assert.Equal(HandheldTdpLimitSource.VendorApp, other.Tdp!.LimitSource);
        Assert.Equal(5, other.MinimumTdpWatts);
        Assert.Equal(30, other.MaximumTdpWatts); // never above the chip's own maximum

        var ally = HandheldDeviceCatalog.Resolve("ASUSTeK COMPUTER INC.", "RC71L", "AMD Ryzen Z1 Extreme");
        Assert.Equal(30, ally.Tdp!.MaximumFor("ac"));
        Assert.Equal(25, ally.Tdp.MaximumFor("battery"));
        Assert.Equal(25, ally.Tdp.ResolveSppt(22, "battery"));
        Assert.Equal(25, ally.Tdp.ResolveFppt(22, "battery"));
        Assert.Equal(27, ally.Tdp.ResolveSppt(22, "ac"));
        Assert.Equal(30, ally.Tdp.ResolveFppt(22, "ac"));

        Assert.Null(HandheldManufacturerLimits.ParseAyaSpace("{}"));
        Assert.Null(HandheldManufacturerLimits.ParseAyaSpace("not json"));
    }

    [Fact]
    public void Resolve_ClawA8BoardWins_AndUnknownCpuStaysUnsupported()
    {
        Assert.Equal("msi-claw-a8", HandheldDeviceCatalog.Resolve("Micro-Star International Co., Ltd.", "MS-1T8K", "AMD Ryzen Z2 Extreme").Id);

        var intel = HandheldDeviceCatalog.Resolve("MSI", "MS-1T52", "Intel(R) Core(TM) Ultra 7 258V");
        Assert.False(HandheldDeviceCatalog.SupportsTdp(intel));
        Assert.False(intel.IsDetected);
    }

    [Theory]
    [InlineData(31u, "StrixPoint", 0x03B10928u, 0x03B10978u, 0x14u, true)]
    [InlineData(33u, "KrackanPoint", 0x03B10928u, 0x03B10978u, 0x14u, false)]
    [InlineData(23u, "Phoenix", 0x03B10528u, 0x03B10578u, 0x14u, false)]
    [InlineData(12u, "VanGogh", 0x03B10528u, 0x03B10578u, 0x14u, false)]
    [InlineData(6u, "RavenRidge", 0x03B10528u, 0x03B10564u, 0x1Au, false)]
    public void SmuFamilies_UseRyzenAdjMailboxes(uint codeName, string name, uint command, uint response, uint stapm, bool echo)
    {
        var family = AmdSmuFamilies.Find(codeName);

        Assert.NotNull(family);
        Assert.Equal(name, family!.Name);
        Assert.Equal(command, family.CommandAddress);
        Assert.Equal(response, family.ResponseAddress);
        Assert.Equal(0x03B10998u, family.ArgumentsAddress);
        Assert.Equal(stapm, family.StapmCommand);
        Assert.Equal(echo, family.EchoesArgument);
    }

    [Theory]
    [InlineData(28u)] // Dragon Range
    [InlineData(16u)] // Raphael desktop
    [InlineData(uint.MaxValue)]
    public void SmuFamilies_RejectUnverifiedFamilies(uint codeName)
    {
        Assert.Null(AmdSmuFamilies.Find(codeName));
    }

    [Fact]
    public void TdpConfirmation_FollowsTheHelpersAnswer()
    {
        var now = DateTimeOffset.UtcNow;
        var command = new HandheldHardwareCommand(now.AddSeconds(-2).ToUnixTimeMilliseconds(), "amd-apu", "AYANEO 3", "set-tdp", 20, SpptWatts: 25, FpptWatts: 30);

        var pending = HandheldPerformanceService.ResolveTdpConfirmation(command, new HandheldHardwareStatus(), now);
        Assert.Equal("pending", pending.State);
        Assert.Equal(20, pending.SplWatts);

        var silent = HandheldPerformanceService.ResolveTdpConfirmation(command, new HandheldHardwareStatus(), now.AddSeconds(10));
        Assert.Equal("no-helper", silent.State);

        var confirmed = HandheldPerformanceService.ResolveTdpConfirmation(command,
            new HandheldHardwareStatus(command.Nonce, true, true, 20, "StrixPoint", "ok", now, "set-tdp", false, "", 25, 30), now);
        Assert.Equal(("confirmed", 20, 25, 30, "StrixPoint"), (confirmed.State, confirmed.SplWatts, confirmed.SpptWatts, confirmed.FpptWatts, confirmed.Chip));

        var failed = HandheldPerformanceService.ResolveTdpConfirmation(command,
            new HandheldHardwareStatus(command.Nonce, false, true, 0, "", "SMU rejected the STAPM limit with status 0xFE.", now, "set-tdp"), now);
        Assert.Equal("failed", failed.State);
        Assert.Contains("0xFE", failed.Message);

        Assert.Equal("none", HandheldPerformanceService.ResolveTdpConfirmation(null, new HandheldHardwareStatus(), now).State);
    }

    [Theory]
    [InlineData("AYASpace", "AYASpace")]
    [InlineData("ArmouryCrate.UserSessionHelper", "Armoury Crate")]
    [InlineData("LegionSpace", "Legion Space")]
    [InlineData("HandheldCompanion", "Handheld Companion")]
    [InlineData("steam", null)]
    public void VendorApps_AreMatchedByPrefix(string processName, string? expected)
    {
        Assert.Equal(expected, HandheldVendorAppDetector.Match(processName));
    }
}
