using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Services;

namespace SteamLoader.App.Infrastructure.Curtain;

/// <summary>
/// TFS Curtain: covers the way from "Play" to the running game.
///
/// Modern mode (default, Big Picture): the curtain is drawn inside Steam's UI
/// by the frontend (curtain-core.js). This service keeps the Big Picture window
/// in the topmost band, hides store-client launcher windows, keeps the game's
/// own windows out of the topmost band so they can build up invisibly behind
/// Steam, detects when the game surface is ready, lets the frontend fade to
/// black, hands focus to the game and releases Steam. Classic mode shows a
/// native topmost overlay instead (desktop Steam, or by choice).
///
/// Everything is fail-open: an unconfirmed launch, a timeout, a manual close,
/// a launcher that waits for input or a TFS restart always release Steam and
/// restore hidden windows.
/// </summary>
public sealed class CurtainService : IAsyncDisposable
{
    internal static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan UnconfirmedRelease = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan HardCap = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan FadeToBlackDuration = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan ExitTrackingCap = TimeSpan.FromHours(24);

    private const string DefaultStatusText = "Preparing game";
    private const long LogLimitBytes = 512 * 1024;

    private readonly CurtainSettingsStore _settingsStore;
    private readonly SteamInstallationService _steamInstallationService;
    private readonly Func<bool> _isPluginEnabled;
    private readonly Func<IntPtr, bool> _focusWindow;
    private readonly string _stateDirectory;
    private readonly object _gate = new();
    private CurtainSession? _session;
    private CancellationTokenSource? _exitTracking;
    private int _sessionCounter;
    private int _lastSessionId;
    private string _lastEndReason = string.Empty;
    private long _revision;

    public CurtainService(
        CurtainSettingsStore settingsStore,
        SteamInstallationService steamInstallationService,
        Func<bool> isPluginEnabled,
        Func<IntPtr, bool>? focusWindow = null)
    {
        _settingsStore = settingsStore;
        _steamInstallationService = steamInstallationService;
        _isPluginEnabled = isPluginEnabled;
        _focusWindow = focusWindow ?? (_ => false);
        _stateDirectory = Path.GetDirectoryName(settingsStore.SettingsPath) ?? AppContext.BaseDirectory;
        RecoverFromPreviousRun();
    }

    private string PinMarkerPath => Path.Combine(_stateDirectory, "curtain-runtime.json");

    private string LogPath => Path.Combine(_stateDirectory, "curtain.log");

    // ---------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------

    public CurtainSnapshot GetSnapshot()
    {
        var configuration = _settingsStore.Load();
        lock (_gate)
        {
            return BuildSnapshotLocked(configuration);
        }
    }

    public CurtainSnapshot SaveGlobal(CurtainGlobalUpdate update)
    {
        var configuration = _settingsStore.Load();
        if (update.Mode is not null)
        {
            configuration.Mode = CurtainModes.NormalizeGlobal(update.Mode);
        }

        configuration.TimeoutSeconds = update.TimeoutSeconds ?? configuration.TimeoutSeconds;
        configuration.ExitDelaySeconds = update.ExitDelaySeconds ?? configuration.ExitDelaySeconds;
        configuration.HideLaunchers = update.HideLaunchers ?? configuration.HideLaunchers;
        configuration.ReturnToSteam = update.ReturnToSteam ?? configuration.ReturnToSteam;
        configuration.ShowStatus = update.ShowStatus ?? configuration.ShowStatus;
        configuration.StatusText = update.StatusText ?? configuration.StatusText;
        configuration.FallbackLogoPath = update.FallbackLogoPath ?? configuration.FallbackLogoPath;
        _settingsStore.Save(configuration);
        Interlocked.Increment(ref _revision);
        return GetSnapshot();
    }

    public CurtainSnapshot SetGameEnabled(uint appId, bool enabled)
    {
        if (appId == 0)
        {
            throw new InvalidOperationException("A Steam app id is required.");
        }

        var configuration = _settingsStore.Load();
        configuration.DisabledAppIds.RemoveAll(candidate => candidate == appId);
        if (!enabled)
        {
            configuration.DisabledAppIds.Add(appId);
        }

        _settingsStore.Save(configuration);
        Interlocked.Increment(ref _revision);
        return GetSnapshot();
    }

    public CurtainGameDetails GetGame(uint appId)
    {
        if (appId == 0)
        {
            throw new InvalidOperationException("A Steam app id is required.");
        }

        var configuration = _settingsStore.Load();
        var steamRoot = ResolveSteamRoot();
        return new CurtainGameDetails(
            Resolve(configuration, appId, steamRoot),
            configuration.FindGame(appId) ?? new CurtainGameSettings(),
            CurtainArtworkResolver.FindCandidates(steamRoot, appId)
                .Select(candidate => new CurtainCandidateView(
                    candidate.Id,
                    candidate.Kind,
                    candidate.Label,
                    $"/api/curtain/image?appId={appId}&kind={candidate.Kind}&source={candidate.Id}&v={ImageVersion(candidate.Path)}"))
                .ToArray());
    }

    public CurtainGameDetails SaveGame(uint appId, CurtainGameSettings settings, bool? enabled)
    {
        if (appId == 0)
        {
            throw new InvalidOperationException("A Steam app id is required.");
        }

        var configuration = _settingsStore.Load();
        CurtainSettingsStore.NormalizeGame(settings);
        if (settings.IsEmpty)
        {
            configuration.Games.Remove(appId.ToString());
        }
        else
        {
            configuration.Games[appId.ToString()] = settings;
        }

        if (enabled is not null)
        {
            configuration.DisabledAppIds.RemoveAll(candidate => candidate == appId);
            if (!enabled.Value)
            {
                configuration.DisabledAppIds.Add(appId);
            }
        }

        _settingsStore.Save(configuration);
        Interlocked.Increment(ref _revision);
        return GetGame(appId);
    }

    public CurtainGameDetails ResetGame(uint appId) => SaveGame(appId, new CurtainGameSettings(), enabled: true);

    /// <summary>
    /// The image file for the curtain or the editor preview. Only paths that
    /// the settings or Steam's artwork folders resolve to are ever returned.
    /// </summary>
    public string ResolveImagePath(uint appId, string kind, string? source)
    {
        var steamRoot = ResolveSteamRoot();
        if (string.Equals(kind, "fallback-logo", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = _settingsStore.Load().FallbackLogoPath;
            return CurtainArtworkResolver.IsUsableCustomImage(fallback) ? Path.GetFullPath(fallback) : string.Empty;
        }

        if (appId == 0)
        {
            return string.Empty;
        }

        var isLogo = string.Equals(kind, "logo", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source) && source is not CurtainImageSources.Auto and not CurtainImageSources.Custom)
        {
            var allowed = isLogo ? CurtainImageSources.LogoIds : CurtainImageSources.BackgroundIds;
            var normalized = CurtainImageSources.Normalize(source, allowed);
            return normalized is null || string.IsNullOrWhiteSpace(steamRoot)
                ? string.Empty
                : CurtainArtworkResolver.FindPath(steamRoot, appId, normalized) ?? string.Empty;
        }

        var resolved = Resolve(_settingsStore.Load(), appId, steamRoot);
        return isLogo ? resolved.LogoPath : resolved.BackgroundPath;
    }

    // ---------------------------------------------------------------------
    // Launch session
    // ---------------------------------------------------------------------

    public async Task<CurtainLaunchResult> ShowAsync(
        uint appId,
        string? title,
        string? reason,
        bool bigPicture,
        CancellationToken cancellationToken)
    {
        if (appId == 0)
        {
            return new CurtainLaunchResult(false, "A Steam app id is required.", GetSnapshot());
        }

        var configuration = _settingsStore.Load();
        if (!_isPluginEnabled())
        {
            return new CurtainLaunchResult(false, "TFS Curtain is disabled.", GetSnapshot());
        }

        var resolved = Resolve(configuration, appId, ResolveSteamRoot());
        if (!resolved.Enabled || resolved.Mode == CurtainModes.Off)
        {
            return new CurtainLaunchResult(false, "TFS Curtain is off for this game.", GetSnapshot());
        }

        var mode = resolved.Mode == CurtainModes.Modern && !bigPicture ? CurtainModes.Classic : resolved.Mode;
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Starting game" : title.Trim();
        CurtainSession? previous;
        CurtainSession session;
        lock (_gate)
        {
            previous = _session;
            _session = null;
            session = new CurtainSession(
                ++_sessionCounter,
                appId,
                normalizedTitle,
                mode,
                resolved,
                configuration.HideLaunchers,
                configuration.ReturnToSteam,
                reason ?? string.Empty);
            _exitTracking?.Cancel();
        }

        if (previous is not null)
        {
            await EndSessionAsync(previous, "superseded", IntPtr.Zero, handedOver: false);
        }

        // Baseline: everything running now is not the game.
        try
        {
            foreach (var window in CurtainWindowSystem.EnumerateWindows(400))
            {
                session.BaselineWindows.Add(window.Handle);
            }

            // Steam may already have spawned the game while this request was on
            // its way; anything younger than a few seconds is not baseline.
            var recentCutoff = DateTime.Now.AddSeconds(-3);
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    bool isRecent;
                    try
                    {
                        isRecent = process.StartTime >= recentCutoff;
                    }
                    catch
                    {
                        isRecent = false;
                    }

                    if (!isRecent)
                    {
                        session.BaselineProcessIds.Add((uint)process.Id);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            Log($"baseline failed: {exception.Message}");
        }

        if (mode == CurtainModes.Modern)
        {
            var steamWindow = SteamBigPictureForegroundDetector.Capture().PreferredWindowHandle;
            if (steamWindow != IntPtr.Zero && CurtainWindowSystem.SetTopmost(steamWindow, true))
            {
                session.PinnedSteamWindow = steamWindow;
            }
        }
        else
        {
            try
            {
                session.Overlay = await CurtainOverlayHost.ShowAsync(ToVisual(resolved, normalizedTitle));
            }
            catch (Exception exception)
            {
                Log($"classic overlay failed: {exception.Message}");
                return new CurtainLaunchResult(false, "The curtain window could not be shown.", GetSnapshot());
            }
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || _session is not null)
            {
                // A newer launch won the race; drop this one.
                _ = EndSessionAsync(session, "superseded", IntPtr.Zero, handedOver: false);
                return new CurtainLaunchResult(false, "The launch was superseded.", BuildSnapshotLocked(configuration));
            }

            _session = session;
            Interlocked.Increment(ref _revision);
        }

        WriteRuntimeMarker(session);
        Log($"start session={session.Id} app={appId} mode={mode} reason={reason} title={normalizedTitle}");
        _ = MonitorAsync(session);
        return new CurtainLaunchResult(true, "TFS Curtain is covering the launch.", GetSnapshot());
    }

    /// <summary>Steam reported the app as running (frontend app-lifetime event).</summary>
    public CurtainSnapshot Confirm(uint appId)
    {
        lock (_gate)
        {
            if (_session is { } session && !session.Confirmed && (appId == 0 || appId == session.AppId))
            {
                session.Confirmed = true;
                session.Phase = CurtainPhases.Launching;
                Interlocked.Increment(ref _revision);
                Log($"confirmed session={session.Id} by Steam");
            }
        }

        return GetSnapshot();
    }

    public async Task<CurtainSnapshot> DismissAsync(string? reason = null)
    {
        CurtainSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is not null)
        {
            await EndSessionAsync(session, string.IsNullOrWhiteSpace(reason) ? "dismissed" : reason!, IntPtr.Zero, handedOver: false);
        }

        return GetSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        CurtainSession? session;
        lock (_gate)
        {
            session = _session;
            _exitTracking?.Cancel();
        }

        if (session is not null)
        {
            await EndSessionAsync(session, "shutdown", IntPtr.Zero, handedOver: false);
        }
    }

    private async Task MonitorAsync(CurtainSession session)
    {
        var ownProcessId = (uint)Environment.ProcessId;
        try
        {
            while (!session.Cancellation.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var elapsed = now - session.StartedAt;
                var timeout = session.Resolved.TimeoutSeconds;
                if (elapsed >= HardCap)
                {
                    await EndSessionAsync(session, "hard-cap", IntPtr.Zero, handedOver: false);
                    return;
                }

                if (timeout > 0 && elapsed >= TimeSpan.FromSeconds(timeout))
                {
                    await EndSessionAsync(session, "timeout", IntPtr.Zero, handedOver: false);
                    return;
                }

                if (!session.Confirmed && elapsed >= UnconfirmedRelease)
                {
                    await EndSessionAsync(session, "unconfirmed", IntPtr.Zero, handedOver: false);
                    return;
                }

                var surfaces = new List<CurtainGameSurface>();
                foreach (var window in CurtainWindowSystem.EnumerateWindows())
                {
                    var process = session.Processes.Get(window.ProcessId);
                    var role = CurtainLaunchTracker.Classify(
                        window,
                        process,
                        session.BaselineProcessIds,
                        session.BaselineWindows,
                        ownProcessId);
                    if (role == CurtainWindowRole.Launcher)
                    {
                        if (session.Mode == CurtainModes.Modern &&
                            session.HideLaunchers &&
                            !session.HiddenLaunchers.ContainsKey(window.Handle) &&
                            CurtainWindowSystem.Hide(window.Handle))
                        {
                            session.HiddenLaunchers[window.Handle] = process?.Name ?? string.Empty;
                            WriteRuntimeMarker(session);
                            Log($"session={session.Id} hid launcher {process?.Name} '{window.Title}'");
                        }

                        continue;
                    }

                    if (role != CurtainWindowRole.Game || process is null)
                    {
                        continue;
                    }

                    surfaces.Add(new CurtainGameSurface(window, process.Name, process.StartedAt));
                    session.GameProcessIds.Add(window.ProcessId);
                    if (session.Mode == CurtainModes.Modern && window.IsTopmost)
                    {
                        // Game and splash windows like to claim the topmost band;
                        // keep them under the pinned Steam window.
                        CurtainWindowSystem.SetTopmost(window.Handle, false);
                    }
                }

                if (!session.Confirmed && surfaces.Count > 0)
                {
                    lock (_gate)
                    {
                        session.Confirmed = true;
                        session.Phase = CurtainPhases.Launching;
                        Interlocked.Increment(ref _revision);
                    }

                    Log($"confirmed session={session.Id} by game window {surfaces[0].ProcessName}");
                }

                if (session.PinnedSteamWindow != IntPtr.Zero && !CurtainWindowSystem.IsTopmost(session.PinnedSteamWindow))
                {
                    CurtainWindowSystem.SetTopmost(session.PinnedSteamWindow, true);
                }

                var ready = session.Tracker.Observe(surfaces, now, elapsed);
                if (session.Tracker.AttentionSurface is { } attention)
                {
                    Log($"session={session.Id} launcher waits for input; stepping aside");
                    await EndSessionAsync(session, "attention", attention.Handle, handedOver: false);
                    return;
                }

                if (ready is not null)
                {
                    await HandOverAsync(session, ready);
                    return;
                }

                await Task.Delay(MonitorInterval, session.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"monitor failed session={session.Id}: {exception}");
            await EndSessionAsync(session, "error", IntPtr.Zero, handedOver: false);
        }
    }

    private async Task HandOverAsync(CurtainSession session, CurtainReadySurface ready)
    {
        Log($"session={session.Id} game ready ({ready.Reason}) pid={ready.ProcessId}");
        SetPhase(session, CurtainPhases.Ready);
        var exitDelay = TimeSpan.FromSeconds(session.Resolved.ExitDelaySeconds);
        if (exitDelay > TimeSpan.Zero)
        {
            await Task.Delay(exitDelay, session.Cancellation.Token);
        }

        SetPhase(session, CurtainPhases.Handoff);
        if (session.Overlay is not null)
        {
            await session.Overlay.FadeToBlackAsync(FadeToBlackDuration);
        }
        else
        {
            // The frontend fades its curtain to black when it sees "handoff".
            await Task.Delay(FadeToBlackDuration + TimeSpan.FromMilliseconds(120), session.Cancellation.Token);
        }

        await EndSessionAsync(session, "ready", ready.Handle, handedOver: true);
    }

    private void SetPhase(CurtainSession session, string phase)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                session.Phase = phase;
                Interlocked.Increment(ref _revision);
            }
        }
    }

    private async Task EndSessionAsync(CurtainSession session, string reason, IntPtr focusTarget, bool handedOver)
    {
        lock (_gate)
        {
            if (session.Ended)
            {
                return;
            }

            session.Ended = true;
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            _lastSessionId = session.Id;
            _lastEndReason = reason;
            Interlocked.Increment(ref _revision);
        }

        try
        {
            session.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var focusAlive = focusTarget != IntPtr.Zero && CurtainWindowSystem.IsAlive(focusTarget);
        if (session.PinnedSteamWindow != IntPtr.Zero)
        {
            // Leaving the topmost band puts Steam on top of the normal band, so
            // tuck it right behind the game (or waiting launcher) as well; then
            // the game stays visible even if Windows refuses the focus change.
            CurtainWindowSystem.SetTopmost(session.PinnedSteamWindow, false);
            if (focusAlive)
            {
                CurtainWindowSystem.PlaceBehind(session.PinnedSteamWindow, focusTarget);
            }
        }

        if (focusAlive)
        {
            try
            {
                _focusWindow(focusTarget);
            }
            catch (Exception exception)
            {
                Log($"focus failed session={session.Id}: {exception.Message}");
            }
        }

        if (session.Overlay is not null)
        {
            await session.Overlay.CloseAsync(animate: !handedOver);
        }

        var hiddenLaunchers = session.HiddenLaunchers.Keys.ToArray();
        if (handedOver)
        {
            // Store clients come back once the game has ended, so they cannot
            // pop up over the game now.
            StartExitTracking(session, hiddenLaunchers);
        }
        else
        {
            RestoreWindows(hiddenLaunchers);
        }

        WriteRuntimeMarker(null, handedOver ? hiddenLaunchers : []);
        Log($"end session={session.Id} reason={reason} handedOver={handedOver}");
    }

    private void StartExitTracking(CurtainSession session, IReadOnlyList<IntPtr> hiddenLaunchers)
    {
        CancellationTokenSource tracking;
        lock (_gate)
        {
            _exitTracking?.Cancel();
            _exitTracking = tracking = new CancellationTokenSource(ExitTrackingCap);
        }

        var processIds = session.GameProcessIds.ToArray();
        var returnToSteam = session.ReturnToSteam;
        _ = Task.Run(async () =>
        {
            var processes = new List<Process>();
            try
            {
                foreach (var processId in processIds)
                {
                    try
                    {
                        var process = Process.GetProcessById((int)processId);
                        if (!process.HasExited)
                        {
                            processes.Add(process);
                        }
                        else
                        {
                            process.Dispose();
                        }
                    }
                    catch
                    {
                        // Already gone.
                    }
                }

                if (processes.Count > 0)
                {
                    await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(tracking.Token)));
                }

                RestoreWindows(hiddenLaunchers);
                WriteRuntimeMarker(null, []);
                if (returnToSteam && processes.Count > 0)
                {
                    await Task.Delay(1500, tracking.Token);
                    ReturnToSteamIfIdle();
                }
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer launch (or the 24 h cap): restore anyway.
                RestoreWindows(hiddenLaunchers);
            }
            catch (Exception exception)
            {
                Log($"exit tracking failed: {exception.Message}");
                RestoreWindows(hiddenLaunchers);
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        });
    }

    private void ReturnToSteamIfIdle()
    {
        var foreground = CurtainWindowSystem.ForegroundWindow;
        var snapshot = SteamBigPictureForegroundDetector.Capture();
        if (snapshot.IsSteamForeground || snapshot.PreferredWindowHandle == IntPtr.Zero || !snapshot.HasLikelyGamepadWindow)
        {
            return;
        }

        // Only step in when nothing else took over (desktop, Explorer or no window).
        if (foreground != IntPtr.Zero)
        {
            var processId = CurtainWindowSystem.GetProcessId(foreground);
            var name = string.Empty;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                name = process.ProcessName;
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(name) &&
                !string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "SearchHost", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        try
        {
            _focusWindow(snapshot.PreferredWindowHandle);
            Log("game ended; returned focus to Big Picture");
        }
        catch (Exception exception)
        {
            Log($"return to Steam failed: {exception.Message}");
        }
    }

    private static void RestoreWindows(IEnumerable<IntPtr> windows)
    {
        foreach (var window in windows)
        {
            try
            {
                CurtainWindowSystem.ShowWithoutActivation(window);
            }
            catch
            {
            }
        }
    }

    // ---------------------------------------------------------------------
    // Crash safety
    // ---------------------------------------------------------------------

    private void WriteRuntimeMarker(CurtainSession? session, IReadOnlyList<IntPtr>? pendingRestore = null)
    {
        try
        {
            var marker = new CurtainRuntimeMarker(
                session?.PinnedSteamWindow.ToInt64() ?? 0,
                (session?.HiddenLaunchers.Keys ?? (IEnumerable<IntPtr>)(pendingRestore ?? []))
                    .Select(handle => handle.ToInt64())
                    .ToArray());
            if (marker.PinnedSteamWindow == 0 && marker.HiddenWindows.Length == 0)
            {
                if (File.Exists(PinMarkerPath))
                {
                    File.Delete(PinMarkerPath);
                }

                return;
            }

            Directory.CreateDirectory(_stateDirectory);
            File.WriteAllText(PinMarkerPath, JsonSerializer.Serialize(marker));
        }
        catch
        {
        }
    }

    /// <summary>
    /// If TFS ended while a curtain was up, Steam would stay "always on top"
    /// and launchers would stay hidden. Undo that on the next start.
    /// </summary>
    private void RecoverFromPreviousRun()
    {
        try
        {
            if (!File.Exists(PinMarkerPath))
            {
                return;
            }

            var marker = JsonSerializer.Deserialize<CurtainRuntimeMarker>(File.ReadAllText(PinMarkerPath));
            if (marker is not null)
            {
                if (marker.PinnedSteamWindow != 0)
                {
                    CurtainWindowSystem.SetTopmost(new IntPtr(marker.PinnedSteamWindow), false);
                }

                RestoreWindows(marker.HiddenWindows.Select(handle => new IntPtr(handle)));
                Log($"recovered previous run: unpinned={marker.PinnedSteamWindow != 0} restored={marker.HiddenWindows.Length}");
            }

            File.Delete(PinMarkerPath);
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------------
    // Resolution
    // ---------------------------------------------------------------------

    internal static CurtainResolvedGame Resolve(CurtainConfiguration configuration, uint appId, string? steamRoot)
    {
        var game = configuration.FindGame(appId) ?? new CurtainGameSettings();
        var backgroundSource = game.BackgroundSource ?? CurtainImageSources.Auto;
        var logoSource = game.LogoSource ?? CurtainImageSources.Auto;
        var backgroundPath = CurtainArtworkResolver.ResolveBackground(steamRoot, appId, backgroundSource, game.BackgroundPath);
        var logoPath = CurtainArtworkResolver.ResolveLogo(steamRoot, appId, logoSource, game.LogoPath);
        var logoKind = "logo";
        if (string.IsNullOrEmpty(logoPath) &&
            logoSource != CurtainImageSources.None &&
            CurtainArtworkResolver.IsUsableCustomImage(configuration.FallbackLogoPath))
        {
            logoPath = Path.GetFullPath(configuration.FallbackLogoPath);
            logoKind = "fallback-logo";
        }

        var statusText = !string.IsNullOrWhiteSpace(game.StatusText)
            ? game.StatusText!
            : !string.IsNullOrWhiteSpace(configuration.StatusText) ? configuration.StatusText : DefaultStatusText;
        return new CurtainResolvedGame(
            AppId: appId,
            Enabled: !configuration.DisabledAppIds.Contains(appId),
            Mode: game.Mode ?? configuration.Mode,
            ModeOverride: game.Mode,
            TimeoutSeconds: game.TimeoutSeconds ?? configuration.TimeoutSeconds,
            ExitDelaySeconds: game.ExitDelaySeconds ?? configuration.ExitDelaySeconds,
            ShowStatus: configuration.ShowStatus,
            StatusText: statusText,
            BackgroundSource: backgroundSource,
            BackgroundOpacity: game.BackgroundOpacity ?? 72,
            BackgroundScale: game.BackgroundScale ?? 100,
            BackgroundX: game.BackgroundX ?? 50,
            BackgroundY: game.BackgroundY ?? 50,
            BackgroundZoom: game.BackgroundZoom ?? true,
            LogoSource: logoSource,
            LogoX: game.LogoX ?? 50,
            LogoY: game.LogoY ?? 45,
            LogoWidth: game.LogoWidth ?? 38,
            LogoShadow: game.LogoShadow ?? true,
            LogoZoom: game.LogoZoom ?? false,
            BackgroundUrl: string.IsNullOrEmpty(backgroundPath)
                ? string.Empty
                : $"/api/curtain/image?appId={appId}&kind=background&v={ImageVersion(backgroundPath)}",
            LogoUrl: string.IsNullOrEmpty(logoPath)
                ? string.Empty
                : $"/api/curtain/image?appId={appId}&kind={logoKind}&v={ImageVersion(logoPath)}",
            HasOverrides: !game.IsEmpty)
        {
            BackgroundPath = backgroundPath,
            LogoPath = logoPath
        };
    }

    private static CurtainVisual ToVisual(CurtainResolvedGame game, string title) => new(
        title,
        game.BackgroundPath,
        game.BackgroundOpacity / 100d,
        game.BackgroundScale / 100d,
        game.BackgroundX / 100d,
        game.BackgroundY / 100d,
        game.BackgroundZoom,
        game.LogoPath,
        game.LogoX / 100d,
        game.LogoY / 100d,
        game.LogoWidth / 100d,
        game.LogoShadow,
        game.LogoZoom,
        game.ShowStatus,
        game.StatusText);

    private static string ImageVersion(string path)
    {
        try
        {
            var stamp = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stamp)))[..12].ToLowerInvariant();
        }
        catch
        {
            return "0";
        }
    }

    private string? ResolveSteamRoot()
    {
        try
        {
            return _steamInstallationService.ResolveSteamRootPath();
        }
        catch
        {
            return null;
        }
    }

    private CurtainSnapshot BuildSnapshotLocked(CurtainConfiguration configuration)
    {
        var session = _session;
        return new CurtainSnapshot(
            Enabled: _isPluginEnabled() && configuration.Mode != CurtainModes.Off,
            PluginEnabled: _isPluginEnabled(),
            Active: session is not null,
            ActiveAppId: session?.AppId ?? 0,
            ActiveTitle: session?.Title ?? string.Empty,
            DisabledAppIds: configuration.DisabledAppIds.ToArray(),
            CustomizedAppIds: configuration.Games.Keys.Select(uint.Parse).Order().ToArray(),
            MaximumDurationSeconds: configuration.TimeoutSeconds,
            Settings: new CurtainGlobalSettings(
                configuration.Mode,
                configuration.TimeoutSeconds,
                configuration.ExitDelaySeconds,
                configuration.HideLaunchers,
                configuration.ReturnToSteam,
                configuration.ShowStatus,
                configuration.StatusText,
                configuration.FallbackLogoPath,
                CurtainArtworkResolver.IsUsableCustomImage(configuration.FallbackLogoPath)),
            Session: session is null
                ? null
                : new CurtainSessionSnapshot(
                    session.Id,
                    session.AppId,
                    session.Title,
                    session.Mode,
                    session.Phase,
                    session.Confirmed,
                    session.StartedAt.ToUnixTimeMilliseconds(),
                    session.Phase == CurtainPhases.Handoff,
                    session.Resolved),
            LastSessionId: _lastSessionId,
            LastEndReason: _lastEndReason,
            Revision: Interlocked.Read(ref _revision));
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > LogLimitBytes)
            {
                File.Move(LogPath, LogPath + ".old", overwrite: true);
            }

            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private sealed class CurtainSession(
        int id,
        uint appId,
        string title,
        string mode,
        CurtainResolvedGame resolved,
        bool hideLaunchers,
        bool returnToSteam,
        string reason)
    {
        public int Id { get; } = id;
        public uint AppId { get; } = appId;
        public string Title { get; } = title;
        public string Mode { get; } = mode;
        public CurtainResolvedGame Resolved { get; } = resolved;
        public bool HideLaunchers { get; } = hideLaunchers;
        public bool ReturnToSteam { get; } = returnToSteam;
        public string Reason { get; } = reason;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        public HashSet<uint> BaselineProcessIds { get; } = [];
        public HashSet<IntPtr> BaselineWindows { get; } = [];
        public HashSet<uint> GameProcessIds { get; } = [];
        public Dictionary<IntPtr, string> HiddenLaunchers { get; } = [];
        public CurtainProcessCache Processes { get; } = new();
        public CurtainLaunchTracker Tracker { get; } = new(title);
        public IntPtr PinnedSteamWindow { get; set; }
        public CurtainOverlayHost? Overlay { get; set; }
        public bool Confirmed { get; set; }
        public bool Ended { get; set; }
        public string Phase { get; set; } = CurtainPhases.Covering;
    }

    private sealed record CurtainRuntimeMarker(long PinnedSteamWindow, long[] HiddenWindows);
}

public static class CurtainPhases
{
    /// <summary>Shown, Steam has not confirmed the start yet.</summary>
    public const string Covering = "covering";
    /// <summary>The game is starting (Steam or a game window confirmed it).</summary>
    public const string Launching = "launching";
    /// <summary>The game surface is ready; waiting for the exit delay.</summary>
    public const string Ready = "ready";
    /// <summary>Fading to black, then focus moves to the game.</summary>
    public const string Handoff = "handoff";
}

public sealed record CurtainSnapshot(
    bool Enabled,
    bool PluginEnabled,
    bool Active,
    uint ActiveAppId,
    string ActiveTitle,
    IReadOnlyList<uint> DisabledAppIds,
    IReadOnlyList<uint> CustomizedAppIds,
    int MaximumDurationSeconds,
    CurtainGlobalSettings Settings,
    CurtainSessionSnapshot? Session,
    int LastSessionId,
    string LastEndReason,
    long Revision);

public sealed record CurtainGlobalSettings(
    string Mode,
    int TimeoutSeconds,
    double ExitDelaySeconds,
    bool HideLaunchers,
    bool ReturnToSteam,
    bool ShowStatus,
    string StatusText,
    string FallbackLogoPath,
    bool FallbackLogoAvailable);

public sealed record CurtainGlobalUpdate(
    string? Mode,
    int? TimeoutSeconds,
    double? ExitDelaySeconds,
    bool? HideLaunchers,
    bool? ReturnToSteam,
    bool? ShowStatus,
    string? StatusText,
    string? FallbackLogoPath);

public sealed record CurtainSessionSnapshot(
    int Id,
    uint AppId,
    string Title,
    string Mode,
    string Phase,
    bool Confirmed,
    long StartedAt,
    bool FadeToBlack,
    CurtainResolvedGame Game);

public sealed record CurtainResolvedGame(
    uint AppId,
    bool Enabled,
    string Mode,
    string? ModeOverride,
    int TimeoutSeconds,
    double ExitDelaySeconds,
    bool ShowStatus,
    string StatusText,
    string BackgroundSource,
    int BackgroundOpacity,
    int BackgroundScale,
    int BackgroundX,
    int BackgroundY,
    bool BackgroundZoom,
    string LogoSource,
    int LogoX,
    int LogoY,
    int LogoWidth,
    bool LogoShadow,
    bool LogoZoom,
    string BackgroundUrl,
    string LogoUrl,
    bool HasOverrides)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string BackgroundPath { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string LogoPath { get; init; } = string.Empty;
}

public sealed record CurtainCandidateView(string Id, string Kind, string Label, string Url);

public sealed record CurtainGameDetails(
    CurtainResolvedGame Resolved,
    CurtainGameSettings Overrides,
    IReadOnlyList<CurtainCandidateView> Candidates);

public sealed record CurtainLaunchResult(
    bool Shown,
    string Message,
    CurtainSnapshot State);
