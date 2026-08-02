using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.Infrastructure.StoreSync;

public sealed class StoreSyncService
{
    private const ulong SteamIdOffset = 76561197960265728UL;
    private const string ManagedShortcutMarker = "steamloader://managed";
    private static readonly TimeSpan BaseAutomaticSyncFailureRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExpectedAutomationWriteIgnoreDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan IncompleteArtworkRetryDelay = TimeSpan.FromHours(6);
    private static readonly TimeSpan UnifySteamSnapshotCacheLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ActiveUnifySteamSnapshotCacheLifetime = TimeSpan.FromSeconds(2);
    private const int DownloadCenterRecentHistoryLimit = 20;
    private static readonly TimeSpan[] OwnershipRepairFollowUpDelays =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8)
    ];
    private static readonly string[] IgnoreCustomExecutableTokens =
    [
        "bootstrap",
        "bootstrapper",
        "launcher",
        "helper",
        "helpers",
        "tool",
        "tools",
        "service",
        "services",
        "sdk",
        "crash",
        "crashreporter",
        "crashpad",
        "report",
        "benchmark",
        "config",
        "configurator",
        "setup",
        "install",
        "unins",
        "uninstall",
        "patch",
        "update",
        "updater",
        "redist",
        "redistributable",
        "easyanticheat",
        "eadesktop",
        "ealauncher",
        "cefprocess",
        "prereq",
        "prerequisite",
        "editor",
        "engine",
        "unrealcefsubprocess",
        "unitycrashhandler",
        "shadercompileworker",
        "bugreport",
        "server",
        "dedicatedserver",
        "test"
    ];

    private static readonly string[] IgnoreCustomDirectoryTokens =
    [
        ".egstore",
        "__installer",
        "_redist",
        "engine",
        "engines",
        "redistributable",
        "redist",
        "prereq",
        "prerequisites",
        "launcher",
        "launchers",
        "support",
        "helper",
        "helpers",
        "tools",
        "tool",
        "editor",
        "editors",
        "sdk",
        "sdks",
        "modkit",
        "modkits",
        "commonredist",
        "steaminput"
    ];

    private static readonly string[] StructuralDirectoryTokens =
    [
        "bin",
        "bins",
        "binary",
        "binaries",
        "x64",
        "x86",
        "win64",
        "win32",
        "windows",
        "shipping",
        "release",
        "debug"
    ];

    private static readonly IReadOnlyDictionary<string, string> BattleNetProductToTitle =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wow"] = "World of Warcraft",
            ["wow_classic"] = "World of Warcraft Classic",
            ["wow_classic_era"] = "World of Warcraft Classic Era",
            ["wowt"] = "World of Warcraft PTR",
            ["wow_beta"] = "World of Warcraft Beta",
            ["d3"] = "Diablo III",
            ["fenris"] = "Diablo IV",
            ["anbs"] = "Diablo Immortal",
            ["s1"] = "StarCraft: Remastered",
            ["s2"] = "StarCraft II",
            ["hs_beta"] = "Hearthstone",
            ["hero"] = "Heroes of the Storm",
            ["prometheus"] = "Overwatch 2",
            ["viper"] = "Call of Duty: Black Ops 4",
            ["lazarus"] = "Call of Duty: Modern Warfare",
            ["zeus"] = "Call of Duty: Black Ops Cold War",
            ["fore"] = "Call of Duty: Vanguard",
            ["odin"] = "Call of Duty: Modern Warfare II",
            ["w3"] = "Warcraft III: Reforged",
            ["auks"] = "Warcraft Rumble",
            ["clnt"] = "Call of Duty HQ",
        };

    private static readonly HashSet<string> BattleNetSkipProducts =
        new(StringComparer.OrdinalIgnoreCase) { "bna", "agent", "bts", "bts2", "battlenet" };

    private static readonly StoreDefinition[] StoreDefinitions =
    [
        new(
            "epic-games",
            "Epic Games",
            "Reads installed titles directly from `LauncherInstalled.dat` so you do not need to manage executables manually.",
            SupportsAdditionalPaths: true),
        new(
            "gog-galaxy",
            "GOG Galaxy",
            "Checks GOG registry entries and `goggame-*.info` manifests for reliable title and executable data.",
            SupportsAdditionalPaths: true),
        new(
            "xbox-game-pass",
            "Xbox / Game Pass",
            "Scans common Xbox app library folders such as `XboxGames` and `ModifiableWindowsApps`.",
            SupportsAdditionalPaths: true),
        new(
            "ubisoft-connect",
            "Ubisoft Connect",
            "Reads Ubisoft Connect library settings, scans the current game library, and supports extra folders for installs on other drives.",
            SupportsAdditionalPaths: true),
        new(
            "ea-app",
            "EA App",
            "Detects EA App and legacy Origin installs from registry data, common EA library folders, and extra scan folders on other drives.",
            SupportsAdditionalPaths: true),
        new(
            "battle-net",
            "Battle.net",
            "Reads installed Blizzard titles from the Battle.net client configuration file.",
            SupportsAdditionalPaths: true),
        new(
            "amazon-games",
            "Amazon Games",
            "Reads installed titles from the Amazon Games Launcher data directory.",
            SupportsAdditionalPaths: true),
        new(
            "itch-io",
            "itch.io",
            "Scans the itch.io apps folder and reads `receipt.json` metadata for accurate game titles.",
            SupportsAdditionalPaths: true),
        new(
            "custom-locations",
            "Custom Locations",
            "Perfect for SSD library folders, emulator setups, or installs that do not belong to a launcher.",
            SupportsCustomPath: true,
            SupportsAdditionalPaths: true),
    ];

    private readonly object _gate = new();
    private readonly SemaphoreSlim _unifyRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _unifySnapshotBuildGate = new(1, 1);
    private readonly StoreSyncSettingsStore _settingsStore;
    private readonly SteamShortcutFile _shortcutFile;
    private readonly SteamGridDbArtworkDownloader _artworkDownloader;
    private readonly WindowsShellService _shellService;
    private readonly SteamInstallationService _steamInstallationService;
    private readonly SteamDevToolsClient _steamDevToolsClient;
    private readonly StoreSyncJournal _journal;
    private readonly UnifySteamService _unifySteamService;
    private Task? _activeSyncTask;
    private CancellationTokenSource? _activeSyncCancellation;
    private bool _activeSyncIsOmniLibrary;
    private string _activeSyncOmniLibraryStoreId = string.Empty;
    private readonly HashSet<string> _pendingOmniLibraryStoreSyncs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OmniLibrarySyncDelta> _pendingOmniLibraryDeltas =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _omniArtworkRetryAtUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<uint, StoreSyncArtworkTarget>>
        _pendingOmniArtworkTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<uint>> _activeOmniArtworkTargetIds =
        new(StringComparer.OrdinalIgnoreCase);
    private string _pendingOmniArtworkGridDirectory = string.Empty;
    private string _pendingOmniArtworkApiKey = string.Empty;
    private bool _pendingAllOmniLibraryStoreSync;
    private Task? _activeOmniArtworkTask;
    private readonly Dictionary<string, CancellationTokenSource> _activeOmniArtworkCancellations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _activeOmniRefreshCancellations =
        new(StringComparer.OrdinalIgnoreCase);
    private Task? _activeOwnershipRepairFollowUpTask;
    private CancellationTokenSource? _activeOwnershipRepairCancellation;
    private bool _activeOwnershipRepairIsOmniLibrary;
    private string _activeOwnershipRepairOmniLibraryStoreId = string.Empty;
    private readonly LinkedList<AppliedSyncSignatureState> _recentAppliedSyncSignatures = new();
    private readonly LinkedList<ScheduledAutomationWriteState> _scheduledAutomationWrites = new();
    private string _lastFailedAutomaticSyncSignature = string.Empty;
    private DateTimeOffset _lastFailedAutomaticSyncAt = DateTimeOffset.MinValue;
    private int _automationWatcherCount;
    private bool _automationWatchersActive;
    private int _consecutiveAutomaticFailures;
    private DateTimeOffset? _lastAutomaticCheckAtUtc;
    private DateTimeOffset? _lastAutomaticTriggerAtUtc;
    private string _lastAutomaticTriggerSource = string.Empty;
    private UnifySteamStateSnapshot? _cachedUnifySteamState;
    private StoreSyncConfiguration? _cachedUnifySteamConfiguration;
    private long _cachedUnifySteamConfigurationRevision = long.MinValue;
    private UnifySteamLibrarySummarySnapshot? _cachedUnifySteamLibrarySummary;
    private long _cachedUnifySteamSettingsRevision = long.MinValue;
    private long _cachedUnifySteamDownloadsRevision = long.MinValue;
    private DateTimeOffset _cachedUnifySteamStateExpiresAtUtc = DateTimeOffset.MinValue;
    private long _unifySteamStateRevision;
    // Tracks the last time TFS itself wrote shortcuts.vdf so we can detect Steam overwriting it.
    private DateTimeOffset _shortcutsFileLastOwnedWriteAtUtc = DateTimeOffset.MinValue;

    internal StoreSyncService(
        StoreSyncSettingsStore settingsStore,
        SteamShortcutFile shortcutFile,
        SteamGridDbArtworkDownloader artworkDownloader,
        WindowsShellService shellService,
        SteamInstallationService steamInstallationService,
        SteamDevToolsClient steamDevToolsClient,
        StoreSyncJournal journal)
    {
        _settingsStore = settingsStore;
        _shortcutFile = shortcutFile;
        _artworkDownloader = artworkDownloader;
        _shellService = shellService;
        _steamInstallationService = steamInstallationService;
        _steamDevToolsClient = steamDevToolsClient;
        _journal = journal;
        _unifySteamService = new UnifySteamService(journal);
    }

    internal void UpdateAutomationWatchers(int watcherCount, bool watchersActive)
    {
        lock (_gate)
        {
            _automationWatcherCount = Math.Max(0, watcherCount);
            _automationWatchersActive = watchersActive && watcherCount > 0;
        }
    }

    internal void RecordAutomationCheck(string triggerSource)
    {
        lock (_gate)
        {
            _lastAutomaticCheckAtUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(triggerSource))
            {
                _lastAutomaticTriggerSource = triggerSource.Trim();
            }
        }
    }

    internal void InvalidateAutomaticSyncState()
    {
        lock (_gate)
        {
            PruneScheduledAutomationWrites(DateTimeOffset.UtcNow);
            _recentAppliedSyncSignatures.Clear();
            _lastFailedAutomaticSyncSignature = string.Empty;
            _lastFailedAutomaticSyncAt = DateTimeOffset.MinValue;
        }
    }

    internal bool ShouldIgnoreAutomationWatcherEvent(string? path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        lock (_gate)
        {
            PruneScheduledAutomationWrites(DateTimeOffset.UtcNow);
            return _scheduledAutomationWrites.Any(entry =>
                string.Equals(entry.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RememberExpectedAutomationWrite(string? path, TimeSpan ignoreDuration)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath) || ignoreDuration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            var ignoreUntilUtc = DateTimeOffset.UtcNow.Add(ignoreDuration);
            PruneScheduledAutomationWrites(ignoreUntilUtc);

            var existingNode = _scheduledAutomationWrites.First;
            while (existingNode is not null &&
                   !string.Equals(existingNode.Value.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                existingNode = existingNode.Next;
            }

            if (existingNode is not null)
            {
                _scheduledAutomationWrites.Remove(existingNode);
            }

            _scheduledAutomationWrites.AddFirst(new ScheduledAutomationWriteState(normalizedPath, ignoreUntilUtc));

            // Record the timestamp of our own write so we can detect Steam overwriting the file later.
            _shortcutsFileLastOwnedWriteAtUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Clears the sync-signature cache when shortcuts.vdf has been modified externally by Steam
    /// (i.e., after a Steam restart that wiped our non-Steam shortcuts).
    /// Must be called inside <see cref="_gate"/>.
    /// </summary>
    private void InvalidateSyncSignaturesIfShortcutsModifiedExternally(string? shortcutsPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutsPath) || !File.Exists(shortcutsPath))
        {
            return;
        }

        // Allow 20 seconds grace to avoid reacting to our own write if the clock resolution is low.
        const double GraceSeconds = 20.0;
        try
        {
            var fileMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(shortcutsPath), TimeSpan.Zero);
            if (fileMtime > _shortcutsFileLastOwnedWriteAtUtc.AddSeconds(GraceSeconds))
            {
                _recentAppliedSyncSignatures.Clear();
                // Advance our own-write stamp so we don't keep clearing on subsequent polls.
                _shortcutsFileLastOwnedWriteAtUtc = fileMtime;
            }
        }
        catch
        {
        }
    }

    private void RecordAutomaticSyncOutcome(bool succeeded, string triggerSource, string message)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(triggerSource))
            {
                _lastAutomaticTriggerSource = triggerSource.Trim();
            }

            if (succeeded)
            {
                _consecutiveAutomaticFailures = 0;
                _lastFailedAutomaticSyncSignature = string.Empty;
                _lastFailedAutomaticSyncAt = DateTimeOffset.MinValue;
                _lastAutomaticTriggerAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            _consecutiveAutomaticFailures++;
            _lastAutomaticTriggerAtUtc = DateTimeOffset.UtcNow;
        }

        _journal.Append("warn", string.IsNullOrWhiteSpace(triggerSource) ? "automatic" : triggerSource, message);
    }

    public StoreSyncSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(_settingsStore.Load());
        }
    }

    public UnifySteamStateSnapshot GetUnifySteamState()
    {
        var now = DateTimeOffset.UtcNow;
        var settingsRevision = _settingsStore.GetRevision();
        var downloadsRevision = UnifySteamDownloadStatusStore.GetRevision();
        lock (_gate)
        {
            if (_cachedUnifySteamState is not null &&
                _cachedUnifySteamSettingsRevision == settingsRevision &&
                _cachedUnifySteamDownloadsRevision == downloadsRevision &&
                now < _cachedUnifySteamStateExpiresAtUtc)
            {
                return _cachedUnifySteamState;
            }
        }

        _unifySnapshotBuildGate.Wait();
        try
        {
            now = DateTimeOffset.UtcNow;
            settingsRevision = _settingsStore.GetRevision();
            downloadsRevision = UnifySteamDownloadStatusStore.GetRevision();
            lock (_gate)
            {
                if (_cachedUnifySteamState is not null &&
                    _cachedUnifySteamSettingsRevision == settingsRevision &&
                    _cachedUnifySteamDownloadsRevision == downloadsRevision &&
                    now < _cachedUnifySteamStateExpiresAtUtc)
                {
                    return _cachedUnifySteamState;
                }
            }

            StoreSyncConfiguration configuration;
            lock (_gate)
            {
                configuration =
                    _cachedUnifySteamConfigurationRevision == settingsRevision
                        ? _cachedUnifySteamConfiguration!
                        : null!;
            }

            if (configuration is null)
            {
                configuration = _settingsStore.Load();
                lock (_gate)
                {
                    _cachedUnifySteamConfiguration = configuration;
                    _cachedUnifySteamConfigurationRevision = settingsRevision;
                }
            }

            var snapshot = _unifySteamService.BuildSnapshot(configuration, []);
            var activeLifecycle = UnifySteamDownloadStatusStore.GetAll().Values.Any(status =>
                UnifySteamDownloadStatusStore.IsBusyOperation(status.Status) ||
                status.Status is "paused" or "uninstalling" or "uninstall-action-required");
            lock (_gate)
            {
                _cachedUnifySteamSettingsRevision = settingsRevision;
                _cachedUnifySteamDownloadsRevision = downloadsRevision;
                _cachedUnifySteamStateExpiresAtUtc = now.Add(
                    activeLifecycle
                        ? ActiveUnifySteamSnapshotCacheLifetime
                        : UnifySteamSnapshotCacheLifetime);
                _cachedUnifySteamState = new UnifySteamStateSnapshot(
                    Interlocked.Increment(ref _unifySteamStateRevision),
                    now,
                    snapshot);
                return _cachedUnifySteamState;
            }
        }
        finally
        {
            _unifySnapshotBuildGate.Release();
        }
    }

    public UnifySteamLibrarySummarySnapshot GetUnifySteamLibrarySummary()
    {
        var state = GetCachedUnifySteamCatalogState();
        var downloadStatuses = UnifySteamDownloadStatusStore.GetAll();
        state = ReconcilePendingXboxRemovals(state, downloadStatuses);
        downloadStatuses = UnifySteamDownloadStatusStore.GetAll();
        var downloadsRevision = UnifySteamDownloadStatusStore.GetRevision();
        var summaryRevision = CombineUnifySteamRevisions(
            state.Revision,
            downloadsRevision);
        lock (_gate)
        {
            if (_cachedUnifySteamLibrarySummary?.Revision == summaryRevision)
            {
                return _cachedUnifySteamLibrarySummary;
            }
        }

        var stores = state.UnifySteam.Stores
            .Select(store =>
            {
                var descriptor = OmniLibraryStoreRegistry.GetRequired(store.Id);
                return new UnifySteamLibraryStoreSummary(
                    store.Id,
                    store.Enabled,
                    store.ReadyForLibraryTab,
                    store.XboxCloudGamingEnabled,
                    store.AvailableCount,
                    store.Games
                        .Select(game => game.SteamAppId)
                        .Where(appId => appId != 0)
                        .Distinct()
                        .ToArray(),
                    store.Games
                        .Where(game => game.Installed)
                        .Select(game => game.SteamAppId)
                        .Where(appId => appId != 0)
                        .Distinct()
                        .ToArray(),
                    store.Games
                        .Where(game => game.CloudPlayable)
                        .Select(game => game.SteamAppId)
                        .Where(appId => appId != 0)
                        .Distinct()
                        .ToArray())
                {
                    Title = descriptor.Title,
                    SupportsUninstall =
                        descriptor.Supports(OmniLibraryStoreCapabilities.ManagedUninstall) ||
                        descriptor.Supports(OmniLibraryStoreCapabilities.ProductPageUninstall),
                    LibraryTabs = OmniLibraryStoreRegistry.BuildLibraryTabSummaries(
                        descriptor,
                        store.RomSystems),
                    ActiveDownloadAppIds = store.Games
                        .Where(game =>
                            UnifySteamDownloadStatusStore.IsBusyOperation(
                                UnifySteamDownloadStatusStore.Get(
                                    downloadStatuses,
                                    store.Id,
                                    game.Id).Status))
                        .Select(game => game.SteamAppId)
                        .Where(appId => appId != 0)
                        .Distinct()
                        .ToArray(),
                    RepairableAppIds = store.Id.Equals(
                            "gog-galaxy",
                            StringComparison.OrdinalIgnoreCase)
                        ? store.Games
                            .Where(game =>
                                game.Installed &&
                                UnifySteamLauncher.IsManagedGogInstall(
                                    game.InstallPath,
                                    game.Id))
                            .Select(game => game.SteamAppId)
                            .Where(appId => appId != 0)
                            .Distinct()
                            .ToArray()
                        : [],
                    RomSystems = store.RomSystems,
                };
            })
            .ToArray();
        var summary = new UnifySteamLibrarySummarySnapshot(
            summaryRevision,
            true,
            stores);
        lock (_gate)
        {
            if (_cachedUnifySteamLibrarySummary is null ||
                _cachedUnifySteamLibrarySummary.Revision != summaryRevision)
            {
                _cachedUnifySteamLibrarySummary = summary;
            }

            return _cachedUnifySteamLibrarySummary.Revision == summaryRevision
                ? _cachedUnifySteamLibrarySummary
                : summary;
        }
    }

    public UnifySteamStateSnapshot GetUnifySteamSettingsState()
    {
        var state = GetUnifySteamState();
        return state with
        {
            UnifySteam = state.UnifySteam with
            {
                Stores = state.UnifySteam.Stores
                    .Select(store => store with { Games = [] })
                    .ToArray(),
            },
        };
    }

    public UnifySteamGameDetailSnapshot GetUnifySteamGame(uint appId)
    {
        var state = GetCachedUnifySteamCatalogState();
        var statuses = UnifySteamDownloadStatusStore.GetAll();
        state = ReconcilePendingXboxRemovals(state, statuses, appId);
        statuses = UnifySteamDownloadStatusStore.GetAll();
        var downloadsRevision = UnifySteamDownloadStatusStore.GetRevision();
        var revision = CombineUnifySteamRevisions(
            state.Revision,
            downloadsRevision);
        foreach (var store in state.UnifySteam.Stores)
        {
            var game = store.Games.FirstOrDefault(candidate => candidate.SteamAppId == appId);
            if (game is not null)
            {
                var download = UnifySteamDownloadStatusStore.Get(
                    statuses,
                    store.Id,
                    game.Id);
                return new UnifySteamGameDetailSnapshot(
                    revision,
                    store.Id,
                    game with
                    {
                        Download = new UnifySteamDownloadState(
                            download.Status,
                            download.ProgressPercent,
                            download.DetailText,
                            download.DownloadedBytes,
                            download.TotalBytes,
                            download.DownloadBytesPerSecond,
                            download.DecompressedBytesPerSecond,
                            download.DiskWriteBytesPerSecond,
                            download.DiskReadBytesPerSecond,
                            download.Attempt),
                    });
            }
        }

        return new UnifySteamGameDetailSnapshot(revision, string.Empty, null);
    }

    public OmniLibraryGameArtworkRepairResult RepairOmniLibraryGameArtwork(
        uint appId)
    {
        var detail = GetUnifySteamGame(appId);
        var game = detail.Game;
        if (game is null || string.IsNullOrWhiteSpace(detail.StoreId))
        {
            throw new InvalidOperationException(
                "The selected game is not managed by OmniLibrary.");
        }

        var profile = ResolveSteamProfile() ??
            throw new InvalidOperationException("Steam profile could not be resolved.");
        var gridDirectory = BuildGridDirectory(profile);
        var missing = SteamGridDbArtworkDownloader.GetMissingArtworkSlots(
            gridDirectory,
            appId);
        if (missing.Count == 0)
        {
            return new OmniLibraryGameArtworkRepairResult(
                appId,
                detail.StoreId,
                game.Id,
                game.Title,
                false,
                [],
                "All five Steam artwork slots are already complete.");
        }

        var configuration = _settingsStore.Load();
        var cachedGame = configuration.UnifySteam.Stores.TryGetValue(
                detail.StoreId,
                out var configuredStore)
            ? configuredStore?.Cache?.Games?.FirstOrDefault(candidate =>
                candidate is not null &&
                candidate.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase))
            : null;
        configuration.UnifySteam.GameData.Providers.TryGetValue(
            "retroachievements",
            out var retroProvider);
        var retroApiKey =
            configuration.UnifySteam.GameData.Enabled &&
            retroProvider?.Enabled == true
                ? retroProvider.Credential?.Trim() ?? string.Empty
                : string.Empty;
        var target = new StoreSyncArtworkTarget(
            $"{detail.StoreId}:{game.Id}",
            game.Title,
            appId,
            [game.Title],
            null,
            string.Empty,
            detail.StoreId,
            cachedGame?.ImageUrl ?? game.ImageUrl,
            cachedGame?.HeroImageUrl ?? string.Empty,
            game.RomPath,
            game.PlatformId,
            retroApiKey,
            ResolveRetroAchievementsArtworkGameId(
                configuration,
                cachedGame?.Id ?? game.Id,
                cachedGame?.RomPath ?? game.RomPath));
        var steamGridDbApiKey = _artworkDownloader.GetEffectiveApiKey(
            configuration.SteamGridDbApiKey);
        lock (_gate)
        {
            _pendingOmniArtworkGridDirectory = gridDirectory;
            _pendingOmniArtworkApiKey = steamGridDbApiKey;
            if (!_pendingOmniArtworkTargets.TryGetValue(
                    detail.StoreId,
                    out var storeTargets))
            {
                storeTargets = new Dictionary<uint, StoreSyncArtworkTarget>();
                _pendingOmniArtworkTargets[detail.StoreId] = storeTargets;
            }
            storeTargets[appId] = target;
            _omniArtworkRetryAtUtc.Remove(detail.StoreId);
            if (_activeOmniArtworkTask is not { IsCompleted: false })
            {
                _activeOmniArtworkTask = Task.Run(ProcessQueuedOmniLibraryArtworkAsync);
            }
        }

        return new OmniLibraryGameArtworkRepairResult(
            appId,
            detail.StoreId,
            game.Id,
            game.Title,
            true,
            missing,
            $"Repairing {string.Join(", ", missing)} in the background.");
    }

    public OmniLibraryDownloadCenterSnapshot GetOmniLibraryDownloadCenter()
    {
        var state = GetCachedUnifySteamCatalogState();
        var statuses = UnifySteamDownloadStatusStore.GetAll();
        state = ReconcilePendingXboxRemovals(state, statuses);
        statuses = UnifySteamDownloadStatusStore.GetAll();
        var entries = new List<OmniLibraryDownloadCenterEntry>();
        var matchedStatusKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var store in state.UnifySteam.Stores)
        {
            var descriptor = OmniLibraryStoreRegistry.GetRequired(store.Id);
            foreach (var game in store.Games)
            {
                var download = UnifySteamDownloadStatusStore.Get(
                    statuses,
                    store.Id,
                    game.Id);
                matchedStatusKeys.Add($"{store.Id}:{game.Id}");
                var normalizedStatus = download.Status.Trim().ToLowerInvariant();
                if (!UnifySteamDownloadStatusStore.IsDownloadCenterStatus(
                        normalizedStatus))
                {
                    continue;
                }

                entries.Add(BuildOmniLibraryDownloadCenterEntry(
                    descriptor,
                    game,
                    game.Id,
                    download));
            }
        }

        // A catalog delta or an account disconnect must never make a running
        // worker disappear from the Download Center. New operations persist
        // enough display metadata to remain controllable until they finish.
        foreach (var (key, download) in statuses)
        {
            if (matchedStatusKeys.Contains(key) ||
                !UnifySteamDownloadStatusStore.IsDownloadCenterStatus(download.Status) ||
                !UnifySteamDownloadStatusStore.TryParseKey(
                    key,
                    out var storeId,
                    out var gameId) ||
                !OmniLibraryStoreRegistry.TryGet(storeId, out var descriptor))
            {
                continue;
            }

            entries.Add(BuildOmniLibraryDownloadCenterEntry(
                descriptor,
                game: null,
                gameId,
                download));
        }

        var recentHistoryCount = 0;
        var orderedEntries = entries
            .OrderBy(entry => GetDownloadCenterSortGroup(entry.Status))
            .ThenByDescending(entry => entry.UpdatedAtUtc)
            .ThenBy(entry => entry.GameTitle, StringComparer.OrdinalIgnoreCase)
            .Where(entry =>
            {
                if (entry.Status is not ("completed" or "canceled"))
                {
                    return true;
                }

                return recentHistoryCount++ < DownloadCenterRecentHistoryLimit;
            })
            .ToArray();

        return new OmniLibraryDownloadCenterSnapshot(
            CombineUnifySteamRevisions(
                state.Revision,
                UnifySteamDownloadStatusStore.GetRevision()),
            DateTimeOffset.UtcNow,
            orderedEntries);
    }

    private static OmniLibraryDownloadCenterEntry BuildOmniLibraryDownloadCenterEntry(
        OmniLibraryStoreDescriptor descriptor,
        UnifySteamGameState? game,
        string gameId,
        UnifySteamDownloadStatus download)
    {
        var normalizedStatus = download.Status.Trim().ToLowerInvariant();
        var managedTransfer =
            descriptor.Supports(OmniLibraryStoreCapabilities.ManagedInstall) &&
            game?.CanInstallDirectly != false;
        var externalProviderTransfer =
            game?.RequiresExternalLauncher == true ||
            (game?.RequiresAccountLink == true &&
             normalizedStatus == "action-required");
        var supportsUninstall =
            descriptor.Supports(OmniLibraryStoreCapabilities.ManagedUninstall) ||
            descriptor.Supports(OmniLibraryStoreCapabilities.ProductPageUninstall);
        var active =
            UnifySteamDownloadStatusStore.IsActivelyTransferring(normalizedStatus);
        var catalogEntryAvailable = game is not null;
        var stalledThreshold = descriptor.Id.Equals(
            "xbox-game-pass",
            StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(90)
            : TimeSpan.FromSeconds(30);
        var isStalled =
            normalizedStatus == "downloading" &&
            DateTimeOffset.UtcNow - download.UpdatedAtUtc > stalledThreshold;
        var canManageExternally =
            (descriptor.Id.Equals(
                 "xbox-game-pass",
                 StringComparison.OrdinalIgnoreCase) &&
             normalizedStatus is not "completed") ||
            (descriptor.Id.Equals(
                 "gog-galaxy",
                 StringComparison.OrdinalIgnoreCase) &&
             (normalizedStatus == "action-required" ||
              normalizedStatus is
                  ("uninstall-action-required" or "uninstall-failed") &&
              descriptor.Supports(
                  OmniLibraryStoreCapabilities.ProductPageUninstall))) ||
            (descriptor.Id.Equals(
                 "epic-games",
                 StringComparison.OrdinalIgnoreCase) &&
             (game?.RequiresExternalLauncher == true ||
              game?.RequiresAccountLink == true) &&
             normalizedStatus is
                 ("action-required" or
                  "uninstall-action-required" or
                  "uninstall-failed"));
        var managedByToolsForSteam =
            managedTransfer &&
            !(
                canManageExternally &&
                normalizedStatus is
                    ("action-required" or
                     "uninstall-action-required" or
                     "uninstall-failed")
            );
        var transferOwner = managedByToolsForSteam
            ? "Tools for Steam"
            : descriptor.Id.Equals(
                "xbox-game-pass",
                StringComparison.OrdinalIgnoreCase)
                ? "Xbox app"
                : externalProviderTransfer &&
                  !string.IsNullOrWhiteSpace(game?.ProviderDisplayName)
                    ? game.ProviderDisplayName
                    : descriptor.Id.Equals(
                        "gog-galaxy",
                        StringComparison.OrdinalIgnoreCase)
                        ? "GOG Galaxy"
                        : descriptor.Title;
        return new OmniLibraryDownloadCenterEntry(
            descriptor.Id,
            descriptor.Title,
            gameId,
            game?.Title ??
            (string.IsNullOrWhiteSpace(download.GameTitle)
                ? gameId
                : download.GameTitle),
            game?.SteamAppId ?? download.SteamAppId,
            game?.Installed == true,
            normalizedStatus,
            download.ProgressPercent,
            download.DetailText,
            download.UpdatedAtUtc,
            download.DownloadedBytes,
            download.TotalBytes,
            download.DownloadBytesPerSecond,
            download.DecompressedBytesPerSecond,
            download.DiskWriteBytesPerSecond,
            download.DiskReadBytesPerSecond,
            download.Attempt,
            transferOwner,
            managedByToolsForSteam,
            CanPause:
                managedByToolsForSteam &&
                active &&
                normalizedStatus != "finalizing",
            CanResume:
                catalogEntryAvailable &&
                (
                    (managedTransfer &&
                     normalizedStatus is
                         ("paused" or "failed" or "cancel-failed" or "canceled")) ||
                    (externalProviderTransfer &&
                     normalizedStatus is
                         ("failed" or "cancel-failed" or "canceled")) ||
                    (descriptor.Id.Equals(
                         "xbox-game-pass",
                         StringComparison.OrdinalIgnoreCase) &&
                     normalizedStatus is ("failed" or "canceled"))
                ),
            CanCancel:
                CanCancelManagedDownload(
                    managedByToolsForSteam,
                    normalizedStatus),
            CanStopTracking:
                CanStopTrackingDownload(
                    managedByToolsForSteam,
                    normalizedStatus),
            CanDismiss:
                normalizedStatus is
                    "completed" or
                    "canceled" or
                    "failed" or
                    "cancel-failed" or
                    "uninstall-failed",
            CanPlay:
                game?.Installed == true &&
                normalizedStatus == "completed",
            CanManageExternally: canManageExternally,
            CanRetryUninstall:
                catalogEntryAvailable &&
                game?.Installed == true &&
                supportsUninstall &&
                normalizedStatus == "uninstall-failed",
            IsStalled: isStalled,
            CatalogEntryAvailable: catalogEntryAvailable);
    }

    internal static bool CanCancelManagedDownload(
        bool managedByToolsForSteam,
        string? status)
    {
        return managedByToolsForSteam &&
               status?.Trim().ToLowerInvariant() is
                   "preparing" or
                   "queued" or
                   "downloading" or
                   "reconnecting" or
                   "finalizing" or
                   "paused" or
                   "failed" or
                   "cancel-failed";
    }

    internal static bool CanStopTrackingDownload(
        bool managedByToolsForSteam,
        string? status)
    {
        var normalizedStatus = status?.Trim().ToLowerInvariant();
        return normalizedStatus is
                   "action-required" or
                   "uninstall-action-required" ||
               (!managedByToolsForSteam &&
                normalizedStatus is
                    "preparing" or
                    "queued" or
                    "downloading" or
                    "reconnecting" or
                    "finalizing" or
                    "paused");
    }

    private UnifySteamStateSnapshot ReconcilePendingXboxRemovals(
        UnifySteamStateSnapshot state,
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> statuses,
        uint appId = 0)
    {
        var xboxStore = state.UnifySteam.Stores.FirstOrDefault(store =>
            store.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase));
        if (xboxStore is null)
        {
            return state;
        }

        var removedGameIds = xboxStore.Games
            .Where(game =>
                (appId == 0 || game.SteamAppId == appId) &&
                UnifySteamService.TryConfirmPendingXboxRemoval(
                    game,
                    UnifySteamDownloadStatusStore.Get(
                        statuses,
                        xboxStore.Id,
                        game.Id)))
            .Select(game => game.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedGameIds.Count == 0)
        {
            return state;
        }

        lock (_gate)
        {
            var current = _cachedUnifySteamState ?? state;
            var stores = current.UnifySteam.Stores
                .Select(store =>
                {
                    if (!store.Id.Equals(
                            "xbox-game-pass",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return store;
                    }

                    var games = store.Games
                        .Select(game => removedGameIds.Contains(game.Id)
                            ? game with
                            {
                                Installed = false,
                                StatusText = "Available",
                                DetailText = "In your account library.",
                                InstallPath = string.Empty,
                                ExecutablePath = string.Empty,
                                Version = string.Empty,
                            }
                            : game)
                        .ToArray();
                    return store with
                    {
                        InstalledCount = games.Count(game => game.Installed),
                        Games = games,
                    };
                })
                .ToArray();
            _cachedUnifySteamState = new UnifySteamStateSnapshot(
                Interlocked.Increment(ref _unifySteamStateRevision),
                DateTimeOffset.UtcNow,
                current.UnifySteam with { Stores = stores });
            _cachedUnifySteamDownloadsRevision =
                UnifySteamDownloadStatusStore.GetRevision();
            _cachedUnifySteamLibrarySummary = null;
            return _cachedUnifySteamState;
        }
    }

    private UnifySteamStateSnapshot GetCachedUnifySteamCatalogState()
    {
        var settingsRevision = _settingsStore.GetRevision();
        var downloadsRevision = UnifySteamDownloadStatusStore.GetRevision();
        UnifySteamStateSnapshot? cachedState = null;
        lock (_gate)
        {
            if (_cachedUnifySteamState is not null &&
                _cachedUnifySteamSettingsRevision == settingsRevision)
            {
                if (_cachedUnifySteamDownloadsRevision == downloadsRevision)
                {
                    return _cachedUnifySteamState;
                }

                cachedState = _cachedUnifySteamState;
            }
        }

        if (cachedState is not null)
        {
            var downloadStatuses = UnifySteamDownloadStatusStore.GetAll();
            if (!RequiresLifecycleCatalogRefresh(
                    cachedState.GeneratedAtUtc,
                    downloadStatuses.Values))
            {
                // Percentage-only updates are overlaid by the game/detail
                // endpoints and must not rebuild the full catalog. A newly
                // completed install is different: its Installed flag,
                // executable and Installed tab membership must be rebuilt
                // together before this cached catalog can be reused.
                return cachedState;
            }
        }

        return GetUnifySteamState();
    }

    internal static bool RequiresLifecycleCatalogRefresh(
        DateTimeOffset catalogGeneratedAtUtc,
        IEnumerable<UnifySteamDownloadStatus> downloadStatuses)
    {
        return downloadStatuses.Any(status =>
            status.UpdatedAtUtc > catalogGeneratedAtUtc &&
            status.Status.Equals(
                "completed",
                StringComparison.OrdinalIgnoreCase));
    }

    private static long CombineUnifySteamRevisions(
        long catalogRevision,
        long downloadsRevision)
    {
        return unchecked((catalogRevision * 397L) ^ downloadsRevision);
    }

    private static int GetDownloadCenterSortGroup(string status)
    {
        return status.Trim().ToLowerInvariant() switch
        {
            "downloading" or "preparing" or "queued" or "reconnecting" => 0,
            "finalizing" or "canceling" or "uninstalling" => 1,
            "paused" or "action-required" or "uninstall-action-required" => 2,
            "failed" or "cancel-failed" or "uninstall-failed" => 3,
            "completed" => 4,
            "canceled" => 5,
            _ => 6,
        };
    }

    public IReadOnlyList<string> GetEnabledUnifySteamStoreIds()
    {
        lock (_gate)
        {
            if (!StorefrontFeatureFlags.Enabled)
            {
                return [];
            }

            return _unifySteamService.GetEnabledStoreIds(_settingsStore.Load());
        }
    }

    internal static OmniLibraryCatalogDelta ComputeOmniLibraryCatalogDelta(
        IEnumerable<string> beforeGameIds,
        IEnumerable<string> afterGameIds)
    {
        var before = beforeGameIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = afterGameIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new OmniLibraryCatalogDelta(
            after
                .Except(before, StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            before
                .Except(after, StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public OmniLibraryStoreCheckResult CheckUnifySteamStoreForChanges(string storeId)
    {
        var normalizedStoreId = storeId?.Trim() ?? string.Empty;
        if (!OmniLibraryStoreRegistry.TryGet(normalizedStoreId, out _))
        {
            return new OmniLibraryStoreCheckResult(
                normalizedStoreId,
                Checked: false,
                Succeeded: false,
                CatalogChanged: false,
                StateChanged: false,
                "Unknown OmniLibrary store.");
        }
        var refreshCancellation = BeginOmniLibraryRefresh(normalizedStoreId);
        try
        {
        var cancellationToken = refreshCancellation.Token;
        var catalogChanged = false;
        var stateChanged = false;
        string[] addedGameIds = [];
        string[] shortcutRepairGameIds = [];
        string[] upsertGameIds = [];
        string[] removedGameIds = [];
        var resumedIncompleteCount = 0;
        UnifySteamStoreRefreshResult? refreshResult = null;
        var acceptedRefresh = false;

        _unifyRefreshGate.Wait(cancellationToken);
        try
        {
            var configuration = _settingsStore.Load();
            if (!StorefrontFeatureFlags.Enabled ||
                string.IsNullOrWhiteSpace(normalizedStoreId) ||
                !configuration.UnifySteam.Stores.TryGetValue(normalizedStoreId, out var store) ||
                store is null ||
                !store.Enabled)
            {
                return new OmniLibraryStoreCheckResult(
                    normalizedStoreId,
                    Checked: false,
                    Succeeded: true,
                    CatalogChanged: false,
                    StateChanged: false,
                    "Store is disabled.");
            }

            var beforeCatalogSignature = UnifySteamService.GetRemoteCatalogSignature(store);
            var beforeStateSignature = UnifySteamService.ComputeLibraryStateSignature(store);
            var refreshInputSignature = ComputeUnifySteamRefreshInputSignature(store);
            var beforeGameIds = (store.Cache?.Games ?? [])
                .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
                .Select(game => game.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var beforeGameFingerprints = GetOmniLibraryGameFingerprints(store);
            var refresh = _unifySteamService.RefreshLibraries(
                configuration,
                normalizedStoreId,
                skipUnconfigured: true,
                lightweight: true,
                quiet: true,
                cancellationToken: cancellationToken);
            refreshResult = refresh.Stores.FirstOrDefault();
            if (refreshResult is null)
            {
                return new OmniLibraryStoreCheckResult(
                    normalizedStoreId,
                    Checked: false,
                    Succeeded: true,
                    CatalogChanged: false,
                    StateChanged: false,
                    "Store is not connected yet.");
            }

            var afterCatalogSignature = UnifySteamService.GetRemoteCatalogSignature(store);
            var afterStateSignature = UnifySteamService.ComputeLibraryStateSignature(store);
            var afterGameIds = (store.Cache?.Games ?? [])
                .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
                .Select(game => game.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var delta = ComputeOmniLibraryCatalogDelta(beforeGameIds, afterGameIds);
            addedGameIds = delta.AddedGameIds.ToArray();
            removedGameIds = delta.RemovedGameIds.ToArray();
            var changedGameIds = ComputeOmniLibraryChangedGameIds(
                beforeGameFingerprints,
                store);
            shortcutRepairGameIds = GetOmniLibraryDuplicateSteamAppIdGameIds(store);
            upsertGameIds = addedGameIds
                .Concat(changedGameIds)
                .Concat(shortcutRepairGameIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (refreshResult.Succeeded &&
                upsertGameIds.Length == 0 &&
                removedGameIds.Length == 0 &&
                ShouldResumeIncompleteOmniLibraryPreparation(store) &&
                CanRetryOmniLibraryArtwork(normalizedStoreId))
            {
                upsertGameIds = GetOmniLibraryIncompleteGameIds(
                    configuration,
                    normalizedStoreId);
                resumedIncompleteCount = upsertGameIds.Length;
            }
            catalogChanged =
                refreshResult.Succeeded &&
                (addedGameIds.Length > 0 ||
                 removedGameIds.Length > 0 ||
                 !string.Equals(
                     beforeCatalogSignature,
                     afterCatalogSignature,
                     StringComparison.OrdinalIgnoreCase));
            stateChanged = !string.Equals(
                beforeStateSignature,
                afterStateSignature,
                StringComparison.OrdinalIgnoreCase);
            if (stateChanged)
            {
                _settingsStore.Update(latest =>
                {
                    var latestStore = GetUnifySteamStoreConfiguration(latest, normalizedStoreId);
                    if (!latestStore.Enabled ||
                        !string.Equals(
                            refreshInputSignature,
                            ComputeUnifySteamRefreshInputSignature(latestStore),
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    MergeUnifySteamRuntimeState(latestStore, store);
                    acceptedRefresh = true;
                });
            }
            else
            {
                acceptedRefresh = true;
            }
        }
        finally
        {
            _unifyRefreshGate.Release();
        }

        if (!acceptedRefresh)
        {
            return new OmniLibraryStoreCheckResult(
                normalizedStoreId,
                Checked: true,
                Succeeded: true,
                CatalogChanged: false,
                StateChanged: false,
                "Store settings changed during refresh; the next quiet check will use the new settings.");
        }

        if (refreshResult!.Succeeded &&
            (upsertGameIds.Length > 0 || removedGameIds.Length > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _journal.Append(
                "info",
                "omnilibrary-auto-check",
                shortcutRepairGameIds.Length > 0
                    ? $"{normalizedStoreId} shortcut identity repair detected."
                    : resumedIncompleteCount > 0
                        ? $"{normalizedStoreId} incomplete preparation resumed."
                    : $"{normalizedStoreId} library changes detected.",
                $"Preparing {addedGameIds.Length} added, {removedGameIds.Length} removed, " +
                $"{shortcutRepairGameIds.Length} shortcut repair, and {resumedIncompleteCount} recovery title(s) in the background.");
            QueueOmniLibraryShortcutSync(
                normalizedStoreId,
                upsertGameIds,
                removedGameIds);
        }

        return new OmniLibraryStoreCheckResult(
            normalizedStoreId,
            Checked: true,
            Succeeded: refreshResult.Succeeded,
            catalogChanged,
            stateChanged,
            refreshResult.Succeeded
                ? shortcutRepairGameIds.Length > 0
                    ? $"Shortcut identity repair queued for {shortcutRepairGameIds.Length} title(s)."
                    : resumedIncompleteCount > 0
                        ? $"Automatic preparation recovery queued for {resumedIncompleteCount} title(s)."
                    : catalogChanged
                    ? addedGameIds.Length > 0 || removedGameIds.Length > 0
                        ? $"Library delta queued: {addedGameIds.Length} added, {removedGameIds.Length} removed."
                        : "Store source membership changed; no Steam shortcut changes are required."
                    : stateChanged
                        ? "Local store state updated; catalog membership is unchanged."
                        : "Catalog membership is unchanged."
                : refreshResult.Error);
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
            return new OmniLibraryStoreCheckResult(
                normalizedStoreId,
                Checked: false,
                Succeeded: true,
                CatalogChanged: false,
                StateChanged: false,
                "Store or OmniLibrary was disabled during the background check.");
        }
        finally
        {
            EndOmniLibraryRefresh(normalizedStoreId, refreshCancellation);
        }
    }

    private static Dictionary<string, string> GetOmniLibraryGameFingerprints(
        UnifySteamStoreConfiguration store)
    {
        return (store.Cache?.Games ?? [])
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var game = group.First();
                    return string.Join(
                        '\u001f',
                        game.Title,
                        game.Installed,
                        game.CloudPlayable,
                        game.InstallPath,
                        game.ExecutablePath,
                        game.Version,
                        game.ImageUrl,
                        game.PreparationSignature,
                        game.PlatformId,
                        game.PlatformTitle,
                        game.RomPath,
                        game.SteamAppId);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> ComputeOmniLibraryChangedGameIds(
        UnifySteamStoreConfiguration before,
        UnifySteamStoreConfiguration after)
    {
        return ComputeOmniLibraryChangedGameIds(
            GetOmniLibraryGameFingerprints(before),
            after);
    }

    private static IReadOnlyList<string> ComputeOmniLibraryChangedGameIds(
        IReadOnlyDictionary<string, string> beforeGameFingerprints,
        UnifySteamStoreConfiguration after)
    {
        return GetOmniLibraryGameFingerprints(after)
            .Where(pair =>
                beforeGameFingerprints.TryGetValue(pair.Key, out var beforeFingerprint) &&
                !string.Equals(beforeFingerprint, pair.Value, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ComputeUnifySteamRefreshInputSignature(
        UnifySteamStoreConfiguration store)
    {
        return string.Join(
            '\u001f',
            store.Enabled,
            store.IncludeXboxPcGamePass,
            store.IncludeXboxCloudGaming,
            store.ToolPath,
            store.AuthPath,
            store.InstallPath,
            string.Join(
                '\u001e',
                (store.RomSystems ?? [])
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}:{pair.Value?.EmulatorPath}:{pair.Value?.Fullscreen}")));
    }

    private static void MergeUnifySteamRuntimeState(
        UnifySteamStoreConfiguration target,
        UnifySteamStoreConfiguration source)
    {
        target.ToolPath = string.IsNullOrWhiteSpace(source.ToolPath)
            ? target.ToolPath
            : source.ToolPath;
        target.AuthPath = string.IsNullOrWhiteSpace(source.AuthPath)
            ? target.AuthPath
            : source.AuthPath;
        target.RemoteCatalogSignature = source.RemoteCatalogSignature;
        target.RemoteCatalogItemIds = source.RemoteCatalogItemIds.ToList();
        target.Lifecycle = source.Lifecycle;
        target.Cache = source.Cache;
    }

    private StoreSyncConfiguration PersistSyncRuntimeConfiguration(
        StoreSyncConfiguration completed,
        bool omniLibraryOnly,
        string? omniLibraryStoreId,
        bool updateStoreSyncStatus,
        DateTimeOffset startedAt)
    {
        return _settingsStore.Update(latest =>
        {
            // These sections are owned by the sync pipeline. User-controlled switches,
            // paths and credentials stay on the latest on-disk configuration.
            foreach (var (titleId, latestArtwork) in latest.ArtworkMatchCache)
            {
                if (latestArtwork is null || latestArtwork.UpdatedAtUtc <= startedAt)
                {
                    continue;
                }

                completed.ArtworkMatchCache[titleId] = latestArtwork;
                if (latest.Manifest.TryGetValue(titleId, out var latestManifest) &&
                    latestManifest is not null &&
                    completed.Manifest.TryGetValue(titleId, out var completedManifest) &&
                    completedManifest is not null)
                {
                    completedManifest.SteamGridDbGameId = latestManifest.SteamGridDbGameId;
                }
            }

            latest.Manifest = completed.Manifest;
            latest.ArtworkMatchCache = completed.ArtworkMatchCache;
            if (updateStoreSyncStatus)
            {
                latest.LastSync = completed.LastSync;
            }

            if (!omniLibraryOnly)
            {
                return;
            }

            IEnumerable<string> storeIds = string.IsNullOrWhiteSpace(omniLibraryStoreId)
                ? completed.UnifySteam.Stores.Keys
                : new[] { omniLibraryStoreId.Trim() };
            foreach (var storeId in storeIds)
            {
                if (!completed.UnifySteam.Stores.TryGetValue(storeId, out var completedStore) ||
                    completedStore?.Cache?.Games is null ||
                    !latest.UnifySteam.Stores.TryGetValue(storeId, out var latestStore) ||
                    latestStore?.Cache?.Games is null)
                {
                    continue;
                }

                var completedById = completedStore.Cache.Games
                    .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
                    .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var latestGame in latestStore.Cache.Games)
                {
                    if (latestGame is not null &&
                        completedById.TryGetValue(latestGame.Id, out var completedGame) &&
                        completedGame.SteamAppId != 0)
                    {
                        latestGame.SteamAppId = completedGame.SteamAppId;
                    }
                }
            }
        });
    }

    public IReadOnlyList<StoreSyncDetectedTitleState> GetDetectedTitlesByStore(string storeId)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            var definition = StoreDefinitions.FirstOrDefault(item =>
                string.Equals(item.Id, storeId, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                return [];
            }

            var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
            if (!storeConfiguration.Enabled)
            {
                return [];
            }

            var storeSnapshots = BuildStoreSnapshots(configuration);
            var analysis = BuildSyncAnalysis(
                configuration,
                storeSnapshots,
                LoadExistingShortcuts(ResolveSteamProfile()));

            return analysis.Items
                .Where(item => string.Equals(item.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
                .Select(ToDetectedTitleState)
                .ToArray();
        }
    }

    public IReadOnlyList<StoreSyncDetectedTitleState> GetDetectedTitles()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            var storeSnapshots = BuildStoreSnapshots(configuration);
            var analysis = BuildSyncAnalysis(
                configuration,
                storeSnapshots,
                LoadExistingShortcuts(ResolveSteamProfile()));

            return analysis.Items
                .Select(ToDetectedTitleState)
                .OrderBy(title => title.StoreTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(title => title.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    internal StoreSyncManagedGameMatch? TryMatchManagedGame(
        string executablePath,
        string? processName = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) && string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        lock (_gate)
        {
            var normalizedExecutablePath = NormalizePath(executablePath);
            var configuration = _settingsStore.Load();
            var candidates = configuration.Manifest.Values
                .Where(entry => entry is not null && IsManifestLifecycleManaged(entry))
                .Select(entry => new
                {
                    Entry = entry,
                    ExecutablePath = NormalizePath(entry.ExecutablePath),
                })
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExecutablePath))
                .Select(candidate => new
                {
                    candidate.Entry,
                    candidate.ExecutablePath,
                    MatchScore = GetManagedGamePathMatchScore(
                        normalizedExecutablePath,
                        candidate.ExecutablePath,
                        processName),
                })
                .Where(candidate => candidate.MatchScore < int.MaxValue)
                .OrderBy(candidate => candidate.MatchScore)
                .ThenByDescending(candidate => candidate.Entry.LastSeenAtUtc)
                .ToArray();

            var match = candidates.FirstOrDefault();
            if (match is null)
            {
                return null;
            }

            // A protected Xbox process may only expose its process name to the
            // elevated controller helper. Never guess when multiple managed
            // games use the same executable name (for example "Game.exe").
            if (match.MatchScore >= 10 &&
                candidates.Count(candidate => candidate.MatchScore == match.MatchScore) != 1)
            {
                return null;
            }

            return new StoreSyncManagedGameMatch(
                match.Entry.TitleId,
                match.Entry.StoreId,
                FirstNonEmpty(match.Entry.EffectiveTitle, match.Entry.Title) ?? match.Entry.TitleId,
                match.ExecutablePath);
        }
    }

    private static int GetManagedGamePathMatchScore(
        string runningExecutablePath,
        string managedExecutablePath,
        string? processName)
    {
        if (!string.IsNullOrWhiteSpace(runningExecutablePath) &&
            string.Equals(runningExecutablePath, managedExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(runningExecutablePath))
        {
            try
            {
                var managedDirectory = Path.GetDirectoryName(managedExecutablePath);
                if (!string.IsNullOrWhiteSpace(managedDirectory))
                {
                    var normalizedDirectory = Path.GetFullPath(managedDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (Path.GetFullPath(runningExecutablePath)
                        .StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return 1;
                    }
                }
            }
            catch
            {
            }
        }

        var managedProcessName = Path.GetFileNameWithoutExtension(managedExecutablePath);
        return !string.IsNullOrWhiteSpace(processName) &&
               string.Equals(processName.Trim(), managedProcessName, StringComparison.OrdinalIgnoreCase)
            ? 10
            : int.MaxValue;
    }

    public async Task<StoreSyncArtworkPreviewState> GetArtworkPreviewAsync(string titleId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            throw new InvalidOperationException("A title ID is required.");
        }

        StoreSyncConfiguration configuration;
        StoreSyncAnalysisItem? item;
        SteamProfileInfo? profile;

        lock (_gate)
        {
            configuration = _settingsStore.Load();
            profile = ResolveSteamProfile();
            var storeSnapshots = BuildStoreSnapshots(configuration);
            var analysis = BuildSyncAnalysis(
                configuration,
                storeSnapshots,
                LoadExistingShortcuts(profile));
            item = analysis.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.TitleId, titleId, StringComparison.OrdinalIgnoreCase));
        }

        if (item is null)
        {
            return new StoreSyncArtworkPreviewState(
                titleId,
                Available: false,
                UsesCurrentArtwork: false,
                ImageDataUri: string.Empty,
                SourceLabel: "Artwork Preview",
                Message: "No detected game is available for this preview.");
        }

        if (profile is not null &&
            TryBuildLocalArtworkPreview(profile, item, out var localPreview) &&
            localPreview is not null)
        {
            return localPreview;
        }

        var apiKey = _artworkDownloader.GetEffectiveApiKey(configuration.SteamGridDbApiKey);
        var remotePreview = await _artworkDownloader.ResolvePreviewAsync(
            item.EffectiveArtworkTitle,
            new[] { item.Game.Title, item.EffectiveTitle, item.Game.ExecutablePath, item.Game.StartDirectory },
            apiKey,
            item.ArtworkCache?.GameId,
            item.ArtworkCache?.MatchName,
            cancellationToken);

        if (remotePreview is null)
        {
            return new StoreSyncArtworkPreviewState(
                titleId,
                Available: false,
                UsesCurrentArtwork: false,
                ImageDataUri: string.Empty,
                SourceLabel: "SteamGridDB Preview",
                Message: $"No SteamGridDB image preview is available for {item.EffectiveArtworkTitle} yet.");
        }

        PersistArtworkPreviewMatch(titleId, remotePreview);

        return new StoreSyncArtworkPreviewState(
            titleId,
            Available: true,
            UsesCurrentArtwork: false,
            ImageDataUri: remotePreview.DataUri,
            SourceLabel: $"SteamGridDB match: {remotePreview.MatchName}",
            Message: "This is the current SteamGridDB capsule that Tools for Steam will use for this title.");
    }

    public StoreSyncSnapshot ToggleSetting(string key)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                switch (key)
                {
                    case "download-artwork":
                        latest.DownloadArtwork = !latest.DownloadArtwork;
                        break;
                    case "prefer-animated-artwork":
                        latest.PreferAnimatedArtwork = !latest.PreferAnimatedArtwork;
                        break;
                    case "close-steam-before-sync":
                        latest.CloseSteamBeforeSync = !latest.CloseSteamBeforeSync;
                        break;
                    case "backup-shortcuts":
                        latest.BackupShortcuts = !latest.BackupShortcuts;
                        break;
                    case "launch-big-picture-after-sync":
                        latest.LaunchBigPictureAfterSync = !latest.LaunchBigPictureAfterSync;
                        break;
                    case "take-over-existing-shortcuts":
                        latest.TakeOverExistingShortcuts = !latest.TakeOverExistingShortcuts;
                        break;
                    case "cleanup-missing-titles":
                        latest.CleanupMissingTitles = !latest.CleanupMissingTitles;
                        break;
                    default:
                        throw new InvalidOperationException("Unknown Store Sync setting.");
                }
            });
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetSteamGridDbApiKey(string value)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
                latest.SteamGridDbApiKey = value.Trim());
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetStoreEnabled(string storeId, bool enabled)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
                GetStoreConfiguration(latest, storeId).Enabled = enabled);
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetUnifySteamStoreEnabled(string storeId, bool enabled)
    {
        var descriptor = OmniLibraryStoreRegistry.GetRequired(storeId);
        storeId = descriptor.Id;
        StoreSyncSnapshot snapshot;
        lock (_gate)
        {
            if (!StorefrontFeatureFlags.Enabled)
            {
                return BuildSnapshot(_settingsStore.Load());
            }

            var configuration = _settingsStore.Update(latest =>
            {
                var storeConfiguration = GetUnifySteamStoreConfiguration(latest, storeId);
                if (enabled && !storeConfiguration.Enabled)
                {
                    storeConfiguration.PreparedCatalogSignature = string.Empty;
                    storeConfiguration.PreparedAtUtc = null;
                }
                if (enabled && descriptor.Id.Equals(
                        OmniLibraryRomSystemRegistry.StoreId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Activation is the only point at which TFS creates the
                    // user's deterministic ROM directory structure.
                    storeConfiguration.InstallPath =
                        OmniLibraryRomSystemRegistry.EnsureFolderStructure(
                            storeConfiguration.InstallPath);
                }
                storeConfiguration.Enabled = enabled;
                OmniLibraryLifecycle.SetStage(
                    storeConfiguration,
                    "authentication",
                    enabled ? "idle" : "disabled");
            });
            snapshot = BuildSnapshot(configuration);
        }

        if (!enabled)
        {
            CancelOmniLibraryBackgroundWork(storeId);
        }

        if (enabled)
        {
            var refreshCancellation = BeginOmniLibraryRefresh(storeId);
            _ = Task.Run(() =>
            {
                try
                {
                    RefreshUnifySteamCore(
                        storeId,
                        includeIncompleteRepair: true,
                        refreshCancellation.Token);
                }
                catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _journal.Append(
                        "warning",
                        "omnilibrary",
                        $"{storeId} preparation could not start.",
                        exception.Message);
                }
                finally
                {
                    EndOmniLibraryRefresh(storeId, refreshCancellation);
                }
            });
        }

        return snapshot;
    }

    public void SetOmniLibraryPluginRuntimeEnabled(bool enabled)
    {
        if (!enabled)
        {
            CancelOmniLibraryBackgroundWork(storeId: null);
        }
    }

    public void SetStoreSyncPluginRuntimeEnabled(bool enabled)
    {
        if (enabled)
        {
            return;
        }

        CancellationTokenSource? cancellation = null;
        CancellationTokenSource? ownershipRepairCancellation = null;
        lock (_gate)
        {
            if (!_activeSyncIsOmniLibrary)
            {
                cancellation = _activeSyncCancellation;
            }
            if (!_activeOwnershipRepairIsOmniLibrary)
            {
                ownershipRepairCancellation = _activeOwnershipRepairCancellation;
            }
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            ownershipRepairCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelOmniLibraryBackgroundWork(string? storeId)
    {
        var normalizedStoreId = storeId?.Trim() ?? string.Empty;
        var cancelAll = string.IsNullOrWhiteSpace(normalizedStoreId);
        CancellationTokenSource? activeSyncCancellation = null;
        CancellationTokenSource? ownershipRepairCancellation = null;
        CancellationTokenSource[] artworkCancellations;
        CancellationTokenSource[] refreshCancellations;

        lock (_gate)
        {
            if (cancelAll)
            {
                _pendingAllOmniLibraryStoreSync = false;
                _pendingOmniLibraryStoreSyncs.Clear();
                _pendingOmniLibraryDeltas.Clear();
                _pendingOmniArtworkTargets.Clear();
                _omniArtworkRetryAtUtc.Clear();
            }
            else
            {
                _pendingOmniLibraryStoreSyncs.Remove(normalizedStoreId);
                _pendingOmniLibraryDeltas.Remove(normalizedStoreId);
                _pendingOmniArtworkTargets.Remove(normalizedStoreId);
                _omniArtworkRetryAtUtc.Remove(normalizedStoreId);
                // A broad queued run cannot safely exclude a store whose state
                // changed after it was queued. Drop it and let enabled stores'
                // next exact delta schedule fresh work.
                _pendingAllOmniLibraryStoreSync = false;
            }

            if (_activeSyncIsOmniLibrary &&
                (cancelAll ||
                 string.IsNullOrWhiteSpace(_activeSyncOmniLibraryStoreId) ||
                 _activeSyncOmniLibraryStoreId.Equals(
                     normalizedStoreId,
                     StringComparison.OrdinalIgnoreCase)))
            {
                activeSyncCancellation = _activeSyncCancellation;
            }
            if (_activeOwnershipRepairIsOmniLibrary &&
                (cancelAll ||
                 string.IsNullOrWhiteSpace(_activeOwnershipRepairOmniLibraryStoreId) ||
                 _activeOwnershipRepairOmniLibraryStoreId.Equals(
                     normalizedStoreId,
                     StringComparison.OrdinalIgnoreCase)))
            {
                ownershipRepairCancellation = _activeOwnershipRepairCancellation;
            }

            artworkCancellations = _activeOmniArtworkCancellations
                .Where(pair => cancelAll ||
                               pair.Key.Equals(normalizedStoreId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .Distinct()
                .ToArray();
            refreshCancellations = _activeOmniRefreshCancellations
                .Where(pair => cancelAll ||
                               pair.Key.Equals(normalizedStoreId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .Distinct()
                .ToArray();
        }

        try
        {
            activeSyncCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            ownershipRepairCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (var cancellation in artworkCancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        foreach (var cancellation in refreshCancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private CancellationTokenSource BeginOmniLibraryRefresh(string storeId)
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation = null;
        lock (_gate)
        {
            if (_activeOmniRefreshCancellations.TryGetValue(
                    storeId,
                    out previousCancellation))
            {
                _activeOmniRefreshCancellations.Remove(storeId);
            }
            _activeOmniRefreshCancellations[storeId] = cancellation;
        }

        try
        {
            previousCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        return cancellation;
    }

    private void EndOmniLibraryRefresh(
        string storeId,
        CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (_activeOmniRefreshCancellations.TryGetValue(
                    storeId,
                    out var activeCancellation) &&
                ReferenceEquals(activeCancellation, cancellation))
            {
                _activeOmniRefreshCancellations.Remove(storeId);
            }
        }
        cancellation.Dispose();
    }

    public StoreSyncSnapshot SetUnifySteamXboxSourceEnabled(string sourceId, bool enabled)
    {
        StoreSyncSnapshot snapshot;
        lock (_gate)
        {
            if (!StorefrontFeatureFlags.Enabled)
            {
                return BuildSnapshot(_settingsStore.Load());
            }

            var configuration = _settingsStore.Update(latest =>
            {
                var xbox = GetUnifySteamStoreConfiguration(latest, "xbox-game-pass");
                switch ((sourceId ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "pc-game-pass":
                        xbox.IncludeXboxPcGamePass = enabled;
                        break;
                    case "xcloud":
                        xbox.IncludeXboxCloudGaming = enabled;
                        break;
                    default:
                        throw new InvalidOperationException("Unknown Xbox library source.");
                }

                // Force a fresh source-membership query. The incremental refresh below
                // preserves already prepared shortcuts and calculates the exact delta.
                xbox.RemoteCatalogSignature = string.Empty;
                xbox.PreparationStatus = "catalog";
                OmniLibraryLifecycle.SetStage(xbox, "catalog", "updating");
                xbox.PreparationDetail = "Updating the selected Xbox library sources in the background.";
            });
            snapshot = BuildSnapshot(configuration);
        }

        const string xboxStoreId = "xbox-game-pass";
        var refreshCancellation = BeginOmniLibraryRefresh(xboxStoreId);
        _ = Task.Run(() =>
        {
            try
            {
                RefreshUnifySteamCore(
                    xboxStoreId,
                    includeIncompleteRepair: false,
                    refreshCancellation.Token);
            }
            catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _journal.Append(
                    "warning",
                    "omnilibrary",
                    "Xbox source refresh failed.",
                    exception.Message);
            }
            finally
            {
                EndOmniLibraryRefresh(xboxStoreId, refreshCancellation);
            }
        });
        return snapshot;
    }

    public StoreSyncSnapshot SetUnifySteamAchievementOptions(
        string storeId,
        bool enabled,
        string? credential)
    {
        var descriptor = OmniLibraryStoreRegistry.GetRequired(storeId);
        var provider = OmniLibraryGameDataProviderRegistry.ResolveForStore(
            descriptor.Id) ??
            throw new InvalidOperationException(
                "This OmniLibrary store has no game-data provider.");
        return SetOmniLibraryGameDataProviderOptions(
            provider.Id,
            enabled,
            credential,
            secondaryCredential: null,
            accountId: null,
            accountName: null,
            region: null,
            locale: null,
            dataPath: null);
    }

    public StoreSyncSnapshot SetOmniLibraryGameDataProviderOptions(
        string providerId,
        bool enabled,
        string? credential,
        string? secondaryCredential,
        string? accountId,
        string? accountName,
        string? region,
        string? locale,
        string? dataPath)
    {
        var descriptor = OmniLibraryGameDataProviderRegistry.GetRequired(providerId);
        providerId = descriptor.Id;
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                var gameData = latest.UnifySteam.GameData;
                var provider = gameData.Providers[providerId];
                provider.Enabled = enabled;
                if (credential is not null)
                {
                    provider.Credential = credential.Trim();
                }
                if (secondaryCredential is not null)
                {
                    provider.SecondaryCredential = secondaryCredential.Trim();
                }
                if (accountId is not null)
                {
                    provider.AccountId = accountId.Trim();
                }
                if (accountName is not null)
                {
                    provider.AccountName = accountName.Trim();
                }
                if (region is not null)
                {
                    provider.Region = region.Trim();
                }
                if (locale is not null)
                {
                    provider.Locale = locale.Trim();
                }
                if (dataPath is not null)
                {
                    provider.DataPath = dataPath.Trim();
                }
                provider.ConnectionStatus = string.Empty;
                provider.ConnectionDetail = string.Empty;
                provider.ConnectionCheckedAtUtc = null;
                provider.UpdatedAtUtc = DateTimeOffset.UtcNow;

                // Keep the old fields synchronized for downgrade safety and for
                // older frontend builds that may still call the store endpoint.
                if (providerId.Equals("xbox-live", StringComparison.OrdinalIgnoreCase))
                {
                    var xbox = GetUnifySteamStoreConfiguration(
                        latest,
                        "xbox-game-pass");
                    xbox.AchievementsEnabled = enabled;
                    var nextKey = provider.Credential;
                    if (!string.Equals(
                            xbox.OpenXblApiKey,
                            nextKey,
                            StringComparison.Ordinal))
                    {
                        xbox.OpenXblApiKey = nextKey;
                        xbox.OpenXblAccountId = string.Empty;
                        xbox.OpenXblAccountName = string.Empty;
                        xbox.OpenXblAccountCheckedAtUtc = null;
                        xbox.OpenXblTitleIds.Clear();
                        provider.AccountId = string.Empty;
                        provider.AccountName = string.Empty;
                        provider.GameIdOverrides.Clear();
                    }
                }
                else if (providerId.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
                {
                    GetUnifySteamStoreConfiguration(latest, "epic-games")
                        .AchievementsEnabled = enabled;
                }
                else if (providerId.Equals("gog", StringComparison.OrdinalIgnoreCase))
                {
                    GetUnifySteamStoreConfiguration(latest, "gog-galaxy")
                        .AchievementsEnabled = enabled;
                }
            });
            return BuildSnapshot(configuration);
        }
    }

    public async Task<StoreSyncSnapshot> TestOmniLibraryGameDataProviderAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var descriptor = OmniLibraryGameDataProviderRegistry.GetRequired(providerId);
        providerId = descriptor.Id;
        var configuration = _settingsStore.Load();
        if (!configuration.UnifySteam.GameData.Providers.TryGetValue(
                providerId,
                out var provider) ||
            provider is null)
        {
            throw new InvalidOperationException("The game-data provider is not configured.");
        }

        var status = "ready";
        var detail = "The saved provider configuration is ready.";
        var resolvedAccountName = provider.AccountName;
        try
        {
            if (!configuration.UnifySteam.GameData.Enabled || !provider.Enabled)
            {
                throw new InvalidOperationException(
                    "Enable Achievements & Metadata and this provider before testing it.");
            }

            if (providerId.Equals("retroachievements", StringComparison.OrdinalIgnoreCase))
            {
                var username = provider.AccountName?.Trim() ?? string.Empty;
                var apiKey = provider.Credential?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException(
                        "Enter a RetroAchievements username and Web API key first.");
                }

                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(12),
                };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "ToolsForSteam-OmniLibrary/0.4.1");
                var uri =
                    $"https://retroachievements.org/API/API_GetUserProfile.php?u={Uri.EscapeDataString(username)}&y={Uri.EscapeDataString(apiKey)}";
                using var response = await client.GetAsync(uri, cancellationToken)
                    .ConfigureAwait(false);
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var returnedUser = FirstNonEmpty(
                    GetJsonString(root, "User"),
                    GetJsonString(root, "user"));
                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(returnedUser))
                {
                    var providerError = FirstNonEmpty(
                        GetJsonString(root, "Error"),
                        GetJsonString(root, "error"));
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(providerError)
                            ? "RetroAchievements rejected the username or Web API key."
                            : providerError);
                }

                resolvedAccountName = returnedUser;
                detail = $"Connected as {returnedUser}. ROM hashes, artwork, and achievement progress are available on demand.";
            }
            else if (descriptor.Supports(OmniLibraryGameDataCapabilities.StoreAccount))
            {
                var connectedStore = configuration.UnifySteam.Stores
                    .Where(pair => descriptor.StoreIds.Contains(
                        pair.Key,
                        StringComparer.OrdinalIgnoreCase))
                    .Select(pair => pair.Value)
                    .FirstOrDefault(store => store?.Cache is not null &&
                        string.IsNullOrWhiteSpace(store.Cache.LastError));
                if (connectedStore?.Cache is null)
                {
                    throw new InvalidOperationException(
                        $"Connect the matching {descriptor.Title} library first.");
                }
                resolvedAccountName = FirstNonEmpty(
                    connectedStore.Cache.AccountName,
                    provider.AccountName);
                detail = string.IsNullOrWhiteSpace(resolvedAccountName)
                    ? $"The connected {descriptor.Title} store session is ready."
                    : $"Connected as {resolvedAccountName}.";
            }
            else
            {
                var configured = !string.IsNullOrWhiteSpace(provider.Credential) ||
                    !string.IsNullOrWhiteSpace(provider.SecondaryCredential) ||
                    !string.IsNullOrWhiteSpace(provider.AccountId) ||
                    !string.IsNullOrWhiteSpace(provider.AccountName) ||
                    !string.IsNullOrWhiteSpace(provider.DataPath);
                if (!configured)
                {
                    throw new InvalidOperationException(
                        "Complete this provider's setup before testing it.");
                }
                detail = "The provider setup is complete. Its first matching title will perform the remote or local title-scoped check.";
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            status = "failed";
            detail = error.Message;
        }

        var checkedAtUtc = DateTimeOffset.UtcNow;
        var updated = _settingsStore.Update(latest =>
        {
            var target = latest.UnifySteam.GameData.Providers[providerId];
            target.ConnectionStatus = status;
            target.ConnectionDetail = detail;
            target.ConnectionCheckedAtUtc = checkedAtUtc;
            if (status == "ready" && !string.IsNullOrWhiteSpace(resolvedAccountName))
            {
                target.AccountName = resolvedAccountName.Trim();
            }
        });
        return BuildSnapshot(updated);
    }

    public StoreSyncSnapshot SetOmniLibraryGameDataEnabled(bool enabled)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                latest.UnifySteam.GameData.Enabled = enabled;
            });
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot RefreshUnifySteam(string? storeId)
    {
        return RefreshUnifySteamCore(storeId, includeIncompleteRepair: true);
    }

    private StoreSyncSnapshot RefreshUnifySteamCore(
        string? storeId,
        bool includeIncompleteRepair,
        CancellationToken cancellationToken = default)
    {
        var candidateDeltas = new Dictionary<string, OmniLibrarySyncDelta>(StringComparer.OrdinalIgnoreCase);
        var candidatePreparedStoresWithoutWork = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var acceptedStoreIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        StoreSyncConfiguration persistedConfiguration;

        _unifyRefreshGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = _settingsStore.Load();
            if (!StorefrontFeatureFlags.Enabled)
            {
                return BuildSnapshot(configuration);
            }

            var enabledStoreIds = _unifySteamService.GetEnabledStoreIds(configuration)
                .Where(id => string.IsNullOrWhiteSpace(storeId) ||
                             id.Equals(storeId.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var refreshInputSignatures = enabledStoreIds.ToDictionary(
                id => id,
                id => ComputeUnifySteamRefreshInputSignature(
                    GetUnifySteamStoreConfiguration(configuration, id)),
                StringComparer.OrdinalIgnoreCase);
            var beforeGameIdsByStore = enabledStoreIds.ToDictionary(
                id => id,
                id => (GetUnifySteamStoreConfiguration(configuration, id).Cache?.Games ?? [])
                    .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
                    .Select(game => game.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            var beforeGameFingerprintsByStore = enabledStoreIds.ToDictionary(
                id => id,
                id => GetOmniLibraryGameFingerprints(
                    GetUnifySteamStoreConfiguration(configuration, id)),
                StringComparer.OrdinalIgnoreCase);
            var refresh = _unifySteamService.RefreshLibraries(
                configuration,
                storeId,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var result in refresh.Stores.Where(result => result.Succeeded))
            {
                var refreshedStore = GetUnifySteamStoreConfiguration(configuration, result.StoreId);
                var afterGameIds = (refreshedStore.Cache?.Games ?? [])
                    .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
                    .Select(game => game.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                beforeGameIdsByStore.TryGetValue(
                    result.StoreId,
                    out var beforeGameIds);
                beforeGameIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var catalogDelta = ComputeOmniLibraryCatalogDelta(
                    beforeGameIds,
                    afterGameIds);
                beforeGameFingerprintsByStore.TryGetValue(
                    result.StoreId,
                    out var beforeGameFingerprints);
                beforeGameFingerprints ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var changedGameIds = ComputeOmniLibraryChangedGameIds(
                    beforeGameFingerprints,
                    refreshedStore);
                var shortcutRepairGameIds = GetOmniLibraryDuplicateSteamAppIdGameIds(refreshedStore);
                var addedGameIds = catalogDelta.AddedGameIds
                    .Concat(changedGameIds)
                    .Concat(shortcutRepairGameIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var removedGameIds = catalogDelta.RemovedGameIds.ToArray();
                if (includeIncompleteRepair &&
                    ShouldResumeIncompleteOmniLibraryPreparation(refreshedStore) &&
                    addedGameIds.Length == 0 &&
                    removedGameIds.Length == 0)
                {
                    // A broad incomplete-game scan is reserved for an interrupted
                    // first-time preparation. Established stores are exclusively
                    // maintained from their exact catalog membership delta.
                    addedGameIds = GetOmniLibraryIncompleteGameIds(
                        configuration,
                        result.StoreId);
                    if (addedGameIds.Length == 0 &&
                        (refreshedStore.Cache?.Games?.Count ?? 0) > 0)
                    {
                        candidatePreparedStoresWithoutWork.Add(result.StoreId);
                    }
                }

                var delta = CreateOmniLibrarySyncDelta(
                    result.StoreId,
                    addedGameIds,
                    removedGameIds);
                if (delta.UpsertGameIds.Count > 0 || delta.RemovedGameIds.Count > 0)
                {
                    candidateDeltas[result.StoreId] = delta;
                }
            }

            persistedConfiguration = _settingsStore.Update(latest =>
            {
                foreach (var result in refresh.Stores.Where(result => result.Succeeded))
                {
                    if (!configuration.UnifySteam.Stores.TryGetValue(result.StoreId, out var refreshedStore) ||
                        refreshedStore is null ||
                        !refreshInputSignatures.TryGetValue(result.StoreId, out var refreshInputSignature))
                    {
                        continue;
                    }

                    var latestStore = GetUnifySteamStoreConfiguration(latest, result.StoreId);
                    if (!latestStore.Enabled ||
                        !string.Equals(
                            refreshInputSignature,
                            ComputeUnifySteamRefreshInputSignature(latestStore),
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    MergeUnifySteamRuntimeState(latestStore, refreshedStore);
                    acceptedStoreIds.Add(result.StoreId);
                }
            });
        }
        finally
        {
            _unifyRefreshGate.Release();
        }

        foreach (var delta in candidateDeltas.Values.Where(delta => acceptedStoreIds.Contains(delta.StoreId)))
        {
            QueueOmniLibraryShortcutSync(
                delta.StoreId,
                delta.UpsertGameIds,
                delta.RemovedGameIds);
        }
        var preparedStoresWithoutWork = candidatePreparedStoresWithoutWork
            .Where(acceptedStoreIds.Contains)
            .ToArray();
        if (preparedStoresWithoutWork.Length > 0)
        {
            MarkOmniLibraryStoresPrepared(
                preparedStoresWithoutWork
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }

        return BuildSnapshot(persistedConfiguration);
    }

    private static bool ShouldResumeIncompleteOmniLibraryPreparation(
        UnifySteamStoreConfiguration store)
    {
        if (!store.PreparedAtUtc.HasValue)
        {
            return true;
        }

        return store.PreparationStatus.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
               store.PreparationStatus.Equals("artwork-pending", StringComparison.OrdinalIgnoreCase) ||
               store.Lifecycle?.Artwork.Equals(
                   "degraded",
                   StringComparison.OrdinalIgnoreCase) == true ||
               ((store.PreparationStatus.Equals("shortcuts", StringComparison.OrdinalIgnoreCase) ||
                  store.PreparationStatus.Equals("artwork", StringComparison.OrdinalIgnoreCase) ||
                  store.Lifecycle?.Artwork.Equals(
                      "updating",
                      StringComparison.OrdinalIgnoreCase) == true) &&
                 store.PreparationTotalCount > 0 &&
                store.PreparationCompletedCount < store.PreparationTotalCount);
    }

    private bool CanRetryOmniLibraryArtwork(string storeId)
    {
        lock (_gate)
        {
            return !_omniArtworkRetryAtUtc.TryGetValue(storeId, out var retryAtUtc) ||
                   DateTimeOffset.UtcNow >= retryAtUtc;
        }
    }

    private string[] GetOmniLibraryIncompleteGameIds(
        StoreSyncConfiguration configuration,
        string storeId)
    {
        var store = GetUnifySteamStoreConfiguration(configuration, storeId);
        var profile = ResolveSteamProfile();
        var gridDirectory = profile is null ? string.Empty : BuildGridDirectory(profile);
        return (store.Cache?.Games ?? [])
            .Where(game =>
                game is not null &&
                !string.IsNullOrWhiteSpace(game.Id) &&
                (game.SteamAppId == 0 ||
                 (!string.IsNullOrWhiteSpace(gridDirectory) &&
                  !SteamGridDbArtworkDownloader.HasCompleteArtworkSet(
                      gridDirectory,
                      game.SteamAppId))))
            .Select(game => game.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] GetOmniLibraryDuplicateSteamAppIdGameIds(
        UnifySteamStoreConfiguration store)
    {
        return (store.Cache?.Games ?? [])
            .Where(game =>
                game is not null &&
                game.SteamAppId != 0 &&
                !string.IsNullOrWhiteSpace(game.Id))
            .GroupBy(game => game.SteamAppId)
            .Select(group => group
                .Select(game => game.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .Where(gameIds => gameIds.Length > 1)
            .SelectMany(gameIds => gameIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public StoreSyncSnapshot SetUnifySteamInstallPath(string storeId, string? value)
    {
        if (!OmniLibraryStoreRegistry.TryGet(storeId, out var descriptor) ||
            !descriptor.Supports(OmniLibraryStoreCapabilities.InstallPath))
        {
            throw new InvalidOperationException("This OmniLibrary store does not expose an installation path.");
        }

        lock (_gate)
        {
            var installPath = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                installPath = string.Empty;
            }
            else
            {
                installPath = Path.GetFullPath(value.Trim());
                if (descriptor.Id.Equals(
                        OmniLibraryRomSystemRegistry.StoreId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    installPath = OmniLibraryRomSystemRegistry.EnsureFolderStructure(
                        installPath);
                }
                else if (!Directory.Exists(installPath))
                {
                    throw new InvalidOperationException("The selected store library folder does not exist.");
                }
            }

            if (descriptor.Id.Equals(
                    OmniLibraryRomSystemRegistry.StoreId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(installPath))
            {
                installPath = OmniLibraryRomSystemRegistry.DefaultRootPath;
            }

            var configuration = _settingsStore.Update(latest =>
                GetUnifySteamStoreConfiguration(latest, storeId).InstallPath = installPath);
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot OpenUnifySteamLibraryFolder(string storeId)
    {
        var descriptor = OmniLibraryStoreRegistry.GetRequired(storeId);
        if (!descriptor.Id.Equals(
                OmniLibraryRomSystemRegistry.StoreId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Opening the library folder is available only for local libraries.");
        }

        StoreSyncSnapshot snapshot;
        string root;
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                var store = GetUnifySteamStoreConfiguration(latest, descriptor.Id);
                store.InstallPath = OmniLibraryRomSystemRegistry.EnsureFolderStructure(
                    store.InstallPath);
            });
            root = GetUnifySteamStoreConfiguration(configuration, descriptor.Id).InstallPath;
            snapshot = BuildSnapshot(configuration);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { root },
            UseShellExecute = false,
        })?.Dispose();
        return snapshot;
    }

    public StoreSyncSnapshot OpenOmniLibraryRomSystemFolder(string systemId)
    {
        var system = OmniLibraryRomSystemRegistry.GetRequired(systemId);
        StoreSyncSnapshot snapshot;
        string folder;
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                var store = GetUnifySteamStoreConfiguration(
                    latest,
                    OmniLibraryRomSystemRegistry.StoreId);
                store.InstallPath = OmniLibraryRomSystemRegistry.EnsureFolderStructure(
                    store.InstallPath);
            });
            var store = GetUnifySteamStoreConfiguration(
                configuration,
                OmniLibraryRomSystemRegistry.StoreId);
            folder = Path.Combine(store.InstallPath, system.FolderName);
            snapshot = BuildSnapshot(configuration);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { folder },
            UseShellExecute = false,
        })?.Dispose();
        return snapshot;
    }

    public StoreSyncSnapshot SetUnifySteamToolPath(string storeId, string? value)
    {
        var descriptor = OmniLibraryStoreRegistry.GetRequired(storeId);
        if (!descriptor.Id.Equals(
                OmniLibraryRomSystemRegistry.StoreId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A custom tool path is available only for the Emulator library.");
        }

        var toolPath = value?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(toolPath))
        {
            toolPath = Path.GetFullPath(toolPath);
            if (!File.Exists(toolPath) ||
                !Path.GetFileName(toolPath).Equals(
                    "PPSSPPWindows64.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Select the 64-bit PPSSPP executable (PPSSPPWindows64.exe).");
            }
        }

        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                var store = GetUnifySteamStoreConfiguration(latest, descriptor.Id);
                store.ToolPath = toolPath;
                store.RomSystems.TryGetValue("psp", out var pspSettings);
                pspSettings ??= new OmniLibraryRomSystemConfiguration();
                pspSettings.EmulatorPath = toolPath;
                store.RomSystems["psp"] = pspSettings;
            });
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetOmniLibraryRomSystemSettings(
        string systemId,
        string? emulatorPathValue,
        bool fullscreen)
    {
        var system = OmniLibraryRomSystemRegistry.GetRequired(systemId);
        var emulatorPath = emulatorPathValue?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(emulatorPath))
        {
            emulatorPath = Path.GetFullPath(emulatorPath);
            if (!File.Exists(emulatorPath) ||
                !Path.GetFileName(emulatorPath).Equals(
                    system.EmulatorExecutableName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Select {system.EmulatorExecutableName} for {system.Title}.");
            }
        }

        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                var store = GetUnifySteamStoreConfiguration(
                    latest,
                    OmniLibraryRomSystemRegistry.StoreId);
                store.RomSystems.TryGetValue(system.Id, out var settings);
                settings ??= new OmniLibraryRomSystemConfiguration();
                settings.EmulatorPath = emulatorPath;
                settings.Fullscreen = fullscreen;
                store.RomSystems[system.Id] = settings;
                if (system.Id.Equals("psp", StringComparison.OrdinalIgnoreCase))
                {
                    store.ToolPath = emulatorPath;
                }
            });
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetUnifySteamDownloadOptions(
        string storeId,
        int downloadWorkers,
        int downloadTimeoutSeconds)
    {
        var descriptor = OmniLibraryStoreRegistry.GetRequired(storeId);
        if (!descriptor.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase) &&
            !descriptor.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Download tuning is available only for managed Epic and GOG downloads.");
        }

        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                var store = GetUnifySteamStoreConfiguration(latest, descriptor.Id);
                store.DownloadWorkers = Math.Clamp(downloadWorkers, 1, 32);
                store.DownloadTimeoutSeconds = Math.Clamp(
                    downloadTimeoutSeconds,
                    15,
                    300);
            });
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetUnifySteamGogOptions(
        bool includeDlc,
        bool preferGalaxyForLaunch)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Update(latest =>
            {
                var store = GetUnifySteamStoreConfiguration(
                    latest,
                    "gog-galaxy");
                var dlcChanged = store.IncludeGogDlc != includeDlc;
                store.IncludeGogDlc = includeDlc;
                store.PreferGogGalaxyForLaunch = preferGalaxyForLaunch;
                if (dlcChanged)
                {
                    // Re-evaluate installed build/DLC metadata soon without
                    // invalidating ownership, shortcuts, or artwork.
                    store.InstalledMetadataCheckedAtUtc = null;
                }
            });
            return BuildSnapshot(configuration);
        }
    }

    private void QueueOmniLibraryShortcutSync(
        string? storeId,
        IReadOnlyCollection<string>? addedGameIds = null,
        IReadOnlyCollection<string>? removedGameIds = null)
    {
        var normalizedStoreId = storeId?.Trim() ?? string.Empty;
        var delta = !string.IsNullOrWhiteSpace(normalizedStoreId) &&
                    (addedGameIds is not null || removedGameIds is not null)
            ? CreateOmniLibrarySyncDelta(normalizedStoreId, addedGameIds, removedGameIds)
            : null;
        if (delta is not null &&
            delta.UpsertGameIds.Count == 0 &&
            delta.RemovedGameIds.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_activeSyncTask is { IsCompleted: false })
            {
                if (string.IsNullOrWhiteSpace(normalizedStoreId))
                {
                    _pendingAllOmniLibraryStoreSync = true;
                    _pendingOmniLibraryStoreSyncs.Clear();
                    _pendingOmniLibraryDeltas.Clear();
                }
                else if (!_pendingAllOmniLibraryStoreSync && delta is null)
                {
                    _pendingOmniLibraryStoreSyncs.Add(normalizedStoreId);
                    _pendingOmniLibraryDeltas.Remove(normalizedStoreId);
                }
                else if (!_pendingAllOmniLibraryStoreSync &&
                         !_pendingOmniLibraryStoreSyncs.Contains(normalizedStoreId) &&
                         delta is not null)
                {
                    MergePendingOmniLibraryDelta(delta);
                }
                _journal.Append(
                    "debug",
                    "omnilibrary",
                    "OmniLibrary shortcut preparation was queued behind the active sync.");
                return;
            }

            var startedAt = DateTimeOffset.UtcNow;
            _journal.Append(
                "info",
                "omnilibrary",
                delta is null
                    ? "OmniLibrary shortcut sync queued."
                    : $"OmniLibrary delta queued: {delta.UpsertGameIds.Count} upsert, {delta.RemovedGameIds.Count} removal.");
            MarkOmniLibraryShortcutPreparationQueued(
                normalizedStoreId,
                delta);
            var cancellation = BeginActiveSyncLocked(
                omniLibraryOnly: true,
                normalizedStoreId);
            _activeSyncTask = Task.Run(() => RunSyncInBackgroundAsync(
                startedAt,
                launchSteamWhenFinished: false,
                allowSteamRestart: false,
                triggerSource: string.IsNullOrWhiteSpace(normalizedStoreId)
                    ? "omnilibrary"
                    : $"omnilibrary-{normalizedStoreId}",
                omniLibraryOnly: true,
                omniLibraryStoreId: normalizedStoreId,
                omniLibraryDelta: delta,
                updateStoreSyncStatus: false,
                cancellationToken: cancellation.Token));
        }
    }

    private CancellationTokenSource BeginActiveSyncLocked(
        bool omniLibraryOnly,
        string? omniLibraryStoreId)
    {
        _activeSyncCancellation?.Dispose();
        _activeSyncCancellation = new CancellationTokenSource();
        _activeSyncIsOmniLibrary = omniLibraryOnly;
        _activeSyncOmniLibraryStoreId = omniLibraryOnly
            ? omniLibraryStoreId?.Trim() ?? string.Empty
            : string.Empty;
        return _activeSyncCancellation;
    }

    private static OmniLibrarySyncDelta CreateOmniLibrarySyncDelta(
        string storeId,
        IReadOnlyCollection<string>? addedGameIds,
        IReadOnlyCollection<string>? removedGameIds)
    {
        var upserts = (addedGameIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removals = (removedGameIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        upserts.ExceptWith(removals);
        return new OmniLibrarySyncDelta(storeId, upserts, removals);
    }

    private void MergePendingOmniLibraryDelta(OmniLibrarySyncDelta delta)
    {
        if (!_pendingOmniLibraryDeltas.TryGetValue(delta.StoreId, out var pending))
        {
            _pendingOmniLibraryDeltas[delta.StoreId] = new OmniLibrarySyncDelta(
                delta.StoreId,
                new HashSet<string>(delta.UpsertGameIds, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(delta.RemovedGameIds, StringComparer.OrdinalIgnoreCase));
            return;
        }

        foreach (var gameId in delta.UpsertGameIds)
        {
            pending.RemovedGameIds.Remove(gameId);
            pending.UpsertGameIds.Add(gameId);
        }
        foreach (var gameId in delta.RemovedGameIds)
        {
            pending.UpsertGameIds.Remove(gameId);
            pending.RemovedGameIds.Add(gameId);
        }
    }

    private void MarkOmniLibraryShortcutPreparationQueued(
        string? requestedStoreId,
        OmniLibrarySyncDelta? delta)
    {
        _settingsStore.Update(configuration =>
        {
            foreach (var (storeId, store) in configuration.UnifySteam.Stores)
            {
                if (store is null ||
                    !store.Enabled ||
                    (!string.IsNullOrWhiteSpace(requestedStoreId) &&
                     !storeId.Equals(requestedStoreId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                store.PreparationStatus = "shortcuts";
                OmniLibraryLifecycle.SetStage(store, "shortcuts", "updating");
                store.PreparationDetail = delta is null
                    ? "Preparing Steam shortcuts in the background. The store tab stays hidden."
                    : $"Preparing {delta.UpsertGameIds.Count} added/changed and " +
                      $"{delta.RemovedGameIds.Count} removed title(s) in the background.";
                store.PreparationCompletedCount = 0;
                store.PreparationTotalCount = delta is null
                    ? store.Cache?.Games?.Count ?? 0
                    : delta.UpsertGameIds.Count + delta.RemovedGameIds.Count;
            }
        });
    }

    public StoreSyncSnapshot StartUnifySteamLogin(string storeId)
    {
        _unifyRefreshGate.Wait();
        try
        {
            var configuration = _settingsStore.Load();
            if (!StorefrontFeatureFlags.Enabled)
            {
                return BuildSnapshot(configuration);
            }

            if (!GetUnifySteamStoreConfiguration(configuration, storeId).Enabled)
            {
                throw new InvalidOperationException("Enable this OmniLibrary store before signing in.");
            }

            _unifySteamService.StartLogin(configuration, storeId);
            var source = GetUnifySteamStoreConfiguration(configuration, storeId);
            var persisted = _settingsStore.Update(latest =>
            {
                var target = GetUnifySteamStoreConfiguration(latest, storeId);
                if (!target.Enabled)
                {
                    return;
                }
                target.ToolPath = source.ToolPath;
                target.AuthPath = source.AuthPath;
                target.Cache = source.Cache;
            });
            return BuildSnapshot(persisted);
        }
        finally
        {
            _unifyRefreshGate.Release();
        }
    }

    public StoreSyncSnapshot CompleteUnifySteamManualAuth(string storeId, string value)
    {
        _unifyRefreshGate.Wait();
        try
        {
            var configuration = _settingsStore.Load();
            if (!StorefrontFeatureFlags.Enabled)
            {
                return BuildSnapshot(configuration);
            }

            _unifySteamService.CompleteManualCodeAuth(configuration, storeId, value);
            var source = GetUnifySteamStoreConfiguration(configuration, storeId);
            _settingsStore.Update(latest =>
            {
                var target = GetUnifySteamStoreConfiguration(latest, storeId);
                if (!target.Enabled)
                {
                    return;
                }
                target.ToolPath = source.ToolPath;
                target.AuthPath = source.AuthPath;
                target.Cache = source.Cache;
            });
        }
        finally
        {
            _unifyRefreshGate.Release();
        }

        // Load and prepare the library immediately so successful sign-in needs no
        // second button press. The refresh itself runs outside the central service lock.
        return RefreshUnifySteamCore(storeId, includeIncompleteRepair: true);
    }

    public StoreSyncSnapshot DisconnectUnifySteamStore(string storeId)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        if (!OmniLibraryStoreRegistry.TryGet(normalizedStoreId, out var descriptor) ||
            !descriptor.Supports(OmniLibraryStoreCapabilities.ManagedWebSignIn))
        {
            throw new InvalidOperationException("This OmniLibrary store does not use a removable TFS sign-in.");
        }

        var blockingOperations = UnifySteamDownloadStatusStore.GetAll()
            .Where(pair =>
                UnifySteamDownloadStatusStore.TryParseKey(
                    pair.Key,
                    out var operationStoreId,
                    out _) &&
                operationStoreId.Equals(
                    normalizedStoreId,
                    StringComparison.OrdinalIgnoreCase) &&
                UnifySteamDownloadStatusStore.BlocksStoreDisconnect(
                    pair.Value.Status))
            .Select(pair =>
                string.IsNullOrWhiteSpace(pair.Value.GameTitle)
                    ? pair.Key
                    : pair.Value.GameTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (blockingOperations.Length > 0)
        {
            throw new InvalidOperationException(
                $"{descriptor.Title} still has {blockingOperations.Length} active or resumable " +
                $"operation{(blockingOperations.Length == 1 ? string.Empty : "s")}. " +
                "Finish or cancel them in Download Center before disconnecting the account.");
        }

        IReadOnlyList<string> removedGameIds = [];
        StoreSyncSnapshot snapshot;
        _unifyRefreshGate.Wait();
        try
        {
            switch (normalizedStoreId)
            {
                case "epic-games":
                    ManagedLegendaryHelper.ClearAuthentication();
                    break;
                case "gog-galaxy":
                    ManagedGogDlHelper.ClearAuthentication();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The {descriptor.Title} credential cleanup is not implemented.");
            }
            OmniLibraryLoginRuntime.ClearUserData(normalizedStoreId);

            var configuration = _settingsStore.Update(latest =>
            {
                var store = GetUnifySteamStoreConfiguration(latest, normalizedStoreId);
                removedGameIds = store.Cache?.Games
                    .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
                    .Select(game => game.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? [];
                store.AuthPath = string.Empty;
                store.PreparedCatalogSignature = string.Empty;
                store.RemoteCatalogSignature = string.Empty;
                store.RemoteCatalogItemIds = [];
                store.PreparedAtUtc = null;
                store.PreparationStatus = string.Empty;
                store.PreparationDetail = string.Empty;
                store.PreparationCompletedCount = 0;
                store.PreparationTotalCount = 0;
                store.Lifecycle = new OmniLibraryStoreLifecycleConfiguration
                {
                    Authentication = "required",
                    Catalog = "idle",
                    Shortcuts = "idle",
                    Artwork = "idle",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                store.Cache = new UnifySteamLibraryCache
                {
                    StatusText = "Login required",
                    DetailText = $"Sign in to {descriptor.Title} to load this account again.",
                };
            });
            snapshot = BuildSnapshot(configuration);
        }
        finally
        {
            _unifyRefreshGate.Release();
        }

        foreach (var gameId in removedGameIds)
        {
            UnifySteamDownloadStatusStore.Clear(normalizedStoreId, gameId);
        }
        QueueOmniLibraryShortcutSync(normalizedStoreId, [], removedGameIds);
        _journal.Append(
            "info",
            "omnilibrary",
            $"Disconnected {descriptor.Title} from OmniLibrary.",
            $"Removed isolated {descriptor.Title} credentials, browser data, cached account data, and managed Steam shortcuts.");
        return snapshot;
    }

    public StoreSyncSnapshot SetStoreScanPath(string storeId, string path)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(storeId))
            {
                throw new InvalidOperationException("A store ID is required.");
            }

            var definition = ResolveStoreDefinition(storeId)
                ?? throw new InvalidOperationException("Unknown store.");
            if (!definition.SupportsCustomPath)
            {
                throw new InvalidOperationException("This store does not support a primary custom path.");
            }

            var fullPath = ResolveValidatedDirectoryPath(path, "A folder path is required.", "The folder does not exist.");
            var configuration = _settingsStore.Update(latest =>
                GetStoreConfiguration(latest, definition.Id).ScanPath = fullPath);
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot ClearStoreScanPath(string storeId)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(storeId))
            {
                throw new InvalidOperationException("A store ID is required.");
            }

            var definition = ResolveStoreDefinition(storeId)
                ?? throw new InvalidOperationException("Unknown store.");
            if (!definition.SupportsCustomPath)
            {
                throw new InvalidOperationException("This store does not support a primary custom path.");
            }

            var configuration = _settingsStore.Update(latest =>
                GetStoreConfiguration(latest, definition.Id).ScanPath = string.Empty);
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetStoreAdditionalScanPaths(string storeId, IReadOnlyList<string>? paths)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(storeId))
            {
                throw new InvalidOperationException("A store ID is required.");
            }

            var definition = ResolveStoreDefinition(storeId)
                ?? throw new InvalidOperationException("Unknown store.");
            if (!definition.SupportsAdditionalPaths)
            {
                throw new InvalidOperationException("This store does not support additional scan paths.");
            }

            var normalizedPaths = NormalizeValidatedDirectoryPaths(paths).ToList();
            var configuration = _settingsStore.Update(latest =>
                GetStoreConfiguration(latest, definition.Id).AdditionalScanPaths = normalizedPaths);
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot SetCustomScanPath(string path) => SetStoreScanPath("custom-locations", path);

    public StoreSyncSnapshot ClearCustomScanPath() => ClearStoreScanPath("custom-locations");

    public StoreSyncSnapshot SetTitleOverride(
        string titleId,
        string titleOverride,
        string artworkTitleOverride,
        bool excluded)
    {
        lock (_gate)
        {
            var normalizedTitleId = titleId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedTitleId))
            {
                throw new InvalidOperationException("A title ID is required.");
            }

            var normalizedTitleOverride = titleOverride.Trim();
            var normalizedArtworkTitleOverride = artworkTitleOverride.Trim();

            var configuration = _settingsStore.Update(latest =>
            {
                if (!excluded &&
                    string.IsNullOrWhiteSpace(normalizedTitleOverride) &&
                    string.IsNullOrWhiteSpace(normalizedArtworkTitleOverride))
                {
                    latest.TitleOverrides.Remove(normalizedTitleId);
                }
                else
                {
                    latest.TitleOverrides[normalizedTitleId] = new StoreSyncTitleOverride
                    {
                        Excluded = excluded,
                        TitleOverride = normalizedTitleOverride,
                        ArtworkTitleOverride = normalizedArtworkTitleOverride,
                    };
                }
            });
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot ClearTitleOverride(string titleId)
    {
        lock (_gate)
        {
            var normalizedTitleId = titleId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedTitleId))
            {
                throw new InvalidOperationException("A title ID is required.");
            }

            var configuration = _settingsStore.Update(latest =>
                latest.TitleOverrides.Remove(normalizedTitleId));
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot RunSync()
    {
        lock (_gate)
        {
            if (_activeSyncTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("A Store Sync run is already in progress.");
            }

            var configuration = _settingsStore.Load();
            var profile = ResolveSteamProfile();
            if (profile is null)
            {
                throw new InvalidOperationException("Steam profile data could not be resolved.");
            }

            var startedAt = DateTimeOffset.UtcNow;
            var queuedState = new StoreSyncLastSyncState(
                Succeeded: true,
                StartedAtUtc: startedAt,
                CompletedAtUtc: startedAt,
                Message: "Tools for Steam is syncing your shortcuts live in Steam when possible and only falls back to the classic file sync when needed.",
                ImportedCount: 0,
                RemovedCount: 0,
                SkippedCount: 0,
                AdoptedCount: 0,
                CleanedUpCount: 0,
                ArtworkUpdatedTitleCount: 0);

            configuration = _settingsStore.Update(latest => latest.LastSync = queuedState);
            _journal.Append("info", "manual", "Manual Store Sync queued.");
            var cancellation = BeginActiveSyncLocked(
                omniLibraryOnly: false,
                omniLibraryStoreId: null);
            _activeSyncTask = Task.Run(() => RunSyncInBackgroundAsync(
                startedAt,
                launchSteamWhenFinished: false,
                allowSteamRestart: true,
                triggerSource: "manual",
                cancellationToken: cancellation.Token));
            return BuildSnapshot(configuration);
        }
    }

    public StoreSyncSnapshot RunStartupSync()
    {
        lock (_gate)
        {
            if (_activeSyncTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("A Store Sync run is already in progress.");
            }

            var configuration = _settingsStore.Load();
            var profile = ResolveSteamProfile();
            if (profile is null)
            {
                throw new InvalidOperationException("Steam profile data could not be resolved.");
            }

            var startedAt = DateTimeOffset.UtcNow;
            var queuedState = new StoreSyncLastSyncState(
                Succeeded: true,
                StartedAtUtc: startedAt,
                CompletedAtUtc: startedAt,
                Message: "Tools for Steam is syncing launchers, then starting Steam as soon as shortcuts are ready.",
                ImportedCount: 0,
                RemovedCount: 0,
                SkippedCount: 0,
                AdoptedCount: 0,
                CleanedUpCount: 0,
                ArtworkUpdatedTitleCount: 0);

            configuration = _settingsStore.Update(latest => latest.LastSync = queuedState);
            _journal.Append("info", "startup", "Startup Store Sync queued.");
            var cancellation = BeginActiveSyncLocked(
                omniLibraryOnly: false,
                omniLibraryStoreId: null);
            _activeSyncTask = Task.Run(() => RunSyncInBackgroundAsync(
                startedAt,
                launchSteamWhenFinished: true,
                allowSteamRestart: false,
                launchSteamAfterShortcutsWritten: true,
                launchBigPictureAfterShortcutWrite: true,
                triggerSource: "startup",
                cancellationToken: cancellation.Token));
            return BuildStartupSnapshot(configuration);
        }
    }

    public bool TryRunAutomaticSync(string triggerSource = "poll")
    {
        lock (_gate)
        {
            _lastAutomaticCheckAtUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(triggerSource))
            {
                _lastAutomaticTriggerSource = triggerSource.Trim();
            }

            if (_activeSyncTask is { IsCompleted: false })
            {
                return false;
            }

            if (_activeOwnershipRepairFollowUpTask is { IsCompleted: false })
            {
                return false;
            }

            var configuration = _settingsStore.Load();
            var profile = ResolveSteamProfile();
            if (profile is null)
            {
                return false;
            }

            // Detect if Steam has rewritten shortcuts.vdf since our last write (e.g. after a restart)
            // and clear the cached signature so we force a re-sync.
            InvalidateSyncSignaturesIfShortcutsModifiedExternally(profile.ShortcutsPath);

            var storeSnapshots = BuildStoreSnapshots(configuration);
            var analysis = BuildSyncAnalysis(
                configuration,
                storeSnapshots,
                LoadExistingShortcuts(profile));
            var syncSignature = BuildDesiredSyncSignature(configuration, analysis);

            if (HasRecentlyAppliedSyncSignature(syncSignature))
            {
                return false;
            }

            if (string.Equals(_lastFailedAutomaticSyncSignature, syncSignature, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - _lastFailedAutomaticSyncAt < GetAutomaticSyncFailureRetryDelay())
            {
                return false;
            }

            if (!HasMeaningfulSyncWork(analysis) &&
                !HasPendingArtworkWork(configuration, profile, analysis))
            {
                RememberAppliedSyncSignature(syncSignature);
                _lastFailedAutomaticSyncSignature = string.Empty;
                _lastFailedAutomaticSyncAt = DateTimeOffset.MinValue;
                return false;
            }

            var startedAt = DateTimeOffset.UtcNow;
            var queuedState = new StoreSyncLastSyncState(
                Succeeded: true,
                StartedAtUtc: startedAt,
                CompletedAtUtc: startedAt,
                Message: "Auto Sync detected launcher changes and is applying them live in Steam.",
                ImportedCount: 0,
                RemovedCount: 0,
                SkippedCount: 0,
                AdoptedCount: 0,
                CleanedUpCount: 0,
                ArtworkUpdatedTitleCount: 0);

            configuration = _settingsStore.Update(latest => latest.LastSync = queuedState);
            _lastAutomaticTriggerAtUtc = startedAt;
            _journal.Append("info", triggerSource, "Automatic Store Sync queued.");
            var cancellation = BeginActiveSyncLocked(
                omniLibraryOnly: false,
                omniLibraryStoreId: null);
            _activeSyncTask = Task.Run(() => RunSyncInBackgroundAsync(
                startedAt,
                launchSteamWhenFinished: false,
                allowSteamRestart: false,
                syncSignature: syncSignature,
                automaticTrigger: true,
                triggerSource: triggerSource,
                cancellationToken: cancellation.Token));
            return true;
        }
    }

    private TimeSpan GetAutomaticSyncFailureRetryDelay()
    {
        var multiplier = Math.Min(Math.Max(_consecutiveAutomaticFailures - 1, 0), 3);
        var seconds = BaseAutomaticSyncFailureRetryDelay.TotalSeconds * Math.Pow(2, multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private StoreSyncSnapshot BuildStartupSnapshot(StoreSyncConfiguration configuration)
    {
        var profile = ResolveSteamProfile();
        var journal = _journal.ReadRecent();
        var stores = StoreDefinitions
            .Select(definition =>
            {
                var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
                return new StoreSyncStoreState(
                    definition.Id,
                    definition.Title,
                    definition.Description,
                    storeConfiguration.Enabled,
                    IsReady: true,
                    CanCleanupMissingTitles: false,
                    StatusText: "Queued",
                    DetailText: "Startup sync is running in the background.",
                    PathValue: storeConfiguration.ScanPath,
                    SupportsCustomPath: definition.SupportsCustomPath,
                    SupportsAdditionalPaths: definition.SupportsAdditionalPaths,
                    AdditionalPaths: storeConfiguration.AdditionalScanPaths.ToArray(),
                    AvailablePathCount: 0,
                    MissingPathCount: 0,
                    DetectedTitleCount: 0,
                    DetectedTitles: []);
            })
            .ToList();
        var unifySteam = StorefrontFeatureFlags.Enabled
            ? _unifySteamService.BuildSnapshot(configuration, stores)
            : StorefrontFeatureFlags.BuildDisabledSnapshot();

        return new StoreSyncSnapshot(
            profile,
            new StoreSyncSettingsState(
                SteamGridDbApiKeyConfigured: !string.IsNullOrWhiteSpace(_artworkDownloader.GetEffectiveApiKey(configuration.SteamGridDbApiKey)),
                SteamGridDbApiKeyPreview: _artworkDownloader.GetPreview(configuration.SteamGridDbApiKey),
                DownloadArtwork: configuration.DownloadArtwork,
                PreferAnimatedArtwork: configuration.PreferAnimatedArtwork,
                CloseSteamBeforeSync: configuration.CloseSteamBeforeSync,
                BackupShortcuts: configuration.BackupShortcuts,
                LaunchBigPictureAfterSync: configuration.LaunchBigPictureAfterSync,
                TakeOverExistingShortcuts: configuration.TakeOverExistingShortcuts,
                CleanupMissingTitles: configuration.CleanupMissingTitles),
            stores,
            unifySteam,
            BuildEmptyPreviewState(),
            configuration.LastSync,
            BuildHealthState(stores, BuildEmptyPreviewState(), journal),
            journal);
    }

    private StoreSyncSnapshot BuildSnapshot(StoreSyncConfiguration configuration)
    {
        var profile = ResolveSteamProfile();
        var storeSnapshots = BuildStoreSnapshots(configuration);
        var analysis = BuildSyncAnalysis(
            configuration,
            storeSnapshots,
            LoadExistingShortcuts(profile));
        var journal = _journal.ReadRecent();

        var stores = storeSnapshots.Select(snapshot => new StoreSyncStoreState(
                snapshot.Definition.Id,
                snapshot.Definition.Title,
                snapshot.Definition.Description,
                snapshot.Configuration.Enabled,
                snapshot.Scan.IsReady,
                snapshot.Scan.CanCleanupMissingTitles,
                snapshot.Scan.StatusText,
                snapshot.Scan.DetailText,
                snapshot.Configuration.ScanPath,
                snapshot.Definition.SupportsCustomPath,
                snapshot.Definition.SupportsAdditionalPaths,
                snapshot.Configuration.AdditionalScanPaths.ToArray(),
                snapshot.Scan.AvailableRoots.Count,
                snapshot.Scan.MissingRoots.Count,
                analysis.Items.Count(item => string.Equals(item.Definition.Id, snapshot.Definition.Id, StringComparison.OrdinalIgnoreCase)),
                analysis.Items
                    .Where(item => string.Equals(item.Definition.Id, snapshot.Definition.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(ToDetectedTitleState)
                    .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToList();
        var unifySteam = StorefrontFeatureFlags.Enabled
            ? _unifySteamService.BuildSnapshot(configuration, stores)
            : StorefrontFeatureFlags.BuildDisabledSnapshot();

        return new StoreSyncSnapshot(
            profile,
            new StoreSyncSettingsState(
                SteamGridDbApiKeyConfigured: !string.IsNullOrWhiteSpace(_artworkDownloader.GetEffectiveApiKey(configuration.SteamGridDbApiKey)),
                SteamGridDbApiKeyPreview: _artworkDownloader.GetPreview(configuration.SteamGridDbApiKey),
                DownloadArtwork: configuration.DownloadArtwork,
                PreferAnimatedArtwork: configuration.PreferAnimatedArtwork,
                CloseSteamBeforeSync: configuration.CloseSteamBeforeSync,
                BackupShortcuts: configuration.BackupShortcuts,
                LaunchBigPictureAfterSync: configuration.LaunchBigPictureAfterSync,
                TakeOverExistingShortcuts: configuration.TakeOverExistingShortcuts,
                CleanupMissingTitles: configuration.CleanupMissingTitles),
            stores,
            unifySteam,
            analysis.Preview,
            configuration.LastSync,
            BuildHealthState(stores, analysis.Preview, journal),
            journal);
    }

    private List<StoreSnapshot> BuildStoreSnapshots(StoreSyncConfiguration configuration)
    {
        var snapshots = StoreDefinitions
            .Select(definition =>
            {
                var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
                var scan = ScanStore(definition, storeConfiguration);
                return new StoreSnapshot(definition, storeConfiguration, scan);
            })
            .ToList();

        if (StorefrontFeatureFlags.SteamShortcutSyncEnabled)
        {
            // Kept as an explicit opt-in boundary for normal Store Sync. OmniLibrary uses
            // its own isolated background sync and does not alter Store Sync's status.
            snapshots.Add(BuildUnifySteamStoreSnapshot(configuration));
        }

        return snapshots;
    }

    private const string UnifySteamStoreId = "unifysteam";

    private static readonly StoreDefinition UnifySteamStoreDefinition = new(
        UnifySteamStoreId,
        "Storefront",
        "Games from your connected launcher accounts that are not installed yet. Click Play in Steam to download and start them.");

    private StoreSnapshot BuildUnifySteamStoreSnapshot(
        StoreSyncConfiguration configuration,
        string? requestedStoreId = null,
        IReadOnlySet<string>? requestedGameIds = null)
    {
        var storeConfiguration = GetStoreConfiguration(configuration, UnifySteamStoreId);
        var games = new List<StoreGameEntry>();
        var launcherPath = NormalizePath(Environment.ProcessPath ?? string.Empty);
        var launcherDirectory = string.IsNullOrWhiteSpace(launcherPath)
            ? string.Empty
            : Path.GetDirectoryName(launcherPath) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
        {
            return new StoreSnapshot(
                UnifySteamStoreDefinition,
                storeConfiguration,
                new StoreScanResult(
                    IsReady: false,
                    CanCleanupMissingTitles: false,
                    "Unavailable",
                    "The Tools for Steam launcher path could not be resolved.",
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    games));
        }

        var enabledStores = configuration.UnifySteam?.Stores?
            .Where(pair =>
                pair.Value?.Enabled == true &&
                pair.Value.Cache?.Games is not null &&
                OmniLibraryStoreRegistry.TryGet(pair.Key, out _) &&
                (string.IsNullOrWhiteSpace(requestedStoreId) ||
                 pair.Key.Equals(requestedStoreId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => OmniLibraryStoreRegistry.GetRequired(pair.Key).Order)
            .ToArray() ?? [];
        foreach (var (storeId, omniStore) in enabledStores)
        {
            foreach (var game in omniStore.Cache.Games)
            {
                if (game is null ||
                    string.IsNullOrWhiteSpace(game.Id) ||
                    string.IsNullOrWhiteSpace(game.Title) ||
                    (requestedGameIds is not null && !requestedGameIds.Contains(game.Id)))
                {
                    continue;
                }

                games.Add(new StoreGameEntry(
                    UnifySteamStoreId,
                    $"{storeId}:{game.Id}",
                    game.Title,
                    launcherPath,
                    launcherDirectory,
                    $"--unifysteam-launch {storeId}:{game.Id}"));
            }
        }

        var sourceReady = enabledStores.Length > 0;
        var isReady = games.Count > 0 || (requestedGameIds is not null && sourceReady);
        return new StoreSnapshot(
            UnifySteamStoreDefinition,
            storeConfiguration,
            new StoreScanResult(
                IsReady: isReady,
                CanCleanupMissingTitles: sourceReady,
                isReady ? "Ready" : "No games",
                games.Count > 0
                    ? $"{games.Count} title(s) are ready for their native Steam Library tabs."
                    : requestedGameIds is not null && sourceReady
                        ? "The requested library delta contains removals only."
                    : "Enable a store, sign in, and sync its library.",
                Array.Empty<string>(),
                Array.Empty<string>(),
                games));
    }

    /// <summary>
    /// Aggressive title key for duplicate detection across stores: lowercase,
    /// keeps only letters and digits so "The Witcher: Enhanced Edition",
    /// "The Witcher - Enhanced Edition" and trademark symbols all collapse.
    /// </summary>
    private static string NormalizeUnifyTitleKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private Task? _activeUnifyRefreshTask;
    private DateTimeOffset _lastUnifyAutoRefreshAtUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan UnifyAutoRefreshInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Refreshes the UnifySteam launcher libraries in the background when they are stale.
    /// Called from the automation loop; unconfigured stores are skipped silently.
    /// </summary>
    public void TryRunAutomaticUnifySteamRefresh()
    {
        if (!StorefrontFeatureFlags.AutomaticRefreshEnabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_activeUnifyRefreshTask is { IsCompleted: false })
            {
                return;
            }

            if (DateTimeOffset.UtcNow - _lastUnifyAutoRefreshAtUtc < UnifyAutoRefreshInterval)
            {
                return;
            }

            _lastUnifyAutoRefreshAtUtc = DateTimeOffset.UtcNow;
            _activeUnifyRefreshTask = Task.Run(() =>
            {
                _unifyRefreshGate.Wait();
                try
                {
                    var configuration = _settingsStore.Load();
                    var enabledStoreIds = _unifySteamService.GetEnabledStoreIds(configuration);
                    var inputSignatures = enabledStoreIds.ToDictionary(
                        id => id,
                        id => ComputeUnifySteamRefreshInputSignature(
                            GetUnifySteamStoreConfiguration(configuration, id)),
                        StringComparer.OrdinalIgnoreCase);
                    var refresh = _unifySteamService.RefreshLibraries(
                        configuration,
                        storeId: null,
                        skipUnconfigured: true);
                    _settingsStore.Update(latest =>
                    {
                        foreach (var result in refresh.Stores.Where(result => result.Succeeded))
                        {
                            if (!inputSignatures.TryGetValue(result.StoreId, out var inputSignature) ||
                                !configuration.UnifySteam.Stores.TryGetValue(
                                    result.StoreId,
                                    out var refreshedStore) ||
                                refreshedStore is null)
                            {
                                continue;
                            }

                            var latestStore = GetUnifySteamStoreConfiguration(latest, result.StoreId);
                            if (latestStore.Enabled &&
                                string.Equals(
                                    inputSignature,
                                    ComputeUnifySteamRefreshInputSignature(latestStore),
                                    StringComparison.Ordinal))
                            {
                                MergeUnifySteamRuntimeState(latestStore, refreshedStore);
                            }
                        }
                    });
                }
                catch (Exception exception)
                {
                    _journal.Append("warning", "unifysteam", "Automatic Storefront library refresh failed.", exception.Message);
                }
                finally
                {
                    _unifyRefreshGate.Release();
                }
            });
        }
    }

    internal IReadOnlyList<StoreSyncWatchTarget> GetAutomationWatchTargets()
    {
        var configuration = _settingsStore.Load();
        var targets = new Dictionary<string, StoreSyncWatchTarget>(StringComparer.OrdinalIgnoreCase);
        var profile = ResolveSteamProfile();

        void addDirectoryTarget(string? path, string filter = "*")
        {
            var normalizedPath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !Directory.Exists(normalizedPath))
            {
                return;
            }

            var key = $"{normalizedPath}|{filter}";
            targets[key] = new StoreSyncWatchTarget(normalizedPath, filter, IncludeSubdirectories: true);
        }

        void addFileTarget(string? path)
        {
            var normalizedPath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            var parentDirectory = Path.GetDirectoryName(normalizedPath);
            var fileName = Path.GetFileName(normalizedPath);
            if (string.IsNullOrWhiteSpace(parentDirectory) ||
                string.IsNullOrWhiteSpace(fileName) ||
                !Directory.Exists(parentDirectory))
            {
                return;
            }

            var key = $"{parentDirectory}|{fileName}";
            targets[key] = new StoreSyncWatchTarget(parentDirectory, fileName, IncludeSubdirectories: false);
        }

        addFileTarget(profile?.ShortcutsPath);

        var romLibrary = GetUnifySteamStoreConfiguration(
            configuration,
            OmniLibraryRomSystemRegistry.StoreId);
        if (romLibrary.Enabled)
        {
            addDirectoryTarget(
                OmniLibraryRomSystemRegistry.ResolveRootPath(
                    romLibrary.InstallPath));
        }

        foreach (var definition in StoreDefinitions)
        {
            var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
            if (!storeConfiguration.Enabled)
            {
                continue;
            }

            switch (definition.Id)
            {
                case "epic-games":
                {
                    var launcherInstalledPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Epic",
                        "UnrealEngineLauncher",
                        "LauncherInstalled.dat");
                    var manifestDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Epic",
                        "EpicGamesLauncher",
                        "Data",
                        "Manifests");
                    addFileTarget(launcherInstalledPath);
                    addDirectoryTarget(manifestDirectory, "*.item");
                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "gog-galaxy":
                {
                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "xbox-game-pass":
                {
                    foreach (var root in GetXboxCandidateRoots().Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        addDirectoryTarget(root);
                    }

                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "ubisoft-connect":
                {
                    var settingsPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Ubisoft Game Launcher",
                        "settings.yaml");
                    addFileTarget(settingsPath);
                    foreach (var root in GetUbisoftConnectCandidateRoots())
                    {
                        addDirectoryTarget(root);
                    }

                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "ea-app":
                {
                    foreach (var root in GetEaAppCandidateRoots())
                    {
                        addDirectoryTarget(root);
                    }

                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "battle-net":
                {
                    var configPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Battle.net",
                        "Battle.net.config");
                    addFileTarget(configPath);
                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "amazon-games":
                {
                    var gamesDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Amazon Games",
                        "Data",
                        "Games");
                    addDirectoryTarget(gamesDataPath, "*.json");
                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "itch-io":
                {
                    var itchAppsPath = ResolveItchAppsPath();
                    if (!string.IsNullOrWhiteSpace(itchAppsPath))
                    {
                        addDirectoryTarget(itchAppsPath, "receipt.json");
                    }

                    foreach (var extraRoot in NormalizeConfiguredScanRoots(storeConfiguration.AdditionalScanPaths))
                    {
                        addDirectoryTarget(extraRoot);
                    }

                    break;
                }
                case "custom-locations":
                {
                    foreach (var root in BuildConfiguredCustomScanRoots(storeConfiguration))
                    {
                        addDirectoryTarget(root);
                    }

                    break;
                }
            }
        }

        return targets.Values
            .OrderBy(target => target.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.Filter, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal bool TryQueueLocalLibraryDelta(string? changedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath) ||
            !StorefrontFeatureFlags.AutomaticRefreshEnabled)
        {
            return false;
        }

        try
        {
            var configuration = _settingsStore.Load();
            var store = GetUnifySteamStoreConfiguration(
                configuration,
                OmniLibraryRomSystemRegistry.StoreId);
            if (!store.Enabled)
            {
                return false;
            }

            var root = OmniLibraryRomSystemRegistry.ResolveRootPath(
                    store.InstallPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(changedPath);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(root, fullPath);
            var systemFolder = relativePath
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!OmniLibraryRomSystemRegistry.Supported.Any(system =>
                    system.FolderName.Equals(
                        systemFolder,
                        StringComparison.OrdinalIgnoreCase)))
            {
                // BIOS and placeholder folders do not affect the imported game
                // set. Consume those watcher events without scheduling a costly
                // Steam shortcut reconciliation.
                return true;
            }

            _ = Task.Run(() => CheckUnifySteamStoreForChanges(
                OmniLibraryRomSystemRegistry.StoreId));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static StoreSyncDetectedTitleState ToDetectedTitleState(StoreSyncAnalysisItem item)
    {
        return new StoreSyncDetectedTitleState(
            item.TitleId,
            item.Game.StoreId,
            item.Definition.Title,
            item.Game.StoreItemId,
            item.Game.Title,
            item.Game.ExecutablePath,
            item.Game.StartDirectory,
            item.Game.LaunchOptions,
            item.EffectiveTitle,
            item.EffectiveArtworkTitle,
            item.TargetAppId,
            FormatActionKind(item.ActionKind),
            item.SyncDetail,
            item.ArtworkState,
            item.Override.Excluded,
            item.ExistingShortcut is not null,
            item.ExistingShortcut?.IsManaged ?? false,
            HasOverrides(item.Override),
            item.ArtworkCache is not null,
            item.ManifestEntry?.ArtworkLocked == true,
            item.Override.TitleOverride,
            item.Override.ArtworkTitleOverride,
            item.DebugLines.ToArray());
    }

    private StoreSyncHealthState BuildHealthState(
        IReadOnlyList<StoreSyncStoreState> stores,
        StoreSyncPreviewState preview,
        IReadOnlyList<StoreSyncJournalEntryState> journal)
    {
        int enabledStoreCount;
        int readyStoreCount;
        int offlineStoreCount;
        int watcherCount;
        bool watchersActive;
        int consecutiveAutomaticFailures;
        DateTimeOffset? lastAutomaticCheckAtUtc;
        DateTimeOffset? lastAutomaticTriggerAtUtc;
        string lastAutomaticTriggerSource;

        lock (_gate)
        {
            enabledStoreCount = stores.Count(store => store.Enabled);
            readyStoreCount = stores.Count(store => store.Enabled && store.IsReady);
            offlineStoreCount = stores.Count(store => store.Enabled && !store.CanCleanupMissingTitles);
            watcherCount = _automationWatcherCount;
            watchersActive = _automationWatchersActive;
            consecutiveAutomaticFailures = _consecutiveAutomaticFailures;
            lastAutomaticCheckAtUtc = _lastAutomaticCheckAtUtc;
            lastAutomaticTriggerAtUtc = _lastAutomaticTriggerAtUtc;
            lastAutomaticTriggerSource = _lastAutomaticTriggerSource;
        }

        var summary = $"{preview.Items.Count} queued - {enabledStoreCount} stores on - {readyStoreCount} ready";
        var detail = offlineStoreCount > 0
            ? $"{offlineStoreCount} store{(offlineStoreCount == 1 ? string.Empty : "s")} are not cleanup-ready, so removals are being deferred safely."
            : preview.DeferredCleanupCount > 0
                ? $"{preview.DeferredCleanupCount} cleanup action{(preview.DeferredCleanupCount == 1 ? string.Empty : "s")} are deferred until stores come back online."
                : "All enabled stores are cleanup-ready.";
        var automation = watchersActive
            ? $"Watchers live on {watcherCount} path{(watcherCount == 1 ? string.Empty : "s")} plus the 10 second poll."
            : "10 second poll is active. Watchers will attach when supported paths are available.";
        var lastJournalSummary = journal.FirstOrDefault()?.Message ?? string.Empty;

        return new StoreSyncHealthState(
            summary,
            detail,
            automation,
            enabledStoreCount,
            readyStoreCount,
            offlineStoreCount,
            preview.DeferredCleanupCount,
            watcherCount,
            watchersActive,
            consecutiveAutomaticFailures,
            lastAutomaticCheckAtUtc,
            lastAutomaticTriggerAtUtc,
            lastAutomaticTriggerSource,
            lastJournalSummary);
    }

    private StoreSyncAnalysis BuildSyncAnalysis(
        StoreSyncConfiguration configuration,
        IReadOnlyList<StoreSnapshot> storeSnapshots,
        IReadOnlyList<ExistingShortcutEntry> existingShortcuts,
        bool forceCleanupMissingTitles = false,
        IReadOnlySet<string>? cleanupOmniGameIds = null)
    {
        var definitionByStoreId = storeSnapshots.ToDictionary(
            snapshot => snapshot.Definition.Id,
            snapshot => snapshot.Definition,
            StringComparer.OrdinalIgnoreCase);
        var cleanupAuthorityByStoreId = storeSnapshots.ToDictionary(
            snapshot => snapshot.Definition.Id,
            snapshot => snapshot.Scan.CanCleanupMissingTitles,
            StringComparer.OrdinalIgnoreCase);
        var items = new List<StoreSyncAnalysisItem>();
        var matchedManagedShortcutIndices = new HashSet<int>();

        foreach (var game in BuildDistinctDiscoveredGames(
                     storeSnapshots
                         .Where(snapshot => snapshot.Configuration.Enabled && snapshot.Scan.IsReady)
                         .SelectMany(snapshot => snapshot.Scan.Games))
                 .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (!definitionByStoreId.TryGetValue(game.StoreId, out var definition))
            {
                continue;
            }

            var titleId = CreateDetectedTitleId(game);
            var overrideState = configuration.TitleOverrides.TryGetValue(titleId, out var configuredOverride) && configuredOverride is not null
                ? configuredOverride
                : new StoreSyncTitleOverride();
            var effectiveTitle = ResolveEffectiveTitle(game, overrideState);
            var effectiveArtworkTitle = ResolveEffectiveArtworkTitle(game, effectiveTitle, overrideState);
            configuration.Manifest.TryGetValue(titleId, out var manifestEntry);
            configuration.ArtworkMatchCache.TryGetValue(titleId, out var artworkCache);

            TryFindExistingShortcut(
                existingShortcuts,
                game,
                manifestEntry,
                effectiveTitle,
                out var existingShortcut);

            var linkedTitleId = titleId;
            if ((manifestEntry is null ||
                 artworkCache is null ||
                 !HasOverrides(overrideState)) &&
                TryResolveLinkedTitleId(
                    configuration,
                    game,
                    effectiveTitle,
                    existingShortcut,
                    out var resolvedLinkedTitleId))
            {
                linkedTitleId = resolvedLinkedTitleId;

                if (!HasOverrides(overrideState) &&
                    configuration.TitleOverrides.TryGetValue(linkedTitleId, out var linkedOverride) &&
                    linkedOverride is not null)
                {
                    overrideState = linkedOverride;
                    effectiveTitle = ResolveEffectiveTitle(game, overrideState);
                    effectiveArtworkTitle = ResolveEffectiveArtworkTitle(game, effectiveTitle, overrideState);
                }

                if (manifestEntry is null)
                {
                    configuration.Manifest.TryGetValue(linkedTitleId, out manifestEntry);
                }

                if (artworkCache is null)
                {
                    configuration.ArtworkMatchCache.TryGetValue(linkedTitleId, out artworkCache);
                }

                TryFindExistingShortcut(
                    existingShortcuts,
                    game,
                    manifestEntry,
                    effectiveTitle,
                    out existingShortcut);
            }

            ResetResolvedPlaceholderArtworkState(
                configuration,
                titleId,
                manifestEntry,
                effectiveArtworkTitle,
                ref artworkCache);

            if (existingShortcut is not null &&
                ShouldTreatShortcutAsManaged(manifestEntry, existingShortcut))
            {
                matchedManagedShortcutIndices.Add(existingShortcut.Index);
            }

            var actionKind = ResolveActionKind(configuration, overrideState, manifestEntry, existingShortcut);
            var shortcutExecutablePath = ResolveShortcutExecutablePath(game);
            var targetAppId = actionKind is StoreSyncActionKind.RefreshManaged or StoreSyncActionKind.AdoptExisting or StoreSyncActionKind.SkipExisting
                ? existingShortcut?.AppId ?? SteamShortcutIds.ComputeAppId(effectiveTitle, shortcutExecutablePath)
                : SteamShortcutIds.ComputeAppId(effectiveTitle, shortcutExecutablePath);

            var debugLines = BuildAnalysisDebugLines(
                titleId,
                linkedTitleId,
                game,
                effectiveTitle,
                effectiveArtworkTitle,
                targetAppId,
                overrideState,
                manifestEntry,
                artworkCache,
                existingShortcut,
                actionKind);

            items.Add(new StoreSyncAnalysisItem(
                TitleId: titleId,
                LinkedTitleId: linkedTitleId,
                Definition: definition,
                Game: game,
                Override: overrideState,
                EffectiveTitle: effectiveTitle,
                EffectiveArtworkTitle: effectiveArtworkTitle,
                TargetAppId: targetAppId,
                ActionKind: actionKind,
                SyncDetail: BuildSyncDetail(actionKind, existingShortcut),
                ArtworkState: BuildArtworkState(configuration, effectiveArtworkTitle, artworkCache),
                ExistingShortcut: existingShortcut,
                ManifestEntry: manifestEntry,
                ArtworkCache: artworkCache,
                DebugLines: debugLines));
        }

        var cleanupPlan = configuration.CleanupMissingTitles || forceCleanupMissingTitles
            ? BuildCleanupCandidates(
                configuration,
                existingShortcuts,
                items,
                matchedManagedShortcutIndices,
                cleanupAuthorityByStoreId,
                cleanupOmniGameIds)
            : new StoreSyncCleanupPlan([], 0);

        return new StoreSyncAnalysis(
            items,
            cleanupPlan.Candidates,
            cleanupPlan.DeferredCount,
            BuildPreviewState(items, cleanupPlan.Candidates, cleanupPlan.DeferredCount));
    }

    private static StoreSyncAnalysis FilterOmniLibraryAnalysis(
        StoreSyncAnalysis analysis,
        OmniLibrarySyncDelta delta)
    {
        var items = analysis.Items
            .Where(item =>
                TryResolveOmniLibraryGameIdentity(
                    item.Game.StoreItemId,
                    out var storeId,
                    out var gameId) &&
                storeId.Equals(delta.StoreId, StringComparison.OrdinalIgnoreCase) &&
                delta.UpsertGameIds.Contains(gameId))
            .ToArray();
        var cleanupCandidates = analysis.CleanupCandidates
            .Where(candidate =>
                candidate.ManifestEntry is not null &&
                TryResolveOmniLibraryGameIdentity(
                    candidate.ManifestEntry.StoreItemId,
                    out var storeId,
                    out var gameId) &&
                storeId.Equals(delta.StoreId, StringComparison.OrdinalIgnoreCase) &&
                delta.RemovedGameIds.Contains(gameId))
            .ToArray();

        return new StoreSyncAnalysis(
            items,
            cleanupCandidates,
            DeferredCleanupCount: 0,
            BuildPreviewState(items, cleanupCandidates, deferredCleanupCount: 0));
    }

    private static StoreSyncCleanupPlan BuildCleanupCandidates(
        StoreSyncConfiguration configuration,
        IReadOnlyList<ExistingShortcutEntry> existingShortcuts,
        IReadOnlyList<StoreSyncAnalysisItem> items,
        IReadOnlySet<int> matchedManagedShortcutIndices,
        IReadOnlyDictionary<string, bool> cleanupAuthorityByStoreId,
        IReadOnlySet<string>? cleanupOmniGameIds = null)
    {
        var currentTitleIds = items
            .SelectMany(item => string.Equals(item.LinkedTitleId, item.TitleId, StringComparison.OrdinalIgnoreCase)
                ? [item.TitleId]
                : new[] { item.TitleId, item.LinkedTitleId })
            .Where(titleId => !string.IsNullOrWhiteSpace(titleId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleanupCandidates = new List<StoreSyncCleanupCandidate>();
        var claimedIndices = new HashSet<int>();
        var deferredCleanupCount = 0;

        foreach (var manifestEntry in configuration.Manifest.Values
                     .Where(entry =>
                         IsManifestLifecycleManaged(entry) &&
                         !currentTitleIds.Contains(entry.TitleId) &&
                         (cleanupOmniGameIds is null ||
                          (TryResolveOmniLibraryGameIdentity(
                               entry.StoreItemId,
                               out _,
                               out var gameId) &&
                           cleanupOmniGameIds.Contains(gameId)))))
        {
            var existingShortcut = TryFindCleanupShortcutForManifest(existingShortcuts, manifestEntry);

            if (existingShortcut is null ||
                matchedManagedShortcutIndices.Contains(existingShortcut.Index) ||
                !claimedIndices.Add(existingShortcut.Index))
            {
                continue;
            }

            var allowsCleanup = cleanupAuthorityByStoreId.TryGetValue(manifestEntry.StoreId, out var storeAllowsCleanup) &&
                                storeAllowsCleanup;
            if (!allowsCleanup)
            {
                deferredCleanupCount++;
                continue;
            }

            cleanupCandidates.Add(new StoreSyncCleanupCandidate(
                manifestEntry.TitleId,
                manifestEntry.Title,
                ResolveStoreTitle(manifestEntry.StoreId),
                existingShortcut,
                manifestEntry,
                [
                    $"Manifest entry {manifestEntry.TitleId} was not detected in the current scan.",
                    $"Managed shortcut {manifestEntry.AppId:x8} will be removed because cleanup is enabled.",
                ]));
        }

        foreach (var existingShortcut in existingShortcuts
                     .Where(entry => entry.IsManaged &&
                                     !matchedManagedShortcutIndices.Contains(entry.Index) &&
                                     claimedIndices.Add(entry.Index)))
        {
            cleanupCandidates.Add(new StoreSyncCleanupCandidate(
                $"managed-{existingShortcut.AppId:x8}",
                string.IsNullOrWhiteSpace(existingShortcut.AppName) ? $"Shortcut {existingShortcut.AppId:x8}" : existingShortcut.AppName,
                "Tools for Steam",
                existingShortcut,
                null,
                [
                    "Managed shortcut no longer matches a detected store title.",
                    "It will be cleaned up to keep Steam shortcuts in sync.",
                ]));
        }

        return cleanupCandidates
            .OrderBy(candidate => candidate.StoreTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray() is var orderedCandidates
            ? new StoreSyncCleanupPlan(orderedCandidates, deferredCleanupCount)
            : new StoreSyncCleanupPlan([], deferredCleanupCount);
    }

    private static ExistingShortcutEntry? TryFindCleanupShortcutForManifest(
        IReadOnlyList<ExistingShortcutEntry> existingShortcuts,
        StoreSyncManifestEntry manifestEntry)
    {
        var normalizedManifestExecutablePath = NormalizePath(manifestEntry.ExecutablePath);

        return existingShortcuts
            .Where(entry => ShouldTreatShortcutAsManaged(manifestEntry, entry))
            .OrderByDescending(entry => entry.IsManaged)
            .ThenBy(entry => manifestEntry.AppId != 0 && entry.AppId == manifestEntry.AppId ? 0 : 1)
            .ThenBy(entry => !string.IsNullOrWhiteSpace(normalizedManifestExecutablePath) &&
                             string.Equals(entry.ExecutablePath, normalizedManifestExecutablePath, StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .FirstOrDefault();
    }

    private static bool IsManifestLifecycleManaged(StoreSyncManifestEntry? manifestEntry)
    {
        return manifestEntry is not null &&
               (manifestEntry.ManagedShortcut || manifestEntry.AdoptedExistingShortcut);
    }

    private static StoreSyncPreviewState BuildPreviewState(
        IReadOnlyList<StoreSyncAnalysisItem> items,
        IReadOnlyList<StoreSyncCleanupCandidate> cleanupCandidates,
        int deferredCleanupCount)
    {
        var previewItems = items
            .Select(item => new StoreSyncPreviewItemState(
                item.TitleId,
                item.EffectiveTitle,
                item.Definition.Title,
                FormatActionKind(item.ActionKind),
                item.SyncDetail,
                item.ArtworkState,
                item.TargetAppId,
                HasOverrides(item.Override),
                item.ArtworkCache is not null,
                item.DebugLines.ToArray()))
            .Concat(cleanupCandidates.Select(candidate => new StoreSyncPreviewItemState(
                candidate.TitleId,
                candidate.Title,
                candidate.StoreTitle,
                "Cleanup",
                "Managed shortcut will be removed because it is no longer detected.",
                "No artwork work required.",
                candidate.ExistingShortcut.AppId,
                false,
                false,
                candidate.DebugLines.ToArray())))
            .OrderBy(item => item.StoreTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StoreSyncPreviewState(
            CreateCount: items.Count(item => item.ActionKind == StoreSyncActionKind.Create),
            RefreshCount: items.Count(item => item.ActionKind == StoreSyncActionKind.RefreshManaged),
            AdoptCount: items.Count(item => item.ActionKind == StoreSyncActionKind.AdoptExisting),
            SkipCount: items.Count(item => item.ActionKind == StoreSyncActionKind.SkipExisting),
            ExcludedCount: items.Count(item => item.ActionKind == StoreSyncActionKind.Excluded),
            CleanupCount: cleanupCandidates.Count,
            DeferredCleanupCount: deferredCleanupCount,
            Items: previewItems);
    }

    private static StoreSyncPreviewState BuildEmptyPreviewState()
    {
        return new StoreSyncPreviewState(0, 0, 0, 0, 0, 0, 0, []);
    }

    private bool HasMeaningfulSyncWork(StoreSyncAnalysis analysis)
    {
        return analysis.CleanupCandidates.Count > 0 ||
               analysis.Items.Any(item =>
                   item.ActionKind is StoreSyncActionKind.Create or StoreSyncActionKind.AdoptExisting ||
                   item.ActionKind == StoreSyncActionKind.RefreshManaged && ManagedShortcutNeedsRefresh(item));
    }

    private static bool ManagedShortcutNeedsRefresh(StoreSyncAnalysisItem item)
    {
        if (item.ExistingShortcut is null)
        {
            return true;
        }

        var desiredEntry = CreateShortcutEntry(
            item,
            item.ExistingShortcut.AppId,
            item.ExistingShortcut.Entry).Entry;
        return !ShortcutEntriesEquivalent(item.ExistingShortcut.Entry, desiredEntry);
    }

    private bool HasPendingArtworkWork(
        StoreSyncConfiguration configuration,
        SteamProfileInfo profile,
        StoreSyncAnalysis analysis)
    {
        if (!configuration.DownloadArtwork)
        {
            return false;
        }

        var gridDirectory = BuildGridDirectory(profile);
        return analysis.Items.Any(item =>
        {
            configuration.ArtworkMatchCache.TryGetValue(item.TitleId, out var artworkCache);
            return ShouldUpdateArtworkForItem(item, item.TargetAppId, artworkCache, gridDirectory);
        });
    }

    private string BuildDesiredSyncSignature(
        StoreSyncConfiguration configuration,
        StoreSyncAnalysis analysis)
    {
        var effectiveApiKey = _artworkDownloader.GetEffectiveApiKey(configuration.SteamGridDbApiKey);
        var payload = new
        {
            downloadArtwork = configuration.DownloadArtwork,
            preferAnimatedArtwork = configuration.PreferAnimatedArtwork,
            apiKeyHash = string.IsNullOrWhiteSpace(effectiveApiKey)
                ? string.Empty
                : ComputeStableSignatureHash(effectiveApiKey),
            items = analysis.Items
                .Where(item => item.ActionKind != StoreSyncActionKind.Excluded)
                .OrderBy(item => item.TitleId, StringComparer.OrdinalIgnoreCase)
                .Select(item => new
                {
                    item.TitleId,
                    item.LinkedTitleId,
                    action = NormalizeActionKindForSignature(item.ActionKind),
                    storeId = item.Game.StoreId,
                    title = item.Game.Title,
                    executablePath = NormalizePath(ResolveShortcutExecutablePath(item.Game)),
                    startDirectory = NormalizePath(ResolveShortcutStartDirectory(item.Game)),
                    launchOptions = ResolveShortcutLaunchOptions(item.Game),
                    item.EffectiveTitle,
                    item.EffectiveArtworkTitle,
                    item.TargetAppId,
                })
                .ToArray(),
            cleanup = analysis.CleanupCandidates
                .OrderBy(candidate => candidate.TitleId, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => new
                {
                    candidate.TitleId,
                    candidate.Title,
                    candidate.StoreTitle,
                    candidate.ExistingShortcut.AppId,
                })
                .ToArray(),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return ComputeStableSignatureHash(json);
    }

    private static string ComputeStableSignatureHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private bool HasRecentlyAppliedSyncSignature(string syncSignature)
    {
        if (string.IsNullOrWhiteSpace(syncSignature))
        {
            return false;
        }

        return _recentAppliedSyncSignatures.Any(entry =>
            string.Equals(entry.Signature, syncSignature, StringComparison.Ordinal));
    }

    private void RememberAppliedSyncSignature(string syncSignature)
    {
        if (string.IsNullOrWhiteSpace(syncSignature))
        {
            return;
        }

        var existingNode = _recentAppliedSyncSignatures.First;
        while (existingNode is not null &&
               !string.Equals(existingNode.Value.Signature, syncSignature, StringComparison.Ordinal))
        {
            existingNode = existingNode.Next;
        }

        if (existingNode is not null)
        {
            _recentAppliedSyncSignatures.Remove(existingNode);
        }

        _recentAppliedSyncSignatures.AddFirst(new AppliedSyncSignatureState(syncSignature, DateTimeOffset.UtcNow));
        while (_recentAppliedSyncSignatures.Count > 6)
        {
            _recentAppliedSyncSignatures.RemoveLast();
        }
    }

    private void PruneScheduledAutomationWrites(DateTimeOffset now)
    {
        var node = _scheduledAutomationWrites.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.IgnoreUntilUtc <= now)
            {
                _scheduledAutomationWrites.Remove(node);
            }

            node = next;
        }
    }

    private static string NormalizeActionKindForSignature(StoreSyncActionKind actionKind)
    {
        return actionKind switch
        {
            StoreSyncActionKind.Create => "Managed",
            StoreSyncActionKind.RefreshManaged => "Managed",
            StoreSyncActionKind.AdoptExisting => "AdoptExisting",
            StoreSyncActionKind.SkipExisting => "SkipExisting",
            StoreSyncActionKind.Excluded => "Excluded",
            _ => "Unknown",
        };
    }

    private static StoreSyncActionKind ResolveActionKind(
        StoreSyncConfiguration configuration,
        StoreSyncTitleOverride overrideState,
        StoreSyncManifestEntry? manifestEntry,
        ExistingShortcutEntry? existingShortcut)
    {
        if (overrideState.Excluded)
        {
            return StoreSyncActionKind.Excluded;
        }

        if (ShouldTreatShortcutAsManaged(manifestEntry, existingShortcut))
        {
            return StoreSyncActionKind.RefreshManaged;
        }

        if (IsManifestLifecycleManaged(manifestEntry))
        {
            return StoreSyncActionKind.RefreshManaged;
        }

        if (existingShortcut is not null)
        {
            return configuration.TakeOverExistingShortcuts
                ? StoreSyncActionKind.AdoptExisting
                : StoreSyncActionKind.SkipExisting;
        }

        return StoreSyncActionKind.Create;
    }

    private static string ResolveEffectiveTitle(StoreGameEntry game, StoreSyncTitleOverride overrideState)
    {
        return string.IsNullOrWhiteSpace(overrideState.TitleOverride)
            ? game.Title
            : PrettifyTitle(overrideState.TitleOverride);
    }

    private static string ResolveEffectiveArtworkTitle(
        StoreGameEntry game,
        string effectiveTitle,
        StoreSyncTitleOverride overrideState)
    {
        if (!string.IsNullOrWhiteSpace(overrideState.ArtworkTitleOverride))
        {
            return PrettifyTitle(overrideState.ArtworkTitleOverride);
        }

        var cleanedTitle = Regex.Replace(
            effectiveTitle,
            @"\s*[\(\[](?:pc|windows|xbox|game pass|microsoft store|wingdk)[^\)\]]*[\)\]]",
            string.Empty,
            RegexOptions.IgnoreCase);
        cleanedTitle = Regex.Replace(
            cleanedTitle,
            @"\b(?:win ?gdk|microsoft store|game pass)\b",
            string.Empty,
            RegexOptions.IgnoreCase);
        cleanedTitle = Regex.Replace(cleanedTitle, @"\s{2,}", " ").Trim(' ', '-', ':');
        return string.IsNullOrWhiteSpace(cleanedTitle) ? effectiveTitle : cleanedTitle;
    }

    private static string BuildSyncDetail(StoreSyncActionKind actionKind, ExistingShortcutEntry? existingShortcut)
    {
        return actionKind switch
        {
            StoreSyncActionKind.Create => "A new managed shortcut will be created.",
            StoreSyncActionKind.RefreshManaged => existingShortcut is null
                ? "The managed Tools for Steam shortcut will be refreshed or recreated if Steam has not loaded it yet."
                : "An existing Tools for Steam shortcut will be refreshed.",
            StoreSyncActionKind.AdoptExisting => $"Existing Steam shortcut {existingShortcut?.AppId:x8} will be reused without creating a duplicate.",
            StoreSyncActionKind.SkipExisting => "Steam already has this title and takeover is turned off.",
            StoreSyncActionKind.Excluded => "This title is excluded by a manual override.",
            _ => "No sync action is planned.",
        };
    }

    private static string BuildArtworkState(
        StoreSyncConfiguration configuration,
        string effectiveArtworkTitle,
        StoreSyncArtworkCacheEntry? artworkCache)
    {
        if (!configuration.DownloadArtwork)
        {
            return "Artwork download is disabled.";
        }

        if (artworkCache is not null && artworkCache.GameId > 0)
        {
            return $"Cached SGDB match ready: {artworkCache.MatchName}.";
        }

        return $"SteamGridDB will search for {effectiveArtworkTitle}.";
    }

    private static List<string> BuildAnalysisDebugLines(
        string titleId,
        string linkedTitleId,
        StoreGameEntry game,
        string effectiveTitle,
        string effectiveArtworkTitle,
        uint targetAppId,
        StoreSyncTitleOverride overrideState,
        StoreSyncManifestEntry? manifestEntry,
        StoreSyncArtworkCacheEntry? artworkCache,
        ExistingShortcutEntry? existingShortcut,
        StoreSyncActionKind actionKind)
    {
        var lines = new List<string>
        {
            $"Title ID: {titleId}",
            $"Source: {game.StoreId}",
            $"Executable: {NormalizePath(game.ExecutablePath)}",
            $"Effective Steam title: {effectiveTitle}",
            $"Effective artwork title: {effectiveArtworkTitle}",
            $"Target app ID: {targetAppId:x8}",
            $"Planned action: {FormatActionKind(actionKind)}",
        };

        if (!string.Equals(linkedTitleId, titleId, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Linked managed title ID: {linkedTitleId}");
        }

        if (HasOverrides(overrideState))
        {
            lines.Add($"Overrides: title='{overrideState.TitleOverride}', artwork='{overrideState.ArtworkTitleOverride}', excluded={overrideState.Excluded}.");
        }

        if (manifestEntry is not null)
        {
            lines.Add($"Manifest: appId={manifestEntry.AppId:x8}, managed={manifestEntry.ManagedShortcut}, adopted={manifestEntry.AdoptedExistingShortcut}, lastAction={manifestEntry.LastAction}.");
        }

        if (existingShortcut is not null)
        {
            lines.Add($"Existing Steam shortcut: appId={existingShortcut.AppId:x8}, managed={existingShortcut.IsManaged}, path={existingShortcut.ExecutablePath}.");
        }

        if (artworkCache is not null)
        {
            lines.Add($"Artwork cache: SGDB game {artworkCache.GameId} ({artworkCache.MatchName}).");
        }

        return lines;
    }

    private static bool HasOverrides(StoreSyncTitleOverride overrideState)
    {
        return overrideState.Excluded ||
               !string.IsNullOrWhiteSpace(overrideState.TitleOverride) ||
               !string.IsNullOrWhiteSpace(overrideState.ArtworkTitleOverride);
    }

    private static string ResolveStoreTitle(string storeId)
    {
        if (string.Equals(storeId, UnifySteamStoreId, StringComparison.OrdinalIgnoreCase))
        {
            return UnifySteamStoreDefinition.Title;
        }

        return ResolveStoreDefinition(storeId)?.Title ?? storeId;
    }

    private static StoreDefinition? ResolveStoreDefinition(string storeId)
    {
        return StoreDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Id, storeId, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ExistingShortcutEntry> LoadExistingShortcuts(SteamProfileInfo? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.ShortcutsPath) || !File.Exists(profile.ShortcutsPath))
        {
            return [];
        }

        try
        {
            return _shortcutFile.Read(profile.ShortcutsPath)
                .Select((entry, index) => TryParseExistingShortcutEntry(entry, index))
                .Where(entry => entry is not null)
                .Cast<ExistingShortcutEntry>()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string FormatActionKind(StoreSyncActionKind actionKind)
    {
        return actionKind switch
        {
            StoreSyncActionKind.Create => "Create",
            StoreSyncActionKind.RefreshManaged => "Refresh Managed",
            StoreSyncActionKind.AdoptExisting => "Adopt Existing",
            StoreSyncActionKind.SkipExisting => "Skip Existing",
            StoreSyncActionKind.Excluded => "Excluded",
            _ => "Unknown",
        };
    }

    private StoreScanResult ScanStore(StoreDefinition definition, StoreSyncStoreConfiguration configuration)
    {
        var primaryScan = definition.Id switch
        {
            "epic-games" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanEpicGames()),
            "gog-galaxy" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanGogGames()),
            "xbox-game-pass" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanXboxGames()),
            "ubisoft-connect" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanUbisoftConnect()),
            "ea-app" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanEaApp()),
            "battle-net" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanBattleNet()),
            "amazon-games" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanAmazonGames()),
            "itch-io" => MergeStoreScanWithAdditionalPaths(definition, configuration, ScanItchIo()),
            "custom-locations" => ScanCustomLocations(BuildConfiguredCustomScanRoots(configuration)),
            _ => new StoreScanResult(false, false, "Unknown store", "The store definition is not supported.", [], [], []),
        };

        return primaryScan;
    }

    private StoreScanResult ScanEpicGames()
    {
        var manifestPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "UnrealEngineLauncher",
            "LauncherInstalled.dat");

        if (!File.Exists(manifestPath))
        {
            return new StoreScanResult(false, false, "Not installed", "Epic Games Launcher was not detected.", [], [manifestPath], []);
        }

        try
        {
            var manifestEntries = LoadEpicManifestEntries();
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("InstallationList", out var installationList) ||
                installationList.ValueKind != JsonValueKind.Array)
            {
                return new StoreScanResult(true, true, "Ready", "Epic metadata is available, but no installed titles were found.", [manifestPath], [], []);
            }

            var games = new List<StoreGameEntry>();

            foreach (var item in installationList.EnumerateArray())
            {
                var installLocation = GetJsonString(item, "InstallLocation");
                var manifest = FindEpicManifest(item, installLocation, manifestEntries);
                installLocation = FirstNonEmpty(installLocation, manifest?.InstallLocation);

                var title = FirstNonEmpty(
                    GetJsonString(item, "DisplayName"),
                    manifest?.DisplayName,
                    manifest?.VaultTitleText,
                    manifest?.MandatoryAppFolderName,
                    Path.GetFileName(installLocation ?? string.Empty),
                    GetJsonString(item, "AppName"),
                    manifest?.AppName);

                var launchExecutable = FirstNonEmpty(GetJsonString(item, "LaunchExecutable"), manifest?.LaunchExecutable);
                var executablePath = ResolveExecutablePath(installLocation ?? string.Empty, launchExecutable);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                var launchOptions = FirstNonEmpty(GetJsonString(item, "LaunchCommand"), manifest?.LaunchCommand) ?? string.Empty;
                var storeItemId = BuildEpicStoreItemId(item, manifest, installLocation, executablePath);
                games.Add(new StoreGameEntry(
                    "epic-games",
                    storeItemId,
                    PrettifyTitle(title),
                    executablePath,
                    Path.GetDirectoryName(executablePath) ?? installLocation ?? string.Empty,
                    launchOptions));
            }

            return new StoreScanResult(
                true,
                true,
                "Ready",
                games.Count > 0
                    ? $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected."
                    : "Epic metadata is available, but no installed titles were found.",
                [manifestPath],
                [],
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [manifestPath], [], []);
        }
    }

    private StoreScanResult ScanGogGames()
    {
        var games = new List<StoreGameEntry>();
        var seenExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games"),
        };

        try
        {
            foreach (var root in roots.Where(root => root is not null))
            {
                using (root)
                {
                    foreach (var subKeyName in root!.GetSubKeyNames())
                    {
                        using var gameKey = root.OpenSubKey(subKeyName);
                        if (gameKey is null)
                        {
                            continue;
                        }

                        var registryTitle =
                            gameKey.GetValue("gameName") as string
                            ?? gameKey.GetValue("GAMENAME") as string
                            ?? gameKey.GetValue("DisplayName") as string;

                        var installPath = NormalizePath(
                            gameKey.GetValue("path") as string
                            ?? gameKey.GetValue("PATH") as string
                            ?? string.Empty);

                        if (string.IsNullOrWhiteSpace(installPath))
                        {
                            continue;
                        }

                        // Prefer goggame-*.info manifests for accurate title and primary executable.
                        var (infoTitle, infoExe) = TryFindGogGameInfoEntry(installPath);

                        var executableHint =
                            infoExe
                            ?? gameKey.GetValue("exe") as string
                            ?? gameKey.GetValue("gameExe") as string
                            ?? gameKey.GetValue("launchCommand") as string
                            ?? string.Empty;

                        var executablePath = ResolveExecutablePath(installPath, executableHint);
                        if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
                        {
                            continue;
                        }

                        var title = FirstNonEmpty(infoTitle, registryTitle, subKeyName)!;

                        games.Add(new StoreGameEntry(
                            "gog-galaxy",
                            BuildStoreItemId("gog", subKeyName, installPath, executablePath),
                            title,
                            executablePath,
                            Path.GetDirectoryName(executablePath) ?? installPath,
                            string.Empty));
                    }
                }
            }

            return games.Count > 0
                ? new StoreScanResult(true, true, "Ready", $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected.", [], [], games)
                : new StoreScanResult(false, false, "Not installed", "No GOG library entries were detected on this system.", [], [], []);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [], [], []);
        }
    }

    /// <summary>
    /// Reads the first matching <c>goggame-*.info</c> JSON file in <paramref name="installPath"/> and
    /// returns the authoritative game title and the path of the primary executable task.
    /// </summary>
    private static (string? Title, string? ExecutablePath) TryFindGogGameInfoEntry(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return (null, null);
        }

        try
        {
            foreach (var infoFile in Directory.EnumerateFiles(installPath, "goggame-*.info", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(infoFile));
                    var root = document.RootElement;

                    var title = GetJsonString(root, "gameName");

                    // playTasks array: find the primary FileTask executable.
                    string? executablePath = null;
                    if (root.TryGetProperty("playTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var task in tasks.EnumerateArray())
                        {
                            var taskType = GetJsonString(task, "type");
                            if (!string.Equals(taskType, "FileTask", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var isPrimary = task.TryGetProperty("isPrimary", out var isPrimaryProp)
                                && isPrimaryProp.ValueKind == JsonValueKind.True;
                            var relPath = GetJsonString(task, "path");
                            if (string.IsNullOrWhiteSpace(relPath))
                            {
                                continue;
                            }

                            var candidatePath = Path.IsPathRooted(relPath)
                                ? NormalizePath(relPath)
                                : NormalizePath(Path.Combine(installPath, relPath));

                            if (!File.Exists(candidatePath))
                            {
                                continue;
                            }

                            if (isPrimary)
                            {
                                executablePath = candidatePath;
                                break;
                            }

                            executablePath ??= candidatePath;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(executablePath))
                    {
                        return (title, executablePath);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return (null, null);
    }

    private StoreScanResult ScanXboxGames()
    {
        try
        {
            var libraryRoots = GetXboxCandidateRoots()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (libraryRoots.Count == 0)
            {
                return new StoreScanResult(false, false, "Not installed", "No Xbox library folders were found.", [], [], []);
            }

            var games = new List<StoreGameEntry>();
            var seenExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var libraryRoot in libraryRoots)
            {
                foreach (var folder in Directory.GetDirectories(libraryRoot))
                {
                    if (ShouldSkipCustomCandidateDirectory(folder))
                    {
                        continue;
                    }

                    if (TryCreateXboxGameFromConfig(folder, seenExecutables, out var configuredGame))
                    {
                        games.Add(configuredGame);
                        continue;
                    }

                    var executablePath = FindBestExecutable(folder);
                    if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
                    {
                        continue;
                    }

                    games.Add(new StoreGameEntry(
                        "xbox-game-pass",
                        BuildStoreItemId("xbox", folder),
                        PrettifyTitle(Path.GetFileName(folder)),
                        executablePath,
                        Path.GetDirectoryName(executablePath) ?? folder,
                        string.Empty));
                }
            }

            return new StoreScanResult(
                true,
                true,
                "Ready",
                games.Count > 0
                    ? $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected."
                    : "Xbox libraries were found, but no launchable executables were detected.",
                libraryRoots,
                [],
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [], [], []);
        }
    }

    private StoreScanResult ScanUbisoftConnect()
    {
        try
        {
            var libraryRoots = GetUbisoftConnectCandidateRoots()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (libraryRoots.Count == 0)
            {
                return new StoreScanResult(false, false, "Not installed", "Ubisoft Connect was not detected on this system.", [], [], []);
            }

            var availableRoots = libraryRoots
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (availableRoots.Count == 0)
            {
                return new StoreScanResult(false, false, "Missing folder", "Ubisoft Connect was detected, but its configured game library folder is currently unavailable.", [], libraryRoots, []);
            }

            var games = new List<StoreGameEntry>();
            var seenExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var libraryRoot in availableRoots)
            {
                foreach (var gameDirectory in Directory.GetDirectories(libraryRoot))
                {
                    if (ShouldSkipCustomCandidateDirectory(gameDirectory))
                    {
                        continue;
                    }

                    if (!TryCreateUbisoftGameFromDirectory(gameDirectory, seenExecutables, out var game))
                    {
                        continue;
                    }

                    games.Add(game);
                }
            }

            return new StoreScanResult(
                true,
                true,
                "Ready",
                games.Count > 0
                    ? $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected."
                    : "Ubisoft Connect was detected, but no launchable titles were found in the current library folder.",
                availableRoots,
                libraryRoots.Except(availableRoots, StringComparer.OrdinalIgnoreCase).ToArray(),
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [], [], []);
        }
    }

    private StoreScanResult ScanEaApp()
    {
        try
        {
            var candidateRoots = GetEaAppCandidateRoots();
            var installReferences = LoadEaInstallReferences();
            var launcherDetected = IsEaAppInstalled();

            if (!launcherDetected && candidateRoots.Count == 0 && installReferences.Count == 0)
            {
                return new StoreScanResult(false, false, "Not installed", "EA App was not detected on this system.", [], [], []);
            }

            var availableRoots = candidateRoots
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var missingRoots = candidateRoots
                .Except(availableRoots, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var games = new List<StoreGameEntry>();
            var seenExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var installReference in installReferences)
            {
                if (!TryCreateEaGameFromInstallPath(installReference, seenExecutables, out var game))
                {
                    continue;
                }

                games.Add(game);
            }

            foreach (var libraryRoot in availableRoots)
            {
                foreach (var gameDirectory in Directory.GetDirectories(libraryRoot))
                {
                    if (ShouldSkipEaCandidateDirectory(gameDirectory))
                    {
                        continue;
                    }

                    if (!TryCreateEaGameFromDirectory(gameDirectory, seenExecutables, out var game))
                    {
                        continue;
                    }

                    games.Add(game);
                }
            }

            if (availableRoots.Count == 0 && games.Count == 0)
            {
                var canCleanupEmptyLibrary = CanCleanupEaMissingTitles(
                    launcherDetected,
                    availableRoots,
                    missingRoots,
                    installReferences.Count,
                    games.Count);
                return new StoreScanResult(
                    true,
                    canCleanupEmptyLibrary,
                    canCleanupEmptyLibrary ? "Ready" : "Detected",
                    canCleanupEmptyLibrary
                        ? "EA App is installed, but no installed titles were detected. Previously synced EA shortcuts can be cleaned up safely."
                        : "EA App is installed, but no known EA game library folders were found yet. Save extra scan folders if your EA games live on another drive.",
                    [],
                    missingRoots,
                    []);
            }

            var canCleanupMissingTitles = CanCleanupEaMissingTitles(
                launcherDetected,
                availableRoots,
                missingRoots,
                installReferences.Count,
                games.Count);
            return new StoreScanResult(
                true,
                canCleanupMissingTitles,
                canCleanupMissingTitles ? "Ready" : "Partial",
                games.Count > 0
                    ? $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected."
                    : "EA App was detected, but no launchable titles were found in the current library folders.",
                availableRoots,
                missingRoots,
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [], [], []);
        }
    }

    private StoreScanResult ScanBattleNet()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Battle.net",
            "Battle.net.config");

        if (!File.Exists(configPath))
        {
            return new StoreScanResult(false, false, "Not installed", "Battle.net was not detected on this system.", [], [configPath], []);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("Games", out var gamesElement) || gamesElement.ValueKind != JsonValueKind.Object)
            {
                return new StoreScanResult(true, true, "Ready", "Battle.net is installed but no game library entries were found.", [configPath], [], []);
            }

            var games = new List<StoreGameEntry>();
            var seenExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var gameProperty in gamesElement.EnumerateObject())
            {
                var productKey = gameProperty.Name;
                if (BattleNetSkipProducts.Contains(productKey))
                {
                    continue;
                }

                var gameDir = GetJsonString(gameProperty.Value, "GameDir");
                if (string.IsNullOrWhiteSpace(gameDir))
                {
                    continue;
                }

                var normalizedGameDir = NormalizePath(gameDir);
                if (string.IsNullOrWhiteSpace(normalizedGameDir) || !Directory.Exists(normalizedGameDir))
                {
                    continue;
                }

                var executablePath = FindBestExecutable(normalizedGameDir);
                if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
                {
                    continue;
                }

                BattleNetProductToTitle.TryGetValue(productKey, out var productTitle);
                var title = FirstNonEmpty(
                    productTitle,
                    TryReadExecutableTitle(executablePath),
                    Path.GetFileName(normalizedGameDir));

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                games.Add(new StoreGameEntry(
                    "battle-net",
                    BuildStoreItemId("battlenet", productKey, normalizedGameDir, executablePath),
                    PrettifyTitle(title),
                    executablePath,
                    Path.GetDirectoryName(executablePath) ?? normalizedGameDir,
                    string.Empty));
            }

            return new StoreScanResult(
                true,
                true,
                "Ready",
                games.Count > 0
                    ? $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected."
                    : "Battle.net is installed but no installed game directories were found.",
                [configPath],
                [],
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [configPath], [], []);
        }
    }

    private StoreScanResult ScanAmazonGames()
    {
        var gamesDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Amazon Games",
            "Data",
            "Games");

        if (!Directory.Exists(gamesDataPath))
        {
            return new StoreScanResult(false, false, "Not installed", "Amazon Games was not detected on this system.", [], [gamesDataPath], []);
        }

        try
        {
            var games = new List<StoreGameEntry>();
            var seenExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var gameFolder in Directory.GetDirectories(gamesDataPath))
            {
                var game = TryCreateAmazonGame(gameFolder, seenExecutables);
                if (game is not null)
                {
                    games.Add(game);
                }
            }

            return new StoreScanResult(
                true,
                true,
                "Ready",
                games.Count > 0
                    ? $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected."
                    : "Amazon Games is installed but no installed titles were found.",
                [gamesDataPath],
                [],
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [gamesDataPath], [], []);
        }
    }

    private static StoreGameEntry? TryCreateAmazonGame(string gameFolder, HashSet<string> seenExecutables)
    {
        var gameId = Path.GetFileName(gameFolder);
        string? title = null;
        string? installDirectory = null;
        string? mainExecutable = null;

        try
        {
            foreach (var jsonFile in Directory.EnumerateFiles(gameFolder, "*.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(jsonFile));
                    var root = document.RootElement;

                    title ??= FirstNonEmpty(
                        GetJsonString(root, "title"),
                        GetJsonString(root, "productTitle"),
                        GetJsonString(root, "localTitle"),
                        GetJsonString(root, "name"));
                    installDirectory ??= FirstNonEmpty(
                        GetJsonString(root, "installDirectory"),
                        GetJsonString(root, "installDir"),
                        GetJsonString(root, "InstallDir"),
                        GetJsonString(root, "installPath"));
                    mainExecutable ??= FirstNonEmpty(
                        GetJsonString(root, "mainExecutable"),
                        GetJsonString(root, "executableFile"),
                        GetJsonString(root, "launchExecutable"),
                        GetJsonString(root, "exeInWorkingDirectory"));
                }
                catch
                {
                    // Ignore malformed JSON files.
                }
            }
        }
        catch
        {
            return null;
        }

        var normalizedInstallDir = NormalizePath(installDirectory);
        if (string.IsNullOrWhiteSpace(normalizedInstallDir) || !Directory.Exists(normalizedInstallDir))
        {
            return null;
        }

        var executablePath = ResolveExecutablePath(normalizedInstallDir, mainExecutable);
        if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
        {
            return null;
        }

        var resolvedTitle = FirstNonEmpty(
            title,
            TryReadExecutableTitle(executablePath),
            Path.GetFileName(normalizedInstallDir));

        if (string.IsNullOrWhiteSpace(resolvedTitle))
        {
            return null;
        }

        return new StoreGameEntry(
            "amazon-games",
            BuildStoreItemId("amazon", gameId, normalizedInstallDir, executablePath),
            PrettifyTitle(resolvedTitle),
            executablePath,
            Path.GetDirectoryName(executablePath) ?? normalizedInstallDir,
            string.Empty);
    }

    private StoreScanResult ScanItchIo()
    {
        var itchAppsPath = ResolveItchAppsPath();
        if (string.IsNullOrWhiteSpace(itchAppsPath))
        {
            return new StoreScanResult(false, false, "Not installed", "itch.io was not detected on this system.", [], [], []);
        }

        if (!Directory.Exists(itchAppsPath))
        {
            return new StoreScanResult(false, false, "Not installed", "The itch.io apps folder was not found.", [], [itchAppsPath], []);
        }

        try
        {
            var games = new List<StoreGameEntry>();
            var seenExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var gameFolder in Directory.GetDirectories(itchAppsPath))
            {
                if (ShouldSkipCustomCandidateDirectory(gameFolder))
                {
                    continue;
                }

                var game = TryCreateItchGame(gameFolder, seenExecutables);
                if (game is not null)
                {
                    games.Add(game);
                }
            }

            return new StoreScanResult(
                true,
                true,
                "Ready",
                games.Count > 0
                    ? $"{games.Count} installed title{(games.Count == 1 ? string.Empty : "s")} detected."
                    : "itch.io is installed but no installed titles were found.",
                [itchAppsPath],
                [],
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, [itchAppsPath], [], []);
        }
    }

    private static string? ResolveItchAppsPath()
    {
        // Check if itch.io is installed by looking for the executable.
        var itchExePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "itch",
            "itch.exe");
        var defaultAppsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "itch",
            "apps");

        if (!File.Exists(itchExePath) && !Directory.Exists(defaultAppsPath))
        {
            return null;
        }

        // Try reading the configured apps directory from preferences.json.
        var prefsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "itch",
            "preferences.json");

        if (File.Exists(prefsPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(prefsPath));
                var root = document.RootElement;

                if (root.TryGetProperty("install_location", out var installLocation))
                {
                    var path = installLocation.ValueKind == JsonValueKind.String
                        ? installLocation.GetString()
                        : GetJsonString(installLocation, "path");

                    var normalized = NormalizePath(path);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        return normalized;
                    }
                }
            }
            catch
            {
            }
        }

        return defaultAppsPath;
    }

    private static StoreGameEntry? TryCreateItchGame(string gameFolder, HashSet<string> seenExecutables)
    {
        string? title = null;
        string? gameId = null;

        // Try receipt.json.gz first (compressed, used by newer butler versions).
        var receiptGzPath = Path.Combine(gameFolder, "receipt.json.gz");
        if (File.Exists(receiptGzPath))
        {
            try
            {
                using var fileStream = File.OpenRead(receiptGzPath);
                using var gzipStream = new System.IO.Compression.GZipStream(
                    fileStream, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gzipStream, Encoding.UTF8);
                var json = reader.ReadToEnd();
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("game", out var gameElement))
                {
                    title = GetJsonString(gameElement, "title");
                    gameId = gameElement.TryGetProperty("id", out var idElement)
                        ? idElement.ToString()
                        : null;
                }
            }
            catch
            {
            }
        }

        // Fall back to uncompressed receipt.json.
        if (string.IsNullOrWhiteSpace(title))
        {
            var receiptJsonPath = Path.Combine(gameFolder, "receipt.json");
            if (File.Exists(receiptJsonPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(receiptJsonPath));
                    var root = document.RootElement;
                    if (root.TryGetProperty("game", out var gameElement))
                    {
                        title = GetJsonString(gameElement, "title");
                        gameId = gameElement.TryGetProperty("id", out var idElement)
                            ? idElement.ToString()
                            : null;
                    }
                }
                catch
                {
                }
            }
        }

        var executablePath = FindBestExecutable(gameFolder);
        if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
        {
            return null;
        }

        var resolvedTitle = FirstNonEmpty(
            title,
            TryReadExecutableTitle(executablePath),
            Path.GetFileName(gameFolder));

        if (string.IsNullOrWhiteSpace(resolvedTitle))
        {
            return null;
        }

        return new StoreGameEntry(
            "itch-io",
            BuildStoreItemId("itch", gameId ?? Path.GetFileName(gameFolder), gameFolder, executablePath),
            PrettifyTitle(resolvedTitle),
            executablePath,
            Path.GetDirectoryName(executablePath) ?? gameFolder,
            string.Empty);
    }

    private static IEnumerable<string> GetXboxCandidateRoots()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        {
            var root = drive.RootDirectory.FullName;
            foreach (var candidate in new[]
            {
                Path.Combine(root, "XboxGames"),
                Path.Combine(root, "ModifiableWindowsApps"),
                Path.Combine(root, "Program Files", "ModifiableWindowsApps")
            })
            {
                if (Directory.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IReadOnlyList<string> GetUbisoftConnectCandidateRoots()
    {
        var roots = new List<string>();

        var configuredLibraryRoot = TryReadUbisoftLibraryRootFromSettings();
        if (!string.IsNullOrWhiteSpace(configuredLibraryRoot))
        {
            roots.Add(configuredLibraryRoot);
        }

        foreach (var launcherInstallRoot in new[]
        {
            TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir"),
            TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Ubisoft\Launcher", "InstallDir"),
            TryReadRegistryString(Registry.CurrentUser, @"SOFTWARE\Ubisoft\Launcher", "InstallDir")
        })
        {
            if (string.IsNullOrWhiteSpace(launcherInstallRoot))
            {
                continue;
            }

            roots.Add(Path.Combine(launcherInstallRoot, "games"));
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ubisoft", "Ubisoft Game Launcher", "games"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ubisoft", "Ubisoft Game Launcher", "games"));

        return roots
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> GetEaAppCandidateRoots()
    {
        var roots = new List<string>();

        foreach (var installRoot in GetEaAppInstallRoots())
        {
            var launcherParent = Directory.GetParent(installRoot);
            if (launcherParent is not null)
            {
                roots.Add(launcherParent.FullName);
            }
        }

        foreach (var installReference in LoadEaInstallReferences())
        {
            var installParent = Directory.GetParent(installReference.InstallPath);
            if (installParent is not null)
            {
                roots.Add(installParent.FullName);
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        {
            var root = drive.RootDirectory.FullName;
            foreach (var candidate in new[]
            {
                Path.Combine(root, "EA Games"),
                Path.Combine(root, "Origin Games"),
                Path.Combine(root, "Electronic Arts"),
                Path.Combine(root, "Program Files", "EA Games"),
                Path.Combine(root, "Program Files", "Origin Games"),
                Path.Combine(root, "Program Files", "Electronic Arts"),
                Path.Combine(root, "Program Files (x86)", "EA Games"),
                Path.Combine(root, "Program Files (x86)", "Origin Games"),
                Path.Combine(root, "Program Files (x86)", "Electronic Arts")
            })
            {
                if (Directory.Exists(candidate))
                {
                    roots.Add(candidate);
                }
            }
        }

        return roots
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEaAppInstalled()
    {
        return GetEaAppInstallRoots().Count > 0
            || LoadEaInstallReferences().Count > 0;
    }

    private static IReadOnlyList<string> GetEaAppInstallRoots()
    {
        var roots = new List<string>();

        foreach (var installRoot in new[]
        {
            TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Electronic Arts\EA Desktop", "InstallLocation"),
            TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop", "InstallLocation"),
            TryReadRegistryString(Registry.CurrentUser, @"SOFTWARE\Electronic Arts\EA Desktop", "InstallLocation"),
            TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Origin", "ClientPath"),
            TryReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Origin", "ClientPath"),
            TryReadRegistryString(Registry.CurrentUser, @"SOFTWARE\Origin", "ClientPath")
        })
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                continue;
            }

            var normalized = NormalizePath(installRoot);
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                normalized = NormalizePath(Path.GetDirectoryName(normalized) ?? string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                roots.Add(normalized);
            }
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? TryReadUbisoftLibraryRootFromSettings()
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ubisoft Game Launcher",
            "settings.yaml");

        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(settingsPath))
            {
                var match = Regex.Match(
                    line,
                    @"^\s*game_installation_path:\s*(?<path>.+?)\s*$",
                    RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var rawPath = match.Groups["path"].Value.Trim().Trim('"').Trim('\'');
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    return null;
                }

                var normalizedSeparators = rawPath.Replace('/', Path.DirectorySeparatorChar);
                return NormalizePath(normalizedSeparators);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryReadRegistryString(RegistryKey rootKey, string subKeyPath, string valueName)
    {
        try
        {
            using var key = rootKey.OpenSubKey(subKeyPath);
            var value = key?.GetValue(valueName) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCreateUbisoftGameFromDirectory(
        string gameDirectory,
        HashSet<string> seenExecutables,
        out StoreGameEntry game)
    {
        game = default!;

        var executablePath = FindBestExecutable(gameDirectory);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (!seenExecutables.Add(executablePath))
        {
            return false;
        }

        var title = FirstNonEmpty(
            TryReadUbisoftTitleFromState(gameDirectory),
            TryReadExecutableTitle(executablePath),
            Path.GetFileName(gameDirectory));
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        game = new StoreGameEntry(
            "ubisoft-connect",
            BuildStoreItemId("ubisoft", gameDirectory),
            PrettifyTitle(title),
            executablePath,
            Path.GetDirectoryName(executablePath) ?? gameDirectory,
            string.Empty);
        return true;
    }

    private static string? TryReadUbisoftTitleFromState(string gameDirectory)
    {
        var statePath = Path.Combine(gameDirectory, "uplay_install.state");
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var stateText = Encoding.UTF8.GetString(File.ReadAllBytes(statePath));

            // Pattern 1: full registry path (older Ubisoft Connect versions).
            var m = Regex.Match(
                stateText,
                @"HKEY_(?:LOCAL_MACHINE|CURRENT_USER)\\SOFTWARE(?:\\WOW6432Node)?\\Ubisoft\\(?<title>[^\\\r\n]+)\\InstallDir",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var title = m.Groups["title"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }
            }

            // Pattern 2: bare Ubisoft registry segment without HKEY_ prefix (newer binary format).
            m = Regex.Match(
                stateText,
                @"SOFTWARE(?:\\WOW6432Node)?\\Ubisoft\\(?<title>[^\\\r\n\x00-\x1F]+?)(?:\\InstallDir|[\x00-\x1F]|$)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var title = m.Groups["title"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }
            }

            // Pattern 3: game name stored as a plain UTF-8 / Latin-1 string near "Name" or "name" markers.
            m = Regex.Match(
                stateText,
                @"[Nn]ame\x00{0,4}(?<title>[A-Za-z][^\x00-\x1F]{3,80})",
                RegexOptions.None);
            if (m.Success)
            {
                var title = m.Groups["title"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(title) && !title.Contains('\\'))
                {
                    return title;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryCreateEaGameFromInstallPath(
        EaInstallReference installReference,
        HashSet<string> seenExecutables,
        out StoreGameEntry game)
    {
        game = default!;

        var installPath = NormalizePath(installReference.InstallPath);
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return false;
        }

        var executablePath = FindBestExecutable(installPath);
        if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
        {
            return false;
        }

        if (IsEaLauncherInstallPath(executablePath) || IsEaLauncherExecutable(executablePath))
        {
            return false;
        }

        var metadata = TryReadEaInstallerMetadata(installPath);
        var title = FirstNonEmpty(
            installReference.Title,
            metadata?.Title,
            TryReadExecutableTitle(executablePath),
            Path.GetFileName(installPath));
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        game = new StoreGameEntry(
            "ea-app",
            BuildEaStoreItemId(installReference, metadata, installPath, executablePath),
            PrettifyTitle(title),
            executablePath,
            Path.GetDirectoryName(executablePath) ?? installPath,
            string.Empty);
        return true;
    }

    private static bool TryCreateEaGameFromDirectory(
        string gameDirectory,
        HashSet<string> seenExecutables,
        out StoreGameEntry game)
    {
        game = default!;

        var executablePath = FindBestExecutable(gameDirectory);
        if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
        {
            return false;
        }

        if (IsEaLauncherInstallPath(executablePath) || IsEaLauncherExecutable(executablePath))
        {
            return false;
        }

        var metadata = TryReadEaInstallerMetadata(gameDirectory);
        var title = FirstNonEmpty(
            metadata?.Title,
            TryReadExecutableTitle(executablePath),
            Path.GetFileName(gameDirectory));
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        game = new StoreGameEntry(
            "ea-app",
            BuildEaStoreItemId(null, metadata, gameDirectory, executablePath),
            PrettifyTitle(title),
            executablePath,
            Path.GetDirectoryName(executablePath) ?? gameDirectory,
            string.Empty);
        return true;
    }

    private static bool ShouldSkipEaCandidateDirectory(string candidatePath)
    {
        if (ShouldSkipCustomCandidateDirectory(candidatePath))
        {
            return true;
        }

        var directoryName = Path.GetFileName(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(directoryName, "EA Desktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(directoryName, "Origin", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEaStoreItemId(
        EaInstallReference? installReference,
        EaInstallerMetadata? metadata,
        string installPath,
        string executablePath)
    {
        var contentId = metadata?.ContentIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        return !string.IsNullOrWhiteSpace(contentId)
            ? BuildStoreItemId("ea", contentId, installPath, executablePath)
            : BuildStoreItemId("ea", installReference?.ReferenceId, installPath, executablePath);
    }

    private static EaInstallerMetadata? TryReadEaInstallerMetadata(string installPath)
    {
        var installerDataPath = Path.Combine(installPath, "__Installer", "installerdata.xml");
        if (!File.Exists(installerDataPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(installerDataPath, LoadOptions.None);
            var title = document
                .Descendants("localeInfo")
                .Select(element => element.Element("title")?.Value?.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var contentIds = document
                .Descendants("contentID")
                .Select(element => element.Value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new EaInstallerMetadata(title, contentIds);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<EaInstallReference> LoadEaInstallReferences()
    {
        var referencesByPath = new Dictionary<string, EaInstallReference>(StringComparer.OrdinalIgnoreCase);

        void addReference(string referenceId, string? title, string? installPath)
        {
            var normalizedInstallPath = NormalizePath(installPath);
            if (string.IsNullOrWhiteSpace(normalizedInstallPath)
                || !Directory.Exists(normalizedInstallPath)
                || IsEaLauncherInstallPath(normalizedInstallPath)
                || title?.Contains("EA App", StringComparison.OrdinalIgnoreCase) == true
                || title?.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase) == true)
            {
                return;
            }

            var normalizedReferenceId = string.IsNullOrWhiteSpace(referenceId)
                ? normalizedInstallPath
                : referenceId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedReferenceId))
            {
                normalizedReferenceId = normalizedInstallPath;
            }

            var resolvedTitle = FirstNonEmpty(title, Path.GetFileName(normalizedInstallPath)) ?? Path.GetFileName(normalizedInstallPath);
            referencesByPath[normalizedInstallPath] = new EaInstallReference(
                normalizedReferenceId,
                resolvedTitle,
                normalizedInstallPath);
        }

        foreach (var registryRoot in new[]
        {
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EA Games"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\EA Games"),
            Registry.CurrentUser.OpenSubKey(@"SOFTWARE\EA Games"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Games"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Electronic Arts\EA Games"),
            Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Games")
        }.Where(key => key is not null))
        {
            using (registryRoot)
            {
                foreach (var subKeyName in registryRoot!.GetSubKeyNames())
                {
                    using var gameKey = registryRoot.OpenSubKey(subKeyName);
                    if (gameKey is null)
                    {
                        continue;
                    }

                    addReference(
                        $"registry:{subKeyName}",
                        FirstNonEmpty(
                            gameKey.GetValue("DisplayName") as string,
                            gameKey.GetValue("ProductName") as string,
                            subKeyName),
                        FirstNonEmpty(
                            gameKey.GetValue("Install Dir") as string,
                            gameKey.GetValue("InstallDir") as string,
                            gameKey.GetValue("Install Location") as string,
                            gameKey.GetValue("InstallLocation") as string));
                }
            }
        }

        foreach (var uninstallRoot in new[]
        {
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        }.Where(key => key is not null))
        {
            using (uninstallRoot)
            {
                foreach (var subKeyName in uninstallRoot!.GetSubKeyNames())
                {
                    using var appKey = uninstallRoot.OpenSubKey(subKeyName);
                    if (appKey is null)
                    {
                        continue;
                    }

                    var installLocation = FirstNonEmpty(
                        appKey.GetValue("InstallLocation") as string,
                        appKey.GetValue("Install Dir") as string,
                        appKey.GetValue("InstallDir") as string);
                    if (string.IsNullOrWhiteSpace(installLocation))
                    {
                        continue;
                    }

                    var publisher = appKey.GetValue("Publisher") as string ?? string.Empty;
                    var uninstallString = appKey.GetValue("UninstallString") as string ?? string.Empty;
                    var displayName = appKey.GetValue("DisplayName") as string ?? string.Empty;
                    if (!publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase)
                        && !uninstallString.Contains(@"\EAInstaller\", StringComparison.OrdinalIgnoreCase)
                        && !installLocation.Contains("EA Games", StringComparison.OrdinalIgnoreCase)
                        && !installLocation.Contains("Origin Games", StringComparison.OrdinalIgnoreCase)
                        && !installLocation.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    addReference($"uninstall:{subKeyName}", displayName, installLocation);
                }
            }
        }

        return referencesByPath.Values
            .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEaLauncherInstallPath(string installPath)
    {
        var normalizedPath = NormalizePath(installPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        return normalizedPath.EndsWith(@"\EA Desktop", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains(@"\EA Desktop\", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.EndsWith(@"\Origin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEaLauncherExecutable(string executablePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(executablePath)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.Equals("EADestager", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("EADesktop", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("EALaunchHelper", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ActivationUI", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Origin", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("OriginThinSetupInternal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanCleanupEaMissingTitles(
        bool launcherDetected,
        IReadOnlyList<string> availableRoots,
        IReadOnlyList<string> missingRoots,
        int installReferenceCount,
        int detectedGameCount)
    {
        if (missingRoots.Count > 0)
        {
            return false;
        }

        if (detectedGameCount > 0 || installReferenceCount > 0 || availableRoots.Any(IsDedicatedEaLibraryRoot))
        {
            return true;
        }

        if (launcherDetected
            && availableRoots.Count > 0
            && availableRoots.All(IsEaLauncherOnlyRoot))
        {
            return true;
        }

        return launcherDetected && availableRoots.Count == 0;
    }

    private static bool IsEaLauncherOnlyRoot(string rootPath)
    {
        var normalizedRoot = NormalizePath(rootPath);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
        {
            return false;
        }

        var directoryName = Path.GetFileName(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(directoryName, "EA Desktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(directoryName, "Origin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(directoryName, "Electronic Arts", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var childDirectories = Directory.GetDirectories(normalizedRoot);
            if (childDirectories.Length == 0)
            {
                return true;
            }

            return childDirectories.All(childDirectory =>
            {
                var childName = Path.GetFileName(childDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.Equals(childName, "EA Desktop", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(childName, "Origin", StringComparison.OrdinalIgnoreCase)
                    || ShouldSkipCustomCandidateDirectory(childDirectory);
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDedicatedEaLibraryRoot(string rootPath)
    {
        var directoryName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(directoryName, "EA Games", StringComparison.OrdinalIgnoreCase)
            || string.Equals(directoryName, "Origin Games", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateXboxGameFromConfig(
        string candidateRoot,
        HashSet<string> seenExecutables,
        out StoreGameEntry game)
    {
        game = default!;

        var configPath = FindXboxConfig(candidateRoot);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return false;
        }

        try
        {
            var document = XDocument.Load(configPath, LoadOptions.None);
            var gameElement = document.Root;
            if (gameElement is null)
            {
                return false;
            }

            var configDirectory = Path.GetDirectoryName(configPath) ?? candidateRoot;
            var executableElement = gameElement
                .Descendants("Executable")
                .Where(element =>
                    !string.Equals((string?)element.Attribute("IsDevOnly"), "true", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace((string?)element.Attribute("TargetDeviceFamily"))
                        || string.Equals((string?)element.Attribute("TargetDeviceFamily"), "PC", StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();

            var executableName = executableElement?.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            var executablePath = ResolveExecutablePath(configDirectory, executableName);
            if (string.IsNullOrWhiteSpace(executablePath) || !seenExecutables.Add(executablePath))
            {
                return false;
            }

            var shellVisuals = gameElement.Element("ShellVisuals");
            var title = ResolveXboxTitle(
                candidateRoot,
                executablePath,
                executableElement?.Attribute("OverrideDisplayName")?.Value,
                shellVisuals?.Attribute("DefaultDisplayName")?.Value,
                shellVisuals?.Attribute("Description")?.Value);

            game = new StoreGameEntry(
                "xbox-game-pass",
                BuildStoreItemId("xbox", configPath, candidateRoot),
                title,
                executablePath,
                Path.GetDirectoryName(executablePath) ?? candidateRoot,
                string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindXboxConfig(string candidateRoot)
    {
        foreach (var configPath in new[]
        {
            Path.Combine(candidateRoot, "MicrosoftGame.Config"),
            Path.Combine(candidateRoot, "Content", "MicrosoftGame.Config")
        })
        {
            if (File.Exists(configPath))
            {
                return configPath;
            }
        }

        try
        {
            return Directory.EnumerateFiles(candidateRoot, "MicrosoftGame.Config", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private StoreScanResult MergeStoreScanWithAdditionalPaths(
        StoreDefinition definition,
        StoreSyncStoreConfiguration configuration,
        StoreScanResult primaryScan)
    {
        if (!definition.SupportsAdditionalPaths)
        {
            return primaryScan;
        }

        var additionalRoots = NormalizeConfiguredScanRoots(configuration.AdditionalScanPaths);
        if (additionalRoots.Count == 0)
        {
            return primaryScan;
        }

        var additionalScan = ScanConfiguredRoots(additionalRoots, definition.Id);
        var mergedGames = MergeStoreGames(primaryScan.Games, additionalScan.Games);
        var isReady = primaryScan.IsReady || additionalScan.AvailableRoots.Count > 0;
        var canCleanupMissingTitles = primaryScan.CanCleanupMissingTitles && additionalScan.MissingRoots.Count == 0;
        var statusText = canCleanupMissingTitles
            ? "Ready"
            : isReady
                ? "Partial"
                : primaryScan.StatusText;
        var detailParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(primaryScan.DetailText))
        {
            detailParts.Add(primaryScan.DetailText);
        }

        if (additionalScan.AvailableRoots.Count > 0)
        {
            var extraCount = additionalScan.Games.Count;
            detailParts.Add(
                extraCount > 0
                    ? $"{extraCount} title{(extraCount == 1 ? string.Empty : "s")} came from extra folders."
                    : "Extra folders were scanned, but no additional titles were found there.");
        }

        if (additionalScan.MissingRoots.Count > 0)
        {
            detailParts.Add(
                $"{additionalScan.MissingRoots.Count} saved extra folder{(additionalScan.MissingRoots.Count == 1 ? string.Empty : "s")} {(additionalScan.MissingRoots.Count == 1 ? "is" : "are")} currently unavailable.");
        }

        if (isReady && !canCleanupMissingTitles)
        {
            detailParts.Add("Cleanup is paused until every saved extra folder is reachable again.");
        }

        return new StoreScanResult(
            isReady,
            canCleanupMissingTitles,
            statusText,
            string.Join(" ", detailParts.Where(part => !string.IsNullOrWhiteSpace(part))),
            primaryScan.AvailableRoots.Concat(additionalScan.AvailableRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            primaryScan.MissingRoots.Concat(additionalScan.MissingRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            mergedGames);
    }

    private StoreScanResult ScanCustomLocations(IReadOnlyList<string> scanRoots)
    {
        if (scanRoots.Count == 0)
        {
            return new StoreScanResult(false, false, "Path required", "Choose at least one folder before syncing custom locations.", [], [], []);
        }

        var configuredScan = ScanConfiguredRoots(scanRoots, "custom-locations");
        if (configuredScan.AvailableRoots.Count == 0)
        {
            return new StoreScanResult(false, false, "Missing folder", "Every saved custom folder is currently unavailable.", [], configuredScan.MissingRoots, []);
        }

        try
        {
            var games = configuredScan.Games.ToList();
            var detailParts = new List<string>
            {
                games.Count > 0
                    ? $"{games.Count} launchable title{(games.Count == 1 ? string.Empty : "s")} detected across {configuredScan.AvailableRoots.Count} folder{(configuredScan.AvailableRoots.Count == 1 ? string.Empty : "s")}."
                    : "The saved folders are valid, but no likely game executables were found."
            };

            if (configuredScan.MissingRoots.Count > 0)
            {
                detailParts.Add(
                    $"{configuredScan.MissingRoots.Count} saved folder{(configuredScan.MissingRoots.Count == 1 ? string.Empty : "s")} {(configuredScan.MissingRoots.Count == 1 ? "is" : "are")} currently unavailable.");
            }

            return new StoreScanResult(
                true,
                configuredScan.MissingRoots.Count == 0,
                "Ready",
                string.Join(" ", detailParts),
                configuredScan.AvailableRoots,
                configuredScan.MissingRoots,
                games);
        }
        catch (Exception exception)
        {
            return new StoreScanResult(false, false, "Error", exception.Message, configuredScan.AvailableRoots, configuredScan.MissingRoots, []);
        }
    }

    private static IReadOnlyList<string> BuildConfiguredCustomScanRoots(StoreSyncStoreConfiguration configuration)
    {
        return NormalizeConfiguredScanRoots(
            new[] { configuration.ScanPath }
                .Concat(configuration.AdditionalScanPaths ?? []));
    }

    private static ExtraPathScanResult ScanConfiguredRoots(IReadOnlyList<string> scanRoots, string storeId)
    {
        var availableRoots = new List<string>();
        var missingRoots = new List<string>();

        foreach (var scanRoot in NormalizeConfiguredScanRoots(scanRoots))
        {
            if (Directory.Exists(scanRoot))
            {
                availableRoots.Add(scanRoot);
            }
            else
            {
                missingRoots.Add(scanRoot);
            }
        }

        var bestCandidatesByRoot = new Dictionary<string, ExecutableCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var scanRoot in availableRoots)
        {
            foreach (var executablePath in EnumerateCustomExecutableCandidates(scanRoot, maximumDepth: 6))
            {
                var candidateRoot = ResolveCustomGameRoot(scanRoot, executablePath);
                if (string.IsNullOrWhiteSpace(candidateRoot)
                    || !Directory.Exists(candidateRoot)
                    || ShouldSkipCustomCandidateDirectory(candidateRoot))
                {
                    continue;
                }

                var score = ScoreCustomExecutable(candidateRoot, executablePath);
                if (score <= 0)
                {
                    continue;
                }

                var candidate = new ExecutableCandidate(executablePath, score);
                if (!bestCandidatesByRoot.TryGetValue(candidateRoot, out var currentBest)
                    || candidate.Score > currentBest.Score)
                {
                    bestCandidatesByRoot[candidateRoot] = candidate;
                }
            }
        }

        var games = bestCandidatesByRoot
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new StoreGameEntry(
                storeId,
                BuildStoreItemId(storeId, entry.Key),
                BuildDetectedTitle(entry.Key, entry.Value.Path),
                entry.Value.Path,
                Path.GetDirectoryName(entry.Value.Path) ?? entry.Key,
                string.Empty))
            .ToArray();

        return new ExtraPathScanResult(availableRoots, missingRoots, games);
    }

    private static IReadOnlyList<StoreGameEntry> MergeStoreGames(
        IReadOnlyList<StoreGameEntry> primaryGames,
        IReadOnlyList<StoreGameEntry> additionalGames)
    {
        var mergedByIdentity = new Dictionary<string, StoreGameEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in primaryGames.Concat(additionalGames))
        {
            var identityKey = ResolveStoreGameIdentityKey(game);
            if (string.IsNullOrWhiteSpace(identityKey) || mergedByIdentity.ContainsKey(identityKey))
            {
                continue;
            }

            mergedByIdentity[identityKey] = game;
        }

        return mergedByIdentity.Values
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeConfiguredScanRoots(IEnumerable<string>? scanRoots)
    {
        return (scanRoots ?? [])
            .Select(path => path?.Trim() ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private SteamProfileInfo? ResolveSteamProfile()
    {
        var steamRootPath = _steamInstallationService.ResolveSteamRootPath();
        if (string.IsNullOrWhiteSpace(steamRootPath))
        {
            return null;
        }

        var userdataPath = Path.Combine(steamRootPath, "userdata");
        if (!Directory.Exists(userdataPath))
        {
            return null;
        }

        var loginUsersPath = Path.Combine(steamRootPath, "config", "loginusers.vdf");
        if (File.Exists(loginUsersPath))
        {
            var text = File.ReadAllText(loginUsersPath);
            var match = Regex.Matches(
                    text,
                    "\"(?<steamId64>\\d{17})\"\\s*\\{(?<body>.*?)\\}",
                    RegexOptions.Singleline)
                .Select(result => new
                {
                    SteamId64 = result.Groups["steamId64"].Value,
                    Body = result.Groups["body"].Value
                })
                .OrderByDescending(entry => GetVdfField(entry.Body, "MostRecent") == "1")
                .ThenByDescending(entry => ParseLong(GetVdfField(entry.Body, "Timestamp")))
                .FirstOrDefault();

            if (match is not null && ulong.TryParse(match.SteamId64, out var steamId64Value))
            {
                var accountIdValue = steamId64Value >= SteamIdOffset
                    ? (steamId64Value - SteamIdOffset).ToString()
                    : match.SteamId64;

                return new SteamProfileInfo(
                    PersonaName: GetVdfField(match.Body, "PersonaName") ?? accountIdValue,
                    AccountName: GetVdfField(match.Body, "AccountName") ?? accountIdValue,
                    SteamId64: match.SteamId64,
                    AccountId: accountIdValue,
                    ShortcutsPath: BuildShortcutsPath(steamRootPath, accountIdValue));
            }
        }

        var accountDirectory = Directory.GetDirectories(userdataPath)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        return accountDirectory is null
            ? null
            : new SteamProfileInfo(
                PersonaName: accountDirectory,
                AccountName: accountDirectory,
                SteamId64: string.Empty,
                AccountId: accountDirectory,
                ShortcutsPath: BuildShortcutsPath(steamRootPath, accountDirectory));
    }

    private async Task RunSyncInBackgroundAsync(
        DateTimeOffset startedAt,
        bool launchSteamWhenFinished,
        bool allowSteamRestart,
        bool launchSteamAfterShortcutsWritten = false,
        bool launchBigPictureAfterShortcutWrite = false,
        string? syncSignature = null,
        bool automaticTrigger = false,
        string triggerSource = "manual",
        bool omniLibraryOnly = false,
        string? omniLibraryStoreId = null,
        OmniLibrarySyncDelta? omniLibraryDelta = null,
        bool updateStoreSyncStatus = true,
        CancellationToken cancellationToken = default)
    {
        var steamWasRunning = false;
        var restartSteamForSync = false;
        var usedLiveShortcutSync = false;
        var liveShortcutAppIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Keep the API responsive; startup sync launches Steam after the shortcut file is ready.
            await Task.Delay(100, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = _settingsStore.Load();
            var profile = ResolveSteamProfile();
            if (profile is null)
            {
                throw new InvalidOperationException("Steam profile data could not be resolved.");
            }
            IReadOnlyList<StoreSnapshot> storeSnapshots = omniLibraryOnly
                ? [BuildUnifySteamStoreSnapshot(
                    configuration,
                    omniLibraryStoreId,
                    omniLibraryDelta?.UpsertGameIds)]
                : BuildStoreSnapshots(configuration);
            var existingEntries = _shortcutFile.Read(profile.ShortcutsPath).ToList();
            var existingShortcutEntries = existingEntries
                .Select((entry, index) => TryParseExistingShortcutEntry(entry, index))
                .Where(entry => entry is not null)
                .Cast<ExistingShortcutEntry>()
                .ToList();
            var analysis = BuildSyncAnalysis(
                configuration,
                storeSnapshots,
                existingShortcutEntries,
                forceCleanupMissingTitles: omniLibraryOnly,
                cleanupOmniGameIds: omniLibraryDelta?.RemovedGameIds);
            if (omniLibraryDelta is not null)
            {
                analysis = FilterOmniLibraryAnalysis(analysis, omniLibraryDelta);
            }

            steamWasRunning = IsSteamRunning();
            if (steamWasRunning)
            {
                using var liveShortcutTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                liveShortcutTimeout.CancelAfter(
                    omniLibraryOnly ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(25));
                var liveShortcutResult = await RetryAsync(
                    () => TryApplyLiveShortcutSyncAsync(
                        configuration,
                        profile,
                        analysis,
                        liveShortcutTimeout.Token),
                    maxAttempts: 2,
                    initialDelay: TimeSpan.FromMilliseconds(250),
                    liveShortcutTimeout.Token);
                if (liveShortcutResult?.Applied == true)
                {
                    usedLiveShortcutSync = true;
                    foreach (var pair in liveShortcutResult.AppIdsByTitleId)
                    {
                        liveShortcutAppIds[pair.Key] = pair.Value;
                    }
                }
            }

            restartSteamForSync = !usedLiveShortcutSync &&
                                  allowSteamRestart &&
                                  configuration.CloseSteamBeforeSync &&
                                  steamWasRunning;
            if (restartSteamForSync)
            {
                CloseSteamForSync();
            }

            if (configuration.BackupShortcuts)
            {
                BackupShortcuts(profile.ShortcutsPath, startedAt);
            }

            if (usedLiveShortcutSync)
            {
                await PersistLiveShortcutSyncMirrorAsync(
                    profile.ShortcutsPath,
                    analysis,
                    liveShortcutAppIds,
                    cancellationToken);
            }
            else
            {
                existingEntries = _shortcutFile.Read(profile.ShortcutsPath).ToList();
                existingShortcutEntries = existingEntries
                    .Select((entry, index) => TryParseExistingShortcutEntry(entry, index))
                    .Where(entry => entry is not null)
                    .Cast<ExistingShortcutEntry>()
                    .ToList();
                analysis = BuildSyncAnalysis(
                    configuration,
                    storeSnapshots,
                    existingShortcutEntries,
                    forceCleanupMissingTitles: omniLibraryOnly,
                    cleanupOmniGameIds: omniLibraryDelta?.RemovedGameIds);
                if (omniLibraryDelta is not null)
                {
                    analysis = FilterOmniLibraryAnalysis(analysis, omniLibraryDelta);
                }
            }

            syncSignature ??= BuildDesiredSyncSignature(configuration, analysis);
            var managedEntries = new List<ManagedShortcutEntry>();
            var entryIndexesToRemove = new HashSet<int>();
            var artworkTargets = new Dictionary<uint, StoreSyncArtworkTarget>();
            var cleanupArtworkAppIds = new HashSet<uint>();
            var createdCount = 0;
            var refreshedCount = 0;
            var adoptedCount = 0;
            var skippedCount = 0;
            var excludedCount = 0;
            var cleanedUpCount = 0;

            foreach (var item in analysis.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (item.ActionKind)
                {
                    case StoreSyncActionKind.Create:
                    {
                        if (!usedLiveShortcutSync)
                        {
                            var managedEntry = CreateShortcutEntry(item);
                            managedEntries.Add(managedEntry);
                        }

                        createdCount++;
                        AddArtworkTarget(
                            artworkTargets,
                            item,
                            appIdOverride: ResolveLiveShortcutAppId(item, liveShortcutAppIds));
                        break;
                    }
                    case StoreSyncActionKind.RefreshManaged:
                    {
                        if (!usedLiveShortcutSync && item.ExistingShortcut is not null)
                        {
                            entryIndexesToRemove.Add(item.ExistingShortcut.Index);
                        }

                        if (!usedLiveShortcutSync)
                        {
                            var managedEntry = CreateShortcutEntry(item);
                            managedEntries.Add(managedEntry);
                        }

                        refreshedCount++;
                        AddArtworkTarget(
                            artworkTargets,
                            item,
                            appIdOverride: ResolveLiveShortcutAppId(item, liveShortcutAppIds));
                        break;
                    }
                    case StoreSyncActionKind.AdoptExisting:
                        adoptedCount++;
                        AddArtworkTarget(
                            artworkTargets,
                            item,
                            appIdOverride: ResolveLiveShortcutAppId(item, liveShortcutAppIds));
                        break;
                    case StoreSyncActionKind.SkipExisting:
                        skippedCount++;
                        AddArtworkTarget(
                            artworkTargets,
                            item,
                            appIdOverride: ResolveLiveShortcutAppId(item, liveShortcutAppIds));
                        break;
                    case StoreSyncActionKind.Excluded:
                        excludedCount++;
                        break;
                }

                if (!string.Equals(item.LinkedTitleId, item.TitleId, StringComparison.OrdinalIgnoreCase))
                {
                    MigrateConfigurationTitleState(configuration, item.LinkedTitleId, item.TitleId);
                }

                var manifestEntry = configuration.Manifest.TryGetValue(item.TitleId, out var storedManifestEntry) && storedManifestEntry is not null
                    ? storedManifestEntry
                    : new StoreSyncManifestEntry();
                manifestEntry.TitleId = item.TitleId;
                manifestEntry.StoreId = item.Game.StoreId;
                manifestEntry.StoreItemId = item.Game.StoreItemId;
                manifestEntry.Title = item.Game.Title;
                manifestEntry.EffectiveTitle = item.EffectiveTitle;
                manifestEntry.ExecutablePath = item.Game.ExecutablePath;
                manifestEntry.AppId = ResolveLiveShortcutAppId(item, liveShortcutAppIds);
                manifestEntry.ManagedShortcut = item.ActionKind is StoreSyncActionKind.Create or StoreSyncActionKind.RefreshManaged;
                manifestEntry.AdoptedExistingShortcut = item.ActionKind == StoreSyncActionKind.AdoptExisting;
                manifestEntry.LastAction = FormatActionKind(item.ActionKind);
                manifestEntry.LastDetail = item.SyncDetail;
                manifestEntry.ArtworkTitle = item.EffectiveArtworkTitle;
                manifestEntry.SteamGridDbGameId = item.ArtworkCache?.GameId;
                manifestEntry.LastSeenAtUtc = startedAt;
                manifestEntry.LastUpdatedAtUtc = startedAt;
                configuration.Manifest[item.TitleId] = manifestEntry;
            }

            if (omniLibraryOnly)
            {
                UpdateOmniLibrarySteamAppIds(configuration, analysis, liveShortcutAppIds);
            }

            foreach (var cleanupCandidate in analysis.CleanupCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!usedLiveShortcutSync)
                {
                    entryIndexesToRemove.Add(cleanupCandidate.ExistingShortcut.Index);
                }

                cleanedUpCount++;
                if (omniLibraryOnly && cleanupCandidate.ExistingShortcut.AppId != 0)
                {
                    cleanupArtworkAppIds.Add(cleanupCandidate.ExistingShortcut.AppId);
                }
                if (cleanupCandidate.ManifestEntry is not null)
                {
                    configuration.Manifest.Remove(cleanupCandidate.ManifestEntry.TitleId);
                    configuration.ArtworkMatchCache.Remove(cleanupCandidate.ManifestEntry.TitleId);
                }
            }

            if (!usedLiveShortcutSync)
            {
                var finalEntries = existingEntries
                    .Where((_, index) => !entryIndexesToRemove.Contains(index))
                    .ToList();
                finalEntries.AddRange(managedEntries.Select(entry => entry.Entry));
                RememberExpectedAutomationWrite(profile.ShortcutsPath, ExpectedAutomationWriteIgnoreDuration);
                await RetryAsync(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _shortcutFile.Write(profile.ShortcutsPath, finalEntries);
                        return Task.CompletedTask;
                    },
                    maxAttempts: 3,
                    initialDelay: TimeSpan.FromMilliseconds(200),
                    cancellationToken);
            }

            if (omniLibraryOnly && cleanupArtworkAppIds.Count > 0)
            {
                var remainingManagedAppIds = configuration.Manifest.Values
                    .Where(entry => entry is not null)
                    .Select(entry => entry.AppId)
                    .Where(appId => appId != 0)
                    .ToHashSet();
                foreach (var appId in cleanupArtworkAppIds.Where(appId => !remainingManagedAppIds.Contains(appId)))
                {
                    DeleteOmniLibraryArtwork(BuildGridDirectory(profile), appId);
                }
            }

            if (launchSteamAfterShortcutsWritten)
            {
                LaunchSteam(launchBigPictureAfterShortcutWrite || configuration.LaunchBigPictureAfterSync);
            }

            StoreSyncArtworkSummary? artworkSummary = null;
            if (!omniLibraryOnly && configuration.DownloadArtwork)
            {
                var gridDirectory = BuildGridDirectory(profile);
                var apiKey = _artworkDownloader.GetEffectiveApiKey(configuration.SteamGridDbApiKey);
                var artworkWorkItems = analysis.Items
                    .Where(item =>
                    {
                        configuration.ArtworkMatchCache.TryGetValue(item.TitleId, out var cachedArtworkMatch);
                        return ShouldUpdateArtworkForItem(
                            item,
                            ResolveLiveShortcutAppId(item, liveShortcutAppIds),
                            cachedArtworkMatch,
                            gridDirectory);
                    })
                    .ToArray();

                var artworkAttemptedAtUtc = DateTimeOffset.UtcNow;
                foreach (var item in artworkWorkItems)
                {
                    if (configuration.Manifest.TryGetValue(item.TitleId, out var manifestEntry) && manifestEntry is not null)
                    {
                        manifestEntry.LastArtworkAttemptAtUtc = artworkAttemptedAtUtc;
                    }
                }

                await WarmArtworkMatchCacheAsync(
                    configuration,
                    artworkWorkItems,
                    apiKey,
                    cancellationToken);
                artworkTargets.Clear();
                foreach (var item in artworkWorkItems)
                {
                    configuration.ArtworkMatchCache.TryGetValue(item.TitleId, out var artworkCache);
                    AddArtworkTarget(
                        artworkTargets,
                        item,
                        artworkCache,
                        ResolveLiveShortcutAppId(item, liveShortcutAppIds));
                }

                artworkSummary = await _artworkDownloader.DownloadAsync(
                    gridDirectory,
                    artworkTargets.Values.ToList(),
                    apiKey,
                    configuration.PreferAnimatedArtwork,
                    cancellationToken);

                foreach (var titleId in artworkSummary.UpdatedTitleIds)
                {
                    if (configuration.Manifest.TryGetValue(titleId, out var manifestEntry) && manifestEntry is not null)
                    {
                        manifestEntry.ArtworkLocked = true;
                        manifestEntry.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
                    }
                }
            }

            var ownershipRepairResult = await RepairManagedShortcutOwnershipAsync(
                profile.ShortcutsPath,
                analysis,
                liveShortcutAppIds,
                usedLiveShortcutSync,
                cancellationToken);
            if (!ownershipRepairResult.Completed && usedLiveShortcutSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScheduleOwnershipRepairFollowUp(
                    profile.ShortcutsPath,
                    analysis,
                    liveShortcutAppIds,
                    omniLibraryOnly,
                    omniLibraryStoreId,
                    cancellationToken);
            }

            // Normal Store Sync can still maintain its per-launcher collections. OmniLibrary
            // uses transient native store-tab filters instead and must not create collections.
            if (!omniLibraryOnly)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TrySyncCollectionsLiveAsync(analysis, liveShortcutAppIds, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                });
            }

            if (restartSteamForSync || (launchSteamWhenFinished && !IsSteamRunning()))
            {
                LaunchSteam(configuration.LaunchBigPictureAfterSync);
            }

            var completionMessage = BuildSyncMessage(
                createdCount,
                refreshedCount,
                adoptedCount,
                skippedCount,
                excludedCount,
                cleanedUpCount,
                artworkSummary,
                configuration.DownloadArtwork,
                steamWasRunning && !restartSteamForSync,
                usedLiveShortcutSync);
            if (!ownershipRepairResult.Completed)
            {
                completionMessage += " Managed shortcut ownership metadata is still settling and will be repaired automatically in the background.";
            }
            if (updateStoreSyncStatus)
            {
                configuration.LastSync = new StoreSyncLastSyncState(
                    Succeeded: true,
                    StartedAtUtc: startedAt,
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    Message: completionMessage,
                    ImportedCount: createdCount,
                    RemovedCount: refreshedCount,
                    SkippedCount: skippedCount,
                    AdoptedCount: adoptedCount,
                    CleanedUpCount: cleanedUpCount,
                    ArtworkUpdatedTitleCount: artworkSummary?.UpdatedTitleCount ?? 0);
            }
            cancellationToken.ThrowIfCancellationRequested();
            configuration = PersistSyncRuntimeConfiguration(
                configuration,
                omniLibraryOnly,
                omniLibraryStoreId,
                updateStoreSyncStatus,
                startedAt);
            _journal.Append("info", triggerSource, completionMessage);
            if (omniLibraryOnly)
            {
                QueueOmniLibraryArtworkSync(
                    profile,
                    configuration,
                    analysis,
                    liveShortcutAppIds,
                    omniLibraryStoreId,
                    cancellationToken);
            }

            if (updateStoreSyncStatus)
            {
                lock (_gate)
                {
                    RememberAppliedSyncSignature(syncSignature ?? string.Empty);
                    try
                    {
                        RememberAppliedSyncSignature(BuildCurrentSyncSignature(configuration, profile));
                    }
                    catch
                    {
                    }
                }
            }

            if (automaticTrigger)
            {
                RecordAutomaticSyncOutcome(true, triggerSource, completionMessage);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _journal.Append(
                "info",
                triggerSource,
                omniLibraryOnly
                    ? "OmniLibrary background work stopped because the plugin or store was disabled."
                    : "Store Sync background work was cancelled.");
        }
        catch (Exception exception)
        {
            try
            {
                var configuration = _settingsStore.Load();
                if ((restartSteamForSync || launchSteamWhenFinished) && !IsSteamRunning())
                {
                    LaunchSteam(configuration.LaunchBigPictureAfterSync);
                }

                if (updateStoreSyncStatus)
                {
                    configuration.LastSync = new StoreSyncLastSyncState(
                        Succeeded: false,
                        StartedAtUtc: startedAt,
                        CompletedAtUtc: DateTimeOffset.UtcNow,
                        Message: exception.Message,
                        ImportedCount: 0,
                        RemovedCount: 0,
                        SkippedCount: 0,
                        AdoptedCount: 0,
                        CleanedUpCount: 0,
                        ArtworkUpdatedTitleCount: 0);
                }
                else if (!string.IsNullOrWhiteSpace(omniLibraryStoreId))
                {
                    var failedStoreId = omniLibraryStoreId.Trim();
                    configuration = _settingsStore.Update(latest =>
                    {
                        var store = GetUnifySteamStoreConfiguration(latest, failedStoreId);
                        store.Cache ??= new UnifySteamLibraryCache();
                        store.Cache.LastError = $"Steam Library sync failed: {exception.Message}";
                        OmniLibraryLifecycle.SetStage(
                            store,
                            "shortcuts",
                            store.PreparedAtUtc.HasValue ? "degraded" : "failed",
                            exception.Message);
                    });
                }
                else
                {
                    configuration = _settingsStore.Update(latest =>
                    {
                        foreach (var store in latest.UnifySteam.Stores.Values.Where(store => store?.Enabled == true))
                        {
                            store.Cache ??= new UnifySteamLibraryCache();
                            store.Cache.LastError = $"Steam Library sync failed: {exception.Message}";
                            OmniLibraryLifecycle.SetStage(
                                store,
                                "shortcuts",
                                store.PreparedAtUtc.HasValue ? "degraded" : "failed",
                                exception.Message);
                        }
                    });
                }

                if (updateStoreSyncStatus)
                {
                    var failedLastSync = configuration.LastSync;
                    _settingsStore.Update(latest => latest.LastSync = failedLastSync);
                }
            }
            catch
            {
            }

            _journal.Append("error", triggerSource, "Store Sync failed.", exception.Message);

            if (automaticTrigger)
            {
                lock (_gate)
                {
                    _lastFailedAutomaticSyncSignature = syncSignature ?? string.Empty;
                    _lastFailedAutomaticSyncAt = DateTimeOffset.UtcNow;
                }

                RecordAutomaticSyncOutcome(false, triggerSource, exception.Message);
            }
        }
        finally
        {
            var runPendingOmniLibrarySync = false;
            string? pendingOmniLibraryStoreId = null;
            OmniLibrarySyncDelta? pendingOmniLibraryDelta = null;
            CancellationTokenSource? completedCancellation;
            lock (_gate)
            {
                completedCancellation = _activeSyncCancellation;
                _activeSyncCancellation = null;
                _activeSyncIsOmniLibrary = false;
                _activeSyncOmniLibraryStoreId = string.Empty;
                _activeSyncTask = null;
                if (_pendingAllOmniLibraryStoreSync)
                {
                    runPendingOmniLibrarySync = true;
                    _pendingAllOmniLibraryStoreSync = false;
                    _pendingOmniLibraryStoreSyncs.Clear();
                    _pendingOmniLibraryDeltas.Clear();
                }
                else if (_pendingOmniLibraryStoreSyncs.Count > 0)
                {
                    runPendingOmniLibrarySync = true;
                    pendingOmniLibraryStoreId = _pendingOmniLibraryStoreSyncs.First();
                    _pendingOmniLibraryStoreSyncs.Remove(pendingOmniLibraryStoreId);
                    _pendingOmniLibraryDeltas.Remove(pendingOmniLibraryStoreId);
                }
                else if (_pendingOmniLibraryDeltas.Count > 0)
                {
                    runPendingOmniLibrarySync = true;
                    var pending = _pendingOmniLibraryDeltas.First();
                    pendingOmniLibraryStoreId = pending.Key;
                    pendingOmniLibraryDelta = pending.Value;
                    _pendingOmniLibraryDeltas.Remove(pending.Key);
                }
            }
            completedCancellation?.Dispose();

            if (runPendingOmniLibrarySync)
            {
                QueueOmniLibraryShortcutSync(
                    pendingOmniLibraryStoreId,
                    pendingOmniLibraryDelta?.UpsertGameIds,
                    pendingOmniLibraryDelta?.RemovedGameIds);
            }
        }
    }

    private static void UpdateOmniLibrarySteamAppIds(
        StoreSyncConfiguration configuration,
        StoreSyncAnalysis analysis,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds)
    {
        foreach (var item in analysis.Items)
        {
            if (!TryResolveOmniLibraryGameIdentity(item.Game.StoreItemId, out var storeId, out var gameId) ||
                !configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                store?.Cache?.Games is null)
            {
                continue;
            }

            var game = store.Cache.Games.FirstOrDefault(candidate =>
                candidate is not null &&
                string.Equals(candidate.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (game is not null)
            {
                game.SteamAppId = ResolveLiveShortcutAppId(item, liveShortcutAppIds);
            }
        }
    }

    private static void DeleteOmniLibraryArtwork(string gridDirectory, uint appId)
    {
        if (appId == 0 || string.IsNullOrWhiteSpace(gridDirectory) || !Directory.Exists(gridDirectory))
        {
            return;
        }

        var validStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            appId.ToString(),
            $"{appId}p",
            $"{appId}_hero",
            $"{appId}_logo",
            $"{appId}_icon",
        };
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         gridDirectory,
                         $"{appId}*",
                         SearchOption.TopDirectoryOnly))
            {
                if (validStems.Contains(Path.GetFileNameWithoutExtension(path)))
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Artwork is a cache. A locked file can be retried during a later cleanup.
        }
    }

    private void QueueOmniLibraryArtworkSync(
        SteamProfileInfo profile,
        StoreSyncConfiguration configuration,
        StoreSyncAnalysis analysis,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds,
        string? requestedStoreId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gridDirectory = BuildGridDirectory(profile);
        var apiKey = _artworkDownloader.GetEffectiveApiKey(configuration.SteamGridDbApiKey);
        configuration.UnifySteam.GameData.Providers.TryGetValue(
            "retroachievements",
            out var retroAchievementsProvider);
        var retroAchievementsApiKey =
            configuration.UnifySteam.GameData.Enabled &&
            retroAchievementsProvider?.Enabled == true
                ? retroAchievementsProvider.Credential?.Trim() ?? string.Empty
                : string.Empty;
        var analyzedStoreIds = analysis.Items
            .Select(item => TryResolveOmniLibraryGameIdentity(item.Game.StoreItemId, out var storeId, out _)
                ? storeId
                : string.Empty)
            .Where(storeId => !string.IsNullOrWhiteSpace(storeId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (analyzedStoreIds.Length == 0 && !string.IsNullOrWhiteSpace(requestedStoreId))
        {
            analyzedStoreIds = [requestedStoreId.Trim()];
        }
        if (analyzedStoreIds.Length > 0)
        {
            // Store readiness is based on a usable catalog and successfully written
            // shortcuts. Artwork is optional cache data and must never hide Xbox,
            // Epic, or a future store after this point.
            MarkOmniLibraryStoresPrepared(analyzedStoreIds);
        }
        var analysisTargets = analysis.Items
            .Select(item =>
            {
                if (!TryResolveOmniLibraryGameIdentity(
                        item.Game.StoreItemId,
                        out var storeId,
                        out var gameId))
                {
                    return null;
                }

                var appId = ResolveLiveShortcutAppId(item, liveShortcutAppIds);
                var cachedGame =
                    configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) &&
                    store?.Cache?.Games is not null
                        ? store.Cache.Games.FirstOrDefault(game =>
                            game is not null &&
                            game.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase))
                        : null;
                return appId == 0 ||
                       SteamGridDbArtworkDownloader.HasCompleteArtworkSet(gridDirectory, appId)
                    ? null
                    : new StoreSyncArtworkTarget(
                        item.TitleId,
                        item.EffectiveArtworkTitle,
                        appId,
                        [item.Game.Title, item.EffectiveTitle],
                        item.ArtworkCache?.GameId,
                        item.ArtworkCache?.MatchName ?? string.Empty,
                        storeId,
                        cachedGame?.ImageUrl ?? string.Empty,
                        cachedGame?.HeroImageUrl ?? string.Empty,
                        cachedGame?.RomPath ?? string.Empty,
                        cachedGame?.PlatformId ?? string.Empty,
                        retroAchievementsApiKey,
                        ResolveRetroAchievementsArtworkGameId(
                            configuration,
                            cachedGame?.Id,
                            cachedGame?.RomPath));
            })
            .Where(target => target is not null)
            .Cast<StoreSyncArtworkTarget>()
            .ToArray();
        var cachedRepairTargets = BuildIncompleteOmniLibraryArtworkTargets(
            configuration,
            gridDirectory,
            analyzedStoreIds);
        var targets = analysisTargets
            .Concat(cachedRepairTargets)
            .GroupBy(
                target => $"{target.StoreId}\u001f{target.AppId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(target => !string.IsNullOrWhiteSpace(target.FallbackPortraitUrl))
                .ThenByDescending(target => !string.IsNullOrWhiteSpace(target.FallbackHeroUrl))
                .First())
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            _pendingOmniArtworkGridDirectory = gridDirectory;
            _pendingOmniArtworkApiKey = apiKey;
            foreach (var target in targets)
            {
                if (_activeOmniArtworkTargetIds.TryGetValue(
                        target.StoreId,
                        out var activeTargetIds) &&
                    activeTargetIds.Contains(target.AppId))
                {
                    continue;
                }

                if (!_pendingOmniArtworkTargets.TryGetValue(target.StoreId, out var storeTargets))
                {
                    storeTargets = new Dictionary<uint, StoreSyncArtworkTarget>();
                    _pendingOmniArtworkTargets[target.StoreId] = storeTargets;
                }

                if (storeTargets.TryGetValue(target.AppId, out var existingTarget))
                {
                    storeTargets[target.AppId] = SelectPreferredArtworkTarget(
                        existingTarget,
                        target);
                }
                else
                {
                    storeTargets[target.AppId] = target;
                }
            }

            if (_activeOmniArtworkTask is { IsCompleted: false })
            {
                return;
            }

            _activeOmniArtworkTask = Task.Run(ProcessQueuedOmniLibraryArtworkAsync);
        }
    }

    private async Task ProcessQueuedOmniLibraryArtworkAsync()
    {
        while (true)
        {
            string gridDirectory;
            string apiKey;
            Dictionary<string, StoreSyncArtworkTarget[]> targetGroups;
            Dictionary<string, CancellationTokenSource> groupCancellations;
            lock (_gate)
            {
                if (_pendingOmniArtworkTargets.Count == 0)
                {
                    _activeOmniArtworkTask = null;
                    return;
                }

                gridDirectory = _pendingOmniArtworkGridDirectory;
                apiKey = _pendingOmniArtworkApiKey;
                targetGroups = _pendingOmniArtworkTargets.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Values.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
                _pendingOmniArtworkTargets.Clear();
                foreach (var group in targetGroups)
                {
                    _activeOmniArtworkTargetIds[group.Key] = group.Value
                        .Select(target => target.AppId)
                        .ToHashSet();
                    if (_activeOmniArtworkCancellations.Remove(
                            group.Key,
                            out var previousCancellation))
                    {
                        previousCancellation.Dispose();
                    }
                    _activeOmniArtworkCancellations[group.Key] =
                        new CancellationTokenSource();
                }
                groupCancellations = targetGroups.Keys.ToDictionary(
                    storeId => storeId,
                    storeId => _activeOmniArtworkCancellations[storeId],
                    StringComparer.OrdinalIgnoreCase);
            }

            MarkOmniLibraryArtworkPreparationStarted(
                targetGroups.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Length,
                    StringComparer.OrdinalIgnoreCase));

            using var concurrencyGate = new SemaphoreSlim(2, 2);
            await Task.WhenAll(targetGroups.Select(async group =>
            {
                var cancellation = groupCancellations[group.Key];
                var cancellationToken = cancellation.Token;
                var gateEntered = false;
                try
                {
                    await concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    gateEntered = true;
                    var summary = await _artworkDownloader.DownloadSteamFirstAsync(
                        gridDirectory,
                        group.Value,
                        apiKey,
                        cancellationToken,
                        (completed, total) => ReportOmniLibraryArtworkProgress(
                            group.Key,
                            completed,
                            total)).ConfigureAwait(false);
                    if (summary.UpdatedFileCount > 0)
                    {
                        _journal.Append(
                            "info",
                            "omnilibrary",
                            $"Downloaded {summary.UpdatedFileCount} Steam Library artwork file(s) for " +
                            $"{summary.UpdatedTitleCount} {group.Key} title(s).");
                    }

                    var incompleteTitles = group.Value
                        .Select(target => new
                        {
                            Target = target,
                            MissingSlots = SteamGridDbArtworkDownloader.GetMissingArtworkSlots(
                                gridDirectory,
                                target.AppId),
                        })
                        .Where(item => item.MissingSlots.Count > 0)
                        .ToArray();
                    if (incompleteTitles.Length == 0)
                    {
                        MarkOmniLibraryArtworkComplete(group.Key, group.Value.Length);
                        return;
                    }

                    var issueDetail = string.Join(
                        "; ",
                        incompleteTitles
                            .Take(12)
                            .Select(item =>
                                $"{item.Target.Title}: {string.Join(", ", item.MissingSlots)}"));
                    if (incompleteTitles.Length > 12)
                    {
                        issueDetail += $"; and {incompleteTitles.Length - 12} more";
                    }
                    MarkOmniLibraryArtworkRepairPending(
                        group.Key,
                        incompleteTitles.Length,
                        group.Value.Length,
                        issueDetail);
                    _journal.Append(
                        "warn",
                        "omnilibrary",
                        $"{group.Key} is ready; optional artwork remains incomplete for " +
                        $"{incompleteTitles.Length} title(s).",
                        issueDetail);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _journal.Append(
                        "debug",
                        "omnilibrary",
                        $"{group.Key} artwork work stopped because the store or plugin was disabled.");
                }
                catch (Exception exception)
                {
                    MarkOmniLibraryArtworkRepairPending(
                        group.Key,
                        group.Value.Length,
                        group.Value.Length,
                        exception.Message);
                    _journal.Append(
                        "warn",
                        "omnilibrary",
                        $"{group.Key} is ready; its optional artwork repair will retry later.",
                        exception.Message);
                }
                finally
                {
                    if (gateEntered)
                    {
                        concurrencyGate.Release();
                    }
                    lock (_gate)
                    {
                        if (_activeOmniArtworkCancellations.TryGetValue(
                                group.Key,
                                out var activeCancellation) &&
                            ReferenceEquals(activeCancellation, cancellation))
                        {
                            _activeOmniArtworkCancellations.Remove(group.Key);
                        }
                    }
                    cancellation.Dispose();
                }
            })).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var storeId in targetGroups.Keys)
                {
                    _activeOmniArtworkTargetIds.Remove(storeId);
                }
            }
        }
    }

    private static StoreSyncArtworkTarget SelectPreferredArtworkTarget(
        StoreSyncArtworkTarget current,
        StoreSyncArtworkTarget candidate)
    {
        static int Score(StoreSyncArtworkTarget target)
        {
            var score = 0;
            score += string.IsNullOrWhiteSpace(target.FallbackPortraitUrl) ? 0 : 4;
            score += string.IsNullOrWhiteSpace(target.FallbackHeroUrl) ? 0 : 4;
            score += string.IsNullOrWhiteSpace(target.RomPath) ? 0 : 3;
            score += string.IsNullOrWhiteSpace(target.RetroAchievementsApiKey) ? 0 : 2;
            score += target.CachedGameId.HasValue && target.CachedGameId.Value > 0 ? 2 : 0;
            score += target.SearchHints.Count > 1 ? 1 : 0;
            return score;
        }

        return Score(candidate) >= Score(current)
            ? candidate
            : current;
    }

    private static IReadOnlyList<StoreSyncArtworkTarget> BuildIncompleteOmniLibraryArtworkTargets(
        StoreSyncConfiguration configuration,
        string gridDirectory,
        IReadOnlyList<string> storeIds)
    {
        var targets = new List<StoreSyncArtworkTarget>();
        foreach (var storeId in storeIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                store?.Cache?.Games is null)
            {
                continue;
            }

            foreach (var game in store.Cache.Games.Where(game =>
                         game is not null &&
                         game.SteamAppId != 0 &&
                         !string.IsNullOrWhiteSpace(game.Id) &&
                         !string.IsNullOrWhiteSpace(game.Title) &&
                         !SteamGridDbArtworkDownloader.HasCompleteArtworkSet(
                             gridDirectory,
                             game.SteamAppId)))
            {
                targets.Add(new StoreSyncArtworkTarget(
                    $"{storeId}:{game.Id}",
                    game.Title,
                    game.SteamAppId,
                    [game.Title],
                    null,
                    string.Empty,
                    storeId,
                    game.ImageUrl,
                    game.HeroImageUrl,
                    game.RomPath,
                    game.PlatformId,
                    configuration.UnifySteam.GameData.Enabled &&
                    configuration.UnifySteam.GameData.Providers.TryGetValue(
                        "retroachievements",
                        out var retroProvider) &&
                    retroProvider?.Enabled == true
                        ? retroProvider.Credential?.Trim() ?? string.Empty
                        : string.Empty,
                    ResolveRetroAchievementsArtworkGameId(
                        configuration,
                        game.Id,
                        game.RomPath)));
            }
        }

        return targets;
    }

    private static uint? ResolveRetroAchievementsArtworkGameId(
        StoreSyncConfiguration configuration,
        string? localGameId,
        string? romPath)
    {
        if (string.IsNullOrWhiteSpace(localGameId) ||
            !configuration.UnifySteam.GameData.Enabled ||
            !configuration.UnifySteam.GameData.Providers.TryGetValue(
                "retroachievements",
                out var provider) ||
            provider?.Enabled != true ||
            !provider.GameIdOverrides.TryGetValue(localGameId, out var mapping) ||
            !OmniLibraryRetroAchievementsSource.TryResolveCachedHashMapping(
                romPath,
                mapping,
                out _,
                out var gameId) ||
            !uint.TryParse(
                gameId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed == 0)
        {
            return null;
        }

        return parsed;
    }

    private void MarkOmniLibraryArtworkPreparationStarted(IReadOnlyDictionary<string, int> totalsByStore)
    {
        _settingsStore.Update(configuration =>
        {
            foreach (var (storeId, total) in totalsByStore)
            {
                if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                    store is null ||
                    !store.Enabled)
                {
                    continue;
                }

                // The store is already usable once its catalog and shortcuts are
                // prepared. Artwork is optional background cache work and must not
                // move the public preparation state backwards.
                if (store.PreparedAtUtc.HasValue)
                {
                    store.PreparationStatus = "prepared";
                }
                OmniLibraryLifecycle.SetStage(store, "artwork", "updating");
                store.PreparationDetail =
                    $"Library ready. Loading optional artwork 0/{total} in the background.";
                store.PreparationCompletedCount = 0;
                store.PreparationTotalCount = total;
            }
        });
    }

    private void ReportOmniLibraryArtworkProgress(string storeId, int completed, int total)
    {
        var updateInterval = total <= 30 ? 1 : 10;
        if (completed != total && completed != 1 && completed % updateInterval != 0)
        {
            return;
        }

        _settingsStore.Update(configuration =>
        {
            if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                store is null ||
                !store.Enabled)
            {
                return;
            }

            if (store.PreparedAtUtc.HasValue)
            {
                store.PreparationStatus = "prepared";
            }
            OmniLibraryLifecycle.SetStage(store, "artwork", "updating");
            store.PreparationCompletedCount = Math.Clamp(completed, 0, Math.Max(total, 0));
            store.PreparationTotalCount = Math.Max(total, 0);
            store.PreparationDetail =
                $"Library ready. Loading optional artwork " +
                $"{store.PreparationCompletedCount}/{store.PreparationTotalCount} in the background.";
        });
    }

    private void MarkOmniLibraryArtworkComplete(string storeId, int total)
    {
        _settingsStore.Update(configuration =>
        {
            if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                store is null ||
                !store.Enabled)
            {
                return;
            }

            store.PreparationStatus = "prepared";
            OmniLibraryLifecycle.SetStage(store, "artwork", "ready");
            store.PreparationCompletedCount = Math.Max(0, total);
            store.PreparationTotalCount = Math.Max(0, total);
            store.PreparationDetail =
                "Library and artwork are ready. Later catalog changes apply live.";
        });
        lock (_gate)
        {
            _omniArtworkRetryAtUtc.Remove(storeId);
        }
    }

    private void MarkOmniLibraryStoresPrepared(IReadOnlyList<string> storeIds)
    {
        var preparedStores = new List<string>();
        _settingsStore.Update(configuration =>
        {
            var preparedAtUtc = DateTimeOffset.UtcNow;
            foreach (var storeId in storeIds)
            {
                if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                    store is null ||
                    !store.Enabled)
                {
                    continue;
                }

                var signature = UnifySteamService.ComputePreparedCatalogSignature(store);
                if (string.IsNullOrWhiteSpace(signature))
                {
                    continue;
                }

                if (!string.Equals(store.PreparedCatalogSignature, signature, StringComparison.OrdinalIgnoreCase) ||
                    !store.PreparedAtUtc.HasValue)
                {
                    store.PreparedCatalogSignature = signature;
                    store.PreparedAtUtc ??= preparedAtUtc;
                }
                preparedStores.Add(storeId);
                store.PreparationStatus = "prepared";
                OmniLibraryLifecycle.SetStage(store, "shortcuts", "ready");
                if (store.Cache?.LastError.StartsWith(
                        "Steam Library sync failed:",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    store.Cache.LastError = string.Empty;
                }
                store.PreparationCompletedCount = Math.Max(
                    store.PreparationCompletedCount,
                    store.PreparationTotalCount);
                store.PreparationDetail =
                    "Preparation complete. First-time store activation requires one Steam restart; later deltas apply live.";
            }
        });
        if (preparedStores.Count > 0)
        {
            lock (_gate)
            {
                foreach (var storeId in preparedStores)
                {
                    _omniArtworkRetryAtUtc.Remove(storeId);
                }
            }
            _journal.Append(
                "info",
                "omnilibrary",
                $"Prepared {string.Join(", ", preparedStores)}.",
                "Initial store activation requires one Steam restart; incremental catalog changes apply live.");
        }
    }

    private void MarkOmniLibraryArtworkRepairPending(
        string storeId,
        int incompleteTitleCount,
        int total,
        string detail)
    {
        lock (_gate)
        {
            _omniArtworkRetryAtUtc[storeId] = DateTimeOffset.UtcNow.AddMinutes(30);
        }
        _settingsStore.Update(configuration =>
        {
            if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var store) ||
                store is null)
            {
                return;
            }

            store.PreparationStatus = store.PreparedAtUtc.HasValue
                ? "prepared"
                : "artwork-pending";
            OmniLibraryLifecycle.SetStage(
                store,
                "artwork",
                "degraded",
                detail);
            store.PreparationCompletedCount = Math.Max(0, total);
            store.PreparationTotalCount = Math.Max(0, total);
            store.PreparationDetail =
                $"Library ready. Optional artwork is still incomplete for " +
                $"{Math.Max(0, incompleteTitleCount)} title(s) and will retry in the background." +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}");
        });
    }

    private static bool TryResolveOmniLibraryGameIdentity(
        string storeItemId,
        out string storeId,
        out string gameId)
    {
        storeId = string.Empty;
        gameId = string.Empty;
        if (string.IsNullOrWhiteSpace(storeItemId))
        {
            return false;
        }

        var separator = storeItemId.IndexOf(':');
        if (separator <= 0 || separator >= storeItemId.Length - 1)
        {
            return false;
        }

        storeId = storeItemId[..separator].Trim().ToLowerInvariant();
        gameId = storeItemId[(separator + 1)..].Trim();
        return OmniLibraryStoreRegistry.TryGet(storeId, out _) &&
               !string.IsNullOrWhiteSpace(gameId);
    }

    private static uint ResolveLiveShortcutAppId(
        StoreSyncAnalysisItem item,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds)
    {
        return liveShortcutAppIds.TryGetValue(item.TitleId, out var appId) && appId != 0
            ? appId
            : item.TargetAppId;
    }

    private string BuildCurrentSyncSignature(
        StoreSyncConfiguration configuration,
        SteamProfileInfo profile)
    {
        var storeSnapshots = BuildStoreSnapshots(configuration);
        var analysis = BuildSyncAnalysis(
            configuration,
            storeSnapshots,
            LoadExistingShortcuts(profile));
        return BuildDesiredSyncSignature(configuration, analysis);
    }

    private async Task<LiveShortcutSyncResult?> TryApplyLiveShortcutSyncAsync(
        StoreSyncConfiguration configuration,
        SteamProfileInfo profile,
        StoreSyncAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var plan = BuildLiveShortcutSyncPlan(configuration, profile, analysis);
        if (plan.IsEmpty)
        {
            return new LiveShortcutSyncResult(true, new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));
        }

        var target = await _steamDevToolsClient.GetSharedJsContextTargetAsync(cancellationToken);
        if (target is null)
        {
            return null;
        }

        var evaluation = await _steamDevToolsClient.EvaluateAsync(
            target.WebSocketDebuggerUrl,
            BuildLiveShortcutSyncExpression(plan),
            cancellationToken);
        if (!evaluation.Success)
        {
            return null;
        }

        var response = DeserializeLiveShortcutSyncResponse(evaluation.Value);
        if (response is null || !response.Available)
        {
            return null;
        }

        if (!response.Success)
        {
            var errorText = response.Errors.Count > 0
                ? string.Join(" ", response.Errors)
                : "Steam accepted the live sync request but reported one or more shortcut operations as failed.";
            throw new InvalidOperationException(errorText);
        }

        return new LiveShortcutSyncResult(
            true,
            response.AppIdsByTitleId ?? new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase));
    }

    private async Task PersistLiveShortcutSyncMirrorAsync(
        string shortcutsPath,
        StoreSyncAnalysis analysis,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds,
        CancellationToken cancellationToken)
    {
        await RetryAsync(
            () =>
            {
                var entries = _shortcutFile.Read(shortcutsPath).ToList();
                if (!TryBuildLiveShortcutMirrorEntries(entries, analysis, liveShortcutAppIds, out var mirroredEntries))
                {
                    return Task.CompletedTask;
                }

                RememberExpectedAutomationWrite(shortcutsPath, ExpectedAutomationWriteIgnoreDuration);
                _shortcutFile.Write(shortcutsPath, mirroredEntries);
                return Task.CompletedTask;
            },
            maxAttempts: 3,
            initialDelay: TimeSpan.FromMilliseconds(250),
            cancellationToken);
    }

    private static bool TryBuildLiveShortcutMirrorEntries(
        IReadOnlyList<Dictionary<string, object?>> existingEntries,
        StoreSyncAnalysis analysis,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds,
        out List<Dictionary<string, object?>> mirroredEntries)
    {
        var parsedEntries = existingEntries
            .Select((entry, index) => TryParseExistingShortcutEntry(entry, index))
            .Where(entry => entry is not null)
            .Cast<ExistingShortcutEntry>()
            .ToArray();
        var replacementByIndex = new Dictionary<int, Dictionary<string, object?>>();
        var removalIndices = new HashSet<int>();
        var appendedEntries = new List<Dictionary<string, object?>>();
        var changed = false;

        foreach (var item in analysis.Items)
        {
            var liveAppId = ResolveLiveShortcutAppId(item, liveShortcutAppIds);
            switch (item.ActionKind)
            {
                case StoreSyncActionKind.Create:
                {
                    var createdEntry = CreateShortcutEntry(item, liveAppId).Entry;
                    if (TryFindMirrorEntryIndices(parsedEntries, item, liveAppId, out var createIndices))
                    {
                        foreach (var duplicateIndex in createIndices.Skip(1))
                        {
                            removalIndices.Add(duplicateIndex);
                            changed = true;
                        }

                        var primaryIndex = createIndices[0];
                        if (!ShortcutEntriesEquivalent(existingEntries[primaryIndex], createdEntry))
                        {
                            replacementByIndex[primaryIndex] = createdEntry;
                            changed = true;
                        }
                    }
                    else
                    {
                        appendedEntries.Add(createdEntry);
                        changed = true;
                    }

                    break;
                }
                case StoreSyncActionKind.RefreshManaged:
                {
                    var refreshedEntry = CreateShortcutEntry(item, liveAppId, item.ExistingShortcut?.Entry).Entry;
                    if (TryFindMirrorEntryIndices(parsedEntries, item, liveAppId, out var refreshIndices))
                    {
                        foreach (var duplicateIndex in refreshIndices.Skip(1))
                        {
                            removalIndices.Add(duplicateIndex);
                            changed = true;
                        }

                        var refreshIndex = refreshIndices[0];
                        if (!ShortcutEntriesEquivalent(existingEntries[refreshIndex], refreshedEntry))
                        {
                            replacementByIndex[refreshIndex] = refreshedEntry;
                            changed = true;
                        }
                    }
                    else
                    {
                        appendedEntries.Add(refreshedEntry);
                        changed = true;
                    }

                    break;
                }
                case StoreSyncActionKind.AdoptExisting:
                {
                    if (!TryFindMirrorEntryIndices(parsedEntries, item, liveAppId, out var adoptIndices))
                    {
                        break;
                    }

                    foreach (var duplicateIndex in adoptIndices.Skip(1))
                    {
                        removalIndices.Add(duplicateIndex);
                        changed = true;
                    }

                    var adoptIndex = adoptIndices[0];
                    var adoptedEntry = CloneShortcutEntry(existingEntries[adoptIndex]);
                    if (ApplyManagedOwnershipMetadata(adoptedEntry, item.Game.StoreId, item.TitleId))
                    {
                        replacementByIndex[adoptIndex] = adoptedEntry;
                        changed = true;
                    }

                    break;
                }
            }
        }

        foreach (var cleanupCandidate in analysis.CleanupCandidates)
        {
            if (TryFindCleanupMirrorEntryIndex(parsedEntries, cleanupCandidate, out var cleanupIndex))
            {
                removalIndices.Add(cleanupIndex);
                changed = true;
            }
        }

        mirroredEntries = new List<Dictionary<string, object?>>(existingEntries.Count - removalIndices.Count + appendedEntries.Count);
        for (var index = 0; index < existingEntries.Count; index++)
        {
            if (removalIndices.Contains(index))
            {
                continue;
            }

            mirroredEntries.Add(
                replacementByIndex.TryGetValue(index, out var replacementEntry)
                    ? replacementEntry
                    : existingEntries[index]);
        }

        mirroredEntries.AddRange(appendedEntries);
        return changed;
    }

    private static bool TryFindMirrorEntryIndices(
        IReadOnlyList<ExistingShortcutEntry> parsedEntries,
        StoreSyncAnalysisItem item,
        uint targetAppId,
        out int[] indices)
    {
        var requiresStrictIdentity =
            string.Equals(item.Game.StoreId, UnifySteamStoreId, StringComparison.OrdinalIgnoreCase);
        indices = parsedEntries
            .Where(entry =>
                OwnershipRepairMatches(
                    entry,
                    item,
                    requiresStrictIdentity ? 0 : targetAppId) ||
                (!requiresStrictIdentity &&
                 item.ExistingShortcut is not null &&
                 entry.AppId == item.ExistingShortcut.AppId) ||
                (!string.IsNullOrWhiteSpace(entry.ManagedTitleId) &&
                 string.Equals(entry.ManagedTitleId, item.TitleId, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Index)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

        return indices.Length > 0;
    }

    private static bool TryFindCleanupMirrorEntryIndex(
        IReadOnlyList<ExistingShortcutEntry> parsedEntries,
        StoreSyncCleanupCandidate cleanupCandidate,
        out int index)
    {
        index = -1;
        var matchedEntry = parsedEntries.FirstOrDefault(entry =>
            entry.AppId == cleanupCandidate.ExistingShortcut.AppId ||
            (!string.IsNullOrWhiteSpace(entry.ManagedTitleId) &&
             string.Equals(entry.ManagedTitleId, cleanupCandidate.TitleId, StringComparison.OrdinalIgnoreCase)));
        if (matchedEntry is null)
        {
            return false;
        }

        index = matchedEntry.Index;
        return true;
    }

    private static Dictionary<string, object?> CloneShortcutEntry(Dictionary<string, object?> source)
    {
        var clone = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            clone[key] = value switch
            {
                Dictionary<string, object?> nestedDictionary => CloneShortcutEntry(nestedDictionary),
                _ => value,
            };
        }

        return clone;
    }

    private static bool ShortcutEntriesEquivalent(
        Dictionary<string, object?> left,
        Dictionary<string, object?> right)
    {
        return string.Equals(
            JsonSerializer.Serialize(CanonicalizeShortcutValue(left)),
            JsonSerializer.Serialize(CanonicalizeShortcutValue(right)),
            StringComparison.Ordinal);
    }

    private static object? CanonicalizeShortcutValue(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> dictionary => dictionary
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    pair => pair.Key,
                    pair => CanonicalizeShortcutValue(pair.Value),
                    StringComparer.OrdinalIgnoreCase),
            IEnumerable<object?> sequence => sequence.Select(CanonicalizeShortcutValue).ToArray(),
            _ => value,
        };
    }

    private async Task<OwnershipRepairResult> RepairManagedShortcutOwnershipAsync(
        string shortcutsPath,
        StoreSyncAnalysis analysis,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds,
        bool liveSyncUsed,
        CancellationToken cancellationToken,
        bool logFailure = true)
    {
        var repairItems = analysis.Items
            .Where(item => item.ActionKind is StoreSyncActionKind.Create or StoreSyncActionKind.RefreshManaged or StoreSyncActionKind.AdoptExisting)
            .ToArray();
        if (repairItems.Length == 0)
        {
            return new OwnershipRepairResult(true, string.Empty);
        }

        try
        {
            await RetryAsync(
                () =>
                {
                    var entries = _shortcutFile.Read(shortcutsPath).ToList();
                    if (liveSyncUsed &&
                        TryBuildLiveShortcutMirrorEntries(entries, analysis, liveShortcutAppIds, out var mirroredEntries))
                    {
                        RememberExpectedAutomationWrite(shortcutsPath, ExpectedAutomationWriteIgnoreDuration);
                        _shortcutFile.Write(shortcutsPath, mirroredEntries);
                        entries = _shortcutFile.Read(shortcutsPath).ToList();
                    }

                    if (entries.Count == 0)
                    {
                        if (liveSyncUsed)
                        {
                            throw new InvalidOperationException("Shortcuts file is not ready for ownership repair yet.");
                        }

                        return Task.CompletedTask;
                    }

                    var parsedEntries = entries
                        .Select((entry, index) => TryParseExistingShortcutEntry(entry, index))
                        .ToArray();
                    var changed = false;
                    var unresolvedCount = 0;

                    foreach (var item in repairItems)
                    {
                        var targetAppId = ResolveLiveShortcutAppId(item, liveShortcutAppIds);
                        var existingEntry = parsedEntries
                            .Where(entry => entry is not null)
                            .Cast<ExistingShortcutEntry>()
                            .FirstOrDefault(entry => OwnershipRepairMatches(entry, item, targetAppId));
                        if (existingEntry is null)
                        {
                            unresolvedCount++;
                            continue;
                        }

                        if (ApplyManagedOwnershipMetadata(existingEntry.Entry, item.Game.StoreId, item.TitleId))
                        {
                            changed = true;
                        }
                    }

                    if (unresolvedCount > 0 && liveSyncUsed)
                    {
                        throw new InvalidOperationException("Live shortcut entries have not been persisted yet.");
                    }

                    if (changed)
                    {
                        RememberExpectedAutomationWrite(shortcutsPath, ExpectedAutomationWriteIgnoreDuration);
                        _shortcutFile.Write(shortcutsPath, entries);
                    }

                    return Task.CompletedTask;
                },
                maxAttempts: liveSyncUsed ? 4 : 2,
                initialDelay: TimeSpan.FromMilliseconds(350),
                cancellationToken);
            return new OwnershipRepairResult(true, string.Empty);
        }
        catch (Exception exception)
        {
            if (logFailure)
            {
                _journal.Append("warn", "ownership-repair", "Store Sync could not fully repair shortcut ownership metadata.", exception.Message);
            }

            return new OwnershipRepairResult(false, exception.Message);
        }
    }

    private void ScheduleOwnershipRepairFollowUp(
        string shortcutsPath,
        StoreSyncAnalysis analysis,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds,
        bool omniLibraryOnly,
        string? omniLibraryStoreId,
        CancellationToken parentCancellationToken)
    {
        lock (_gate)
        {
            if (_activeOwnershipRepairFollowUpTask is { IsCompleted: false })
            {
                return;
            }

            parentCancellationToken.ThrowIfCancellationRequested();
            var cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(parentCancellationToken);
            _activeOwnershipRepairCancellation = cancellation;
            _activeOwnershipRepairIsOmniLibrary = omniLibraryOnly;
            _activeOwnershipRepairOmniLibraryStoreId = omniLibraryOnly
                ? omniLibraryStoreId?.Trim() ?? string.Empty
                : string.Empty;
            _activeOwnershipRepairFollowUpTask = Task.Run(async () =>
            {
                try
                {
                    OwnershipRepairResult? finalResult = null;

                    for (var attempt = 0; attempt < OwnershipRepairFollowUpDelays.Length; attempt += 1)
                    {
                        await Task.Delay(
                            OwnershipRepairFollowUpDelays[attempt],
                            cancellation.Token);
                        finalResult = await RepairManagedShortcutOwnershipAsync(
                            shortcutsPath,
                            analysis,
                            liveShortcutAppIds,
                            liveSyncUsed: true,
                            cancellation.Token,
                            logFailure: false);

                        if (finalResult?.Completed == true)
                        {
                            _journal.Append(
                                "info",
                                "ownership-repair",
                                attempt == 0
                                    ? "Store Sync repaired managed shortcut ownership metadata in the background."
                                    : $"Store Sync repaired managed shortcut ownership metadata in the background after retry {attempt + 1}.");
                            return;
                        }
                    }

                    _journal.Append(
                        "warn",
                        "ownership-repair",
                        "Store Sync background ownership repair still needs attention.",
                        finalResult?.Message ?? "Live shortcut metadata was not ready after multiple background repair attempts.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _journal.Append(
                        "warn",
                        "ownership-repair",
                        "Store Sync background ownership repair failed.",
                        exception.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(
                                _activeOwnershipRepairCancellation,
                                cancellation))
                        {
                            _activeOwnershipRepairCancellation = null;
                            _activeOwnershipRepairIsOmniLibrary = false;
                            _activeOwnershipRepairOmniLibraryStoreId = string.Empty;
                        }
                        _activeOwnershipRepairFollowUpTask = null;
                    }
                    cancellation.Dispose();
                }
            });
        }
    }

    private static bool OwnershipRepairMatches(
        ExistingShortcutEntry entry,
        StoreSyncAnalysisItem item,
        uint targetAppId)
    {
        if (targetAppId != 0 && entry.AppId == targetAppId)
        {
            return true;
        }

        if (!string.Equals(entry.ExecutablePath, NormalizePath(ResolveShortcutExecutablePath(item.Game)), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.ExecutablePath, NormalizePath(item.Game.ExecutablePath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedEntryLaunchOptions = NormalizeLaunchOptions(entry.LaunchOptions);
        var normalizedGameLaunchOptions = NormalizeLaunchOptions(ResolveShortcutLaunchOptions(item.Game));
        if ((!string.IsNullOrWhiteSpace(normalizedEntryLaunchOptions) || !string.IsNullOrWhiteSpace(normalizedGameLaunchOptions)) &&
            !string.Equals(normalizedEntryLaunchOptions, normalizedGameLaunchOptions, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedGameStartDirectory = NormalizePath(ResolveShortcutStartDirectory(item.Game));
        if (!string.IsNullOrWhiteSpace(normalizedGameStartDirectory) &&
            !string.IsNullOrWhiteSpace(entry.StartDirectory) &&
            !string.Equals(entry.StartDirectory, normalizedGameStartDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(NormalizeKey(entry.AppName), NormalizeKey(item.EffectiveTitle), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NormalizeKey(entry.AppName), NormalizeKey(item.Game.Title), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ApplyManagedOwnershipMetadata(
        Dictionary<string, object?> entry,
        string storeId,
        string titleId)
    {
        var changed = false;

        if (!entry.TryGetValue("ShortcutPath", out var shortcutPathValue) ||
            !string.Equals(Convert.ToString(shortcutPathValue), ManagedShortcutMarker, StringComparison.OrdinalIgnoreCase))
        {
            entry["ShortcutPath"] = ManagedShortcutMarker;
            changed = true;
        }

        if (!entry.TryGetValue("tags", out var tagsValue) ||
            tagsValue is not Dictionary<string, object?> tags)
        {
            tags = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            entry["tags"] = tags;
            changed = true;
        }

        changed |= SetShortcutTag(tags, "0", "Tools for Steam");
        changed |= SetShortcutTag(tags, "1", "Store Sync");
        changed |= SetShortcutTag(tags, "2", storeId);
        changed |= SetShortcutTag(tags, "3", titleId);
        return changed;
    }

    private static bool SetShortcutTag(Dictionary<string, object?> tags, string key, string value)
    {
        if (tags.TryGetValue(key, out var existingValue) &&
            string.Equals(Convert.ToString(existingValue), value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        tags[key] = value;
        return true;
    }

    private static async Task<T> RetryAsync<T>(
        Func<Task<T>> operation,
        int maxAttempts,
        TimeSpan initialDelay,
        CancellationToken cancellationToken)
    {
        if (maxAttempts <= 1)
        {
            return await operation();
        }

        var delay = initialDelay;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxAttempts)
            {
                lastException = exception;
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2d, 2000d));
            }
            catch (Exception exception)
            {
                lastException = exception;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("The retry operation did not complete successfully.");
    }

    private static Task RetryAsync(
        Func<Task> operation,
        int maxAttempts,
        TimeSpan initialDelay,
        CancellationToken cancellationToken)
    {
        return RetryAsync(
            async () =>
            {
                await operation();
                return true;
            },
            maxAttempts,
            initialDelay,
            cancellationToken);
    }

    private static LiveShortcutSyncPlan BuildLiveShortcutSyncPlan(
        StoreSyncConfiguration configuration,
        SteamProfileInfo profile,
        StoreSyncAnalysis analysis)
    {
        _ = configuration;
        var refreshItems = analysis.Items
            .Where(item => item.ActionKind == StoreSyncActionKind.RefreshManaged)
            .ToArray();
        var reusableDuplicateOmniLibraryTitleIds = refreshItems
            .Where(item => string.Equals(item.Game.StoreId, UnifySteamStoreId, StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Item = item,
                AppId = ResolveRefreshShortcutAppId(item),
            })
            .Where(candidate => candidate.AppId != 0)
            .GroupBy(candidate => candidate.AppId)
            .Where(group => group
                .Select(candidate => candidate.Item.TitleId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(candidate => candidate.Item.ExistingShortcut is not null)
                    .ThenBy(candidate => candidate.Item.TitleId, StringComparer.OrdinalIgnoreCase)
                    .First()
                    .Item
                    .TitleId);
        var createOperations = analysis.Items
            .Where(item => item.ActionKind == StoreSyncActionKind.Create)
            .Select(item => new LiveShortcutSyncCreateOperation(
                item.TitleId,
                item.TargetAppId,
                item.EffectiveTitle,
                NormalizePath(ResolveShortcutExecutablePath(item.Game)),
                NormalizePath(ResolveShortcutStartDirectory(item.Game)),
                ResolveShortcutLaunchOptions(item.Game),
                NormalizePath(item.Game.ExecutablePath)))
            .ToArray();

        var updateOperations = refreshItems
            .Select(item =>
            {
                var appId = ResolveRefreshShortcutAppId(item);
                var duplicateOmniLibraryAppId =
                    reusableDuplicateOmniLibraryTitleIds.TryGetValue(appId, out var reusableTitleId);
                var forceCreate =
                    item.ExistingShortcut is null ||
                    (duplicateOmniLibraryAppId &&
                     !string.Equals(item.TitleId, reusableTitleId, StringComparison.OrdinalIgnoreCase));
                return new LiveShortcutSyncUpdateOperation(
                    item.TitleId,
                    appId,
                    item.TargetAppId,
                    forceCreate,
                    item.EffectiveTitle,
                    NormalizePath(ResolveShortcutExecutablePath(item.Game)),
                    NormalizePath(ResolveShortcutStartDirectory(item.Game)),
                    ResolveShortcutLaunchOptions(item.Game),
                    NormalizePath(item.Game.ExecutablePath));
            })
            .ToArray();

        var removeOperations = analysis.CleanupCandidates
            .Select(candidate => new LiveShortcutSyncRemoveOperation(
                candidate.TitleId,
                candidate.ExistingShortcut.AppId))
            .ToArray();

        return new LiveShortcutSyncPlan(
            profile.AccountId,
            configuration.DownloadArtwork,
            createOperations,
            updateOperations,
            removeOperations);
    }

    private static uint ResolveRefreshShortcutAppId(StoreSyncAnalysisItem item)
    {
        if (item.ExistingShortcut is not null && item.ExistingShortcut.AppId != 0)
        {
            return item.ExistingShortcut.AppId;
        }

        if (item.ManifestEntry is not null && item.ManifestEntry.AppId != 0)
        {
            return item.ManifestEntry.AppId;
        }

        return item.TargetAppId;
    }

    private static string BuildLiveShortcutSyncExpression(LiveShortcutSyncPlan plan)
    {
        var planJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return $$"""
(async () => {
  try {
    const plan = {{planJson}};
    const apps = window.SteamClient?.Apps;
    if (!apps) {
      return {
        available: false,
        success: false,
        attemptedCount: 0,
        appIdsByTitleId: {},
        errors: ["SteamClient.Apps is not available in SharedJSContext."]
      };
    }

    const invoke = async (name, ...args) => {
      if (typeof apps[name] !== "function") {
        throw new Error(`SteamClient.Apps.${name} is not available.`);
      }

      return await apps[name](...args);
    };

    const toText = (value) => typeof value === "string" ? value : value == null ? "" : String(value);
    const appIdsByTitleId = {};
    const errors = [];
    let attemptedCount = 0;

    const applyShortcutFields = async (appId, operation) => {
      await invoke("SetShortcutName", appId, operation.name);
      await invoke("SetShortcutExe", appId, operation.executablePath);
      await invoke("SetShortcutStartDir", appId, operation.startDirectory);
      await invoke("SetShortcutLaunchOptions", appId, toText(operation.launchOptions));

      if (typeof apps.SetShortcutIcon === "function" && operation.iconPath) {
        await invoke("SetShortcutIcon", appId, operation.iconPath);
      }

      if (typeof apps.SetShortcutIsVR === "function") {
        await invoke("SetShortcutIsVR", appId, false);
      }

      if (typeof apps.SetShortcutSortAs === "function") {
        await invoke("SetShortcutSortAs", appId, operation.name);
      }
    };

    const tryApplyToShortcut = async (appId, operation) => {
      const normalizedAppId = Number(appId) >>> 0;
      if (!Number.isFinite(normalizedAppId) || normalizedAppId <= 0) {
        return false;
      }

      try {
        await applyShortcutFields(normalizedAppId, operation);
        return true;
      } catch {
        return false;
      }
    };

    for (const operation of plan.createOperations ?? []) {
      attemptedCount += 1;
      try {
        const createdAppId = Number(await invoke(
          "AddShortcut",
          operation.name,
          operation.executablePath,
          toText(operation.launchOptions),
          operation.startDirectory));
        if (!Number.isFinite(createdAppId) || createdAppId <= 0) {
          throw new Error("Steam returned an invalid shortcut id.");
        }

        const liveAppId = createdAppId >>> 0;
        await applyShortcutFields(liveAppId, operation);
        appIdsByTitleId[operation.titleId] = liveAppId;
      } catch (error) {
        errors.push(`Create ${operation.titleId}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }

    for (const operation of plan.updateOperations ?? []) {
      attemptedCount += 1;
      try {
        let liveAppId = operation.appId >>> 0;
        let appliedToExistingShortcut = false;
        if (!operation.forceCreate) {
          appliedToExistingShortcut = await tryApplyToShortcut(liveAppId, operation);
        }

        if (!appliedToExistingShortcut && !operation.forceCreate) {
          const fallbackAppId = operation.targetAppId >>> 0;
          if (fallbackAppId && fallbackAppId !== liveAppId) {
            liveAppId = fallbackAppId;
            appliedToExistingShortcut = await tryApplyToShortcut(liveAppId, operation);
          }
        }

        if (!appliedToExistingShortcut) {
          const createdAppId = Number(await invoke(
            "AddShortcut",
            operation.name,
            operation.executablePath,
            toText(operation.launchOptions),
            operation.startDirectory));
          if (!Number.isFinite(createdAppId) || createdAppId <= 0) {
            throw new Error("Steam could not find the managed live shortcut to refresh or recreate.");
          }

          liveAppId = createdAppId >>> 0;
          await applyShortcutFields(liveAppId, operation);
        }

        appIdsByTitleId[operation.titleId] = liveAppId;
      } catch (error) {
        errors.push(`Refresh ${operation.titleId}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }

    for (const operation of plan.removeOperations ?? []) {
      attemptedCount += 1;
      try {
        await invoke("RemoveShortcut", operation.appId >>> 0);
      } catch (error) {
        errors.push(`Cleanup ${operation.titleId}: ${error instanceof Error ? error.message : String(error)}`);
      }
    }

    return {
      available: true,
      success: errors.length === 0,
      attemptedCount,
      appIdsByTitleId,
      errors
    };
  } catch (error) {
    return {
      available: false,
      success: false,
      attemptedCount: 0,
      appIdsByTitleId: {},
      errors: [error instanceof Error ? error.message : String(error)]
    };
  }
})()
""";
    }

    private static LiveShortcutSyncResponse? DeserializeLiveShortcutSyncResponse(object? value)
    {
        if (value is null)
        {
            return null;
        }

        JsonElement jsonElement;
        if (value is JsonElement element)
        {
            jsonElement = element;
        }
        else
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            jsonElement = document.RootElement.Clone();
        }

        if (jsonElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var available = jsonElement.TryGetProperty("available", out var availableElement) &&
                        availableElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                        availableElement.GetBoolean();
        var success = jsonElement.TryGetProperty("success", out var successElement) &&
                      successElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                      successElement.GetBoolean();
        var attemptedCount = jsonElement.TryGetProperty("attemptedCount", out var attemptedCountElement) &&
                             attemptedCountElement.TryGetInt32(out var attemptedCountValue)
            ? attemptedCountValue
            : 0;

        var appIdsByTitleId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (jsonElement.TryGetProperty("appIdsByTitleId", out var appIdsElement) &&
            appIdsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in appIdsElement.EnumerateObject())
            {
                if (property.Value.TryGetUInt32(out var appId) && appId != 0)
                {
                    appIdsByTitleId[property.Name] = appId;
                }
            }
        }

        var errors = new List<string>();
        if (jsonElement.TryGetProperty("errors", out var errorsElement) &&
            errorsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var errorElement in errorsElement.EnumerateArray())
            {
                var text = errorElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    errors.Add(text);
                }
            }
        }

        return new LiveShortcutSyncResponse(
            available,
            success,
            attemptedCount,
            appIdsByTitleId,
            errors);
    }

    private void BackupShortcuts(string shortcutsPath, DateTimeOffset startedAt)
    {
        var sourceDirectory = Path.GetDirectoryName(shortcutsPath);
        if (sourceDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(sourceDirectory);
        if (!File.Exists(shortcutsPath))
        {
            return;
        }

        var backupDirectory = Path.Combine(sourceDirectory, "steamloader-backups");
        Directory.CreateDirectory(backupDirectory);

        var backupName = $"shortcuts-{startedAt:yyyyMMdd-HHmmss}.vdf";
        File.Copy(shortcutsPath, Path.Combine(backupDirectory, backupName), overwrite: true);
    }

    private async Task WarmArtworkMatchCacheAsync(
        StoreSyncConfiguration configuration,
        IReadOnlyList<StoreSyncAnalysisItem> items,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        foreach (var item in items.Where(item => item.ActionKind != StoreSyncActionKind.Excluded))
        {
            if (configuration.ArtworkMatchCache.TryGetValue(item.TitleId, out var existingCache) &&
                existingCache is not null &&
                existingCache.GameId > 0)
            {
                continue;
            }

            var match = await _artworkDownloader.ResolveMatchAsync(
                item.EffectiveArtworkTitle,
                new[] { item.Game.Title, item.EffectiveTitle, item.Game.ExecutablePath, item.Game.StartDirectory },
                apiKey,
                cancellationToken);
            if (match is null)
            {
                continue;
            }

            configuration.ArtworkMatchCache[item.TitleId] = new StoreSyncArtworkCacheEntry
            {
                GameId = match.GameId,
                MatchName = string.IsNullOrWhiteSpace(match.MatchName)
                    ? item.EffectiveArtworkTitle
                    : match.MatchName,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            if (configuration.Manifest.TryGetValue(item.TitleId, out var manifestEntry) && manifestEntry is not null)
            {
                manifestEntry.SteamGridDbGameId = match.GameId;
            }
        }
    }

    private static bool ShouldUpdateArtworkForItem(
        StoreSyncAnalysisItem item,
        uint appId,
        StoreSyncArtworkCacheEntry? artworkCache,
        string gridDirectory)
    {
        if (item.ActionKind is StoreSyncActionKind.Excluded or StoreSyncActionKind.SkipExisting)
        {
            return false;
        }

        if (appId == 0)
        {
            return false;
        }

        // Existing shortcuts keep the artwork they already have. We only
        // rehydrate artwork when a managed shortcut lost its primary files,
        // or when TFS is creating a brand-new shortcut from scratch.
        if (item.ActionKind == StoreSyncActionKind.AdoptExisting)
        {
            return false;
        }

        var hasPrimaryArtwork = HasPrimaryArtworkFiles(gridDirectory, appId);
        if (!hasPrimaryArtwork)
        {
            var lastAttemptAtUtc = item.ManifestEntry?.LastArtworkAttemptAtUtc;
            if (lastAttemptAtUtc.HasValue &&
                DateTimeOffset.UtcNow - lastAttemptAtUtc.Value < IncompleteArtworkRetryDelay)
            {
                return false;
            }

            return true;
        }

        if (item.ActionKind == StoreSyncActionKind.Create)
        {
            return true;
        }

        var manifestEntry = item.ManifestEntry;
        if (manifestEntry is null)
        {
            return true;
        }

        if (IsWindowsResourcePlaceholder(manifestEntry.ArtworkTitle) &&
            !string.Equals(manifestEntry.ArtworkTitle, item.EffectiveArtworkTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool HasPrimaryArtworkFiles(string gridDirectory, uint appId)
    {
        if (appId == 0 || string.IsNullOrWhiteSpace(gridDirectory) || !Directory.Exists(gridDirectory))
        {
            return false;
        }

        var gridId = SteamShortcutIds.BuildGridId(appId);
        return HasArtworkVariant(gridDirectory, gridId) &&
               HasArtworkVariant(gridDirectory, $"{gridId}p");
    }

    private static bool HasArtworkVariant(string gridDirectory, string stem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            if (File.Exists(Path.Combine(gridDirectory, stem + extension)))
            {
                return true;
            }
        }

        return false;
    }

    private void CloseSteamForSync()
    {
        var steamProcesses = GetSteamProcesses().ToList();
        if (steamProcesses.Count == 0)
        {
            return;
        }

        foreach (var process in steamProcesses)
        {
            try
            {
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
            }
        }

        var steamExePath = _steamInstallationService.ResolveSteamExecutablePath();
        if (!string.IsNullOrWhiteSpace(steamExePath) && File.Exists(steamExePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = steamExePath,
                    Arguments = "-shutdown",
                    WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.Dispose();
            }
            catch
            {
            }
        }

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (!IsSteamRunning())
            {
                return;
            }

            Thread.Sleep(300);
        }

        if (IsSteamRunning())
        {
            throw new InvalidOperationException("Steam did not finish its official shutdown in time. Close Steam manually and try again.");
        }
    }

    private void LaunchSteam(bool launchBigPicture)
    {
        var steamExePath = _steamInstallationService.ResolveSteamExecutablePath();
        if (string.IsNullOrWhiteSpace(steamExePath) || !File.Exists(steamExePath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = steamExePath,
            Arguments = SteamClientLaunchService.BuildSteamLaunchArguments(launchBigPicture),
            WorkingDirectory = Path.GetDirectoryName(steamExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    private static StoreSyncStoreConfiguration GetStoreConfiguration(StoreSyncConfiguration configuration, string storeId)
    {
        if (!configuration.Stores.TryGetValue(storeId, out var storeConfiguration) || storeConfiguration is null)
        {
            storeConfiguration = new StoreSyncStoreConfiguration();
            configuration.Stores[storeId] = storeConfiguration;
        }

        storeConfiguration.ScanPath ??= string.Empty;
        storeConfiguration.AdditionalScanPaths ??= [];
        return storeConfiguration;
    }

    private static UnifySteamStoreConfiguration GetUnifySteamStoreConfiguration(StoreSyncConfiguration configuration, string storeId)
    {
        configuration.UnifySteam ??= new UnifySteamConfiguration();
        configuration.UnifySteam.Stores ??= new Dictionary<string, UnifySteamStoreConfiguration>(StringComparer.OrdinalIgnoreCase);

        if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var storeConfiguration) || storeConfiguration is null)
        {
            storeConfiguration = new UnifySteamStoreConfiguration();
            configuration.UnifySteam.Stores[storeId] = storeConfiguration;
        }

        storeConfiguration.ToolPath ??= string.Empty;
        storeConfiguration.AuthPath ??= string.Empty;
        storeConfiguration.Cache ??= new UnifySteamLibraryCache();
        storeConfiguration.Cache.Games ??= [];
        return storeConfiguration;
    }

    private static string ResolveValidatedDirectoryPath(
        string path,
        string missingValueMessage,
        string missingDirectoryMessage)
    {
        var trimmedPath = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            throw new InvalidOperationException(missingValueMessage);
        }

        var fullPath = Path.GetFullPath(trimmedPath);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException(missingDirectoryMessage);
        }

        return fullPath;
    }

    private static IReadOnlyList<string> NormalizeValidatedDirectoryPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        return paths
            .Select(path => ResolveValidatedDirectoryPath(
                path,
                "Folder paths are required.",
                $"The folder `{path?.Trim()}` does not exist."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildShortcutsPath(string steamRootPath, string accountId)
    {
        return Path.Combine(
            steamRootPath,
            "userdata",
            accountId,
            "config",
            "shortcuts.vdf");
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<EpicManifestEntry> LoadEpicManifestEntries()
    {
        var manifestRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "EpicGamesLauncher",
            "Data",
            "Manifests");

        if (!Directory.Exists(manifestRoot))
        {
            return [];
        }

        var entries = new List<EpicManifestEntry>();
        foreach (var manifestPath in Directory.EnumerateFiles(manifestRoot, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                entries.Add(new EpicManifestEntry(
                    AppName: GetJsonString(root, "AppName"),
                    MainGameAppName: GetJsonString(root, "MainGameAppName"),
                    CatalogNamespace: GetJsonString(root, "CatalogNamespace"),
                    CatalogItemId: GetJsonString(root, "CatalogItemId"),
                    InstallLocation: GetJsonString(root, "InstallLocation"),
                    DisplayName: GetJsonString(root, "DisplayName"),
                    VaultTitleText: GetJsonString(root, "VaultTitleText"),
                    MandatoryAppFolderName: GetJsonString(root, "MandatoryAppFolderName"),
                    LaunchExecutable: GetJsonString(root, "LaunchExecutable"),
                    LaunchCommand: GetJsonString(root, "LaunchCommand")));
            }
            catch
            {
                // One broken Epic manifest should not hide the rest of the library.
            }
        }

        return entries;
    }

    private static EpicManifestEntry? FindEpicManifest(
        JsonElement launcherEntry,
        string? installLocation,
        IReadOnlyList<EpicManifestEntry> manifests)
    {
        if (manifests.Count == 0)
        {
            return null;
        }

        var normalizedInstallLocation = NormalizePath(installLocation);
        if (!string.IsNullOrWhiteSpace(normalizedInstallLocation))
        {
            var installMatch = manifests.FirstOrDefault(manifest =>
                string.Equals(NormalizePath(manifest.InstallLocation), normalizedInstallLocation, StringComparison.OrdinalIgnoreCase));
            if (installMatch is not null)
            {
                return installMatch;
            }
        }

        var appName = GetJsonString(launcherEntry, "AppName");
        var artifactId = GetJsonString(launcherEntry, "ArtifactId");
        var namespaceId = GetJsonString(launcherEntry, "NamespaceId");
        var itemId = GetJsonString(launcherEntry, "ItemId");

        return manifests.FirstOrDefault(manifest =>
            StringsEqual(manifest.AppName, appName)
            || StringsEqual(manifest.MainGameAppName, appName)
            || StringsEqual(manifest.AppName, artifactId)
            || StringsEqual(manifest.MainGameAppName, artifactId)
            || (StringsEqual(manifest.CatalogNamespace, namespaceId) && StringsEqual(manifest.CatalogItemId, itemId)));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static bool StringsEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEpicStoreItemId(
        JsonElement launcherEntry,
        EpicManifestEntry? manifest,
        string? installLocation,
        string executablePath)
    {
        var namespaceId = FirstNonEmpty(GetJsonString(launcherEntry, "NamespaceId"), manifest?.CatalogNamespace);
        var itemId = FirstNonEmpty(GetJsonString(launcherEntry, "ItemId"), manifest?.CatalogItemId);
        if (!string.IsNullOrWhiteSpace(namespaceId) && !string.IsNullOrWhiteSpace(itemId))
        {
            return BuildStoreItemId("epic", namespaceId, itemId);
        }

        var appName = FirstNonEmpty(
            GetJsonString(launcherEntry, "AppName"),
            GetJsonString(launcherEntry, "ArtifactId"),
            manifest?.MainGameAppName,
            manifest?.AppName);
        if (!string.IsNullOrWhiteSpace(appName))
        {
            return BuildStoreItemId("epic", appName);
        }

        return BuildStoreItemId("epic", installLocation, executablePath);
    }

    private static string BuildStoreItemId(params string?[] parts)
    {
        return string.Join(
            "|",
            parts
                .Select(NormalizeStoreItemPart)
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string NormalizeStoreItemPart(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(trimmed) ||
            trimmed.Contains(Path.DirectorySeparatorChar) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            return NormalizePath(trimmed).ToLowerInvariant();
        }

        return trimmed.ToLowerInvariant();
    }

    private static string ResolveExecutablePath(string installPath, string? executableHint)
    {
        if (!string.IsNullOrWhiteSpace(executableHint))
        {
            var combinedPath = executableHint;
            if (!Path.IsPathRooted(combinedPath) && !string.IsNullOrWhiteSpace(installPath))
            {
                combinedPath = Path.Combine(installPath, combinedPath);
            }

            if (File.Exists(combinedPath))
            {
                return Path.GetFullPath(combinedPath);
            }
        }

        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return string.Empty;
        }

        return FindBestExecutable(installPath) ?? string.Empty;
    }

    private static string? FindBestExecutable(string directoryPath)
    {
        return FindExecutableCandidates(directoryPath, maximumDepth: 2)
            .OrderBy(path => ScoreExecutable(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<string> FindExecutableCandidates(string directoryPath, int maximumDepth)
    {
        var rootDirectory = new DirectoryInfo(directoryPath);
        if (!rootDirectory.Exists)
        {
            yield break;
        }

        foreach (var file in EnumerateFiles(rootDirectory, maximumDepth))
        {
            if (!file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsIgnoredExecutable(file.Name))
            {
                continue;
            }

            yield return file.FullName;
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo rootDirectory, int maximumDepth)
    {
        var queue = new Queue<(DirectoryInfo Directory, int Depth)>();
        queue.Enqueue((rootDirectory, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            FileInfo[] files;
            try
            {
                files = directory.GetFiles();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maximumDepth)
            {
                continue;
            }

            DirectoryInfo[] directories;
            try
            {
                directories = directory.GetDirectories();
            }
            catch
            {
                continue;
            }

            foreach (var childDirectory in directories)
            {
                queue.Enqueue((childDirectory, depth + 1));
            }
        }
    }

    private static bool IsIgnoredExecutable(string fileName)
    {
        var lowerName = fileName.ToLowerInvariant();

        return lowerName.Contains("unins")
            || lowerName.Contains("uninstall")
            || lowerName.Contains("crashreport")
            || lowerName.Contains("vc_redist")
            || lowerName.Contains("eosbootstrapper")
            || lowerName.Equals("activationui.exe", StringComparison.OrdinalIgnoreCase)
            || lowerName.Equals("setup.exe", StringComparison.OrdinalIgnoreCase)
            || lowerName.Equals("updater.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreExecutable(string path)
    {
        var lowerPath = path.ToLowerInvariant();
        var score = 0;

        if (lowerPath.Contains("shipping"))
        {
            score -= 3;
        }

        if (lowerPath.Contains("\\content\\"))
        {
            score -= 2;
        }

        if (lowerPath.Contains("launcher"))
        {
            score += 4;
        }

        return score;
    }

    private static IEnumerable<string> EnumerateCustomExecutableCandidates(string rootDirectory, int maximumDepth)
    {
        if (!Directory.Exists(rootDirectory))
        {
            yield break;
        }

        var pendingDirectories = new Stack<(string Directory, int Depth)>();
        pendingDirectories.Push((rootDirectory, 0));

        while (pendingDirectories.Count > 0)
        {
            var (directory, depth) = pendingDirectories.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.exe");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (IgnoreCustomExecutableTokens.Any(token =>
                        fileName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return file;
            }

            if (depth >= maximumDepth)
            {
                continue;
            }

            IEnumerable<string> subDirectories;
            try
            {
                subDirectories = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var subDirectory in subDirectories)
            {
                var directoryName = Path.GetFileName(subDirectory);
                if (IgnoreCustomDirectoryTokens.Any(token =>
                        directoryName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                pendingDirectories.Push((subDirectory, depth + 1));
            }
        }
    }

    private static bool ShouldSkipCustomCandidateDirectory(string candidatePath)
    {
        var directoryName = Path.GetFileName(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return false;
        }

        return IgnoreCustomDirectoryTokens.Any(token =>
            directoryName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveCustomGameRoot(string rootPath, string executablePath)
    {
        var currentDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(currentDirectory))
        {
            return rootPath;
        }

        var normalizedRoot = NormalizePath(rootPath);
        while (!string.IsNullOrWhiteSpace(currentDirectory)
               && NormalizePath(currentDirectory).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var directoryName = Path.GetFileName(currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!IsStructuralDirectory(directoryName))
            {
                return currentDirectory;
            }

            var parent = Directory.GetParent(currentDirectory);
            if (parent is null)
            {
                break;
            }

            currentDirectory = parent.FullName;
        }

        return rootPath;
    }

    private static bool IsStructuralDirectory(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return false;
        }

        var normalizedDirectoryName = NormalizeToken(directoryName);
        return StructuralDirectoryTokens.Any(token =>
            string.Equals(normalizedDirectoryName, NormalizeToken(token), StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreCustomExecutable(string rootDirectory, string executablePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(executablePath);
        if (IgnoreCustomExecutableTokens.Any(token =>
                fileName.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return -100;
        }

        var normalizedRoot = NormalizeToken(Path.GetFileName(rootDirectory));
        var normalizedFile = NormalizeToken(fileName);
        var normalizedParent = NormalizeToken(Path.GetFileName(Path.GetDirectoryName(executablePath) ?? string.Empty));

        var score = 20;
        if (normalizedRoot == normalizedFile)
        {
            score += 90;
        }
        else if (!string.IsNullOrWhiteSpace(normalizedRoot)
                 && !string.IsNullOrWhiteSpace(normalizedFile)
                 && (normalizedFile.Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                     || normalizedRoot.Contains(normalizedFile, StringComparison.OrdinalIgnoreCase)))
        {
            score += 55;
        }

        if (normalizedParent == normalizedRoot)
        {
            score += 25;
        }

        if (executablePath.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (executablePath.Contains(@"\Win64\", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (executablePath.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (executablePath.Contains(@"\Engine\", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains(@"\Editor\", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains(@"\Support\", StringComparison.OrdinalIgnoreCase))
        {
            score -= 120;
        }

        try
        {
            var fileInfo = new FileInfo(executablePath);
            score += (int)Math.Min(18, fileInfo.Length / (20 * 1024 * 1024));
        }
        catch
        {
        }

        return score;
    }

    private static string BuildDetectedTitle(string candidateRoot, string executablePath)
    {
        var metadataTitle = TryReadExecutableTitle(executablePath);
        if (!string.IsNullOrWhiteSpace(metadataTitle))
        {
            return PrettifyTitle(metadataTitle);
        }

        return PrettifyTitle(Path.GetFileName(candidateRoot));
    }

    private static string ResolveXboxTitle(
        string candidateRoot,
        string executablePath,
        string? overrideDisplayName,
        string? shellDisplayName,
        string? shellDescription)
    {
        return FirstMeaningfulTitle(
                   overrideDisplayName,
                   shellDisplayName,
                   shellDescription,
                   TryReadXboxTitleFromAppxManifest(candidateRoot),
                   TryReadExecutableTitle(executablePath),
                   Path.GetFileName(candidateRoot))
               ?? BuildDetectedTitle(candidateRoot, executablePath);
    }

    private static string? TryReadExecutableTitle(string executablePath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            foreach (var value in new[] { versionInfo.ProductName, versionInfo.FileDescription })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var trimmed = value.Trim();
                if (IgnoreCustomExecutableTokens.Any(token =>
                        trimmed.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return trimmed;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryReadXboxTitleFromAppxManifest(string candidateRoot)
    {
        var appxManifestPath = FindXboxAppxManifest(candidateRoot);
        if (string.IsNullOrWhiteSpace(appxManifestPath) || !File.Exists(appxManifestPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(appxManifestPath, LoadOptions.None);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            var manifestNamespace = root.Name.Namespace;
            var propertiesElement = root.Element(manifestNamespace + "Properties");
            var displayName = propertiesElement?.Element(manifestNamespace + "DisplayName")?.Value;

            var visualElementsDisplayName = root
                .Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "VisualElements", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("DisplayName")
                ?.Value;

            return FirstMeaningfulTitle(
                displayName,
                visualElementsDisplayName,
                Path.GetFileName(candidateRoot));
        }
        catch
        {
            return null;
        }
    }

    private static string? FindXboxAppxManifest(string candidateRoot)
    {
        foreach (var manifestPath in new[]
        {
            Path.Combine(candidateRoot, "appxmanifest.xml"),
            Path.Combine(candidateRoot, "Content", "appxmanifest.xml")
        })
        {
            if (File.Exists(manifestPath))
            {
                return manifestPath;
            }
        }

        try
        {
            return Directory.EnumerateFiles(candidateRoot, "appxmanifest.xml", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstMeaningfulTitle(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (IsWindowsResourcePlaceholder(trimmed))
            {
                continue;
            }

            return PrettifyTitle(trimmed);
        }

        return null;
    }

    private static bool IsWindowsResourcePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("ms resource:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string? value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return (path ?? string.Empty)
                .Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string PrettifyTitle(string value)
    {
        var cleaned = value.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ').Trim();
        cleaned = Regex.Replace(cleaned, "(?<=[a-z0-9])(?=[A-Z])", " ");
        cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? value : cleaned;
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeLaunchOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value.Trim(), "\\s+", " ");
    }

    private static string CreateDetectedTitleId(StoreGameEntry game)
    {
        var key = ResolveStoreGameIdentityKey(game);

        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return $"{game.StoreId}-{Convert.ToHexString(hash[..6]).ToLowerInvariant()}";
    }

    private static IEnumerable<StoreGameEntry> BuildDistinctDiscoveredGames(IEnumerable<StoreGameEntry> games)
    {
        return games
            .GroupBy(ResolveStoreGameIdentityKey)
            .Select(group => group
                .OrderByDescending(game => !string.IsNullOrWhiteSpace(game.LaunchOptions))
                .ThenBy(game => game.StoreId, StringComparer.OrdinalIgnoreCase)
                .First());
    }

    private static string ResolveStoreGameIdentityKey(StoreGameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.StoreItemId))
        {
            return $"{NormalizeKey(game.StoreId)}|item|{NormalizeStoreItemId(game.StoreItemId)}";
        }

        var normalizedExecutablePath = NormalizePath(game.ExecutablePath);
        var normalizedStartDirectory = NormalizePath(game.StartDirectory);
        return !string.IsNullOrWhiteSpace(normalizedExecutablePath)
            ? $"{NormalizeKey(game.StoreId)}|exe|{normalizedExecutablePath.ToLowerInvariant()}"
            : $"{NormalizeKey(game.StoreId)}|title|{NormalizeKey(game.Title)}|dir|{normalizedStartDirectory.ToLowerInvariant()}";
    }

    private static string NormalizeStoreItemId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalizedPath = NormalizePath(value);
        return !string.IsNullOrWhiteSpace(normalizedPath)
            ? normalizedPath.ToLowerInvariant()
            : NormalizeKey(value);
    }

    private static ExistingShortcutEntry? TryParseExistingShortcutEntry(
        Dictionary<string, object?> entry,
        int index)
    {
        var executablePath = NormalizePath(UnquotePath(ReadShortcutString(entry, "Exe")));
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (!TryReadShortcutAppId(entry, out var appId))
        {
            return null;
        }

        return new ExistingShortcutEntry(
            Index: index,
            AppId: appId,
            AppName: ReadShortcutString(entry, "appname"),
            ExecutablePath: executablePath,
            StartDirectory: NormalizePath(UnquotePath(ReadShortcutString(entry, "StartDir"))),
            LaunchOptions: ReadShortcutString(entry, "LaunchOptions"),
            IsManaged: SteamShortcutFile.HasManagedTag(entry),
            ManagedStoreId: ReadShortcutTagString(entry, "2"),
            ManagedTitleId: ReadShortcutTagString(entry, "3"),
            Entry: entry);
    }

    private static bool TryFindExistingShortcut(
        IReadOnlyList<ExistingShortcutEntry> existingEntries,
        StoreGameEntry game,
        StoreSyncManifestEntry? manifestEntry,
        string effectiveTitle,
        out ExistingShortcutEntry? existingShortcut)
    {
        var normalizedExecutablePath = NormalizePath(ResolveShortcutExecutablePath(game));
        var normalizedSourceExecutablePath = NormalizePath(game.ExecutablePath);
        var normalizedStartDirectory = NormalizePath(ResolveShortcutStartDirectory(game));
        var normalizedLaunchOptions = NormalizeLaunchOptions(ResolveShortcutLaunchOptions(game));
        var normalizedRawTitle = NormalizeKey(game.Title);
        var normalizedEffectiveTitle = NormalizeKey(effectiveTitle);
        var expectedAppId = SteamShortcutIds.ComputeAppId(effectiveTitle, normalizedExecutablePath);
        var manifestAppId = manifestEntry?.AppId ?? 0;

        if (string.Equals(game.StoreId, UnifySteamStoreId, StringComparison.OrdinalIgnoreCase))
        {
            return TryFindExistingUnifySteamShortcut(
                existingEntries,
                game,
                manifestEntry,
                normalizedExecutablePath,
                normalizedStartDirectory,
                normalizedLaunchOptions,
                normalizedRawTitle,
                normalizedEffectiveTitle,
                expectedAppId,
                manifestAppId,
                out existingShortcut);
        }

        existingShortcut = existingEntries
            .Where(entry =>
                string.Equals(entry.ExecutablePath, normalizedExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.ExecutablePath, normalizedSourceExecutablePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.IsManaged)
            .ThenBy(entry => string.Equals(NormalizeLaunchOptions(entry.LaunchOptions), normalizedLaunchOptions, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => string.Equals(entry.StartDirectory, normalizedStartDirectory, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry =>
                string.Equals(NormalizeKey(entry.AppName), normalizedEffectiveTitle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeKey(entry.AppName), normalizedRawTitle, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1)
            .ThenBy(entry => entry.AppId == manifestAppId ? 0 : entry.AppId == expectedAppId ? 1 : 2)
            .FirstOrDefault()
            ?? (manifestAppId != 0 ? existingEntries.FirstOrDefault(entry => entry.AppId == manifestAppId) : null)
            ?? existingEntries.FirstOrDefault(entry => entry.AppId == expectedAppId)
            ?? existingEntries.FirstOrDefault(entry =>
                (string.Equals(NormalizeKey(entry.AppName), normalizedEffectiveTitle, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeKey(entry.AppName), normalizedRawTitle, StringComparison.OrdinalIgnoreCase))
                && string.Equals(entry.StartDirectory, normalizedStartDirectory, StringComparison.OrdinalIgnoreCase));

        existingShortcut ??= existingEntries
            .Where(entry =>
                entry.IsManaged &&
                string.Equals(entry.ManagedStoreId, game.StoreId, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(NormalizeKey(entry.AppName), normalizedEffectiveTitle, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(NormalizeKey(entry.AppName), normalizedRawTitle, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry =>
                !string.IsNullOrWhiteSpace(entry.ManagedTitleId) &&
                manifestEntry is not null &&
                string.Equals(entry.ManagedTitleId, manifestEntry.TitleId, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : string.Equals(entry.StartDirectory, normalizedStartDirectory, StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 2)
            .FirstOrDefault();

        return existingShortcut is not null;
    }

    private static bool TryFindExistingUnifySteamShortcut(
        IReadOnlyList<ExistingShortcutEntry> existingEntries,
        StoreGameEntry game,
        StoreSyncManifestEntry? manifestEntry,
        string normalizedExecutablePath,
        string normalizedStartDirectory,
        string normalizedLaunchOptions,
        string normalizedRawTitle,
        string normalizedEffectiveTitle,
        uint expectedAppId,
        uint manifestAppId,
        out ExistingShortcutEntry? existingShortcut)
    {
        static bool TitleMatches(ExistingShortcutEntry entry, string normalizedRawTitle, string normalizedEffectiveTitle)
        {
            var normalizedEntryTitle = NormalizeKey(entry.AppName);
            return string.Equals(normalizedEntryTitle, normalizedEffectiveTitle, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedEntryTitle, normalizedRawTitle, StringComparison.OrdinalIgnoreCase);
        }

        var manifestTitleId = manifestEntry?.TitleId ?? string.Empty;
        existingShortcut = existingEntries
            .Where(entry => string.Equals(entry.ExecutablePath, normalizedExecutablePath, StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                var launchOptionsMatch =
                    !string.IsNullOrWhiteSpace(normalizedLaunchOptions) &&
                    string.Equals(NormalizeLaunchOptions(entry.LaunchOptions), normalizedLaunchOptions, StringComparison.OrdinalIgnoreCase);
                var startDirectoryMatch =
                    string.IsNullOrWhiteSpace(normalizedStartDirectory) ||
                    string.IsNullOrWhiteSpace(entry.StartDirectory) ||
                    string.Equals(entry.StartDirectory, normalizedStartDirectory, StringComparison.OrdinalIgnoreCase);
                var titleMatches = TitleMatches(entry, normalizedRawTitle, normalizedEffectiveTitle);
                var managedTitleMatch =
                    entry.IsManaged &&
                    string.Equals(entry.ManagedStoreId, game.StoreId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(manifestTitleId) &&
                    string.Equals(entry.ManagedTitleId, manifestTitleId, StringComparison.OrdinalIgnoreCase);

                var score = managedTitleMatch
                    ? 0
                    : launchOptionsMatch && titleMatches
                        ? 1
                        : launchOptionsMatch
                            ? 2
                            : titleMatches && startDirectoryMatch && (entry.AppId == expectedAppId || (manifestAppId != 0 && entry.AppId == manifestAppId))
                                ? 3
                                : int.MaxValue;

                return new { Entry = entry, Score = score };
            })
            .Where(candidate => candidate.Score < int.MaxValue)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.Index)
            .Select(candidate => candidate.Entry)
            .FirstOrDefault();

        return existingShortcut is not null;
    }

    private static bool TryResolveLinkedTitleId(
        StoreSyncConfiguration configuration,
        StoreGameEntry game,
        string effectiveTitle,
        ExistingShortcutEntry? existingShortcut,
        out string linkedTitleId)
    {
        linkedTitleId = string.Empty;

        if (existingShortcut?.IsManaged == true &&
            string.Equals(existingShortcut.ManagedStoreId, game.StoreId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(existingShortcut.ManagedTitleId) &&
            configuration.Manifest.TryGetValue(existingShortcut.ManagedTitleId, out var manifestFromShortcut) &&
            ManifestLikelyMatchesGame(manifestFromShortcut, game, effectiveTitle, existingShortcut))
        {
            linkedTitleId = manifestFromShortcut.TitleId;
            return true;
        }

        var manifestMatch = configuration.Manifest.Values
            .Where(entry => entry.ManagedShortcut &&
                            string.Equals(entry.StoreId, game.StoreId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new
            {
                Entry = entry,
                Score = GetManifestMatchScore(entry, game, effectiveTitle, existingShortcut),
            })
            .Where(candidate => candidate.Score < int.MaxValue)
            .OrderBy(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Entry.LastSeenAtUtc)
            .Select(candidate => candidate.Entry)
            .FirstOrDefault();

        if (manifestMatch is null)
        {
            return false;
        }

        linkedTitleId = manifestMatch.TitleId;
        return true;
    }

    private static bool ManifestLikelyMatchesGame(
        StoreSyncManifestEntry manifestEntry,
        StoreGameEntry game,
        string effectiveTitle,
        ExistingShortcutEntry? existingShortcut)
    {
        return GetManifestMatchScore(manifestEntry, game, effectiveTitle, existingShortcut) < int.MaxValue;
    }

    private static int GetManifestMatchScore(
        StoreSyncManifestEntry manifestEntry,
        StoreGameEntry game,
        string effectiveTitle,
        ExistingShortcutEntry? existingShortcut)
    {
        if (!string.Equals(manifestEntry.StoreId, game.StoreId, StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var normalizedExecutablePath = NormalizePath(game.ExecutablePath);
        var normalizedStartDirectory = NormalizePath(game.StartDirectory);
        var normalizedRawTitle = NormalizeKey(game.Title);
        var normalizedEffectiveTitle = NormalizeKey(effectiveTitle);
        var normalizedManifestTitle = NormalizeKey(manifestEntry.Title);
        var normalizedManifestEffectiveTitle = NormalizeKey(manifestEntry.EffectiveTitle);
        var manifestExecutablePath = NormalizePath(manifestEntry.ExecutablePath);
        var normalizedStoreItemId = NormalizeStoreItemId(game.StoreItemId);
        var normalizedManifestStoreItemId = NormalizeStoreItemId(manifestEntry.StoreItemId);
        var hasTitleMatch =
            string.Equals(normalizedManifestEffectiveTitle, normalizedEffectiveTitle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedManifestEffectiveTitle, normalizedRawTitle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedManifestTitle, normalizedEffectiveTitle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedManifestTitle, normalizedRawTitle, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(normalizedStoreItemId) &&
            string.Equals(normalizedManifestStoreItemId, normalizedStoreItemId, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // Every OmniLibrary shortcut shares the same ToolsForSteam executable.
        // Falling back to executable-path or title matching here can therefore link
        // hundreds of unrelated store products to one manifest and one Steam app id.
        // Store product identity is authoritative for OmniLibrary.
        if (string.Equals(game.StoreId, UnifySteamStoreId, StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        if (!string.IsNullOrWhiteSpace(normalizedExecutablePath) &&
            string.Equals(manifestExecutablePath, normalizedExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (existingShortcut is not null && manifestEntry.AppId != 0 && existingShortcut.AppId == manifestEntry.AppId && hasTitleMatch)
        {
            return 2;
        }

        if (hasTitleMatch &&
            string.Equals(existingShortcut?.ManagedStoreId, game.StoreId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingShortcut?.ManagedTitleId, manifestEntry.TitleId, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (hasTitleMatch)
        {
            return string.Equals(existingShortcut?.StartDirectory, normalizedStartDirectory, StringComparison.OrdinalIgnoreCase)
                ? 4
                : 5;
        }

        return int.MaxValue;
    }

    private static bool ShouldTreatShortcutAsManaged(
        StoreSyncManifestEntry? manifestEntry,
        ExistingShortcutEntry? existingShortcut)
    {
        if (existingShortcut is null)
        {
            return false;
        }

        if (manifestEntry is null)
        {
            return existingShortcut.IsManaged;
        }

        if (!IsManifestLifecycleManaged(manifestEntry))
        {
            return false;
        }

        var lifecycleManifestEntry = manifestEntry!;

        if (lifecycleManifestEntry.AppId != 0 && lifecycleManifestEntry.AppId == existingShortcut.AppId)
        {
            return true;
        }

        var manifestExecutablePath = NormalizePath(lifecycleManifestEntry.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(manifestExecutablePath) &&
            string.Equals(manifestExecutablePath, existingShortcut.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!existingShortcut.IsManaged)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(existingShortcut.ManagedTitleId) &&
            string.Equals(existingShortcut.ManagedTitleId, lifecycleManifestEntry.TitleId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(existingShortcut.ManagedStoreId) &&
            string.Equals(existingShortcut.ManagedStoreId, lifecycleManifestEntry.StoreId, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedManifestTitle = NormalizeKey(lifecycleManifestEntry.Title);
            var normalizedManifestEffectiveTitle = NormalizeKey(lifecycleManifestEntry.EffectiveTitle);
            var normalizedShortcutTitle = NormalizeKey(existingShortcut.AppName);
            return string.Equals(normalizedShortcutTitle, normalizedManifestTitle, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedShortcutTitle, normalizedManifestEffectiveTitle, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void MigrateConfigurationTitleState(
        StoreSyncConfiguration configuration,
        string sourceTitleId,
        string targetTitleId)
    {
        if (string.IsNullOrWhiteSpace(sourceTitleId) ||
            string.IsNullOrWhiteSpace(targetTitleId) ||
            string.Equals(sourceTitleId, targetTitleId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (configuration.TitleOverrides.TryGetValue(sourceTitleId, out var sourceOverride) &&
            sourceOverride is not null &&
            !configuration.TitleOverrides.ContainsKey(targetTitleId))
        {
            configuration.TitleOverrides[targetTitleId] = sourceOverride;
        }

        configuration.TitleOverrides.Remove(sourceTitleId);

        if (configuration.ArtworkMatchCache.TryGetValue(sourceTitleId, out var sourceArtworkCache) &&
            sourceArtworkCache is not null &&
            !configuration.ArtworkMatchCache.ContainsKey(targetTitleId))
        {
            configuration.ArtworkMatchCache[targetTitleId] = sourceArtworkCache;
        }

        configuration.ArtworkMatchCache.Remove(sourceTitleId);

        if (configuration.Manifest.TryGetValue(sourceTitleId, out var sourceManifest) &&
            sourceManifest is not null)
        {
            if (!configuration.Manifest.ContainsKey(targetTitleId))
            {
                configuration.Manifest[targetTitleId] = sourceManifest;
            }

            configuration.Manifest.Remove(sourceTitleId);
        }
    }

    private static void ResetResolvedPlaceholderArtworkState(
        StoreSyncConfiguration configuration,
        string titleId,
        StoreSyncManifestEntry? manifestEntry,
        string effectiveArtworkTitle,
        ref StoreSyncArtworkCacheEntry? artworkCache)
    {
        if (manifestEntry is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(manifestEntry.ArtworkTitle) ||
            string.Equals(manifestEntry.ArtworkTitle, effectiveArtworkTitle, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsWindowsResourcePlaceholder(manifestEntry.ArtworkTitle) &&
            !IsWindowsResourcePlaceholder(manifestEntry.Title) &&
            !IsWindowsResourcePlaceholder(manifestEntry.EffectiveTitle))
        {
            return;
        }

        configuration.ArtworkMatchCache.Remove(titleId);
        artworkCache = null;
        manifestEntry.SteamGridDbGameId = null;
        manifestEntry.ArtworkLocked = false;
    }

    private static void AddArtworkTarget(
        IDictionary<uint, StoreSyncArtworkTarget> targets,
        StoreSyncAnalysisItem item,
        StoreSyncArtworkCacheEntry? artworkCache = null,
        uint? appIdOverride = null)
    {
        artworkCache ??= item.ArtworkCache;
        var appId = appIdOverride ?? item.TargetAppId;
        if (appId == 0)
        {
            return;
        }

        targets[appId] = new StoreSyncArtworkTarget(
            item.TitleId,
            item.EffectiveArtworkTitle,
            appId,
            new[] { item.Game.Title, item.EffectiveTitle, item.Game.ExecutablePath, item.Game.StartDirectory },
            artworkCache?.GameId,
            artworkCache?.MatchName ?? string.Empty,
            item.Game.StoreId);
    }

    private static string ResolveShortcutExecutablePath(StoreGameEntry game)
    {
        return string.Equals(game.StoreId, "xbox-game-pass", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "ToolsForSteam.exe")
            : game.ExecutablePath;
    }

    private static string ResolveShortcutStartDirectory(StoreGameEntry game)
    {
        return game.StartDirectory;
    }

    private static string ResolveShortcutLaunchOptions(StoreGameEntry game)
    {
        return string.Equals(game.StoreId, "xbox-game-pass", StringComparison.OrdinalIgnoreCase)
            ? XboxStoreLaunchHost.BuildLaunchArguments(game.ExecutablePath, game.StartDirectory)
            : game.LaunchOptions ?? string.Empty;
    }

    private static ManagedShortcutEntry CreateShortcutEntry(
        StoreSyncAnalysisItem item,
        uint? appIdOverride = null,
        Dictionary<string, object?>? seedEntry = null)
    {
        var appId = appIdOverride ?? item.TargetAppId;
        var entry = seedEntry is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : CloneShortcutEntry(seedEntry);

        entry["appid"] = unchecked((int)appId);
        entry["appname"] = item.EffectiveTitle;
        entry["Exe"] = QuotePath(ResolveShortcutExecutablePath(item.Game));
        entry["StartDir"] = QuotePath(ResolveShortcutStartDirectory(item.Game));
        entry["icon"] = item.Game.ExecutablePath;
        entry["ShortcutPath"] = ManagedShortcutMarker;
        entry["LaunchOptions"] = ResolveShortcutLaunchOptions(item.Game);
        entry["IsHidden"] = 0;
        entry["AllowDesktopConfig"] = 1;
        entry["AllowOverlay"] = 1;
        entry["OpenVR"] = 0;
        entry["Devkit"] = 0;
        entry["DevkitGameID"] = string.Empty;
        entry["DevkitOverrideAppID"] = 0;
        entry["LastPlayTime"] = entry.TryGetValue("LastPlayTime", out var lastPlayTimeValue) ? lastPlayTimeValue ?? 0 : 0;
        entry["FlatpakAppID"] = string.Empty;
        entry["tags"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = "Tools for Steam",
            ["1"] = "Store Sync",
            ["2"] = item.Game.StoreId,
            ["3"] = item.TitleId,
        };

        return new ManagedShortcutEntry(item.Game, appId, entry);
    }

    private static string ReadShortcutString(Dictionary<string, object?> entry, string key)
    {
        return entry.TryGetValue(key, out var value)
            ? Convert.ToString(value) ?? string.Empty
            : string.Empty;
    }

    private static string ReadShortcutTagString(Dictionary<string, object?> entry, string tagKey)
    {
        if (!entry.TryGetValue("tags", out var tagsValue) ||
            tagsValue is not Dictionary<string, object?> tags ||
            !tags.TryGetValue(tagKey, out var tagValue))
        {
            return string.Empty;
        }

        return Convert.ToString(tagValue)?.Trim() ?? string.Empty;
    }

    private static bool TryReadShortcutAppId(Dictionary<string, object?> entry, out uint appId)
    {
        appId = 0;
        if (!entry.TryGetValue("appid", out var value) || value is null)
        {
            return false;
        }

        try
        {
            appId = value switch
            {
                int intValue => unchecked((uint)intValue),
                long longValue => unchecked((uint)longValue),
                uint uintValue => uintValue,
                ulong ulongValue => unchecked((uint)ulongValue),
                short shortValue => unchecked((uint)shortValue),
                byte byteValue => byteValue,
                _ => unchecked((uint)Convert.ToInt64(value)),
            };
            return appId != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string UnquotePath(string value)
    {
        return value.Trim().Trim('"');
    }

    private static string QuotePath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : $"\"{path}\"";
    }

    private static string BuildSyncMessage(
        int createdCount,
        int refreshedCount,
        int adoptedCount,
        int skippedCount,
        int excludedCount,
        int cleanedUpCount,
        StoreSyncArtworkSummary? artworkSummary,
        bool artworkEnabled,
        bool syncedWhileSteamWasRunning,
        bool usedLiveShortcutSync)
    {
        if (createdCount == 0 &&
            refreshedCount == 0 &&
            adoptedCount == 0 &&
            skippedCount == 0 &&
            cleanedUpCount == 0)
        {
            return excludedCount > 0
                ? $"No Steam shortcuts changed. {excludedCount} title{(excludedCount == 1 ? string.Empty : "s")} are currently excluded."
                : "No launchable third-party titles were found during this sync.";
        }

        var parts = new List<string>();
        if (createdCount > 0)
        {
            parts.Add($"created {createdCount} new shortcut{(createdCount == 1 ? string.Empty : "s")}");
        }

        if (refreshedCount > 0)
        {
            parts.Add($"refreshed {refreshedCount} managed shortcut{(refreshedCount == 1 ? string.Empty : "s")}");
        }

        if (adoptedCount > 0)
        {
            parts.Add($"adopted {adoptedCount} existing Steam shortcut{(adoptedCount == 1 ? string.Empty : "s")}");
        }

        if (cleanedUpCount > 0)
        {
            parts.Add($"cleaned up {cleanedUpCount} stale shortcut{(cleanedUpCount == 1 ? string.Empty : "s")}");
        }

        if (skippedCount > 0)
        {
            parts.Add($"skipped {skippedCount} existing title{(skippedCount == 1 ? string.Empty : "s")}");
        }

        if (excludedCount > 0)
        {
            parts.Add($"left {excludedCount} excluded title{(excludedCount == 1 ? string.Empty : "s")} untouched");
        }

        var message = $"Store Sync {JoinHumanReadable(parts)}.";
        if (artworkEnabled && artworkSummary is not null && artworkSummary.UpdatedTitleCount > 0)
        {
            message += $" Artwork was updated for {artworkSummary.UpdatedTitleCount} title{(artworkSummary.UpdatedTitleCount == 1 ? string.Empty : "s")}.";
        }

        if (usedLiveShortcutSync)
        {
            message += " Changes were applied live in the running Steam client.";
        }
        else if (syncedWhileSteamWasRunning)
        {
            message += " Steam may show the changes after the library refreshes or the next restart.";
        }

        return message;
    }

    private static string JoinHumanReadable(IReadOnlyList<string> parts)
    {
        return parts.Count switch
        {
            0 => "made no changes",
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))}, and {parts[^1]}",
        };
    }

    private static IEnumerable<Process> GetSteamProcesses()
    {
        return Process.GetProcessesByName("steam")
            .Concat(Process.GetProcessesByName("steamwebhelper"));
    }

    private static bool IsSteamRunning()
    {
        return GetSteamProcesses().Any(process =>
        {
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        });
    }

    private static string BuildGridDirectory(SteamProfileInfo profile)
    {
        var shortcutsDirectory = Path.GetDirectoryName(profile.ShortcutsPath)
            ?? throw new InvalidOperationException("The Steam shortcuts folder could not be resolved.");

        return Path.Combine(shortcutsDirectory, "grid");
    }

    private void PersistArtworkPreviewMatch(string titleId, StoreSyncArtworkPreview preview)
    {
        lock (_gate)
        {
            _settingsStore.Update(configuration =>
            {
                configuration.ArtworkMatchCache[titleId] = new StoreSyncArtworkCacheEntry
                {
                    GameId = preview.GameId,
                    MatchName = preview.MatchName,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };

                if (configuration.Manifest.TryGetValue(titleId, out var manifestEntry) && manifestEntry is not null)
                {
                    manifestEntry.SteamGridDbGameId = preview.GameId;
                }
            });
        }
    }

    private static bool TryBuildLocalArtworkPreview(
        SteamProfileInfo profile,
        StoreSyncAnalysisItem item,
        out StoreSyncArtworkPreviewState? preview)
    {
        preview = null;

        try
        {
            var gridDirectory = BuildGridDirectory(profile);
            if (!Directory.Exists(gridDirectory))
            {
                return false;
            }

            var gridId = SteamShortcutIds.BuildGridId(item.TargetAppId);
            var localPreviewPath = FindFirstExistingArtworkPath(gridDirectory, gridId);
            if (string.IsNullOrWhiteSpace(localPreviewPath))
            {
                return false;
            }

            var mimeType = ResolveArtworkMimeType(localPreviewPath);
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                return false;
            }

            var bytes = File.ReadAllBytes(localPreviewPath);
            preview = new StoreSyncArtworkPreviewState(
                item.TitleId,
                Available: true,
                UsesCurrentArtwork: true,
                ImageDataUri: $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}",
                SourceLabel: "Current Steam artwork",
                Message: "This preview shows the artwork already stored in Steam for this shortcut.");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindFirstExistingArtworkPath(string gridDirectory, string gridId)
    {
        foreach (var stem in new[] { gridId, $"{gridId}p", $"{gridId}_hero" })
        {
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            {
                var path = Path.Combine(gridDirectory, stem + extension);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string? ResolveArtworkMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null,
        };
    }

    private static string? GetVdfField(string body, string fieldName)
    {
        var match = Regex.Match(body, $"\"{Regex.Escape(fieldName)}\"\\s+\"(?<value>.*?)\"");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static long ParseLong(string? value)
    {
        return long.TryParse(value, out var parsedValue) ? parsedValue : 0;
    }

    private sealed record StoreDefinition(
        string Id,
        string Title,
        string Description,
        bool SupportsCustomPath = false,
        bool SupportsAdditionalPaths = false);

    private sealed record StoreScanResult(
        bool IsReady,
        bool CanCleanupMissingTitles,
        string StatusText,
        string DetailText,
        IReadOnlyList<string> AvailableRoots,
        IReadOnlyList<string> MissingRoots,
        IReadOnlyList<StoreGameEntry> Games);

    private sealed record ExtraPathScanResult(
        IReadOnlyList<string> AvailableRoots,
        IReadOnlyList<string> MissingRoots,
        IReadOnlyList<StoreGameEntry> Games);

    internal sealed record StoreSyncWatchTarget(
        string DirectoryPath,
        string Filter,
        bool IncludeSubdirectories);

    private sealed record StoreSnapshot(
        StoreDefinition Definition,
        StoreSyncStoreConfiguration Configuration,
        StoreScanResult Scan);

    private sealed record StoreGameEntry(
        string StoreId,
        string StoreItemId,
        string Title,
        string ExecutablePath,
        string StartDirectory,
        string LaunchOptions);

    private sealed record EpicManifestEntry(
        string? AppName,
        string? MainGameAppName,
        string? CatalogNamespace,
        string? CatalogItemId,
        string? InstallLocation,
        string? DisplayName,
        string? VaultTitleText,
        string? MandatoryAppFolderName,
        string? LaunchExecutable,
        string? LaunchCommand);

    private sealed record EaInstallerMetadata(
        string? Title,
        IReadOnlyList<string> ContentIds);

    private sealed record EaInstallReference(
        string ReferenceId,
        string Title,
        string InstallPath);

    private readonly record struct ExecutableCandidate(string Path, int Score);

    private enum StoreSyncActionKind
    {
        Create,
        RefreshManaged,
        AdoptExisting,
        SkipExisting,
        Excluded,
    }

    private sealed record ExistingShortcutEntry(
        int Index,
        uint AppId,
        string AppName,
        string ExecutablePath,
        string StartDirectory,
        string LaunchOptions,
        bool IsManaged,
        string ManagedStoreId,
        string ManagedTitleId,
        Dictionary<string, object?> Entry);

    private sealed record ManagedShortcutEntry(
        StoreGameEntry Game,
        uint AppId,
        Dictionary<string, object?> Entry);

    private sealed record StoreSyncAnalysisItem(
        string TitleId,
        string LinkedTitleId,
        StoreDefinition Definition,
        StoreGameEntry Game,
        StoreSyncTitleOverride Override,
        string EffectiveTitle,
        string EffectiveArtworkTitle,
        uint TargetAppId,
        StoreSyncActionKind ActionKind,
        string SyncDetail,
        string ArtworkState,
        ExistingShortcutEntry? ExistingShortcut,
        StoreSyncManifestEntry? ManifestEntry,
        StoreSyncArtworkCacheEntry? ArtworkCache,
        IReadOnlyList<string> DebugLines);

    private sealed record StoreSyncCleanupCandidate(
        string TitleId,
        string Title,
        string StoreTitle,
        ExistingShortcutEntry ExistingShortcut,
        StoreSyncManifestEntry? ManifestEntry,
        IReadOnlyList<string> DebugLines);

    private sealed record StoreSyncCleanupPlan(
        IReadOnlyList<StoreSyncCleanupCandidate> Candidates,
        int DeferredCount);

    private sealed record StoreSyncAnalysis(
        IReadOnlyList<StoreSyncAnalysisItem> Items,
        IReadOnlyList<StoreSyncCleanupCandidate> CleanupCandidates,
        int DeferredCleanupCount,
        StoreSyncPreviewState Preview);

    private sealed record OmniLibrarySyncDelta(
        string StoreId,
        HashSet<string> UpsertGameIds,
        HashSet<string> RemovedGameIds);

    private readonly record struct AppliedSyncSignatureState(
        string Signature,
        DateTimeOffset AppliedAtUtc);

    private readonly record struct ScheduledAutomationWriteState(
        string NormalizedPath,
        DateTimeOffset IgnoreUntilUtc);

    private readonly record struct OwnershipRepairResult(
        bool Completed,
        string Message);

    private sealed record LiveShortcutSyncCreateOperation(
        string TitleId,
        uint TargetAppId,
        string Name,
        string ExecutablePath,
        string StartDirectory,
        string LaunchOptions,
        string IconPath);

    private sealed record LiveShortcutSyncUpdateOperation(
        string TitleId,
        uint AppId,
        uint TargetAppId,
        bool ForceCreate,
        string Name,
        string ExecutablePath,
        string StartDirectory,
        string LaunchOptions,
        string IconPath);

    private sealed record LiveShortcutSyncRemoveOperation(
        string TitleId,
        uint AppId);

    private sealed record LiveShortcutSyncPlan(
        string ProfileAccountId,
        bool DownloadArtwork,
        IReadOnlyList<LiveShortcutSyncCreateOperation> CreateOperations,
        IReadOnlyList<LiveShortcutSyncUpdateOperation> UpdateOperations,
        IReadOnlyList<LiveShortcutSyncRemoveOperation> RemoveOperations)
    {
        public bool IsEmpty =>
            CreateOperations.Count == 0 &&
            UpdateOperations.Count == 0 &&
            RemoveOperations.Count == 0;
    }

    private sealed record LiveShortcutSyncResponse(
        bool Available,
        bool Success,
        int AttemptedCount,
        Dictionary<string, uint>? AppIdsByTitleId,
        List<string> Errors);

    private sealed record LiveShortcutSyncResult(
        bool Applied,
        Dictionary<string, uint> AppIdsByTitleId);

    // ── Store Collections ─────────────────────────────────────────────────────

    private sealed record StoreCollectionEntry(
        string StoreId,
        string DisplayName,
        IReadOnlyList<uint> AppIds);

    private sealed record StoreCollectionSyncPlan(
        IReadOnlyList<StoreCollectionEntry> StoreCollections);

    private sealed record StoreCollectionSyncResponse(
        bool Available,
        bool Success,
        List<string> Errors);

    /// <summary>
    /// Groups all active (non-excluded) synced items by store and asks Steam via CDP
    /// to create or update one collection per store.  Silently no-ops when CDP is unavailable.
    /// </summary>
    private async Task TrySyncCollectionsLiveAsync(
        StoreSyncAnalysis analysis,
        IReadOnlyDictionary<string, uint> liveShortcutAppIds,
        CancellationToken cancellationToken)
    {
        // Build one entry per store that has at least one active game. OmniLibrary
        // shortcuts belong to their source collection only, which becomes the native
        // Xbox tab without adding a second generic collection.
        var byStore = new Dictionary<string, (string DisplayName, List<uint> AppIds)>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in analysis.Items)
        {
            if (item.ActionKind == StoreSyncActionKind.Excluded)
            {
                continue;
            }

            var appId = ResolveLiveShortcutAppId(item, liveShortcutAppIds);
            if (appId == 0)
            {
                continue;
            }

            foreach (var collectionStore in ResolveCollectionStoresForItem(item))
            {
                if (string.IsNullOrWhiteSpace(collectionStore.StoreId))
                {
                    continue;
                }

                // Xbox is represented by OmniLibrary's transient native tab filter.
                // Never create a persistent Steam collection for it, including when
                // normal Store Sync discovers locally installed Xbox titles.
                if (string.Equals(
                        collectionStore.StoreId,
                        "xbox-game-pass",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!byStore.TryGetValue(collectionStore.StoreId, out var entry))
                {
                    entry = (collectionStore.DisplayName, []);
                    byStore[collectionStore.StoreId] = entry;
                }

                if (!entry.AppIds.Contains(appId))
                {
                    entry.AppIds.Add(appId);
                }
            }
        }

        if (byStore.Count == 0)
        {
            return;
        }

        var plan = new StoreCollectionSyncPlan(
            byStore
                .Select(kv => new StoreCollectionEntry(kv.Key, kv.Value.DisplayName, kv.Value.AppIds))
                .ToList());

        try
        {
            var target = await _steamDevToolsClient.GetSharedJsContextTargetAsync(cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return;
            }

            var evaluation = await _steamDevToolsClient.EvaluateAsync(
                target.WebSocketDebuggerUrl,
                BuildCollectionSyncExpression(plan),
                cancellationToken).ConfigureAwait(false);

            if (!evaluation.Success)
            {
                _journal.Append("debug", "collections", "Store collection sync via CDP was not successful.", evaluation.ErrorMessage ?? string.Empty);
                return;
            }

            StoreCollectionSyncResponse? response = null;
            try
            {
                response = evaluation.Value switch
                {
                    JsonElement element => element.Deserialize<StoreCollectionSyncResponse>(
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    null => null,
                    _ => JsonSerializer.Deserialize<StoreCollectionSyncResponse>(
                        JsonSerializer.Serialize(evaluation.Value),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                };
            }
            catch (JsonException ex)
            {
                _journal.Append("debug", "collections", "Store collection sync returned an unreadable response.", ex.Message);
                return;
            }

            if (response is not { Available: true, Success: true })
            {
                var details = response is null
                    ? "Steam returned no collection-sync result."
                    : string.Join(" | ", response.Errors);
                _journal.Append("debug", "collections", "Store collection sync via CDP was not applied.", details);
                return;
            }

            _journal.Append("debug", "collections", $"Store collection sync via CDP completed for {byStore.Count} store(s).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _journal.Append("debug", "collections", "Store collection sync via CDP threw an exception.", ex.Message);
        }
    }

    private static IEnumerable<StoreCollectionEntry> ResolveCollectionStoresForItem(StoreSyncAnalysisItem item)
    {
        var storeId = item.Game.StoreId;
        if (string.IsNullOrWhiteSpace(storeId))
        {
            yield break;
        }

        if (string.Equals(storeId, UnifySteamStoreId, StringComparison.OrdinalIgnoreCase) &&
            TryResolveUnifySteamSourceStoreId(item.Game.StoreItemId, out var sourceStoreId))
        {
            yield return new StoreCollectionEntry(sourceStoreId, ResolveStoreTitle(sourceStoreId), []);
            yield break;
        }

        yield return new StoreCollectionEntry(storeId, ResolveStoreTitle(storeId), []);
    }

    private static bool TryResolveUnifySteamSourceStoreId(string storeItemId, out string sourceStoreId)
    {
        sourceStoreId = string.Empty;
        if (string.IsNullOrWhiteSpace(storeItemId))
        {
            return false;
        }

        var separatorIndex = storeItemId.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var candidate = storeItemId[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.Equals(candidate, UnifySteamStoreId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sourceStoreId = candidate;
        return true;
    }

    private static string BuildCollectionSyncExpression(StoreCollectionSyncPlan plan)
    {
        var planJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return $$"""
(async () => {
  try {
    const plan = {{planJson}};
    const errors = [];
    const collectionStore = window.collectionStore;
    const collectionMap = collectionStore?.m_mapCollectionsFromStorage;

    // Current Steam builds no longer expose SteamClient.Collections in the
    // SharedJSContext. Use the live collection store that backs the Library.
    // Probe its backing map directly because the public MobX computed getters
    // can throw while Steam's collection storage is still initializing.
    if (collectionStore && collectionMap && typeof collectionMap.values === "function") {
      for (const entry of plan.storeCollections ?? []) {
        try {
          const displayName = String(entry.displayName ?? "").trim();
          const appIds = Array.from(new Set(
            (entry.appIds ?? [])
              .map(Number)
              .filter(appId => Number.isFinite(appId) && appId > 0)));
          if (!displayName || appIds.length === 0) {
            continue;
          }

          const existing = Array.from(collectionMap.values()).find(collection =>
            String(collection?.displayName ?? collection?.m_strName ?? collection?.name ?? "")
              .trim()
              .toLowerCase() === displayName.toLowerCase());

          if (existing) {
            collectionStore.AddOrRemoveApp(appIds, true, existing.id);
            await collectionStore.SaveCollection(existing);
          } else {
            // NewUnsavedCollection only reads the appid field before resolving
            // the real app overviews, so lightweight objects are sufficient.
            const created = collectionStore.NewUnsavedCollection(
              displayName,
              undefined,
              appIds.map(appid => ({ appid })));
            await collectionStore.SaveCollection(created);
          }
        } catch (err) {
          errors.push(`${entry.displayName}: ${err instanceof Error ? err.message : String(err)}`);
        }
      }

      return { available: true, success: errors.length === 0, errors };
    }

    const col = window.SteamClient?.Collections;
    if (!col) {
      return {
        available: false,
        success: false,
        errors: ["Neither collectionStore nor SteamClient.Collections is available in SharedJSContext."]
      };
    }

    // Fetch existing user collections.
    let existing = [];
    try {
      const raw = await (col.GetUserCollectionList?.() ?? Promise.resolve([]));
      existing = Array.isArray(raw) ? raw : [];
    } catch {
      existing = [];
    }

    for (const entry of plan.storeCollections ?? []) {
      try {
        // Match by display name (case-insensitive).
        const found = existing.find(c =>
          (c.displayName ?? c.name ?? "").toLowerCase() === entry.displayName.toLowerCase());

        if (!found) {
          // Create a new collection and populate it.
          const newCol = await (col.CreateCollection?.(entry.displayName) ?? Promise.resolve(null));
          const newId = newCol?.id ?? newCol;
          if (newId && entry.appIds.length > 0) {
            await col.AddToCollection?.(newId, entry.appIds.map(Number));
          }
        } else {
          // Add any games that aren't already in the collection (additive — never removes).
          const currentApps = new Set(
            (found.apps ?? found.added ?? []).map(Number));
          const toAdd = entry.appIds.map(Number).filter(id => !currentApps.has(id));
          if (toAdd.length > 0) {
            await col.AddToCollection?.(found.id, toAdd);
          }
        }
      } catch (err) {
        errors.push(`${entry.displayName}: ${err instanceof Error ? err.message : String(err)}`);
      }
    }

    return { available: true, success: errors.length === 0, errors };
  } catch (err) {
    return {
      available: false,
      success: false,
      errors: [err instanceof Error ? err.message : String(err)]
    };
  }
})()
""";
    }
}

internal sealed record OmniLibraryCatalogDelta(
    IReadOnlyList<string> AddedGameIds,
    IReadOnlyList<string> RemovedGameIds);
