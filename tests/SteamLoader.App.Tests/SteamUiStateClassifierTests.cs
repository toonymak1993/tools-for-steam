using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamUiStateClassifierTests
{
    [Theory]
    [InlineData("Steam Login", "https://steamloopback.host/login", SteamUiState.Login)]
    [InlineData("Offline", "https://steamloopback.host/offline", SteamUiState.Offline)]
    [InlineData("Fatal error", "https://steamloopback.host/error", SteamUiState.Error)]
    [InlineData("Big-Picture", "https://steamloopback.host/index.html?browserType=3", SteamUiState.Gamepad)]
    public void Classify_RecognizesImportantSteamSurfaces(
        string title,
        string url,
        SteamUiState expected)
    {
        var targets = new[]
        {
            new SteamDevToolsTarget("1", title, "page", url, "ws://localhost/1")
        };

        Assert.Equal(expected, SteamUiStateClassifier.Classify(targets));
    }
}
