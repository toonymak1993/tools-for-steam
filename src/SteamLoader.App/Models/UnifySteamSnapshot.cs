namespace SteamLoader.App.Models;

public sealed record UnifySteamSnapshot(
    string StatusText,
    string DetailText,
    DateTimeOffset? LastRefreshedAtUtc,
    IReadOnlyList<UnifySteamStoreState> Stores)
{
    public OmniLibraryGameDataState GameData { get; init; } =
        OmniLibraryGameDataState.Disabled;
}

public sealed record OmniLibraryGameDataState(
    bool Enabled,
    int EnabledProviderCount,
    int ConfiguredProviderCount,
    IReadOnlyList<OmniLibraryGameDataProviderState> Providers)
{
    public static OmniLibraryGameDataState Disabled { get; } =
        new(false, 0, 0, []);
}

public sealed record OmniLibraryGameDataProviderState(
    string Id,
    string Title,
    string Description,
    bool RuntimeAvailable,
    bool Enabled,
    bool Configured,
    string Status,
    string Detail,
    string ConnectionStatus,
    string ConnectionDetail,
    DateTimeOffset? ConnectionCheckedAtUtc,
    string SetupKind,
    string CredentialPreview,
    string AccountName,
    string AccountId,
    string Region,
    string Locale,
    string DataPath,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> StoreIds);

public sealed record UnifySteamStateSnapshot(
    long Revision,
    DateTimeOffset GeneratedAtUtc,
    UnifySteamSnapshot UnifySteam);

public sealed record UnifySteamLibrarySummarySnapshot(
    long Revision,
    bool PluginEnabled,
    IReadOnlyList<UnifySteamLibraryStoreSummary> Stores);

public sealed record UnifySteamLibraryStoreSummary(
    string Id,
    bool Enabled,
    bool ReadyForLibraryTab,
    bool XboxCloudGamingEnabled,
    int AvailableCount,
    IReadOnlyList<uint> AppIds,
    IReadOnlyList<uint> InstalledAppIds,
    IReadOnlyList<uint> CloudAppIds)
{
    public string Title { get; init; } = string.Empty;

    public bool SupportsUninstall { get; init; }

    public IReadOnlyList<UnifySteamLibraryTabSummary> LibraryTabs { get; init; } = [];

    public IReadOnlyList<uint> ActiveDownloadAppIds { get; init; } = [];

    public IReadOnlyList<uint> RepairableAppIds { get; init; } = [];

    public IReadOnlyList<OmniLibraryRomSystemState> RomSystems { get; init; } = [];
}

public sealed record UnifySteamLibraryTabSummary(
    string Id,
    string Title,
    string Filter,
    bool RequiresCloudSource);

public sealed record UnifySteamGameDetailSnapshot(
    long Revision,
    string StoreId,
    UnifySteamGameState? Game);

public sealed record OmniLibraryGameArtworkRepairResult(
    uint SteamAppId,
    string StoreId,
    string GameId,
    string Title,
    bool Queued,
    IReadOnlyList<string> MissingArtworkSlots,
    string Message);

public sealed record OmniLibraryDownloadCenterSnapshot(
    long Revision,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<OmniLibraryDownloadCenterEntry> Entries);

public sealed record OmniLibraryDownloadCenterEntry(
    string StoreId,
    string StoreTitle,
    string GameId,
    string GameTitle,
    uint SteamAppId,
    bool Installed,
    string Status,
    int ProgressPercent,
    string DetailText,
    DateTimeOffset UpdatedAtUtc,
    long DownloadedBytes,
    long TotalBytes,
    double DownloadBytesPerSecond,
    double DecompressedBytesPerSecond,
    double DiskWriteBytesPerSecond,
    double DiskReadBytesPerSecond,
    int Attempt,
    string TransferOwner,
    bool ManagedByToolsForSteam,
    bool CanPause,
    bool CanResume,
    bool CanCancel,
    bool CanStopTracking,
    bool CanDismiss,
    bool CanPlay,
    bool CanManageExternally,
    bool CanRetryUninstall,
    bool IsStalled,
    bool CatalogEntryAvailable);

public sealed record OmniLibraryStoreCheckResult(
    string StoreId,
    bool Checked,
    bool Succeeded,
    bool CatalogChanged,
    bool StateChanged,
    string Detail);

public sealed record UnifySteamStoreState(
    string Id,
    string Title,
    bool Enabled,
    bool ToolDetected,
    bool AuthConfigured,
    bool AuthReady,
    bool CanRefresh,
    bool ReadyForLibraryTab,
    bool SteamRestartRequired,
    string PreparationStatus,
    string PreparationDetail,
    int PreparationCompletedCount,
    int PreparationTotalCount,
    bool SupportsManualCodeAuth,
    bool XboxPcGamePassEnabled,
    bool XboxCloudGamingEnabled,
    string StatusText,
    string DetailText,
    string AccountName,
    string InstallPath,
    DateTimeOffset? RefreshedAtUtc,
    int InstalledCount,
    int AvailableCount,
    IReadOnlyList<UnifySteamGameState> Games)
{
    public OmniLibraryLifecycleSnapshot Lifecycle { get; init; } =
        OmniLibraryLifecycleSnapshot.Disabled;

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public IReadOnlyList<UnifySteamLibraryTabSummary> LibraryTabs { get; init; } = [];

    public int DownloadWorkers { get; init; } = 16;

    public int DownloadTimeoutSeconds { get; init; } = 60;

    public bool GogDlcEnabled { get; init; } = true;

    public bool GogGalaxyLaunchEnabled { get; init; }

    public bool AchievementsEnabled { get; init; } = true;

    public bool AchievementProviderConfigured { get; init; }

    public string AchievementProviderName { get; init; } = string.Empty;

    public string AchievementProviderDetail { get; init; } = string.Empty;

    public string AchievementCredentialPreview { get; init; } = string.Empty;

    public IReadOnlyList<OmniLibraryRomSystemState> RomSystems { get; init; } = [];

    public string ToolPath { get; init; } = string.Empty;
}

public sealed record OmniLibraryRomSystemState(
    string Id,
    string Title,
    string EmulatorTitle,
    int GameCount,
    string IconId,
    IReadOnlyList<uint> AppIds)
{
    public string EmulatorPath { get; init; } = string.Empty;

    public string ExecutableName { get; init; } = string.Empty;

    public string FolderPath { get; init; } = string.Empty;

    public bool EmulatorDetected { get; init; }

    public bool Fullscreen { get; init; } = true;
}

public sealed record OmniLibraryLifecycleSnapshot(
    string Authentication,
    string Catalog,
    string Shortcuts,
    string Artwork,
    string FailureScope,
    string FailureDetail)
{
    public static OmniLibraryLifecycleSnapshot Disabled { get; } =
        new("disabled", "idle", "idle", "idle", string.Empty, string.Empty);
}

public sealed record UnifySteamGameState(
    string Id,
    string Title,
    bool Installed,
    bool CloudPlayable,
    bool SyncedToSteam,
    string StatusText,
    string DetailText,
    string ImageUrl,
    string InstallPath,
    string ExecutablePath,
    string Version,
    uint SteamAppId,
    UnifySteamDownloadState Download)
{
    public string DeliveryProvider { get; init; } = string.Empty;

    public string ProviderDisplayName { get; init; } = string.Empty;

    public bool RequiresAccountLink { get; init; }

    public bool RequiresExternalLauncher { get; init; }

    public bool CanInstallDirectly { get; init; } = true;

    public string ExternalAction { get; init; } = string.Empty;

    public bool SupportsCloudSaves { get; init; }

    public bool IsPreloaded { get; init; }

    public bool UpdateAvailable { get; init; }

    public string StoreTitleId { get; init; } = string.Empty;

    public string StoreNamespace { get; init; } = string.Empty;

    public string PlatformId { get; init; } = string.Empty;

    public string PlatformTitle { get; init; } = string.Empty;

    public string RomPath { get; init; } = string.Empty;
}

public sealed record UnifySteamDownloadState(
    string Status,
    int ProgressPercent,
    string DetailText,
    long DownloadedBytes,
    long TotalBytes,
    double DownloadBytesPerSecond,
    double DecompressedBytesPerSecond,
    double DiskWriteBytesPerSecond,
    double DiskReadBytesPerSecond,
    int Attempt);
