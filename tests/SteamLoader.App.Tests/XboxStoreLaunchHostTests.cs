using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class XboxStoreLaunchHostTests
{
    [Fact]
    public void BuildLaunchArguments_RoundTripsPathsWithSpacesAndUnicode()
    {
        var executablePath = @"D:\Xbox Games\Forza Mötorsport\Content\Forza.exe";
        var startDirectory = @"D:\Xbox Games\Forza Mötorsport\Content";

        var arguments = XboxStoreLaunchHost.BuildLaunchArguments(executablePath, startDirectory)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(XboxStoreLaunchHost.TryParseArguments(arguments, out var payload));
        Assert.Equal(executablePath, payload.ExecutablePath);
        Assert.Equal(startDirectory, payload.StartDirectory);
    }

    [Fact]
    public void TryParseArguments_RejectsMalformedPayload()
    {
        Assert.False(XboxStoreLaunchHost.TryParseArguments(
            [XboxStoreLaunchHost.LaunchArgument, "not-base64"],
            out _));
    }

    [Theory]
    [InlineData("GameOverlayRenderer.dll")]
    [InlineData("GameOverlayRenderer64.dll")]
    [InlineData("gameoverlayrenderer64.DLL")]
    public void IsSteamOverlayRendererModule_AcceptsValveRendererNames(string moduleName)
    {
        Assert.True(XboxStoreLaunchHost.IsSteamOverlayRendererModule(moduleName));
    }

    [Theory]
    [InlineData("steamclient64.dll")]
    [InlineData("GameOverlayUI.exe")]
    [InlineData("")]
    public void IsSteamOverlayRendererModule_RejectsOtherModules(string moduleName)
    {
        Assert.False(XboxStoreLaunchHost.IsSteamOverlayRendererModule(moduleName));
    }

    [Theory]
    [InlineData(@"D:\Games\Title.exe", @"D:\Games\Title.exe")]
    [InlineData(@"D:\Xbox Games\Title.exe", "\"D:\\Xbox Games\\Title.exe\"")]
    [InlineData("D:\\Games\\Trailing Slash\\", "\"D:\\Games\\Trailing Slash\\\\\"")]
    public void QuoteCommandLineArgument_UsesWindowsEscapingRules(string value, string expected)
    {
        Assert.Equal(expected, XboxStoreLaunchHost.QuoteCommandLineArgument(value));
    }
}
