namespace SteamLoader.App.Models;

public sealed record SteamLoaderGeneralSettingsSnapshot(
    bool RunOnWindowsSignIn,
    string StartupMode,
    bool HideWindowsShellInConsoleMode,
    bool FirstRunCompleted,
    bool ConsoleModeDefaultApplied,
    SteamLoaderSplashScreenSettingsSnapshot SplashScreen,
    string ProductVersion,
    string InstallPath,
    IReadOnlyList<SteamLoaderPluginSettingsState> Plugins);

public sealed record SteamLoaderPluginSettingsState(
    string Id,
    string Title,
    string Description,
    bool Enabled,
    bool CanDisable);

public sealed record SteamLoaderSplashScreenSettingsSnapshot(
    bool Enabled,
    bool ShowText,
    string WallpaperPath,
    bool WallpaperExists,
    string IconPath,
    bool IconExists,
    int ExtraCloseDelaySeconds);
