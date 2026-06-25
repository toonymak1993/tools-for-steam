using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed class StoreSyncJournal
{
    private const long MaxJournalBytes = 256 * 1024;
    private const int MaxRetainedEntries = 240;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _journalPath;

    public StoreSyncJournal(string journalPath)
    {
        _journalPath = journalPath;
    }

    public void Append(string level, string trigger, string message, string detail = "")
    {
        try
        {
            var entry = new StoreSyncJournalEntryState(
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim(),
                message?.Trim() ?? string.Empty,
                detail?.Trim() ?? string.Empty);
            var directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                _journalPath,
                JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            TrimIfNeeded();
        }
        catch
        {
        }
    }

    public IReadOnlyList<StoreSyncJournalEntryState> ReadRecent(int count = 12)
    {
        if (count <= 0 || !File.Exists(_journalPath))
        {
            return [];
        }

        try
        {
            return File.ReadLines(_journalPath)
                .Reverse()
                .Select(TryDeserialize)
                .Where(entry => entry is not null)
                .Take(count)
                .Cast<StoreSyncJournalEntryState>()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void TrimIfNeeded()
    {
        try
        {
            if (!File.Exists(_journalPath))
            {
                return;
            }

            var fileInfo = new FileInfo(_journalPath);
            if (fileInfo.Length <= MaxJournalBytes)
            {
                return;
            }

            var retainedLines = File.ReadLines(_journalPath)
                .Reverse()
                .Take(MaxRetainedEntries)
                .Reverse()
                .ToArray();
            File.WriteAllLines(_journalPath, retainedLines);
        }
        catch
        {
        }
    }

    private static StoreSyncJournalEntryState? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoreSyncJournalEntryState>(line, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
