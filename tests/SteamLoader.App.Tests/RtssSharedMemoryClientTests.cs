using SteamLoader.App.Infrastructure.Performance;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class RtssSharedMemoryClientTests
{
    [Fact]
    public void FrameRate_PrefersRtssRollingAverage()
    {
        var fps = RtssSharedMemoryClient.ComputeFramesPerSecond(
            bufferedFramerateTenths: 599,
            periodStartMilliseconds: 1_000,
            periodEndMilliseconds: 2_000,
            periodFrames: 2_604);

        Assert.Equal(59.9, fps, precision: 3);
        Assert.Equal(16.694, 1000d / fps, precision: 3);
    }

    [Fact]
    public void FrameRate_RejectsImplausibleRollingAndPeriodValues()
    {
        var fps = RtssSharedMemoryClient.ComputeFramesPerSecond(
            bufferedFramerateTenths: 26_042,
            periodStartMilliseconds: 1_000,
            periodEndMilliseconds: 2_000,
            periodFrames: 2_604);

        Assert.Equal(0, fps);
    }

    [Fact]
    public void FrameRate_FallsBackToStableMeasurementPeriod()
    {
        var fps = RtssSharedMemoryClient.ComputeFramesPerSecond(
            bufferedFramerateTenths: 0,
            periodStartMilliseconds: 10_000,
            periodEndMilliseconds: 12_000,
            periodFrames: 120);

        Assert.Equal(60, fps);
    }

    [Fact]
    public void TelemetryFreshness_HandlesNormalAndWrappedTickCounts()
    {
        Assert.True(RtssSharedMemoryClient.IsTelemetryFresh(9_000, 10_000));
        Assert.False(RtssSharedMemoryClient.IsTelemetryFresh(7_000, 10_000));
        Assert.True(RtssSharedMemoryClient.IsTelemetryFresh(uint.MaxValue - 50, 100));
        Assert.False(RtssSharedMemoryClient.IsTelemetryFresh(0, 100));
    }

    [Fact]
    public void OnePercentLow_UsesPercentileBoundaryInsteadOfAveragingSpikes()
    {
        var samples = Enumerable.Repeat(16_667u, 990)
            .Concat(Enumerable.Repeat(50_000u, 9))
            .Append(200_000u);

        var low = RtssSharedMemoryClient.ComputeOnePercentLow(samples);

        Assert.Equal(20, low);
    }
}
