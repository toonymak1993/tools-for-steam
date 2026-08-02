using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed class UnifySteamService
{
    private const string GogClientId = "46899977096215655";
    private const string GogClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    private const string GogRedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    // Same public client legendary uses for its own webview-based login.
    private const string EpicClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string EpicClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string EpicRedirectPrefix = "https://www.epicgames.com/id/api/redirect";
    private const string XboxPcGamePassCatalogId = "fdd9e2a7-0fee-49f6-ad69-4354098401ff";
    private const string XboxCloudGamingCatalogId = "af206485-e87d-4624-9007-cb7f6d0cc42e";
    private const string XboxCloudCatalogMarker = "__Cloud:XGPUWEB";
    private const string XboxCatalogShapeVersion =
        "xbox-catalog-v3-console-cloud-title-id";
    private const string GogCatalogShapeVersion = "gog-catalog-v1";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly ConcurrentDictionary<string, string> SteamGridDbPortraitCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object XboxInstalledCacheGate = new();
    private static readonly Dictionary<string, XboxInstalledCacheState> XboxInstalledCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (int MissingCount, DateTimeOffset FirstMissingAtUtc)>
        XboxPendingUninstallMisses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan XboxInstalledCacheLifetime = TimeSpan.FromSeconds(30);
    private static DateTimeOffset XboxLastCompletedDownloadObservedUtc = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<OmniLibraryStoreDescriptor> Definitions =>
        OmniLibraryStoreRegistry.All;

    private readonly StoreSyncJournal _journal;

    public UnifySteamService(StoreSyncJournal journal)
    {
        _journal = journal;
    }

    public UnifySteamSnapshot BuildSnapshot(
        StoreSyncConfiguration configuration,
        IReadOnlyList<StoreSyncStoreState> storeSyncStores)
    {
        var detectedTitles = storeSyncStores
            .SelectMany(store => store.DetectedTitles)
            .ToArray();
        var downloadStatuses = UnifySteamDownloadStatusStore.GetAll();
        Dictionary<string, UnifySteamGameCacheEntry>? installedXboxGames = null;
        var xboxInstallPath =
            configuration.UnifySteam.Stores.TryGetValue("xbox-game-pass", out var xboxStore)
                ? xboxStore?.InstallPath
                : null;
        if (XboxInstallEventTracker.ReconcileStatusStore(
                downloadStatuses,
                productId =>
                {
                    installedXboxGames ??= LoadXboxInstalledGames(
                        xboxInstallPath,
                        forceRefresh: true);
                    var catalogGame = xboxStore?.Cache?.Games?.FirstOrDefault(game =>
                        game is not null &&
                        string.Equals(
                            game.Id,
                            productId,
                            StringComparison.OrdinalIgnoreCase)) ??
                        new UnifySteamGameCacheEntry
                        {
                            Id = productId,
                            Title = productId,
                        };
                    return TryResolveXboxInstalledGame(
                        catalogGame,
                        installedXboxGames,
                        out _);
                }))
        {
            downloadStatuses = UnifySteamDownloadStatusStore.GetAll();
        }

        var stores = Definitions
            .Select(definition => BuildStoreState(
                definition,
                configuration,
                detectedTitles,
                downloadStatuses))
            .ToArray();
        var enabledStores = stores
            .Where(store => store.Enabled)
            .ToArray();

        var lastRefreshedAtUtc = enabledStores
            .Where(store => store.RefreshedAtUtc.HasValue)
            .Select(store => store.RefreshedAtUtc)
            .Max();

        var readyCount = enabledStores.Count(store => store.AuthReady);
        var totalAvailable = enabledStores.Sum(store => store.AvailableCount);
        var totalInstalled = enabledStores.Sum(store => store.InstalledCount);

        var statusText = enabledStores.Length == 0
            ? "Disabled"
            : readyCount > 0
                ? "Ready"
                : "Setup";
        var detailText = enabledStores.Length == 0
            ? "Enable at least one store to add its library to Steam."
            : totalAvailable > 0 || totalInstalled > 0
                ? $"{totalInstalled} installed / {totalAvailable} titles across {enabledStores.Length} enabled store(s)."
                : "Sign in to an enabled store, then sync its library into Steam.";

        return new UnifySteamSnapshot(
            statusText,
            detailText,
            lastRefreshedAtUtc,
            stores)
        {
            GameData = OmniLibraryGameDataProviderRegistry.BuildState(
                configuration,
                stores),
        };
    }

    public UnifySteamRefreshBatchResult RefreshLibraries(
        StoreSyncConfiguration configuration,
        string? storeId = null,
        bool skipUnconfigured = false,
        bool lightweight = false,
        bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        var results = new List<UnifySteamStoreRefreshResult>();
        foreach (var definition in ResolveDefinitions(storeId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
            if (!storeConfiguration.Enabled)
            {
                continue;
            }

            if (skipUnconfigured && !IsStoreConfigured(definition, storeConfiguration))
            {
                continue;
            }

            var previousSteamAppIds = (storeConfiguration.Cache?.Games ?? [])
                .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id) && game.SteamAppId != 0)
                .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().SteamAppId, StringComparer.OrdinalIgnoreCase);
            var result = RefreshStore(definition, storeConfiguration, lightweight, quiet);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Succeeded)
            {
                PreserveSteamAppIds(storeConfiguration.Cache?.Games, previousSteamAppIds);
            }
            results.Add(result);
        }

        return new UnifySteamRefreshBatchResult(results);
    }

    public IReadOnlyList<string> GetEnabledStoreIds(StoreSyncConfiguration configuration)
    {
        return Definitions
            .Where(definition =>
                GetStoreConfiguration(configuration, definition.Id).Enabled)
            .Select(definition => definition.Id)
            .ToArray();
    }

    private static bool IsStoreConfigured(OmniLibraryStoreDescriptor definition, UnifySteamStoreConfiguration storeConfiguration)
    {
        if (definition.Id.Equals(
                OmniLibraryRomSystemRegistry.StoreId,
                StringComparison.OrdinalIgnoreCase))
        {
            // A local ROM library has no account dependency. Enabling it is the
            // configuration step; Refresh creates the deterministic folders.
            return storeConfiguration.Enabled;
        }

        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
        {
            var epicAuthPath = ResolveReadableEpicAuthPath(storeConfiguration.AuthPath);
            return !string.IsNullOrWhiteSpace(ResolveToolPath(definition, storeConfiguration.ToolPath)) ||
                   !string.IsNullOrWhiteSpace(ResolveEpicLauncherPath()) ||
                   (!string.IsNullOrWhiteSpace(epicAuthPath) && File.Exists(epicAuthPath));
        }

        if (definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase))
        {
            return IsXboxAppInstalled() && IsXboxAppSignedIn();
        }

        var authPath = ResolveReadableGogAuthPath(storeConfiguration.AuthPath);
        return !string.IsNullOrWhiteSpace(authPath) && File.Exists(authPath);
    }

    public void StartLogin(StoreSyncConfiguration configuration, string storeId)
    {
        var definition = ResolveDefinition(storeId);
        var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);

        if (definition.Supports(OmniLibraryStoreCapabilities.ManagedWebSignIn))
        {
            storeConfiguration.AuthPath = definition.Id switch
            {
                "epic-games" => ManagedLegendaryHelper.UserDataPath,
                "gog-galaxy" => ManagedGogDlHelper.AuthPath,
                _ => storeConfiguration.AuthPath,
            };
            _journal.Append(
                "info",
                "omnilibrary",
                $"Prepared secure {definition.Title} sign-in.",
                $"Tools for Steam opens {definition.Title} in an isolated sign-in window and captures the authorization response automatically.");
            return;
        }

        if (definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase))
        {
            OpenInDefaultBrowser("msxbox://signIn/");
            _journal.Append(
                "info",
                "omnilibrary",
                "Opened Xbox sign-in.",
                "Sign in with the Windows account that owns Game Pass, then choose Sync Xbox Library in OmniLibrary.");
            return;
        }

        throw new InvalidOperationException(
            $"The {definition.Title} sign-in flow is not implemented.");
    }

    private static void OpenInDefaultBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        })?.Dispose();
    }

    internal static string BuildEpicLoginUrl()
    {
        var redirect = $"{EpicRedirectPrefix}?clientId={EpicClientId}&responseType=code";
        return $"https://www.epicgames.com/id/login?redirectUrl={Uri.EscapeDataString(redirect)}";
    }

    internal static string BuildGogLoginUrl() => BuildGogAuthUrl();

    public void CompleteManualCodeAuth(StoreSyncConfiguration configuration, string storeId, string value)
    {
        var definition = ResolveDefinition(storeId);
        if (!definition.SupportsManualCodeAuth)
        {
            throw new InvalidOperationException("This store uses an external login flow.");
        }

        var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
        var authCode = ExtractAuthorizationCode(value);
        if (string.IsNullOrWhiteSpace(authCode))
        {
            throw new InvalidOperationException("Paste the code, the full page URL, or the whole text from the login page.");
        }

        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
        {
            var toolPath = ManagedLegendaryHelper.Authenticate(authCode);

            storeConfiguration.ToolPath = toolPath;
            storeConfiguration.AuthPath = ManagedLegendaryHelper.UserDataPath;
            _journal.Append(
                "info",
                "omnilibrary",
                "Saved Epic sign-in.",
                "Legendary stores the Epic session in OmniLibrary's isolated data folder.");
            return;
        }

        if (definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase))
        {
            var toolPath = ManagedGogDlHelper.Authenticate(authCode);
            storeConfiguration.ToolPath = toolPath;
            storeConfiguration.AuthPath = ManagedGogDlHelper.AuthPath;
            _journal.Append(
                "info",
                "omnilibrary",
                "Saved GOG sign-in.",
                "gogdl stores the GOG session in OmniLibrary's isolated data folder.");
            return;
        }

        throw new InvalidOperationException(
            $"The {definition.Title} manual sign-in flow is not implemented.");
    }

    private UnifySteamStoreState BuildStoreState(
        OmniLibraryStoreDescriptor definition,
        StoreSyncConfiguration configuration,
        IReadOnlyList<StoreSyncDetectedTitleState> detectedTitles,
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> downloadStatuses)
    {
        var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
        var effectiveToolPath = ResolveToolPath(definition, storeConfiguration.ToolPath);
        var effectiveEpicLauncherPath = definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
            ? ResolveEpicLauncherPath()
            : string.Empty;
        var effectiveAuthPath = definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
            ? ResolveReadableGogAuthPath(storeConfiguration.AuthPath)
            : definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                ? ResolveReadableEpicAuthPath(storeConfiguration.AuthPath)
                : string.Empty;
        var cache = storeConfiguration.Cache ?? new UnifySteamLibraryCache();
        var cacheLastError = cache.LastError;
        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(effectiveEpicLauncherPath) &&
            cacheLastError.Contains("legendary was not found", StringComparison.OrdinalIgnoreCase))
        {
            cacheLastError = string.Empty;
        }
        var detectedByStore = detectedTitles
            .Where(title => string.Equals(title.StoreId, definition.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var effectiveDownloadStatuses = downloadStatuses;
        var cachedGames = cache.Games.AsEnumerable();
        if (definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase))
        {
            var forceInstalledRefresh = ShouldRefreshXboxInstallState(
                cache.Games,
                downloadStatuses);
            var installedXboxGames = LoadXboxInstalledGames(
                storeConfiguration.InstallPath,
                forceInstalledRefresh);
            if (ReconcileXboxInstalledDownloadStatuses(
                    cache.Games,
                    installedXboxGames,
                    effectiveDownloadStatuses))
            {
                effectiveDownloadStatuses = UnifySteamDownloadStatusStore.GetAll();
            }

            cachedGames = cache.Games
                .Where(game => game is not null)
                .Select(game => MergeXboxInstallState(
                    game,
                    installedXboxGames,
                    UnifySteamDownloadStatusStore.Get(
                        effectiveDownloadStatuses,
                        definition.Id,
                        game.Id)));
        }
        else if (definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase))
        {
            cachedGames = cache.Games
                .Where(game => game is not null)
                .Select(game => MergeGogInstallState(
                    game,
                    storeConfiguration.InstallPath,
                    UnifySteamDownloadStatusStore.Get(
                        effectiveDownloadStatuses,
                        definition.Id,
                        game.Id)));
        }

        var games = cachedGames
            .Select(game => ToGameState(
                definition,
                game,
                detectedByStore,
                effectiveDownloadStatuses))
            .OrderByDescending(game => game.Installed)
            .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var installedCount = games.Count(game => game.Installed);
        var availableCount = games.Length;
        var xboxAppInstalled = definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase) && IsXboxAppInstalled();
        var xboxSignedIn = xboxAppInstalled && IsXboxAppSignedIn();
        var isRomLibrary = definition.Id.Equals(
            OmniLibraryRomSystemRegistry.StoreId,
            StringComparison.OrdinalIgnoreCase);
        var romSystemStates = isRomLibrary
            ? BuildRomSystemStates(games, storeConfiguration)
            : [];
        var toolDetected = isRomLibrary
            ? romSystemStates.Any(system => system.EmulatorDetected)
            : definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(effectiveToolPath) || !string.IsNullOrWhiteSpace(effectiveEpicLauncherPath)
            : definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase)
                ? xboxAppInstalled || !string.IsNullOrWhiteSpace(effectiveToolPath)
                : !string.IsNullOrWhiteSpace(effectiveToolPath);
        var authConfigured = isRomLibrary
            ? true
            : definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(effectiveAuthPath)
            : definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                ? !string.IsNullOrWhiteSpace(effectiveAuthPath) || toolDetected
                : definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase)
                    ? xboxSignedIn
                    : toolDetected;
        var authReady = isRomLibrary
            ? true
            : definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase)
            ? xboxSignedIn
            : !string.IsNullOrWhiteSpace(cache.AccountName);
        var canRefresh = storeConfiguration.Enabled && (isRomLibrary || authReady);
        var steamSessionStartedAtUtc = GetSteamSessionStartedAtUtc();
        var readiness = OmniLibraryLifecycle.Evaluate(
            storeConfiguration,
            authConfigured,
            authReady,
            games.Length,
            games.Length > 0 && games.All(game => game.SteamAppId != 0),
            ComputePreparedCatalogSignature(storeConfiguration),
            cacheLastError,
            steamSessionStartedAtUtc);
        var preparationComplete = readiness.CurrentShortcutCatalogReady;
        var readyForLibraryTab = readiness.ReadyForLibraryTab;
        var steamRestartRequired = readiness.SteamRestartRequired;

        var statusText = !storeConfiguration.Enabled
            ? "Disabled"
            : !string.IsNullOrWhiteSpace(cacheLastError)
                ? "Attention"
                : !authReady && definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                    ? authConfigured
                        ? "Login required"
                        : "Setup required"
                    : !authReady && definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
                        ? authConfigured
                            ? "Login required"
                            : "Setup required"
                        : !authReady && definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase)
                            ? xboxAppInstalled ? "Sign-in required" : "Xbox app required"
                        : isRomLibrary && availableCount == 0
                            ? "Waiting for ROMs"
                        : availableCount > 0
                            ? "Ready"
                            : "Not loaded";

        var detailText = !string.IsNullOrWhiteSpace(cacheLastError)
            ? cacheLastError
            : isRomLibrary
                ? !string.IsNullOrWhiteSpace(cache.DetailText)
                    ? cache.DetailText
                    : $"Add ROMs to the matching system folders in {OmniLibraryRomSystemRegistry.ResolveRootPath(storeConfiguration.InstallPath)}."
            : !authReady && definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                ? authConfigured
                    ? !string.IsNullOrWhiteSpace(cache.DetailText)
                        ? cache.DetailText
                        : "Sign in with Epic. OmniLibrary completes the connection and refreshes the library automatically."
                    : "Sign in with Epic or install Epic Games Launcher, Heroic, or legendary, then refresh OmniLibrary."
                : !authReady && definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
                    ? authConfigured
                        ? "Sign in with GOG, then refresh the library."
                        : "Sign in with GOG or refresh after Heroic has saved GOG auth data."
                    : !authReady && definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase)
                        ? xboxAppInstalled
                            ? "Sign in inside the official Xbox app, then sync the Xbox library."
                            : "Install the Xbox app, sign in there, and sync the PC Game Pass catalog."
                    : !string.IsNullOrWhiteSpace(cache.DetailText)
                        ? cache.DetailText
                        : availableCount > 0
                            ? $"{installedCount} installed / {availableCount} total."
                            : "Refresh this store to build the library snapshot.";
        if (storeConfiguration.Enabled && authReady && availableCount > 0 && string.IsNullOrWhiteSpace(cacheLastError))
        {
            if (!preparationComplete)
            {
                var preparationFailed = storeConfiguration.PreparationStatus.Equals(
                    "failed",
                    StringComparison.OrdinalIgnoreCase);
                statusText = preparationFailed ? "Preparation failed" : "Preparing";
                detailText = !string.IsNullOrWhiteSpace(storeConfiguration.PreparationDetail)
                    ? storeConfiguration.PreparationDetail
                    : "OmniLibrary is preparing Steam shortcuts in the background. Artwork continues independently.";
            }
            else if (steamRestartRequired)
            {
                statusText = "Restart required";
                detailText = "Preparation is complete. Restart Steam once to activate the fully populated store tab.";
            }
            else if (readyForLibraryTab)
            {
                statusText = "Ready";
                detailText = string.IsNullOrWhiteSpace(cache.DetailText)
                    ? $"{installedCount} installed / {availableCount} total."
                    : cache.DetailText;
            }
        }

        return new UnifySteamStoreState(
            definition.Id,
            definition.Title,
            storeConfiguration.Enabled,
            toolDetected,
            authConfigured,
            authReady,
            canRefresh,
            readyForLibraryTab,
            steamRestartRequired,
            storeConfiguration.PreparationStatus,
            storeConfiguration.PreparationDetail,
            Math.Max(0, storeConfiguration.PreparationCompletedCount),
            Math.Max(0, storeConfiguration.PreparationTotalCount),
            definition.SupportsManualCodeAuth,
            definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase) &&
            storeConfiguration.IncludeXboxPcGamePass,
            definition.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase) &&
            storeConfiguration.IncludeXboxCloudGaming,
            statusText,
            detailText,
            cache.AccountName,
            isRomLibrary
                ? OmniLibraryRomSystemRegistry.ResolveRootPath(storeConfiguration.InstallPath)
                : storeConfiguration.InstallPath,
            cache.RefreshedAtUtc,
            installedCount,
            availableCount,
            games)
        {
            Lifecycle = readiness.Lifecycle,
            Capabilities = OmniLibraryStoreRegistry.GetCapabilityIds(definition),
            LibraryTabs = OmniLibraryStoreRegistry.BuildLibraryTabSummaries(
                definition,
                isRomLibrary ? romSystemStates : null),
            DownloadWorkers = Math.Clamp(storeConfiguration.DownloadWorkers, 1, 32),
            DownloadTimeoutSeconds = Math.Clamp(
                storeConfiguration.DownloadTimeoutSeconds,
                15,
                300),
            GogDlcEnabled =
                !definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase) ||
                storeConfiguration.IncludeGogDlc,
            GogGalaxyLaunchEnabled =
                definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase) &&
                storeConfiguration.PreferGogGalaxyForLaunch,
            AchievementsEnabled = ResolveGameDataProvider(configuration, definition.Id)?.Enabled == true,
            AchievementProviderConfigured = IsGameDataProviderConfigured(
                configuration,
                definition.Id,
                authReady),
            AchievementProviderName = ResolveGameDataProviderDescriptor(definition.Id)?.Title ??
                                      string.Empty,
            AchievementProviderDetail = BuildGameDataProviderDetail(
                configuration,
                definition.Id,
                authReady),
            AchievementCredentialPreview = PreviewSecret(
                ResolveGameDataProvider(configuration, definition.Id)?.Credential ??
                string.Empty),
            RomSystems = isRomLibrary
                ? romSystemStates
                : [],
            ToolPath = isRomLibrary
                ? romSystemStates.FirstOrDefault(system => system.Id.Equals(
                    "psp",
                    StringComparison.OrdinalIgnoreCase))?.EmulatorPath ?? string.Empty
                : effectiveToolPath,
        };
    }

    private static IReadOnlyList<OmniLibraryRomSystemState> BuildRomSystemStates(
        IReadOnlyList<UnifySteamGameState> games,
        UnifySteamStoreConfiguration storeConfiguration)
    {
        var root = OmniLibraryRomSystemRegistry.ResolveRootPath(storeConfiguration.InstallPath);
        return OmniLibraryRomSystemRegistry.Supported
            .Select(system =>
            {
                storeConfiguration.RomSystems.TryGetValue(system.Id, out var settings);
                var configuredPath = settings?.EmulatorPath ??
                    (system.Id.Equals("psp", StringComparison.OrdinalIgnoreCase)
                        ? storeConfiguration.ToolPath
                        : string.Empty);
                var resolvedPath = ResolveRomEmulatorExecutable(system.Id, configuredPath);
                return new OmniLibraryRomSystemState(
                    system.Id,
                    system.Title,
                    system.EmulatorTitle,
                    games.Count(game => game.PlatformId.Equals(
                        system.Id,
                        StringComparison.OrdinalIgnoreCase)),
                    system.Id,
                    games.Where(game =>
                            game.PlatformId.Equals(
                                system.Id,
                                StringComparison.OrdinalIgnoreCase) &&
                            game.SteamAppId != 0)
                        .Select(game => game.SteamAppId)
                        .Distinct()
                        .OrderBy(appId => appId)
                        .ToArray())
                {
                    EmulatorPath = resolvedPath.Length > 0 ? resolvedPath : configuredPath,
                    ExecutableName = system.EmulatorExecutableName,
                    FolderPath = Path.Combine(root, system.FolderName),
                    EmulatorDetected = resolvedPath.Length > 0,
                    Fullscreen = settings?.Fullscreen ?? true,
                };
            })
            .ToArray();
    }

    private static OmniLibraryGameDataProviderDescriptor?
        ResolveGameDataProviderDescriptor(string storeId) =>
        OmniLibraryGameDataProviderRegistry.ResolveForStore(storeId);

    private static OmniLibraryGameDataProviderConfiguration?
        ResolveGameDataProvider(
            StoreSyncConfiguration configuration,
            string storeId)
    {
        var descriptor = ResolveGameDataProviderDescriptor(storeId);
        return descriptor is not null &&
               configuration.UnifySteam.GameData.Providers.TryGetValue(
                   descriptor.Id,
                   out var provider)
            ? provider
            : null;
    }

    private static bool IsGameDataProviderConfigured(
        StoreSyncConfiguration configuration,
        string storeId,
        bool storeAuthReady)
    {
        var descriptor = ResolveGameDataProviderDescriptor(storeId);
        var provider = ResolveGameDataProvider(configuration, storeId);
        if (descriptor is null || provider?.Enabled != true)
        {
            return false;
        }

        return descriptor.SetupKind switch
        {
            "openxbl" => !string.IsNullOrWhiteSpace(provider.Credential),
            "store-account" => storeAuthReady,
            "local-path" => !string.IsNullOrWhiteSpace(provider.DataPath),
            "username-api-key" =>
                !string.IsNullOrWhiteSpace(provider.AccountName) &&
                !string.IsNullOrWhiteSpace(provider.Credential),
            _ =>
                (descriptor.Supports(OmniLibraryGameDataCapabilities.StoreAccount) &&
                 storeAuthReady) ||
                !string.IsNullOrWhiteSpace(provider.Credential) ||
                !string.IsNullOrWhiteSpace(provider.SecondaryCredential) ||
                !string.IsNullOrWhiteSpace(provider.AccountId) ||
                !string.IsNullOrWhiteSpace(provider.AccountName) ||
                !string.IsNullOrWhiteSpace(provider.DataPath),
        };
    }

    private static string BuildGameDataProviderDetail(
        StoreSyncConfiguration configuration,
        string storeId,
        bool storeAuthReady)
    {
        var descriptor = ResolveGameDataProviderDescriptor(storeId);
        var provider = ResolveGameDataProvider(configuration, storeId);
        if (descriptor is null || provider is null)
        {
            return "No game-data provider is registered for this store.";
        }

        if (!configuration.UnifySteam.GameData.Enabled || !provider.Enabled)
        {
            return "Achievements and enhanced metadata are disabled for this provider.";
        }

        if (descriptor.Supports(OmniLibraryGameDataCapabilities.StoreAccount))
        {
            return storeAuthReady
                ? $"Uses the connected {descriptor.Title} account and refreshes only opened or recently played titles."
                : $"Connect {descriptor.Title} to show verified achievement progress.";
        }

        return !string.IsNullOrWhiteSpace(provider.Credential) ||
               !string.IsNullOrWhiteSpace(provider.AccountId) ||
               !string.IsNullOrWhiteSpace(provider.AccountName) ||
               !string.IsNullOrWhiteSpace(provider.DataPath)
            ? $"{descriptor.Title} is configured and loaded on demand."
            : $"Configure {descriptor.Title} in Achievements & Metadata.";
    }

    internal static string ComputePreparedCatalogSignature(UnifySteamStoreConfiguration storeConfiguration)
    {
        var games = storeConfiguration.Cache?.Games?
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .OrderBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .Select(game => $"{game.Id.Trim().ToLowerInvariant()}\t{game.SteamAppId}")
            .ToArray() ?? [];
        if (games.Length == 0)
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', games))));
    }

    internal static string GetRemoteCatalogSignature(UnifySteamStoreConfiguration storeConfiguration)
    {
        return string.IsNullOrWhiteSpace(storeConfiguration.RemoteCatalogSignature)
            ? ComputeLibraryCatalogSignature(storeConfiguration.Cache?.Games?.Select(game => game.Id) ?? [])
            : storeConfiguration.RemoteCatalogSignature;
    }

    internal static string ComputeLibraryStateSignature(UnifySteamStoreConfiguration storeConfiguration)
    {
        var cache = storeConfiguration.Cache ?? new UnifySteamLibraryCache();
        var games = cache.Games
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .OrderBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .Select(game => string.Join(
                '\t',
                game.Id.Trim().ToLowerInvariant(),
                game.Title.Trim(),
                game.Installed,
                game.CloudPlayable,
                game.InstallPath.Trim(),
                game.ExecutablePath.Trim(),
                game.Version.Trim(),
                game.DeliveryProvider.Trim(),
                game.ThirdPartyManagedApp.Trim(),
                game.PartnerLinkType.Trim(),
                game.PartnerLinkId.Trim(),
                game.ProviderGameId.Trim(),
                game.PlatformId.Trim(),
                game.PlatformTitle.Trim(),
                game.RomPath.Trim(),
                game.RegistryPath.Trim(),
                game.RegistryValueName.Trim(),
                game.ProcessNames.Trim(),
                game.HasInstallableAsset,
                game.RequiresAccountLink,
                game.RequiresExternalLauncher,
                game.RequiresEpicLauncherBridge,
                game.SupportsCloudSaves,
                game.IsPreloaded,
                game.LatestVersion.Trim(),
                game.PreparationSignature.Trim(),
                game.ImageUrl.Trim(),
                game.SteamAppId))
            .ToArray();
        var state = string.Join(
            '\n',
            storeConfiguration.RemoteCatalogSignature,
            cache.AccountName,
            cache.StatusText,
            cache.DetailText,
            cache.LastError,
            string.Join('\n', games));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

    private static string ComputeLibraryCatalogSignature(IEnumerable<UnifySteamGameCacheEntry> games)
    {
        return ComputeLibraryCatalogSignature(games.Select(game => game.Id));
    }

    private static string ComputeLibraryCatalogSignature(IEnumerable<string?> gameIds)
    {
        var normalizedIds = gameIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalizedIds.Length == 0
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalizedIds))));
    }

    private static void PreserveSteamAppIds(
        IEnumerable<UnifySteamGameCacheEntry>? games,
        IReadOnlyDictionary<string, uint> previousSteamAppIds)
    {
        if (games is null || previousSteamAppIds.Count == 0)
        {
            return;
        }

        foreach (var game in games)
        {
            if (game is not null &&
                game.SteamAppId == 0 &&
                previousSteamAppIds.TryGetValue(game.Id, out var steamAppId))
            {
                game.SteamAppId = steamAppId;
            }
        }
    }

    private static DateTimeOffset? GetSteamSessionStartedAtUtc()
    {
        DateTimeOffset? earliestStart = null;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    if (!earliestStart.HasValue || startedAt < earliestStart.Value)
                    {
                        earliestStart = startedAt;
                    }
                }
                catch
                {
                }
            }
        }

        return earliestStart;
    }

    internal static UnifySteamGameCacheEntry MergeXboxInstallState(
        UnifySteamGameCacheEntry game,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedGames,
        UnifySteamDownloadStatus downloadStatus)
    {
        TryResolveXboxInstalledGame(game, installedGames, out var installed);
        var downloadActive =
            UnifySteamDownloadStatusStore.IsActivelyTransferring(downloadStatus.Status) ||
            downloadStatus.Status == "paused";
        var uninstallPending =
            downloadStatus.Status.Equals(
                "uninstall-action-required",
                StringComparison.OrdinalIgnoreCase);
        var scannerReportsInstalled = installed?.Installed == true;
        var removalConfirmed = ConfirmXboxRemoval(
            game.Id,
            scannerReportsInstalled,
            uninstallPending);
        var installationReady =
            scannerReportsInstalled ||
            (!downloadActive &&
             uninstallPending &&
             !removalConfirmed &&
             game.Installed);
        return new UnifySteamGameCacheEntry
        {
            Id = game.Id,
            Title = game.Title,
            Installed = installationReady,
            CloudPlayable = game.CloudPlayable,
            InstallPath = installationReady ? installed?.InstallPath ?? string.Empty : string.Empty,
            ExecutablePath = installationReady ? installed?.ExecutablePath ?? string.Empty : string.Empty,
            Version = installationReady ? installed?.Version ?? string.Empty : string.Empty,
            ImageUrl = game.ImageUrl,
            HeroImageUrl = game.HeroImageUrl,
            SteamAppId = game.SteamAppId,
            DeliveryProvider = game.DeliveryProvider,
            ThirdPartyManagedApp = game.ThirdPartyManagedApp,
            PartnerLinkType = game.PartnerLinkType,
            PartnerLinkId = game.PartnerLinkId,
            ProviderGameId = installationReady
                ? installed?.Id ?? game.ProviderGameId
                : game.ProviderGameId,
            StoreTitleId = FirstNonEmpty(
                game.StoreTitleId,
                installed?.StoreTitleId),
            StoreNamespace = game.StoreNamespace,
            RegistryPath = game.RegistryPath,
            RegistryValueName = game.RegistryValueName,
            ProcessNames = game.ProcessNames,
            HasInstallableAsset = game.HasInstallableAsset,
            RequiresAccountLink = game.RequiresAccountLink,
            RequiresExternalLauncher = game.RequiresExternalLauncher,
            RequiresEpicLauncherBridge = game.RequiresEpicLauncherBridge,
            SupportsCloudSaves = game.SupportsCloudSaves,
            IsPreloaded = game.IsPreloaded,
            LatestVersion = game.LatestVersion,
            PreparationSignature = game.PreparationSignature,
        };
    }

    private static bool ReconcileXboxInstalledDownloadStatuses(
        IReadOnlyList<UnifySteamGameCacheEntry> catalogGames,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedGames,
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> downloadStatuses)
    {
        var changed = false;
        foreach (var game in catalogGames.Where(game =>
                     game is not null &&
                     !string.IsNullOrWhiteSpace(game.Id)))
        {
            var status = UnifySteamDownloadStatusStore.Get(
                downloadStatuses,
                "xbox-game-pass",
                game.Id);
            var canComplete =
                UnifySteamDownloadStatusStore.IsActivelyTransferring(status.Status) ||
                status.Status is "paused" or "action-required" or "failed";
            if (!canComplete ||
                !TryResolveXboxInstalledGame(
                    game,
                    installedGames,
                    out _))
            {
                continue;
            }

            UnifySteamDownloadStatusStore.Update(
                "xbox-game-pass",
                game.Id,
                "completed",
                100,
                "Installed.",
                workerProcessId: 0,
                downloadedBytes: Math.Max(
                    status.DownloadedBytes,
                    status.TotalBytes),
                totalBytes: status.TotalBytes,
                attempt: status.Attempt);
            changed = true;
        }

        return changed;
    }

    private static UnifySteamGameCacheEntry MergeGogInstallState(
        UnifySteamGameCacheEntry game,
        string configuredInstallRoot,
        UnifySteamDownloadStatus downloadStatus)
    {
        var transaction = GogOperationJournal.Get(game.Id);
        var operationRelevant =
            transaction is not null ||
            downloadStatus.Status is
                "action-required" or
                "uninstall-action-required" or
                "uninstalling" or
                "finalizing" or
                "failed";
        var probe = GogInstallStateTracker.Probe(
            game.Id,
            configuredInstallRoot,
            game.InstallPath,
            force: operationRelevant);
        var transferActive =
            UnifySteamDownloadStatusStore.IsActivelyTransferring(downloadStatus.Status) ||
            downloadStatus.Status == "paused";
        var installed = probe.Installed ||
                        (!probe.Conclusive && game.Installed && !transferActive);
        var installPath = installed
            ? FirstNonEmpty(probe.InstallPath, game.InstallPath)
            : string.Empty;
        var executablePath = installed
            ? FirstNonEmpty(probe.ExecutablePath, game.ExecutablePath)
            : string.Empty;
        return new UnifySteamGameCacheEntry
        {
            Id = game.Id,
            Title = game.Title,
            Installed = installed,
            CloudPlayable = game.CloudPlayable,
            InstallPath = installPath,
            ExecutablePath = executablePath,
            Version = installed
                ? FirstNonEmpty(probe.BuildId, game.Version)
                : string.Empty,
            DeliveryProvider = game.DeliveryProvider,
            ThirdPartyManagedApp = game.ThirdPartyManagedApp,
            PartnerLinkType = game.PartnerLinkType,
            PartnerLinkId = game.PartnerLinkId,
            ProviderGameId = game.ProviderGameId,
            RegistryPath = game.RegistryPath,
            RegistryValueName = game.RegistryValueName,
            ProcessNames = game.ProcessNames,
            HasInstallableAsset = game.HasInstallableAsset,
            RequiresAccountLink = game.RequiresAccountLink,
            RequiresExternalLauncher = game.RequiresExternalLauncher,
            RequiresEpicLauncherBridge = game.RequiresEpicLauncherBridge,
            SupportsCloudSaves = game.SupportsCloudSaves,
            IsPreloaded = game.IsPreloaded,
            LatestVersion = game.LatestVersion,
            PreparationSignature = game.PreparationSignature,
            ImageUrl = game.ImageUrl,
            HeroImageUrl = game.HeroImageUrl,
            SteamAppId = game.SteamAppId,
        };
    }

    internal static bool TryConfirmPendingXboxRemoval(
        UnifySteamGameState game,
        UnifySteamDownloadStatus downloadStatus)
    {
        if (!downloadStatus.Status.Equals(
                "uninstall-action-required",
                StringComparison.OrdinalIgnoreCase) ||
            !TryProbeXboxGameInstallation(game, out var scannerReportsInstalled))
        {
            return false;
        }

        return ConfirmXboxRemoval(
            game.Id,
            scannerReportsInstalled,
            uninstallPending: true);
    }

    internal static bool TryProbeXboxGameInstallation(
        UnifySteamGameState game,
        out bool installed)
    {
        installed = game.Installed;
        if (!game.Installed)
        {
            installed = false;
            return true;
        }

        var contentDirectory = game.InstallPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contentDirectory))
        {
            // A previously installed catalog entry normally always has its
            // Content path. Treat missing metadata as inconclusive instead of
            // turning a temporary cache issue into a false uninstall.
            return false;
        }

        if (!Directory.Exists(contentDirectory))
        {
            installed = false;
            return true;
        }

        var configPath = new[]
            {
                Path.Combine(contentDirectory, "MicrosoftGame.Config"),
                Path.Combine(contentDirectory, "MicrosoftGame.config"),
            }
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            installed = false;
            return true;
        }

        try
        {
            var document = XDocument.Load(configPath, LoadOptions.None);
            var root = document.Root;
            var storeId = root?.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals(
                    "StoreId",
                    StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;
            if (!string.Equals(storeId, game.Id, StringComparison.OrdinalIgnoreCase) &&
                !XboxProductRelationStore.IsRelated(game.Id, storeId))
            {
                return false;
            }

            var identity = root?.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals(
                    "Identity",
                    StringComparison.OrdinalIgnoreCase));
            var executable = root?.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals(
                    "Executable",
                    StringComparison.OrdinalIgnoreCase));
            var identityName = identity?.Attribute("Name")?.Value?.Trim() ?? string.Empty;
            var identityVersion = identity?.Attribute("Version")?.Value?.Trim() ?? string.Empty;
            var executableName = executable?.Attribute("Name")?.Value?.Trim() ?? string.Empty;
            var executablePath = string.IsNullOrWhiteSpace(executableName)
                ? game.ExecutablePath
                : Path.Combine(contentDirectory, executableName);
            installed = IsReadyXboxExecutable(
                executablePath,
                identityName,
                identityVersion,
                LoadRegisteredXboxPackageNames());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool ConfirmXboxRemoval(
        string productId,
        bool scannerReportsInstalled,
        bool uninstallPending)
    {
        var confirmed = false;
        lock (XboxInstalledCacheGate)
        {
            if (!uninstallPending || scannerReportsInstalled)
            {
                XboxPendingUninstallMisses.Remove(productId);
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (!XboxPendingUninstallMisses.TryGetValue(productId, out var observation))
            {
                XboxPendingUninstallMisses[productId] = (1, now);
                return false;
            }

            observation = (observation.MissingCount + 1, observation.FirstMissingAtUtc);
            confirmed =
                observation.MissingCount >= 2 &&
                now - observation.FirstMissingAtUtc >= TimeSpan.FromSeconds(1);
            if (confirmed)
            {
                XboxPendingUninstallMisses.Remove(productId);
                foreach (var cached in XboxInstalledCache.Values)
                {
                    cached.Games.Remove(productId);
                }
            }
            else
            {
                XboxPendingUninstallMisses[productId] = observation;
            }
        }

        if (confirmed)
        {
            UnifySteamDownloadStatusStore.Clear("xbox-game-pass", productId);
        }
        return confirmed;
    }

    private UnifySteamGameState ToGameState(
        OmniLibraryStoreDescriptor definition,
        UnifySteamGameCacheEntry game,
        IReadOnlyList<StoreSyncDetectedTitleState> detectedTitles,
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> downloadStatuses)
    {
        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
        {
            NormalizeEpicDeliveryCapabilities(game);
        }

        var matchedDetectedTitle = detectedTitles.FirstOrDefault(title =>
        {
            if (!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                string.Equals(title.ExecutablePath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(game.InstallPath) &&
                title.ExecutablePath.StartsWith(game.InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(title.Title, game.Title, StringComparison.OrdinalIgnoreCase);
        });

        var externalRegistryIsAuthoritative =
            definition.Id.Equals(
                "epic-games",
                StringComparison.OrdinalIgnoreCase) &&
            game.RequiresExternalLauncher &&
            !string.IsNullOrWhiteSpace(game.RegistryPath);
        var externalInstallPath = string.Empty;
        var externalInstalled = externalRegistryIsAuthoritative &&
                                TryReadEpicExternalInstallPath(
                                    game.RegistryPath,
                                    game.RegistryValueName,
                                    out externalInstallPath);
        var installed = externalRegistryIsAuthoritative
            ? externalInstalled
            : definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
                ? game.Installed
                : game.Installed || matchedDetectedTitle is not null;
        var syncedToSteam = game.SteamAppId != 0 || matchedDetectedTitle is not null;
        var executablePath = externalInstalled &&
                             !string.Equals(
                                 externalInstallPath,
                                 game.InstallPath,
                                 StringComparison.OrdinalIgnoreCase)
            ? FindEpicExternalExecutable(externalInstallPath, game.ProcessNames)
            : !string.IsNullOrWhiteSpace(game.ExecutablePath)
            ? game.ExecutablePath
            : matchedDetectedTitle?.ExecutablePath ?? string.Empty;
        var installPath = externalInstalled
            ? externalInstallPath
            : !string.IsNullOrWhiteSpace(game.InstallPath)
            ? game.InstallPath
            : matchedDetectedTitle?.StartDirectory ?? string.Empty;
        var updateAvailable =
            installed &&
            !string.IsNullOrWhiteSpace(game.LatestVersion) &&
            !string.IsNullOrWhiteSpace(game.Version) &&
            !string.Equals(
                game.LatestVersion,
                game.Version,
                StringComparison.OrdinalIgnoreCase);
        var providerDisplayName =
            definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                ? GetEpicProviderDisplayName(game.DeliveryProvider)
                : definition.Title;
        var eaAppAvailable =
            game.DeliveryProvider.Equals(
                "ea-app",
                StringComparison.OrdinalIgnoreCase) &&
            EaAppIntegration.GetAvailability().IsAvailable;
        var eaAppRequired =
            game.DeliveryProvider.Equals(
                "ea-app",
                StringComparison.OrdinalIgnoreCase) &&
            !eaAppAvailable;
        var statusText = game.IsPreloaded
            ? "Preloaded"
            : updateAvailable
                ? "Update available"
            : !installed
            ? eaAppRequired
                ? "EA app required"
                : game.RequiresExternalLauncher
                    ? $"Available via {providerDisplayName}"
                    : "Available"
            : syncedToSteam
                ? "Installed + Synced"
                : "Installed";
        var detailText = game.IsPreloaded
            ? "Game files are preloaded. Play becomes available after the store release unlock."
            : !installed
            ? eaAppRequired
                ? "Owned on Epic. Install the official EA app before linking the accounts and installing this title."
                : game.RequiresExternalLauncher
                    ? $"Owned on Epic. Installation and account linking continue in {providerDisplayName}."
                    : "In your account library."
            : !string.IsNullOrWhiteSpace(installPath)
                ? installPath
                : "Installed locally.";
        var downloadState = ToDownloadState(
            definition.Id,
            game.Id,
            downloadStatuses);
        if (externalRegistryIsAuthoritative &&
            ((installed &&
              downloadState.Status.Equals(
                  "action-required",
                  StringComparison.OrdinalIgnoreCase)) ||
             (!installed &&
              downloadState.Status.Equals(
                  "uninstall-action-required",
                  StringComparison.OrdinalIgnoreCase))))
        {
            UnifySteamDownloadStatusStore.Clear(definition.Id, game.Id);
            downloadState = new UnifySteamDownloadState(
                "idle",
                0,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
        }
        var externalAction = game.DeliveryProvider.Equals(
                "ea-app",
                StringComparison.OrdinalIgnoreCase)
            ? EaAppIntegration.GetExternalAction(
                installed,
                eaAppAvailable,
                downloadState.Status)
            : string.Empty;

        return new UnifySteamGameState(
            game.Id,
            game.Title,
            installed,
            game.CloudPlayable,
            syncedToSteam,
            statusText,
            detailText,
            NormalizeImageUrl(game.ImageUrl),
            installPath,
            executablePath,
            game.Version,
            game.SteamAppId,
            downloadState)
        {
            DeliveryProvider = game.DeliveryProvider,
            ProviderDisplayName = providerDisplayName,
            RequiresAccountLink = game.RequiresAccountLink,
            RequiresExternalLauncher = game.RequiresExternalLauncher,
            CanInstallDirectly = CanInstallDirectly(
                definition.Id,
                game.CloudPlayable,
                game.DeliveryProvider,
                game.HasInstallableAsset),
            ExternalAction = externalAction,
            SupportsCloudSaves = game.SupportsCloudSaves,
            IsPreloaded = game.IsPreloaded,
            UpdateAvailable = updateAvailable,
            StoreTitleId = game.StoreTitleId,
            StoreNamespace = game.StoreNamespace,
            PlatformId = game.PlatformId,
            PlatformTitle = game.PlatformTitle,
            RomPath = game.RomPath,
        };
    }

    internal static string ResolvePpssppExecutable(string? configuredPath) =>
        ResolveRomEmulatorExecutable("psp", configuredPath);

    internal static string ResolveRomEmulatorExecutable(
        string systemId,
        string? configuredPath)
    {
        var system = OmniLibraryRomSystemRegistry.GetRequired(systemId);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath.Trim());
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(
            localAppData,
            "Programs",
            system.EmulatorTitle,
            system.EmulatorExecutableName));
        candidates.Add(Path.Combine(localAppData, system.EmulatorTitle, system.EmulatorExecutableName));
        candidates.Add(Path.Combine(programFiles, system.EmulatorTitle, system.EmulatorExecutableName));
        candidates.Add(Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            system.Id,
            system.EmulatorExecutableName));

        if (system.Id.Equals("gamecube", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(programFiles, "Dolphin Emulator", system.EmulatorExecutableName));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
            }
        }

        try
        {
            var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directory in pathDirectories)
            {
                var candidate = Path.Combine(directory.Trim('"'), system.EmulatorExecutableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static UnifySteamDownloadState ToDownloadState(
        string storeId,
        string gameId,
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> downloadStatuses)
    {
        var download = UnifySteamDownloadStatusStore.Get(downloadStatuses, storeId, gameId);
        return new UnifySteamDownloadState(
            download.Status,
            download.ProgressPercent,
            download.DetailText,
            download.DownloadedBytes,
            download.TotalBytes,
            download.DownloadBytesPerSecond,
            download.DecompressedBytesPerSecond,
            download.DiskWriteBytesPerSecond,
            download.DiskReadBytesPerSecond,
            download.Attempt);
    }

    private UnifySteamStoreRefreshResult RefreshStore(
        OmniLibraryStoreDescriptor definition,
        UnifySteamStoreConfiguration storeConfiguration,
        bool lightweight,
        bool quiet)
    {
        try
        {
            switch (definition.Id)
            {
                case OmniLibraryRomSystemRegistry.StoreId:
                    OmniLibraryRomLibrary.Refresh(storeConfiguration);
                    break;
                case "epic-games":
                    RefreshEpicStore(definition, storeConfiguration, quiet);
                    break;
                case "gog-galaxy":
                    RefreshGogStore(storeConfiguration, lightweight, quiet);
                    break;
                case "xbox-game-pass":
                    RefreshXboxStore(storeConfiguration, lightweight, quiet);
                    break;
                default:
                    throw new InvalidOperationException("Unknown OmniLibrary store.");
            }

            var cache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
            OmniLibraryLifecycle.SetStage(
                storeConfiguration,
                "authentication",
                definition.Id.Equals(
                    OmniLibraryRomSystemRegistry.StoreId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(cache.AccountName)
                    ? "ready"
                    : "required");
            OmniLibraryLifecycle.SetStage(
                storeConfiguration,
                "catalog",
                cache.Games.Count > 0 ? "ready" : "empty");
            return new UnifySteamStoreRefreshResult(definition.Id, true, string.Empty);
        }
        catch (Exception exception)
        {
            storeConfiguration.Cache ??= new UnifySteamLibraryCache();
            storeConfiguration.Cache.LastError = exception.Message.Trim();
            storeConfiguration.Cache.StatusText = "Attention";
            storeConfiguration.Cache.DetailText = exception.Message.Trim();
            storeConfiguration.Cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            OmniLibraryLifecycle.SetStage(
                storeConfiguration,
                "catalog",
                storeConfiguration.Cache.Games.Count > 0 ? "degraded" : "failed",
                exception.Message);
            _journal.Append("warning", "unifysteam", $"Failed to refresh {definition.Title}.", exception.Message);
            return new UnifySteamStoreRefreshResult(definition.Id, false, exception.Message.Trim());
        }
    }

    private void RefreshEpicStore(
        OmniLibraryStoreDescriptor definition,
        UnifySteamStoreConfiguration storeConfiguration,
        bool quiet)
    {
        var toolPath = ResolveToolPath(definition, storeConfiguration.ToolPath);
        var launcherInstalledGames = LoadEpicLauncherInstalledGames();
        if (!string.IsNullOrWhiteSpace(toolPath))
        {
            storeConfiguration.ToolPath = toolPath;
            storeConfiguration.AuthPath = ManagedLegendaryHelper.UserDataPath;
            var installedMap = MergeEpicInstalledGames(
                LoadEpicInstalledGames(toolPath),
                launcherInstalledGames);
            var status = LoadEpicStatus(toolPath);
            var cache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();

            if (!status.Authenticated)
            {
                cache.AccountName = string.Empty;
                cache.LastError = string.Empty;
                cache.StatusText = "Login required";
                cache.DetailText = installedMap.Count > 0
                    ? "Installed titles were found locally, but Epic sign-in is still required for the full library."
                    : "Choose Sign in to Epic. OmniLibrary will finish the secure sign-in and sync automatically.";
                cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
                if (cache.Games.Count == 0 && installedMap.Count > 0)
                {
                    cache.Games = installedMap.Values
                        .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    storeConfiguration.RemoteCatalogSignature = ComputeLibraryCatalogSignature(cache.Games);
                }

                if (!quiet)
                {
                    _journal.Append("info", "omnilibrary", "Epic refresh needs sign-in.", cache.DetailText);
                }
                return;
            }

            var games = LoadEpicLibraryGames(toolPath, installedMap);
            PreserveEpicPreparationState(cache.Games, games);
            cache.AccountName = status.AccountName;
            cache.Games = games;
            storeConfiguration.RemoteCatalogSignature = ComputeLibraryCatalogSignature(games);
            cache.LastError = string.Empty;
            cache.StatusText = "Ready";
            cache.DetailText = $"Loaded {games.Count} Epic title{(games.Count == 1 ? string.Empty : "s")} for {status.AccountName}.";
            cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            if (!quiet)
            {
                _journal.Append("info", "omnilibrary", "Refreshed Epic library.", cache.DetailText);
            }
            return;
        }

        var legacyAuthPath = ResolveReadableEpicAuthPath(storeConfiguration.AuthPath);
        if (!string.IsNullOrWhiteSpace(legacyAuthPath) &&
            !legacyAuthPath.Equals(ManagedLegendaryHelper.UserDataPath, StringComparison.OrdinalIgnoreCase))
        {
            var credentials = EnsureEpicCredentials(legacyAuthPath);
            var epicGames = LoadEpicAccountLibraryGames(credentials.AccessToken, launcherInstalledGames);
            var legacyCache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
            legacyCache.AccountName = FirstNonEmpty(credentials.DisplayName, credentials.AccountId, "Epic Account");
            legacyCache.Games = epicGames;
            storeConfiguration.RemoteCatalogSignature = ComputeLibraryCatalogSignature(epicGames);
            legacyCache.LastError = string.Empty;
            legacyCache.StatusText = "Ready";
            legacyCache.DetailText = $"Loaded {epicGames.Count} Epic title{(epicGames.Count == 1 ? string.Empty : "s")} for {legacyCache.AccountName}.";
            legacyCache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            if (!quiet)
            {
                _journal.Append("info", "omnilibrary", "Refreshed legacy Epic library.", legacyCache.DetailText);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(toolPath))
        {
            var launcherPath = ResolveEpicLauncherPath();
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new InvalidOperationException("Epic Games Launcher or legendary was not found. Install Epic Games Launcher, Heroic, or legendary, then refresh OmniLibrary.");
            }

            var launcherCache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
            launcherCache.AccountName = string.Empty;
            launcherCache.Games = launcherInstalledGames.Values
                .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            storeConfiguration.RemoteCatalogSignature = ComputeLibraryCatalogSignature(launcherCache.Games);
            launcherCache.LastError = string.Empty;
            launcherCache.StatusText = "Ready";
            launcherCache.DetailText = launcherInstalledGames.Count > 0
                ? $"Epic Games Launcher detected. Showing {launcherInstalledGames.Count} installed title{(launcherInstalledGames.Count == 1 ? string.Empty : "s")} from the local launcher manifest."
                : "Epic Games Launcher detected. Use Epic Login, paste the code, then refresh to import the full account library.";
            launcherCache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            if (!quiet)
            {
                _journal.Append("info", "unifysteam", "Refreshed Epic launcher fallback.", launcherCache.DetailText);
            }
            return;
        }

    }

    private static void PreserveEpicPreparationState(
        IReadOnlyList<UnifySteamGameCacheEntry>? previousGames,
        IReadOnlyList<UnifySteamGameCacheEntry> currentGames)
    {
        if (previousGames is null || previousGames.Count == 0)
        {
            return;
        }

        var previousById = previousGames
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var game in currentGames)
        {
            if (previousById.TryGetValue(game.Id, out var previous))
            {
                game.PreparationSignature = previous.PreparationSignature;
            }
        }
    }

    private void RefreshGogStore(
        UnifySteamStoreConfiguration storeConfiguration,
        bool lightweight,
        bool quiet)
    {
        var cache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
        var authPath = ResolveReadableGogAuthPath(storeConfiguration.AuthPath);
        if (string.IsNullOrWhiteSpace(authPath))
        {
            throw new InvalidOperationException("GOG auth data was not found. Open the GOG login flow first.");
        }

        var credentials = EnsureGogCredentials(authPath);
        ReconcileGogCachedInstallState(cache);
        var installedGames = LoadGogInstalledGames(cache);
        ApplyGogInstalledState(cache.Games, installedGames);
        var ownedIds = LoadGogOwnedIds(credentials.AccessToken)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var remoteCatalogSignature = ComputeLibraryCatalogSignature(
            new[] { GogCatalogShapeVersion }.Concat(ownedIds));
        var previouslyProcessedIds = (storeConfiguration.RemoteCatalogItemIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentOwnedIdSet = ownedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var membershipFullyProcessed = previouslyProcessedIds.SetEquals(currentOwnedIdSet);
        if (lightweight &&
            !string.IsNullOrWhiteSpace(storeConfiguration.RemoteCatalogSignature) &&
            string.Equals(
                storeConfiguration.RemoteCatalogSignature,
                remoteCatalogSignature,
                StringComparison.OrdinalIgnoreCase) &&
            membershipFullyProcessed)
        {
            RefreshGogInstalledBuildMetadataIfDue(
                storeConfiguration,
                authPath,
                cache.Games,
                force: false);
            cache.LastError = string.Empty;
            cache.StatusText = "Ready";
            if (string.IsNullOrWhiteSpace(cache.DetailText))
            {
                cache.DetailText =
                    $"Loaded {cache.Games.Count} GOG title{(cache.Games.Count == 1 ? string.Empty : "s")}.";
            }
            cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        var previousGames = cache.Games
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        GogLibraryResponse response;
        IReadOnlyCollection<string> processedOwnedIds;
        if (previouslyProcessedIds.Count > 0 && previousGames.Count > 0)
        {
            var addedIds = ownedIds
                .Where(id => !previouslyProcessedIds.Contains(id))
                .ToArray();
            var removedIds = previouslyProcessedIds
                .Where(id => !currentOwnedIdSet.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retainedGames = previousGames.Values
                .Where(game => !removedIds.Contains(game.Id))
                .ToList();
            var productDetails = new List<UnifySteamGameCacheEntry>();
            var resolvedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (removedIds.Count > 0)
            {
                // A removed edition may have been the representative of several
                // same-title products. Rebuild cheap product metadata for the remaining
                // IDs, but keep existing artwork and never redownload the art set.
                AppendGogProductDetails(
                    ownedIds,
                    productDetails,
                    resolvedIds);
                processedOwnedIds = resolvedIds;
            }
            else
            {
                AppendGogProductDetails(
                    addedIds,
                    productDetails,
                    // The batched products response already contains a usable
                    // portrait. Higher quality Steam/SteamGridDB artwork belongs
                    // to the existing asynchronous artwork worker and must never
                    // delay a catalog delta.
                    resolvedIds);
                processedOwnedIds = previouslyProcessedIds
                    .Concat(resolvedIds)
                    .Where(currentOwnedIdSet.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            response = new GogLibraryResponse(
                cache.AccountName,
                MergeGogProductDetails(retainedGames, productDetails),
                processedOwnedIds);
        }
        else
        {
            response = LoadGogLibrary(credentials.AccessToken, ownedIds);
            processedOwnedIds = response.ProcessedOwnedIds;
        }
        ApplyGogInstalledState(response.Games, installedGames);
        cache.AccountName = string.IsNullOrWhiteSpace(response.AccountName)
            ? "GOG Account"
            : response.AccountName;
        cache.Games = response.Games;
        RefreshGogInstalledBuildMetadataIfDue(
            storeConfiguration,
            authPath,
            cache.Games,
            force: !lightweight);
        storeConfiguration.RemoteCatalogSignature = remoteCatalogSignature;
        storeConfiguration.RemoteCatalogItemIds = processedOwnedIds
            .Where(currentOwnedIdSet.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        cache.LastError = string.Empty;
        cache.StatusText = "Ready";
        var pendingMetadataCount = Math.Max(
            0,
            ownedIds.Length - storeConfiguration.RemoteCatalogItemIds.Count);
        cache.DetailText =
            $"Loaded {response.Games.Count} GOG title{(response.Games.Count == 1 ? string.Empty : "s")}." +
            (pendingMetadataCount > 0
                ? $" Metadata for {pendingMetadataCount} product{(pendingMetadataCount == 1 ? string.Empty : "s")} will retry during the next background sync."
                : string.Empty);
        cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
        if (!quiet)
        {
            _journal.Append("info", "unifysteam", "Refreshed GOG library.", cache.DetailText);
        }
    }

    private static void ApplyGogInstalledState(
        IEnumerable<UnifySteamGameCacheEntry> games,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedGames)
    {
        var gamesArray = games
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .ToArray();
        var installedByTitle = installedGames.Values
            .Where(game => !string.IsNullOrWhiteSpace(game.Title))
            .GroupBy(
                game => NormalizeGameTitleKey(game.Title),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Take(2).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var game in gamesArray)
        {
            var installed = installedGames.GetValueOrDefault(game.Id);
            if (installed is null)
            {
                var normalizedTitle = NormalizeGameTitleKey(game.Title);
                if (!string.IsNullOrWhiteSpace(normalizedTitle) &&
                    installedByTitle.TryGetValue(normalizedTitle, out var titleMatches) &&
                    titleMatches.Length == 1)
                {
                    installed = titleMatches[0];
                }
            }

            if (installed is null)
            {
                continue;
            }

            game.Installed = true;
            game.InstallPath = installed.InstallPath;
            game.ExecutablePath = installed.ExecutablePath;
            game.Version = installed.Version;
        }
    }

    private static void RefreshGogInstalledBuildMetadataIfDue(
        UnifySteamStoreConfiguration storeConfiguration,
        string authPath,
        IEnumerable<UnifySteamGameCacheEntry> games,
        bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force &&
            storeConfiguration.InstalledMetadataCheckedAtUtc.HasValue &&
            now - storeConfiguration.InstalledMetadataCheckedAtUtc.Value <
            TimeSpan.FromMinutes(30))
        {
            return;
        }

        var installedGames = games
            .Where(game =>
                game is not null &&
                game.Installed &&
                !string.IsNullOrWhiteSpace(game.Id))
            .OrderBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (installedGames.Length == 0)
        {
            storeConfiguration.InstalledMetadataCheckedAtUtc = now;
            return;
        }

        var gogdl = ManagedGogDlHelper.ResolveExistingToolPath(
            storeConfiguration.ToolPath);
        if (string.IsNullOrWhiteSpace(gogdl) ||
            string.IsNullOrWhiteSpace(authPath) ||
            !File.Exists(authPath))
        {
            return;
        }

        const int maximumChecksPerPass = 4;
        var cursor = Math.Clamp(
            storeConfiguration.InstalledMetadataCursor,
            0,
            Math.Max(0, installedGames.Length - 1));
        var selectedGames = installedGames
            .Skip(cursor)
            .Concat(installedGames.Take(cursor))
            .Take(Math.Min(maximumChecksPerPass, installedGames.Length))
            .ToArray();
        foreach (var game in selectedGames)
        {
            try
            {
                var arguments = new List<string>
                {
                    "--auth-config-path",
                    authPath,
                    "info",
                    game.Id,
                    "--platform",
                    "windows",
                };
                arguments.Add(
                    storeConfiguration.IncludeGogDlc
                        ? "--with-dlcs"
                        : "--skip-dlcs");
                var result = RunTool(gogdl, arguments.ToArray());
                if (result.ExitCode != 0 ||
                    string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(result.StandardOutput);
                var buildId = GetJsonString(document.RootElement, "buildId");
                if (!string.IsNullOrWhiteSpace(buildId))
                {
                    game.LatestVersion = buildId;
                }
            }
            catch
            {
                // Update discovery is optional metadata. It must never turn a
                // healthy cached library into an error or delay its next delta.
            }
        }

        var nextCursor = installedGames.Length == 0
            ? 0
            : (cursor + selectedGames.Length) % installedGames.Length;
        storeConfiguration.InstalledMetadataCursor = nextCursor;
        storeConfiguration.InstalledMetadataCheckedAtUtc =
            nextCursor == 0
                ? now
                : now - TimeSpan.FromMinutes(25);
    }

    private static void ReconcileGogCachedInstallState(UnifySteamLibraryCache cache)
    {
        foreach (var game in cache.Games.Where(game => game?.Installed == true))
        {
            var executableExists =
                !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                File.Exists(game.ExecutablePath);
            var manifestExists =
                !string.IsNullOrWhiteSpace(game.InstallPath) &&
                File.Exists(Path.Combine(game.InstallPath, $"goggame-{game.Id}.info"));
            if (executableExists || manifestExists)
            {
                continue;
            }

            game.Installed = false;
            game.InstallPath = string.Empty;
            game.ExecutablePath = string.Empty;
            game.Version = string.Empty;
        }
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> LoadGogInstalledGames(
        UnifySteamLibraryCache cache)
    {
        var installed = cache.Games
            .Where(game =>
                game is not null &&
                game.Installed &&
                !string.IsNullOrWhiteSpace(game.Id) &&
                ((!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                  File.Exists(game.ExecutablePath)) ||
                 (!string.IsNullOrWhiteSpace(game.InstallPath) &&
                  File.Exists(Path.Combine(game.InstallPath, $"goggame-{game.Id}.info")))))
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[]
                 {
                     RegistryHive.CurrentUser,
                     RegistryHive.LocalMachine,
                 })
        {
            foreach (var view in new[]
                     {
                         RegistryView.Registry64,
                         RegistryView.Registry32,
                     })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var root = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                    if (root is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        using var gameKey = root.OpenSubKey(subKeyName);
                        if (gameKey is null)
                        {
                            continue;
                        }

                        var gameId = FirstNonEmpty(
                            gameKey.GetValue("gameID")?.ToString(),
                            gameKey.GetValue("gameId")?.ToString(),
                            gameKey.GetValue("productID")?.ToString(),
                            gameKey.GetValue("productId")?.ToString(),
                            subKeyName);
                        var installPath = NormalizePath(FirstNonEmpty(
                            gameKey.GetValue("path")?.ToString(),
                            gameKey.GetValue("PATH")?.ToString(),
                            gameKey.GetValue("InstallLocation")?.ToString()));
                        if (string.IsNullOrWhiteSpace(gameId) ||
                            string.IsNullOrWhiteSpace(installPath) ||
                            !Directory.Exists(installPath))
                        {
                            continue;
                        }

                        var executablePath = ResolveGogManifestExecutable(
                            installPath,
                            gameId);
                        if (string.IsNullOrWhiteSpace(executablePath))
                        {
                            executablePath = ResolveExecutablePath(
                                installPath,
                                FirstNonEmpty(
                                    gameKey.GetValue("exe")?.ToString(),
                                    gameKey.GetValue("gameExe")?.ToString(),
                                    gameKey.GetValue("launchCommand")?.ToString()));
                        }

                        var manifestExists = File.Exists(
                            Path.Combine(installPath, $"goggame-{gameId}.info"));
                        if (!manifestExists &&
                            (string.IsNullOrWhiteSpace(executablePath) ||
                             !File.Exists(executablePath)))
                        {
                            continue;
                        }

                        installed[gameId] = new UnifySteamGameCacheEntry
                        {
                            Id = gameId,
                            Title = FirstNonEmpty(
                                gameKey.GetValue("gameName")?.ToString(),
                                gameKey.GetValue("GAMENAME")?.ToString(),
                                gameKey.GetValue("DisplayName")?.ToString(),
                                gameId),
                            Installed = true,
                            InstallPath = installPath,
                            ExecutablePath = executablePath,
                            Version = FirstNonEmpty(
                                ReadGogManifestBuildId(installPath, gameId),
                                gameKey.GetValue("buildId")?.ToString(),
                                gameKey.GetValue("version")?.ToString()),
                        };
                    }
                }
                catch
                {
                    // GOG's registry data is optional. Managed gogdl installs
                    // remain authoritative when a view is unavailable.
                }
            }
        }

        return installed;
    }

    private static string ReadGogManifestBuildId(
        string installPath,
        string gameId)
    {
        try
        {
            foreach (var path in new[]
                     {
                         ManagedGogDlHelper.GetInstalledManifestPath(gameId),
                         Path.Combine(installPath, $"goggame-{gameId}.info"),
                     })
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var buildId = FirstNonEmpty(
                    GetJsonString(document.RootElement, "buildId"),
                    GetJsonString(document.RootElement, "build_id"));
                if (!string.IsNullOrWhiteSpace(buildId))
                {
                    return buildId;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ResolveGogManifestExecutable(string installPath, string gameId)
    {
        try
        {
            var manifestPath = Path.Combine(installPath, $"goggame-{gameId}.info");
            if (!File.Exists(manifestPath))
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("playTasks", out var tasks) ||
                tasks.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            JsonElement? fallbackTask = null;
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object ||
                    !task.TryGetProperty("path", out var pathNode) ||
                    string.IsNullOrWhiteSpace(pathNode.GetString()))
                {
                    continue;
                }

                fallbackTask ??= task;
                if (task.TryGetProperty("isPrimary", out var primaryNode) &&
                    primaryNode.ValueKind == JsonValueKind.True)
                {
                    return ResolveExecutablePath(installPath, pathNode.GetString()!);
                }
            }

            return fallbackTask is { } fallback &&
                   fallback.TryGetProperty("path", out var fallbackPath)
                ? ResolveExecutablePath(installPath, fallbackPath.GetString() ?? string.Empty)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RefreshXboxStore(
        UnifySteamStoreConfiguration storeConfiguration,
        bool lightweight,
        bool quiet)
    {
        var cache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
        if (!storeConfiguration.IncludeXboxPcGamePass &&
            !storeConfiguration.IncludeXboxCloudGaming)
        {
            cache.AccountName = "Xbox account";
            cache.Games = [];
            cache.LastError = string.Empty;
            cache.StatusText = "Ready";
            cache.DetailText = "Both Xbox library sources are disabled.";
            cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            storeConfiguration.RemoteCatalogSignature = string.Empty;
            return;
        }

        if (!IsXboxAppInstalled())
        {
            throw new InvalidOperationException("The Xbox app is not installed. Install it from Microsoft Store, sign in there, then refresh OmniLibrary.");
        }

        if (!IsXboxAppSignedIn())
        {
            throw new InvalidOperationException("Sign in inside the official Xbox app before syncing the Xbox library.");
        }

        var previousSteamAppIds = (storeConfiguration.Cache?.Games ?? [])
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id) && game.SteamAppId != 0)
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().SteamAppId, StringComparer.OrdinalIgnoreCase);

        var (language, market) = ResolveXboxCatalogLocale();
        var pcProductIds = storeConfiguration.IncludeXboxPcGamePass
            ? LoadXboxPcGamePassProductIds(language, market)
            : [];
        var cloudProductIds = storeConfiguration.IncludeXboxCloudGaming
            ? LoadXboxCloudProductIds(language, market)
            : [];
        var pcProductIdSet = pcProductIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cloudProductIdSet = cloudProductIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var productIds = pcProductIds
            .Concat(cloudProductIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var remoteCatalogSignature = ComputeLibraryCatalogSignature(
            new[] { XboxCatalogShapeVersion }
                .Concat(pcProductIds.Select(productId => $"pc:{productId}"))
                .Concat(cloudProductIds.Select(productId => $"cloud:{productId}")));
        var previousRemoteCatalogSignature = string.IsNullOrWhiteSpace(storeConfiguration.RemoteCatalogSignature)
            ? string.Empty
            : storeConfiguration.RemoteCatalogSignature;
        if (lightweight &&
            !string.IsNullOrWhiteSpace(previousRemoteCatalogSignature) &&
            string.Equals(
                previousRemoteCatalogSignature,
                remoteCatalogSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            // The two public membership lists are sufficient for the frequent probe.
            // Avoid hundreds of product-detail records when neither source changed.
            storeConfiguration.RemoteCatalogSignature = remoteCatalogSignature;
            cache.LastError = string.Empty;
            cache.StatusText = "Ready";
            if (string.IsNullOrWhiteSpace(cache.DetailText))
            {
                cache.DetailText = $"Loaded {cache.Games.Count} Xbox title{(cache.Games.Count == 1 ? string.Empty : "s")}.";
            }
            return;
        }

        var installed = LoadXboxInstalledGames(storeConfiguration.InstallPath, forceRefresh: true);
        var games = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var catalogCandidates = new List<XboxCatalogCandidate>();

        const int batchSize = 30;
        for (var offset = 0; offset < productIds.Length; offset += batchSize)
        {
            var batch = productIds.Skip(offset).Take(batchSize).ToArray();
            var productsUri =
                $"https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={Uri.EscapeDataString(string.Join(',', batch))}" +
                $"&market={Uri.EscapeDataString(market)}&languages={Uri.EscapeDataString(language)}";
            using var productsDocument = LoadRequiredJson(productsUri, "Xbox product details could not be loaded.");
            if (!productsDocument.RootElement.TryGetProperty("Products", out var products) ||
                products.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var product in products.EnumerateArray())
            {
                var productId = GetJsonString(product, "ProductId");
                if (string.IsNullOrWhiteSpace(productId) || !IsSafeXboxProductId(productId))
                {
                    continue;
                }

                var localized = product.TryGetProperty("LocalizedProperties", out var localizedProperties) &&
                                localizedProperties.ValueKind == JsonValueKind.Array
                    ? localizedProperties.EnumerateArray().FirstOrDefault()
                    : default;
                var title = localized.ValueKind == JsonValueKind.Object
                    ? GetJsonString(localized, "ProductTitle")
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = productId;
                }

                TryResolveXboxInstalledGame(
                    new UnifySteamGameCacheEntry
                    {
                        Id = productId,
                        Title = title,
                    },
                    installed,
                    out var installedGame);
                previousSteamAppIds.TryGetValue(productId, out var steamAppId);
                var cloudPlayable =
                    cloudProductIdSet.Contains(productId) &&
                    !pcProductIdSet.Contains(productId);
                if (cloudPlayable && !IsXboxConsoleCatalogProduct(product))
                {
                    // The public cloud SIGL currently also contains EA Play PC
                    // products. They often have the same title as the actual Xbox
                    // stream and produced duplicate, non-launchable Cloud entries.
                    continue;
                }

                var game = new UnifySteamGameCacheEntry
                {
                    Id = productId,
                    Title = title,
                    Installed = installedGame?.Installed == true,
                    // Prefer the downloadable PC entry when a title belongs to both
                    // catalogs. The cloud tab owns only cloud-only titles, avoiding
                    // duplicate Steam shortcuts with conflicting launch actions.
                    CloudPlayable = cloudPlayable,
                    InstallPath = installedGame?.InstallPath ?? string.Empty,
                    ExecutablePath = installedGame?.ExecutablePath ?? string.Empty,
                    Version = installedGame?.Version ?? string.Empty,
                    ProviderGameId = installedGame?.Id ?? string.Empty,
                    StoreTitleId = FirstNonEmpty(
                        ResolveXboxTitleId(product),
                        installedGame?.StoreTitleId),
                    ImageUrl = localized.ValueKind == JsonValueKind.Object
                        ? ResolveXboxPortraitUrl(localized)
                        : string.Empty,
                    HeroImageUrl = localized.ValueKind == JsonValueKind.Object
                        ? ResolveXboxHeroUrl(localized)
                        : string.Empty,
                    SteamAppId = steamAppId,
                };
                catalogCandidates.Add(new XboxCatalogCandidate(
                    game,
                    ScoreXboxCatalogLocalization(localized, language),
                    ResolveXboxMaximumPackageSize(product)));
            }
        }

        foreach (var candidate in catalogCandidates.Where(candidate => !candidate.Game.CloudPlayable))
        {
            games[candidate.Game.Id] = candidate.Game;
        }

        foreach (var candidate in catalogCandidates
                     .Where(candidate => candidate.Game.CloudPlayable)
                     .GroupBy(
                         candidate => NormalizeGameTitleKey(candidate.Game.Title),
                         StringComparer.OrdinalIgnoreCase)
                     .Select(group => group
                         .OrderByDescending(candidate => candidate.LocalizationScore)
                         .ThenByDescending(candidate => candidate.MaximumPackageSize)
                         .ThenBy(candidate => candidate.Game.Id, StringComparer.OrdinalIgnoreCase)
                         .First()))
        {
            // Regional and PC/console product variants can share the exact
            // display title. Keep one localized, console-launchable product so
            // the native Cloud tab never repeats the same visible game.
            games[candidate.Game.Id] = candidate.Game;
        }

        var selectedProductIds = games.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (productId, installedGame) in installed)
        {
            if (!installedGame.Installed ||
                !selectedProductIds.Contains(productId))
            {
                continue;
            }

            if (!games.TryGetValue(productId, out var catalogGame))
            {
                installedGame.CloudPlayable =
                    cloudProductIdSet.Contains(productId) &&
                    !pcProductIdSet.Contains(productId);
                games[productId] = installedGame;
                continue;
            }

            catalogGame.Installed = true;
            catalogGame.InstallPath = installedGame.InstallPath;
            catalogGame.ExecutablePath = installedGame.ExecutablePath;
            catalogGame.Version = installedGame.Version;
            if (catalogGame.SteamAppId == 0 && previousSteamAppIds.TryGetValue(productId, out var steamAppId))
            {
                catalogGame.SteamAppId = steamAppId;
            }
        }

        cache.AccountName = "Xbox account";
        cache.Games = games.Values
            .OrderByDescending(game => game.Installed)
            .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        storeConfiguration.RemoteCatalogSignature = remoteCatalogSignature;
        cache.LastError = string.Empty;
        cache.StatusText = "Ready";
        cache.DetailText =
            $"Loaded {cache.Games.Count} Xbox title{(cache.Games.Count == 1 ? string.Empty : "s")} " +
            $"({cache.Games.Count(game => !game.CloudPlayable)} PC Game Pass, " +
            $"{cache.Games.Count(game => game.CloudPlayable)} Xbox Cloud); " +
            $"{cache.Games.Count(game => game.Installed)} installed locally.";
        cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
        if (!quiet)
        {
            _journal.Append("info", "omnilibrary", "Refreshed Xbox library.", cache.DetailText);
        }
    }

    private static string[] LoadXboxPcGamePassProductIds(string language, string market)
    {
        return LoadXboxSiglProductIds(
            XboxPcGamePassCatalogId,
            language,
            market,
            "The PC Game Pass catalog could not be loaded.");
    }

    private static string[] LoadXboxSiglProductIds(
        string catalogId,
        string language,
        string market,
        string failureMessage)
    {
        var catalogUri =
            $"https://catalog.gamepass.com/sigls/v2?id={Uri.EscapeDataString(catalogId)}" +
            $"&language={Uri.EscapeDataString(language)}&market={Uri.EscapeDataString(market)}";
        using var catalogDocument = LoadRequiredJson(catalogUri, failureMessage);
        if (catalogDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{failureMessage} The response format was unexpected.");
        }

        return catalogDocument.RootElement
            .EnumerateArray()
            .Select(item => GetJsonString(item, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id) && IsSafeXboxProductId(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] LoadXboxCloudProductIds(string language, string market)
    {
        try
        {
            var productIds = LoadXboxSiglProductIds(
                XboxCloudGamingCatalogId,
                language,
                market,
                "The Xbox Cloud Gaming catalog could not be loaded.");
            if (productIds.Length > 0)
            {
                return productIds;
            }
        }
        catch
        {
            // Microsoft occasionally replaces curated SIGLS identifiers. The public
            // Xbox play page remains a robust discovery fallback, but is avoided on
            // the normal five-minute path because it is much larger.
        }

        var locale = NormalizeXboxWebLocale(language);
        var requestUri = $"https://www.xbox.com/{Uri.EscapeDataString(locale)}/play";
        using var response = HttpClient.GetAsync(requestUri).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The Xbox Cloud Gaming catalog could not be loaded. HTTP {(int)response.StatusCode}.");
        }

        var page = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ExtractXboxCloudProductIds(page);
    }

    internal static string[] ExtractXboxCloudProductIds(string page)
    {
        var catalogMatch = Regex.Match(
            page ?? string.Empty,
            $"\"v3_[^\"]+{Regex.Escape(XboxCloudCatalogMarker)}\"\\s*:\\s*\\{{.*?\"products\"\\s*:\\s*\\[(?<products>.*?)\\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!catalogMatch.Success)
        {
            throw new InvalidOperationException("The Xbox Cloud Gaming catalog returned an unexpected response.");
        }

        return Regex.Matches(
                catalogMatch.Groups["products"].Value,
                "\"(?<id>[A-Z0-9]{6,32})\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["id"].Value)
            .Where(IsSafeXboxProductId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeXboxWebLocale(string? language)
    {
        var locale = (language ?? string.Empty).Trim();
        return Regex.IsMatch(
            locale,
            "^[a-z]{2}-[a-z]{2}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? locale.ToLowerInvariant()
            : "en-us";
    }

    private static JsonDocument LoadRequiredJson(string requestUri, string failureMessage)
    {
        using var response = HttpClient.GetAsync(requestUri).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{failureMessage} HTTP {(int)response.StatusCode}.");
        }

        using var stream = response.Content.ReadAsStream();
        return JsonDocument.Parse(stream);
    }

    private static (string Language, string Market) ResolveXboxCatalogLocale()
    {
        var culture = CultureInfo.CurrentUICulture;
        var language = string.IsNullOrWhiteSpace(culture.Name) ? "en-US" : culture.Name;
        try
        {
            return (language.ToLowerInvariant(), new RegionInfo(culture.Name).TwoLetterISORegionName.ToUpperInvariant());
        }
        catch
        {
            return ("en-us", "US");
        }
    }

    private static string ResolveXboxPortraitUrl(JsonElement localizedProduct)
    {
        if (!localizedProduct.TryGetProperty("Images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var best = images
            .EnumerateArray()
            .Select(image => new
            {
                Uri = GetJsonString(image, "Uri"),
                Purpose = GetJsonString(image, "ImagePurpose"),
                Width = GetJsonInt(image, "Width"),
                Height = GetJsonInt(image, "Height"),
            })
            .Where(image => !string.IsNullOrWhiteSpace(image.Uri))
            .OrderByDescending(image => image.Purpose.Equals("Poster", StringComparison.OrdinalIgnoreCase) ? 100 :
                image.Purpose.Equals("BrandedKeyArt", StringComparison.OrdinalIgnoreCase) ? 90 :
                image.Purpose.Equals("BoxArt", StringComparison.OrdinalIgnoreCase) ? 80 : 0)
            .ThenByDescending(image => image.Height > image.Width ? 1 : 0)
            .ThenByDescending(image => image.Width * image.Height)
            .FirstOrDefault();
        return best is null ? string.Empty : NormalizeImageUrl(best.Uri);
    }

    private static string ResolveXboxHeroUrl(JsonElement localizedProduct)
    {
        if (!localizedProduct.TryGetProperty("Images", out var images) ||
            images.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var best = images
            .EnumerateArray()
            .Select(image => new
            {
                Uri = GetJsonString(image, "Uri"),
                Purpose = GetJsonString(image, "ImagePurpose"),
                Width = GetJsonInt(image, "Width"),
                Height = GetJsonInt(image, "Height"),
            })
            .Where(image =>
                !string.IsNullOrWhiteSpace(image.Uri) &&
                image.Width > image.Height)
            .OrderByDescending(image => image.Purpose.Equals("TitledHeroArt", StringComparison.OrdinalIgnoreCase) ? 100 :
                image.Purpose.Equals("SuperHeroArt", StringComparison.OrdinalIgnoreCase) ? 90 :
                image.Purpose.Equals("FeaturePromotionalWideArt", StringComparison.OrdinalIgnoreCase) ? 80 :
                image.Purpose.Equals("Screenshot", StringComparison.OrdinalIgnoreCase) ? 10 : 0)
            .ThenByDescending(image => image.Width * image.Height)
            .FirstOrDefault();
        return best is null ? string.Empty : NormalizeImageUrl(best.Uri);
    }

    internal static bool IsXboxConsoleCatalogProduct(JsonElement product)
    {
        if (product.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (product.TryGetProperty("Properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            if (HasXboxCatalogValues(properties, "XboxConsoleGenCompatible") ||
                HasXboxCatalogValues(properties, "XboxConsoleGenOptimized"))
            {
                return true;
            }
        }

        if (!product.TryGetProperty("DisplaySkuAvailabilities", out var availabilities) ||
            availabilities.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var availability in availabilities.EnumerateArray())
        {
            if (!availability.TryGetProperty("Sku", out var sku) ||
                !sku.TryGetProperty("Properties", out var skuProperties) ||
                !skuProperties.TryGetProperty("Packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var package in packages.EnumerateArray())
            {
                if (!package.TryGetProperty("PlatformDependencies", out var dependencies) ||
                    dependencies.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (dependencies.EnumerateArray().Any(dependency =>
                        GetJsonString(dependency, "PlatformName")
                            .Equals("Windows.Xbox", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasXboxCatalogValues(JsonElement properties, string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out var values))
        {
            return false;
        }

        return values.ValueKind switch
        {
            JsonValueKind.Array => values.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(values.GetString()),
            _ => false,
        };
    }

    private static int ScoreXboxCatalogLocalization(JsonElement localizedProduct, string language)
    {
        var localizedLanguage = GetJsonString(localizedProduct, "Language");
        if (localizedLanguage.Equals(language, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return localizedLanguage.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }

    private static long ResolveXboxMaximumPackageSize(JsonElement product)
    {
        if (!product.TryGetProperty("DisplaySkuAvailabilities", out var availabilities) ||
            availabilities.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        long maximum = 0;
        foreach (var availability in availabilities.EnumerateArray())
        {
            if (!availability.TryGetProperty("Sku", out var sku) ||
                !sku.TryGetProperty("Properties", out var properties) ||
                !properties.TryGetProperty("Packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var package in packages.EnumerateArray())
            {
                if (package.TryGetProperty("MaxDownloadSizeInBytes", out var size) &&
                    size.TryGetInt64(out var bytes))
                {
                    maximum = Math.Max(maximum, bytes);
                }
            }
        }

        return maximum;
    }

    internal static Dictionary<string, UnifySteamGameCacheEntry> LoadXboxInstalledGames(
        string? preferredInstallPath = null,
        bool forceRefresh = false)
    {
        var cacheKey = string.IsNullOrWhiteSpace(preferredInstallPath)
            ? string.Empty
            : preferredInstallPath.Trim();
        lock (XboxInstalledCacheGate)
        {
            if (!forceRefresh &&
                XboxInstalledCache.TryGetValue(cacheKey, out var cached) &&
                DateTimeOffset.UtcNow - cached.CreatedAtUtc < XboxInstalledCacheLifetime)
            {
                return CloneXboxInstalledGames(cached.Games);
            }
        }

        var games = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var registeredPackageNames = LoadRegisteredXboxPackageNames();
        var xboxRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                var xboxRoot = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(xboxRoot))
                {
                    continue;
                }

                xboxRoots.Add(xboxRoot);
            }
            catch
            {
                // Drives can disappear or deny enumeration while the catalog refresh is running.
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredInstallPath))
        {
            try
            {
                var normalizedPreferredPath = Path.GetFullPath(preferredInstallPath);
                if (Directory.Exists(normalizedPreferredPath))
                {
                    xboxRoots.Add(normalizedPreferredPath);
                    var nestedXboxRoot = Path.Combine(normalizedPreferredPath, "XboxGames");
                    if (Directory.Exists(nestedXboxRoot))
                    {
                        xboxRoots.Add(nestedXboxRoot);
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var xboxRoot in xboxRoots)
        {
            try
            {
                foreach (var gameDirectory in Directory.EnumerateDirectories(xboxRoot))
                {
                    var contentDirectory = Path.Combine(gameDirectory, "Content");
                    var configPath = new[]
                        {
                            Path.Combine(contentDirectory, "MicrosoftGame.Config"),
                            Path.Combine(contentDirectory, "MicrosoftGame.config"),
                        }
                        .FirstOrDefault(File.Exists);
                    if (string.IsNullOrWhiteSpace(configPath))
                    {
                        continue;
                    }

                    try
                    {
                        var document = XDocument.Load(configPath, LoadOptions.None);
                        var root = document.Root;
                        var storeId = root?.Elements().FirstOrDefault(element =>
                            element.Name.LocalName.Equals("StoreId", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;
                        if (!IsSafeXboxProductId(storeId))
                        {
                            continue;
                        }

                        var shellVisuals = root?.Elements().FirstOrDefault(element =>
                            element.Name.LocalName.Equals("ShellVisuals", StringComparison.OrdinalIgnoreCase));
                        var executable = root?.Descendants().FirstOrDefault(element =>
                            element.Name.LocalName.Equals("Executable", StringComparison.OrdinalIgnoreCase));
                        var identity = root?.Elements().FirstOrDefault(element =>
                            element.Name.LocalName.Equals("Identity", StringComparison.OrdinalIgnoreCase));
                        var identityName = identity?.Attribute("Name")?.Value?.Trim() ?? string.Empty;
                        var identityVersion = identity?.Attribute("Version")?.Value?.Trim() ?? string.Empty;
                        var manifestTitleId = root?.Elements().FirstOrDefault(element =>
                            element.Name.LocalName.Equals(
                                "TitleId",
                                StringComparison.OrdinalIgnoreCase))?.Value;
                        TryNormalizeXboxManifestTitleId(
                            manifestTitleId,
                            out var storeTitleId);
                        var executableName = executable?.Attribute("Name")?.Value?.Trim() ?? string.Empty;
                        var executablePath = string.IsNullOrWhiteSpace(executableName)
                            ? string.Empty
                            : Path.Combine(contentDirectory, executableName);
                        var executableReady = IsReadyXboxExecutable(
                            executablePath,
                            identityName,
                            identityVersion,
                            registeredPackageNames);
                        var title = ResolveXboxManifestDisplayName(
                            executable?.Attribute("OverrideDisplayName")?.Value,
                            shellVisuals?.Attribute("DefaultDisplayName")?.Value,
                            Path.GetFileName(gameDirectory),
                            storeId);
                        var candidate = new UnifySteamGameCacheEntry
                        {
                            Id = storeId,
                            Title = title,
                            Installed = executableReady,
                            InstallPath = contentDirectory,
                            ExecutablePath = executableReady ? executablePath : string.Empty,
                            Version = identityVersion,
                            StoreTitleId = storeTitleId,
                        };
                        if (!games.TryGetValue(storeId, out var existing) ||
                            (!existing.Installed && candidate.Installed) ||
                            (existing.Installed == candidate.Installed &&
                             CompareXboxVersions(
                                 candidate.Version,
                                 existing.Version) > 0))
                        {
                            games[storeId] = candidate;
                        }
                    }
                    catch
                    {
                        // A single malformed or locked game config must not hide the rest of the Xbox library.
                    }
                }
            }
            catch
            {
                // Xbox library roots can disappear or deny enumeration while refresh is running.
            }
        }

        lock (XboxInstalledCacheGate)
        {
            XboxInstalledCache[cacheKey] = new XboxInstalledCacheState(
                DateTimeOffset.UtcNow,
                CloneXboxInstalledGames(games));
        }

        return games;
    }

    internal static bool TryResolveXboxInstalledGame(
        UnifySteamGameCacheEntry catalogGame,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedGames,
        out UnifySteamGameCacheEntry installedGame)
    {
        installedGame = default!;
        if (catalogGame is null ||
            string.IsNullOrWhiteSpace(catalogGame.Id))
        {
            return false;
        }

        var relatedProductIds = XboxProductRelationStore
            .GetRelatedProductIds(catalogGame.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateIds = relatedProductIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        candidateIds.Add(catalogGame.Id);
        if (IsSafeXboxProductId(catalogGame.ProviderGameId))
        {
            candidateIds.Add(catalogGame.ProviderGameId);
        }

        foreach (var candidateId in candidateIds)
        {
            if (!installedGames.TryGetValue(candidateId, out var candidate) ||
                !IsReadyXboxInstalledGame(candidate))
            {
                continue;
            }

            if (!string.Equals(
                    candidate.Id,
                    catalogGame.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                !relatedProductIds.Contains(candidate.Id))
            {
                XboxProductRelationStore.Register(catalogGame.Id, candidate.Id);
            }

            installedGame = candidate;
            return true;
        }

        var catalogTitle = NormalizeXboxInstallMatchTitle(catalogGame.Title);
        if (string.IsNullOrWhiteSpace(catalogTitle))
        {
            return false;
        }

        var titleMatches = installedGames.Values
            .Where(IsReadyXboxInstalledGame)
            .Where(candidate => string.Equals(
                NormalizeXboxInstallMatchTitle(candidate.Title),
                catalogTitle,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(2)
            .ToArray();
        if (titleMatches.Length != 1)
        {
            return false;
        }

        installedGame = titleMatches[0];
        XboxProductRelationStore.Register(catalogGame.Id, installedGame.Id);
        return true;
    }

    internal static string ResolveXboxManifestDisplayName(
        string? executableOverrideDisplayName,
        string? shellDefaultDisplayName,
        string? gameDirectoryName,
        string storeId)
    {
        return new[]
            {
                executableOverrideDisplayName,
                shellDefaultDisplayName,
                gameDirectoryName,
                storeId,
            }
            .Select(value => value?.Trim() ?? string.Empty)
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value) &&
                !value.StartsWith(
                    "ms-resource:",
                    StringComparison.OrdinalIgnoreCase)) ??
            storeId;
    }

    internal static string NormalizeXboxInstallMatchTitle(string value)
    {
        var normalized = NormalizeGameTitleKey(value ?? string.Empty);
        string previous;
        do
        {
            previous = normalized;
            normalized = Regex.Replace(
                    normalized,
                    @"\s+(?:standard edition|for windows|windows|pc)$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Trim();
        }
        while (!string.Equals(previous, normalized, StringComparison.Ordinal));

        return normalized;
    }

    private static bool IsReadyXboxInstalledGame(UnifySteamGameCacheEntry game)
    {
        return game is not null &&
               game.Installed &&
               !string.IsNullOrWhiteSpace(game.ExecutablePath);
    }

    private static bool IsReadyXboxExecutable(
        string executablePath,
        string identityName,
        string identityVersion,
        IReadOnlyList<string> registeredPackageNames)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        // Xbox protects many fully installed game executables from direct reads.
        // Their AppX registration is the authoritative readiness boundary: it
        // appears only after Gaming Services has registered the completed
        // package. Requiring it also prevents a partially downloaded PE from
        // being mistaken for a launchable game.
        if (!string.IsNullOrWhiteSpace(identityName))
        {
            if (IsXboxPackageRegistered(
                    identityName,
                    identityVersion,
                    registeredPackageNames))
            {
                return true;
            }

            if (registeredPackageNames.Count > 0)
            {
                return false;
            }
        }

        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 64)
            {
                return false;
            }

            Span<byte> dosHeader = stackalloc byte[64];
            if (stream.Read(dosHeader) != dosHeader.Length ||
                dosHeader[0] != (byte)'M' ||
                dosHeader[1] != (byte)'Z')
            {
                return false;
            }

            var peHeaderOffset = BitConverter.ToInt32(dosHeader[60..64]);
            if (peHeaderOffset < 64 ||
                peHeaderOffset > stream.Length - 4 ||
                peHeaderOffset > 1024 * 1024)
            {
                return false;
            }

            stream.Position = peHeaderOffset;
            Span<byte> peSignature = stackalloc byte[4];
            return stream.Read(peSignature) == peSignature.Length &&
                   peSignature[0] == (byte)'P' &&
                   peSignature[1] == (byte)'E' &&
                   peSignature[2] == 0 &&
                   peSignature[3] == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> LoadRegisteredXboxPackageNames()
    {
        const string packagesKeyPath =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using var packagesKey = Registry.CurrentUser.OpenSubKey(packagesKeyPath);
            return packagesKey?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsXboxPackageRegistered(
        string identityName,
        string identityVersion,
        IReadOnlyList<string> registeredPackageNames)
    {
        var identityPrefix = identityName.Trim() + "_";
        var versionPrefix = string.IsNullOrWhiteSpace(identityVersion)
            ? identityPrefix
            : identityPrefix + identityVersion.Trim() + "_";
        return registeredPackageNames.Any(packageName =>
            packageName.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(identityVersion) &&
             packageName.StartsWith(identityPrefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static int CompareXboxVersions(string left, string right)
    {
        return Version.TryParse(left, out var leftVersion) &&
               Version.TryParse(right, out var rightVersion)
            ? leftVersion.CompareTo(rightVersion)
            : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRefreshXboxInstallState(
        IReadOnlyList<UnifySteamGameCacheEntry> games,
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> downloadStatuses)
    {
        var relevantStatuses = games
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .Select(game => UnifySteamDownloadStatusStore.Get(
                downloadStatuses,
                "xbox-game-pass",
                game.Id))
            .ToArray();
        if (relevantStatuses.Any(download =>
                download.Status.Equals(
                    "uninstall-action-required",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var latestCompletion = relevantStatuses
            .Where(download =>
                download.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            .Select(download => download.UpdatedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (latestCompletion == DateTimeOffset.MinValue)
        {
            return false;
        }

        lock (XboxInstalledCacheGate)
        {
            if (latestCompletion <= XboxLastCompletedDownloadObservedUtc)
            {
                return false;
            }

            XboxLastCompletedDownloadObservedUtc = latestCompletion;
            return true;
        }
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> CloneXboxInstalledGames(
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> games)
    {
        return games.ToDictionary(
            pair => pair.Key,
            pair => new UnifySteamGameCacheEntry
            {
                Id = pair.Value.Id,
                Title = pair.Value.Title,
                Installed = pair.Value.Installed,
                CloudPlayable = pair.Value.CloudPlayable,
                InstallPath = pair.Value.InstallPath,
                ExecutablePath = pair.Value.ExecutablePath,
                Version = pair.Value.Version,
                DeliveryProvider = pair.Value.DeliveryProvider,
                ThirdPartyManagedApp = pair.Value.ThirdPartyManagedApp,
                PartnerLinkType = pair.Value.PartnerLinkType,
                PartnerLinkId = pair.Value.PartnerLinkId,
                ProviderGameId = pair.Value.ProviderGameId,
                StoreTitleId = pair.Value.StoreTitleId,
                StoreNamespace = pair.Value.StoreNamespace,
                RegistryPath = pair.Value.RegistryPath,
                RegistryValueName = pair.Value.RegistryValueName,
                ProcessNames = pair.Value.ProcessNames,
                HasInstallableAsset = pair.Value.HasInstallableAsset,
                RequiresAccountLink = pair.Value.RequiresAccountLink,
                RequiresExternalLauncher = pair.Value.RequiresExternalLauncher,
                RequiresEpicLauncherBridge =
                    pair.Value.RequiresEpicLauncherBridge,
                SupportsCloudSaves = pair.Value.SupportsCloudSaves,
                IsPreloaded = pair.Value.IsPreloaded,
                LatestVersion = pair.Value.LatestVersion,
                PreparationSignature = pair.Value.PreparationSignature,
                ImageUrl = pair.Value.ImageUrl,
                HeroImageUrl = pair.Value.HeroImageUrl,
                SteamAppId = pair.Value.SteamAppId,
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record XboxInstalledCacheState(
        DateTimeOffset CreatedAtUtc,
        Dictionary<string, UnifySteamGameCacheEntry> Games);

    private sealed record XboxCatalogCandidate(
        UnifySteamGameCacheEntry Game,
        int LocalizationScore,
        long MaximumPackageSize);

    private static bool IsXboxAppInstalled()
    {
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.GamingApp_8wekyb3d8bbwe");
        return Directory.Exists(packageRoot);
    }

    private static bool IsXboxAppSignedIn()
    {
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.GamingApp_8wekyb3d8bbwe");
        if (!Directory.Exists(packageRoot))
        {
            return false;
        }

        // Do not read or copy Xbox credentials. The official app's token broker and
        // OneAuth account stores expose enough filesystem state to tell whether the
        // user has completed sign-in while keeping all tokens inside Microsoft's app.
        var accountDirectories = new[]
        {
            Path.Combine(packageRoot, "AC", "Microsoft", "OneAuth", "accounts"),
            Path.Combine(packageRoot, "AC", "TokenBroker", "Cache"),
        };

        return accountDirectories.Any(directory =>
        {
            try
            {
                return Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any();
            }
            catch
            {
                return false;
            }
        });
    }

    private static bool IsSafeXboxProductId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 32 &&
               value.All(char.IsLetterOrDigit);
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> LoadEpicLauncherInstalledGames()
    {
        var installed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var manifestPath = GetEpicLauncherInstalledManifestPath();
        if (!File.Exists(manifestPath))
        {
            return installed;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("InstallationList", out var installationList) ||
                installationList.ValueKind != JsonValueKind.Array)
            {
                return installed;
            }

            foreach (var item in installationList.EnumerateArray())
            {
                var appName = FirstNonEmpty(
                    GetJsonString(item, "AppName"),
                    GetJsonString(item, "MainGameAppName"),
                    GetJsonString(item, "ArtifactId"),
                    GetJsonString(item, "ItemId"));
                if (string.IsNullOrWhiteSpace(appName))
                {
                    continue;
                }

                var installLocation = GetJsonString(item, "InstallLocation");
                var launchExecutable = GetJsonString(item, "LaunchExecutable");
                var executablePath = ResolveExecutablePath(installLocation, launchExecutable);
                installed[appName] = new UnifySteamGameCacheEntry
                {
                    Id = appName,
                    Title = FirstNonEmpty(GetJsonString(item, "DisplayName"), appName),
                    Installed = true,
                    InstallPath = installLocation,
                    ExecutablePath = executablePath,
                };
            }
        }
        catch
        {
        }

        return installed;
    }

    private static string GetEpicLauncherInstalledManifestPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "UnrealEngineLauncher",
            "LauncherInstalled.dat");
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> MergeEpicInstalledGames(
        Dictionary<string, UnifySteamGameCacheEntry> primary,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> fallback)
    {
        foreach (var pair in fallback)
        {
            primary.TryAdd(pair.Key, pair.Value);
        }

        return primary;
    }

    private static List<UnifySteamGameCacheEntry> LoadEpicAccountLibraryGames(
        string accessToken,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedMap)
    {
        var games = new List<UnifySteamGameCacheEntry>();
        var cursor = string.Empty;

        do
        {
            var url =
                "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true&platform=Windows&limit=100";
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", accessToken);
            using var response = HttpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Epic library could not be loaded (HTTP {(int)response.StatusCode}). Sign in again and retry.");
            }

            var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Epic library returned an unexpected response.");
            }

            if (document.RootElement.TryGetProperty("records", out var records) &&
                records.ValueKind == JsonValueKind.Array)
            {
                AppendEpicLibraryRecords(records, installedMap, games);
            }

            cursor = string.Empty;
            if (document.RootElement.TryGetProperty("responseMetadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object)
            {
                cursor = GetJsonString(metadata, "nextCursor");
            }
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return DedupeLibraryGames(games);
    }

    private static void AppendEpicLibraryRecords(
        JsonElement records,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedMap,
        ICollection<UnifySteamGameCacheEntry> target)
    {
        foreach (var record in records.EnumerateArray())
        {
            var recordType = GetJsonString(record, "recordType");
            if (!string.Equals(recordType, "APPLICATION", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var appName = FirstNonEmpty(
                GetJsonString(record, "appName"),
                GetJsonString(record, "artifactId"),
                GetJsonString(record, "catalogItemId"));
            if (string.IsNullOrWhiteSpace(appName))
            {
                continue;
            }

            var title = FirstNonEmpty(
                GetJsonString(record, "sandboxName"),
                GetJsonString(record, "title"),
                appName);
            installedMap.TryGetValue(appName, out var installed);
            target.Add(new UnifySteamGameCacheEntry
            {
                Id = appName,
                Title = title,
                Installed = installed?.Installed == true,
                InstallPath = installed?.InstallPath ?? string.Empty,
                ExecutablePath = installed?.ExecutablePath ?? string.Empty,
                Version = installed?.Version ?? string.Empty,
                StoreNamespace = FirstNonEmpty(
                    GetJsonString(record, "namespace"),
                    GetJsonString(record, "sandboxId")),
                // Artwork is resolved later by the asynchronous Steam-first worker.
                ImageUrl = string.Empty,
            });
        }
    }

    private static EpicStatus LoadEpicStatus(string toolPath)
    {
        var result = RunTool(toolPath, "status", "--json");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstNonEmpty(result.StandardError, result.StandardOutput, "legendary status failed."));
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var accountName = GetJsonString(root, "account");
        return new EpicStatus(
            !string.IsNullOrWhiteSpace(accountName) && !string.Equals(accountName, "<not logged in>", StringComparison.OrdinalIgnoreCase),
            string.Equals(accountName, "<not logged in>", StringComparison.OrdinalIgnoreCase) ? string.Empty : accountName);
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> LoadEpicInstalledGames(string toolPath)
    {
        var result = RunTool(toolPath, "list-installed", "--json");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var installed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var appName = GetJsonString(item, "app_name");
            if (string.IsNullOrWhiteSpace(appName))
            {
                continue;
            }

            installed[appName] = new UnifySteamGameCacheEntry
            {
                Id = appName,
                Title = FirstNonEmpty(GetJsonString(item, "title"), appName),
                Installed =
                    !string.Equals(
                        GetJsonString(item, "is_preloaded"),
                        "true",
                        StringComparison.OrdinalIgnoreCase),
                IsPreloaded =
                    string.Equals(
                        GetJsonString(item, "is_preloaded"),
                        "true",
                        StringComparison.OrdinalIgnoreCase),
                InstallPath = GetJsonString(item, "install_path"),
                ExecutablePath = ResolveInstalledExecutablePath(item),
                Version = GetJsonString(item, "version"),
            };
        }

        return installed;
    }

    private static List<UnifySteamGameCacheEntry> LoadEpicLibraryGames(
        string toolPath,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedMap)
    {
        var result = RunTool(toolPath, "list", "--third-party", "--json");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstNonEmpty(result.StandardError, result.StandardOutput, "legendary list failed."));
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("legendary did not return a valid library list.");
        }

        var games = new List<UnifySteamGameCacheEntry>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var appName = GetJsonString(item, "app_name");
            if (string.IsNullOrWhiteSpace(appName))
            {
                continue;
            }

            var metadata = item.TryGetProperty("metadata", out var metadataNode)
                ? metadataNode
                : default;
            var isDlc = metadata.ValueKind == JsonValueKind.Object &&
                        metadata.TryGetProperty("mainGameItem", out _);
            if (isDlc)
            {
                continue;
            }

            installedMap.TryGetValue(appName, out var installed);
            var thirdPartyManagedApp = GetEpicCustomAttribute(
                metadata,
                "ThirdPartyManagedApp");
            var thirdPartyManagedProvider = GetEpicCustomAttribute(
                metadata,
                "ThirdPartyManagedProvider");
            var partnerLinkType = GetEpicCustomAttribute(
                metadata,
                "partnerLinkType");
            var deliveryProvider = ResolveEpicDeliveryProvider(
                thirdPartyManagedApp,
                thirdPartyManagedProvider,
                partnerLinkType);
            var hasInstallableAsset = HasEpicWindowsAsset(item);
            var latestVersion = GetEpicWindowsBuildVersion(item);
            var entry = new UnifySteamGameCacheEntry
            {
                Id = appName,
                Title = FirstNonEmpty(GetJsonString(item, "app_title"), appName),
                Installed = installed?.Installed == true,
                InstallPath = installed?.InstallPath ?? string.Empty,
                ExecutablePath = installed?.ExecutablePath ?? string.Empty,
                Version = installed?.Version ?? string.Empty,
                DeliveryProvider = deliveryProvider,
                ThirdPartyManagedApp = thirdPartyManagedApp,
                PartnerLinkType = partnerLinkType,
                PartnerLinkId = GetEpicCustomAttribute(metadata, "partnerLinkId"),
                ProviderGameId = GetEpicCustomAttribute(metadata, "GameID"),
                StoreTitleId = GetJsonString(metadata, "id"),
                StoreNamespace = FirstNonEmpty(
                    GetJsonString(metadata, "namespace"),
                    GetJsonString(metadata, "sandboxId")),
                RegistryPath = GetEpicCustomAttribute(metadata, "RegistryPath"),
                RegistryValueName = FirstNonEmpty(
                    GetEpicCustomAttribute(metadata, "RegistryKey"),
                    "Install Dir"),
                ProcessNames = FirstNonEmpty(
                    GetEpicCustomAttribute(metadata, "ProcessNames"),
                    GetEpicCustomAttribute(metadata, "MainWindowProcessName")),
                HasInstallableAsset = hasInstallableAsset,
                RequiresAccountLink =
                    deliveryProvider is "ea-app" or "ubisoft-connect",
                RequiresExternalLauncher = RequiresEpicExternalLauncher(
                    deliveryProvider,
                    hasInstallableAsset),
                RequiresEpicLauncherBridge =
                    EpicCompatibilityCatalog.Get(appName).FakeEpicLauncher,
                SupportsCloudSaves =
                    !string.IsNullOrWhiteSpace(
                        GetEpicCustomAttribute(metadata, "CloudSaveFolder")),
                IsPreloaded = installed?.IsPreloaded == true,
                LatestVersion = latestVersion,
                ImageUrl = ResolveEpicImageUrl(metadata),
            };
            MergeEpicExternalInstallState(entry);
            games.Add(entry);
        }

        return DedupeLibraryGames(games);
    }

    private static string GetEpicCustomAttribute(
        JsonElement metadata,
        string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("customAttributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Object ||
            !attributes.TryGetProperty(propertyName, out var attribute))
        {
            return string.Empty;
        }

        return attribute.ValueKind == JsonValueKind.Object
            ? GetJsonString(attribute, "value")
            : GetJsonString(attributes, propertyName);
    }

    private static bool HasEpicWindowsAsset(JsonElement item)
    {
        return item.ValueKind == JsonValueKind.Object &&
               item.TryGetProperty("asset_infos", out var assetInfos) &&
               assetInfos.ValueKind == JsonValueKind.Object &&
               assetInfos.TryGetProperty("Windows", out var windows) &&
               windows.ValueKind == JsonValueKind.Object &&
               windows.EnumerateObject().Any();
    }

    private static string GetEpicWindowsBuildVersion(JsonElement item)
    {
        return item.ValueKind == JsonValueKind.Object &&
               item.TryGetProperty("asset_infos", out var assetInfos) &&
               assetInfos.ValueKind == JsonValueKind.Object &&
               assetInfos.TryGetProperty("Windows", out var windows)
            ? GetJsonString(windows, "build_version")
            : string.Empty;
    }

    internal static string ResolveEpicDeliveryProvider(
        string? thirdPartyManagedApp,
        string? thirdPartyManagedProvider,
        string? partnerLinkType)
    {
        var combined =
            $"{thirdPartyManagedApp} {thirdPartyManagedProvider} {partnerLinkType}";
        if (combined.Contains("origin", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("ea app", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("electronic arts", StringComparison.OrdinalIgnoreCase))
        {
            return "ea-app";
        }

        if (combined.Contains("ubisoft", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("uplay", StringComparison.OrdinalIgnoreCase))
        {
            return "ubisoft-connect";
        }

        return string.IsNullOrWhiteSpace(combined)
            ? "epic"
            : "external";
    }

    internal static string GetEpicProviderDisplayName(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            "ea-app" => "EA app",
            "ubisoft-connect" => "Ubisoft Connect",
            "external" => "the publisher launcher",
            _ => "Epic Games",
        };
    }

    internal static bool RequiresEpicExternalLauncher(
        string? deliveryProvider,
        bool hasInstallableAsset)
    {
        var normalizedProvider = deliveryProvider?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalizedProvider == "ea-app" ||
               (!hasInstallableAsset && normalizedProvider != "epic");
    }

    internal static bool CanInstallEpicDirectly(
        string? deliveryProvider,
        bool hasInstallableAsset)
    {
        return !RequiresEpicExternalLauncher(
                   deliveryProvider,
                   hasInstallableAsset) &&
               (string.IsNullOrWhiteSpace(deliveryProvider) ||
                hasInstallableAsset);
    }

    internal static bool CanInstallDirectly(
        string? storeId,
        bool cloudPlayable,
        string? deliveryProvider,
        bool hasInstallableAsset)
    {
        if (string.Equals(
                storeId,
                "epic-games",
                StringComparison.OrdinalIgnoreCase))
        {
            return CanInstallEpicDirectly(
                deliveryProvider,
                hasInstallableAsset);
        }

        if (string.Equals(
                storeId,
                "xbox-game-pass",
                StringComparison.OrdinalIgnoreCase))
        {
            return !cloudPlayable;
        }

        return true;
    }

    internal static void NormalizeEpicDeliveryCapabilities(
        UnifySteamGameCacheEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var normalizedProvider =
            game.DeliveryProvider?.Trim().ToLowerInvariant() ?? string.Empty;
        game.RequiresAccountLink =
            normalizedProvider is "ea-app" or "ubisoft-connect";
        game.RequiresExternalLauncher = RequiresEpicExternalLauncher(
            normalizedProvider,
            game.HasInstallableAsset);
    }

    private static void MergeEpicExternalInstallState(
        UnifySteamGameCacheEntry game)
    {
        if (!game.RequiresExternalLauncher ||
            string.IsNullOrWhiteSpace(game.RegistryPath) ||
            !TryReadEpicExternalInstallPath(
                game.RegistryPath,
                game.RegistryValueName,
                out var installPath))
        {
            return;
        }

        game.Installed = true;
        game.InstallPath = installPath;
        game.ExecutablePath = FindEpicExternalExecutable(
            installPath,
            game.ProcessNames);
    }

    internal static bool TryReadEpicExternalInstallPath(
        string registryPath,
        string registryValueName,
        out string installPath)
    {
        installPath = string.Empty;
        var normalizedPath = (registryPath ?? string.Empty)
            .Trim()
            .TrimStart('\\');
        foreach (var prefix in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\",
                     @"HKLM\",
                     @"HKEY_CURRENT_USER\",
                     @"HKCU\",
                 })
        {
            if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = normalizedPath[prefix.Length..];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(normalizedPath);
                    var value = key?.GetValue(
                        string.IsNullOrWhiteSpace(registryValueName)
                            ? null
                            : registryValueName.Trim()) as string;
                    if (string.IsNullOrWhiteSpace(value) ||
                        !Directory.Exists(value.Trim().Trim('"')))
                    {
                        continue;
                    }

                    installPath = NormalizePath(value.Trim().Trim('"'));
                    return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static string FindEpicExternalExecutable(
        string installPath,
        string processNames)
    {
        if (string.IsNullOrWhiteSpace(installPath) ||
            !Directory.Exists(installPath))
        {
            return string.Empty;
        }

        var names = Regex.Split(processNames ?? string.Empty, @"[;,|]")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return string.Empty;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((installPath, 0));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var name in names)
            {
                var candidate = Path.Combine(current.Path, name!);
                if (File.Exists(candidate))
                {
                    return NormalizePath(candidate);
                }
            }

            if (current.Depth >= 3)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current.Path))
                {
                    pending.Push((child, current.Depth + 1));
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string ResolveEpicImageUrl(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("keyImages", out var keyImages) ||
            keyImages.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var bestUrl = string.Empty;
        var bestScore = double.NegativeInfinity;
        foreach (var image in keyImages.EnumerateArray())
        {
            var url = GetJsonString(image, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var score = ScoreEpicImage(image, url);
            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
            }
        }

        return NormalizeImageUrl(bestUrl);
    }

    private static double ScoreEpicImage(JsonElement image, string url)
    {
        var type = GetJsonString(image, "type");
        var width = GetJsonInt(image, "width");
        var height = GetJsonInt(image, "height");
        var areaScore = width > 0 && height > 0 ? Math.Min(width * height / 1000d, 3000d) : 0d;
        var aspect = width > 0 && height > 0 ? height / (double)width : 1.5d;
        var portraitScore = height >= width ? 1500d : -500d;
        var aspectScore = Math.Max(0d, 1000d - Math.Abs(aspect - 1.5d) * 900d);
        var combined = $"{type} {url}";
        var typeScore = type.ToLowerInvariant() switch
        {
            "dieselgameboxtall" => 6000d,
            "offerimagetall" => 5600d,
            "dieselgamebox" => 5200d,
            "vaultopened" => 4400d,
            "vaultclosed" => 4200d,
            "offerimagewide" => 2400d,
            "thumbnail" => 400d,
            "dieselgameboxlogo" => -600d,
            _ => 0d,
        };
        var nameScore = combined.Contains("portrait", StringComparison.OrdinalIgnoreCase) ||
                        combined.Contains("tall", StringComparison.OrdinalIgnoreCase) ||
                        combined.Contains("box", StringComparison.OrdinalIgnoreCase)
            ? 800d
            : 0d;

        return typeScore + portraitScore + aspectScore + areaScore + nameScore;
    }

    private GogCredential EnsureGogCredentials(string authPath)
    {
        var current = LoadGogCredential(authPath)
            ?? throw new InvalidOperationException("GOG auth data could not be read.");

        if (DateTimeOffset.UtcNow < current.ExpiresAtUtc.AddMinutes(-2))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            throw new InvalidOperationException("The saved GOG login has expired. Sign in again to refresh the library.");
        }

        var requestUri = $"{BuildGogTokenBaseUri()}&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(current.RefreshToken)}";
        using var response = HttpClient.GetAsync(requestUri).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The saved GOG login could not be refreshed. Sign in again.");
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        var token = ParseGogToken(document.RootElement);
        SaveGogCredentials(authPath, token);
        return token;
    }

    private EpicCredential EnsureEpicCredentials(string authPath)
    {
        ManagedLegendaryHelper.CredentialGate.Wait();
        try
        {
            var current = LoadEpicCredential(authPath)
                ?? throw new InvalidOperationException("Epic auth data could not be read.");

            if (DateTimeOffset.UtcNow < current.ExpiresAtUtc.AddMinutes(-2))
            {
                return current;
            }

            if (string.IsNullOrWhiteSpace(current.RefreshToken))
            {
                throw new InvalidOperationException("The saved Epic login has expired. Sign in again to refresh the library.");
            }

            var token = ExchangeEpicRefreshToken(current.RefreshToken);
            SaveEpicCredentials(authPath, token);
            return token;
        }
        finally
        {
            ManagedLegendaryHelper.CredentialGate.Release();
        }
    }

    private GogLibraryResponse LoadGogLibrary(
        string accessToken,
        IReadOnlyList<string>? knownOwnedIds = null)
    {
        // 1) Authoritative ownership list; works reliably with the Galaxy bearer token.
        var ownedIds = knownOwnedIds?.ToList() ?? LoadGogOwnedIds(accessToken);

        // 2) Try the account pages for rich data (may return nothing for bearer sessions).
        var games = new List<UnifySteamGameCacheEntry>();
        var fromAccountPages = 0;
        try
        {
            LoadGogAccountPages(accessToken, games);
            fromAccountPages = games.Count;
        }
        catch (Exception exception)
        {
            _journal.Append(
                "warning",
                "unifysteam",
                "GOG account pages could not be loaded; using the products API instead.",
                exception.Message);
        }

        // Safety net: never keep the same product twice, even if GOG exposes
        // the same title under separate product IDs or editions.
        games = DedupeLibraryGames(games);
        fromAccountPages = games.Count;

        // 3) Always enrich with the public products API. Account pages often only expose
        // small cover thumbnails, while product details include higher resolution cards.
        var beforeDetails = games.Count;
        var productDetails = new List<UnifySteamGameCacheEntry>();
        var processedOwnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ownedIds.Count > 0)
        {
            AppendGogProductDetails(
                ownedIds,
                productDetails,
                // Preparing the store only needs usable catalog metadata. The
                // central artwork pipeline upgrades images asynchronously after
                // shortcuts are ready; one GOG v2 request per title here made
                // first-time sign-in scale with the library size.
                processedOwnedIds);
            games = MergeGogProductDetails(games, productDetails);
        }

        _journal.Append(
            "info",
            "unifysteam",
            "GOG library assembled.",
            $"Owned product IDs: {ownedIds.Count}; from account pages: {fromAccountPages}; enriched from products API: {productDetails.Count}; added from products API: {Math.Max(0, games.Count - beforeDetails)}.");

        return new GogLibraryResponse(
            string.Empty,
            DedupeLibraryGames(games),
            processedOwnedIds);
    }

    private List<string> LoadGogOwnedIds(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://embed.gog.com/user/data/games");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The GOG ownership list could not be loaded (HTTP {(int)response.StatusCode}). Sign in again and retry.");
        }

        var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(payload);
        var ids = new List<string>();
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("owned", out var owned) &&
            owned.ValueKind == JsonValueKind.Array)
        {
            foreach (var idNode in owned.EnumerateArray())
            {
                var id = idNode.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private void AppendGogProductDetails(
        IReadOnlyList<string> ids,
        ICollection<UnifySteamGameCacheEntry> target,
        ISet<string> processedIds)
    {
        const int batchSize = 50;
        for (var offset = 0; offset < ids.Count; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            try
            {
                using var response = HttpClient.GetAsync(
                    $"https://api.gog.com/products?ids={string.Join(',', batch)}").GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    _journal.Append(
                        "warning",
                        "unifysteam",
                        "A GOG products API batch failed.",
                        $"HTTP {(int)response.StatusCode} for {batch.Length} products; these titles are skipped for now.");
                    continue;
                }

                var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var id in batch)
                {
                    processedIds.Add(id);
                }

                var entries = new List<UnifySteamGameCacheEntry>();
                foreach (var product in document.RootElement.EnumerateArray())
                {
                    // Skip DLC entries; packs and games stay.
                    var gameType = GetJsonString(product, "game_type");
                    if (string.Equals(gameType, "dlc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var worksOnWindows = !product.TryGetProperty("content_system_compatibility", out var compatibility) ||
                                         compatibility.ValueKind != JsonValueKind.Object ||
                                         !compatibility.TryGetProperty("windows", out var windowsNode) ||
                                         windowsNode.ValueKind != JsonValueKind.False;
                    if (!worksOnWindows)
                    {
                        continue;
                    }

                    var id = GetJsonString(product, "id");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var imageUrl = string.Empty;
                    if (product.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object)
                    {
                        imageUrl = FirstNonEmpty(
                            GetJsonString(images, "productCard2x"),
                            GetJsonString(images, "productCard"),
                            GetJsonString(images, "verticalCover"),
                            GetJsonString(images, "cover"),
                            GetJsonString(images, "boxArtImage"),
                            GetJsonString(images, "featuredVertical"),
                            GetJsonString(images, "background"),
                            GetJsonString(images, "icon"));
                    }

                    entries.Add(new UnifySteamGameCacheEntry
                    {
                        Id = id,
                        Title = FirstNonEmpty(GetJsonString(product, "title"), id),
                        Installed = false,
                        ImageUrl = NormalizeImageUrl(imageUrl),
                    });
                }

                foreach (var entry in entries)
                {
                    target.Add(entry);
                }
            }
            catch (Exception exception)
            {
                _journal.Append(
                    "warning",
                    "unifysteam",
                    "A GOG products API batch could not be processed.",
                    exception.Message);
            }
        }
    }

    private static Dictionary<string, string> LoadSteamGridDbPortraitArtworkBatch(IReadOnlyList<string> titles)
    {
        var uniqueTitles = titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (uniqueTitles.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var artworkByTitle = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(
            uniqueTitles,
            new ParallelOptions { MaxDegreeOfParallelism = 3 },
            title =>
            {
                var artworkUrl = ResolveSteamGridDbPortraitArtworkUrl(title);
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                {
                    artworkByTitle[title] = artworkUrl;
                }
            });

        return artworkByTitle.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveSteamGridDbPortraitArtworkUrl(string title)
    {
        var cacheKey = NormalizeArtworkLookupTitle(title);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return string.Empty;
        }

        if (SteamGridDbPortraitCache.TryGetValue(cacheKey, out var cachedArtworkUrl))
        {
            return cachedArtworkUrl;
        }

        var artworkUrl = string.Empty;
        try
        {
            var gameId = ResolveSteamGridDbGameId(title, cacheKey);
            if (gameId > 0)
            {
                artworkUrl = ResolveSteamGridDbPortraitAssetUrl(gameId);
            }
        }
        catch (Exception)
        {
            artworkUrl = string.Empty;
        }

        artworkUrl = NormalizeImageUrl(artworkUrl);
        SteamGridDbPortraitCache[cacheKey] = artworkUrl;
        return artworkUrl;
    }

    private static int ResolveSteamGridDbGameId(string title, string normalizedTitle)
    {
        using var document = LoadSteamGridDbJson($"search/autocomplete/{Uri.EscapeDataString(title.Trim())}");
        if (document is null ||
            document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var bestGameId = 0;
        var bestScore = int.MinValue;
        foreach (var match in data.EnumerateArray())
        {
            var gameId = GetJsonInt(match, "id");
            var matchName = GetJsonString(match, "name");
            if (gameId <= 0 || string.IsNullOrWhiteSpace(matchName))
            {
                continue;
            }

            var score = ScoreSteamGridDbMatch(normalizedTitle, matchName);
            if (score > bestScore)
            {
                bestScore = score;
                bestGameId = gameId;
            }
        }

        return bestGameId;
    }

    private static string ResolveSteamGridDbPortraitAssetUrl(int gameId)
    {
        var requestPaths = new[]
        {
            $"grids/game/{gameId}?types=static&dimensions=600x900&mimes=image/png,image/jpeg",
            $"grids/game/{gameId}?types=static&mimes=image/png,image/jpeg",
        };

        foreach (var requestPath in requestPaths)
        {
            using var document = LoadSteamGridDbJson(requestPath);
            if (document is null ||
                document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var artworkUrl = SelectSteamGridDbPortraitUrl(data);
            if (!string.IsNullOrWhiteSpace(artworkUrl))
            {
                return artworkUrl;
            }
        }

        return string.Empty;
    }

    private static JsonDocument? LoadSteamGridDbJson(string requestPath)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/{requestPath}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            SteamGridDbArtworkDownloader.BuiltInApiKey);

        using var response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonDocument.Parse(payload);
    }

    private static string SelectSteamGridDbPortraitUrl(JsonElement data)
    {
        var bestUrl = string.Empty;
        var bestScore = int.MinValue;
        foreach (var asset in data.EnumerateArray())
        {
            var url = GetJsonString(asset, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var width = GetJsonInt(asset, "width");
            var height = GetJsonInt(asset, "height");
            var score = 0;
            if (width == 600 && height == 900)
            {
                score += 10000;
            }

            if (height > width)
            {
                score += 5000;
            }

            score += Math.Min(width, 1600) + Math.Min(height, 2400);
            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
            }
        }

        return bestUrl;
    }

    private static int ScoreSteamGridDbMatch(string normalizedTitle, string matchName)
    {
        var normalizedMatch = NormalizeArtworkLookupTitle(matchName);
        if (string.IsNullOrWhiteSpace(normalizedMatch))
        {
            return 0;
        }

        if (string.Equals(normalizedMatch, normalizedTitle, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (normalizedMatch.StartsWith(normalizedTitle, StringComparison.Ordinal) ||
            normalizedTitle.StartsWith(normalizedMatch, StringComparison.Ordinal))
        {
            return 800;
        }

        if (normalizedMatch.Contains(normalizedTitle, StringComparison.Ordinal) ||
            normalizedTitle.Contains(normalizedMatch, StringComparison.Ordinal))
        {
            return 600;
        }

        return 100;
    }

    private static string NormalizeArtworkLookupTitle(string title)
    {
        var normalized = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static List<UnifySteamGameCacheEntry> MergeGogProductDetails(
        IReadOnlyList<UnifySteamGameCacheEntry> games,
        IReadOnlyList<UnifySteamGameCacheEntry> productDetails)
    {
        var detailsById = productDetails
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var merged = new List<UnifySteamGameCacheEntry>(games.Count + productDetails.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            seen.Add(game.Id);
            if (!detailsById.TryGetValue(game.Id, out var details))
            {
                merged.Add(game);
                continue;
            }

            merged.Add(new UnifySteamGameCacheEntry
            {
                Id = game.Id,
                Title = FirstNonEmpty(game.Title, details.Title, game.Id),
                Installed = game.Installed,
                InstallPath = game.InstallPath,
                ExecutablePath = game.ExecutablePath,
                Version = game.Version,
                ImageUrl = FirstNonEmpty(details.ImageUrl, game.ImageUrl),
                HeroImageUrl = game.HeroImageUrl,
                SteamAppId = game.SteamAppId,
            });
        }

        foreach (var details in productDetails)
        {
            if (seen.Add(details.Id))
            {
                merged.Add(details);
            }
        }

        return DedupeLibraryGames(merged);
    }

    private void LoadGogAccountPages(string accessToken, List<UnifySteamGameCacheEntry> games)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://embed.gog.com/account/getFilteredProducts?mediaType=1&page=1");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GOG library could not be loaded (HTTP {(int)response.StatusCode}). Sign in again and retry.");
        }

        var firstPayload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        JsonDocument firstDocument;
        try
        {
            firstDocument = JsonDocument.Parse(firstPayload);
        }
        catch (JsonException)
        {
            var preview = firstPayload.Length > 160 ? firstPayload[..160] : firstPayload;
            throw new InvalidOperationException($"GOG returned an unexpected (non-JSON) response. It usually means the session is not valid - sign in again. Response started with: {preview}");
        }

        using var _ = firstDocument;
        var root = firstDocument.RootElement;
        var totalPages = root.TryGetProperty("totalPages", out var totalPagesNode) && totalPagesNode.TryGetInt32(out var parsedPages)
            ? Math.Max(parsedPages, 1)
            : 1;
        var totalProducts = root.TryGetProperty("totalProducts", out var totalProductsNode) && totalProductsNode.TryGetInt32(out var parsedProducts)
            ? parsedProducts
            : -1;
        var moviesCount = root.TryGetProperty("moviesCount", out var moviesCountNode) && moviesCountNode.TryGetInt32(out var parsedMovies)
            ? parsedMovies
            : -1;
        var pageProductCount = root.TryGetProperty("products", out var productsNode) && productsNode.ValueKind == JsonValueKind.Array
            ? productsNode.GetArrayLength()
            : -1;

        _journal.Append(
            "info",
            "unifysteam",
            "GOG library response received.",
            $"Page 1: {pageProductCount} products, totalPages={totalPages}, totalProducts={totalProducts}, moviesCount={moviesCount}.");

        AppendGogGames(root, games);

        for (var page = 2; page <= totalPages; page += 1)
        {
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, $"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}");
            pageRequest.Headers.Authorization = new("Bearer", accessToken);
            using var pageResponse = HttpClient.Send(pageRequest);
            if (!pageResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GOG library page {page} could not be loaded.");
            }

            var pagePayload = pageResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var pageDocument = JsonDocument.Parse(pagePayload);
            AppendGogGames(pageDocument.RootElement, games);
        }
    }

    private static void AppendGogGames(JsonElement root, ICollection<UnifySteamGameCacheEntry> target)
    {
        if (!root.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var product in products.EnumerateArray())
        {
            if (product.TryGetProperty("isGame", out var isGameNode) && isGameNode.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            var worksOnWindows = !product.TryGetProperty("worksOn", out var worksOn) ||
                                 worksOn.ValueKind != JsonValueKind.Object ||
                                 !worksOn.TryGetProperty("Windows", out var windowsNode) ||
                                 windowsNode.ValueKind != JsonValueKind.False;
            if (!worksOnWindows)
            {
                continue;
            }

            var id = GetJsonString(product, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            target.Add(new UnifySteamGameCacheEntry
            {
                Id = id,
                Title = FirstNonEmpty(GetJsonString(product, "title"), id),
                Installed = false,
                ImageUrl = NormalizeImageUrl(GetJsonString(product, "image")),
            });
        }
    }

    private static List<UnifySteamGameCacheEntry> DedupeLibraryGames(IEnumerable<UnifySteamGameCacheEntry> games)
    {
        return games
            .Where(game => game is not null &&
                           (!string.IsNullOrWhiteSpace(game.Id) || !string.IsNullOrWhiteSpace(game.Title)))
            .GroupBy(game => NormalizeGameTitleKey(FirstNonEmpty(game.Title, game.Id)), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(game => game.Installed)
                .ThenByDescending(game => !string.IsNullOrWhiteSpace(game.ExecutablePath))
                .ThenByDescending(game => !string.IsNullOrWhiteSpace(game.InstallPath))
                .ThenByDescending(game => !string.IsNullOrWhiteSpace(game.ImageUrl))
                .ThenBy(game => (game.Id ?? string.Empty).Length)
                .ThenBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeGameTitleKey(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private GogCredential ExchangeGogAuthorizationCode(string authCode)
    {
        var requestUri =
            $"{BuildGogTokenBaseUri()}&grant_type=authorization_code&code={Uri.EscapeDataString(authCode)}&redirect_uri={Uri.EscapeDataString(GogRedirectUri)}";
        using var response = HttpClient.GetAsync(requestUri).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("GOG rejected the code. Open the login page again and paste a fresh code.");
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        return ParseGogToken(document.RootElement);
    }

    private static string BuildGogTokenBaseUri()
    {
        return
            $"https://auth.gog.com/token?client_id={GogClientId}&client_secret={GogClientSecret}";
    }

    private static string BuildGogAuthUrl()
    {
        return
            $"https://auth.gog.com/auth?client_id={GogClientId}&redirect_uri={Uri.EscapeDataString(GogRedirectUri)}&response_type=code&layout=client2";
    }

    private static EpicCredential ExchangeEpicAuthorizationCode(string authCode)
    {
        using var document = PostEpicTokenRequest(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authCode,
        }, "Epic rejected the code. Open the login page again and paste a fresh code.");

        return ParseEpicToken(document.RootElement);
    }

    private static EpicCredential ExchangeEpicRefreshToken(string refreshToken)
    {
        using var document = PostEpicTokenRequest(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, "The saved Epic login could not be refreshed. Sign in again.");

        return ParseEpicToken(document.RootElement);
    }

    private static JsonDocument PostEpicTokenRequest(
        IReadOnlyDictionary<string, string> formValues,
        string failureMessage)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
        {
            Content = new FormUrlEncodedContent(formValues),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{EpicClientId}:{EpicClientSecret}")));

        using var response = HttpClient.Send(request);
        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(failureMessage);
        }

        return JsonDocument.Parse(json);
    }

    private static GogCredential ParseGogToken(JsonElement root)
    {
        var expiresIn = root.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 3600;
        var accessToken = GetJsonString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The GOG token response did not include an access token.");
        }

        var loginTime = DateTimeOffset.UtcNow;
        return new GogCredential(
            accessToken,
            GetJsonString(root, "refresh_token"),
            GetJsonString(root, "user_id"),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static EpicCredential ParseEpicToken(JsonElement root)
    {
        var expiresIn = root.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 7200;
        var accessToken = GetJsonString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The Epic token response did not include an access token.");
        }

        var loginTime = DateTimeOffset.UtcNow;
        return new EpicCredential(
            accessToken,
            GetJsonString(root, "refresh_token"),
            FirstNonEmpty(GetJsonString(root, "account_id"), GetJsonString(root, "accountId")),
            FirstNonEmpty(GetJsonString(root, "displayName"), GetJsonString(root, "display_name")),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static GogCredential? LoadGogCredential(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(authPath));
        JsonElement credentialNode;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(GogClientId, out var gogCredentialNode))
        {
            credentialNode = gogCredentialNode;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var firstProperty = document.RootElement.EnumerateObject().FirstOrDefault();
            credentialNode = firstProperty.Value;
        }
        else
        {
            return null;
        }

        var accessToken = GetJsonString(credentialNode, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var expiresIn = credentialNode.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 3600;
        var loginTimeSeconds = credentialNode.TryGetProperty("loginTime", out var loginTimeNode) && loginTimeNode.TryGetDouble(out var parsedLoginTime)
            ? parsedLoginTime
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var loginTime = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Math.Floor(loginTimeSeconds)));

        return new GogCredential(
            accessToken,
            GetJsonString(credentialNode, "refresh_token"),
            GetJsonString(credentialNode, "user_id"),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static EpicCredential? LoadEpicCredential(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(authPath));
        JsonElement credentialNode;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(EpicClientId, out var epicCredentialNode))
        {
            credentialNode = epicCredentialNode;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            credentialNode = document.RootElement;
        }
        else
        {
            return null;
        }

        var accessToken = GetJsonString(credentialNode, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var expiresIn = credentialNode.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 7200;
        var loginTimeSeconds = credentialNode.TryGetProperty("loginTime", out var loginTimeNode) && loginTimeNode.TryGetDouble(out var parsedLoginTime)
            ? parsedLoginTime
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var loginTime = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Math.Floor(loginTimeSeconds)));

        return new EpicCredential(
            accessToken,
            GetJsonString(credentialNode, "refresh_token"),
            FirstNonEmpty(GetJsonString(credentialNode, "account_id"), GetJsonString(credentialNode, "accountId")),
            FirstNonEmpty(GetJsonString(credentialNode, "displayName"), GetJsonString(credentialNode, "display_name")),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static void SaveGogCredentials(string authPath, GogCredential credential)
    {
        var directory = Path.GetDirectoryName(authPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [GogClientId] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["access_token"] = credential.AccessToken,
                ["refresh_token"] = credential.RefreshToken,
                ["user_id"] = credential.UserId,
                ["expires_in"] = (int)Math.Max(1, (credential.ExpiresAtUtc - credential.LoginTimeUtc).TotalSeconds),
                ["loginTime"] = credential.LoginTimeUtc.ToUnixTimeSeconds(),
                ["token_type"] = "bearer",
            }
        };

        File.WriteAllText(authPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static void SaveEpicCredentials(string authPath, EpicCredential credential)
    {
        var directory = Path.GetDirectoryName(authPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [EpicClientId] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["access_token"] = credential.AccessToken,
                ["refresh_token"] = credential.RefreshToken,
                ["account_id"] = credential.AccountId,
                ["displayName"] = credential.DisplayName,
                ["expires_in"] = (int)Math.Max(1, (credential.ExpiresAtUtc - credential.LoginTimeUtc).TotalSeconds),
                ["loginTime"] = credential.LoginTimeUtc.ToUnixTimeSeconds(),
                ["token_type"] = "bearer",
            }
        };

        var temporaryPath = Path.Combine(
            directory ?? AppContext.BaseDirectory,
            $".{Path.GetFileName(authPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(payload, JsonOptions),
                new UTF8Encoding(false));
            if (File.Exists(authPath))
            {
                File.Replace(temporaryPath, authPath, authPath + ".bak", true);
            }
            else
            {
                File.Move(temporaryPath, authPath);
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

    private static CommandResult RunTool(string toolPath, params string[] arguments)
    {
        var startInfo = CreateHiddenStartInfo(toolPath, arguments);
        if (Path.GetFileName(toolPath).Equals("legendary.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(toolPath).Equals("legendary", StringComparison.OrdinalIgnoreCase))
        {
            ManagedLegendaryHelper.ConfigureEnvironment(startInfo);
        }
        else if (Path.GetFileName(toolPath).Equals("gogdl.exe", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileNameWithoutExtension(toolPath).Equals("gogdl", StringComparison.OrdinalIgnoreCase))
        {
            ManagedGogDlHelper.ConfigureEnvironment(startInfo);
        }
        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120000);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new InvalidOperationException("The external launcher tool timed out.");
        }

        Task.WaitAll(outputTask, errorTask);
        return new CommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static void StartDetachedProcess(string toolPath, string arguments, bool visible)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = true,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };

        if (IsBatchLike(toolPath))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{toolPath}\" {arguments}\"";
        }
        else
        {
            startInfo.FileName = toolPath;
            startInfo.Arguments = arguments;
        }

        Process.Start(startInfo)?.Dispose();
    }

    private static ProcessStartInfo CreateHiddenStartInfo(string toolPath, IReadOnlyList<string> arguments)
    {
        if (IsBatchLike(toolPath))
        {
            var batchStartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            batchStartInfo.ArgumentList.Add("/d");
            batchStartInfo.ArgumentList.Add("/s");
            batchStartInfo.ArgumentList.Add("/c");
            batchStartInfo.ArgumentList.Add($"\"{toolPath}\" {JoinCommandLine(arguments)}");
            return batchStartInfo;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string JoinCommandLine(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
    }

    private static bool IsBatchLike(string toolPath)
    {
        var extension = Path.GetExtension(toolPath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveToolPath(OmniLibraryStoreDescriptor definition, string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
        {
            var managedPath = ManagedLegendaryHelper.ResolveExistingToolPath(configuredPath);
            if (!string.IsNullOrWhiteSpace(managedPath))
            {
                return managedPath;
            }
        }
        else if (definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase))
        {
            var managedPath = ManagedGogDlHelper.ResolveExistingToolPath(configuredPath);
            if (!string.IsNullOrWhiteSpace(managedPath))
            {
                return managedPath;
            }
        }

        foreach (var candidate in GetCandidateToolPaths(definition))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string ResolveEpicLauncherPath()
    {
        foreach (var candidate in GetEpicLauncherCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetEpicLauncherCandidates()
    {
        foreach (var candidate in FindOnPath("EpicGamesLauncher.exe"))
        {
            yield return candidate;
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
                 })
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe");
        }
    }

    private static IEnumerable<string> GetCandidateToolPaths(OmniLibraryStoreDescriptor definition)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in FindOnPath(definition.ToolExecutableName, definition.ToolCommandName))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        var heroicBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "heroic",
            "resources",
            "app.asar.unpacked",
            "build",
            "bin",
            "win32");

        foreach (var candidate in new[]
                 {
                     Path.Combine(heroicBase, definition.ToolExecutableName),
                     Path.Combine(heroicBase, definition.ToolCommandName),
                 })
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> FindOnPath(params string[] names)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists);

        foreach (var directory in directories)
        {
            foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }

                if (Path.HasExtension(name))
                {
                    continue;
                }

                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    var extendedCandidate = candidate + extension;
                    if (File.Exists(extendedCandidate))
                    {
                        yield return extendedCandidate;
                    }
                }
            }
        }
    }

    private static string ResolveReadableGogAuthPath(string configuredPath)
    {
        if (File.Exists(ManagedGogDlHelper.AuthPath))
        {
            return ManagedGogDlHelper.AuthPath;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        return string.Empty;
    }

    private static string ResolveReadableEpicAuthPath(string configuredPath)
    {
        if (File.Exists(ManagedLegendaryHelper.UserDataPath))
        {
            return ManagedLegendaryHelper.UserDataPath;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
        {
            var configuredUserDataPath = Path.Combine(configuredPath, "user.json");
            if (File.Exists(configuredUserDataPath))
            {
                return configuredUserDataPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in GetCandidateEpicAuthPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string ResolveWritableGogAuthPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return NormalizePath(configuredPath);
        }

        return NormalizePath(ManagedGogDlHelper.AuthPath);
    }

    private static string ResolveWritableEpicAuthPath(string configuredPath)
    {
        _ = configuredPath;
        return ManagedLegendaryHelper.UserDataPath;
    }

    private static IEnumerable<string> GetCandidateEpicAuthPaths()
    {
        yield return ManagedLegendaryHelper.UserDataPath;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "ToolsForSteam", "unifystore", "epic-auth.json");
    }

    private static string ResolveInstalledExecutablePath(JsonElement item)
    {
        var executable = GetJsonString(item, "executable");
        var installPath = GetJsonString(item, "install_path");
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(executable))
        {
            return NormalizePath(executable);
        }

        if (!string.IsNullOrWhiteSpace(installPath))
        {
            return NormalizePath(Path.Combine(installPath, executable));
        }

        return executable;
    }

    private static string ResolveExecutablePath(string installPath, string executableHint)
    {
        if (string.IsNullOrWhiteSpace(executableHint))
        {
            return string.Empty;
        }

        var cleanedHint = executableHint.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(cleanedHint)
            ? NormalizePath(cleanedHint)
            : NormalizePath(Path.Combine(installPath ?? string.Empty, cleanedHint));
    }

    private static string ExtractAuthorizationCode(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?');
            foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = segment.Split('=', 2);
                if (parts.Length == 2 && string.Equals(parts[0], "code", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
        }

        // Epic's login page ends on a JSON document containing "authorizationCode".
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var propertyName in new[] { "authorizationCode", "code" })
                    {
                        if (document.RootElement.TryGetProperty(propertyName, out var codeNode) &&
                            codeNode.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(codeNode.GetString()))
                        {
                            return codeNode.GetString()!.Trim();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not valid JSON after all; fall through to the raw value.
            }
        }

        return trimmed;
    }

    private static string NormalizeImageUrl(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return string.Empty;
        }

        var normalized = imageUrl.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = $"https:{normalized}";
        }

        return UpgradeArtworkUrl(normalized);
    }

    private static string UpgradeArtworkUrl(string imageUrl)
    {
        if (imageUrl.Contains("gog-statics.com", StringComparison.OrdinalIgnoreCase))
        {
            var upgraded = Regex.Replace(
                imageUrl,
                @"_(196|392)(\.[a-z0-9]+)(\?.*)?$",
                "_784$2$3",
                RegexOptions.IgnoreCase);
            upgraded = Regex.Replace(
                upgraded,
                @"_product_card_v2_mobile_slider_\d+(\.[a-z0-9]+)(\?.*)?$",
                "_product_card_v2_mobile_slider_1280$1$2",
                RegexOptions.IgnoreCase);
            upgraded = Regex.Replace(
                upgraded,
                @"_glx_vertical_cover_\d+(\.[a-z0-9]+)(\?.*)?$",
                "_glx_vertical_cover_1200$1$2",
                RegexOptions.IgnoreCase);
            return upgraded;
        }

        if (imageUrl.Contains("epicgames", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("unrealengine", StringComparison.OrdinalIgnoreCase))
        {
            var upgraded = Regex.Replace(imageUrl, @"([?&])w=\d+", "$1w=1200", RegexOptions.IgnoreCase);
            upgraded = Regex.Replace(upgraded, @"([?&])h=\d+", "$1h=1600", RegexOptions.IgnoreCase);
            return upgraded;
        }

        return imageUrl;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static string ResolveXboxTitleId(JsonElement product)
    {
        if (product.ValueKind != JsonValueKind.Object ||
            !product.TryGetProperty("AlternateIds", out var alternateIds) ||
            alternateIds.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var alternateId in alternateIds.EnumerateArray())
        {
            if (!GetJsonString(alternateId, "IdType")
                    .Equals("XboxTitleId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = GetJsonString(alternateId, "Value");
            if (ulong.TryParse(value, out _))
            {
                return value;
            }
        }

        return string.Empty;
    }

    internal static bool TryNormalizeXboxManifestTitleId(
        string? value,
        out string titleId)
    {
        titleId = string.Empty;
        var hexadecimal = value?.Trim() ?? string.Empty;
        if (hexadecimal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hexadecimal = hexadecimal[2..];
        }
        if (hexadecimal.Length != 8 ||
            !uint.TryParse(
                hexadecimal,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var numericTitleId) ||
            numericTitleId is 0 or uint.MaxValue)
        {
            return false;
        }

        titleId = numericTitleId.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static string PreviewSecret(string value)
    {
        var secret = value?.Trim() ?? string.Empty;
        if (secret.Length == 0)
        {
            return string.Empty;
        }

        return secret.Length <= 8
            ? new string('•', secret.Length)
            : $"{secret[..4]}…{secret[^4..]}";
    }

    private static string GetGogLinkHref(JsonElement links, string propertyName)
    {
        if (links.ValueKind != JsonValueKind.Object ||
            !links.TryGetProperty(propertyName, out var link))
        {
            return string.Empty;
        }

        return link.ValueKind switch
        {
            JsonValueKind.String => link.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Object => GetJsonString(link, "href"),
            _ => string.Empty,
        };
    }

    private static int GetJsonInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => 0,
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static UnifySteamStoreConfiguration GetStoreConfiguration(StoreSyncConfiguration configuration, string storeId)
    {
        if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var storeConfiguration) || storeConfiguration is null)
        {
            storeConfiguration = new UnifySteamStoreConfiguration();
            configuration.UnifySteam.Stores[storeId] = storeConfiguration;
        }

        storeConfiguration.Cache ??= new UnifySteamLibraryCache();
        storeConfiguration.Cache.Games ??= [];
        return storeConfiguration;
    }

    private static OmniLibraryStoreDescriptor ResolveDefinition(string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new InvalidOperationException("A store ID is required.");
        }

        return OmniLibraryStoreRegistry.GetRequired(storeId);
    }

    private static IEnumerable<OmniLibraryStoreDescriptor> ResolveDefinitions(string? storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            return Definitions;
        }

        return [ResolveDefinition(storeId)];
    }

    private sealed record GogCredential(
        string AccessToken,
        string RefreshToken,
        string UserId,
        DateTimeOffset LoginTimeUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record EpicCredential(
        string AccessToken,
        string RefreshToken,
        string AccountId,
        string DisplayName,
        DateTimeOffset LoginTimeUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record GogLibraryResponse(
        string AccountName,
        List<UnifySteamGameCacheEntry> Games,
        IReadOnlyCollection<string> ProcessedOwnedIds);

    private sealed record EpicStatus(
        bool Authenticated,
        string AccountName);

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

internal sealed record UnifySteamStoreRefreshResult(
    string StoreId,
    bool Succeeded,
    string Error);

internal sealed record UnifySteamRefreshBatchResult(
    IReadOnlyList<UnifySteamStoreRefreshResult> Stores);
