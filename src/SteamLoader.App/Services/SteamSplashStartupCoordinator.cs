namespace SteamLoader.App.Services;

internal sealed class SteamSplashStartupCoordinator
{
    private const int RequiredStableForegroundObservations = 2;
    private const int RequiredStableUiObservationsWithHostSignal = 2;
    private const int RequiredStableUiObservationsWithoutHostSignal = 4;
    private const int MaximumAutomaticFocusAttempts = 3;
    private static readonly TimeSpan FailedStateDesktopFallbackDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UserWaitExtension = TimeSpan.FromMinutes(5);

    private readonly SteamStartupTimingPolicy _timing;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _failedAt;
    private DateTimeOffset? _hostUnavailableAt;
    private DateTimeOffset? _waitExtendedUntil;
    private int _stableForegroundCount;
    private int _stableUiCount;
    private int _stableHostSignalCount;
    private int _automaticFocusAttempts;

    public SteamSplashStartupCoordinator(
        SteamStartupTimingPolicy timing,
        DateTimeOffset startedAt)
    {
        _timing = timing;
        _startedAt = startedAt;
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

        if (runtime.GameOrOverlayRunning || startupStage == SteamClientStartupStage.Protected)
        {
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithDesktop,
                ShowRecoveryActions: false,
                CanRestartSteam: false,
                "Active Steam game or download protected from startup recovery.");
        }

        var updateProtected = runtime.UpdateInProgress ||
            startupStage == SteamClientStartupStage.Updating ||
            uiState == SteamUiState.Updating;
        var elapsed = now - _startedAt;
        var recoveryActionsDue = elapsed >= _timing.ShowRecoveryActionsAfter;

        if (blockingUiState && startupStage != SteamClientStartupStage.Failed)
        {
            var blockingUiDeadline = _waitExtendedUntil ?? _startedAt + _timing.SplashSafetyTimeout;
            if (now >= blockingUiDeadline)
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

        if (_hostUnavailableAt.HasValue && now - _hostUnavailableAt.Value >= TimeSpan.FromSeconds(30))
        {
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithDesktop,
                ShowRecoveryActions: true,
                CanRestartSteam: false,
                "The background service is unavailable.");
        }

        var effectiveSafetyDeadline = _waitExtendedUntil ?? _startedAt + _timing.SplashSafetyTimeout;
        if (now >= effectiveSafetyDeadline)
        {
            return new SteamSplashDecision(
                SteamSplashNextAction.CompleteWithDesktop,
                ShowRecoveryActions: true,
                CanRestartSteam: !updateProtected,
                "The splash safety limit was reached without another automatic restart.");
        }

        if (runtime.Windows.HasVisibleSteamWindow &&
            uiState != SteamUiState.Desktop &&
            !runtime.Windows.IsSteamForeground)
        {
            if (recoveryActionsDue || _automaticFocusAttempts >= MaximumAutomaticFocusAttempts)
            {
                return new SteamSplashDecision(
                    SteamSplashNextAction.Wait,
                    ShowRecoveryActions: true,
                    CanRestartSteam: !updateProtected,
                    "Steam is visible, but Windows did not keep it in the foreground.");
            }

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
                    : "Waiting for Steam Gamepad UI.");
    }

    public void ExtendWait(DateTimeOffset now)
    {
        _waitExtendedUntil = now + UserWaitExtension;
        _failedAt = null;
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
