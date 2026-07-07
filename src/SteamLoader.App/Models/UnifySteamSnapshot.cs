namespace SteamLoader.App.Models;

public sealed record UnifySteamSnapshot(
    string StatusText,
    string DetailText,
    DateTimeOffset? LastRefreshedAtUtc,
    IReadOnlyList<UnifySteamStoreState> Stores);

public sealed record UnifySteamStoreState(
    string Id,
    string Title,
    bool Enabled,
    bool ToolDetected,
    bool AuthConfigured,
    bool AuthReady,
    bool CanRefresh,
    bool SupportsManualCodeAuth,
    string StatusText,
    string DetailText,
    string AccountName,
    DateTimeOffset? RefreshedAtUtc,
    int InstalledCount,
    int AvailableCount,
    IReadOnlyList<UnifySteamGameState> Games);

public sealed record UnifySteamGameState(
    string Id,
    string Title,
    bool Installed,
    bool SyncedToSteam,
    string StatusText,
    string DetailText,
    string ImageUrl,
    string InstallPath,
    string ExecutablePath,
    string Version);
