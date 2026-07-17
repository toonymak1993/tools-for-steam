namespace SteamLoader.App.Models;

public sealed record ExternalGameQuickAccessState(
    bool Active,
    string GameTitle,
    string StoreId,
    int ProcessId,
    bool CanCloseCurrentGame,
    string StatusText);
