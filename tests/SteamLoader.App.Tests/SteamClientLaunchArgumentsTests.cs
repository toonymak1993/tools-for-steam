using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamClientLaunchArgumentsTests
{
    [Fact]
    public void BuildSteamLaunchArguments_BigPicture_MatchesLegacyDefault()
    {
        var arguments = SteamClientLaunchService.BuildSteamLaunchArguments(launchBigPicture: true);

        Assert.Equal("-gamepadui -dev -cef-enable-debugging", arguments);
    }

    [Fact]
    public void BuildSteamLaunchArguments_WithoutBigPicture_MatchesLegacyDefault()
    {
        var arguments = SteamClientLaunchService.BuildSteamLaunchArguments(launchBigPicture: false);

        Assert.Equal("-dev -cef-enable-debugging", arguments);
    }

    [Fact]
    public void BuildSteamLaunchArguments_DisableCefGpu_AppendsFlagLast()
    {
        var arguments = SteamClientLaunchService.BuildSteamLaunchArguments(
            launchBigPicture: true,
            disableCefGpu: true);

        Assert.Equal("-gamepadui -dev -cef-enable-debugging -cef-disable-gpu", arguments);
    }

    [Fact]
    public void BuildSteamLaunchArguments_DisableCefGpuWithoutBigPicture_AppendsFlagLast()
    {
        var arguments = SteamClientLaunchService.BuildSteamLaunchArguments(
            launchBigPicture: false,
            disableCefGpu: true);

        Assert.Equal("-dev -cef-enable-debugging -cef-disable-gpu", arguments);
    }

    [Fact]
    public void BuildSteamLaunchArguments_AlwaysKeepsDevToolsDebuggingEnabled()
    {
        foreach (var bigPicture in new[] { true, false })
        {
            foreach (var disableGpu in new[] { true, false })
            {
                var arguments = SteamClientLaunchService.BuildSteamLaunchArguments(bigPicture, disableGpu);

                Assert.Contains("-dev", arguments);
                Assert.Contains("-cef-enable-debugging", arguments);
            }
        }
    }
}
