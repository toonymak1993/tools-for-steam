using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsLibraryModeTests
{
    [Fact]
    public void DefaultMode_LeavesBothLibraryPluginsDisabled()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"));

            AssertLibraryMode(service.GetSnapshot(), storeSyncEnabled: false, omniLibraryEnabled: false);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void EnablingEitherMode_AtomicallyDisablesTheAlternative()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var service = CreateService(settingsPath);

            AssertLibraryMode(
                service.SetPluginEnabled("omnilibrary", enabled: true),
                storeSyncEnabled: false,
                omniLibraryEnabled: true);
            Assert.False(service.IsPluginEnabled("store-sync"));
            Assert.True(service.IsPluginEnabled("omnilibrary"));

            AssertLibraryMode(
                service.SetPluginEnabled("store-sync", enabled: true),
                storeSyncEnabled: true,
                omniLibraryEnabled: false);
            Assert.True(service.IsPluginEnabled("store-sync"));
            Assert.False(service.IsPluginEnabled("omnilibrary"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void DisablingActiveMode_DoesNotEnableTheAlternativeAndPersists()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var service = CreateService(settingsPath);

            AssertLibraryMode(
                service.SetPluginEnabled("store-sync", enabled: false),
                storeSyncEnabled: false,
                omniLibraryEnabled: false);

            var reloadedService = CreateService(settingsPath);
            AssertLibraryMode(
                reloadedService.GetSnapshot(),
                storeSyncEnabled: false,
                omniLibraryEnabled: false);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void InvalidLegacyState_WithBothEnabled_PreservesStoreSyncOnly()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "pluginEnabled": {
                    "store-sync": true,
                    "omnilibrary": true
                  }
                }
                """);

            var service = CreateService(settingsPath);

            AssertLibraryMode(service.GetSnapshot(), storeSyncEnabled: true, omniLibraryEnabled: false);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ExistingStoreSyncSelection_DoesNotAutoEnableOmniLibrary()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "pluginEnabled": {
                    "store-sync": true
                  }
                }
                """);

            var service = CreateService(settingsPath);

            AssertLibraryMode(service.GetSnapshot(), storeSyncEnabled: true, omniLibraryEnabled: false);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void AssertLibraryMode(
        SteamLoaderGeneralSettingsSnapshot snapshot,
        bool storeSyncEnabled,
        bool omniLibraryEnabled)
    {
        Assert.Equal(storeSyncEnabled, GetPlugin(snapshot, "store-sync").Enabled);
        Assert.Equal(omniLibraryEnabled, GetPlugin(snapshot, "omnilibrary").Enabled);
        Assert.False(storeSyncEnabled && omniLibraryEnabled);
    }

    private static SteamLoaderPluginSettingsState GetPlugin(
        SteamLoaderGeneralSettingsSnapshot snapshot,
        string pluginId)
    {
        return Assert.Single(snapshot.Plugins, plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
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
