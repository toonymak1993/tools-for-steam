using System.Diagnostics;
using Microsoft.Win32;

namespace SteamLoader.App.Services;

public sealed class WindowsShellService
{
    private const string DefaultWinlogonKeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string ShellValueName = "Shell";
    private const string DefaultAppStateKeyPath = @"Software\GCM\SteamTools";
    private const string PreviousShellValueName = "PreviousShell";
    private const string DefaultShellCommand = "explorer.exe";
    private readonly string _winlogonKeyPath;
    private readonly string _appStateKeyPath;

    public WindowsShellService()
        : this(DefaultWinlogonKeyPath, DefaultAppStateKeyPath)
    {
    }

    public WindowsShellService(string winlogonKeyPath, string appStateKeyPath)
    {
        _winlogonKeyPath = string.IsNullOrWhiteSpace(winlogonKeyPath)
            ? DefaultWinlogonKeyPath
            : winlogonKeyPath;
        _appStateKeyPath = string.IsNullOrWhiteSpace(appStateKeyPath)
            ? DefaultAppStateKeyPath
            : appStateKeyPath;
    }

    public bool IsEnabled(string executablePath, string arguments)
    {
        var desiredCommand = BuildCommand(executablePath, arguments);
        var currentShell = GetShellCommand();
        return string.Equals(currentShell, desiredCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(string executablePath, string arguments, bool enabled)
    {
        var desiredCommand = BuildCommand(executablePath, arguments);
        if (enabled)
        {
            var currentShell = GetShellCommand();
            if (!string.IsNullOrWhiteSpace(currentShell) &&
                !string.Equals(currentShell, desiredCommand, StringComparison.OrdinalIgnoreCase))
            {
                using var stateKey = Registry.CurrentUser.CreateSubKey(_appStateKeyPath);
                stateKey?.SetValue(PreviousShellValueName, currentShell, RegistryValueKind.String);
            }

            SetShellCommand(desiredCommand);
            return;
        }

        SetShellCommand(GetFallbackShellCommand());
    }

    public void SetExplorerShell()
    {
        SetShellCommand(DefaultShellCommand);
    }

    public void PrepareCurrentSession(string executablePath, string arguments)
    {
        var desiredCommand = BuildCommand(executablePath, arguments);
        var fallbackShell = GetFallbackShellCommand();

        try
        {
            SetShellCommand(fallbackShell);
        }
        catch
        {
        }

        try
        {
            SetShellCommand(desiredCommand);
        }
        catch
        {
        }
    }

    public void StartWindowsShellIfNeeded()
    {
        try
        {
            StartExplorerIfNeeded();
        }
        catch
        {
        }
    }

    public string GetShellCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_winlogonKeyPath, writable: false);
        return key?.GetValue(ShellValueName) as string ?? DefaultShellCommand;
    }

    private string GetFallbackShellCommand()
    {
        using var stateKey = Registry.CurrentUser.OpenSubKey(_appStateKeyPath, writable: false);
        var previousShell = stateKey?.GetValue(PreviousShellValueName) as string;
        return string.IsNullOrWhiteSpace(previousShell)
            ? DefaultShellCommand
            : previousShell;
    }

    private void SetShellCommand(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_winlogonKeyPath);
        key?.SetValue(ShellValueName, string.IsNullOrWhiteSpace(command) ? DefaultShellCommand : command, RegistryValueKind.String);
    }

    private static void StartExplorerIfNeeded()
    {
        var explorerRunning = Process.GetProcessesByName("explorer")
            .Any(process =>
            {
                try
                {
                    return !process.HasExited;
                }
                catch
                {
                    return false;
                }
            });

        if (explorerRunning)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        })?.Dispose();
    }

    private static string BuildCommand(string executablePath, string arguments)
    {
        return string.IsNullOrWhiteSpace(arguments)
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {arguments}";
    }
}
