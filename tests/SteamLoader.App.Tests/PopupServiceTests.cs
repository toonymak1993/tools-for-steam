using SteamLoader.App.Infrastructure.Popups;
using SteamLoader.App.Infrastructure.Settings;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class PopupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tfs-popups-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void DefaultsCoverEveryCategoryWithSafeValues()
    {
        var settings = new PopupSettingsStore(Path.Combine(_directory, "popups.json")).Load();

        Assert.Equal(PopupThemeIds.Tfs, settings.Theme);
        Assert.Equal(PopupSoundModes.Theme, settings.SoundMode);
        Assert.Equal(100, settings.SoundVolumePercent);
        Assert.True(settings.VolumeOverlay);
        Assert.True(settings.SteamInExclusiveFullscreen);
        Assert.Equal(PopupCategories.All.OrderBy(id => id), settings.Categories.Keys.OrderBy(id => id));
        Assert.All(settings.Categories.Values, category => Assert.True(category.Managed));
        Assert.Equal(PopupPositions.TopCenter, settings.Categories[PopupCategories.Achievements].Position);
    }

    [Fact]
    public void UpdatesAreNormalizedAndPersisted()
    {
        var path = Path.Combine(_directory, "popups.json");
        var store = new PopupSettingsStore(path);

        store.Update(new PopupSettingsUpdate("GLOW", "loud", 900, false, "nowhere", false, null, null, null, null));
        store.Update(new PopupSettingsUpdate(null, null, null, null, null, null, PopupCategories.Friends, false, "BOTTOM-LEFT", 99));
        store.Update(new PopupSettingsUpdate(null, null, null, null, null, null, "unknown", false, null, null));

        var reloaded = new PopupSettingsStore(path).Load();
        Assert.Equal(PopupThemeIds.Glow, reloaded.Theme);
        Assert.Equal(PopupSoundModes.Theme, reloaded.SoundMode);
        Assert.Equal(200, reloaded.SoundVolumePercent);
        Assert.False(reloaded.VolumeOverlay);
        Assert.Equal(PopupPositions.TopCenter, reloaded.VolumePosition);
        Assert.False(reloaded.SteamInExclusiveFullscreen);
        Assert.False(reloaded.Categories[PopupCategories.Friends].Managed);
        Assert.Equal(PopupPositions.BottomLeft, reloaded.Categories[PopupCategories.Friends].Position);
        Assert.Equal(PopupConfiguration.MaximumDurationSeconds, reloaded.Categories[PopupCategories.Friends].DurationSeconds);
        Assert.Equal(6, reloaded.Categories.Count);
    }

    [Fact]
    public void BrokenSettingsFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "popups.json");
        File.WriteAllText(path, "{ not json");

        var settings = new PopupSettingsStore(path).Load();

        Assert.Equal(PopupThemeIds.Tfs, settings.Theme);
        Assert.Equal(6, settings.Categories.Count);
    }

    [Theory]
    [InlineData("https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/1/a.jpg", true)]
    [InlineData("https://avatars.steamstatic.com/abc_medium.jpg", true)]
    [InlineData("https://steamcdn-a.akamaihd.net/a.png", true)]
    [InlineData("http://avatars.steamstatic.com/abc.jpg", false)]
    [InlineData("https://evil.example/steamstatic.com.jpg", false)]
    [InlineData("https://steamstatic.com.evil.example/a.jpg", false)]
    [InlineData("https://avatars.steamstatic.com:8443/a.jpg", false)]
    [InlineData("https://127.0.0.1/a.jpg", false)]
    public void OnlySteamCdnImagesAreFetched(string url, bool allowed)
    {
        Assert.Equal(allowed, PopupImageCache.IsAllowedUrl(url, out _));
    }

    [Fact]
    public void DataUrlsAreDecodedOnlyForImages()
    {
        Assert.Equal([1, 2, 3], PopupImageCache.TryDecodeDataUrl("data:image/png;base64,AQID"));
        Assert.Null(PopupImageCache.TryDecodeDataUrl("data:text/html;base64,AQID"));
        Assert.Null(PopupImageCache.TryDecodeDataUrl("data:image/png,raw"));
        Assert.Null(PopupImageCache.TryDecodeDataUrl("data:image/png;base64,%%%"));
    }

    [Fact]
    public void PopupTextIsCleanedAndShortened()
    {
        Assert.Equal("Hello world", PopupService.Clean("  Hello\u0007 world \n", 50));
        Assert.Equal("abcd…", PopupService.Clean("abcdefghij", 5));
        Assert.Equal(string.Empty, PopupService.Clean(null, 5));
    }

    [Fact]
    public void PopupsIsAToggleableBuiltInFeature()
    {
        var popups = Assert.Single(SteamLoaderPluginCatalog.BuildDefinitions(), definition => definition.Id == "popups");

        Assert.Equal("TFS Popups", popups.Title);
        Assert.True(popups.CanDisable);
        Assert.True(popups.DefaultEnabled);
    }
}
