using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ProviderInstallStateTrackerTests
{
    [Fact]
    public void IdleReconciliation_ProbesGamesNotPreviouslyMarkedInstalled()
    {
        Assert.True(ProviderInstallStateTracker.ShouldScheduleGameProbe(
            "idle",
            includeIdle: true));
    }

    [Theory]
    [InlineData("action-required")]
    [InlineData("uninstall-action-required")]
    [InlineData("uninstall-failed")]
    [InlineData("failed")]
    public void ActiveNativeHandoff_IsProbedBetweenIdlePasses(string status)
    {
        Assert.True(ProviderInstallStateTracker.ShouldScheduleGameProbe(
            status,
            includeIdle: false));
    }

    [Fact]
    public void UnrelatedIdleGame_WaitsForTheNextProviderWidePass()
    {
        Assert.False(ProviderInstallStateTracker.ShouldScheduleGameProbe(
            "idle",
            includeIdle: false));
    }
}
