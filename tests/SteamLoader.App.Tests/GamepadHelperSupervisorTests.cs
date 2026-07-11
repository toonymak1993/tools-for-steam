using SteamLoader.App.Infrastructure.Helpers;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class GamepadHelperSupervisorTests
{
    [Fact]
    public async Task EnsureRunningOnceAsync_DoesNotRestartHealthyHelper()
    {
        var startAttempts = 0;
        var supervisor = CreateSupervisor(
            isRegistered: () => true,
            isRunning: () => true,
            tryRun: () =>
            {
                startAttempts++;
                return new GamepadHelperStartResult(true, string.Empty);
            });

        var running = await supervisor.EnsureRunningOnceAsync(CancellationToken.None);

        Assert.True(running);
        Assert.Equal(0, startAttempts);
    }

    [Fact]
    public async Task EnsureRunningOnceAsync_RestartsStoppedHelperAndConfirmsIt()
    {
        var helperRunning = false;
        var supervisor = CreateSupervisor(
            isRegistered: () => true,
            isRunning: () => helperRunning,
            tryRun: () =>
            {
                helperRunning = true;
                return new GamepadHelperStartResult(true, string.Empty);
            });

        var running = await supervisor.EnsureRunningOnceAsync(CancellationToken.None);

        Assert.True(running);
    }

    [Fact]
    public async Task EnsureRunningOnceAsync_RetriesWhenSchedulerAcceptedButDidNotStartHelper()
    {
        var startAttempts = 0;
        var supervisor = CreateSupervisor(
            isRegistered: () => true,
            isRunning: () => false,
            tryRun: () =>
            {
                startAttempts++;
                return new GamepadHelperStartResult(true, string.Empty);
            });

        Assert.False(await supervisor.EnsureRunningOnceAsync(CancellationToken.None));
        Assert.False(await supervisor.EnsureRunningOnceAsync(CancellationToken.None));
        Assert.Equal(2, startAttempts);
    }

    [Fact]
    public async Task EnsureRunningOnceAsync_UsesFallbackWhenTaskIsMissing()
    {
        var startAttempts = 0;
        var supervisor = CreateSupervisor(
            isRegistered: () => false,
            isRunning: () => false,
            tryRun: () =>
            {
                startAttempts++;
                return new GamepadHelperStartResult(true, string.Empty);
            });

        var running = await supervisor.EnsureRunningOnceAsync(CancellationToken.None);

        Assert.False(running);
        Assert.Equal(0, startAttempts);
    }

    private static GamepadHelperSupervisor CreateSupervisor(
        Func<bool> isRegistered,
        Func<bool> isRunning,
        Func<GamepadHelperStartResult> tryRun)
    {
        return new GamepadHelperSupervisor(
            isRegistered,
            isRunning,
            tryRun,
            (_, _) => Task.CompletedTask,
            _ => { });
    }
}
