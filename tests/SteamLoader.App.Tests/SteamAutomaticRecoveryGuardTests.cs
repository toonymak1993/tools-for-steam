using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamAutomaticRecoveryGuardTests
{
    [Fact]
    public void TryBegin_AllowsOneAttemptUntilCooldownExpires()
    {
        var root = Path.Combine(Path.GetTempPath(), "tfs-steam-recovery-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "recovery.state");
        var cooldown = TimeSpan.FromMinutes(10);
        var guard = new SteamAutomaticRecoveryGuard(path, cooldown);
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        try
        {
            Assert.True(guard.TryBegin(now, out var firstRetryAfter));
            Assert.Equal(TimeSpan.Zero, firstRetryAfter);

            Assert.False(guard.TryBegin(now.AddMinutes(1), out var blockedRetryAfter));
            Assert.Equal(TimeSpan.FromMinutes(9), blockedRetryAfter);

            Assert.True(guard.TryBegin(now.Add(cooldown), out var laterRetryAfter));
            Assert.Equal(TimeSpan.Zero, laterRetryAfter);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MarkHealthy_ReleasesTheRecoveryBudgetImmediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "tfs-steam-recovery-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "recovery.state");
        var guard = new SteamAutomaticRecoveryGuard(path, TimeSpan.FromMinutes(10));
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        try
        {
            Assert.True(guard.TryBegin(now, out _));

            guard.MarkHealthy();

            Assert.True(guard.TryBegin(now.AddSeconds(1), out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TryBegin_ClockRollbackPreservesThenNormalizesTheCooldown()
    {
        var root = Path.Combine(Path.GetTempPath(), "tfs-steam-recovery-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "recovery.state");
        var cooldown = TimeSpan.FromMinutes(10);
        var guard = new SteamAutomaticRecoveryGuard(path, cooldown);
        var originalNow = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var correctedNow = originalNow.AddMinutes(-30);

        try
        {
            Assert.True(guard.TryBegin(originalNow, out _));

            Assert.False(guard.TryBegin(correctedNow, out var retryAfter));
            Assert.Equal(cooldown, retryAfter);
            Assert.True(guard.TryBegin(correctedNow.Add(cooldown), out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
