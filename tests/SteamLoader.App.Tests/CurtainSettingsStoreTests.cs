using SteamLoader.App.Infrastructure.Curtain;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class CurtainSettingsStoreTests
{
    [Fact]
    public void DisabledGamesArePersistedNormalizedAndReversible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tfs-curtain-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "curtain.json");
        try
        {
            var store = new CurtainSettingsStore(path);
            Assert.Empty(store.Load().DisabledAppIds);

            store.Save(new CurtainConfiguration
            {
                DisabledAppIds = [20920, 0, 1091500, 20920]
            });

            Assert.Equal([20920u, 1091500u], store.Load().DisabledAppIds);

            store.Save(new CurtainConfiguration());
            Assert.Empty(store.Load().DisabledAppIds);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FirstCurtainFilesLoadWithModernDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tfs-curtain-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "curtain.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """{ "disabledAppIds": [ 20920 ] }""");

            var configuration = new CurtainSettingsStore(path).Load();

            Assert.Equal([20920u], configuration.DisabledAppIds);
            Assert.Equal(CurtainModes.Modern, configuration.Mode);
            Assert.Equal(CurtainConfiguration.DefaultTimeoutSeconds, configuration.TimeoutSeconds);
            Assert.True(configuration.HideLaunchers);
            Assert.True(configuration.ReturnToSteam);
            Assert.Empty(configuration.Games);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void PerGameOverridesAreClampedAndEmptyOnesDropped()
    {
        var configuration = new CurtainConfiguration
        {
            Mode = "SOMETHING",
            TimeoutSeconds = 3,
            ExitDelaySeconds = 42,
            Games =
            {
                ["1091500"] = new CurtainGameSettings
                {
                    Mode = "Classic",
                    BackgroundOpacity = 140,
                    BackgroundScale = 20,
                    LogoWidth = 2,
                    LogoSource = "grid-logo",
                    BackgroundSource = "made-up",
                    TimeoutSeconds = -5
                },
                ["20920"] = new CurtainGameSettings(),
                ["not-an-id"] = new CurtainGameSettings { LogoX = 10 }
            }
        };

        CurtainSettingsStore.Normalize(configuration);

        Assert.Equal(CurtainModes.Modern, configuration.Mode);
        Assert.Equal(10, configuration.TimeoutSeconds);
        Assert.Equal(10, configuration.ExitDelaySeconds);
        var game = Assert.Single(configuration.Games).Value;
        Assert.Equal(CurtainModes.Classic, game.Mode);
        Assert.Equal(100, game.BackgroundOpacity);
        Assert.Equal(100, game.BackgroundScale);
        Assert.Equal(10, game.LogoWidth);
        Assert.Equal("grid-logo", game.LogoSource);
        Assert.Null(game.BackgroundSource);
        Assert.Equal(0, game.TimeoutSeconds);
    }

    [Fact]
    public void ResolvedGameUsesOverridesThenGlobalValues()
    {
        var configuration = new CurtainConfiguration
        {
            TimeoutSeconds = 75,
            ExitDelaySeconds = 2,
            StatusText = "Loading",
            DisabledAppIds = [20920],
            Games = { ["1091500"] = new CurtainGameSettings { TimeoutSeconds = 0, LogoX = 30, BackgroundSource = "none" } }
        };

        var customized = CurtainService.Resolve(configuration, 1091500, steamRoot: null);
        var plain = CurtainService.Resolve(configuration, 20920, steamRoot: null);

        Assert.True(customized.Enabled);
        Assert.Equal(0, customized.TimeoutSeconds);
        Assert.Equal(2, customized.ExitDelaySeconds);
        Assert.Equal(30, customized.LogoX);
        Assert.Equal(string.Empty, customized.BackgroundUrl);
        Assert.Equal("Loading", customized.StatusText);
        Assert.True(customized.HasOverrides);
        Assert.False(plain.Enabled);
        Assert.Equal(75, plain.TimeoutSeconds);
        Assert.Equal(50, plain.LogoX);
        Assert.False(plain.HasOverrides);
    }

    [Theory]
    [InlineData("Cyberpunk 2077", "Cyberpunk 2077", true)]
    [InlineData("The Witcher: Enhanced Edition", "The Witcher", true)]
    [InlineData("The Witcher: Enhanced Edition", "Codex", false)]
    [InlineData("Game Windows Edition", "Unrelated launcher", false)]
    public void WindowedFallbackRequiresAGameTitleMatch(
        string expectedTitle,
        string windowTitle,
        bool expected)
    {
        Assert.Equal(expected, CurtainLaunchTracker.TitlesLikelyMatch(expectedTitle, windowTitle));
    }
}
