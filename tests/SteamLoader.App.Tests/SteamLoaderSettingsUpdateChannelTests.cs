using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsUpdateChannelTests
{
    [Fact]
    public void UpdateChannel_PersistsAcrossServiceReload()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var firstService = CreateService(settingsPath);
            var secondService = CreateService(settingsPath);

            var setChannel = firstService.SetUpdateChannel("beta");

            Assert.Equal("beta", setChannel);
            Assert.Equal("beta", firstService.GetUpdateChannel());
            Assert.Equal("beta", secondService.GetUpdateChannel());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void UpdateChannel_InvalidValueFallsBackToStable()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var service = CreateService(settingsPath);

            var setChannel = service.SetUpdateChannel("preview-preview");

            Assert.Equal("stable", setChannel);
            Assert.Equal("stable", service.GetUpdateChannel());
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
