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
        Assert.Equal(expected, CurtainService.TitlesLikelyMatch(expectedTitle, windowTitle));
    }
}
