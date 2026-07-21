using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.Performance;

internal sealed record RtssInstallation(
    bool Installed,
    bool Running,
    string Version,
    string InstallPath,
    string ExecutablePath);

internal sealed class RtssInstallationService
{
    private const string PackageId = "Guru3D.RTSS";
    private const string SupportedPackageVersion = "7.3.7";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS";
    private static readonly string[] RelatedProcessNames =
    [
        "RTSS",
        "EncoderServer",
        "EncoderServer64",
        "RTSSHooksLoader",
        "RTSSHooksLoader64",
        "DesktopOverlayHost",
        "DesktopOverlayHost64",
        "DesktopOverlayHostLoader"
    ];

    public RtssInstallation Detect()
    {
        var installPath = FindInstallPath();
        var executablePath = string.IsNullOrWhiteSpace(installPath)
            ? string.Empty
            : Path.Combine(installPath, "RTSS.exe");
        var version = ReadRegisteredVersion();
        if (string.IsNullOrWhiteSpace(version))
        {
            version = ReadExecutableVersion(executablePath);
        }
        // RTSS' registered package version and RTSS.exe product version do not always match.
        // A valid executable is the reliable compatibility signal for the shared-memory/profile APIs.
        var installed = File.Exists(executablePath);
        var processes = Process.GetProcessesByName("RTSS");

        try
        {
            return new RtssInstallation(
                installed,
                installed && processes.Any(process => !process.HasExited),
                version,
                installPath,
                executablePath);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public RtssInstallation EnsureInstalledAndRunning(bool allowInstall)
    {
        var installation = Detect();
        if (!installation.Installed)
        {
            if (!allowInstall)
            {
                return installation with { Installed = false, Running = false };
            }

            InstallWithWinget(forceRepair: false);
            installation = Detect();
            if (!installation.Installed)
            {
                throw new InvalidOperationException("RTSS was not available after installation.");
            }
        }

        if (!installation.Running)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installation.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = installation.InstallPath,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            var timeout = Stopwatch.StartNew();
            do
            {
                Thread.Sleep(100);
                installation = Detect();
            }
            while (!installation.Running && timeout.Elapsed < TimeSpan.FromSeconds(8));
        }

        if (!installation.Running)
        {
            throw new InvalidOperationException("RTSS could not be started.");
        }

        return installation;
    }

    public RtssInstallation InstallOrRepair()
    {
        InstallWithWinget(forceRepair: true);
        return EnsureInstalledAndRunning(allowInstall: true);
    }

    public void EnsureProfileWriteAccess()
    {
        var installation = Detect();
        if (!installation.Installed)
        {
            throw new InvalidOperationException("RTSS must be installed before its game profiles can be configured.");
        }

        var profilesPath = GetProfilesPath(installation.InstallPath);
        if (CanWriteProfiles(profilesPath))
        {
            return;
        }

        var userSid = GetCurrentUserSid();
        var script = BuildProfileAccessScript(profilesPath, userSid);
        RunElevatedPowerShell(
            script,
            TimeSpan.FromMinutes(2),
            "RTSS profile access setup was cancelled at the Windows administrator prompt.",
            "RTSS profile access could not be configured.");

        if (!CanWriteProfiles(profilesPath))
        {
            throw new InvalidOperationException(
                "RTSS game profiles are still read-only. Use Repair RTSS once, then try the frame limit again.");
        }
    }

    private static void InstallWithWinget(bool forceRepair)
    {
        var wingetPath = ResolveWingetPath();
        var registeredVersion = ReadRegisteredVersion();
        var targetVersion = forceRepair && Version.TryParse(registeredVersion, out _)
            ? registeredVersion
            : SupportedPackageVersion;
        var wingetArguments = new List<string>
        {
            "install",
            "--id", PackageId,
            "--exact",
            "--version", targetVersion,
            "--source", "winget",
            "--scope", "machine",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };
        if (forceRepair)
        {
            wingetArguments.Add("--force");
        }

        var processNames = string.Join(", ", RelatedProcessNames.Select(QuotePowerShellLiteral));
        var argumentList = string.Join(", ", wingetArguments.Select(QuotePowerShellLiteral));
        var existingInstallPath = FindInstallPath();
        var expectedInstallPath = string.IsNullOrWhiteSpace(existingInstallPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "RivaTuner Statistics Server")
            : existingInstallPath;
        var profilesPath = GetProfilesPath(expectedInstallPath);
        var userSid = GetCurrentUserSid();
        var profileAccessScript = BuildProfileAccessScript(profilesPath, userSid);
        var maintenanceScript = $$"""
            $ErrorActionPreference = 'Continue'
            $rtssProcessNames = @({{processNames}})
            Get-Process -Name $rtssProcessNames -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }
            Start-Sleep -Milliseconds 700
            Get-Process -Name $rtssProcessNames -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            $deadline = [DateTime]::UtcNow.AddSeconds(8)
            while ((Get-Process -Name $rtssProcessNames -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
              Start-Sleep -Milliseconds 200
            }
            if (Get-Process -Name $rtssProcessNames -ErrorAction SilentlyContinue) { exit 1618 }
            $wingetArguments = @({{argumentList}})
            & {{QuotePowerShellLiteral(wingetPath)}} @wingetArguments
            $wingetExitCode = $LASTEXITCODE
            if (Test-Path -LiteralPath {{QuotePowerShellLiteral(Path.Combine(expectedInstallPath, "RTSS.exe"))}}) {
            {{profileAccessScript}}
            }
            exit $wingetExitCode
            """;
        var exitCode = RunElevatedPowerShell(
            maintenanceScript,
            TimeSpan.FromMinutes(10),
            "RTSS maintenance was cancelled at the Windows administrator prompt.",
            "Windows Package Manager (winget) could not be started.");
        if (exitCode == 1618)
        {
            throw new InvalidOperationException("RTSS maintenance could not close all RTSS background processes.");
        }

        if (exitCode != 0 && exitCode != unchecked((int)0x8A15002B))
        {
            throw new InvalidOperationException($"RTSS installation failed (winget code 0x{exitCode:X8}).");
        }
    }

    private static string FindInstallPath()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(UninstallKey);
                var installLocation = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation)
                    && File.Exists(Path.Combine(installLocation, "RTSS.exe")))
                {
                    return installLocation;
                }

                var uninstallString = key?.GetValue("UninstallString") as string;
                if (!string.IsNullOrWhiteSpace(uninstallString))
                {
                    var candidate = Path.GetDirectoryName(ExtractExecutablePath(uninstallString));
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, "RTSS.exe")))
                    {
                        return candidate;
                    }
                }
            }
        }

        var fallbackPaths = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "RivaTuner Statistics Server"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "RivaTuner Statistics Server"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "RivaTuner Statistics Server")
        };
        return fallbackPaths.FirstOrDefault(path => File.Exists(Path.Combine(path, "RTSS.exe"))) ?? string.Empty;
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        var value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : value.Trim('"');
        }

        var executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? value[..(executableEnd + 4)] : value;
    }

    private static string ReadRegisteredVersion()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(UninstallKey);
                var version = key?.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }

        return string.Empty;
    }

    private static string ReadExecutableVersion(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return string.Empty;
        }

        return FileVersionInfo.GetVersionInfo(executablePath).ProductVersion ?? string.Empty;
    }

    private static string ResolveWingetPath()
    {
        var aliasPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "winget.exe");
        return File.Exists(aliasPath) ? aliasPath : "winget.exe";
    }

    private static string GetProfilesPath(string installPath) => Path.Combine(installPath, "Profiles");

    private static string GetCurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The signed-in Windows user could not be identified.");

    private static bool CanWriteProfiles(string profilesPath)
    {
        if (!Directory.Exists(profilesPath))
        {
            return false;
        }

        var probePath = Path.Combine(profilesPath, $".tfs-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
            File.Delete(probePath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch
            {
            }
        }
    }

    private static string BuildProfileAccessScript(string profilesPath, string userSid) => $$"""
        $profilesPath = {{QuotePowerShellLiteral(profilesPath)}}
        $userSid = [System.Security.Principal.SecurityIdentifier]::new({{QuotePowerShellLiteral(userSid)}})
        if (-not (Test-Path -LiteralPath $profilesPath)) {
          New-Item -ItemType Directory -Path $profilesPath -Force | Out-Null
        }
        $acl = Get-Acl -LiteralPath $profilesPath
        $rights = [System.Security.AccessControl.FileSystemRights]::Modify
        $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [System.Security.AccessControl.PropagationFlags]::None
        $allow = [System.Security.AccessControl.AccessControlType]::Allow
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($userSid, $rights, $inheritance, $propagation, $allow)
        $acl.SetAccessRule($rule)
        Set-Acl -LiteralPath $profilesPath -AclObject $acl
        """;

    private static int RunElevatedPowerShell(
        string script,
        TimeSpan timeout,
        string cancellationMessage,
        string startFailureMessage)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.SystemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(cancellationMessage, exception);
        }

        using (process ?? throw new InvalidOperationException(startFailureMessage))
        {
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new InvalidOperationException("The elevated RTSS operation timed out.");
            }

            return process.ExitCode;
        }
    }

    private static string QuotePowerShellLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
