using System.Diagnostics;

namespace SteamLoader.App.Infrastructure.Display;

public sealed class DisplaySwitchService
{
    private static readonly string DisplaySwitchPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DisplaySwitch.exe");

    public DisplaySwitchResult SwitchToInternalDisplay()
    {
        RunDisplaySwitch("/internal");
        return new DisplaySwitchResult("internal", "Internal display mode requested.");
    }

    public DisplaySwitchResult SwitchToExternalDisplay()
    {
        RunDisplaySwitch("/external");
        return new DisplaySwitchResult("external", "External display mode requested.");
    }

    private static void RunDisplaySwitch(string arguments)
    {
        if (!File.Exists(DisplaySwitchPath))
        {
            throw new InvalidOperationException("DisplaySwitch.exe could not be found on this Windows installation.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = DisplaySwitchPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Windows display switch could not be started.");

        process.WaitForExit(5000);
    }
}

public sealed record DisplaySwitchResult(string Mode, string Message);
