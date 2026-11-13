using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsNotificationScaleTests
{
    [Fact]
    public void NotificationScale_DefaultsToSteamSize()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"));

            Assert.Equal(100, service.GetNotificationScalePercent());
            Assert.Equal(100, service.GetSnapshot().NotificationScalePercent);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void NotificationScale_PersistsAcrossServiceReload()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var firstService = CreateService(settingsPath);
            var secondService = CreateService(settingsPath);

            var snapshot = firstService.SetNotificationScalePercent(175);

            Assert.Equal(175, snapshot.NotificationScalePercent);
            Assert.Equal(175, secondService.GetNotificationScalePercent());
            Assert.Equal(175, secondService.GetSnapshot().NotificationScalePercent);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData(0, 100)]
    [InlineData(-40, 100)]
    [InlineData(100, 100)]
    [InlineData(112, 100)]
    [InlineData(113, 125)]
    [InlineData(150, 150)]
    [InlineData(240, 250)]
    [InlineData(400, 250)]
    public void NotificationScale_ClampsAndSnapsToSteps(int? input, int expected)
    {
        Assert.Equal(expected, SteamLoaderSettingsService.NormalizeNotificationScalePercent(input));
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
