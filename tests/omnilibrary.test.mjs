import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");

test("OmniLibrary exposes opt-in Xbox, Epic, and GOG stores through one isolated shortcut sync", () => {
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const settings = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncSettingsStore.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const registry = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryStoreRegistry.cs");

  assert.match(service, /OmniLibraryStoreRegistry\.All/);
  assert.match(registry, /"xbox-game-pass"[\s\S]*"epic-games"[\s\S]*"gog-galaxy"/);
  assert.match(settings, /OmniLibraryStoreRegistry\.Ids/);
  assert.match(settings, /Enabled = false/);
  assert.match(storeSync, /omniLibraryOnly: true/);
  assert.match(storeSync, /private void QueueOmniLibraryShortcutSync\(/);
  assert.match(storeSync, /omniLibraryStoreId: normalizedStoreId/);
  assert.match(
    storeSync,
    /BuildUnifySteamStoreSnapshot\(\s*configuration,\s*omniLibraryStoreId,\s*omniLibraryDelta\?\.UpsertGameIds\)/s,
  );
  assert.match(storeSync, /OmniLibraryStoreRegistry\.TryGet\(pair\.Key, out _\)/);
  assert.match(storeSync, /--unifysteam-launch \{storeId\}:\{game\.Id\}/);
  assert.match(storeSync, /if \(!omniLibraryOnly\)\s*\{\s*_ = Task\.Run/s);
});

test("dynamic native tabs, LB/RB, and D-Pad use exactly the enabled store order", () => {
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const topology = read("src/SteamLoader.App/Assets/omnilibrary-tab-topology.js");
  const registry = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryStoreRegistry.cs");

  assert.match(registry, /IReadOnlyList<OmniLibraryTabDescriptor> LibraryTabs/);
  assert.match(registry, /new\("tfs-xbox", "Xbox", "non-cloud"\)/);
  assert.match(registry, /new\("tfs-xbox-cloud", "Xbox Cloud", "cloud", RequiresCloudSource: true\)/);
  assert.match(registry, /new\("tfs-epic", "Epic", "all"\)/);
  assert.match(registry, /new\("tfs-gog", "GOG", "all"\)/);
  assert.match(tabs, /synchronizeStoreDefinitions\(stores\)/);
  assert.match(tabs, /tabTopology\.buildCanonicalTabOrder/);
  assert.match(tabs, /navigationCursorTabId/);
  assert.match(tabs, /navigationCursorAt/);
  assert.match(tabs, /commitLibraryNavigationTarget/);
  assert.match(tabs, /window\.localStorage\?\.setItem\(activeStoreTabSessionKey/);
  assert.match(tabs, /const backingRoutes = \["Installed", "AllGames", "DesktopApps"\]/);
  assert.match(tabs, /function navigateLibraryByDirection\(direction\)/);
  assert.match(tabs, /state\.navigationRuntime\.activeTab = normalizedTabId/);
  assert.match(tabs, /function installLibraryBumperInput\(\)/);
  assert.match(tabs, /function uninstallLibraryBumperInput\(\)/);
  assert.match(tabs, /function activateFocusedLibraryTab\(\)/);
  assert.match(tabs, /function scheduleFocusedLibraryTabActivation\(\)/);
  assert.match(tabs, /document\.addEventListener\("focusin", state\.libraryTabFocusHandler, true\)/);
  assert.match(tabs, /numericButton === 11 \? -1 : numericButton === 12 \? 1 : 0/);
  assert.match(topology, /function buildCanonicalTabOrder/);
  assert.match(topology, /function getAdjacentTabId/);
  assert.match(topology, /function resolveActiveTabId/);
});

test("managed game pages relabel only Steam's original action with download progress", () => {
  const surface = read("src/SteamLoader.App/Assets/xbox-library-surface.js");

  assert.match(surface, /state\.summary\?\.stores/);
  assert.match(surface, /api\/unifystore\/summary/);
  assert.match(surface, /api\/unifystore\/games\/\$\{encodeURIComponent\(appId\)\}/);
  assert.match(surface, /const activeDownloadRefreshIntervalMs = 1000/);
  assert.match(surface, /const managedGameRefreshIntervalMs = 15000/);
  assert.match(surface, /const idleRefreshIntervalMs = 30000/);
  assert.match(surface, /game\.cloudPlayable/);
  assert.doesNotMatch(surface, /downloadStatus === "completed"/);
  assert.match(
    surface,
    /if \(\(game\.installed && game\.updateAvailable !== true\) \|\| game\.cloudPlayable\)/,
  );
  assert.match(surface, /game\.installed && game\.updateAvailable === true/);
  assert.match(surface, /\? "Update"/);
  assert.match(surface, /`Downloading \$\{progress\}%`/);
  assert.match(surface, /downloadBytesPerSecond/);
  assert.match(surface, /downloadStatus === "reconnecting"/);
  assert.match(surface, /"Retry Download"/);
  assert.match(surface, /@keyframes steamtools-omni-download-bob/);
  assert.match(surface, /steamtools-omni-download-icon/);
  assert.match(surface, /\["failed", "cancel-failed", "action-required"\]\.includes\(status\) \? "alert" : "status"/);
  assert.match(surface, /Network \$\{formatBytes\(speedBytes\)\}\/s/);
  assert.match(surface, /Processing \$\{formatBytes\(decompressedBytes\)\}\/s/);
  assert.match(surface, /Disk \$\{formatBytes\(diskWriteBytes\)\}\/s/);
  assert.match(surface, /Retry requested\./);
  assert.match(surface, /event\.stopImmediatePropagation\(\)/);
  assert.match(surface, /overflow-wrap: anywhere/);
  assert.match(surface, /banner\.dataset\.signature/);
  assert.match(surface, /refreshCurrentGame\(true\)/);
  assert.match(surface, /managedLabels: new Map\(\)/);
  assert.match(surface, /appId >= 0x80000000/);
  assert.match(surface, /lastActivatedManagedAppId/);
  assert.match(surface, /Date\.now\(\) - recentAt < 5000/);
  assert.match(surface, /payloadAppId !== appId/);
  assert.match(surface, /textNode\.nodeValue === appliedLabel/);
  assert.match(surface, /restoreManagedLabels\(label\)/);
  assert.doesNotMatch(surface, /createElement\("button"\)/);
});

test("Epic downloads resume safely, expose telemetry, and block system sleep", () => {
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");
  const downloads = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamDownloadStatusStore.cs");
  const sleep = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryDownloadSleepBlocker.cs");
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");

  assert.match(launcher, /EpicMaximumDownloadAttempts = 5/);
  assert.match(launcher, /"--dl-timeout"/);
  assert.match(launcher, /"--max-workers"/);
  assert.match(launcher, /BuildEpicDownloadPlan/);
  assert.match(launcher, /GetEpicResumeCompletedBytes/);
  assert.match(launcher, /EnsureEpicDownloadHasSpace/);
  assert.match(launcher, /appName,\s*"reconnecting"/s);
  assert.match(launcher, /Thread\.Sleep\(retryDelay\)/);
  assert.match(launcher, /omnilibrary-epic-download\.log/);
  assert.match(downloads, /FileOptions\.WriteThrough/);
  assert.match(downloads, /File\.Replace/);
  assert.match(downloads, /BackupFilePath/);
  assert.match(downloads, /GetRecoverableDownloads/);
  assert.match(downloads, /Environment\.TickCount64/);
  assert.match(downloads, /storeOwnsTransferLifecycle/);
  assert.match(sleep, /ExecutionState\.Continuous \| ExecutionState\.SystemRequired/);
  assert.match(host, /ResumeInterruptedDownloads\(\)/);
  assert.match(host, /RunStatusMonitorAsync/);
});

test("all managed stores expose durable phases and only terminal failures become Retry", () => {
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");
  const downloads = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamDownloadStatusStore.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const surface = read("src/SteamLoader.App/Assets/xbox-library-surface.js");

  assert.match(downloads, /"preparing" or[\s\S]*"queued" or[\s\S]*"downloading" or[\s\S]*"reconnecting" or[\s\S]*"finalizing"/);
  assert.match(launcher, /foreach \(var storeId in new\[\][\s\S]*"xbox-game-pass"[\s\S]*"epic-games"[\s\S]*"gog-galaxy"/);
  assert.match(launcher, /ManagedGogDlHelper\.AuthPath/);
  assert.match(launcher, /Resuming GOG download \(attempt/);
  assert.match(launcher, /GOG download stopped after automatic resume attempts/);
  assert.match(launcher, /AssignDownloadWorkerIfUnclaimed/);
  assert.match(api, /AssignDownloadWorkerIfUnclaimed/);
  assert.match(surface, /\["failed", "cancel-failed"\]\.includes\(downloadStatus\)[\s\S]*"Retry Download"/);
  assert.match(surface, /downloadStatus === "action-required"[\s\S]*"Open Xbox"[\s\S]*"Open GOG"/);
});

test("Download Center unifies concurrent stores with durable controller actions and safe cleanup", () => {
  const models = read("src/SteamLoader.App/Models/UnifySteamSnapshot.cs");
  const downloads = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamDownloadStatusStore.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const program = read("src/SteamLoader.App/Program.cs");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const surface = read("src/SteamLoader.App/Assets/xbox-library-surface.js");
  const sleep = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryDownloadSleepBlocker.cs");

  assert.match(models, /OmniLibraryDownloadCenterSnapshot/);
  assert.match(models, /OmniLibraryDownloadCenterEntry/);
  assert.match(models, /bool CanPause[\s\S]*bool CanResume[\s\S]*bool CanCancel[\s\S]*bool CanDismiss/);
  assert.match(
    downloads,
    /IsDownloadCenterStatus[\s\S]*"uninstalling"[\s\S]*"uninstall-action-required"[\s\S]*"uninstall-failed"/,
  );
  assert.match(downloads, /IsBusyOperation/);
  assert.match(downloads, /BlocksStoreDisconnect/);
  assert.match(downloads, /ProgressPersistenceInterval/);
  assert.match(downloads, /WorkerStartedAtUtc/);
  assert.match(downloads, /TryParseKey/);
  assert.match(downloads, /GetRecoverableCancellations/);
  assert.match(storeSync, /GetOmniLibraryDownloadCenter/);
  assert.match(storeSync, /DownloadCenterRecentHistoryLimit/);
  assert.match(storeSync, /BuildOmniLibraryDownloadCenterEntry/);
  assert.match(storeSync, /CatalogEntryAvailable/);
  assert.match(storeSync, /BlocksStoreDisconnect/);
  assert.match(storeSync, /OmniLibraryStoreRegistry\.GetRequired\(store\.Id\)/);
  assert.match(api, /\/api\/unifystore\/downloads"/);
  assert.match(api, /\/api\/unifystore\/downloads\/action"/);
  assert.match(api, /case "pause" when entry\.CanPause/);
  assert.match(api, /case "resume" when entry\.CanResume/);
  assert.match(api, /case "cancel" when entry\.CanCancel/);
  assert.match(api, /case "dismiss" when entry\.CanDismiss/);
  assert.match(api, /case "manage" when entry\.CanManageExternally/);
  assert.match(api, /case "retry-uninstall" when entry\.CanRetryUninstall/);
  assert.match(launcher, /CancelDownloadArgument = "--unifysteam-cancel-download"/);
  assert.match(program, /UnifySteamLauncher\.CancelDownloadArgument/);
  assert.match(launcher, /process\.Kill\(entireProcessTree: true\)/);
  assert.match(launcher, /DeleteContainedDownloadDirectory/);
  assert.match(launcher, /normalizedTarget\.StartsWith\([\s\S]*containedPrefix/s);
  assert.match(launcher, /FileAttributes\.ReparsePoint/);
  assert.match(popup, /buildOmniLibraryDownloadCenterSlots/);
  assert.match(popup, /topSlots:\s*\[\s*\.\.\.downloadCenterSlots/s);
  assert.match(popup, /"Pause Download"/);
  assert.match(popup, /"Resume Download"/);
  assert.match(popup, /"Cancel & Delete Partial Files"/);
  assert.match(popup, /"Remove from Download Center"/);
  assert.match(popup, /"Retry Uninstall"/);
  assert.match(popup, /downloadActionKeys/);
  assert.match(popup, /download-status-changed/);
  assert.match(popup, /AbortController/);
  assert.match(popup, /downloadCenterRefreshQueued/);
  assert.match(popup, /hasActiveDownload \? 2000 : 10000/);
  assert.match(surface, /downloadStatus === "canceling"[\s\S]*"Canceling"/);
  assert.match(surface, /"canceling",/);
  assert.match(sleep, /IsBusyOperation/);
});

test("OmniLibrary hot paths use cached lightweight state and batched download status", () => {
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const unifySteam = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const downloads = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamDownloadStatusStore.cs");
  const artwork = read("src/SteamLoader.App/Infrastructure/StoreSync/SteamGridDbArtworkDownloader.cs");
  const publisher = read("src/SteamLoader.App/Hosting/QuickAccessLiveStatePublisher.cs");
  const automation = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncAutomationService.cs");
  const controller = read("src/SteamLoader.App/Services/ControllerShortcutService.cs");

  assert.match(api, /\/api\/unifystore\/state[\s\S]*GetUnifySteamState\(\)/);
  assert.match(api, /\/api\/unifystore\/settings-state[\s\S]*GetUnifySteamSettingsState\(\)/);
  assert.match(api, /\/api\/unifystore\/summary[\s\S]*GetUnifySteamLibrarySummary\(\)/);
  assert.match(api, /unifySteamGamePathPrefix = "\/api\/unifystore\/games\/"/);
  assert.match(api, /GetUnifySteamGame\(unifySteamGameAppId\)/);
  assert.match(storeSync, /UnifySteamSnapshotCacheLifetime = TimeSpan\.FromSeconds\(60\)/);
  assert.match(storeSync, /GetCachedUnifySteamCatalogState/);
  assert.match(
    storeSync,
    /GetOmniLibraryDownloadCenter\(\)[\s\S]*GetCachedUnifySteamCatalogState\(\)/,
  );
  assert.match(
    storeSync,
    /GetUnifySteamGame\(uint appId\)[\s\S]*GetCachedUnifySteamCatalogState\(\)/,
  );
  assert.match(storeSync, /_unifySteamService\.BuildSnapshot\(configuration,\s*\[\]\)/);
  assert.match(storeSync, /ReconcilePendingXboxRemovals/);
  assert.match(unifySteam, /TryProbeXboxGameInstallation/);
  assert.match(unifySteam, /XboxInstalledCache\.Values/);
  assert.match(unifySteam, /UnifySteamDownloadStatusStore\.GetAll\(\)/);
  assert.match(downloads, /Get\(GetAll\(\), storeId, gameId\)/);
  assert.match(artwork, /HasPrimaryArtworkSet/);
  assert.match(publisher, /StoreSyncInterval = TimeSpan\.FromSeconds\(30\)/);
  assert.match(publisher, /if \(!_isStoreSyncEnabled\(\)\)/);
  assert.match(automation, /if \(!_isPluginEnabled\(\)\)[\s\S]*ClearWatchers\(\)/);
  assert.match(automation, /Task\.Delay\(TimeSpan\.FromSeconds\(5\), cancellationToken\)/);
  assert.match(controller, /PollInterval = TimeSpan\.FromMilliseconds\(25\)/);
});

test("OmniLibrary reconciles install and uninstall lifecycle deltas promptly", () => {
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const artwork = read("src/SteamLoader.App/Assets/artwork-surface.js");

  assert.match(tabs, /function isPendingLifecycleStatus\(status\)/);
  assert.match(
    tabs,
    /isActiveDownloadCenterStatus\(status\)[\s\S]*"uninstalling"[\s\S]*"uninstall-action-required"/,
  );
  assert.match(
    tabs,
    /state\.pendingLifecycleAppIds\.size > 0[\s\S]*activeDownloadRefreshIntervalMs/,
  );
  assert.match(tabs, /if \(lifecycle\.completed\)[\s\S]*refreshXboxAppIds\(true\)/);
  assert.match(artwork, /publishOmniLibraryLifecycleStatus\(appId, "uninstalling"\)/);
  assert.match(
    tabs,
    /followUpRequest[\s\S]*shared\.refresh\(true\)[\s\S]*\.finally\(\(\) =>/,
  );
  assert.match(tabs, /AbortController/);
  assert.match(tabs, /libraryForceRefreshPending/);
  assert.match(tabs, /function applyConfirmedInstalledLifecycleDeltas\(entries\)/);
  assert.match(tabs, /confirmedInstalledHints[\s\S]*expiresAt: now \+ 15000/);
  assert.match(
    tabs,
    /entry\?\.installed !== true[\s\S]*state\.pendingLifecycleAppIds\.has\(appId\)/,
  );
});

test("disabling OmniLibrary removes its UI, input hooks, and protected API surface", () => {
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const surface = read("src/SteamLoader.App/Assets/xbox-library-surface.js");
  const artwork = read("src/SteamLoader.App/Assets/artwork-surface.js");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const models = read("src/SteamLoader.App/Models/UnifySteamSnapshot.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");

  assert.match(models, /bool PluginEnabled/);
  assert.match(
    api,
    /new UnifySteamLibrarySummarySnapshot\(\s*0,\s*false,\s*\[\]\)/s,
  );
  assert.match(tabs, /function uninstallLibraryBumperInput\(\)/);
  assert.match(tabs, /function restoreFiberNode\(node\)/);
  assert.match(tabs, /function disableXboxTileObserver\(\)/);
  assert.match(tabs, /function disableActiveRuntimeTimers\(\)/);
  assert.match(tabs, /state\.pluginEnabled = snapshot\?\.pluginEnabled === true/);
  assert.match(tabs, /if \(!state\.pluginEnabled \|\| !direction\)/);
  assert.match(tabs, /if \(!runtimeActive\)\s*\{\s*setVirtualActiveTabId\("", true\)/s);
  assert.match(tabs, /if \(!state\.pluginEnabled \|\| !enabledDefinitions\.length\)/);
  assert.match(tabs, /state\.activationResolved = true/);
  assert.match(tabs, /getNativeNavigationHandler\(props\.onShowTab\)/);
  assert.match(tabs, /backendUnavailable: true/);
  assert.match(tabs, /shared\.snapshot = unavailableSnapshot/);
  assert.match(api, /SetOmniLibraryPluginRuntimeEnabled/);
  assert.match(api, /SetStoreSyncPluginRuntimeEnabled/);
  assert.match(storeSync, /CancelOmniLibraryBackgroundWork\(storeId\)/);
  assert.match(storeSync, /activeSyncCancellation\?\.Cancel\(\)/);
  assert.match(storeSync, /cancellation\.Cancel\(\)/);
  assert.match(storeSync, /DownloadSteamFirstAsync\([\s\S]*cancellationToken/s);
  assert.match(storeSync, /catch \(OperationCanceledException\) when \(cancellationToken\.IsCancellationRequested\)/);
  assert.doesNotMatch(
    tabs,
    /installLibraryBumperInput\(\);\s*ensureXboxTileObserver\(\);\s*patchXboxTabBasis\(\);\s*void refreshXboxAppIds\(true\)/s,
  );
  assert.match(surface, /function deactivateSurface\(\)/);
  assert.match(surface, /state\.summary\?\.pluginEnabled === true/);
  assert.match(artwork, /snapshot\?\.pluginEnabled !== true/);
  assert.match(artwork, /removeOmniLibraryUninstallContextRows\(\)/);
});

test("OmniLibrary uninstall provides store-specific feedback without taking controller focus", () => {
  const artwork = read("src/SteamLoader.App/Assets/artwork-surface.js");
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");

  assert.match(
    artwork,
    /Please finish uninstalling this game in the Xbox window\./,
  );
  assert.match(
    artwork,
    /Please wait while OmniLibrary uninstalls this game automatically\./,
  );
  assert.match(artwork, /notice\.setAttribute\("role", failed \? "alert" : "status"\)/);
  assert.match(artwork, /"pointer-events:none"/);
  assert.match(artwork, /document\.body\.appendChild\(notice\)/);
  assert.match(artwork, /type: "uninstall-notice"/);
  assert.match(artwork, /__steamLoaderLibraryTabsState\?\.showUninstallNotice/);
  assert.match(tabs, /event\?\.data\?\.type === "uninstall-notice"/);
  assert.match(tabs, /state\.showUninstallNotice = showOmniLibraryUninstallNotice/);
  assert.match(tabs, /"bottom:116px"/);
  assert.match(api, /ActivateLaunchedAppWhenReady\(\s*"Xbox",\s*"XboxPcApp"/s);
  assert.match(launcher, /msxbox:\/\/game\/\?productId=/);
  assert.doesNotMatch(launcher, /IApplicationActivationManager/);
  assert.doesNotMatch(launcher, /TryActivateXboxProductPage/);
  assert.match(launcher, /TryOpenXboxProductPage\(productId, out _\)/);
});

test("managed stores use one isolated authorization runtime", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const login = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryLoginRuntime.cs");

  assert.match(popup, /makeAccordionSlot\(\s*"Xbox"/s);
  assert.match(popup, /makeAccordionSlot\(\s*"Epic Games"/s);
  assert.match(popup, /Enable Epic Games/);
  assert.match(popup, /Sign in to Epic/);
  assert.match(popup, /closes and syncs your library automatically/);
  assert.doesNotMatch(popup, /title: "Epic Authorization Code"/);
  assert.doesNotMatch(popup, /"Complete Epic Sign-In"/);
  assert.match(popup, /Sync Epic Library/);
  assert.match(popup, /Epic Install Path/);
  assert.match(popup, /toggleUnifyStoreEnabled\("epic-games", !epicEnabled\)/);
  assert.match(api, /\/api\/unifystore\/stores\/auth-code/);
  assert.match(api, /MonitorOmniLibraryLoginAsync/);
  assert.match(login, /WebView2/);
  assert.match(login, /WebResourceResponseReceived/);
  assert.match(login, /ManagedLegendaryHelper\.Authenticate/);
  assert.match(login, /ManagedGogDlHelper\.Authenticate/);
});

test("future OmniLibrary stores inherit capabilities, generic settings, and backend tab descriptors", () => {
  const registry = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryStoreRegistry.cs");
  const models = read("src/SteamLoader.App/Models/UnifySteamSnapshot.cs");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");

  assert.match(registry, /enum OmniLibraryStoreCapabilities/);
  assert.match(registry, /GetCapabilityIds/);
  assert.match(models, /IReadOnlyList<string> Capabilities/);
  assert.match(models, /IReadOnlyList<UnifySteamLibraryTabSummary> LibraryTabs/);
  assert.match(popup, /genericStoreSlots/);
  assert.match(popup, /capabilities\.has\("install-path"\)/);
  assert.match(popup, /saveGenericUnifyStoreInstallPath/);
  assert.match(tabs, /tabTopology\.buildDefinitionsFromSummary/);
});

test("Epic downloads use a pinned verified hidden Legendary helper", () => {
  const helper = read("src/SteamLoader.App/Infrastructure/StoreSync/ManagedLegendaryHelper.cs");
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");

  assert.match(helper, /Version = "0\.20\.43"/);
  assert.match(helper, /Sha256 =\s*"ec1ad2d19d44e07b2b0330191c300979f102c509f2a889708099f453c5188f20"/s);
  assert.match(helper, /LEGENDARY_CONFIG_PATH/);
  assert.match(launcher, /"epic-games" => InstallEpic\(gameId\)/);
  assert.match(launcher, /"epic-games" => RunEpic\(gameId\)/);
  assert.match(launcher, /RunHiddenDownloadAndTrack/);
  assert.match(launcher, /"--skip-sdl"/);
  assert.match(launcher, /"--skip-dlcs"/);
  assert.match(launcher, /CreateNoWindow = !visible/);
  assert.match(launcher, /UnifySteamDownloadStatusStore\.Update/);
});

test("Epic Rockstar games use a pinned ownership bridge without inheriting Steam identity", () => {
  const bridge = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/ManagedEpicLauncherBridge.cs",
  );
  const launcher = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs",
  );
  const compatibility = read(
    "src/SteamLoader.App/Assets/omnilibrary-epic-compatibility.json",
  );
  const compatibilityCatalog = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/EpicCompatibilityCatalog.cs",
  );
  const project = read("src/SteamLoader.App/SteamLoader.App.csproj");

  assert.match(bridge, /heroic-epic-integration\/releases\/download\/v0\.4/);
  assert.match(bridge, /Sha256/);
  assert.match(bridge, /HasExpectedHash\(temporaryPath\)/);
  assert.match(compatibility, /"appName": "Heather"/);
  assert.match(compatibility, /"appName": "9d2d0eb64d5c44529cece33fe2a46482"/);
  assert.match(compatibility, /"appName": "8769e24080ea413b8ebca3f1b8c50951"/);
  assert.match(compatibilityCatalog, /A deliberately small, bundled compatibility catalog/);
  assert.doesNotMatch(compatibilityCatalog, /Process\.Start/);
  assert.match(launcher, /ManagedEpicLauncherBridge\.EnsureInstalled\(\)/);
  assert.match(launcher, /startInfo\.Environment\["LEGENDARY_WRAPPER_EXE"\]/);
  assert.doesNotMatch(launcher, /launchArguments\.Add\("--wrapper"\)/);
  assert.match(launcher, /RemoveInheritedSteamLaunchContext\(startInfo\)/);
  assert.match(launcher, /startInfo\.Environment\.Remove\("SteamAppId"\)/);
  assert.match(project, /ThirdParty\\EpicLauncherBridge\\NOTICE\.txt/);
});

test("GOG reuses the managed provider contracts and processes only catalog deltas", () => {
  const registry = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryStoreRegistry.cs");
  const helper = read("src/SteamLoader.App/Infrastructure/StoreSync/ManagedGogDlHelper.cs");
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");
  const settings = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncSettingsStore.cs");
  const journal = read("src/SteamLoader.App/Infrastructure/StoreSync/GogOperationJournal.cs");
  const tracker = read("src/SteamLoader.App/Infrastructure/StoreSync/GogInstallStateTracker.cs");
  const artworkSurface = read("src/SteamLoader.App/Assets/artwork-surface.js");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");

  assert.match(registry, /"gog-galaxy"[\s\S]*ManagedWebSignIn[\s\S]*ManagedInstall[\s\S]*ManagedUninstall/);
  assert.match(helper, /Version = "1\.2\.2"/);
  assert.match(helper, /37e7cf848d35ffff92dfaeb62d7751709e0b8a0deb17dda36a013d73300e61c1/);
  assert.match(helper, /SHA256\.HashData/);
  assert.match(helper, /data", "omnilibrary", "gog"/);
  assert.match(helper, /GOGDL_CONFIG_PATH/);
  assert.match(settings, /RemoteCatalogItemIds/);
  assert.match(service, /var addedIds = ownedIds[\s\S]*previouslyProcessedIds/);
  assert.doesNotMatch(service, /api\.gog\.com\/v2\/games/);
  assert.match(service, /central artwork pipeline upgrades images asynchronously/);
  assert.match(service, /Metadata for \{pendingMetadataCount\}/);
  assert.match(launcher, /repairRequested[\s\S]*"repair"[\s\S]*updateRequested[\s\S]*"update"[\s\S]*"download"/);
  assert.match(launcher, /includeDlc \? "--with-dlcs" : "--skip-dlcs"/);
  assert.match(launcher, /"--max-workers"/);
  assert.match(launcher, /Progress:\\s\*/);
  assert.match(launcher, /IsManagedGogInstallRoot/);
  assert.match(launcher, /GogManagedInstallMarkerFileName/);
  assert.match(launcher, /WriteGogManagedInstallMarker\(actualInstallRoot, gameId\)/);
  assert.match(launcher, /File\.ReadAllText\([\s\S]*GogManagedInstallMarkerFileName/s);
  assert.match(journal, /transactions\.json/);
  assert.match(journal, /File\.Replace/);
  assert.match(tracker, /RegistrySnapshotLifetime/);
  assert.match(tracker, /ConfirmMissing/);
  assert.match(artworkSurface, /Verify & Repair\.\.\./);
  assert.match(storeSync, /RepairableAppIds/);
  assert.doesNotMatch(service, /heroic_gogdl/);
  assert.doesNotMatch(launcher, /heroic_gogdl/);
});

test("OmniLibrary artwork is asynchronous and Steam-public-first with SteamGridDB fallback", () => {
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const artwork = read("src/SteamLoader.App/Infrastructure/StoreSync/SteamGridDbArtworkDownloader.cs");

  assert.match(storeSync, /QueueOmniLibraryArtworkSync/);
  assert.match(storeSync, /_activeOmniArtworkTask/);
  assert.match(storeSync, /_pendingOmniArtworkTargets/);
  assert.match(storeSync, /_activeOmniArtworkTargetIds/);
  assert.match(storeSync, /ProcessQueuedOmniLibraryArtworkAsync/);
  assert.match(storeSync, /DownloadSteamFirstAsync/);
  assert.match(storeSync, /must not[\s\S]*move the public preparation state backwards/);
  assert.match(artwork, /store\.steampowered\.com\/api\/storesearch/);
  assert.match(artwork, /cdn\.cloudflare\.steamstatic\.com/);
  assert.match(artwork, /library_600x900_2x\.jpg/);
  assert.match(artwork, /library_hero\.jpg/);
  assert.match(artwork, /logo_2x\.png/);
  assert.match(artwork, /DownloadArtworkSetAsync\(/);
  assert.match(artwork, /Deliberately sequential/);
});

test("differential five-minute checks cover every enabled store with isolated backoff", () => {
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const unifySteam = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");

  assert.match(host, /OmniLibraryStartupDelay = TimeSpan\.FromSeconds\(15\)/);
  assert.match(host, /OmniLibraryCatalogCheckInterval = TimeSpan\.FromMinutes\(5\)/);
  assert.match(host, /GetEnabledUnifySteamStoreIds\(\)/);
  assert.match(host, /CheckUnifySteamStoreForChanges\(storeId\)/);
  assert.match(host, /ComputeOmniLibraryFailureBackoff/);
  assert.match(host, /Dictionary<string, OmniLibraryStoreCheckSchedule>/);
  assert.match(service, /GetRemoteCatalogSignature\(store\)/);
  assert.match(service, /ComputeOmniLibraryCatalogDelta/);
  assert.match(service, /QueueOmniLibraryShortcutSync\(\s*normalizedStoreId,\s*upsertGameIds,\s*removedGameIds/s);
  assert.match(service, /FilterOmniLibraryAnalysis\(analysis, omniLibraryDelta\)/);
  assert.match(service, /delta\.UpsertGameIds\.Contains\(gameId\)/);
  assert.match(service, /delta\.RemovedGameIds\.Contains\(gameId\)/);
  assert.match(service, /QueueOmniLibraryArtworkSync\([\s\S]*analysis,[\s\S]*liveShortcutAppIds/s);
  assert.match(service, /includeIncompleteRepair\s*&&\s*ShouldResumeIncompleteOmniLibraryPreparation\(refreshedStore\)/s);
  assert.match(service, /store\.PreparationCompletedCount < store\.PreparationTotalCount/);
  assert.match(service, /store\.PreparedAtUtc \?\?= preparedAtUtc/);
  assert.match(service, /PreparationStatus\.Equals\("artwork-pending"/);
  assert.match(service, /lightweight:\s*true/);
  assert.match(unifySteam, /RemoteCatalogSignature/);
});

test("Xbox sources are independent and cloud-only games stream without entering Installed", () => {
  const settings = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncSettingsStore.cs");
  const models = read("src/SteamLoader.App/Models/UnifySteamSnapshot.cs");
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const surface = read("src/SteamLoader.App/Assets/xbox-library-surface.js");

  assert.match(settings, /IncludeXboxPcGamePass \{ get; set; \} = true/);
  assert.match(settings, /IncludeXboxCloudGaming \{ get; set; \}/);
  assert.match(settings, /OmniLibrarySettingsVersion < 1/);
  assert.match(settings, /IncludeXboxCloudGaming = false/);
  assert.match(settings, /CloudPlayable \{ get; set; \}/);
  assert.match(models, /IReadOnlyList<uint> CloudAppIds/);
  assert.match(models, /bool XboxCloudGamingEnabled/);
  assert.match(models, /IReadOnlyList<uint> ActiveDownloadAppIds/);
  assert.match(service, /XboxCloudCatalogMarker = "__Cloud:XGPUWEB"/);
  assert.match(service, /XboxCloudGamingCatalogId = "af206485-e87d-4624-9007-cb7f6d0cc42e"/);
  assert.match(service, /LoadXboxSiglProductIds/);
  assert.match(service, /LoadXboxPcGamePassProductIds/);
  assert.match(service, /LoadXboxCloudProductIds/);
  assert.match(service, /XboxCatalogShapeVersion = "xbox-catalog-v2-console-cloud"/);
  assert.match(service, /IsXboxConsoleCatalogProduct\(product\)/);
  assert.match(service, /GroupBy\(\s*candidate => NormalizeGameTitleKey\(candidate\.Game\.Title\)/s);
  assert.match(service, /ResolveXboxHeroUrl\(localized\)/);
  assert.match(service, /var cloudPlayable =\s*cloudProductIdSet\.Contains\(productId\) &&\s*!pcProductIdSet\.Contains\(productId\)/s);
  assert.match(launcher, /BuildXboxCloudLaunchUrl/);
  assert.match(launcher, /\/play\/launch\//);
  assert.match(storeSync, /SetUnifySteamXboxSourceEnabled/);
  assert.match(storeSync, /\.Where\(game => game\.CloudPlayable\)/);
  assert.match(popup, /Include PC Game Pass/);
  assert.match(popup, /Enable Xbox Cloud tab/);
  assert.match(popup, /api\/unifystore\/stores\/xbox-source/);
  assert.match(tabs, /cloudAppIds/);
  assert.match(tabs, /function createXboxTileStatusIcon\(status\)/);
  assert.match(tabs, /status === "installed"/);
  assert.match(tabs, /status === "cloud"/);
  assert.match(tabs, /status === "downloading"/);
  assert.match(tabs, /api\/unifystore\/downloads/);
  assert.match(tabs, /activeDownloadRefreshIntervalMs = 2000/);
  assert.match(tabs, /idleDownloadRefreshIntervalMs = 10000/);
  assert.match(tabs, /steamtools-omni-tile-download-spinner/);
  assert.match(tabs, /steamtools-omni-tile-download-spin/);
  assert.match(tabs, /event\?\.data\?\.type === "download-status-changed"/);
  assert.match(surface, /type: "download-status-changed"/);
  assert.doesNotMatch(tabs, /\\u2713 INSTALLED|\\u2601 CLOUD|\\u2193 DOWNLOAD/);
  assert.match(popup, /restartRequiredStores/);
  assert.match(popup, /"Restart Steam Now"/);
  assert.match(popup, /api\/unifystore\/restart-steam/);
});

test("OmniLibrary repairs only missing artwork and keeps store catalog images as the final fallback", () => {
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const artwork = read("src/SteamLoader.App/Infrastructure/StoreSync/SteamGridDbArtworkDownloader.cs");
  const settings = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncSettingsStore.cs");

  assert.match(storeSync, /BuildIncompleteOmniLibraryArtworkTargets/);
  assert.match(storeSync, /!SteamGridDbArtworkDownloader\.HasPrimaryArtworkSet\(/);
  assert.match(storeSync, /analysisTargets\s*\.Concat\(cachedRepairTargets\)/s);
  assert.match(storeSync, /Artwork is optional cache data and must never hide Xbox/);
  assert.match(storeSync, /MarkOmniLibraryArtworkRepairPending/);
  assert.match(storeSync, /GetMissingPrimaryArtworkSlots/);
  assert.match(storeSync, /optional artwork remains incomplete/);
  assert.match(settings, /OmniLibrarySettingsVersion < 2/);
  assert.match(settings, /wasBlockedOnlyByArtwork/);
  assert.match(artwork, /DownloadStoreFallbackArtworkSetAsync/);
  assert.match(artwork, /\[gridId\] = wideFallbackUrl/);
  assert.match(artwork, /\[\$"\{gridId\}p"\] = portraitFallbackUrl/);
  assert.match(artwork, /PromoteStoreFallbackArtwork/);
});
