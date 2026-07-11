using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Services;

public sealed class SteamClientLaunchService
{
    private const string BigPictureProtocolUri = "steam://open/bigpicture";
    private const string SteamStartupMutexName = @"Local\ToolsForSteam.SteamStartup";
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(20);

    // How long Steam may take to expose its DevTools endpoint before we treat it
    // as stuck and restart it. Handhelds (e.g. MSI Claw / Ryzen Z2 Extreme)
    // cold-start Big Picture far slower than a desktop, so we wait much longer
    // there - otherwise a slow-but-fine startup is mistaken for a hang and gets
    // restarted repeatedly, which is exactly what makes it take minutes.
    private static readonly TimeSpan DesktopStartupGracePeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandheldStartupGracePeriod = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InvisibleUiGracePeriod = TimeSpan.FromSeconds(45);

    // How often we try to auto-recover a Steam client that starts but never
    // finishes building its UI (the "stuck at the splash screen" hang). The
    // first attempt is gentle; later attempts escalate to a force-kill plus a
    // web-cache wipe. After this many attempts we stop and surface a message so
    // we never hammer the machine in an endless kill/relaunch loop.
    private const int MaxRecoveryAttempts = 3;

    public static SteamClientLaunchState PrepareConsoleStartup(
        SteamInstallationService steamInstallationService)
    {
        var steamExePath = steamInstallationService.ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            SteamStartupDiagnostics.Write("console bootstrap failed: steam.exe was not found");
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        using var startupMutex = new Mutex(false, SteamStartupMutexName);
        var ownsMutex = false;
        try
        {
            ownsMutex = startupMutex.WaitOne(TimeSpan.FromSeconds(15));
            if (!ownsMutex)
            {
                SteamStartupDiagnostics.Write("console bootstrap skipped: another startup owner holds the mutex");
                return new SteamClientLaunchState(false, "Another Tools for Steam startup owner is preparing Steam.");
            }

            return PrepareOwnedConsoleStartup(steamExePath);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
            SteamStartupDiagnostics.Write("console bootstrap recovered an abandoned startup mutex");
            return PrepareOwnedConsoleStartup(steamExePath);
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"console bootstrap failed: {exception}");
            return new SteamClientLaunchState(false, $"Steam could not be prepared: {exception.Message}");
        }
        finally
        {
            if (ownsMutex)
            {
                startupMutex.ReleaseMutex();
            }
        }
    }

    private static SteamClientLaunchState PrepareOwnedConsoleStartup(string steamExePath)
    {
        var steamRunning = IsSteamRunning();
        var commandLine = TryGetSteamMainProcessCommandLine();
        SteamStartupDiagnostics.Write(
            $"console bootstrap inspect running={steamRunning} commandLine={commandLine ?? "<missing>"}");

        if (!steamRunning)
        {
            LaunchSteam(steamExePath);
            SteamStartupDiagnostics.Write("console bootstrap launched one clean Steam process");
            return new SteamClientLaunchState(false, "Starting Steam in Gamepad UI with DevTools enabled.");
        }

        if (HasRequiredConsoleLaunchArguments(commandLine))
        {
            SteamStartupDiagnostics.Write("console bootstrap kept the existing compatible Steam startup");
            return new SteamClientLaunchState(false, "Steam is already starting with the required TFS arguments.");
        }

        SteamStartupDiagnostics.Write("console bootstrap found an incompatible or orphaned Steam startup; restarting once");
        if (!RestartSteamForConsoleBootstrap(steamExePath))
        {
            SteamStartupDiagnostics.Write("console bootstrap could not stop the conflicting Steam process tree");
            return new SteamClientLaunchState(false, "The conflicting Steam startup could not be stopped safely.");
        }

        SteamStartupDiagnostics.Write("console bootstrap replaced the conflicting startup with one clean Steam process");
        return new SteamClientLaunchState(false, "Restarted Steam once with the required Gamepad UI and DevTools arguments.");
    }

    internal static bool HasRequiredConsoleLaunchArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return commandLine.Contains("-gamepadui", StringComparison.OrdinalIgnoreCase) &&
            commandLine.Contains("-dev", StringComparison.OrdinalIgnoreCase) &&
            commandLine.Contains("-cef-enable-debugging", StringComparison.OrdinalIgnoreCase);
    }

    private readonly HttpClient _httpClient;
    private readonly Uri _debugEndpoint;
    private readonly SteamInstallationService _steamInstallationService;
    private readonly TimeSpan _startupGracePeriod;
    private readonly object _recoverySync = new();

    private DateTimeOffset? _firstUnavailableAt;
    private DateTimeOffset? _firstInvisibleUiAt;
    private DateTimeOffset _lastActionAt = DateTimeOffset.MinValue;
    private int _recoveryAttempts;
    private bool _steamUiWasVisible;

    public SteamClientLaunchService(
        HttpClient httpClient,
        Uri debugEndpoint,
        SteamInstallationService steamInstallationService,
        bool isHandheld = false)
    {
        _httpClient = httpClient;
        _debugEndpoint = debugEndpoint;
        _steamInstallationService = steamInstallationService;
        _startupGracePeriod = isHandheld
            ? HandheldStartupGracePeriod
            : DesktopStartupGracePeriod;
    }

    public static SteamClientLaunchState RequestSteamStartForTools(SteamInstallationService steamInstallationService)
    {
        var steamExePath = steamInstallationService.ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        try
        {
            if (IsSteamRunning())
            {
                RequestSteamGamepadUi();
                return new SteamClientLaunchState(
                    false,
                    "Steam is already running. Requesting the Gamepad UI and waiting for the DevTools endpoint.");
            }

            LaunchSteam(steamExePath);
            return new SteamClientLaunchState(
                false,
                "Starting Steam in Gamepad UI with DevTools enabled.");
        }
        catch (Exception exception)
        {
            return new SteamClientLaunchState(
                false,
                $"Steam could not be started: {exception.Message}");
        }
    }

    public static SteamClientLaunchState RequestSteamAttention(SteamInstallationService steamInstallationService)
    {
        var steamExePath = steamInstallationService.ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        try
        {
            if (!IsSteamRunning())
            {
                LaunchSteam(steamExePath);
                return new SteamClientLaunchState(
                    false,
                    "Starting Steam in Gamepad UI with DevTools enabled.");
            }

            RequestSteamGamepadUi();
            return new SteamClientLaunchState(
                false,
                "Requesting the Steam Gamepad UI.");
        }
        catch (Exception exception)
        {
            return new SteamClientLaunchState(
                false,
                $"Steam could not be activated: {exception.Message}");
        }
    }

    public async Task<SteamClientLaunchState> EnsureDevToolsReadyAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var launchAttemptInFlight = _firstUnavailableAt.HasValue;
        if (await IsDebugEndpointAvailableAsync(cancellationToken))
        {
            if (IsSteamUiVisible())
            {
                _steamUiWasVisible = true;
                _firstInvisibleUiAt = null;
                _firstUnavailableAt = null;
                _recoveryAttempts = 0;
                return new SteamClientLaunchState(true, "Steam DevTools endpoint and UI are ready.");
            }

            if (_steamUiWasVisible)
            {
                // Steam may hide its main window while a game is active. Once
                // the UI was confirmed during this session, DevTools alone is
                // enough and we must never restart Steam underneath a game.
                _firstUnavailableAt = null;
                return new SteamClientLaunchState(true, "Steam DevTools endpoint is ready.");
            }

            _firstUnavailableAt = null;
            _firstInvisibleUiAt ??= now;
            if (launchAttemptInFlight || now - _lastActionAt >= ActionCooldown)
            {
                RequestSteamGamepadUi();
                _lastActionAt = now;
                SteamStartupDiagnostics.Write("Steam DevTools is ready but no UI is visible; requested Gamepad UI attention");
            }

            if (now - _firstInvisibleUiAt.Value < InvisibleUiGracePeriod)
            {
                return new SteamClientLaunchState(
                    true,
                    "Steam is running, but its UI is still opening. Requesting Gamepad UI attention.");
            }

            if (_recoveryAttempts >= MaxRecoveryAttempts)
            {
                return new SteamClientLaunchState(
                    false,
                    "Steam DevTools responds, but the Steam UI remains invisible after several hard restarts.");
            }

            _recoveryAttempts++;
            var repairSteamExePath = ResolveSteamExecutablePath();
            if (repairSteamExePath is null)
            {
                return new SteamClientLaunchState(
                    false,
                    "Steam is running without a visible UI, but steam.exe could not be resolved for recovery.");
            }

            var uiRecovered = RecoverHungSteam(
                repairSteamExePath,
                forceKill: true,
                clearWebCache: _recoveryAttempts > 1,
                disableCefGpu: _recoveryAttempts >= MaxRecoveryAttempts);
            _lastActionAt = now;
            _firstUnavailableAt = now;
            _firstInvisibleUiAt = null;
            _steamUiWasVisible = false;
            SteamStartupDiagnostics.Write(
                $"Steam UI stayed invisible although DevTools responded; hard recovery attempt={_recoveryAttempts} result={uiRecovered}");
            return new SteamClientLaunchState(
                false,
                uiRecovered
                    ? "Steam was running without a visible UI. Its process tree was stopped and restarted cleanly."
                    : "Steam UI is invisible and its stale process tree could not be stopped yet.");
        }

        _firstInvisibleUiAt = null;
        _firstUnavailableAt ??= now;

        if (now - _lastActionAt < ActionCooldown)
        {
            return new SteamClientLaunchState(
                false,
                "Steam is starting with the DevTools endpoint enabled.");
        }

        var steamExePath = ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        if (!IsSteamRunning())
        {
            // Steam is not running at all (fresh boot or the user closed it).
            // Start a clean session and reset the recovery budget.
            LaunchSteam(steamExePath);
            _lastActionAt = now;
            _firstUnavailableAt = now;
            _steamUiWasVisible = false;
            _recoveryAttempts = 0;
            return new SteamClientLaunchState(
                false,
                "Starting Steam in Gamepad UI with DevTools enabled.");
        }

        // Steam is running but the DevTools endpoint never came up. Give it a
        // short grace window in case it is simply slow to finish loading.
        if (now - _firstUnavailableAt.Value < _startupGracePeriod)
        {
            return new SteamClientLaunchState(
                false,
                "Steam is running without the DevTools endpoint. Preparing a controlled restart.");
        }

        if (_recoveryAttempts >= MaxRecoveryAttempts)
        {
            // We already force-restarted and cleared the cache several times and
            // Steam still will not finish loading. Stop retrying so we do not
            // loop forever, and tell the user what to try next.
            return new SteamClientLaunchState(
                false,
                "Steam keeps stopping at its splash screen after several restart attempts. " +
                "Try updating your graphics driver or verifying the Steam installation.");
        }

        _recoveryAttempts++;
        // Be gentle first; only the final attempt escalates to a force-kill, so a
        // slow-loading Steam is never hard-killed in the middle of starting up.
        var escalate = _recoveryAttempts >= MaxRecoveryAttempts;
        _lastActionAt = now;

        // First attempt: gentle, official restart (unchanged behaviour).
        // Later attempts: the UI is almost certainly wedged (hung steamwebhelper),
        // so force-kill the process tree, wipe the CEF web cache, and relaunch
        // with GPU acceleration disabled - the combination that reliably clears
        // the splash-screen hang.
        var recovered = RecoverHungSteam(
            steamExePath,
            forceKill: escalate,
            clearWebCache: escalate,
            disableCefGpu: escalate);

        if (!recovered)
        {
            return new SteamClientLaunchState(
                false,
                "Waiting for Steam to finish its official shutdown before restarting.");
        }

        return new SteamClientLaunchState(
            false,
            escalate
                ? $"Steam looked stuck at its splash screen. Force-restarting Steam and clearing its web cache (attempt {_recoveryAttempts})."
                : "Restarting Steam once so Tools for Steam can attach to the DevTools endpoint.");
    }

    public static string BuildSteamLaunchArguments(bool launchBigPicture, bool disableCefGpu = false)
    {
        var arguments = new List<string>();
        if (launchBigPicture)
        {
            arguments.Add("-gamepadui");
        }

        arguments.Add("-dev");
        arguments.Add("-cef-enable-debugging");

        if (disableCefGpu)
        {
            // Renders the CEF-based Steam UI without GPU acceleration. This is a
            // known workaround for the client hanging on a blank splash screen
            // when a GPU/driver problem stalls the browser layer.
            arguments.Add("-cef-disable-gpu");
        }

        return string.Join(' ', arguments);
    }

    public SteamClientLaunchState RestartSteamForSteamTools()
    {
        var steamExePath = ResolveSteamExecutablePath();
        if (steamExePath is null)
        {
            return new SteamClientLaunchState(
                false,
                "Tools for Steam could not find steam.exe. Install Steam or start it once manually.");
        }

        // User-initiated "Restart Steam" (Power plugin). People press this when
        // Steam is misbehaving, so make it a strong repair: try a graceful
        // shutdown first, force-kill if it will not exit, and wipe the web cache
        // before relaunching.
        var restartRequested = RecoverHungSteam(
            steamExePath,
            forceKill: true,
            clearWebCache: true,
            disableCefGpu: false);

        _lastActionAt = DateTimeOffset.UtcNow;
        _firstUnavailableAt = _lastActionAt;
        _firstInvisibleUiAt = null;
        _steamUiWasVisible = false;
        _recoveryAttempts = restartRequested ? 1 : 0;

        return new SteamClientLaunchState(
            false,
            restartRequested
                ? "Steam is restarting in Gamepad UI with a freshly cleared web cache."
                : "Waiting for Steam to finish its official shutdown before restarting.");
    }

    private async Task<bool> IsDebugEndpointAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_debugEndpoint, "/json/list"));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private bool RecoverHungSteam(
        string steamExePath,
        bool forceKill,
        bool clearWebCache,
        bool disableCefGpu)
    {
        using var startupMutex = new Mutex(false, SteamStartupMutexName);
        var ownsStartupMutex = false;
        try
        {
            try
            {
                ownsStartupMutex = startupMutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                ownsStartupMutex = true;
            }

            if (!ownsStartupMutex)
            {
                SteamStartupDiagnostics.Write("Steam recovery skipped because another startup repair owns the mutex");
                return false;
            }

            // Serialize both threads and processes so the Xbox host watchdog,
            // the background injector and a manual restart cannot overlap.
            lock (_recoverySync)
            {
                if (!CloseSteam(forceIfUnresponsive: forceKill))
                {
                    return false;
                }

                if (clearWebCache)
                {
                    ClearSteamWebCache();
                }

                LaunchSteam(steamExePath, disableCefGpu);
                return true;
            }
        }
        finally
        {
            if (ownsStartupMutex)
            {
                startupMutex.ReleaseMutex();
            }
        }
    }

    private static void LaunchSteam(string steamExePath, bool disableCefGpu = false)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = steamExePath,
            Arguments = BuildSteamLaunchArguments(launchBigPicture: true, disableCefGpu: disableCefGpu),
            WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    private static bool RestartSteamForConsoleBootstrap(string steamExePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExePath,
                Arguments = "-shutdown",
                WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"official Steam shutdown request failed: {exception.Message}");
        }

        if (!WaitForSteamToExit(TimeSpan.FromSeconds(20)))
        {
            SteamStartupDiagnostics.Write("official Steam shutdown timed out; terminating the stale boot process tree");
            ForceKillSteamProcesses();
            if (!WaitForSteamToExit(ForceKillTimeout))
            {
                return false;
            }
        }

        LaunchSteam(steamExePath);
        return true;
    }

    private static string? TryGetSteamMainProcessCommandLine()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'steam.exe'");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                var commandLine = Convert.ToString(item["CommandLine"])?.Trim();
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    return commandLine;
                }
            }
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"Steam command-line inspection failed: {exception.Message}");
        }

        return null;
    }

    private bool CloseSteam(bool forceIfUnresponsive)
    {
        var steamProcesses = GetSteamProcesses().ToList();
        if (steamProcesses.Count == 0)
        {
            return true;
        }

        foreach (var process in steamProcesses)
        {
            try
            {
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
            }
        }

        var steamExePath = ResolveSteamExecutablePath();
        if (steamExePath is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = steamExePath,
                    Arguments = "-shutdown",
                    WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.Dispose();
            }
            catch
            {
            }
        }

        if (WaitForSteamToExit(ShutdownTimeout))
        {
            return true;
        }

        if (!forceIfUnresponsive)
        {
            return !IsSteamRunning();
        }

        // The official "-shutdown" did not take within the timeout - the client
        // is wedged (typically a hung steamwebhelper on the splash screen).
        // Terminate the whole Steam process tree so we can relaunch cleanly.
        ForceKillSteamProcesses();
        return WaitForSteamToExit(ForceKillTimeout);
    }

    private static bool WaitForSteamToExit(TimeSpan timeout)
    {
        var timeoutAt = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (!IsSteamRunning())
            {
                return true;
            }

            Thread.Sleep(300);
        }

        return !IsSteamRunning();
    }

    private static void ForceKillSteamProcesses()
    {
        foreach (var process in GetSteamProcesses())
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    private void ClearSteamWebCache()
    {
        foreach (var cacheDirectory in EnumerateWebCacheDirectories())
        {
            try
            {
                if (Directory.Exists(cacheDirectory))
                {
                    Directory.Delete(cacheDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort: a locked file just means a slightly staler cache,
                // which is still fine for the next launch.
            }
        }
    }

    private IEnumerable<string> EnumerateWebCacheDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            // Current Steam clients keep the CEF/browser cache here.
            yield return Path.Combine(localAppData, "Steam", "htmlcache");
        }

        var steamRoot = _steamInstallationService.ResolveSteamRootPath();
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            // Older layouts (and some installs) also keep one under the install dir.
            yield return Path.Combine(steamRoot, "config", "htmlcache");
        }
    }

    private string? ResolveSteamExecutablePath()
    {
        return _steamInstallationService.ResolveSteamExecutablePath();
    }

    private static bool IsSteamRunning()
    {
        return GetSteamProcesses().Any();
    }

    private static IEnumerable<Process> GetSteamProcesses()
    {
        return Process.GetProcesses()
            .Where(process =>
            {
                try
                {
                    return !process.HasExited &&
                        (string.Equals(process.ProcessName, "steam", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(process.ProcessName, "steamwebhelper", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(process.ProcessName, "GameOverlayUI", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(process.ProcessName, "steamerrorreporter", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            });
    }

    private static void RequestSteamGamepadUi()
    {
        if (IsSteamUiVisible())
        {
            return;
        }

        if (TryFocusSteamWindow())
        {
            return;
        }

        TryOpenBigPictureProtocol();
    }

    private static bool IsBigPictureWindowVisible()
    {
        var processes = Process.GetProcessesByName("steamwebhelper");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited &&
                        process.MainWindowHandle != IntPtr.Zero &&
                        IsBigPictureWindowTitle(process.MainWindowTitle))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsSteamUiVisible() => FindSteamWindowHandle() != IntPtr.Zero;

    private static bool IsBigPictureWindowTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return title.Replace('-', ' ').Contains("Big Picture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFocusSteamWindow()
    {
        var handle = FindSteamWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(handle, ShowWindowRestore);
        SetForegroundWindow(handle);
        return true;
    }

    private static IntPtr FindSteamWindowHandle()
    {
        var preferredHandle = FindSteamWindowHandle("steamwebhelper", preferSteamTitle: true);
        if (preferredHandle != IntPtr.Zero)
        {
            return preferredHandle;
        }

        var steamHandle = FindSteamWindowHandle("steam", preferSteamTitle: false);
        if (steamHandle != IntPtr.Zero)
        {
            return steamHandle;
        }

        return FindSteamWindowHandle("steamwebhelper", preferSteamTitle: false);
    }

    private static IntPtr FindSteamWindowHandle(string processName, bool preferSteamTitle)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            var fallbackHandle = IntPtr.Zero;
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    fallbackHandle = process.MainWindowHandle;
                    if (!preferSteamTitle ||
                        process.MainWindowTitle.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
                        IsBigPictureWindowTitle(process.MainWindowTitle))
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch
                {
                }
            }

            return fallbackHandle;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void TryOpenBigPictureProtocol()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BigPictureProtocolUri,
                UseShellExecute = true
            })?.Dispose();
        }
        catch
        {
        }
    }

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

public sealed record SteamClientLaunchState(
    bool DevToolsReady,
    string Message);
