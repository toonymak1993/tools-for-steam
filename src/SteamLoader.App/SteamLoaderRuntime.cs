namespace SteamLoader.App;

internal static class SteamLoaderRuntime
{
    public const string ProductName = "Tools for Steam";
    public const string ShortProductName = "TFS";
    public const string ReleaseRepository = "toonymak1993/tools-for-steam";
    public const string ReleaseAssetName = "ToolsForSteamSetup.exe";
    public const string PortableReleaseAssetName = "ToolsForSteam-portable-win-x64.zip";
    public const string InstallerMutexName = "ToolsForSteam.App";
    public const string AppMutexName = "ToolsForSteam.Main";
    public const string BackgroundHostMutexName = "ToolsForSteam.BackgroundHost";
    public const string BackgroundArgument = "--background";
    public const string TrayArgument = "--tray";
    public const string ManagerArgument = "--manager";
    public const string PreviewSplashArgument = "--preview-splash";
    public const string PreviewSplashDurationArgument = "--preview-duration";
    public const string HidDebugArgument = "--hid-debug";
    public const string GamepadHelperArgument = "--gamepad-helper";
    public const string RegisterInstalledHelperTasksArgument = "--register-installed-helper-tasks";
    public const string RegisterGamepadHelperTaskArgument = "--register-gamepad-helper-task";
    public const string CheckGamepadHelperTaskArgument = "--check-gamepad-helper-task";
    public const string SanitizeSteamAutostartArgument = "--sanitize-steam-autostart";
    public const string RequestSteamAttentionArgument = "--request-steam-attention";
    public const string RepairSteamStartupArgument = "--repair-steam-startup";
    // Removes what the retired Xbox Mode integration left behind. Kept so the
    // installer and the uninstall entries of older installs can still call it.
    public const string RestoreXboxModeArgument = "--restore-xbox-mode";
    public const string PrepareHandheldOemArgument = "--prepare-handheld-oem";
    public const string PrepareHandheldReplacementArgument = "--prepare-handheld-replacement";
    public const string SuspendHandheldReplacementForUpdateArgument = "--suspend-handheld-replacement-for-update";
    public const string HandheldDataDirectoryArgumentPrefix = "--handheld-data-directory=";
    public const string RestoreHandheldReplacementArgument = "--restore-handheld-replacement";
    public const string RemoveOwnedHandheldDriversArgument = "--remove-owned-handheld-drivers";
    public const string UsbIpOwnedByTfsArgument = "--usbip-owned-by-tfs";
    public const string HidHideOwnedByTfsArgument = "--hidhide-owned-by-tfs";
    public const string SetStartupModeArgumentPrefix = "--set-startup-mode=";
    public const string StartupSyncArgument = "--startup-sync";
    // Shell Takeover was removed (2026-11-11). Windows still starts TFS with
    // this argument while an old Winlogon "Shell" value points at TFS; that
    // launch opens Explorer at once and migrates to Console Mode.
    public const string LegacyShellBootstrapArgument = "--shell-bootstrap";
    // Console Mode: Explorer stays the Windows shell; a per-user sign-in task
    // starts TFS with this argument and TFS covers the desktop until Steam is up.
    public const string ConsoleStartArgument = "--console-start";
    // Uninstall: drop every startup hook (shell value, Run value, sign-in task).
    public const string RemoveStartupIntegrationArgument = "--remove-startup-integration";
    public const string AutostartValueName = "TFS";
    public const string StartupModeTray = "tray";
    public const string StartupModeConsole = "console";
    // Saved by builds that still had Shell Takeover; read only to migrate it.
    public const string LegacyStartupModeShell = "shell";
    public const string GamepadHelperMutexName = "ToolsForSteam.GamepadHelper";

    public static string AutostartArguments => TrayArgument;

    // The Winlogon "Shell" value Shell Takeover used to write; only used to
    // recognize and remove it.
    public static string LegacyShellLaunchArguments => $"{TrayArgument} {LegacyShellBootstrapArgument} {StartupSyncArgument}";

    public static string ConsoleLaunchArguments => $"{TrayArgument} {ConsoleStartArgument} {StartupSyncArgument}";

    internal static bool ShouldUseConsoleStart(bool consoleStartRequested, string? startupMode)
    {
        // A sign-in task left behind by an older mode must not cover the
        // desktop: only the saved Console Mode turns the launch into a console start.
        return consoleStartRequested &&
            string.Equals(startupMode, StartupModeConsole, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Console Mode presents Steam as the console and may hide the taskbar
    /// while Big Picture is up. eTray never does.
    /// </summary>
    internal static bool IsConsoleStartupMode(string? startupMode)
    {
        return string.Equals(startupMode, StartupModeConsole, StringComparison.OrdinalIgnoreCase);
    }
}
