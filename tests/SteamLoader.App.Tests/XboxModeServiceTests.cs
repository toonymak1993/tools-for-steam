using Microsoft.Win32;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class XboxModeServiceTests
{
    [Fact]
    public void UnsupportedPlatformRefusesEnableWithoutChangingCurrentValues()
    {
        var root = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}";
        var settingsPath = $@"{root}\GamingConfiguration";
        var backupPath = $@"{root}\XboxModeBackup";

        try
        {
            using (var original = Registry.CurrentUser.CreateSubKey(settingsPath))
            {
                original!.SetValue("GamingHomeApp", "Existing.Console_123!App", RegistryValueKind.String);
                original.SetValue("StartupToGamingHome", 0, RegistryValueKind.DWord);
            }

            var service = new XboxModeService(
                settingsPath,
                backupPath,
                _ => throw new InvalidOperationException("Session API must not be called."),
                () => "GCM.ToolsForSteam.XboxHost_test!App",
                () => new XboxModeSupportStatus(false, "Unsupported test platform."));

            var exception = Assert.Throws<InvalidOperationException>(() => service.SetStartupEnabled(true));

            Assert.Equal("Unsupported test platform.", exception.Message);
            Assert.Equal("Existing.Console_123!App", ReadValue(settingsPath, "GamingHomeApp"));
            Assert.Equal(0, ReadValue(settingsPath, "StartupToGamingHome"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ModeTransitionsAndUninstallRestoreTheOriginalWindowsConfiguration()
    {
        var root = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}";
        var settingsPath = $@"{root}\GamingConfiguration";
        var backupPath = $@"{root}\XboxModeBackup";
        var activeStates = new List<bool>();

        try
        {
            using (var original = Registry.CurrentUser.CreateSubKey(settingsPath))
            {
                original!.SetValue("GamingHomeApp", "Existing.Console_123!App", RegistryValueKind.String);
                original.SetValue("StartupToGamingHome", 1, RegistryValueKind.DWord);
            }

            var service = new XboxModeService(
                settingsPath,
                backupPath,
                activeStates.Add,
                () => "GCM.ToolsForSteam.XboxHost_test!App");

            service.SetStartupEnabled(true);
            Assert.Equal("GCM.ToolsForSteam.XboxHost_test!App", ReadValue(settingsPath, "GamingHomeApp"));
            Assert.Equal(1, ReadValue(settingsPath, "StartupToGamingHome"));

            service.SetStartupEnabled(false);
            Assert.Equal("GCM.ToolsForSteam.XboxHost_test!App", ReadValue(settingsPath, "GamingHomeApp"));
            Assert.Equal(0, ReadValue(settingsPath, "StartupToGamingHome"));

            service.RestoreOnUninstall();
            Assert.Equal("Existing.Console_123!App", ReadValue(settingsPath, "GamingHomeApp"));
            Assert.Equal(1, ReadValue(settingsPath, "StartupToGamingHome"));
            using var removedBackup = Registry.CurrentUser.OpenSubKey(backupPath);
            Assert.Null(removedBackup);
            Assert.Equal(new[] { true, false, false }, activeStates);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static object? ReadValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName);
    }
}
