using Microsoft.Win32;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class LegacyXboxModeCleanupTests
{
    [Fact]
    public void RestoresOriginalValuesWhenTfsIsStillTheGamingHomeApp()
    {
        var root = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}";
        var settingsPath = $@"{root}\GamingConfiguration";
        var backupPath = $@"{root}\XboxModeBackup";
        var leaveCalls = 0;

        try
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(settingsPath))
            {
                settings!.SetValue("GamingHomeApp", LegacyXboxModeCleanup.LegacyHostAumid, RegistryValueKind.String);
                settings.SetValue("StartupToGamingHome", 1, RegistryValueKind.DWord);
            }

            using (var backup = Registry.CurrentUser.CreateSubKey(backupPath))
            {
                backup!.SetValue("Created", 1, RegistryValueKind.DWord);
                backup.SetValue("GamingHomeAppExisted", 1, RegistryValueKind.DWord);
                backup.SetValue("GamingHomeApp", "Existing.Console_123!App", RegistryValueKind.String);
                backup.SetValue("StartupToGamingHomeExisted", 1, RegistryValueKind.DWord);
                backup.SetValue("StartupToGamingHome", 0, RegistryValueKind.DWord);
            }

            var found = new LegacyXboxModeCleanup(settingsPath, backupPath, () => leaveCalls++)
                .Run(leaveActiveSession: true);

            Assert.True(found);
            Assert.Equal(1, leaveCalls);
            Assert.Equal("Existing.Console_123!App", ReadValue(settingsPath, "GamingHomeApp"));
            Assert.Equal(0, ReadValue(settingsPath, "StartupToGamingHome"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(backupPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void LeavesALaterUserChosenHomeAppAloneButDropsTheSnapshot()
    {
        var root = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}";
        var settingsPath = $@"{root}\GamingConfiguration";
        var backupPath = $@"{root}\XboxModeBackup";

        try
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(settingsPath))
            {
                settings!.SetValue("GamingHomeApp", "Other.Launcher_1!App", RegistryValueKind.String);
                settings.SetValue("StartupToGamingHome", 1, RegistryValueKind.DWord);
            }

            using (var backup = Registry.CurrentUser.CreateSubKey(backupPath))
            {
                backup!.SetValue("Created", 1, RegistryValueKind.DWord);
                backup.SetValue("GamingHomeAppExisted", 0, RegistryValueKind.DWord);
                backup.SetValue("StartupToGamingHomeExisted", 0, RegistryValueKind.DWord);
            }

            var found = new LegacyXboxModeCleanup(
                    settingsPath,
                    backupPath,
                    () => throw new InvalidOperationException("Must not leave a session TFS does not own."))
                .Run(leaveActiveSession: true);

            Assert.True(found);
            Assert.Equal("Other.Launcher_1!App", ReadValue(settingsPath, "GamingHomeApp"));
            Assert.Equal(1, ReadValue(settingsPath, "StartupToGamingHome"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(backupPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void WithoutSnapshotRemovesTfsHomeAppAndStopsGamingHomeStartup()
    {
        var root = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}";
        var settingsPath = $@"{root}\GamingConfiguration";
        var backupPath = $@"{root}\XboxModeBackup";
        var leaveCalls = 0;

        try
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(settingsPath))
            {
                settings!.SetValue("GamingHomeApp", LegacyXboxModeCleanup.LegacyHostAumid, RegistryValueKind.String);
                settings.SetValue("StartupToGamingHome", 1, RegistryValueKind.DWord);
            }

            var found = new LegacyXboxModeCleanup(settingsPath, backupPath, () => leaveCalls++)
                .Run(leaveActiveSession: false);

            Assert.True(found);
            Assert.Equal(0, leaveCalls);
            Assert.Null(ReadValue(settingsPath, "GamingHomeApp"));
            Assert.Equal(0, ReadValue(settingsPath, "StartupToGamingHome"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ReportsNothingWhenTheOldIntegrationWasNeverUsed()
    {
        var root = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}";

        try
        {
            var found = new LegacyXboxModeCleanup(
                    $@"{root}\GamingConfiguration",
                    $@"{root}\XboxModeBackup",
                    () => throw new InvalidOperationException("Must not be called."))
                .Run(leaveActiveSession: true);

            Assert.False(found);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static object? ReadValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName);
    }
}
