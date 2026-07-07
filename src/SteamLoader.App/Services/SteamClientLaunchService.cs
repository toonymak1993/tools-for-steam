using System.Diagnostics;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Services;

public sealed class SteamClientLaunchService
{
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

    // How often we try to auto-recover a Steam client that starts but never
    // finishes building its UI (the "stuck at the splash screen" hang). The
    // first attempt is gentle; later attempts escalate to a force-kill plus a
    // web-cache wipe. After this many attempts we stop and surface a message so
    // we never hammer the machine in an endless kill/relaunch loop.
    private const int MaxRecoveryAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly Uri _debugEndpoint;
    private readonly SteamInstallationService _steamInstallationService;
    private readonly TimeSpan _startupGracePeriod;
    private readonly object _recoverySync = new();

    private DateTimeOffset? _firstUnavailableAt;
    private DateTimeOffset _lastActionAt = DateTimeOffset.MinValue;
    private int _recoveryAttempts;

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

    public async Task<SteamClientLaunchState> EnsureDevToolsReadyAsync(CancellationToken cancellationToken)
    {
        if (await IsDebugEndpointAvailableAsync(cancellationToken))
        {
            // Steam finished loading and the DevTools endpoint answered: the
            // client is healthy, so clear the hang bookkeeping.
            _firstUnavailableAt = null;
            _recoveryAttempts = 0;
            return new SteamClientLaunchState(true, "Steam DevTools endpoint is ready.");
        }

        var now = DateTimeOffset.UtcNow;
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
        // Serialize recovery so a background watchdog pass and a manual restart
        // can never run overlapping kill/relaunch sequences at the same time.
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
                         string.Equals(process.ProcessName, "steamwebhelper", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            });
    }
}

public sealed record SteamClientLaunchState(
    bool DevToolsReady,
    string Message);
