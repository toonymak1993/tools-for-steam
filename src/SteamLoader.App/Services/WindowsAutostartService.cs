using Microsoft.Win32;

namespace SteamLoader.App.Services;

public sealed class WindowsAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string[] _valueNames;

    public WindowsAutostartService(string valueName, params string[] legacyValueNames)
    {
        _valueNames = [valueName, .. legacyValueNames.Where(name => !string.IsNullOrWhiteSpace(name))];
    }

    public bool IsEnabled(string executablePath, string arguments)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var expectedCommand = BuildCommand(executablePath, arguments);
        return _valueNames.Any(valueName =>
            string.Equals(key?.GetValue(valueName) as string, expectedCommand, StringComparison.OrdinalIgnoreCase));
    }

    public void SetEnabled(string executablePath, string arguments, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            key.SetValue(_valueNames[0], BuildCommand(executablePath, arguments), RegistryValueKind.String);
            return;
        }

        foreach (var valueName in _valueNames)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
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
            if (_valueNames.Any(ownValueName => string.Equals(valueName, ownValueName, StringComparison.OrdinalIgnoreCase)))
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
