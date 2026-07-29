using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class HandheldDeviceCatalogTests
{
    [Fact]
    public void UnsupportedDevice_OemSnapshotRemainsInactive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-unsupported-oem-{Guid.NewGuid():N}");
        try
        {
            var service = new HandheldSystemControlService(root);
            var snapshot = service.GetOemSoftware(
                HandheldDeviceCatalog.CreateMsiClawA8(detected: false));

            Assert.False(snapshot.Supported);
            Assert.False(snapshot.Detected);
            Assert.False(snapshot.Running);
            Assert.False(snapshot.ControlActive);
            Assert.Equal("OEM control is not available for this device.", snapshot.StatusText);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("System", false)]
    [InlineData("lsass", false)]
    [InlineData("steamwebhelper", false)]
    [InlineData("MSI.CentralServer", true)]
    [InlineData("GamingCenter", true)]
    [InlineData("DCv2", true)]
    public void MsiProcessMetadata_IsReadOnlyForPlausibleCandidates(
        string processName,
        bool expected)
    {
        Assert.Equal(
            expected,
            HandheldSystemControlService.ShouldInspectMsiCenterProcessMetadata(processName));
    }

    [Fact]
    public void MsiClawDirectInputState_MapsTheCompleteXboxLayoutWithoutConsumingBackPaddles()
    {
        var buttons = new bool[128];
        buttons[0] = true;  // X
        buttons[1] = true;  // A
        buttons[4] = true;  // LB
        buttons[8] = true;  // Back
        buttons[10] = true; // LS
        buttons[15] = true; // M1
        buttons[16] = true; // M2
        var state = new MsiClawDirectInputSnapshot(
            X: 65535,
            Y: 0,
            Z: 32768,
            RotationX: 65535,
            RotationY: 0,
            RotationZ: 65535,
            PointOfView: 4500,
            Buttons: buttons);

        var mapped = MsiClawDirectInputSource.ConvertState(state);

        Assert.Equal(0x5169u, mapped.Buttons);
        Assert.Equal(byte.MaxValue, mapped.LeftTrigger);
        Assert.Equal(0, mapped.RightTrigger);
        Assert.Equal(short.MaxValue, mapped.LeftX);
        Assert.Equal(short.MaxValue, mapped.LeftY);
        Assert.Equal(0, mapped.RightX);
        Assert.Equal(short.MinValue, mapped.RightY);
        Assert.True(mapped.M1);
        Assert.True(mapped.M2);
    }

    [Fact]
    public void MsiClawDirectInputState_FiltersNeutralStickAndTriggerNoise()
    {
        var mapped = MsiClawDirectInputSource.ConvertState(new MsiClawDirectInputSnapshot(
            X: 33000,
            Y: 32500,
            Z: 35000,
            RotationX: 1200,
            RotationY: 1900,
            RotationZ: 30000,
            PointOfView: -1,
            Buttons: new bool[128]));

        Assert.Equal(0, mapped.LeftTrigger);
        Assert.Equal(0, mapped.RightTrigger);
        Assert.Equal(0, mapped.LeftX);
        Assert.Equal(0, mapped.LeftY);
        Assert.Equal(0, mapped.RightX);
        Assert.Equal(0, mapped.RightY);
        Assert.Equal(0u, mapped.Buttons);
    }

    [Fact]
    public void MsiClawDirectInputState_DigitalTriggerButtonsDoNotBecomeFaceOrMenuButtons()
    {
        var buttons = new bool[128];
        buttons[6] = true; // LT full-click companion signal
        buttons[7] = true; // RT full-click companion signal

        var mapped = MsiClawDirectInputSource.ConvertState(new MsiClawDirectInputSnapshot(
            X: 32767,
            Y: 32767,
            Z: 32767,
            RotationX: 65535,
            RotationY: 65535,
            RotationZ: 32766,
            PointOfView: -1,
            Buttons: buttons));

        Assert.Equal(byte.MaxValue, mapped.LeftTrigger);
        Assert.Equal(byte.MaxValue, mapped.RightTrigger);
        Assert.Equal(0u, mapped.Buttons);
    }

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
    public void PluginCatalog_KeepsRtssPerformanceSeparateFromHandheldPerformance()
    {
        var definitions = SteamLoaderPluginCatalog.BuildDefinitions();
        var plugin = Assert.Single(definitions, candidate => candidate.Id == "performance");

        Assert.Equal("Performance", plugin.Title);
        Assert.DoesNotContain(definitions, candidate => candidate.Id == "handheld-performance");
    }

    [Fact]
    public void MsiClawA8Profile_ExposesReusableLightingCapabilities()
    {
        var lighting = HandheldDeviceCatalog.CreateMsiClawA8().Lighting;

        Assert.True(lighting.Supported);
        Assert.True(lighting.SeparateZones);
        Assert.Equal(0, lighting.MinimumBrightness);
        Assert.Equal(100, lighting.MaximumBrightness);
        Assert.Equal(["solid", "dual-zone"], lighting.Effects);
    }

    [Fact]
    public void MsiClawA8Profile_ExposesDeviceSpecificOemButtons()
    {
        var oem = HandheldDeviceCatalog.CreateMsiClawA8().OemSoftware;

        Assert.True(oem.Supported);
        Assert.Equal("MSI Center M", oem.SoftwareName);
        Assert.Equal(["m1", "m2", "msi-center", "quick-settings"], oem.Buttons.Select(button => button.Id));
    }

    [Fact]
    public void MsiClawA8Profile_ExposesDeviceSpecificVibrationCapabilities()
    {
        var controller = HandheldDeviceCatalog.CreateMsiClawA8().Controller;

        Assert.True(controller.VibrationSupported);
        Assert.Equal(0, controller.MinimumVibrationStrengthPercent);
        Assert.Equal(100, controller.MaximumVibrationStrengthPercent);
        Assert.Equal(70, controller.DefaultVibrationStrengthPercent);
    }

    [Fact]
    public void MsiDirectInputEndpoint_DoesNotDriveXboxBackOrMenuShortcuts()
    {
        Assert.False(HidMenuButtonMonitor.CanContributeToShortcutState(
            @"\\?\HID#VID_0DB0&PID_1902&MI_00#test"));
        Assert.True(HidMenuButtonMonitor.CanContributeToShortcutState(
            @"\\?\HID#VID_045E&PID_028E#virtual"));
    }

    [Fact]
    public void OemButtonLiveDetect_PersistsTheObservedMsiInputAndChosenAction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-oem-buttons-{Guid.NewGuid():N}");
        try
        {
            var service = new HandheldSystemControlService(root, () => (true, "Test button mode ready."));
            var device = HandheldDeviceCatalog.CreateMsiClawA8(detected: true);

            service.StartButtonCapture(device, "m1");
            service.ObserveOemInput(device, new HidMenuButtonReport(
                1,
                [],
                false,
                8,
                @"\\?\HID#VID_0DB0&PID_1901&MI_02#test",
                "keyboard",
                "keyboard:vk-7A:scan-57:flags-0",
                true,
                "VK 0x7A, scan 0x57"));
            service.SetButtonBinding(device, "m1", "custom-shortcut", "Ctrl+Shift+F12");

            var binding = Assert.Single(service.GetOemSoftware(device).Buttons, button => button.ButtonId == "m1");
            Assert.True(binding.Configured);
            Assert.Contains("keyboard:vk-7A", binding.InputCode);
            Assert.Equal("custom-shortcut", binding.ActionId);
            Assert.Equal("CTRL+SHIFT+F12", binding.CustomShortcut);
            Assert.Contains(
                service.GetOemSoftware(device).Actions,
                action => action.Id == "focus-steam" && action.Title == "Focus Steam");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MsiClawA8RgbReport_UsesVerifiedFirmware308Layout()
    {
        var report = MsiClawA8LightingController.BuildReport(
            75, (0x11, 0x22, 0x33), (0xAA, 0xBB, 0xCC), (0x44, 0x55, 0x66));

        Assert.Equal(64, report.Length);
        Assert.Equal([0x0F, 0x00, 0x00, 0x3C, 0x21, 0x01, 0x02, 0x4A, 0x24, 0x00, 0x01, 0x09, 0x03, 75], report[..14]);
        Assert.Equal([0xAA, 0xBB, 0xCC], report[14..17]);
        Assert.Equal([0x11, 0x22, 0x33], report[26..29]);
        Assert.Equal([0x44, 0x55, 0x66], report[38..41]);
        Assert.All(report[41..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void MsiClawA8ButtonModeReports_UseFirmware308MKeysAndDirectInput()
    {
        var m1 = MsiClawControllerProtocol.BuildMKeyReport(0x00, 0xBA);
        var m2 = MsiClawControllerProtocol.BuildMKeyReport(0x01, 0x63);
        var directInput = MsiClawControllerProtocol.BuildSwitchModeReport(0x02);
        var xInput = MsiClawControllerProtocol.BuildSwitchModeReport(0x01);

        Assert.Equal(64, m1.Length);
        Assert.Equal([0x0F, 0x00, 0x00, 0x3C, 0x21, 0x01, 0x00, 0xBA, 0x05, 0x01, 0x00, 0x00, 0x11, 0x00], m1[..14]);
        Assert.Equal([0x01, 0x63], m2[6..8]);
        Assert.Equal([0x0F, 0x00, 0x00, 0x3C, 0x24, 0x02, 0x00], directInput[..7]);
        Assert.Equal([0x0F, 0x00, 0x00, 0x3C, 0x24, 0x01, 0x00], xInput[..7]);
    }

    [Fact]
    public void MsiClawA8VibrationReport_PreservesScaledMotorStrength()
    {
        Assert.Equal(128, MsiClawControllerProtocol.ScaleVibration(255, 50));
        Assert.Equal(0, MsiClawControllerProtocol.ScaleVibration(255, 0));

        var report = MsiClawControllerProtocol.BuildVibrationReport(largeMotor: 128, smallMotor: 64);
        Assert.Equal([0x05, 0x01, 0x00, 0x00, 64, 128, 0, 0, 0, 0, 0], report);
    }

    [Fact]
    public void ControllerVibrationStrength_IsPersistedPerDeviceProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-controller-settings-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(70, HandheldControllerSettingsStore.ReadVibrationStrengthPercent(root, "msi-claw-a8", 70));
            HandheldControllerSettingsStore.WriteVibrationStrengthPercent(root, "msi-claw-a8", 40);
            HandheldControllerSettingsStore.WriteVibrationStrengthPercent(root, "future-device", 90);

            Assert.Equal(40, HandheldControllerSettingsStore.ReadVibrationStrengthPercent(root, "msi-claw-a8", 70));
            Assert.Equal(90, HandheldControllerSettingsStore.ReadVibrationStrengthPercent(root, "future-device", 50));
            Assert.True(HandheldControllerSettingsStore.ReadUiHapticsEnabled(root, "msi-claw-a8"));

            HandheldControllerSettingsStore.WriteUiHapticsEnabled(root, "msi-claw-a8", false, 70);
            Assert.False(HandheldControllerSettingsStore.ReadUiHapticsEnabled(root, "msi-claw-a8"));
            Assert.Equal(40, HandheldControllerSettingsStore.ReadVibrationStrengthPercent(root, "msi-claw-a8", 70));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("move", 0, 150, 38)]
    [InlineData("confirm", 150, 190, 72)]
    [InlineData("back", 100, 165, 55)]
    public void UiHapticPatterns_AreShortAndBounded(
        string kind,
        byte expectedLarge,
        byte expectedSmall,
        int expectedDuration)
    {
        var command = HandheldUiHapticPatternCatalog.Create("msi-claw-a8", kind, 123);

        Assert.Equal(123, command.Nonce);
        Assert.Equal(expectedLarge, command.LargeMotor);
        Assert.Equal(expectedSmall, command.SmallMotor);
        Assert.Equal(expectedDuration, command.DurationMilliseconds);
        Assert.InRange(command.DurationMilliseconds, 10, 150);
    }

    [Theory]
    [InlineData("handheld-ui-haptic.json")]
    [InlineData("HANDHELD-UI-HAPTIC.JSON")]
    [InlineData("handheld-ui-haptic.json.0123456789abcdef.tmp")]
    [InlineData("data/handheld-ui-haptic.json.0123456789abcdef.tmp")]
    public void UiHapticWatcher_AcceptsAtomicCommandFileEvents(string name)
    {
        Assert.True(ViiperHandheldControllerBridge.IsUiHapticCommandFileEvent(name));
    }

    [Theory]
    [InlineData("handheld-controller-settings.json")]
    [InlineData("other-handheld-ui-haptic.json")]
    [InlineData("")]
    public void UiHapticWatcher_IgnoresUnrelatedFileEvents(string name)
    {
        Assert.False(ViiperHandheldControllerBridge.IsUiHapticCommandFileEvent(name));
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
