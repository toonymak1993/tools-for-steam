using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

public sealed class StoreSyncSettingsStore
{
    private const string ProtectedSecretPrefix = "dpapi:v1:";
    private static readonly byte[] ProtectedSecretEntropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("ToolsForSteam.StoreSync.Secrets.v1"));
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
            UnprotectStoredSecrets(configuration);
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
        var backupPath = _settingsPath + ".bak";
        var legacySecretAtRest =
            ContainsLegacyOpenXblSecret(_settingsPath) ||
            ContainsLegacyOpenXblSecret(backupPath) ||
            ContainsLegacyGameDataSecret(_settingsPath) ||
            ContainsLegacyGameDataSecret(backupPath);
        try
        {
            var json = SerializeForStorage(configuration);
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
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _settingsPath);
            }

            if (legacySecretAtRest && File.Exists(backupPath))
            {
                try
                {
                    File.Copy(_settingsPath, backupPath, overwrite: true);
                }
                catch
                {
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch
                    {
                    }
                }
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

    private static string SerializeForStorage(StoreSyncConfiguration configuration)
    {
        var root = JsonSerializer.SerializeToNode(configuration, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Store Sync settings could not be serialized.");
        if (root["unifySteam"]?["stores"] is JsonObject stores)
        {
            foreach (var pair in stores)
            {
                if (pair.Value is not JsonObject store ||
                    store["openXblApiKey"] is not JsonValue value ||
                    !value.TryGetValue<string>(out var apiKey) ||
                    string.IsNullOrWhiteSpace(apiKey))
                {
                    continue;
                }

                store["openXblApiKey"] = ProtectSecret(apiKey);
            }
        }

        if (root["unifySteam"]?["gameData"]?["providers"] is JsonObject providers)
        {
            foreach (var pair in providers)
            {
                if (pair.Value is not JsonObject provider)
                {
                    continue;
                }

                ProtectJsonSecret(provider, "credential");
                ProtectJsonSecret(provider, "secondaryCredential");
            }
        }

        return root.ToJsonString(JsonOptions);
    }

    private static void ProtectJsonSecret(JsonObject owner, string propertyName)
    {
        if (owner[propertyName] is not JsonValue value ||
            !value.TryGetValue<string>(out var secret) ||
            string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        owner[propertyName] = ProtectSecret(secret);
    }

    private static void UnprotectStoredSecrets(StoreSyncConfiguration configuration)
    {
        foreach (var store in configuration.UnifySteam.Stores.Values)
        {
            if (store is null ||
                string.IsNullOrWhiteSpace(store.OpenXblApiKey) ||
                !store.OpenXblApiKey.StartsWith(
                    ProtectedSecretPrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            store.OpenXblApiKey = UnprotectSecret(store.OpenXblApiKey);
        }

        foreach (var provider in configuration.UnifySteam.GameData.Providers.Values)
        {
            if (provider is null)
            {
                continue;
            }

            provider.Credential = UnprotectStoredSecret(provider.Credential);
            provider.SecondaryCredential = UnprotectStoredSecret(
                provider.SecondaryCredential);
        }
    }

    private static string UnprotectStoredSecret(string? value)
    {
        var secret = value?.Trim() ?? string.Empty;
        return secret.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal)
            ? UnprotectSecret(secret)
            : secret;
    }

    private static string ProtectSecret(string value)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value.Trim()),
            ProtectedSecretEntropy,
            DataProtectionScope.CurrentUser);
        return ProtectedSecretPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectSecret(string value)
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(
                value[ProtectedSecretPrefix.Length..]);
            var clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                ProtectedSecretEntropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch
        {
            // A key protected by another Windows account must never be treated as
            // plaintext or sent to a provider. The UI will ask the user to replace it.
            return string.Empty;
        }
    }

    private static bool ContainsLegacyOpenXblSecret(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["unifySteam"]?["stores"] is not JsonObject stores)
            {
                return false;
            }

            return stores.Any(pair =>
                pair.Value?["openXblApiKey"] is JsonValue value &&
                value.TryGetValue<string>(out var apiKey) &&
                !string.IsNullOrWhiteSpace(apiKey) &&
                !apiKey.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsLegacyGameDataSecret(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["unifySteam"]?["gameData"]?["providers"] is not JsonObject providers)
            {
                return false;
            }

            return providers.Any(pair =>
                pair.Value is JsonObject provider &&
                new[] { "credential", "secondaryCredential" }.Any(propertyName =>
                    provider[propertyName] is JsonValue value &&
                    value.TryGetValue<string>(out var secret) &&
                    !string.IsNullOrWhiteSpace(secret) &&
                    !secret.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal)));
        }
        catch
        {
            return false;
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
        configuration.UnifySteam.GameData ??= new OmniLibraryGameDataConfiguration();
        configuration.UnifySteam.GameData.Providers ??=
            new Dictionary<string, OmniLibraryGameDataProviderConfiguration>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in OmniLibraryGameDataProviderRegistry.All)
        {
            if (!configuration.UnifySteam.GameData.Providers.TryGetValue(
                    descriptor.Id,
                    out var provider) ||
                provider is null)
            {
                provider = new OmniLibraryGameDataProviderConfiguration
                {
                    Enabled = descriptor.EnabledByDefault,
                };
                configuration.UnifySteam.GameData.Providers[descriptor.Id] = provider;
            }

            provider.Credential ??= string.Empty;
            provider.SecondaryCredential ??= string.Empty;
            provider.AccountId ??= string.Empty;
            provider.AccountName ??= string.Empty;
            provider.Region ??= string.Empty;
            provider.Locale ??= string.Empty;
            provider.DataPath = NormalizeStoredPath(provider.DataPath);
            provider.ConnectionStatus ??= string.Empty;
            provider.ConnectionDetail ??= string.Empty;
            provider.GameIdOverrides = new Dictionary<string, string>(
                provider.GameIdOverrides ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
        }

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
            unifyStoreConfiguration.RomSystems = new Dictionary<string, OmniLibraryRomSystemConfiguration>(
                unifyStoreConfiguration.RomSystems ?? [],
                StringComparer.OrdinalIgnoreCase);
            foreach (var romSystem in OmniLibraryRomSystemRegistry.Supported)
            {
                if (!unifyStoreConfiguration.RomSystems.TryGetValue(
                        romSystem.Id,
                        out var romSystemConfiguration) ||
                    romSystemConfiguration is null)
                {
                    romSystemConfiguration = new OmniLibraryRomSystemConfiguration();
                    unifyStoreConfiguration.RomSystems[romSystem.Id] = romSystemConfiguration;
                }

                romSystemConfiguration.EmulatorPath = NormalizeStoredPath(
                    romSystemConfiguration.EmulatorPath);
            }

            if (storeId.Equals(OmniLibraryRomSystemRegistry.StoreId, StringComparison.OrdinalIgnoreCase))
            {
                var psp = unifyStoreConfiguration.RomSystems["psp"];
                if (string.IsNullOrWhiteSpace(psp.EmulatorPath) &&
                    !string.IsNullOrWhiteSpace(unifyStoreConfiguration.ToolPath))
                {
                    psp.EmulatorPath = unifyStoreConfiguration.ToolPath;
                }

                // Keep the legacy PSP field readable by older TFS builds.
                unifyStoreConfiguration.ToolPath = psp.EmulatorPath;
            }
            unifyStoreConfiguration.OpenXblApiKey ??= string.Empty;
            unifyStoreConfiguration.OpenXblAccountId ??= string.Empty;
            unifyStoreConfiguration.OpenXblAccountName ??= string.Empty;
            unifyStoreConfiguration.OpenXblTitleIds =
                new Dictionary<string, string>(
                    unifyStoreConfiguration.OpenXblTitleIds ??
                    new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
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
            foreach (var game in unifyStoreConfiguration.Cache.Games.Where(game => game is not null))
            {
                game.PlatformId ??= string.Empty;
                game.PlatformTitle ??= string.Empty;
                game.RomPath = NormalizeStoredPath(game.RomPath);
            }
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

        if (configuration.OmniLibrarySettingsVersion < 5)
        {
            // Move the old per-store achievement switches into the independent
            // provider layer without changing the user's effective behavior.
            var xboxStore = configuration.UnifySteam.Stores["xbox-game-pass"];
            var xboxProvider = configuration.UnifySteam.GameData.Providers["xbox-live"];
            xboxProvider.Enabled = xboxStore.AchievementsEnabled;
            xboxProvider.Credential = FirstNonEmpty(
                xboxProvider.Credential,
                xboxStore.OpenXblApiKey);
            xboxProvider.AccountId = FirstNonEmpty(
                xboxProvider.AccountId,
                xboxStore.OpenXblAccountId);
            xboxProvider.AccountName = FirstNonEmpty(
                xboxProvider.AccountName,
                xboxStore.OpenXblAccountName);
            foreach (var pair in xboxStore.OpenXblTitleIds)
            {
                xboxProvider.GameIdOverrides.TryAdd(pair.Key, pair.Value);
            }

            configuration.UnifySteam.GameData.Providers["epic-games"].Enabled =
                configuration.UnifySteam.Stores["epic-games"].AchievementsEnabled;
            configuration.UnifySteam.GameData.Providers["gog"].Enabled =
                configuration.UnifySteam.Stores["gog-galaxy"].AchievementsEnabled;
            configuration.OmniLibrarySettingsVersion = 5;
        }

        if (configuration.OmniLibrarySettingsVersion < 6)
        {
            // Artwork is best-effort cache data. Older builds persisted a
            // store-wide degraded state when even one optional image could not
            // be found, causing an endless "repair pending" loop.
            foreach (var store in configuration.UnifySteam.Stores.Values.Where(store =>
                         store is not null &&
                         store.PreparedAtUtc.HasValue &&
                         (store.PreparationStatus.Equals(
                              "artwork-pending",
                              StringComparison.OrdinalIgnoreCase) ||
                          store.Lifecycle?.Artwork.Equals(
                              "degraded",
                              StringComparison.OrdinalIgnoreCase) == true)))
            {
                store.PreparationStatus = "prepared";
                OmniLibraryLifecycle.SetStage(store, "artwork", "ready");
                store.PreparationCompletedCount = Math.Max(
                    store.PreparationCompletedCount,
                    store.PreparationTotalCount);
                store.PreparationDetail =
                    "Library ready. Optional artwork can be refreshed per game.";
            }

            configuration.OmniLibrarySettingsVersion = 6;
        }

        // Downgrade/older-client compatibility: legacy callers still writing
        // the former per-store fields must remain effective. New setters write
        // both representations, so this merge cannot undo current UI choices.
        var legacyXbox = configuration.UnifySteam.Stores["xbox-game-pass"];
        var currentXbox = configuration.UnifySteam.GameData.Providers["xbox-live"];
        currentXbox.Enabled = legacyXbox.AchievementsEnabled;
        currentXbox.Credential = FirstNonEmpty(
            legacyXbox.OpenXblApiKey,
            currentXbox.Credential);
        currentXbox.AccountId = FirstNonEmpty(
            legacyXbox.OpenXblAccountId,
            currentXbox.AccountId);
        currentXbox.AccountName = FirstNonEmpty(
            legacyXbox.OpenXblAccountName,
            currentXbox.AccountName);
        foreach (var pair in legacyXbox.OpenXblTitleIds)
        {
            currentXbox.GameIdOverrides[pair.Key] = pair.Value;
        }
        configuration.UnifySteam.GameData.Providers["epic-games"].Enabled =
            configuration.UnifySteam.Stores["epic-games"].AchievementsEnabled;
        configuration.UnifySteam.GameData.Providers["gog"].Enabled =
            configuration.UnifySteam.Stores["gog-galaxy"].AchievementsEnabled;

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

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ??
        string.Empty;
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

    public OmniLibraryGameDataConfiguration GameData { get; set; } = new();
}

public sealed class OmniLibraryGameDataConfiguration
{
    public bool Enabled { get; set; } = true;

    public Dictionary<string, OmniLibraryGameDataProviderConfiguration> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OmniLibraryGameDataProviderConfiguration
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Provider-specific secret such as an API key, NPSSO token or session
    /// credential. StoreSyncSettingsStore protects it with Windows DPAPI.
    /// </summary>
    public string Credential { get; set; } = string.Empty;

    public string SecondaryCredential { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string AccountName { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string DataPath { get; set; } = string.Empty;

    /// <summary>
    /// Last explicit connection test. These fields contain no credentials and
    /// are safe to expose in the settings snapshot.
    /// </summary>
    public string ConnectionStatus { get; set; } = string.Empty;

    public string ConnectionDetail { get; set; } = string.Empty;

    public DateTimeOffset? ConnectionCheckedAtUtc { get; set; }

    /// <summary>
    /// OmniLibrary game identity to provider-native identity. This is both a
    /// durable resolver cache and the user-controlled escape hatch for titles
    /// whose store metadata is incomplete.
    /// </summary>
    public Dictionary<string, string> GameIdOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

public sealed class UnifySteamStoreConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enables user-scoped achievement progress for this store. Achievement
    /// traffic is title-scoped and never participates in the five-minute
    /// ownership catalog probe.
    /// </summary>
    public bool AchievementsEnabled { get; set; } = true;

    /// <summary>
    /// User-owned OpenXBL API key used only for Xbox achievement progress.
    /// It is never copied into snapshots or frontend payloads.
    /// </summary>
    public string OpenXblApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Cached OpenXBL account identity. Resolving it once avoids spending one
    /// request from the user's rate limit every time TFS starts.
    /// </summary>
    public string OpenXblAccountId { get; set; } = string.Empty;

    public string OpenXblAccountName { get; set; } = string.Empty;

    public DateTimeOffset? OpenXblAccountCheckedAtUtc { get; set; }

    /// <summary>
    /// Verified Microsoft Store product-id to Xbox title-id mappings learned
    /// from the connected player's OpenXBL title history. Microsoft catalog
    /// rows do not consistently expose XboxTitleId, so retaining a successful
    /// account-scoped match prevents repeated title-history requests.
    /// </summary>
    public Dictionary<string, string> OpenXblTitleIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

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

    public Dictionary<string, OmniLibraryRomSystemConfiguration> RomSystems { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

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

public sealed class OmniLibraryRomSystemConfiguration
{
    public string EmulatorPath { get; set; } = string.Empty;

    public bool Fullscreen { get; set; } = true;
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

    /// <summary>
    /// Optional local-emulation system identity. It lets one EMULATOR tab group
    /// games into PSP, N64 and future system collections without more tabs.
    /// </summary>
    public string PlatformId { get; set; } = string.Empty;

    public string PlatformTitle { get; set; } = string.Empty;

    public string RomPath { get; set; } = string.Empty;

    /// <summary>
    /// Store-owned title identifier used by user-scoped metadata providers.
    /// Xbox fills this from AlternateIds/XboxTitleId in the catalog response.
    /// </summary>
    public string StoreTitleId { get; set; } = string.Empty;

    /// <summary>
    /// Store sandbox/namespace. Epic achievements are keyed by this value,
    /// which is already present in Legendary's library metadata.
    /// </summary>
    public string StoreNamespace { get; set; } = string.Empty;

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
