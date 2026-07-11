using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace SteamLoader.App.Services;

public interface IXboxModeService
{
    XboxModeSupportStatus GetSupportStatus();

    void SetStartupEnabled(bool enabled);

    bool VerifyStartupEnabled(bool expectedEnabled);

    void RestoreOnUninstall();
}

public sealed record XboxModeSupportStatus(bool IsSupported, string Reason);

public sealed class XboxModeService : IXboxModeService
{
    public const string XboxHostPackageName = "GCM.ToolsForSteam.XboxHost";
    public const string XboxHostPackageFamilyName = "GCM.ToolsForSteam.XboxHost_kpg9gzy2ksp2j";
    public const string XboxHostAumid = XboxHostPackageFamilyName + "!App";

    private const string GamingExperienceLibrary = "api-ms-win-gaming-experience-l1-1-0.dll";
    private const int MinimumWindowsBuild = 26100;
    private const int MinimumWindowsBuildRevision = 7019;
    private const string DefaultGamingConfigurationKeyPath = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string DefaultBackupKeyPath = @"Software\GCM\SteamTools\XboxModeBackup";
    private const string GamingHomeAppValueName = "GamingHomeApp";
    private const string StartupToGamingHomeValueName = "StartupToGamingHome";
    private const string BackupCreatedValueName = "Created";
    private const string HomeAppExistedValueName = "GamingHomeAppExisted";
    private const string StartupExistedValueName = "StartupToGamingHomeExisted";

    private readonly string _gamingConfigurationKeyPath;
    private readonly string _backupKeyPath;
    private readonly Action<bool> _setCurrentSessionActive;
    private readonly Func<string> _resolveXboxHostAumid;
    private readonly Func<XboxModeSupportStatus> _getSupportStatus;

    public XboxModeService()
        : this(
            DefaultGamingConfigurationKeyPath,
            DefaultBackupKeyPath,
            SetCurrentSessionActive,
            ResolveXboxHostAumid,
            CheckSupport)
    {
    }

    internal XboxModeService(
        string gamingConfigurationKeyPath,
        string backupKeyPath,
        Action<bool> setCurrentSessionActive,
        Func<string> resolveXboxHostAumid,
        Func<XboxModeSupportStatus>? getSupportStatus = null)
    {
        _gamingConfigurationKeyPath = gamingConfigurationKeyPath;
        _backupKeyPath = backupKeyPath;
        _setCurrentSessionActive = setCurrentSessionActive;
        _resolveXboxHostAumid = resolveXboxHostAumid;
        _getSupportStatus = getSupportStatus ?? (() => new XboxModeSupportStatus(true, string.Empty));
    }

    public XboxModeSupportStatus GetSupportStatus() => _getSupportStatus();

    public void SetStartupEnabled(bool enabled)
    {
        BackupOriginalValuesOnce();
        using var key = Registry.CurrentUser.CreateSubKey(_gamingConfigurationKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Windows Xbox Mode settings could not be opened.");
        }

        if (enabled)
        {
            var support = GetSupportStatus();
            if (!support.IsSupported)
            {
                throw new InvalidOperationException(support.Reason);
            }
        }

        var previousHomeApp = ReadValue(key, GamingHomeAppValueName);
        var previousStartup = ReadValue(key, StartupToGamingHomeValueName);
        try
        {
            if (enabled)
            {
                key.SetValue(GamingHomeAppValueName, _resolveXboxHostAumid(), RegistryValueKind.String);
                key.SetValue(StartupToGamingHomeValueName, 1, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue(StartupToGamingHomeValueName, 0, RegistryValueKind.DWord);
            }

            try
            {
                _setCurrentSessionActive(enabled);
            }
            catch (Exception exception) when (
                !enabled &&
                (exception is DllNotFoundException || exception is EntryPointNotFoundException))
            {
                WriteLog($"Xbox Mode native deactivation API is unavailable; startup was still disabled: {FormatException(exception)}");
            }
            if (!VerifyStartupEnabled(enabled))
            {
                throw new InvalidOperationException("Windows did not confirm the requested Xbox Mode startup state.");
            }

            WriteLog($"Xbox Mode startup changed to {(enabled ? "enabled" : "disabled")}.");
        }
        catch (Exception exception)
        {
            RestoreCapturedValue(key, GamingHomeAppValueName, previousHomeApp);
            RestoreCapturedValue(key, StartupToGamingHomeValueName, previousStartup);
            WriteLog($"Xbox Mode startup change failed and was rolled back: {FormatException(exception)}");
            throw;
        }
    }

    public bool VerifyStartupEnabled(bool expectedEnabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_gamingConfigurationKeyPath, writable: false);
        var startupEnabled = Convert.ToInt32(key?.GetValue(StartupToGamingHomeValueName, 0) ?? 0) == 1;
        if (!expectedEnabled)
        {
            return !startupEnabled;
        }

        return startupEnabled && string.Equals(
            key?.GetValue(GamingHomeAppValueName) as string,
            _resolveXboxHostAumid(),
            StringComparison.Ordinal);
    }

    private static string ResolveXboxHostAumid()
    {
        var packageDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            XboxHostPackageFamilyName);
        if (Directory.Exists(packageDataPath))
        {
            return XboxHostAumid;
        }

        throw new InvalidOperationException(
            "The Tools for Steam Xbox Mode package is not installed for this Windows user.");
    }

    public void RestoreOnUninstall()
    {
        try
        {
            _setCurrentSessionActive(false);
        }
        catch (Exception exception)
        {
            WriteLog($"Xbox Mode session deactivation during uninstall failed: {FormatException(exception)}");
        }
        using var backup = Registry.CurrentUser.OpenSubKey(_backupKeyPath, writable: false);
        if (backup?.GetValue(BackupCreatedValueName) is not int created || created != 1)
        {
            return;
        }

        using var settings = Registry.CurrentUser.CreateSubKey(_gamingConfigurationKeyPath);
        if (settings is null)
        {
            return;
        }

        RestoreValue(settings, backup, GamingHomeAppValueName, HomeAppExistedValueName, RegistryValueKind.String);
        RestoreValue(settings, backup, StartupToGamingHomeValueName, StartupExistedValueName, RegistryValueKind.DWord);
        Registry.CurrentUser.DeleteSubKeyTree(_backupKeyPath, throwOnMissingSubKey: false);
        WriteLog("Original Windows Xbox Mode configuration restored during uninstall.");
    }

    private static XboxModeSupportStatus CheckSupport()
    {
        var (build, revision) = GetWindowsBuild();
        if (build < MinimumWindowsBuild ||
            (build == MinimumWindowsBuild && revision < MinimumWindowsBuildRevision))
        {
            return new XboxModeSupportStatus(
                false,
                $"Xbox Mode requires Windows build {MinimumWindowsBuild}.{MinimumWindowsBuildRevision} or newer. Detected {build}.{revision}.");
        }

        if (!HasGamingExperienceExports())
        {
            return new XboxModeSupportStatus(false, "The Windows Gaming Full Screen Experience API is not available.");
        }

        var packageDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            XboxHostPackageFamilyName);
        if (!Directory.Exists(packageDataPath))
        {
            return new XboxModeSupportStatus(false, "The Tools for Steam Xbox Mode package is not registered. Run the installer repair.");
        }

        return new XboxModeSupportStatus(true, "Xbox Mode package and Windows Gaming FSE support are ready.");
    }

    private static (int Build, int Revision) GetWindowsBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
        _ = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var build);
        var revision = Convert.ToInt32(key?.GetValue("UBR", 0) ?? 0);
        return (build, revision);
    }

    private static bool HasGamingExperienceExports()
    {
        if (!NativeLibrary.TryLoad(GamingExperienceLibrary, out var library))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(library, nameof(IsGamingFullScreenExperienceActive), out _) &&
                   NativeLibrary.TryGetExport(library, nameof(SetGamingFullScreenExperience), out _);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private void BackupOriginalValuesOnce()
    {
        using var existingBackup = Registry.CurrentUser.OpenSubKey(_backupKeyPath, writable: false);
        if (existingBackup?.GetValue(BackupCreatedValueName) is int created && created == 1)
        {
            return;
        }

        using var settings = Registry.CurrentUser.OpenSubKey(_gamingConfigurationKeyPath, writable: false);
        using var backup = Registry.CurrentUser.CreateSubKey(_backupKeyPath);
        if (backup is null)
        {
            throw new InvalidOperationException("The Xbox Mode rollback snapshot could not be created.");
        }

        BackupValue(settings, backup, GamingHomeAppValueName, HomeAppExistedValueName);
        BackupValue(settings, backup, StartupToGamingHomeValueName, StartupExistedValueName);
        backup.SetValue(BackupCreatedValueName, 1, RegistryValueKind.DWord);
    }

    private static void BackupValue(RegistryKey? source, RegistryKey backup, string valueName, string existedValueName)
    {
        var value = source?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        backup.SetValue(existedValueName, value is null ? 0 : 1, RegistryValueKind.DWord);
        if (value is not null)
        {
            backup.SetValue(valueName, value, source!.GetValueKind(valueName));
        }
    }

    private static void RestoreValue(
        RegistryKey target,
        RegistryKey backup,
        string valueName,
        string existedValueName,
        RegistryValueKind fallbackKind)
    {
        if (backup.GetValue(existedValueName) is int existed && existed == 1)
        {
            var value = backup.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is not null)
            {
                RegistryValueKind kind;
                try
                {
                    kind = backup.GetValueKind(valueName);
                }
                catch
                {
                    kind = fallbackKind;
                }
                target.SetValue(valueName, value, kind);
                return;
            }
        }

        target.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static void SetCurrentSessionActive(bool active)
    {
        const int eAbort = unchecked((int)0x80004004);
        var lastResult = 0;
        WriteLog($"Gaming FSE transition requested: active={active}.");
        for (var transitionAttempt = 0; transitionAttempt < 8; transitionAttempt++)
        {
            if (IsGamingFullScreenExperienceActive() == active)
            {
                WriteLog($"Gaming FSE transition already satisfied on attempt {transitionAttempt + 1}: active={active}.");
                return;
            }

            lastResult = SetGamingFullScreenExperience(active);
            WriteLog(
                $"SetGamingFullScreenExperience attempt {transitionAttempt + 1} returned " +
                $"0x{unchecked((uint)lastResult):X8} for active={active}.");
            if (lastResult < 0)
            {
                if (lastResult == eAbort)
                {
                    Thread.Sleep(500);
                    continue;
                }

                Marshal.ThrowExceptionForHR(lastResult);
            }

            for (var confirmationAttempt = 0; confirmationAttempt < 20; confirmationAttempt++)
            {
                if (IsGamingFullScreenExperienceActive() == active)
                {
                    WriteLog(
                        $"Gaming FSE transition confirmed after attempt {transitionAttempt + 1}, " +
                        $"poll {confirmationAttempt + 1}: active={active}.");
                    return;
                }

                Thread.Sleep(100);
            }
        }

        if (lastResult < 0)
        {
            Marshal.ThrowExceptionForHR(lastResult);
        }
        throw new InvalidOperationException("Windows did not enter the requested Gaming Full Screen Experience state.");
    }

    private static CapturedRegistryValue ReadValue(RegistryKey key, string valueName)
    {
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null
            ? new CapturedRegistryValue(false, null, RegistryValueKind.None)
            : new CapturedRegistryValue(true, value, key.GetValueKind(valueName));
    }

    private static void RestoreCapturedValue(RegistryKey key, string valueName, CapturedRegistryValue captured)
    {
        if (captured.Exists && captured.Value is not null)
        {
            key.SetValue(valueName, captured.Value, captured.Kind);
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            File.AppendAllText(
                Path.Combine(dataDirectory, "xbox-mode.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string FormatException(Exception exception)
        => $"HRESULT=0x{unchecked((uint)exception.HResult):X8}{Environment.NewLine}{exception}";

    private sealed record CapturedRegistryValue(bool Exists, object? Value, RegistryValueKind Kind);

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsGamingFullScreenExperienceActive();

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
    private static extern int SetGamingFullScreenExperience([MarshalAs(UnmanagedType.Bool)] bool active);
}

internal sealed class NoOpXboxModeService : IXboxModeService
{
    public static NoOpXboxModeService Instance { get; } = new();

    public XboxModeSupportStatus GetSupportStatus() => new(true, string.Empty);

    public void SetStartupEnabled(bool enabled)
    {
    }

    public void RestoreOnUninstall()
    {
    }

    public bool VerifyStartupEnabled(bool expectedEnabled) => true;
}
