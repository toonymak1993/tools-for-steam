using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace SteamLoader.App.Services;

/// <summary>
/// Tools for Steam no longer supports running inside the Windows Xbox Mode
/// (Gaming Full Screen Experience) as a Gaming Home app. Console Mode is the
/// only console startup. Older builds registered the GCM.ToolsForSteam.XboxHost
/// package as Gaming Home app and kept a rollback snapshot of the original
/// Windows values; this class removes what they left behind.
/// </summary>
public sealed class LegacyXboxModeCleanup
{
    public const string LegacyHostPackageName = "GCM.ToolsForSteam.XboxHost";
    public const string LegacyHostPackageFamilyName = "GCM.ToolsForSteam.XboxHost_kpg9gzy2ksp2j";
    public const string LegacyHostAumid = LegacyHostPackageFamilyName + "!App";

    private const string DefaultGamingConfigurationKeyPath = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string DefaultBackupKeyPath = @"Software\GCM\SteamTools\XboxModeBackup";
    private const string GamingHomeAppValueName = "GamingHomeApp";
    private const string StartupToGamingHomeValueName = "StartupToGamingHome";
    private const string BackupCreatedValueName = "Created";
    private const string HomeAppExistedValueName = "GamingHomeAppExisted";
    private const string StartupExistedValueName = "StartupToGamingHomeExisted";
    private const string GamingExperienceLibrary = "api-ms-win-gaming-experience-l1-1-0.dll";

    private readonly string _gamingConfigurationKeyPath;
    private readonly string _backupKeyPath;
    private readonly Action _leaveActiveSession;

    public LegacyXboxModeCleanup()
        : this(DefaultGamingConfigurationKeyPath, DefaultBackupKeyPath, LeaveActiveGamingSession)
    {
    }

    internal LegacyXboxModeCleanup(
        string gamingConfigurationKeyPath,
        string backupKeyPath,
        Action leaveActiveSession)
    {
        _gamingConfigurationKeyPath = gamingConfigurationKeyPath;
        _backupKeyPath = backupKeyPath;
        _leaveActiveSession = leaveActiveSession;
    }

    /// <summary>
    /// Points Windows away from the removed TFS Gaming Home package.
    /// Restores the values from before TFS took over Xbox Mode when the TFS
    /// package is still the Gaming Home app; a home app the user picked later is
    /// left alone. The rollback snapshot is always deleted afterwards.
    /// </summary>
    /// <param name="leaveActiveSession">
    /// Also leave a running Xbox Mode session that uses the TFS package. Only the
    /// installer/uninstaller asks for this; the running app must never pull the
    /// user out of the current session.
    /// </param>
    /// <returns>True when anything from the old Xbox Mode integration was found.</returns>
    public bool Run(bool leaveActiveSession)
    {
        var found = false;
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(_gamingConfigurationKeyPath, writable: true);
            using var backup = Registry.CurrentUser.OpenSubKey(_backupKeyPath, writable: false);
            var homeAppIsTfs = string.Equals(
                settings?.GetValue(GamingHomeAppValueName) as string,
                LegacyHostAumid,
                StringComparison.OrdinalIgnoreCase);
            var hasBackup = backup?.GetValue(BackupCreatedValueName) is int created && created == 1;
            found = homeAppIsTfs || backup is not null;

            if (homeAppIsTfs && leaveActiveSession)
            {
                try
                {
                    _leaveActiveSession();
                }
                catch (Exception exception)
                {
                    WriteLog($"Leaving the legacy Xbox Mode session failed: {exception.Message}");
                }
            }

            if (homeAppIsTfs && settings is not null)
            {
                if (hasBackup)
                {
                    RestoreValue(settings, backup!, GamingHomeAppValueName, HomeAppExistedValueName, RegistryValueKind.String);
                    RestoreValue(settings, backup!, StartupToGamingHomeValueName, StartupExistedValueName, RegistryValueKind.DWord);
                    WriteLog("Restored the Windows Xbox Mode configuration from before the removed TFS Xbox Mode.");
                }
                else
                {
                    settings.DeleteValue(GamingHomeAppValueName, throwOnMissingValue: false);
                    settings.SetValue(StartupToGamingHomeValueName, 0, RegistryValueKind.DWord);
                    WriteLog("Removed the TFS Gaming Home app from the Windows Xbox Mode configuration (no snapshot found).");
                }
            }
        }
        catch (Exception exception)
        {
            WriteLog($"Legacy Xbox Mode cleanup failed: {exception.Message}");
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_backupKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception exception)
        {
            WriteLog($"Legacy Xbox Mode snapshot could not be deleted: {exception.Message}");
        }

        return found;
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

    private static void LeaveActiveGamingSession()
    {
        if (!NativeLibrary.TryLoad(GamingExperienceLibrary, out var library))
        {
            return;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, "IsGamingFullScreenExperienceActive", out var isActiveExport) ||
                !NativeLibrary.TryGetExport(library, "SetGamingFullScreenExperience", out var setExport))
            {
                return;
            }

            var isActive = Marshal.GetDelegateForFunctionPointer<IsGamingFullScreenExperienceActiveDelegate>(isActiveExport);
            var setActive = Marshal.GetDelegateForFunctionPointer<SetGamingFullScreenExperienceDelegate>(setExport);
            if (!isActive())
            {
                return;
            }

            var result = setActive(false);
            WriteLog($"Left the legacy Xbox Mode session: SetGamingFullScreenExperience(false)=0x{unchecked((uint)result):X8}.");
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            File.AppendAllText(
                Path.Combine(dataDirectory, "startup-mode.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool IsGamingFullScreenExperienceActiveDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetGamingFullScreenExperienceDelegate([MarshalAs(UnmanagedType.Bool)] bool active);
}
