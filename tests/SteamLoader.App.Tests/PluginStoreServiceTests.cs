using SteamLoader.App.Infrastructure.PluginStore;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class PluginStoreServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ListsOnlyIncludedPlugins()
    {
        var root = CreateTempRoot();

        try
        {
            var storeRoot = Path.Combine(root, "plugin-store");
            Directory.CreateDirectory(storeRoot);
            File.WriteAllText(
                Path.Combine(storeRoot, "catalog.json"),
                "{\"plugins\":[{\"id\":\"third-party\",\"title\":\"Third Party\"}]}");
            var service = CreatePluginStoreService(root, out _);

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal("Plugin Activator", snapshot.CatalogTitle);
            Assert.NotEmpty(snapshot.BuiltInPlugins);
            Assert.Equal(snapshot.BuiltInPlugins.Count, snapshot.BuiltInCount);
            Assert.All(snapshot.BuiltInPlugins, plugin => Assert.True(plugin.IsBuiltIn));
            Assert.DoesNotContain(snapshot.BuiltInPlugins, plugin => plugin.Id == "third-party");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_ReflectsPluginActivationState()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreatePluginStoreService(root, out var settings);
            settings.SetPluginEnabled("audio", false);

            var disabled = await service.GetSnapshotAsync(CancellationToken.None);
            var audio = Assert.Single(disabled.BuiltInPlugins, plugin => plugin.Id == "audio");
            Assert.False(audio.IsEnabled);
            Assert.Equal("Inactive", audio.StatusText);

            settings.SetPluginEnabled("audio", true);
            var enabled = await service.GetSnapshotAsync(CancellationToken.None);
            audio = Assert.Single(enabled.BuiltInPlugins, plugin => plugin.Id == "audio");
            Assert.True(audio.IsEnabled);
            Assert.Equal("Active", audio.StatusText);
            Assert.NotEmpty(audio.Images);
            Assert.True(service.TryGetBuiltInImage("audio", out var imagePath, out var contentType));
            Assert.True(File.Exists(imagePath));
            Assert.Equal("image/svg+xml", contentType);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void OverlayStateAndControllerInput_RemainAvailable()
    {
        var root = CreateTempRoot();

        try
        {
            var service = CreatePluginStoreService(root, out _);

            Assert.False(service.GetOverlayState().IsOpen);
            Assert.True(service.SetOverlayOpen(true).IsOpen);
            var input = service.AddOverlayInput("down", "test");
            var batch = service.GetOverlayInputs(0);
            Assert.Equal(input.Nonce, batch.LatestNonce);
            Assert.Single(batch.Inputs);
            Assert.False(service.SetOverlayOpen(false).IsOpen);
            Assert.Empty(service.GetOverlayInputs(0).Inputs);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static PluginStoreService CreatePluginStoreService(
        string root,
        out SteamLoaderSettingsService settings)
    {
        settings = new SteamLoaderSettingsService(
            new WindowsAutostartService("ToolsForSteamPluginActivatorTests"),
            new WindowsShellService(),
            executablePath: @"C:\ToolsForSteam\ToolsForSteam.exe",
            shellLaunchArguments: "--shell",
            settingsPath: Path.Combine(root, "settings.json"));
        return new PluginStoreService(
            new HttpClient(),
            settings,
            Path.Combine(root, "plugin-store"));
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
