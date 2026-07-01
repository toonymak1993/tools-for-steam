using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

public sealed class StoreSyncSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public StoreSyncSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public StoreSyncConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultConfiguration();
            }

            var json = File.ReadAllText(_settingsPath);
            var configuration = JsonSerializer.Deserialize<StoreSyncConfiguration>(json, JsonOptions)
                ?? CreateDefaultConfiguration();

            Normalize(configuration);
            return configuration;
        }
        catch
        {
            return CreateDefaultConfiguration();
        }
    }

    public void Save(StoreSyncConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(configuration, JsonOptions));
    }

    private static StoreSyncConfiguration CreateDefaultConfiguration()
    {
        var configuration = new StoreSyncConfiguration();
        Normalize(configuration);
        return configuration;
    }

    private static void Normalize(StoreSyncConfiguration configuration)
    {
        configuration.SteamGridDbApiKey ??= string.Empty;
        configuration.Stores ??= new Dictionary<string, StoreSyncStoreConfiguration>(StringComparer.OrdinalIgnoreCase);
        configuration.TitleOverrides ??= new Dictionary<string, StoreSyncTitleOverride>(StringComparer.OrdinalIgnoreCase);
        configuration.Manifest ??= new Dictionary<string, StoreSyncManifestEntry>(StringComparer.OrdinalIgnoreCase);
        configuration.ArtworkMatchCache ??= new Dictionary<string, StoreSyncArtworkCacheEntry>(StringComparer.OrdinalIgnoreCase);

        if (configuration.SyncBehaviorVersion < 2)
        {
            configuration.CloseSteamBeforeSync = true;
            configuration.SyncBehaviorVersion = 2;
        }

        if (configuration.SyncBehaviorVersion < 3)
        {
            configuration.TakeOverExistingShortcuts = true;
            configuration.CleanupMissingTitles = true;
            configuration.SyncBehaviorVersion = 3;
        }

        foreach (var storeId in new[] { "epic-games", "gog-galaxy", "xbox-game-pass", "ubisoft-connect", "ea-app", "battle-net", "amazon-games", "itch-io", "custom-locations" })
        {
            if (!configuration.Stores.TryGetValue(storeId, out var storeConfiguration) || storeConfiguration is null)
            {
                configuration.Stores[storeId] = new StoreSyncStoreConfiguration();
                continue;
            }

            storeConfiguration.ScanPath = NormalizeStoredPath(storeConfiguration.ScanPath);
            storeConfiguration.AdditionalScanPaths = (storeConfiguration.AdditionalScanPaths ?? [])
                .Select(NormalizeStoredPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string NormalizeStoredPath(string? path)
    {
        var trimmedPath = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmedPath);
        }
        catch
        {
            return trimmedPath;
        }
    }
}

public sealed class StoreSyncConfiguration
{
    public string SteamGridDbApiKey { get; set; } = string.Empty;

    public bool DownloadArtwork { get; set; } = true;

    public bool PreferAnimatedArtwork { get; set; }

    public bool CloseSteamBeforeSync { get; set; }

    public bool BackupShortcuts { get; set; } = true;

    public bool LaunchBigPictureAfterSync { get; set; } = true;

    public bool TakeOverExistingShortcuts { get; set; }

    public bool CleanupMissingTitles { get; set; }

    public int SyncBehaviorVersion { get; set; }

    public Dictionary<string, StoreSyncStoreConfiguration> Stores { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StoreSyncTitleOverride> TitleOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StoreSyncManifestEntry> Manifest { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StoreSyncArtworkCacheEntry> ArtworkMatchCache { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public StoreSyncLastSyncState? LastSync { get; set; }
}

public sealed class StoreSyncStoreConfiguration
{
    public bool Enabled { get; set; } = true;

    public string ScanPath { get; set; } = string.Empty;

    public List<string> AdditionalScanPaths { get; set; } = [];
}

public sealed class StoreSyncTitleOverride
{
    public bool Excluded { get; set; }

    public string TitleOverride { get; set; } = string.Empty;

    public string ArtworkTitleOverride { get; set; } = string.Empty;
}

public sealed class StoreSyncManifestEntry
{
    public string TitleId { get; set; } = string.Empty;

    public string StoreId { get; set; } = string.Empty;

    public string StoreItemId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string EffectiveTitle { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public uint AppId { get; set; }

    public bool ManagedShortcut { get; set; }

    public bool AdoptedExistingShortcut { get; set; }

    public string LastAction { get; set; } = string.Empty;

    public string LastDetail { get; set; } = string.Empty;

    public int? SteamGridDbGameId { get; set; }

    public string ArtworkTitle { get; set; } = string.Empty;

    public bool ArtworkLocked { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }

    public DateTimeOffset LastUpdatedAtUtc { get; set; }
}

public sealed class StoreSyncArtworkCacheEntry
{
    public int GameId { get; set; }

    public string MatchName { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
