namespace SteamLoader.App.Models;

public sealed record SteamLoaderGeneralSettingsSnapshot(
    bool RunOnWindowsSignIn,
    string StartupMode,
    bool HideWindowsShellInConsoleMode,
    bool FirstRunCompleted,
    bool ConsoleModeDefaultApplied,
    SteamLoaderSplashScreenSettingsSnapshot SplashScreen,
    SteamLoaderThemeSettingsSnapshot Theme,
    ControllerShortcutSettingsSnapshot ControllerShortcuts,
    int WindowsShellStartDelaySeconds,
    string ProductVersion,
    string InstallPath,
    bool DeveloperDebugEnabled,
    bool HandheldPerformanceAvailable,
    string HandheldPerformanceTitle,
    bool OemSoftwareAvailable,
    string OemSoftwareTitle,
    IReadOnlyList<SteamLoaderPluginSettingsState> Plugins,
    // Size of TFS Popups, in percent (set in the TFS Popups Quick Access plugin).
    int NotificationScalePercent = 100,
    // Windows' own Xbox Mode is on while TFS starts as the console (Console
    // Mode). Both want the screen at sign-in; only one works.
    bool WindowsXboxModeConflict = false,
    // Xbox button opens Game Bar only in Xbox / Microsoft Store games, never
    // in Big Picture (GameBarControllerAutomationService).
    bool GameBarControllerAutomation = true);

public sealed record SteamLoaderPluginSettingsState(
    string Id,
    string Title,
    string Description,
    bool Enabled,
    bool CanDisable);

public sealed record SteamLoaderSplashScreenSettingsSnapshot(
    string ArtworkMode,
    string CustomImagePath,
    bool CustomImageExists);

public sealed record SteamLoaderThemeSettingsSnapshot(
    bool WallpaperEnabled,
    bool AnimationsEnabled,
    string AccentColor);
