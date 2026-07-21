using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ExternalGameQuickAccessObservationTests
{
    [Fact]
    public void FirstOpen_DoesNotReturnWhileGameStillOwnsForeground()
    {
        var observation = new ExternalGameQuickAccessObservation();

        var returned = observation.Observe(
            quickAccessVisible: true,
            gameIsForeground: true);

        Assert.False(returned);
        Assert.False(observation.GameLostForeground);
        Assert.False(observation.QuickAccessReady);
    }

    [Fact]
    public void ReturnIsArmedOnlyAfterSteamHandOffAndVisibleQuickAccess()
    {
        var observation = new ExternalGameQuickAccessObservation();

        Assert.False(observation.Observe(false, gameIsForeground: true));
        Assert.False(observation.Observe(false, gameIsForeground: false));
        Assert.False(observation.QuickAccessReady);
        Assert.False(observation.Observe(true, gameIsForeground: false));
        Assert.True(observation.QuickAccessReady);
        Assert.True(observation.Observe(true, gameIsForeground: true));
    }
}
