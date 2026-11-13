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

    /// <summary>
    /// True while the per-user shell value still starts a TFS build as the
    /// removed Shell Takeover (any install path).
    /// </summary>
    public bool IsLegacyTfsShell()
    {
        var currentShell = GetShellCommand();
        return currentShell.Contains(SteamLoaderRuntime.LegacyShellBootstrapArgument, StringComparison.OrdinalIgnoreCase) &&
            (currentShell.Contains("ToolsForSteam.exe", StringComparison.OrdinalIgnoreCase) ||
             currentShell.Contains("SteamLoader.exe", StringComparison.OrdinalIgnoreCase));
    }

    public void SetExplorerShell()
    {
        SetShellCommand(DefaultShellCommand);
    }

    /// <summary>
    /// Gives Windows its shell back when the per-user shell value still starts
    /// this TFS executable (any arguments). Used on uninstall.
    /// </summary>
    public bool RestoreIfOwnedBy(string executablePath)
    {
        var currentShell = GetShellCommand();
        if (string.IsNullOrWhiteSpace(executablePath) ||
            currentShell.IndexOf(executablePath, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        var fallbackShell = GetFallbackShellCommand();
        var executableName = Path.GetFileName(executablePath);
        SetShellCommand(
            !string.IsNullOrWhiteSpace(executableName) &&
            fallbackShell.IndexOf(executableName, StringComparison.OrdinalIgnoreCase) >= 0
                ? DefaultShellCommand
                : fallbackShell);
        return true;
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
        var explorerProcesses = Process.GetProcessesByName("explorer");
        var explorerRunning = false;
        try
        {
            explorerRunning = explorerProcesses.Any(process =>
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
        }
        finally
        {
            foreach (var process in explorerProcesses)
            {
                process.Dispose();
            }
        }

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
}
