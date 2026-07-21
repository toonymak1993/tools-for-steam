using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;
using ToolsForSteam.Splash;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsSplashScreenTests
{
    [Fact]
    public void SplashScreen_DefaultsToDynamicArtwork()
    {
        var root = CreateTempRoot();

        try
        {
            var snapshot = CreateService(Path.Combine(root, "settings.json")).GetSplashScreenSettings();

            Assert.Equal(StartupSplashArtworkMode.Dynamic, snapshot.ArtworkMode);
            Assert.Empty(snapshot.CustomImagePath);
            Assert.False(snapshot.CustomImageExists);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void CustomImage_IsSharedAndPersistsAsTheSelectedArtworkMode()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var imagePath = Path.Combine(root, "my-splash.png");
            File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4e, 0x47]);

            var saved = CreateService(settingsPath).SetSplashScreenCustomImagePath(imagePath).SplashScreen;
            var reloaded = CreateService(settingsPath).GetSplashScreenSettings();

            Assert.Equal(StartupSplashArtworkMode.Custom, saved.ArtworkMode);
            Assert.True(saved.CustomImageExists);
            Assert.Equal(saved, reloaded);

            var json = File.ReadAllText(settingsPath);
            Assert.Contains("\"artworkMode\": \"custom\"", json);
            Assert.Contains("\"customImagePath\"", json);
            Assert.DoesNotContain("\"wallpaperPath\"", json);
            Assert.DoesNotContain("\"iconPath\"", json);
            Assert.DoesNotContain("\"showText\"", json);
            Assert.DoesNotContain("\"enabled\"", json);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void LegacyWallpaper_IsMigratedToCustomArtwork()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var imagePath = Path.Combine(root, "legacy.jpg");
            File.WriteAllBytes(imagePath, [0xff, 0xd8, 0xff]);
            File.WriteAllText(
                settingsPath,
                $$"""
                {
                  "splashScreen": {
                    "enabled": false,
                    "showText": false,
                    "wallpaperPath": "{{imagePath.Replace("\\", "\\\\")}}",
                    "iconPath": "C:\\old-icon.png"
                  }
                }
                """);

            var splash = CreateService(settingsPath).GetSplashScreenSettings();

            Assert.Equal(StartupSplashArtworkMode.Custom, splash.ArtworkMode);
            Assert.Equal(imagePath, splash.CustomImagePath);
            Assert.True(splash.CustomImageExists);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ClearingCustomImage_ReturnsAllModesToDynamicArtwork()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var imagePath = Path.Combine(root, "my-splash.webp");
            File.WriteAllBytes(imagePath, [0x52, 0x49, 0x46, 0x46]);
            var service = CreateService(settingsPath);
            service.SetSplashScreenCustomImagePath(imagePath);

            var splash = service.SetSplashScreenCustomImagePath(string.Empty).SplashScreen;

            Assert.Equal(StartupSplashArtworkMode.Dynamic, splash.ArtworkMode);
            Assert.Empty(splash.CustomImagePath);
            Assert.False(splash.CustomImageExists);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void CustomMode_RequiresAnExistingSupportedImage()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"));

            Assert.Throws<InvalidOperationException>(() =>
                service.SetSplashScreenCustomImagePath(Path.Combine(root, "missing.png")));
            Assert.Throws<InvalidOperationException>(() =>
                service.SetSplashScreenArtworkMode(StartupSplashArtworkMode.Custom));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static SteamLoaderSettingsService CreateService(string settingsPath)
    {
        return new SteamLoaderSettingsService(
            new WindowsAutostartService("ToolsForSteamSplashTests"),
            new WindowsShellService(),
            executablePath: @"C:\ToolsForSteam\ToolsForSteam.exe",
            shellLaunchArguments: "--shell",
            settingsPath: settingsPath);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-splash-tests", Guid.NewGuid().ToString("N"));
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
