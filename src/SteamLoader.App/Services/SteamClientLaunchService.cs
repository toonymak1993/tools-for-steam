using System.Diagnostics;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Services;

public sealed class SteamClientLaunchService
{
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RestartGracePeriod = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(18);

    private readonly HttpClient _httpClient;
    private readonly Uri _debugEndpoint;
    private readonly SteamInstallationService _steamInstallationService;

    private DateTimeOffset? _firstUnavailableAt;
    private DateTimeOffset _lastActionAt = DateTimeOffset.MinValue;
    private bool _restartedExistingSteam;

    public SteamClientLaunchService(
        HttpClient httpClient,
        Uri debugEndpoint,
        SteamInstallationService steamInstallationService)
    {
        _httpClient = httpClient;
        _debugEndpoint = debugEndpoint;
        _steamInstallationService = steamInstallationService;
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
            _firstUnavailableAt = null;
            _restartedExistingSteam = false;
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

        var steamRunning = IsSteamRunning();
        if (!steamRunning)
        {
            LaunchSteam(steamExePath);
            _lastActionAt = now;
            return new SteamClientLaunchState(
                false,
                "Starting Steam in Gamepad UI with DevTools enabled.");
        }

        if (!_restartedExistingSteam && now - _firstUnavailableAt.Value >= RestartGracePeriod)
        {
            if (RestartSteamForDevTools(steamExePath))
            {
                _lastActionAt = now;
                _restartedExistingSteam = true;
                return new SteamClientLaunchState(
                    false,
                    "Restarting Steam once so Tools for Steam can attach to the DevTools endpoint.");
            }

            _lastActionAt = now;
            return new SteamClientLaunchState(
                false,
                "Waiting for Steam to finish its official shutdown before restarting.");
        }

        return new SteamClientLaunchState(
            false,
            "Steam is running without the DevTools endpoint. Preparing a controlled restart.");
    }

    public static string BuildSteamLaunchArguments(bool launchBigPicture)
    {
        return launchBigPicture
            ? "-gamepadui -dev -cef-enable-debugging"
            : "-dev -cef-enable-debugging";
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

        var restartRequested = RestartSteamForDevTools(steamExePath);
        _lastActionAt = DateTimeOffset.UtcNow;
        _firstUnavailableAt = _lastActionAt;
        _restartedExistingSteam = restartRequested;

        return new SteamClientLaunchState(
            false,
            restartRequested
                ? "Steam is restarting in Gamepad UI with DevTools enabled."
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

    private bool RestartSteamForDevTools(string steamExePath)
    {
        if (!CloseSteam())
        {
            return false;
        }

        LaunchSteam(steamExePath);
        return true;
    }

    private static void LaunchSteam(string steamExePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = steamExePath,
            Arguments = BuildSteamLaunchArguments(launchBigPicture: true),
            WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    private bool CloseSteam()
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

        var timeoutAt = DateTime.UtcNow.Add(ShutdownTimeout);
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
