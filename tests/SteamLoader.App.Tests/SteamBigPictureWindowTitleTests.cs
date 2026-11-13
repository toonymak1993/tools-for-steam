using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamBigPictureWindowTitleTests
{
    [Theory]
    [InlineData("Steam Big Picture Mode")]
    [InlineData("Big-Picture-Modus")]
    [InlineData("Mode Big Picture")]
    [InlineData("Modo Big Picture")]
    [InlineData("Режим Big Picture")]
    [InlineData("Big Picture 模式")]
    [InlineData("Valve Steam Gamepad")]
    public void LocalizedBigPictureTitlesAreRecognized(string title)
    {
        Assert.True(SteamBigPictureForegroundDetector.IsBigPictureWindowTitle(title));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Steam")]
    [InlineData("Freundesliste")]
    [InlineData("Tools for Steam")]
    public void OtherWindowsAreNotBigPicture(string? title)
    {
        Assert.False(SteamBigPictureForegroundDetector.IsBigPictureWindowTitle(title));
    }
}
