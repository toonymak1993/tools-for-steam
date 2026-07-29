using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class UnifySteamDownloadStatusStoreTests
{
    [Theory]
    [InlineData("preparing")]
    [InlineData("downloading")]
    [InlineData("paused")]
    [InlineData("failed")]
    [InlineData("action-required")]
    [InlineData("uninstalling")]
    [InlineData("uninstall-action-required")]
    [InlineData("uninstall-failed")]
    [InlineData("completed")]
    public void IsDownloadCenterStatus_KeepsActionableAndRecentLifecycleStatesVisible(
        string status)
    {
        Assert.True(UnifySteamDownloadStatusStore.IsDownloadCenterStatus(status));
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("downloading")]
    [InlineData("paused")]
    [InlineData("action-required")]
    [InlineData("uninstalling")]
    [InlineData("uninstall-action-required")]
    public void BlocksStoreDisconnect_ProtectsActiveOrResumableOperations(string status)
    {
        Assert.True(UnifySteamDownloadStatusStore.BlocksStoreDisconnect(status));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("canceled")]
    [InlineData("failed")]
    [InlineData("cancel-failed")]
    [InlineData("uninstall-failed")]
    [InlineData("")]
    public void BlocksStoreDisconnect_AllowsTerminalOperations(string status)
    {
        Assert.False(UnifySteamDownloadStatusStore.BlocksStoreDisconnect(status));
    }

    [Theory]
    [InlineData("epic-games:Fortnite", "epic-games", "Fortnite")]
    [InlineData("GOG-GALAXY:1207658924:setup", "gog-galaxy", "1207658924:setup")]
    public void TryParseKey_PreservesGameIdsContainingSeparators(
        string key,
        string expectedStoreId,
        string expectedGameId)
    {
        Assert.True(
            UnifySteamDownloadStatusStore.TryParseKey(
                key,
                out var storeId,
                out var gameId));
        Assert.Equal(expectedStoreId, storeId);
        Assert.Equal(expectedGameId, gameId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-separator")]
    [InlineData(":missing-store")]
    [InlineData("missing-game:")]
    public void TryParseKey_RejectsIncompleteKeys(string key)
    {
        Assert.False(
            UnifySteamDownloadStatusStore.TryParseKey(
                key,
                out _,
                out _));
    }
}
