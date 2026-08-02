using SteamLoader.App.Services;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ExternalGameQuickAccessObservationTests
{
    [Fact]
    public void MissingManagedGame_UsesForegroundWindowMetadata()
    {
        var metadata = ExternalGameQuickAccessService.ResolveSessionMetadata(
            managedGame: null,
            windowTitle: "Hollow Knight Silksong",
            processName: "Hollow Knight Silksong",
            executablePath:
                @"C:\Program Files\WindowsApps\TeamCherry.HollowKnightSilksong_1.0.30000.0_x64__y4jvztpgccj42\Hollow Knight Silksong.exe");

        Assert.Equal("Hollow Knight Silksong", metadata.GameTitle);
        Assert.Equal(string.Empty, metadata.StoreId);
    }

    [Fact]
    public void ManagedGame_MetadataRemainsPreferredWhenAvailable()
    {
        var metadata = ExternalGameQuickAccessService.ResolveSessionMetadata(
            new StoreSyncManagedGameMatch(
                "managed-title",
                "xbox-game-pass",
                "Hollow Knight: Silksong",
                @"C:\XboxGames\Hollow Knight- Silksong\Content\Hollow Knight Silksong.exe"),
            windowTitle: "Hollow Knight Silksong",
            processName: "Hollow Knight Silksong",
            executablePath:
                @"C:\Program Files\WindowsApps\TeamCherry.HollowKnightSilksong_1.0.30000.0_x64__y4jvztpgccj42\Hollow Knight Silksong.exe");

        Assert.Equal("Hollow Knight: Silksong", metadata.GameTitle);
        Assert.Equal("xbox-game-pass", metadata.StoreId);
    }

    [Fact]
    public void MissingManagedGame_FallsBackToExecutableName()
    {
        var metadata = ExternalGameQuickAccessService.ResolveSessionMetadata(
            managedGame: null,
            windowTitle: "",
            processName: "",
            executablePath: @"C:\Games\Example Game.exe");

        Assert.Equal("Example Game", metadata.GameTitle);
        Assert.Equal(string.Empty, metadata.StoreId);
    }

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
