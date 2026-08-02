using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamSplashStartupCoordinatorTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SteamStartupTimingPolicy Timing =
        SteamStartupTimingPolicy.FromGracePeriod(TimeSpan.FromSeconds(30), isAdaptive: false);

    [Fact]
    public void Observe_CompletesOnlyAfterStableUiAndForegroundSignals()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(visible: true, gamepad: true, foreground: true);

        var first = coordinator.Observe(
            StartedAt.AddSeconds(5), runtime, SteamClientStartupStage.Ready,
            SteamUiState.Gamepad, hostAvailable: true, hostSteamSignalReady: true);
        var second = coordinator.Observe(
            StartedAt.AddSeconds(6), runtime, SteamClientStartupStage.Ready,
            SteamUiState.Gamepad, hostAvailable: true, hostSteamSignalReady: true);

        Assert.NotEqual(SteamSplashNextAction.CompleteWithSteam, first.Action);
        Assert.Equal(SteamSplashNextAction.CompleteWithSteam, second.Action);
    }

    [Fact]
    public void Observe_ProtectsActiveGameWithoutRestartingSteam()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(gameRunning: true);

        var decision = coordinator.Observe(
            StartedAt.AddSeconds(5), runtime, SteamClientStartupStage.Protected,
            SteamUiState.Unknown, hostAvailable: true, hostSteamSignalReady: false);

        Assert.Equal(SteamSplashNextAction.CompleteWithDesktop, decision.Action);
        Assert.False(decision.CanRestartSteam);
    }

    [Fact]
    public void Observe_FailedRecoveryExposesActionsThenFallsBackToDesktop()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime();

        var failed = coordinator.Observe(
            StartedAt.AddSeconds(40), runtime, SteamClientStartupStage.Failed,
            SteamUiState.Error, hostAvailable: true, hostSteamSignalReady: false);
        var fallback = coordinator.Observe(
            StartedAt.AddSeconds(56), runtime, SteamClientStartupStage.Failed,
            SteamUiState.Error, hostAvailable: true, hostSteamSignalReady: false);

        Assert.True(failed.ShowRecoveryActions);
        Assert.Equal(SteamSplashNextAction.CompleteWithDesktop, fallback.Action);
    }

    [Fact]
    public void Observe_UpdateMakesManualRestartUnavailable()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(updateInProgress: true);

        var decision = coordinator.Observe(
            StartedAt.AddSeconds(25), runtime, SteamClientStartupStage.Updating,
            SteamUiState.Updating, hostAvailable: true, hostSteamSignalReady: false);

        Assert.True(decision.ShowRecoveryActions);
        Assert.False(decision.CanRestartSteam);
    }

    [Fact]
    public void ExtendWait_DelaysButDoesNotDisableTheFinalFallback()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime();
        _ = coordinator.Observe(
            StartedAt.AddSeconds(40), runtime, SteamClientStartupStage.Failed,
            SteamUiState.Error, hostAvailable: true, hostSteamSignalReady: false);

        coordinator.ExtendWait(StartedAt.AddSeconds(45));
        var duringExtension = coordinator.Observe(
            StartedAt.AddMinutes(4), runtime, SteamClientStartupStage.Failed,
            SteamUiState.Error, hostAvailable: true, hostSteamSignalReady: false);
        var afterExtension = coordinator.Observe(
            StartedAt.AddMinutes(6), runtime, SteamClientStartupStage.Failed,
            SteamUiState.Error, hostAvailable: true, hostSteamSignalReady: false);

        Assert.Equal(SteamSplashNextAction.Wait, duringExtension.Action);
        Assert.Equal(SteamSplashNextAction.CompleteWithDesktop, afterExtension.Action);
    }

    [Theory]
    [InlineData(SteamUiState.Login)]
    [InlineData(SteamUiState.Offline)]
    public void Observe_LoginAndOfflineScreensNeverCountAsReadyOrRestartable(SteamUiState uiState)
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(visible: true, gamepad: true, foreground: true);

        var first = coordinator.Observe(
            StartedAt.AddSeconds(5), runtime, SteamClientStartupStage.Ready,
            uiState, hostAvailable: true, hostSteamSignalReady: true);
        var second = coordinator.Observe(
            StartedAt.AddSeconds(6), runtime, SteamClientStartupStage.Ready,
            uiState, hostAvailable: true, hostSteamSignalReady: true);

        Assert.Equal(SteamSplashNextAction.Wait, first.Action);
        Assert.Equal(SteamSplashNextAction.Wait, second.Action);
        Assert.True(second.ShowRecoveryActions);
        Assert.False(second.CanRestartSteam);
    }

    [Fact]
    public void Observe_StopsStealingFocusAndOffersActionsAfterBoundedAttempts()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(visible: true, gamepad: true, foreground: false);

        var first = coordinator.Observe(
            StartedAt.AddSeconds(2), runtime, SteamClientStartupStage.Starting,
            SteamUiState.Gamepad, hostAvailable: true, hostSteamSignalReady: true);
        var second = coordinator.Observe(
            StartedAt.AddSeconds(4), runtime, SteamClientStartupStage.Starting,
            SteamUiState.Gamepad, hostAvailable: true, hostSteamSignalReady: true);
        var third = coordinator.Observe(
            StartedAt.AddSeconds(6), runtime, SteamClientStartupStage.Starting,
            SteamUiState.Gamepad, hostAvailable: true, hostSteamSignalReady: true);
        var bounded = coordinator.Observe(
            StartedAt.AddSeconds(8), runtime, SteamClientStartupStage.Starting,
            SteamUiState.Gamepad, hostAvailable: true, hostSteamSignalReady: true);

        Assert.All(new[] { first, second, third }, decision =>
            Assert.Equal(SteamSplashNextAction.FocusSteam, decision.Action));
        Assert.Equal(SteamSplashNextAction.Wait, bounded.Action);
        Assert.True(bounded.ShowRecoveryActions);
    }

    private static SteamRuntimeObservation CreateRuntime(
        bool visible = false,
        bool gamepad = false,
        bool foreground = false,
        bool gameRunning = false,
        bool updateInProgress = false) =>
        new(
            SteamRunning: visible,
            WebHelperRunning: visible,
            ErrorReporterRunning: false,
            GameOrOverlayRunning: gameRunning,
            UpdateInProgress: updateInProgress,
            Windows: new SteamWindowSnapshot(
                visible,
                gamepad,
                foreground,
                IntPtr.Zero,
                IntPtr.Zero,
                string.Empty,
                string.Empty));
}
