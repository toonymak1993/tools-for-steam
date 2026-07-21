using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsControllerShortcutTests
{
    [Fact]
    public void ControllerShortcuts_DefaultsPreserveCurrentOverlayBehavior()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"));

            var shortcuts = service.GetSnapshot().ControllerShortcuts;

            Assert.Equal("back", shortcuts.SteamButton);
            Assert.Equal("start", shortcuts.InGameButton);
            Assert.Equal(["back"], shortcuts.SteamMenuButtons);
            Assert.Equal(["back"], shortcuts.SteamQuickAccessButtons);
            Assert.Equal(["start"], shortcuts.InGameOverlayButtons);
            Assert.Equal(["start"], shortcuts.InGameQuickAccessButtons);
            Assert.Equal(1050, shortcuts.SteamHoldMilliseconds);
            Assert.Equal(1050, shortcuts.InGameOverlayHoldMilliseconds);
            Assert.Equal(3300, shortcuts.InGameQuickAccessHoldMilliseconds);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ControllerShortcuts_PersistIndependentCombinationsPerAction()
    {
        var root = CreateTempRoot();
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var service = CreateService(settingsPath);
            service.SetControllerShortcutCombination("steam-menu", ["back", "left-bumper"]);
            service.SetControllerShortcutCombination("steam-quick-access", ["back", "right-bumper"]);
            service.SetControllerShortcutCombination("in-game-overlay", ["start", "x"]);
            service.SetControllerShortcutCombination("in-game-quick-access", ["start", "y", "right-bumper"]);

            var shortcuts = CreateService(settingsPath).GetControllerShortcutSettings();

            Assert.Equal(["back", "left-bumper"], shortcuts.SteamMenuButtons);
            Assert.Equal(["back", "right-bumper"], shortcuts.SteamQuickAccessButtons);
            Assert.Equal(["start", "x"], shortcuts.InGameOverlayButtons);
            Assert.Equal(["start", "y", "right-bumper"], shortcuts.InGameQuickAccessButtons);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ControllerShortcuts_MigrateLegacySingleButtonsToEveryRelatedAction()
    {
        var root = CreateTempRoot();
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "controllerShortcuts": {
                    "steamButton": "right-bumper",
                    "inGameButton": "x"
                  }
                }
                """);

            var shortcuts = CreateService(settingsPath).GetControllerShortcutSettings();

            Assert.Equal(["right-bumper"], shortcuts.SteamMenuButtons);
            Assert.Equal(["right-bumper"], shortcuts.SteamQuickAccessButtons);
            Assert.Equal(["x"], shortcuts.InGameOverlayButtons);
            Assert.Equal(["x"], shortcuts.InGameQuickAccessButtons);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ControllerShortcuts_RequireOneToThreeDifferentButtons()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"));

            Assert.Throws<InvalidOperationException>(() =>
                service.SetControllerShortcutCombination("steam-menu", []));
            Assert.Throws<InvalidOperationException>(() =>
                service.SetControllerShortcutCombination("steam-menu", ["back", "back"]));
            Assert.Throws<InvalidOperationException>(() =>
                service.SetControllerShortcutCombination(
                    "steam-menu",
                    ["back", "left-bumper", "right-bumper", "x"]));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ControllerCombination_MustBeHeldOnOneController()
    {
        const ushort back = 0x0020;
        const ushort leftBumper = 0x0100;

        Assert.False(ControllerShortcutService.IsXInputCombinationDown(
            ["back", "left-bumper"],
            [back, leftBumper]));
        Assert.True(ControllerShortcutService.IsXInputCombinationDown(
            ["back", "left-bumper"],
            [(ushort)(back | leftBumper)]));
    }

    [Fact]
    public void ControllerCombination_AllowsRawHidSystemButtonWithXInputChord()
    {
        const ushort leftBumper = 0x0100;
        const ushort rightBumper = 0x0200;

        Assert.True(ControllerShortcutService.IsHybridCombinationDown(
            ["back", "left-bumper"],
            [leftBumper],
            isHidBackDown: true,
            isHidMenuDown: false));
        Assert.False(ControllerShortcutService.IsHybridCombinationDown(
            ["back", "left-bumper", "right-bumper"],
            [leftBumper, rightBumper],
            isHidBackDown: true,
            isHidMenuDown: false));
        Assert.True(ControllerShortcutService.IsHybridCombinationDown(
            ["back", "start"],
            [],
            isHidBackDown: true,
            isHidMenuDown: true));
    }

    [Fact]
    public void ControllerInputRecorder_UsesTheControllerWithMostHeldButtons()
    {
        const ushort start = 0x0010;
        const ushort a = 0x1000;
        const ushort leftBumper = 0x0100;

        var buttons = ControllerShortcutService.ReadPressedButtonIds(
            [a, (ushort)(start | leftBumper)],
            isHidBackDown: true,
            isHidMenuDown: false);

        Assert.Equal(["back", "start", "left-bumper"], buttons);
    }

    [Fact]
    public void RawHidControllerUsages_MapBackAndRightStickAsOneChord()
    {
        var mask = HidMenuButtonMonitor.ConvertButtonUsagesToXInputMask([7, 10]);

        Assert.Equal((ushort)(0x0020 | 0x0080), mask);
        Assert.True(ControllerShortcutService.IsXInputCombinationDown(
            ["back", "right-stick"],
            [mask]));
    }

    [Fact]
    public void ControllerShortcuts_PersistButtonsAndHoldTimesAcrossReload()
    {
        var root = CreateTempRoot();
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var firstService = CreateService(settingsPath);
            firstService.SetControllerShortcutButton("steam", "right-bumper");
            firstService.SetControllerShortcutButton("in-game", "x");
            firstService.SetControllerShortcutHoldMilliseconds("steam", 750);
            firstService.SetControllerShortcutHoldMilliseconds("in-game-overlay", 1400);
            firstService.SetControllerShortcutHoldMilliseconds("in-game-quick-access", 2600);

            var shortcuts = CreateService(settingsPath).GetControllerShortcutSettings();

            Assert.Equal("right-bumper", shortcuts.SteamButton);
            Assert.Equal("x", shortcuts.InGameButton);
            Assert.Equal(750, shortcuts.SteamHoldMilliseconds);
            Assert.Equal(1400, shortcuts.InGameOverlayHoldMilliseconds);
            Assert.Equal(2600, shortcuts.InGameQuickAccessHoldMilliseconds);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ControllerShortcuts_KeepExtendedHoldAfterOverlayHold()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"));

            var snapshot = service.SetControllerShortcutHoldMilliseconds("in-game-overlay", 4000);

            Assert.Equal(4000, snapshot.ControllerShortcuts.InGameOverlayHoldMilliseconds);
            Assert.Equal(4250, snapshot.ControllerShortcuts.InGameQuickAccessHoldMilliseconds);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ControllerShortcuts_RejectUnsupportedButton()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"));

            Assert.Throws<InvalidOperationException>(() =>
                service.SetControllerShortcutButton("steam", "xbox-guide"));
            Assert.Equal(
                ControllerShortcutSettingsSnapshot.DefaultSteamButton,
                service.GetControllerShortcutSettings().SteamButton);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static SteamLoaderSettingsService CreateService(string settingsPath)
    {
        return new SteamLoaderSettingsService(
            new WindowsAutostartService("ToolsForSteamTests"),
            new WindowsShellService(),
            executablePath: @"C:\ToolsForSteam\ToolsForSteam.exe",
            shellLaunchArguments: "--shell",
            settingsPath: settingsPath);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
