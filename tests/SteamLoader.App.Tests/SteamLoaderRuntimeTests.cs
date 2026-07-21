using SteamLoader.App;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderRuntimeTests
{
    [Fact]
    public void ShouldUseShellBootstrap_ReturnsTrue_WhenRequestedInShellMode()
    {
        Assert.True(SteamLoaderRuntime.ShouldUseShellBootstrap(
            shellBootstrapRequested: true,
            startupMode: SteamLoaderRuntime.StartupModeShell));
    }

    [Theory]
    [InlineData(SteamLoaderRuntime.StartupModeTray)]
    [InlineData(SteamLoaderRuntime.StartupModeExternal)]
    public void ShouldUseShellBootstrap_ReturnsFalse_ForNonShellModes(string startupMode)
    {
        Assert.False(SteamLoaderRuntime.ShouldUseShellBootstrap(
            shellBootstrapRequested: true,
            startupMode: startupMode));
    }

    [Fact]
    public void ShouldUseShellBootstrap_ReturnsFalse_WhenBootstrapWasNotRequested()
    {
        Assert.False(SteamLoaderRuntime.ShouldUseShellBootstrap(
            shellBootstrapRequested: false,
            startupMode: SteamLoaderRuntime.StartupModeShell));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldStartShellHandOffMonitor_ReturnsTrue_InShellMode_RegardlessOfSplash(
        bool startupSplashVisible)
    {
        Assert.True(SteamLoaderRuntime.ShouldStartShellHandOffMonitor(
            shellBootstrapMode: true,
            startupSplashVisible: startupSplashVisible));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldStartShellHandOffMonitor_ReturnsFalse_OutsideShellMode(
        bool startupSplashVisible)
    {
        Assert.False(SteamLoaderRuntime.ShouldStartShellHandOffMonitor(
            shellBootstrapMode: false,
            startupSplashVisible: startupSplashVisible));
    }
}
