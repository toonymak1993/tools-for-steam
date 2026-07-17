namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record StoreSyncManagedGameMatch(
    string TitleId,
    string StoreId,
    string Title,
    string ExecutablePath);
