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
    public const string RestoreXboxModeArgument = "--restore-xbox-mode";
    public const string CheckXboxModeSupportArgument = "--check-xbox-mode-support";
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
    public const string ShellBootstrapArgument = "--shell-bootstrap";
    public const string XboxBootstrapArgument = "--xbox-bootstrap";
    public const string XboxHostedSplashArgument = "--xbox-hosted-splash";
    public const string AutostartValueName = "TFS";
    public const string StartupModeShell = "shell";
    public const string StartupModeTray = "tray";
    public const string StartupModeXbox = "xbox";
    public const string StartupModeExternal = StartupModeXbox;
    public const string UpdateChannelStable = "stable";
    public const string UpdateChannelBeta = "beta";
    public const string GamepadHelperMutexName = "ToolsForSteam.GamepadHelper";

    public static string AutostartArguments => TrayArgument;

    public static string ShellLaunchArguments => $"{TrayArgument} {ShellBootstrapArgument} {StartupSyncArgument}";

    internal static bool ShouldUseShellBootstrap(bool shellBootstrapRequested, string? startupMode)
    {
        return shellBootstrapRequested &&
            string.Equals(startupMode, StartupModeShell, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldStartShellHandOffMonitor(
        bool shellBootstrapMode,
        bool startupSplashVisible)
    {
        // Shell Mode must always restore Explorer, while Xbox/bootstrap modes
        // need the same monitor whenever this process owns the WPF splash.
        return shellBootstrapMode || startupSplashVisible;
    }

    internal static bool ShouldShowStartupSplash(
        bool consoleBootstrapMode,
        bool xboxHostedSplash)
    {
        // A normal tray/manager/debug launch must never open a splash that has no
        // console bootstrap to complete. The Xbox host supplies its own splash.
        return consoleBootstrapMode && !xboxHostedSplash;
    }
}
