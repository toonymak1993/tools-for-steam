namespace SteamLoader.App.Models;

public sealed record StoreSyncSnapshot(
    SteamProfileInfo? SteamProfile,
    StoreSyncSettingsState Settings,
    IReadOnlyList<StoreSyncStoreState> Stores,
    StoreSyncPreviewState Preview,
    StoreSyncLastSyncState? LastSync,
    StoreSyncHealthState Health,
    IReadOnlyList<StoreSyncJournalEntryState> Journal);
