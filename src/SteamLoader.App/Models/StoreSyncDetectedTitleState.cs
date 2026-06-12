namespace SteamLoader.App.Models;

public sealed record StoreSyncDetectedTitleState(
    string Id,
    string StoreId,
    string StoreTitle,
    string Title,
    string ExecutablePath,
    string StartDirectory,
    string LaunchOptions);
