using System.Security.Cryptography;
using System.Text;
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
    private readonly string _mutexName;

    public StoreSyncSettingsStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        var pathHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(_settingsPath.ToUpperInvariant())));
        _mutexName = $@"Local\ToolsForSteam.StoreSync.{pathHash[..24]}";
    }

    public StoreSyncConfiguration Load()
    {
        return WithExclusiveAccess(LoadCore);
    }

    public long GetRevision()
    {
        try
        {
            var file = new FileInfo(_settingsPath);
            return file.Exists
                ? HashCode.Combine(file.LastWriteTimeUtc.Ticks, file.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Save(StoreSyncConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        WithExclusiveAccess(() =>
        {
            SaveCore(configuration);
            return true;
        });
    }

    public StoreSyncConfiguration Update(Action<StoreSyncConfiguration> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return WithExclusiveAccess(() =>
        {
            var configuration = LoadCore();
            update(configuration);
            SaveCore(configuration);
            return configuration;
        });
    }

    private StoreSyncConfiguration LoadCore()
    {
        if (TryLoadPath(_settingsPath, out var configuration))
        {
            return configuration;
        }

        var backupPath = _settingsPath + ".bak";
        if (TryLoadPath(backupPath, out configuration))
        {
            return configuration;
        }

        return CreateDefaultConfiguration();
    }

    private static bool TryLoadPath(string path, out StoreSyncConfiguration configuration)
    {
        configuration = null!;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path);
            configuration = JsonSerializer.Deserialize<StoreSyncConfiguration>(json, JsonOptions)!;
            if (configuration is null)
            {
                return false;
            }

            Normalize(configuration);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveCore(StoreSyncConfiguration configuration)
    {
        Normalize(configuration);
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The Store Sync settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(configuration, JsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_settingsPath))
            {
                File.Replace(
                    temporaryPath,
                    _settingsPath,
                    _settingsPath + ".bak",
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _settingsPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    private T WithExclusiveAccess<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("Timed out while waiting for Store Sync settings access.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
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
        configuration.UnifySteam ??= new UnifySteamConfiguration();
        configuration.UnifySteam.Stores ??= new Dictionary<string, UnifySteamStoreConfiguration>(StringComparer.OrdinalIgnoreCase);

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

        foreach (var storeId in OmniLibraryStoreRegistry.Ids)
        {
            if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var unifyStoreConfiguration) || unifyStoreConfiguration is null)
            {
                configuration.UnifySteam.Stores[storeId] = new UnifySteamStoreConfiguration
                {
                    // Every OmniLibrary store is opt-in. Enabling the plugin itself must
                    // not create surprise tabs or start background work.
                    Enabled = false,
                };
                continue;
            }

            unifyStoreConfiguration.ToolPath = NormalizeStoredPath(unifyStoreConfiguration.ToolPath);
            unifyStoreConfiguration.AuthPath = NormalizeStoredPath(unifyStoreConfiguration.AuthPath);
            unifyStoreConfiguration.InstallPath = NormalizeStoredPath(unifyStoreConfiguration.InstallPath);
            unifyStoreConfiguration.DownloadWorkers = Math.Clamp(
                unifyStoreConfiguration.DownloadWorkers,
                1,
                32);
            unifyStoreConfiguration.DownloadTimeoutSeconds = Math.Clamp(
                unifyStoreConfiguration.DownloadTimeoutSeconds,
                15,
                300);
            unifyStoreConfiguration.PreparedCatalogSignature ??= string.Empty;
            unifyStoreConfiguration.RemoteCatalogSignature ??= string.Empty;
            unifyStoreConfiguration.RemoteCatalogItemIds = (unifyStoreConfiguration.RemoteCatalogItemIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            unifyStoreConfiguration.PreparationStatus ??= string.Empty;
            unifyStoreConfiguration.PreparationDetail ??= string.Empty;
            unifyStoreConfiguration.Cache ??= new UnifySteamLibraryCache();
            unifyStoreConfiguration.Cache.Games ??= [];
        }

        if (configuration.OmniLibrarySettingsVersion < 1)
        {
            // Xbox Cloud is an explicit opt-in surface. Earlier development builds
            // enabled the combined cloud catalog by default, so migrate that implicit
            // default once without overriding later user choices.
            configuration.UnifySteam.Stores["xbox-game-pass"].IncludeXboxCloudGaming = false;
            configuration.OmniLibrarySettingsVersion = 1;
        }

        if (configuration.OmniLibrarySettingsVersion < 2)
        {
            foreach (var store in configuration.UnifySteam.Stores.Values.Where(store =>
                         store is not null &&
                         store.Enabled &&
                         store.Cache?.Games is { Count: > 0 } games &&
                         games.All(game => game is not null && game.SteamAppId != 0)))
            {
                var wasBlockedOnlyByArtwork =
                    store.PreparationStatus.Equals("artwork", StringComparison.OrdinalIgnoreCase) ||
                    (store.PreparationStatus.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
                     store.PreparationDetail.StartsWith(
                         "Artwork preparation paused:",
                         StringComparison.OrdinalIgnoreCase));
                if (!wasBlockedOnlyByArtwork)
                {
                    continue;
                }

                var signature = UnifySteamService.ComputePreparedCatalogSignature(store);
                if (string.IsNullOrWhiteSpace(signature))
                {
                    continue;
                }

                store.PreparedCatalogSignature = signature;
                store.PreparedAtUtc ??= store.Cache.RefreshedAtUtc ?? DateTimeOffset.UtcNow;
                store.PreparationStatus = "artwork-pending";
                store.PreparationDetail =
                    "Library ready. Optional artwork repair continues in the background.";
            }

            configuration.OmniLibrarySettingsVersion = 2;
        }

        if (configuration.OmniLibrarySettingsVersion < 3)
        {
            foreach (var storeId in OmniLibraryStoreRegistry.Ids)
            {
                OmniLibraryLifecycle.MigrateLegacyState(
                    configuration.UnifySteam.Stores[storeId]);
            }

            configuration.OmniLibrarySettingsVersion = 3;
        }

        if (configuration.OmniLibrarySettingsVersion < 4)
        {
            foreach (var store in configuration.UnifySteam.Stores.Values.Where(store =>
                         store is not null &&
                         store.PreparedAtUtc.HasValue &&
                         !string.IsNullOrWhiteSpace(store.PreparedCatalogSignature)))
            {
                if (store.PreparationStatus.Equals("artwork", StringComparison.OrdinalIgnoreCase))
                {
                    OmniLibraryLifecycle.SetStage(store, "artwork", "updating");
                    store.PreparationStatus = "prepared";
                    store.PreparationDetail =
                        "Library ready. Optional artwork continues loading in the background.";
                }
                else if (store.PreparationStatus.Equals(
                             "artwork-pending",
                             StringComparison.OrdinalIgnoreCase))
                {
                    OmniLibraryLifecycle.SetStage(
                        store,
                        "artwork",
                        "degraded",
                        store.PreparationDetail);
                    store.PreparationStatus = "prepared";
                }
            }

            configuration.OmniLibrarySettingsVersion = 4;
        }

        foreach (var storeId in OmniLibraryStoreRegistry.Ids)
        {
            OmniLibraryLifecycle.MigrateLegacyState(
                configuration.UnifySteam.Stores[storeId]);
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

    public int OmniLibrarySettingsVersion { get; set; }

    public Dictionary<string, StoreSyncStoreConfiguration> Stores { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StoreSyncTitleOverride> TitleOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StoreSyncManifestEntry> Manifest { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StoreSyncArtworkCacheEntry> ArtworkMatchCache { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public UnifySteamConfiguration UnifySteam { get; set; } = new();

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

    public DateTimeOffset? LastArtworkAttemptAtUtc { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }

    public DateTimeOffset LastUpdatedAtUtc { get; set; }
}

public sealed class StoreSyncArtworkCacheEntry
{
    public int GameId { get; set; }

    public string MatchName { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class UnifySteamConfiguration
{
    public Dictionary<string, UnifySteamStoreConfiguration> Stores { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UnifySteamStoreConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum parallel file download workers used by managed store helpers.
    /// Sixteen matches Legendary's proven default while still allowing users
    /// with unusual networks or disks to tune the value.
    /// </summary>
    public int DownloadWorkers { get; set; } = 16;

    /// <summary>
    /// Per-request network timeout used by managed store helpers. A longer
    /// timeout avoids treating short CDN stalls as a failed large download.
    /// </summary>
    public int DownloadTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Xbox-only source switch. Kept on by default so existing installations retain
    /// the current PC Game Pass behavior after the setting is introduced.
    /// </summary>
    public bool IncludeXboxPcGamePass { get; set; } = true;

    /// <summary>
    /// Xbox-only opt-in source switch for titles playable through Xbox Cloud Gaming.
    /// These titles are presented in their own native XBOX CLOUD tab.
    /// </summary>
    public bool IncludeXboxCloudGaming { get; set; }

    /// <summary>
    /// GOG-only switch. GOG Galaxy normally installs owned DLC together with
    /// the base game, so managed downloads mirror that behavior by default.
    /// DLC remains attached to its base product and never becomes a Library tab
    /// entry of its own.
    /// </summary>
    public bool IncludeGogDlc { get; set; } = true;

    /// <summary>
    /// GOG-only compatibility mode. When enabled and Galaxy is installed, Play
    /// is delegated to Galaxy so its overlay, achievements, multiplayer
    /// authentication, and cloud-save lifecycle remain available.
    /// </summary>
    public bool PreferGogGalaxyForLaunch { get; set; }

    /// <summary>
    /// Update metadata is intentionally refreshed less often than ownership
    /// membership. This timestamp prevents one GOG manifest request per
    /// installed game on every five-minute library delta.
    /// </summary>
    public DateTimeOffset? InstalledMetadataCheckedAtUtc { get; set; }

    public int InstalledMetadataCursor { get; set; }

    public string ToolPath { get; set; } = string.Empty;

    public string AuthPath { get; set; } = string.Empty;

    /// <summary>
    /// Preferred installation root for this store. Xbox uses it for install detection;
    /// managed download helpers use it as their base path.
    /// </summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the shortcut catalog that was written successfully. Artwork is an
    /// optional background cache and never participates in store readiness. A native
    /// Steam Library tab is exposed only in a Steam session started after this
    /// preparation timestamp.
    /// </summary>
    public string PreparedCatalogSignature { get; set; } = string.Empty;

    /// <summary>
    /// Lightweight store-owned catalog signature. It contains only remote library
    /// membership and deliberately excludes Steam shortcut IDs and local install
    /// state so a cheap five-minute probe can detect purchases/removals.
    /// </summary>
    public string RemoteCatalogSignature { get; set; } = string.Empty;

    /// <summary>
    /// Store-owned membership IDs from the last successfully processed catalog probe.
    /// Keeping the IDs as well as their hash lets providers fetch metadata and artwork
    /// only for additions, while removals can be applied without rebuilding everything.
    /// </summary>
    public List<string> RemoteCatalogItemIds { get; set; } = [];

    public DateTimeOffset? PreparedAtUtc { get; set; }

    public string PreparationStatus { get; set; } = string.Empty;

    public string PreparationDetail { get; set; } = string.Empty;

    public int PreparationCompletedCount { get; set; }

    public int PreparationTotalCount { get; set; }

    public OmniLibraryStoreLifecycleConfiguration Lifecycle { get; set; } = new();

    public UnifySteamLibraryCache Cache { get; set; } = new();
}

public sealed class OmniLibraryStoreLifecycleConfiguration
{
    public string Authentication { get; set; } = "idle";

    public string Catalog { get; set; } = "idle";

    public string Shortcuts { get; set; } = "idle";

    public string Artwork { get; set; } = "idle";

    public string LastFailureScope { get; set; } = string.Empty;

    public string LastFailureDetail { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

public sealed class UnifySteamLibraryCache
{
    public string AccountName { get; set; } = string.Empty;

    public string StatusText { get; set; } = string.Empty;

    public string DetailText { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset? RefreshedAtUtc { get; set; }

    public List<UnifySteamGameCacheEntry> Games { get; set; } = [];
}

public sealed class UnifySteamGameCacheEntry
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public bool Installed { get; set; }

    public bool CloudPlayable { get; set; }

    public string InstallPath { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// The launcher that actually delivers this Epic-owned title. This remains
    /// empty for stores that do not need a per-game provider.
    /// </summary>
    public string DeliveryProvider { get; set; } = string.Empty;

    public string ThirdPartyManagedApp { get; set; } = string.Empty;

    public string PartnerLinkType { get; set; } = string.Empty;

    public string PartnerLinkId { get; set; } = string.Empty;

    public string ProviderGameId { get; set; } = string.Empty;

    public string RegistryPath { get; set; } = string.Empty;

    public string RegistryValueName { get; set; } = string.Empty;

    public string ProcessNames { get; set; } = string.Empty;

    public bool HasInstallableAsset { get; set; }

    public bool RequiresAccountLink { get; set; }

    public bool RequiresExternalLauncher { get; set; }

    public bool RequiresEpicLauncherBridge { get; set; }

    public bool SupportsCloudSaves { get; set; }

    public bool IsPreloaded { get; set; }

    public string LatestVersion { get; set; } = string.Empty;

    public string PreparationSignature { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Store-owned landscape artwork used only after the public Steam and
    /// SteamGridDB sources cannot fill a Library hero/capsule.
    /// </summary>
    public string HeroImageUrl { get; set; } = string.Empty;

    public uint SteamAppId { get; set; }
}
