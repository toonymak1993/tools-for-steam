using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamStartupHistoryStoreTests
{
    [Fact]
    public void TimingPolicy_UsesDefaultUntilEnoughSuccessfulSamplesExist()
    {
        var path = CreateHistoryPath();
        try
        {
            var store = new SteamStartupHistoryStore(path);

            var policy = store.GetTimingPolicy(isHandheld: false);

            Assert.False(policy.IsAdaptive);
            Assert.Equal(TimeSpan.FromSeconds(30), policy.StartupGracePeriod);
        }
        finally
        {
            DeleteHistory(path);
        }
    }

    [Fact]
    public void TimingPolicy_AdaptsToSuccessfulStartupHistoryWithinSafeBounds()
    {
        var path = CreateHistoryPath();
        try
        {
            var store = new SteamStartupHistoryStore(path);
            var now = DateTimeOffset.UtcNow;
            foreach (var seconds in new[] { 10, 12, 14, 16, 18 })
            {
                store.Record(
                    now - TimeSpan.FromSeconds(seconds),
                    SteamStartupOutcome.Ready,
                    recoveryUsed: false,
                    "test");
            }

            var policy = store.GetTimingPolicy(isHandheld: false);

            Assert.True(policy.IsAdaptive);
            Assert.InRange(policy.StartupGracePeriod, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(90));
            Assert.InRange(policy.SplashSafetyTimeout, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));
        }
        finally
        {
            DeleteHistory(path);
        }
    }

    private static string CreateHistoryPath() => Path.Combine(
        Path.GetTempPath(),
        "tfs-steam-history-tests",
        Guid.NewGuid().ToString("N"),
        "history.json");

    private static void DeleteHistory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
