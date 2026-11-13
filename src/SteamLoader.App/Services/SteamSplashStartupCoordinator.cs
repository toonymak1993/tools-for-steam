namespace SteamLoader.App.Services;

internal sealed class SteamSplashStartupCoordinator
{
    private const int RequiredStableForegroundObservations = 2;
    private const int RequiredStableUiObservationsWithHostSignal = 2;
    private const int RequiredStableUiObservationsWithoutHostSignal = 4;
    private const int RequiredStableBlockingUiObservations = 2;
    private const int MaximumAutomaticFocusAttempts = 3;
    private static readonly TimeSpan FailedStateDesktopFallbackDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UserWaitExtension = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HostUnavailableDesktopFallbackDelay = TimeSpan.FromSeconds(60);

    // Recovery choices appear only when Steam is slower than usual AND has not
    // shown a new startup signal for this long. A slow but progressing start
    // never sees "Steam is taking longer than usual".
    private static readonly TimeSpan ProgressStallWindow = TimeSpan.FromSeconds(30);

    // Progress also moves the automatic desktop fallback, but never beyond
    // this absolute limit.
    private static readonly TimeSpan StalledSafetyWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumSplashDuration = TimeSpan.FromMinutes(15);

    private readonly SteamStartupTimingPolicy _timing;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _failedAt;
    private DateTimeOffset? _hostUnavailableAt;
    private DateTimeOffset? _waitExtendedUntil;
    private DateTimeOffset _lastProgressAt;
    private int _lastProgressLevel;
    private int _stableForegroundCount;
    private int _stableUiCount;
    private int _stableHostSignalCount;
    private int _stableBlockingUiCount;
    private int _automaticFocusAttempts;

    public SteamSplashStartupCoordinator(
        SteamStartupTimingPolicy timing,
        DateTimeOffset startedAt)
    {
        _timing = timing;
        _startedAt = startedAt;
        _lastProgressAt = startedAt;
    }

    public SteamSplashDecision Observe(
        DateTimeOffset now,
        SteamRuntimeObservation runtime,
        SteamClientStartupStage startupStage,
        SteamUiState uiState,
        bool hostAvailable,
        bool hostSteamSignalReady)
    {
        if (_waitExtendedUntil.HasValue && now >= _waitExtendedUntil.Value)
        {
            _waitExtendedUntil = null;
        }

        _hostUnavailableAt = hostAvailable
            ? null
            : _hostUnavailableAt ?? now;

        var blockingUiState = uiState is SteamUiState.Login or SteamUiState.Offline or SteamUiState.Error;
        var gamepadCandidate = uiState == SteamUiState.Gamepad ||
            (uiState is SteamUiState.Unknown or SteamUiState.Starting &&
             runtime.Windows.HasLikelyGamepadWindow);
        var uiVisible = !blockingUiState && gamepadCandidate;
        _stableUiCount = uiVisible ? _stableUiCount + 1 : 0;
        _stableForegroundCount = runtime.Windows.IsSteamForeground
            ? _stableForegroundCount + 1
            : 0;
        if (runtime.Windows.IsSteamForeground)
        {
            _automaticFocusAttempts = 0;
        }

        _stableHostSignalCount = hostSteamSignalReady && uiVisible
            ? _stableHostSignalCount + 1
            : 0;
        _stableBlockingUiCount = blockingUiState && runtime.Windows.HasLikelyGamepadWindow
            ? _stableBlockingUiCount + 1
            : 0;

        var updateProtected = runtime.UpdateInProgress ||
            startupStage == SteamClientStartupStage.Updating ||
            uiState == SteamUiState.Updating;
        TrackProgress(now, runtime, uiVisible, hostSteamSignalReady, updateProtected);

        var uiStable =
            _stableHostSignalCount >= RequiredStableUiObservationsWithHostSignal ||
            _stableUiCount >= RequiredStableUiObservationsWithoutHostSignal;
        if (uiStable && _stableForegroundCount >= RequiredStableForegroundObservations)
        {
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithSteam,
                ShowRecoveryActions: false,
                CanRestartSteam: false,
                "Steam is ready in the foreground.");
        }

        if (uiStable && _automaticFocusAttempts >= MaximumAutomaticFocusAttempts)
        {
            // Big Picture is up but Windows did not hand it the foreground.
            // The splash must not keep covering a ready Steam: step aside so
            // Big Picture is the top window.
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithSteam,
                ShowRecoveryActions: false,
                CanRestartSteam: false,
                "Steam Big Picture is ready; handing over without confirmed foreground.");
        }

        if (_stableBlockingUiCount >= RequiredStableBlockingUiObservations)
        {
            // Sign-in, offline or error screens in Big Picture need the user.
            // Show them instead of covering them with the splash.
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithSteam,
                ShowRecoveryActions: false,
                CanRestartSteam: false,
                uiState switch
                {
                    SteamUiState.Login => "Steam Big Picture is waiting for sign-in; handing over to Steam.",
                    SteamUiState.Offline => "Steam Big Picture shows an offline/network screen; handing over to Steam.",
                    _ => "Steam Big Picture shows an error screen; handing over to Steam."
                });
        }

        if (runtime.GameOrOverlayRunning || startupStage == SteamClientStartupStage.Protected)
        {
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithDesktop,
                ShowRecoveryActions: false,
                CanRestartSteam: false,
                "Active Steam game or download protected from startup recovery.");
        }

        var elapsed = now - _startedAt;
        var stalled = now - _lastProgressAt >= ProgressStallWindow;
        var recoveryActionsDue = elapsed >= _timing.ShowRecoveryActionsAfter && stalled;
        var safetyDeadline = _waitExtendedUntil ?? ResolveSafetyDeadline();

        if (blockingUiState && startupStage != SteamClientStartupStage.Failed)
        {
            if (now >= safetyDeadline)
            {
                return new SteamSplashDecision(
                    SteamSplashNextAction.CompleteWithDesktop,
                    ShowRecoveryActions: true,
                    CanRestartSteam: uiState == SteamUiState.Error,
                    "Steam requires user interaction; opening the Windows desktop safely.");
            }

            return new SteamSplashDecision(
                SteamSplashNextAction.Wait,
                ShowRecoveryActions: true,
                CanRestartSteam: uiState == SteamUiState.Error,
                uiState switch
                {
                    SteamUiState.Login => "Steam is waiting for sign-in; automatic restart is disabled.",
                    SteamUiState.Offline => "Steam is showing an offline/network screen; automatic restart is disabled.",
                    _ => "Steam reported an error screen."
                });
        }

        if (startupStage == SteamClientStartupStage.Failed)
        {
            _failedAt ??= now;
            if (_failedAt.HasValue &&
                now - _failedAt.Value >= FailedStateDesktopFallbackDelay &&
                !_waitExtendedUntil.HasValue)
            {
                return new SteamSplashDecision(
                    SteamSplashNextAction.CompleteWithDesktop,
                    ShowRecoveryActions: true,
                    CanRestartSteam: !updateProtected,
                    "Automatic Steam recovery stopped safely.");
            }

            return new SteamSplashDecision(
                SteamSplashNextAction.Wait,
                ShowRecoveryActions: true,
                CanRestartSteam: !updateProtected,
                "Steam needs attention. The Windows desktop will open automatically.");
        }

        _failedAt = null;

        if (_hostUnavailableAt.HasValue && now - _hostUnavailableAt.Value >= HostUnavailableDesktopFallbackDelay)
        {
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithDesktop,
                ShowRecoveryActions: true,
                CanRestartSteam: false,
                "The background service is unavailable.");
        }

        if (now >= safetyDeadline)
        {
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithDesktop,
                ShowRecoveryActions: true,
                CanRestartSteam: !updateProtected,
                "The splash safety limit was reached without another automatic restart.");
        }

        if (runtime.Windows.HasVisibleSteamWindow &&
            uiState != SteamUiState.Desktop &&
            !runtime.Windows.IsSteamForeground &&
            _automaticFocusAttempts < MaximumAutomaticFocusAttempts)
        {
            _automaticFocusAttempts++;
            return new SteamSplashDecision(
                SteamSplashNextAction.FocusSteam,
                ShowRecoveryActions: false,
                CanRestartSteam: !updateProtected,
                "Steam UI detected; confirming foreground focus.");
        }

        return new SteamSplashDecision(
            SteamSplashNextAction.Wait,
            ShowRecoveryActions: recoveryActionsDue,
            CanRestartSteam: !updateProtected,
            updateProtected
                ? "Steam update/download activity detected; automatic restart is paused."
                : startupStage == SteamClientStartupStage.Recovering
                    ? "Steam is completing its single controlled recovery."
                    : runtime.Windows.HasVisibleSteamWindow && !runtime.Windows.IsSteamForeground
                        ? "Steam is visible, but Windows did not keep it in the foreground."
                        : "Waiting for Steam Gamepad UI.");
    }

    public void ExtendWait(DateTimeOffset now)
    {
        _waitExtendedUntil = now + UserWaitExtension;
        _failedAt = null;
        _lastProgressAt = now;
    }

    /// <summary>
    /// Every new startup signal (steam.exe, web helper, a visible window, the
    /// host's Steam connection, Big Picture) counts as progress. An active
    /// Steam update counts as progress the whole time it runs. A signal that
    /// disappears (Steam restarting itself) and comes back counts again.
    /// </summary>
    private void TrackProgress(
        DateTimeOffset now,
        SteamRuntimeObservation runtime,
        bool uiVisible,
        bool hostSteamSignalReady,
        bool updateProtected)
    {
        var level =
            (runtime.SteamRunning ? 1 : 0) +
            (runtime.WebHelperRunning ? 1 : 0) +
            (runtime.Windows.HasVisibleSteamWindow ? 1 : 0) +
            (hostSteamSignalReady ? 1 : 0) +
            (uiVisible ? 1 : 0);
        if (level > _lastProgressLevel || updateProtected)
        {
            _lastProgressAt = now;
        }

        _lastProgressLevel = level;
    }

    private DateTimeOffset ResolveSafetyDeadline()
    {
        var timeoutDeadline = _startedAt + _timing.SplashSafetyTimeout;
        var progressDeadline = _lastProgressAt + StalledSafetyWindow;
        var absoluteDeadline = _startedAt + MaximumSplashDuration;
        var deadline = progressDeadline > timeoutDeadline ? progressDeadline : timeoutDeadline;
        return deadline < absoluteDeadline ? deadline : absoluteDeadline;
    }
}

internal enum SteamSplashNextAction
{
    Wait,
    FocusSteam,
    CompleteWithSteam,
    CompleteWithDesktop
}

internal sealed record SteamSplashDecision(
    SteamSplashNextAction Action,
    bool ShowRecoveryActions,
    bool CanRestartSteam,
    string Detail);
