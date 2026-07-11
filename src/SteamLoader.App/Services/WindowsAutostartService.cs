using Microsoft.Win32;
using System.Diagnostics;
using System.Text;

namespace SteamLoader.App.Services;

public sealed class WindowsAutostartService
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _runKeyPath;
    private readonly string[] _valueNames;

    public WindowsAutostartService(string valueName, params string[] legacyValueNames)
        : this(DefaultRunKeyPath, valueName, legacyValueNames)
    {
    }

    public WindowsAutostartService(string runKeyPath, string valueName, params string[] legacyValueNames)
    {
        _runKeyPath = string.IsNullOrWhiteSpace(runKeyPath)
            ? DefaultRunKeyPath
            : runKeyPath;
        _valueNames = [valueName, .. legacyValueNames.Where(name => !string.IsNullOrWhiteSpace(name))];
    }

    public bool IsEnabled(string executablePath, string arguments)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
        var expectedCommand = BuildCommand(executablePath, arguments);
        return _valueNames.Any(valueName =>
            string.Equals(key?.GetValue(valueName) as string, expectedCommand, StringComparison.OrdinalIgnoreCase));
    }

    public void SetEnabled(string executablePath, string arguments, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);

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

    public int DisableSteamAutostartEntries(Action<string>? diagnosticLog = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);
        var removedCount = 0;
        if (key is not null)
        {
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
                diagnosticLog?.Invoke($"disabled registry Steam autostart value={valueName} command={command}");
                removedCount += 1;
            }
        }

        removedCount += DisableSteamScheduledAutostartTasks(diagnosticLog);
        return removedCount;
    }

    private static int DisableSteamScheduledAutostartTasks(Action<string>? diagnosticLog)
    {
        const string script = """
$disabled = 0

function Test-IsConflictingStartupTask {
  param($Task)

  $steamExePattern = '(?i)(^|[^A-Za-z])steam\.exe([^A-Za-z]|$)'
  $tfsExePattern = '(?i)(toolsforsteam|steamloader)\.exe'

  $hasLogonTrigger = @($Task.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' }).Count -gt 0
  if (-not $hasLogonTrigger) {
    return $false
  }

  if ($Task.TaskName -like '*Steam*Big Picture*' -or $Task.Description -like '*Steam*Big Picture*') {
    return $true
  }

  foreach ($action in $Task.Actions) {
    $execute = [string]$action.Execute
    $arguments = [string]$action.Arguments
    $combined = ($execute + ' ' + $arguments)

    if ($combined -match 'steam://open/bigpicture' -or
        $combined -match '(^|\s)-bigpicture(\s|$)' -or
        $combined -match $steamExePattern -or
        $combined -match $tfsExePattern) {
      return $true
    }

    if ($execute -match 'powershell(\.exe)?$' -and $arguments -match '-EncodedCommand\s+([A-Za-z0-9+/=]+)') {
      try {
        $decoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Matches[1]))
        if ($decoded -match 'steam://open/bigpicture' -or
            $decoded -match '(^|\s)-bigpicture(\s|$)' -or
            $decoded -match $steamExePattern -or
            $decoded -match $tfsExePattern) {
          return $true
        }
      } catch {
      }
    }
  }

  return $false
}

Get-ScheduledTask -ErrorAction SilentlyContinue |
  Where-Object { $_.State -ne 'Disabled' -and (Test-IsConflictingStartupTask $_) } |
  ForEach-Object {
    try {
      Disable-ScheduledTask -InputObject $_ -ErrorAction Stop | Out-Null
      $disabled++
      Write-Output ('DISABLED|' + $_.TaskPath + $_.TaskName)
    } catch {
    }
  }

Write-Output ('COUNT|' + $disabled)
""";

        try
        {
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                return 0;
            }

            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return 0;
            }

            if (process.ExitCode != 0)
            {
                return 0;
            }

            var output = process.StandardOutput.ReadToEnd();
            var disabledCount = 0;
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("DISABLED|", StringComparison.Ordinal))
                {
                    diagnosticLog?.Invoke($"disabled scheduled logon task={line[9..]}");
                }
                else if (line.StartsWith("COUNT|", StringComparison.Ordinal))
                {
                    _ = int.TryParse(line[6..], out disabledCount);
                }
            }

            return disabledCount;
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildCommand(string executablePath, string arguments)
    {
        return string.IsNullOrWhiteSpace(arguments)
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {arguments}";
    }
}
