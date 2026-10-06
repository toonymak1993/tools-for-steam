using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryCoordinationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void AutomationWatchers_RunForEitherOwningPlugin(
        bool storeSyncEnabled,
        bool omniLibraryEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            StoreSyncAutomationService.ShouldRunWatchers(
                storeSyncEnabled,
                omniLibraryEnabled));
    }

    [Fact]
    public void StoreGates_NormalizeInRegistryOrderAndRemoveDuplicates()
    {
        var normalized = OmniLibraryStoreGateSet.NormalizeStoreIds(
            ["gog-galaxy", "xbox-game-pass", "GOG-GALAXY", "unknown"]);

        Assert.Equal(
            ["xbox-game-pass", "gog-galaxy", "unknown"],
            normalized);
    }

    [Fact]
    public async Task StoreGates_DoNotBlockAnUnrelatedProvider()
    {
        var gates = new OmniLibraryStoreGateSet();
        using var xboxLease = gates.Enter("xbox-game-pass");

        var gogEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gogTask = Task.Run(() =>
        {
            using var gogLease = gates.Enter("gog-galaxy");
            gogEntered.SetResult();
        });

        await gogEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await gogTask;
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(8, 60)]
    public void RefreshBackoff_IsExponentialAndCapped(
        int failureCount,
        double expectedMinutes)
    {
        var delay = OmniLibraryRefreshCoordinator.ComputeFailureBackoff(
            failureCount,
            jitterMultiplier: 1);

        Assert.Equal(expectedMinutes, delay.TotalMinutes);
    }

    [Fact]
    public void SuccessfulRefreshes_AreStaggeredWithinThirtySeconds()
    {
        var minimum = OmniLibraryRefreshCoordinator.AddSuccessJitter(
            OmniLibraryRefreshCoordinator.CatalogCheckInterval,
            0);
        var maximum = OmniLibraryRefreshCoordinator.AddSuccessJitter(
            OmniLibraryRefreshCoordinator.CatalogCheckInterval,
            1);

        Assert.Equal(TimeSpan.FromMinutes(5), minimum);
        Assert.Equal(TimeSpan.FromMinutes(5.5), maximum);
    }

    [Fact]
    public void InstallStateCoordinator_UsesFastChecksForRecentNativeHandoffs()
    {
        var now = DateTimeOffset.UtcNow;
        var statuses = new Dictionary<string, UnifySteamDownloadStatus>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["xbox-game-pass:game-1"] = new(
                "action-required",
                100,
                "Waiting for the native provider.",
                now),
        };

        Assert.True(OmniLibraryInstallStateCoordinator.HasRelevantStatus(
            statuses,
            "xbox-game-pass",
            now));
        Assert.False(OmniLibraryInstallStateCoordinator.HasRelevantStatus(
            statuses,
            "epic-games",
            now));
    }

    [Fact]
    public void InstallStateCoordinator_DoesNotFastPollTransfersOrStaleFailures()
    {
        var now = DateTimeOffset.UtcNow;
        var statuses = new Dictionary<string, UnifySteamDownloadStatus>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["xbox-game-pass:transfer"] = new(
                "downloading",
                50,
                "Downloading.",
                now),
            ["xbox-game-pass:old-failure"] = new(
                "failed",
                0,
                "Old failure.",
                now - OmniLibraryInstallStateCoordinator.ActiveStatusLifetime -
                TimeSpan.FromSeconds(1)),
        };

        Assert.False(OmniLibraryInstallStateCoordinator.HasRelevantStatus(
            statuses,
            "xbox-game-pass",
            now));
    }
}
