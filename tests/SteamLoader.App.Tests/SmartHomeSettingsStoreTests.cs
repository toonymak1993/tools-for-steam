using SteamLoader.App.Infrastructure.SmartHome;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SmartHomeSettingsStoreTests
{
    [Fact]
    public void Load_MissingFileReturnsDefaultHomeyConfiguration()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var store = new SmartHomeSettingsStore(settingsPath);

            var configuration = store.Load();

            Assert.Equal(SmartHomeProviderIds.Homey, configuration.ProviderId);
            Assert.NotNull(configuration.Homey);
            Assert.Equal(string.Empty, configuration.Homey.BaseUrl);
            Assert.Equal(string.Empty, configuration.Homey.SessionToken);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void SaveAndLoad_NormalizesBaseUrlAndProvider()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            var store = new SmartHomeSettingsStore(settingsPath);
            var configuration = new SmartHomeConfiguration
            {
                ProviderId = "HOME-ASSISTANT",
                Homey = new SmartHomeHomeyConfiguration
                {
                    BaseUrl = "  homey.local/api/manager/devices/device  ",
                    HomeyId = "  abc123  ",
                    SessionToken = "  session-token  "
                }
            };

            store.Save(configuration);
            var reloaded = store.Load();

            Assert.Equal(SmartHomeProviderIds.HomeAssistant, reloaded.ProviderId);
            Assert.Equal("http://homey.local", reloaded.Homey.BaseUrl);
            Assert.Equal("abc123", reloaded.Homey.HomeyId);
            Assert.Equal("session-token", reloaded.Homey.SessionToken);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    [Fact]
    public void Load_InvalidJsonFallsBackToDefaultConfiguration()
    {
        var settingsPath = CreateTempSettingsPath();
        try
        {
            File.WriteAllText(settingsPath, "{ this-is-not-valid-json");
            var store = new SmartHomeSettingsStore(settingsPath);

            var configuration = store.Load();

            Assert.Equal(SmartHomeProviderIds.Homey, configuration.ProviderId);
            Assert.Equal(string.Empty, configuration.Homey.BaseUrl);
            Assert.Equal(string.Empty, configuration.Homey.HomeyId);
            Assert.Equal(string.Empty, configuration.Homey.SessionToken);
        }
        finally
        {
            DeleteTempSettingsPath(settingsPath);
        }
    }

    private static string CreateTempSettingsPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "smart-home.settings.json");
    }

    private static void DeleteTempSettingsPath(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
