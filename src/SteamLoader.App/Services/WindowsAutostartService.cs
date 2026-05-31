using Microsoft.Win32;

namespace SteamLoader.App.Services;

public sealed class WindowsAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public WindowsAutostartService(string valueName)
    {
        _valueName = valueName;
    }

    public bool IsEnabled(string executablePath, string arguments)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var currentValue = key?.GetValue(_valueName) as string;
        return string.Equals(currentValue, BuildCommand(executablePath, arguments), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(string executablePath, string arguments, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            key.SetValue(_valueName, BuildCommand(executablePath, arguments), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    public int DisableSteamAutostartEntries()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            return 0;
        }

        var removedCount = 0;
        foreach (var valueName in key.GetValueNames())
        {
            if (string.Equals(valueName, _valueName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key.GetValue(valueName) is not string command)
            {
                continue;
            }

            if (!command.Contains("steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            removedCount += 1;
        }

        return removedCount;
    }

    private static string BuildCommand(string executablePath, string arguments)
    {
        return string.IsNullOrWhiteSpace(arguments)
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {arguments}";
    }
}
