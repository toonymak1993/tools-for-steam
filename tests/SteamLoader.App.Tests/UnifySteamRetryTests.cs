using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class UnifySteamRetryTests
{
    // Locks in the fast path that lets a restarted TFS worker recover from a
    // crash between Legendary finishing the download and TFS finishing the
    // Windows setup step, without re-downloading: InstallEpicCore only takes
    // that fast path when this returns false.
    [Fact]
    public void IsEpicUpdateRequired_ReturnsFalse_WhenNotInstalled()
    {
        var cachedGame = new UnifySteamGameCacheEntry
        {
            Id = "game",
            Title = "Game",
            Version = "1.0",
            LatestVersion = "2.0",
        };

        Assert.False(UnifySteamLauncher.IsEpicUpdateRequired(alreadyInstalled: false, cachedGame));
    }

    [Fact]
    public void IsEpicUpdateRequired_ReturnsFalse_WhenNoCatalogEntryIsCached()
    {
        Assert.False(UnifySteamLauncher.IsEpicUpdateRequired(alreadyInstalled: true, cachedGame: null));
    }

    [Fact]
    public void IsEpicUpdateRequired_ReturnsFalse_WhenInstalledVersionMatchesLatest()
    {
        var cachedGame = new UnifySteamGameCacheEntry
        {
            Id = "game",
            Title = "Game",
            Version = "1.0",
            LatestVersion = "1.0",
        };

        Assert.False(UnifySteamLauncher.IsEpicUpdateRequired(alreadyInstalled: true, cachedGame));
    }

    [Fact]
    public void IsEpicUpdateRequired_ReturnsTrue_WhenInstalledVersionIsStale()
    {
        var cachedGame = new UnifySteamGameCacheEntry
        {
            Id = "game",
            Title = "Game",
            Version = "1.0",
            LatestVersion = "2.0",
        };

        Assert.True(UnifySteamLauncher.IsEpicUpdateRequired(alreadyInstalled: true, cachedGame));
    }

    [Theory]
    [InlineData("", "2.0")]
    [InlineData("1.0", "")]
    public void IsEpicUpdateRequired_ReturnsFalse_WhenEitherVersionIsUnknown(
        string installedVersion,
        string latestVersion)
    {
        var cachedGame = new UnifySteamGameCacheEntry
        {
            Id = "game",
            Title = "Game",
            Version = installedVersion,
            LatestVersion = latestVersion,
        };

        Assert.False(UnifySteamLauncher.IsEpicUpdateRequired(alreadyInstalled: true, cachedGame));
    }

    [Theory]
    [InlineData("Connection reset by peer")]
    [InlineData("The download timed out")]
    [InlineData("Temporary DNS resolution failure")]
    [InlineData("")]
    public void ShouldRetryManagedDownload_RetriesTransientDiagnostics(string diagnostic)
    {
        Assert.True(UnifySteamLauncher.ShouldRetryManagedDownload(diagnostic));
    }

    [Theory]
    [InlineData("Not enough free space on the destination drive")]
    [InlineData("no space left on device")]
    [InlineData("User is not logged in")]
    [InlineData("Authentication failed")]
    [InlineData("Invalid credential supplied")]
    [InlineData("Account does not own this title")]
    [InlineData("Item is not owned")]
    [InlineData("Invalid app requested")]
    [InlineData("Manifest not found for this build")]
    public void ShouldRetryManagedDownload_DoesNotRetryPermanentDiagnostics(string diagnostic)
    {
        Assert.False(UnifySteamLauncher.ShouldRetryManagedDownload(diagnostic));
    }

    [Theory]
    [InlineData("NOT ENOUGH FREE SPACE")]
    [InlineData("Not Logged In")]
    public void ShouldRetryManagedDownload_IsCaseInsensitive(string diagnostic)
    {
        Assert.False(UnifySteamLauncher.ShouldRetryManagedDownload(diagnostic));
    }
}
