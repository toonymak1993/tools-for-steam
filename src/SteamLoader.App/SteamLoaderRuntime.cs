namespace SteamLoader.App;

internal static class SteamLoaderRuntime
{
    public const string BackgroundArgument = "--background";
    public const string TrayArgument = "--tray";
    public const string ManagerArgument = "--manager";
    public const string StartupSyncArgument = "--startup-sync";
    public const string AutostartValueName = "SteamLoader";

    public static string AutostartArguments => $"{TrayArgument} {StartupSyncArgument}";
}
