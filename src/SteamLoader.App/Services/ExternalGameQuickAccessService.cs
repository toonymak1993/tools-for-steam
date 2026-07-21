using System.Diagnostics;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;

namespace SteamLoader.App.Services;

/// <summary>
/// Opens Steam's real Big Picture Quick Access surface in front of a Store Sync
/// game when the game process has no injected Steam overlay renderer. Closing the
/// Quick Access surface restores the exact game window that was active before it.
/// </summary>
public sealed class ExternalGameQuickAccessService
{
    private static readonly TimeSpan QuickAccessOpenTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan VisibilityPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan GracefulGameCloseTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly StoreSyncService _storeSyncService;
    private readonly ProcessWindowService _processWindowService;
    private readonly SteamWindowFocusService _steamWindowFocusService;
    private readonly SteamDevToolsClient _devToolsClient;
    private readonly QuickAccessLiveUpdateHub _liveUpdateHub;
    private readonly string _logPath;

    private ActiveSession? _activeSession;

    public ExternalGameQuickAccessService(
        StoreSyncService storeSyncService,
        ProcessWindowService processWindowService,
        SteamWindowFocusService steamWindowFocusService,
        SteamDevToolsClient devToolsClient,
        QuickAccessLiveUpdateHub liveUpdateHub,
        string logPath)
    {
        _storeSyncService = storeSyncService;
        _processWindowService = processWindowService;
        _steamWindowFocusService = steamWindowFocusService;
        _devToolsClient = devToolsClient;
        _liveUpdateHub = liveUpdateHub;
        _logPath = logPath;
    }

    public ExternalGameQuickAccessState GetState()
    {
        lock (_gate)
        {
            return BuildState(_activeSession);
        }
    }

    public async Task<bool> TryOpenForForegroundGameAsync(
        CancellationToken cancellationToken,
        ExternalGameQuickAccessTarget? suppliedTarget = null)
    {
        lock (_gate)
        {
            if (_activeSession is not null)
            {
                return true;
            }
        }

        var resolvedTarget = suppliedTarget is null
            ? PerformanceForegroundTargetResolver.TryResolve()
            : null;
        var processId = suppliedTarget?.ProcessId ?? resolvedTarget?.ProcessId ?? 0;
        var processName = suppliedTarget?.ProcessName ?? resolvedTarget?.ProcessName ?? string.Empty;
        var windowTitle = suppliedTarget?.WindowTitle ?? resolvedTarget?.WindowTitle ?? string.Empty;
        var executablePath = suppliedTarget?.ExecutablePath ?? resolvedTarget?.ExecutablePath ?? string.Empty;
        var windowHandle = suppliedTarget?.WindowHandle ?? resolvedTarget?.WindowHandle ?? string.Empty;

        if (processId <= 0)
        {
            Log("fallback-rejected reason=foreground-target-unavailable");
            return false;
        }

        if (string.IsNullOrWhiteSpace(windowHandle))
        {
            var foregroundWindow = _processWindowService.GetSnapshot().Windows.FirstOrDefault(window =>
                window.IsForeground && window.ProcessId == processId);
            windowHandle = foregroundWindow?.Handle ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(windowHandle))
        {
            Log($"fallback-rejected reason=foreground-window-unavailable pid={processId} name={processName}");
            return false;
        }

        var managedGame = _storeSyncService.TryMatchManagedGame(executablePath, processName);
        if (managedGame is null)
        {
            Log(
                $"fallback-rejected reason=store-sync-match-missing pid={processId} name={processName} " +
                $"title={windowTitle} executable={executablePath}");
            return false;
        }

        var overlayRendererMissing = suppliedTarget?.OverlayRendererMissing ??
            XboxStoreLaunchHost.IsSteamOverlayRendererMissing((uint)processId);
        if (!overlayRendererMissing)
        {
            Log(
                $"native-overlay-path pid={processId} title={managedGame.Title} " +
                "reason=renderer-present-or-unavailable");
            return false;
        }

        var session = new ActiveSession(
            Guid.NewGuid(),
            managedGame.Title,
            managedGame.StoreId,
            processId,
            windowHandle,
            SuppressGameRestore: false,
            QuickAccessReady: false);

        lock (_gate)
        {
            if (_activeSession is not null)
            {
                return true;
            }

            _activeSession = session;
        }

        PublishState();
        Log(
            $"fallback-opening session={session.Id} pid={session.ProcessId} " +
            $"store={session.StoreId} title={session.GameTitle} window={session.GameWindowHandle}");

        try
        {
            var focusResult = await _steamWindowFocusService.FocusSteamWindowAsync(cancellationToken);
            var openedDirectly = await _devToolsClient.TryOpenQuickAccessMenuAsync(cancellationToken);
            var opened = openedDirectly ||
                await _devToolsClient.SendControlDigitShortcutAsync(2, cancellationToken);
            if (!opened)
            {
                throw new InvalidOperationException("Steam did not accept the Quick Access command.");
            }

            Log(
                $"fallback-opened session={session.Id} delivery={(openedDirectly ? "steam-direct" : "steam-ctrl-2")} " +
                $"focus={focusResult}");
            _ = MonitorQuickAccessAsync(session.Id, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearSession(session.Id, restoreGame: true, "opening-cancelled");
            throw;
        }
        catch (Exception exception)
        {
            Log($"fallback-open-failed session={session.Id} error={exception.GetType().Name}:{exception.Message}");
            ClearSession(session.Id, restoreGame: true, "opening-failed");
            return false;
        }
    }

    public async Task<ExternalGameQuickAccessState> CloseCurrentGameAsync(CancellationToken cancellationToken)
    {
        ActiveSession? session;
        lock (_gate)
        {
            session = _activeSession;
            if (session is not null)
            {
                _activeSession = session with { SuppressGameRestore = true };
                session = _activeSession;
            }
        }

        if (session is null)
        {
            return GetState();
        }

        Log($"close-current-game requested session={session.Id} pid={session.ProcessId} title={session.GameTitle}");
        var gameClosed = false;
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (!process.HasExited)
            {
                var closeRequested = process.CloseMainWindow();
                if (closeRequested)
                {
                    try
                    {
                        await process.WaitForExitAsync(cancellationToken)
                            .WaitAsync(GracefulGameCloseTimeout, cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                    }
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }

            gameClosed = true;
            Log($"close-current-game completed session={session.Id} pid={session.ProcessId}");
        }
        catch (ArgumentException)
        {
            gameClosed = true;
            Log($"close-current-game already-exited session={session.Id} pid={session.ProcessId}");
        }
        catch
        {
            lock (_gate)
            {
                if (_activeSession?.Id == session.Id)
                {
                    _activeSession = session with { SuppressGameRestore = false };
                }
            }

            PublishState();
            throw;
        }

        if (gameClosed)
        {
            ClearSession(session.Id, restoreGame: false, "game-closed");
        }

        return GetState();
    }

    public ExternalGameQuickAccessState ReturnToGame()
    {
        ActiveSession? session;
        lock (_gate)
        {
            session = _activeSession;
        }

        if (session is not null)
        {
            ClearSession(session.Id, restoreGame: true, "quick-access-surface-hidden");
        }

        return GetState();
    }

    private async Task MonitorQuickAccessAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var openDeadline = DateTimeOffset.UtcNow + QuickAccessOpenTimeout;
        var observation = new ExternalGameQuickAccessObservation();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var session = GetSession(sessionId);
                if (session is null)
                {
                    return;
                }

                if (!IsProcessRunning(session.ProcessId))
                {
                    ClearSession(sessionId, restoreGame: false, "game-exited");
                    return;
                }

                var visible = await _devToolsClient.TryGetQuickAccessMenuVisibilityAsync(cancellationToken);
                var returnedToGame = observation.Observe(
                    visible,
                    _processWindowService.IsForegroundWindow(session.GameWindowHandle));
                if (observation.QuickAccessReady && !session.QuickAccessReady)
                {
                    MarkQuickAccessReady(sessionId);
                }

                if (observation.QuickAccessReady && visible == false)
                {
                    ClearSession(sessionId, restoreGame: true, "quick-access-closed");
                    return;
                }
                else if (!observation.QuickAccessReady && DateTimeOffset.UtcNow >= openDeadline)
                {
                    ClearSession(sessionId, restoreGame: true, "quick-access-not-observed");
                    return;
                }

                if (returnedToGame)
                {
                    ClearSession(sessionId, restoreGame: false, "game-already-foreground");
                    return;
                }

                await Task.Delay(VisibilityPollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearSession(sessionId, restoreGame: false, "host-stopping");
        }
        catch (Exception exception)
        {
            Log($"fallback-monitor-failed session={sessionId} error={exception.GetType().Name}:{exception.Message}");
            ClearSession(sessionId, restoreGame: true, "monitor-failed");
        }
    }

    private ActiveSession? GetSession(Guid sessionId)
    {
        lock (_gate)
        {
            return _activeSession?.Id == sessionId ? _activeSession : null;
        }
    }

    private void MarkQuickAccessReady(Guid sessionId)
    {
        lock (_gate)
        {
            if (_activeSession?.Id != sessionId || _activeSession.QuickAccessReady)
            {
                return;
            }

            _activeSession = _activeSession with { QuickAccessReady = true };
        }

        Log($"fallback-ready session={sessionId}");
        PublishState();
    }

    private void ClearSession(Guid sessionId, bool restoreGame, string reason)
    {
        ActiveSession? session;
        lock (_gate)
        {
            if (_activeSession?.Id != sessionId)
            {
                return;
            }

            session = _activeSession!;
            _activeSession = null;
        }

        PublishState();
        var restored = restoreGame &&
            !session.SuppressGameRestore &&
            IsProcessRunning(session.ProcessId) &&
            _steamWindowFocusService.TryRestoreWindow(session.GameWindowHandle);
        Log(
            $"fallback-closed session={session.Id} reason={reason} restoreRequested={restoreGame} " +
            $"restoreSuppressed={session.SuppressGameRestore} restored={restored}");
    }

    private void PublishState()
    {
        _liveUpdateHub.Publish("external-game-quick-access.state", GetState());
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static ExternalGameQuickAccessState BuildState(ActiveSession? session)
    {
        return session is null
            ? new ExternalGameQuickAccessState(
                Active: false,
                QuickAccessReady: false,
                GameTitle: string.Empty,
                StoreId: string.Empty,
                ProcessId: 0,
                CanCloseCurrentGame: false,
                StatusText: string.Empty)
            : new ExternalGameQuickAccessState(
                Active: true,
                QuickAccessReady: session.QuickAccessReady,
                GameTitle: session.GameTitle,
                StoreId: session.StoreId,
                ProcessId: session.ProcessId,
                CanCloseCurrentGame: true,
                StatusText: session.QuickAccessReady
                    ? $"Quick Access is open for {session.GameTitle} without the in-game Steam overlay."
                    : $"Opening Quick Access for {session.GameTitle}...");
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(
                _logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private sealed record ActiveSession(
        Guid Id,
        string GameTitle,
        string StoreId,
        int ProcessId,
        string GameWindowHandle,
        bool SuppressGameRestore,
        bool QuickAccessReady);
}

internal sealed class ExternalGameQuickAccessObservation
{
    public bool GameLostForeground { get; private set; }

    public bool QuickAccessReady { get; private set; }

    public bool Observe(bool? quickAccessVisible, bool gameIsForeground)
    {
        if (!gameIsForeground)
        {
            GameLostForeground = true;
        }

        // Visibility alone is not enough during the first invocation: Steam can
        // report the panel before Windows has completed the foreground hand-off.
        if (quickAccessVisible == true && GameLostForeground)
        {
            QuickAccessReady = true;
        }

        return QuickAccessReady && gameIsForeground;
    }
}
