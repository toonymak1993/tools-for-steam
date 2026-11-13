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
    public void Observe_RunningUpdateCountsAsProgressAndIsNeverRestartable()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(updateInProgress: true);

        SteamSplashDecision decision = null!;
        for (var seconds = 2; seconds <= 240; seconds += 2)
        {
            decision = coordinator.Observe(
                StartedAt.AddSeconds(seconds), runtime, SteamClientStartupStage.Updating,
                SteamUiState.Updating, hostAvailable: true, hostSteamSignalReady: false);
            Assert.Equal(SteamSplashNextAction.Wait, decision.Action);
        }

        Assert.False(decision.ShowRecoveryActions);
        Assert.False(decision.CanRestartSteam);
    }

    [Fact]
    public void Observe_ColdStartGetsNoRecoveryChoicesWhileSteamKeepsProgressing()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var noSteam = CreateRuntime();
        var steamOnly = CreateRuntime() with { SteamRunning = true };
        var webHelper = steamOnly with { WebHelperRunning = true };

        // Nothing for 40 s, steam.exe at 40 s, web helper at 65 s: never "taking longer".
        for (var seconds = 2; seconds < 40; seconds += 2)
        {
            Assert.False(Observe(coordinator, seconds, noSteam).ShowRecoveryActions);
        }

        for (var seconds = 40; seconds < 65; seconds += 2)
        {
            Assert.False(Observe(coordinator, seconds, steamOnly).ShowRecoveryActions);
        }

        for (var seconds = 65; seconds < 95; seconds += 2)
        {
            Assert.False(Observe(coordinator, seconds, webHelper).ShowRecoveryActions);
        }

        // 30 s without any new signal after the usual start time: now offer choices.
        var stalled = Observe(coordinator, 96, webHelper);
        Assert.True(stalled.ShowRecoveryActions);
        Assert.Equal(SteamSplashNextAction.Wait, stalled.Action);
    }

    [Fact]
    public void Observe_ProgressMovesTheDesktopFallbackButNotBeyondTheAbsoluteLimit()
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var steamOnly = CreateRuntime() with { SteamRunning = true };
        var webHelper = steamOnly with { WebHelperRunning = true };

        // The web helper appears at 4:50, right before the 5 min safety limit.
        Observe(coordinator, 10, steamOnly);
        Assert.Equal(SteamSplashNextAction.Wait, Observe(coordinator, 290, webHelper).Action);
        Assert.Equal(SteamSplashNextAction.Wait, Observe(coordinator, 360, webHelper).Action);
        Assert.Equal(SteamSplashNextAction.CompleteWithDesktop, Observe(coordinator, 411, webHelper).Action);

        // An endless update still ends at the absolute limit.
        var updating = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var update = CreateRuntime(updateInProgress: true);
        Assert.Equal(SteamSplashNextAction.Wait, Observe(updating, 14 * 60, update, SteamClientStartupStage.Updating).Action);
        Assert.Equal(
            SteamSplashNextAction.CompleteWithDesktop,
            Observe(updating, 15 * 60 + 1, update, SteamClientStartupStage.Updating).Action);
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
    public void Observe_LoginAndOfflineScreensInBigPictureAreHandedToSteam(SteamUiState uiState)
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(visible: true, gamepad: true, foreground: true);

        var first = coordinator.Observe(
            StartedAt.AddSeconds(5), runtime, SteamClientStartupStage.Ready,
            uiState, hostAvailable: true, hostSteamSignalReady: true);
        var second = coordinator.Observe(
            StartedAt.AddSeconds(6), runtime, SteamClientStartupStage.Ready,
            uiState, hostAvailable: true, hostSteamSignalReady: true);

        // The user has to sign in or confirm in Big Picture: the splash must
        // not cover that screen, and nothing restarts Steam.
        Assert.Equal(SteamSplashNextAction.Wait, first.Action);
        Assert.Equal(SteamSplashNextAction.CompleteWithSteam, second.Action);
        Assert.False(second.CanRestartSteam);
    }

    [Theory]
    [InlineData(SteamUiState.Login)]
    [InlineData(SteamUiState.Offline)]
    public void Observe_LoginAndOfflineWithoutBigPictureNeverCountAsReadyOrRestartable(SteamUiState uiState)
    {
        var coordinator = new SteamSplashStartupCoordinator(Timing, StartedAt);
        var runtime = CreateRuntime(visible: true, gamepad: false, foreground: true);

        var first = Observe(coordinator, 5, runtime, SteamClientStartupStage.Ready, uiState);
        var second = Observe(coordinator, 6, runtime, SteamClientStartupStage.Ready, uiState);

        Assert.Equal(SteamSplashNextAction.Wait, first.Action);
        Assert.Equal(SteamSplashNextAction.Wait, second.Action);
        Assert.True(second.ShowRecoveryActions);
        Assert.False(second.CanRestartSteam);
    }

    [Fact]
    public void Observe_HandsOverToReadyBigPictureAfterBoundedFocusAttempts()
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
        // Never keep covering a ready Big Picture just because Windows refused
        // the foreground.
        Assert.Equal(SteamSplashNextAction.CompleteWithSteam, bounded.Action);
        Assert.False(bounded.ShowRecoveryActions);
    }

    private static SteamSplashDecision Observe(
        SteamSplashStartupCoordinator coordinator,
        int seconds,
        SteamRuntimeObservation runtime,
        SteamClientStartupStage stage = SteamClientStartupStage.Starting,
        SteamUiState uiState = SteamUiState.Starting) =>
        coordinator.Observe(
            StartedAt.AddSeconds(seconds), runtime, stage,
            uiState, hostAvailable: true, hostSteamSignalReady: false);

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
