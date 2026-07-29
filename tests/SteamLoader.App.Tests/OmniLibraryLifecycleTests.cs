using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryLifecycleTests
{
    [Fact]
    public void Registry_HasStableUniqueStoreAndTabOrder()
    {
        Assert.Equal(
            ["xbox-game-pass", "epic-games", "gog-galaxy"],
            OmniLibraryStoreRegistry.All.Select(store => store.Id));
        Assert.Equal(
            ["tfs-xbox", "tfs-xbox-cloud", "tfs-epic", "tfs-gog"],
            OmniLibraryStoreRegistry.All.SelectMany(store => store.LibraryTabs).Select(tab => tab.Id));
        Assert.Equal(
            OmniLibraryStoreRegistry.All.Count,
            OmniLibraryStoreRegistry.All.Select(store => store.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            OmniLibraryStoreRegistry.All.Sum(store => store.LibraryTabs.Count),
            OmniLibraryStoreRegistry.All.SelectMany(store => store.LibraryTabs)
                .Select(tab => tab.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void Registry_GogUsesTheSameManagedStoreContractsAsEpic()
    {
        var gog = OmniLibraryStoreRegistry.GetRequired("gog-galaxy");

        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.ManagedWebSignIn));
        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.InstallPath));
        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.ManagedInstall));
        Assert.True(gog.Supports(OmniLibraryStoreCapabilities.ManagedUninstall));
        Assert.Equal("tfs-gog", Assert.Single(gog.LibraryTabs).Id);
    }

    [Fact]
    public void Evaluate_ExpiredAuthenticationDoesNotHideEstablishedShortcuts()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: false,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("required", result.Lifecycle.Authentication);
        Assert.Equal("ready", result.Lifecycle.Shortcuts);
    }

    [Fact]
    public void Evaluate_FailedCatalogProbeIsIsolatedFromExistingLibraryTab()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: "Temporary store outage",
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("degraded", result.Lifecycle.Catalog);
        Assert.Equal("catalog", result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_IncompleteArtworkNeverBlocksPreparedLibraryTab()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);
        store.PreparationStatus = "artwork-pending";
        store.PreparationDetail = "One optional image is unavailable.";

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("degraded", result.Lifecycle.Artwork);
        Assert.Equal("artwork", result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_BackgroundArtworkDoesNotReversePreparedLibraryState()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);
        OmniLibraryLifecycle.SetStage(store, "artwork", "updating");
        store.PreparationStatus = "prepared";
        store.PreparationDetail =
            "Library ready. Loading optional artwork 1/10 in the background.";

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("updating", result.Lifecycle.Artwork);
        Assert.Equal("prepared", store.PreparationStatus);
        Assert.Empty(result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_FirstPreparationRequiresNewSteamSession()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var store = CreatePreparedStore(preparedAt);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(-1));

        Assert.False(result.ReadyForLibraryTab);
        Assert.True(result.SteamRestartRequired);
    }

    [Fact]
    public void Evaluate_ShortcutFailureIsNotMisreportedAsCatalogFailure()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var store = CreatePreparedStore(preparedAt);
        store.Cache.LastError = "Steam Library sync failed: Steam was temporarily unavailable.";
        OmniLibraryLifecycle.SetStage(
            store,
            "shortcuts",
            "degraded",
            store.Cache.LastError);

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: true,
            currentCatalogSignature: store.PreparedCatalogSignature,
            cacheError: store.Cache.LastError,
            steamSessionStartedAtUtc: preparedAt.AddMinutes(1));

        Assert.True(result.ReadyForLibraryTab);
        Assert.Equal("ready", result.Lifecycle.Catalog);
        Assert.Equal("degraded", result.Lifecycle.Shortcuts);
        Assert.Equal("shortcuts", result.Lifecycle.FailureScope);
    }

    [Fact]
    public void Evaluate_OneStoreFailureDoesNotChangeAnotherStoreLifecycle()
    {
        var preparedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var xbox = CreatePreparedStore(preparedAt);
        var epic = CreatePreparedStore(preparedAt);
        xbox.Cache.LastError = "Xbox catalog unavailable";
        OmniLibraryLifecycle.SetStage(
            xbox,
            "catalog",
            "degraded",
            xbox.Cache.LastError);

        var xboxResult = OmniLibraryLifecycle.Evaluate(
            xbox,
            true,
            true,
            1,
            true,
            xbox.PreparedCatalogSignature,
            xbox.Cache.LastError,
            preparedAt.AddMinutes(1));
        var epicResult = OmniLibraryLifecycle.Evaluate(
            epic,
            true,
            true,
            1,
            true,
            epic.PreparedCatalogSignature,
            string.Empty,
            preparedAt.AddMinutes(1));

        Assert.Equal("catalog", xboxResult.Lifecycle.FailureScope);
        Assert.Empty(epicResult.Lifecycle.FailureScope);
        Assert.True(xboxResult.ReadyForLibraryTab);
        Assert.True(epicResult.ReadyForLibraryTab);
    }

    [Fact]
    public void Evaluate_UnpreparedCatalogNeverCreatesTab()
    {
        var store = new UnifySteamStoreConfiguration
        {
            Enabled = true,
            Cache = new UnifySteamLibraryCache
            {
                Games =
                [
                    new UnifySteamGameCacheEntry
                    {
                        Id = "new-game",
                        Title = "New Game",
                    },
                ],
            },
        };

        var result = OmniLibraryLifecycle.Evaluate(
            store,
            authConfigured: true,
            authReady: true,
            gameCount: 1,
            allGamesHaveSteamAppIds: false,
            currentCatalogSignature: string.Empty,
            cacheError: string.Empty,
            steamSessionStartedAtUtc: DateTimeOffset.UtcNow);

        Assert.False(result.ReadyForLibraryTab);
        Assert.False(result.SteamRestartRequired);
        Assert.Equal("pending", result.Lifecycle.Shortcuts);
    }

    private static UnifySteamStoreConfiguration CreatePreparedStore(DateTimeOffset preparedAt)
    {
        return new UnifySteamStoreConfiguration
        {
            Enabled = true,
            PreparedAtUtc = preparedAt,
            PreparedCatalogSignature = "CATALOG",
            PreparationStatus = "prepared",
            Cache = new UnifySteamLibraryCache
            {
                AccountName = "Test",
                Games =
                [
                    new UnifySteamGameCacheEntry
                    {
                        Id = "game",
                        Title = "Game",
                        SteamAppId = 123u,
                    },
                ],
            },
        };
    }
}
