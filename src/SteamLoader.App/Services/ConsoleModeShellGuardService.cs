using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Services;

public sealed class ConsoleModeShellGuardService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RestoreGracePeriod = TimeSpan.FromSeconds(8);

    private readonly SteamDevToolsClient _devToolsClient;
    private readonly SteamLoaderSettingsService _settingsService;
    private readonly WindowsShellVisibilityService _shellVisibilityService;
    private readonly ConsoleShellState _state;

    public ConsoleModeShellGuardService(
        SteamDevToolsClient devToolsClient,
        SteamLoaderSettingsService settingsService,
        WindowsShellVisibilityService shellVisibilityService)
        : this(devToolsClient, settingsService, shellVisibilityService, new ConsoleShellState())
    {
    }

    internal ConsoleModeShellGuardService(
        SteamDevToolsClient devToolsClient,
        SteamLoaderSettingsService settingsService,
        WindowsShellVisibilityService shellVisibilityService,
        ConsoleShellState state)
    {
        _devToolsClient = devToolsClient;
        _settingsService = settingsService;
        _shellVisibilityService = shellVisibilityService;
        _state = state;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastBigPictureSeenAt = DateTimeOffset.MinValue;
        var desktopTracker = new ConsoleDesktopRequestTracker();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var settings = _settingsService.GetSnapshot();
                var hideEnabled = SteamLoaderRuntime.IsConsoleStartupMode(settings.StartupMode)
                    && settings.HideWindowsShellInConsoleMode;
                var bigPictureActive = hideEnabled && await IsBigPictureActiveAsync(cancellationToken);
                if (bigPictureActive)
                {
                    lastBigPictureSeenAt = now;
                }

                var desktopRequested = desktopTracker.IsDesktopActive(
                    now,
                    _state.GetDesktopRequest(),
                    bigPictureActive && SteamBigPictureForegroundDetector.IsGamepadUiForeground(),
                    out var desktopRequestEnded);
                if (desktopRequestEnded)
                {
                    _state.ClearDesktopRequest();
                    SteamStartupDiagnostics.Write("console desktop session ended: Big Picture is in front again");
                }

                switch (Decide(
                    hideEnabled,
                    bigPictureActive,
                    desktopRequested,
                    _shellVisibilityService.IsHidden,
                    now - lastBigPictureSeenAt > RestoreGracePeriod,
                    _state.IsStartCoverActive()))
                {
                    case ShellChromeAction.Hide:
                        _shellVisibilityService.HideShellChrome();
                        break;
                    case ShellChromeAction.Restore:
                        _shellVisibilityService.RestoreShellChrome();
                        break;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally
        {
            _shellVisibilityService.RestoreShellChrome();
        }
    }

    /// <summary>
    /// Decides from the current state, never from what this process remembers,
    /// so a restarted host (or one that replaced a crashed host) heals a
    /// taskbar that was left hidden.
    /// </summary>
    internal static ShellChromeAction Decide(
        bool hideEnabled,
        bool bigPictureActive,
        bool desktopRequested,
        bool chromeHidden,
        bool restoreGraceElapsed,
        bool startCoverActive = false)
    {
        if (bigPictureActive && !desktopRequested)
        {
            return ShellChromeAction.Hide;
        }

        // The Console Mode splash owns the taskbar until its hand-off; showing
        // it here would flash it over the splash.
        if (!chromeHidden || startCoverActive)
        {
            return ShellChromeAction.None;
        }

        // An explicit desktop request or a disabled hide setting restores
        // at once; Big Picture disappearing gets a grace period because Steam
        // briefly drops its surface while it restarts the Gamepad UI.
        return !hideEnabled || desktopRequested || restoreGraceElapsed
            ? ShellChromeAction.Restore
            : ShellChromeAction.None;
    }

    private async Task<bool> IsBigPictureActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _devToolsClient.HasBigPictureSurfaceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

internal enum ShellChromeAction
{
    None,
    Hide,
    Restore
}

/// <summary>
/// Tracks one "show me the Windows desktop" request. It stays active while the
/// user works on the desktop and ends when Big Picture comes back to the front
/// (Guide button, Big Picture shortcut, "Return to Console").
/// </summary>
internal sealed class ConsoleDesktopRequestTracker
{
    // If Big Picture never left the foreground after a request (for example,
    // Steam refused to minimize), do not leave the taskbar over the console.
    internal static readonly TimeSpan UnconfirmedRequestTimeout = TimeSpan.FromSeconds(10);

    private DateTimeOffset? _trackedRequest;
    private bool _bigPictureLeftForeground;

    public bool IsDesktopActive(
        DateTimeOffset now,
        DateTimeOffset? request,
        bool bigPictureForeground,
        out bool requestEnded)
    {
        requestEnded = false;
        if (request is null)
        {
            Reset();
            return false;
        }

        if (_trackedRequest != request)
        {
            _trackedRequest = request;
            _bigPictureLeftForeground = false;
        }

        if (!bigPictureForeground)
        {
            _bigPictureLeftForeground = true;
            return true;
        }

        if (_bigPictureLeftForeground || now - request.Value >= UnconfirmedRequestTimeout)
        {
            Reset();
            requestEnded = true;
            return false;
        }

        return true;
    }

    private void Reset()
    {
        _trackedRequest = null;
        _bigPictureLeftForeground = false;
    }
}
