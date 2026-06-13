namespace SteamLoader.App;

internal static class SteamLoaderRuntime
{
    public const string ProductName = "Tools for Steam";
    public const string ShortProductName = "TFS";
    public const string ReleaseRepository = "toonymak1993/tools-for-steam";
    public const string ReleaseAssetName = "ToolsForSteamSetup.exe";
    public const string PortableReleaseAssetName = "ToolsForSteam-portable-win-x64.zip";
    public const string InstallerMutexName = "ToolsForSteam.App";
    public const string BackgroundArgument = "--background";
    public const string TrayArgument = "--tray";
    public const string ManagerArgument = "--manager";
    public const string PreviewSplashArgument = "--preview-splash";
    public const string PreviewSplashDurationArgument = "--preview-duration";
    public const string SetStartupModeArgumentPrefix = "--set-startup-mode=";
    public const string StartupSyncArgument = "--startup-sync";
    public const string ShellBootstrapArgument = "--shell-bootstrap";
    public const string AutostartValueName = "TFS";
    public const string StartupModeShell = "shell";
    public const string StartupModeTray = "tray";
    public const string StartupModeManual = "manual";

    public static string AutostartArguments => $"{TrayArgument} {StartupSyncArgument}";

    public static string ShellLaunchArguments => $"{TrayArgument} {StartupSyncArgument} {ShellBootstrapArgument}";
}
