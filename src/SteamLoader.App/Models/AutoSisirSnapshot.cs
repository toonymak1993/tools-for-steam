namespace SteamLoader.App.Models;

public sealed record AutoSisirSnapshot(
    AutoSisirSettingsState Settings,
    string StatusText,
    bool ExecutableExists,
    bool MarkerRunning,
    string ActiveGameTitle,
    int? ActiveGameProcessId,
    string LogPath,
    IReadOnlyList<string> RecentLogLines,
    IReadOnlyList<AutoSisirWatchTitleState> WatchableTitles);

public sealed record AutoSisirSettingsState(
    bool Enabled,
    bool AutoStartForGamePass,
    string ExecutablePath,
    string DefaultExecutablePath,
    string LaunchArguments,
    bool UsesDefaultPath);

public sealed record AutoSisirWatchTitleState(
    string Id,
    string StoreId,
    string StoreTitle,
    string Title,
    string ExecutablePath,
    bool Selected,
    bool Automatic,
    bool Watched);
