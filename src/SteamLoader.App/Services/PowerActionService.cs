using System.Diagnostics;
using Application = System.Windows.Forms.Application;
using PowerState = System.Windows.Forms.PowerState;

namespace SteamLoader.App.Services;

public sealed class PowerActionService
{
    private readonly SteamClientLaunchService _steamClientLaunchService;
    private readonly WindowsShellService _shellService;
    private readonly string _executablePath;
    private readonly string _backgroundArguments;
    private readonly WindowsShellVisibilityService _shellVisibilityService;
    private readonly ConsoleShellState _consoleShellState = new();

    public PowerActionService(
        SteamClientLaunchService steamClientLaunchService,
        WindowsShellService shellService,
        string executablePath,
        string backgroundArguments,
        WindowsShellVisibilityService? shellVisibilityService = null)
    {
        _steamClientLaunchService = steamClientLaunchService;
        _shellService = shellService;
        _executablePath = executablePath;
        _backgroundArguments = backgroundArguments;
        _shellVisibilityService = shellVisibilityService ?? new WindowsShellVisibilityService();
    }

    public PowerActionResult StartWindowsDesktop()
    {
        // Explorer already runs behind Big Picture in Console Mode and in eTray:
        // "desktop" means showing it, not starting it.
        ConsoleDesktopSwitcher.SwitchToDesktop(
            _shellService,
            _shellVisibilityService,
            _consoleShellState,
            minimizeBigPicture: true);
        return new PowerActionResult("Windows desktop is shown. Press the Guide button or open Big Picture to return.");
    }

    public PowerActionResult RestartSteam()
    {
        return new PowerActionResult(_steamClientLaunchService.RestartSteamForSteamTools().Message);
    }

    public PowerActionResult RestartSteamAfterDriverUpdate()
    {
        return new PowerActionResult(
            _steamClientLaunchService
                .RestartSteamForSteamTools(clearWebCache: false)
                .Message);
    }

    public PowerActionResult RestartSteamTools()
    {
        ScheduleBackgroundHostRestart();
        return new PowerActionResult("Tools for Steam background host is restarting.");
    }

    public PowerActionResult SleepWindows()
    {
        Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false);
        return new PowerActionResult("Windows is going to sleep.");
    }

    public PowerActionResult RestartWindows()
    {
        StartShutdownCommand("/r /t 0");
        return new PowerActionResult("Windows restart requested.");
    }

    public PowerActionResult ShutDownWindows()
    {
        StartShutdownCommand("/s /t 0");
        return new PowerActionResult("Windows shutdown requested.");
    }

    private void ScheduleBackgroundHostRestart()
    {
        var restartCommand =
            $"/c \"timeout /t 2 /nobreak >nul & start \"\" \"{_executablePath}\" {_backgroundArguments}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = restartCommand,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
    }

    private static void StartShutdownCommand(string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
    }
}

public sealed record PowerActionResult(string Message);
