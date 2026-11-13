using SteamLoader.App;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderRuntimeTests
{
    [Fact]
    public void ShouldUseConsoleStart_ReturnsTrue_WhenRequestedInConsoleMode()
    {
        Assert.True(SteamLoaderRuntime.ShouldUseConsoleStart(
            consoleStartRequested: true,
            startupMode: SteamLoaderRuntime.StartupModeConsole));
    }

    [Theory]
    [InlineData(SteamLoaderRuntime.LegacyStartupModeShell)]
    [InlineData(SteamLoaderRuntime.StartupModeTray)]
    [InlineData(null)]
    public void ShouldUseConsoleStart_IgnoresALeftoverSignInTaskOutsideConsoleMode(string? startupMode)
    {
        Assert.False(SteamLoaderRuntime.ShouldUseConsoleStart(
            consoleStartRequested: true,
            startupMode: startupMode));
    }

    [Fact]
    public void ShouldUseConsoleStart_ReturnsFalse_ForANormalLaunchInConsoleMode()
    {
        Assert.False(SteamLoaderRuntime.ShouldUseConsoleStart(
            consoleStartRequested: false,
            startupMode: SteamLoaderRuntime.StartupModeConsole));
    }

    [Theory]
    [InlineData(SteamLoaderRuntime.StartupModeConsole, true)]
    [InlineData("CONSOLE", true)]
    [InlineData(SteamLoaderRuntime.LegacyStartupModeShell, false)]
    [InlineData(SteamLoaderRuntime.StartupModeTray, false)]
    [InlineData(null, false)]
    public void IsConsoleStartupMode_OnlyCoversConsoleMode(string? startupMode, bool expected)
    {
        Assert.Equal(expected, SteamLoaderRuntime.IsConsoleStartupMode(startupMode));
    }

    [Fact]
    public void ConsoleLaunchArguments_MarkTheConsoleStartAndNeverTheLegacyShellBootstrap()
    {
        Assert.Contains(SteamLoaderRuntime.ConsoleStartArgument, SteamLoaderRuntime.ConsoleLaunchArguments);
        Assert.DoesNotContain(SteamLoaderRuntime.LegacyShellBootstrapArgument, SteamLoaderRuntime.ConsoleLaunchArguments);
    }
}
