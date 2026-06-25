namespace SteamLoader.App.Models;

public sealed record StoreSyncJournalEntryState(
    DateTimeOffset TimestampUtc,
    string Level,
    string Trigger,
    string Message,
    string Detail);
