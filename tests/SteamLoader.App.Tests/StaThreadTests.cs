using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Audio;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StaThreadTests
{
    [Fact]
    public void AudioService_DoesNotCreateCoreAudioControllerUntilFirstUse()
    {
        using var service = new CoreAudioOutputDeviceService();
        var controllerField = typeof(CoreAudioOutputDeviceService).GetField(
            "_controller",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(controllerField);
        Assert.Null(controllerField.GetValue(service));
    }

    [Fact]
    public async Task RunAsync_ReusesOneDedicatedStaThread()
    {
        var calls = Enumerable.Range(0, 5)
            .Select(_ => StaThread.RunAsync(
                () => new
                {
                    ThreadId = Environment.CurrentManagedThreadId,
                    ApartmentState = Thread.CurrentThread.GetApartmentState()
                },
                CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(calls);

        Assert.Single(results.Select(result => result.ThreadId).Distinct());
        Assert.All(results, result => Assert.Equal(ApartmentState.STA, result.ApartmentState));
    }

    [Fact]
    public async Task RunAsync_DoesNotExecuteAlreadyCancelledWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executed = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StaThread.RunAsync(
                () =>
                {
                    executed = true;
                    return true;
                },
                cancellation.Token));

        Assert.False(executed);
    }
}
