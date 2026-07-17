using SteamLoader.App.Infrastructure.AppStart;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class AppStartServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ToolsForSteam-AppStartTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CatalogUsesCachedIndexAndAppliesOnlyLaterChanges()
    {
        Directory.CreateDirectory(_directory);
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var scanCount = 0;
        var discovered = new List<AppStartDiscoveredEntry>
        {
            CreateApp("one", "One"),
            CreateApp("two", "Two")
        };
        var service = new AppStartService(
            Path.Combine(_directory, "app-start.json"),
            () =>
            {
                scanCount++;
                return new AppStartDiscoveryResult(discovered.ToArray());
            },
            () => now);

        Assert.Equal(2, service.GetSnapshot().Shortcuts.Count);
        Assert.Equal(2, service.GetSnapshot().Shortcuts.Count);
        Assert.Equal(1, scanCount);

        discovered = [CreateApp("one", "One"), CreateApp("three", "Three")];
        now = now.AddMinutes(11);

        var refreshed = service.GetSnapshot();

        Assert.Equal(2, scanCount);
        Assert.Equal(["One", "Three"], refreshed.Shortcuts.Select(app => app.Name).Order().ToArray());
    }

    [Fact]
    public void FavoritesAndHiddenAppsPersistAndCanBeRestored()
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "app-start.json");
        var apps = new[] { CreateApp("one", "One"), CreateApp("two", "Two") };
        var service = new AppStartService(
            settingsPath,
            () => new AppStartDiscoveryResult(apps),
            () => new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero));

        var favoriteSnapshot = service.ToggleFavorite("two");

        Assert.True(favoriteSnapshot.Shortcuts[0].Favorite);
        Assert.Equal("two", favoriteSnapshot.Shortcuts[0].Id);

        service.RemoveShortcut("two");
        Assert.DoesNotContain(service.GetSnapshot().Shortcuts, app => app.Id == "two");
        Assert.True(service.GetCatalog().Apps.Single(app => app.Id == "two").Hidden);

        service.AddShortcut("two");
        var restored = service.GetSnapshot().Shortcuts.Single(app => app.Id == "two");
        Assert.False(restored.Favorite);

        var reloaded = new AppStartService(
            settingsPath,
            () => throw new InvalidOperationException("The fresh cache should be used."),
            () => new DateTimeOffset(2026, 7, 17, 12, 1, 0, TimeSpan.Zero));
        Assert.Contains(reloaded.GetSnapshot().Shortcuts, app => app.Id == "two");
    }

    [Fact]
    public void FailedPackagedAppScanKeepsPreviouslyIndexedStoreApps()
    {
        Directory.CreateDirectory(_directory);
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var includePackagedApp = true;
        var service = new AppStartService(
            Path.Combine(_directory, "app-start.json"),
            () => includePackagedApp
                ? new AppStartDiscoveryResult(
                    [CreateApp("store", "Store App", AppStartSourceKinds.Packaged)])
                : new AppStartDiscoveryResult([], PackagedAppsScanSucceeded: false),
            () => now);

        Assert.Single(service.GetSnapshot().Shortcuts);
        includePackagedApp = false;
        now = now.AddMinutes(11);

        Assert.Single(service.GetSnapshot().Shortcuts);
        Assert.Equal("store", service.GetSnapshot().Shortcuts[0].Id);
    }

    private static AppStartDiscoveredEntry CreateApp(
        string id,
        string name,
        string sourceKind = AppStartSourceKinds.Desktop)
    {
        return new AppStartDiscoveredEntry(
            id,
            name,
            sourceKind == AppStartSourceKinds.Packaged ? $"Package.{id}!App" : $"C:\\Apps\\{id}.lnk",
            sourceKind,
            $"{id}|{name}",
            $"C:\\Apps\\{id}.lnk");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
