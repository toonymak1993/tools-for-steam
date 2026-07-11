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
    public const string FpsHelperArgument = "--fps-helper";
    public const string RegisterInstalledHelperTasksArgument = "--register-installed-helper-tasks";
    public const string RegisterGamepadHelperTaskArgument = "--register-gamepad-helper-task";
    public const string RegisterFpsHelperTaskArgument = "--register-fps-helper-task";
    public const string CheckGamepadHelperTaskArgument = "--check-gamepad-helper-task";
    public const string CheckFpsHelperTaskArgument = "--check-fps-helper-task";
    public const string SanitizeSteamAutostartArgument = "--sanitize-steam-autostart";
    public const string RequestSteamAttentionArgument = "--request-steam-attention";
    public const string RepairSteamStartupArgument = "--repair-steam-startup";
    public const string RestoreXboxModeArgument = "--restore-xbox-mode";
    public const string CheckXboxModeSupportArgument = "--check-xbox-mode-support";
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
    public const string FpsHelperMutexName = "ToolsForSteam.FpsHelper";

    public static string AutostartArguments => TrayArgument;

    public static string ShellLaunchArguments => $"{TrayArgument} {ShellBootstrapArgument} {StartupSyncArgument}";

    internal static bool ShouldUseShellBootstrap(bool shellBootstrapRequested, string? startupMode)
    {
        return shellBootstrapRequested &&
            string.Equals(startupMode, StartupModeShell, StringComparison.OrdinalIgnoreCase);
    }
}
