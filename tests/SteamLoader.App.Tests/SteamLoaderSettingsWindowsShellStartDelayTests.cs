using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsWindowsShellStartDelayTests
{
    [Fact]
    public void WindowsShellStartDelay_PersistsAcrossServiceReload()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var firstService = CreateService(settingsPath);
            var secondService = CreateService(settingsPath);

            var snapshot = firstService.SetWindowsShellStartDelaySeconds(9);

            Assert.Equal(9, snapshot.WindowsShellStartDelaySeconds);
            Assert.Equal(9, firstService.GetSnapshot().WindowsShellStartDelaySeconds);
            Assert.Equal(9, secondService.GetSnapshot().WindowsShellStartDelaySeconds);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void WindowsShellStartDelay_FallsBackToLegacySplashDelay()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "splashScreen": {
                    "showText": true,
                    "extraCloseDelaySeconds": 7
                  }
                }
                """);

            var service = CreateService(settingsPath);

            Assert.Equal(7, service.GetSnapshot().WindowsShellStartDelaySeconds);
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
