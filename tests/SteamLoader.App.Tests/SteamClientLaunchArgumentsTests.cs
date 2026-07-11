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

    [Theory]
    [InlineData("C:\\Steam\\steam.exe -gamepadui -dev -cef-enable-debugging")]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -CEF-ENABLE-DEBUGGING -DEV -GAMEPADUI")]
    public void HasRequiredConsoleLaunchArguments_AllRequiredFlags_ReturnsTrue(string commandLine)
    {
        Assert.True(SteamClientLaunchService.HasRequiredConsoleLaunchArguments(commandLine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("steam.exe")]
    [InlineData("steam.exe -dev -cef-enable-debugging")]
    [InlineData("steam.exe -gamepadui -cef-enable-debugging")]
    [InlineData("steam.exe -gamepadui -dev")]
    public void HasRequiredConsoleLaunchArguments_MissingFlag_ReturnsFalse(string? commandLine)
    {
        Assert.False(SteamClientLaunchService.HasRequiredConsoleLaunchArguments(commandLine));
    }
}
