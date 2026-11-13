import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");

test("OmniLibrary exposes opt-in stores and one local emulation library through one isolated shortcut sync", () => {
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const settings = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncSettingsStore.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const registry = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryStoreRegistry.cs");

  assert.match(service, /OmniLibraryStoreRegistry\.All/);
  assert.match(registry, /"xbox-game-pass"[\s\S]*"epic-games"[\s\S]*"gog-galaxy"[\s\S]*OmniLibraryRomSystemRegistry\.StoreId/);
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
  assert.match(registry, /BuildLibraryTabSummaries\([\s\S]*romSystems/);
  assert.match(registry, /\$"platform:\{system\.Id\}"/);
  assert.match(registry, /BuildLibraryTabId\(system\.Id\)/);
  assert.match(tabs, /synchronizeStoreDefinitions\(stores\)/);
  assert.match(tabs, /tabTopology\.buildCanonicalTabOrder/);
  assert.match(tabs, /navigationCursorTabId/);
  assert.match(tabs, /navigationCursorAt/);
  assert.match(tabs, /commitLibraryNavigationTarget/);
  assert.match(tabs, /window\.localStorage\?\.setItem\(activeStoreTabSessionKey/);
  assert.match(tabs, /const preferredRoutes = \["Installed", "AllGames", "DesktopApps"\]/);
  assert.match(tabs, /visibleNativeIds/);
  assert.match(tabs, /chooseDistinctBackingRoute/);
  assert.match(tabs, /__steamLoaderNativeDesktopTab: templateTab/);
  assert.match(tabs, /restoreRememberedNativeTabs/);
  assert.match(tabs, /tabTopology\?\.hasTabMasterOwnership\?\.\(tabs\)/);
  assert.match(
    tabs,
    /const restoredTabs = tabMasterOwnsLayout[\s\S]*\? visibleTabs[\s\S]*: restoreRememberedNativeTabs\(visibleTabs\)/,
  );
  assert.match(tabs, /function withoutTabMasterFooter\(tab\)/);
  assert.match(tabs, /delete footer\.onMenuButton/);
  assert.match(tabs, /storeTemplateTab[\s\S]*rememberedDesktopTab[\s\S]*nativeAllGamesTab/);
  assert.match(tabs, /function navigateLibraryByDirection\(direction\)/);
  assert.match(tabs, /function isNavigableLibraryTabElement\(element\)/);
  assert.match(tabs, /document\.querySelectorAll\('\[role="tablist"\]'\)/);
  assert.match(tabs, /for \(let depth = 0; current && depth < 24; depth \+= 1\)/);
  assert.match(tabs, /function filterMountedNavigationTabs\(tabs, visibleTabs\)/);
  assert.match(tabs, /const tabs = filterMountedNavigationTabs\(plannedTabs, visibleTabs\)/);
  assert.match(
    tabs,
    /isManagedStoreTabId\(nextTabId\)[\s\S]*navigation\.onShowTab\(nextTabId\)[\s\S]*visibleDestination\.click/,
    "virtual tabs must use the translated navigation proxy instead of Steam's native DOM click handler",
  );
  assert.match(
    tabs,
    /if \(!visibleDestination\) \{[\s\S]*state\.forceRenderRequested = true;[\s\S]*scheduleXboxTabPatch\(\);[\s\S]*return true;[\s\S]*commitLibraryNavigationTarget\(nextTabId\)/,
    "a tab removed during navigation must trigger a repair instead of becoming an invisible destination",
  );
  assert.doesNotMatch(
    tabs,
    /else if \(typeof navigation\.onShowTab === "function"\) \{\s*navigation\.onShowTab\(nextTabId\)/,
    "unmounted tabs must never be selected through the stale React navigation callback",
  );
  assert.match(tabs, /state\.navigationRuntime\.activeTab = normalizedTabId/);
  assert.match(tabs, /function installLibraryBumperInput\(\)/);
  assert.match(tabs, /function uninstallLibraryBumperInput\(\)/);
  assert.match(tabs, /function activateFocusedLibraryTab\(\)/);
  assert.match(tabs, /function scheduleFocusedLibraryTabActivation\(\)/);
  assert.match(tabs, /function mountedLibraryTabLayoutNeedsPatch\(\)/);
  assert.match(
    tabs,
    /touchesLibraryTabs && mountedLibraryTabLayoutNeedsPatch\(\)[\s\S]*scheduleXboxTabPatch\(\)/,
  );
  assert.match(
    tabs,
    /ensureXboxTileObserver\(\);[\s\S]*installLibraryBumperInput\(\);[\s\S]*scheduleXboxTabPatch\(\);/,
  );
  assert.match(tabs, /document\.addEventListener\("focusin", state\.libraryTabFocusHandler, true\)/);
  assert.match(tabs, /numericButton === 11 \? -1 : numericButton === 12 \? 1 : 0/);
  assert.match(topology, /function buildCanonicalTabOrder/);
  assert.match(topology, /function getAdjacentTabId/);
  assert.match(topology, /function resolveActiveTabId/);
  assert.match(tabs, /tabs\.length > 128/);
  assert.match(tabs, /function buildSourceStoreSnapshot\(stores, sourceStoreId, now\)/);
  assert.match(tabs, /function scheduleLibraryTabReveal\(tabId\)/);
  assert.match(tabs, /function scheduleLibraryLayoutVerification\(resetAttempts = false\)/);
  assert.match(tabs, /function scheduleInitialLibraryPatchRetry\(\)/);
  assert.match(tabs, /maximumInitialPatchAttempts/);
  assert.match(tabs, /function pulseMountedNativeTabRoute\(\)/);
  assert.match(
    tabs,
    /runBatchedLibraryUpdate\(\(\) => \{[\s\S]*pulseTab\.click\?\.\(\);[\s\S]*selectedTab\.click\?\.\(\);/,
    "the initial compositor render must finish on the already-selected native tab",
  );
  assert.match(tabs, /layoutRenderPulseUsed/);
  assert.match(tabs, /bumperHeldFallbackMs = 700/);
  assert.match(tabs, /function holdLibraryBumper\(direction\)/);
  assert.match(tabs, /state\.heldBumperDirections\.has\(direction\)/);
  assert.match(tabs, /"vgp_onbuttonup",\s*state\.libraryBumperReleaseEventHandler/);
  assert.match(
    tabs,
    /if \(!getLiveLibraryNavigation\(\)\) \{[\s\S]*scheduleXboxTabPatch\(\);[\s\S]*\} else \{\s*navigateLibraryByDirection\(direction\);/,
    "the Library handler must repair a stale row instead of delegating bumper input to its invisible tab array",
  );
  assert.doesNotMatch(
    tabs,
    /mountedLibraryTabLayoutNeedsPatch\(\)[\s\S]{0,1200}selectedTab\.click\?\.\(\);\s*if \(preservedVirtualTabId\)/,
    "a selected-tab-only click does not wake Steam's function-component tab row",
  );
  assert.doesNotMatch(
    tabs,
    /function scheduleXboxTabPatch\(\)\s*\{\s*state\.forceRenderRequested = true/,
    "ordinary tab navigation must not turn every coalesced patch into a forced Library render",
  );
  assert.doesNotMatch(tabs, /\bpersist\b/);
  assert.match(tabs, /function recordLibraryRuntimeError\(scope, error\)/);
  assert.match(tabs, /Library tab render preparation/);
  assert.match(tabs, /Library navigation follow-up/);
  assert.match(
    tabs,
    /scrollIntoView\(\{ behavior: "auto", block: "nearest", inline: "center" \}\)/,
  );
});

test("ROM systems become native platform tabs only when imported games have Steam app ids", () => {
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const registry = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryStoreRegistry.cs");
  const systems = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryRomSystemRegistry.cs");
  const automation = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncAutomationService.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");

  assert.match(registry, /system\.GameCount > 0/);
  assert.match(registry, /system\.AppIds\.Any\(appId => appId != 0\)/);
  assert.match(systems, /return \$"tfs-emulation-/);
  assert.match(tabs, /startsWith\("platform:"\)/);
  assert.match(tabs, /platform\?\.appIds/);
  assert.match(tabs, /legacyManagedTabIds\.add\(definition\.tabId\)/);
  assert.match(automation, /watcher\.Error \+= OnWatcherError/);
  assert.match(automation, /ScheduleWatcherTrigger\("watch-recovery"/);
  assert.match(storeSync, /OmniLibraryRomSystemRegistry\.Supported\.Any/);
  assert.doesNotMatch(tabs, /state\.tileBadgeCount = activeBadges\.size;\s*refreshEmulatorSystemSurface\(\)/);
});

test("emulation settings keep global ROM controls above the per-system emulator sections", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const registry = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryRomSystemRegistry.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");

  assert.match(popup, /title: localLibrary \? "ROM Root Folder"/);
  assert.match(popup, /omnilibrary-rom-system-\$\{systemId\}/);
  assert.match(popup, /`Open \$\{system\.title\} ROM Folder`/);
  assert.match(popup, /"Start in Fullscreen"/);
  assert.match(popup, /api\/unifystore\/rom-system\/settings/);
  assert.match(popup, /api\/unifystore\/rom-system\/open-folder/);
  const catalog = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryEmulatorCatalog.cs");
  assert.match(registry, /"game-boy-advance"[^\n]*\["mgba", "retroarch"\]/);
  assert.match(registry, /"gamecube"[^\n]*\["dolphin", "retroarch"\]/);
  assert.match(registry, /"nintendo-64"[^\n]*\["project64", "ares", "retroarch"\]/);
  assert.match(registry, /"psp"[^\n]*\["ppsspp", "retroarch"\]/);
  assert.match(catalog, /"mgba",\s*"mGBA",\s*\["mGBA\.exe"\]/);
  assert.match(catalog, /"ares",\s*"ares",\s*\["ares\.exe"\]/);
  assert.match(catalog, /"ppsspp",\s*"PPSSPP",\s*\["PPSSPPWindows64\.exe"\]/);
  assert.match(popup, /api\/unifystore\/rom-system\/install-emulator/);
  assert.match(popup, /api\/unifystore\/emulation\/options/);
  assert.match(popup, /"Install Emulators Automatically"/);
  assert.match(api, /InstallOmniLibraryEmulator/);
  assert.match(api, /SetOmniLibraryRomSystemSettings/);
  assert.match(api, /OpenOmniLibraryRomSystemFolder/);
});

test("OmniLibrary overview links to one page per library with real store logos", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");

  assert.match(popup, /function collapseAllSectionsForRoute/);
  assert.match(popup, /enteringOmniLibrary[\s\S]*collapseAllSectionsForRoute\(route\)/);
  // Store logos are brand tiles with an alpha-mask glyph, not line drawings.
  for (const brand of ["xbox", "epic", "gog", "retro", "steam"]) {
    assert.match(popup, new RegExp(`\\n    ${brand}: \\{\\s*color: "#[0-9a-f]{6}",\\s*mask: "data:image/png;base64,`));
  }
  assert.match(popup, /function OmniLibraryXboxIcon\(\) \{\s*return renderOmniLibraryBrandMark\("xbox"\);/);
  assert.match(popup, /function OmniLibraryEpicIcon\(\) \{\s*return renderOmniLibraryBrandMark\("epic"\);/);
  assert.match(popup, /function OmniLibraryGogIcon\(\) \{\s*return renderOmniLibraryBrandMark\("gog"\);/);
  assert.match(popup, /function OmniLibraryEmulationIcon\(\) \{\s*return renderOmniLibraryBrandMark\("retro"\);/);
  assert.match(popup, /\.tfs-omni-brand-glyph \{[\s\S]*?mask-size: contain;/);
  assert.match(popup, /function OmniLibraryMetadataIcon/);
  assert.match(popup, /function OmniLibraryModeIcon/);

  // Overview: status, libraries, then downloads & extras, then maintenance.
  assert.match(popup, /createSectionHeader\(0, "Status"/);
  assert.match(popup, /createSectionHeader\(statusRows\.length, "Libraries"/);
  assert.match(popup, /createSectionHeader\(statusRows\.length \+ libraryRows\.length, "Downloads & Extras"/);
  assert.match(popup, /createSectionHeader\(statusRows\.length \+ libraryRows\.length \+ extraRows\.length, "Maintenance"/);
  assert.match(popup, /slots: \[\.\.\.statusRows, \.\.\.libraryRows, \.\.\.extraRows, \.\.\.maintenanceRows\]/);
  assert.match(popup, /slotKey: `omnilibrary-library-\$\{storeId\}`/);
  assert.match(popup, /rowClassName: `tfs-omni-library-row is-\$\{summary\.tone\}`/);
  assert.doesNotMatch(popup, /unifystore-xbox-accordion|unifystore-epic-accordion|unifystore-\$\{storeId\}-accordion/);

  // Each library, Downloads and Achievements & Metadata are their own pages,
  // and B returns focus to the row that opened them.
  assert.match(popup, /const omniLibraryLibraryPagePrefix = "library-"/);
  assert.match(popup, /\{ asPage: true \},\s*\)\.slice\(1\)/);
  assert.match(popup, /options\.asPage === true \|\| isExpandedSection\(segmentKey, false\)/);
  assert.match(popup, /options\.asPage === true \|\| isExpandedSection\(centerKey, false\)/);
  assert.match(
    popup,
    /if \(isOmniLibrarySectionRoute\(route\)\)[\s\S]*?route: parseRoute\("plugin:omnilibrary"\)[\s\S]*?`omnilibrary-library-\$\{storeId\}`[\s\S]*?"omnilibrary-downloads-entry"[\s\S]*?"omnilibrary-game-data-entry"/,
  );
  // Store pages group the existing controls by purpose.
  for (const group of ["Library", "Account", "Launch & Content", "Systems"]) {
    assert.match(popup, new RegExp(`title: "${group}",`));
  }
  assert.match(popup, /title: localLibrary \? "ROM Folder" : "Install & Downloads"/);
});

test("Steam's Play button is never touched; OmniLibrary downloads run from their own game page row", () => {
  const surface = read("src/SteamLoader.App/Assets/xbox-library-surface.js");
  const downloads = read("src/SteamLoader.App/Assets/download-core.js");
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");

  // The old surface is a retire stub: no polling, no relabelling, it only
  // puts Steam's own Play label back and removes old banners.
  assert.match(surface, /const stateVersion = 28;/);
  assert.match(surface, /retired: true/);
  assert.match(surface, /restoreSteamPlayLabels\(previousState\)/);
  assert.match(surface, /previousState\.currentGame = null;/);
  assert.doesNotMatch(surface, /fetch\(/);
  assert.doesNotMatch(surface, /label\.nodeValue = nextLabel/);
  assert.doesNotMatch(surface, /addEventListener\("click"/);

  // The download row lives in the shared context, after the HLTB row.
  assert.match(host, /Assets\/hltb-core\.js"\),[\s\S]*?Assets\/download-core\.js"\)/);
  assert.match(downloads, /const routePath = "\/library\/app\/:appid";/);
  assert.match(downloads, /tfs\.routes\.patch\(routePath, patchAppRoute, \{ id: patchId \}\)/);
  assert.match(downloads, /api\/unifystore\/games\/\$\{encodeURIComponent\(appId\)\}/);
  assert.match(downloads, /api\/unifystore\/games\/install/);
  assert.match(downloads, /api\/unifystore\/downloads\/action/);
  assert.match(downloads, /const activePollMs = 1000;/);
  assert.match(downloads, /appId >= 0x80000000/);
  assert.match(downloads, /summary\.pluginEnabled !== true/);
  assert.doesNotMatch(downloads, /nodeValue/);
  assert.doesNotMatch(downloads, /querySelectorAll/);
});

test("enhanced OmniLibrary game pages feed Steam native stores without replacing its details UI", () => {
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const service = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryGamePageMetadataService.cs",
  );
  const surface = read(
    "src/SteamLoader.App/Assets/omnilibrary-metadata-surface.js",
  );

  assert.match(host, /Assets\/omnilibrary-metadata-surface\.js/);
  assert.match(api, /api\/unifystore\/metadata\/games\//);
  assert.match(api, /TryNormalizeMetadataUrl/);
  assert.match(service, /GameInfoLifetime = TimeSpan\.FromDays\(7\)/);
  assert.match(service, /ActivityLifetime = TimeSpan\.FromHours\(6\)/);
  assert.match(service, /AchievementDefinitionLifetime = TimeSpan\.FromDays\(7\)/);
  assert.match(service, /AchievementProgressLifetime = TimeSpan\.FromHours\(6\)/);
  assert.match(service, /SourceMatchLifetime = TimeSpan\.FromDays\(30\)/);
  assert.match(service, /UnmatchedSourceMatchLifetime = TimeSpan\.FromHours\(6\)/);
  assert.match(service, /forceRefresh \|\|[\s\S]*sourceMatchLifetime/);
  assert.match(service, /displaycatalog\.mp\.microsoft\.com\/v7\.0\/products/);
  assert.match(service, /metadataSource = "Xbox"/);
  assert.match(service, /ConcurrentDictionary<string, Lazy<Task<CacheEntry>>>/);
  assert.match(service, /SemaphoreSlim _networkGate = new\(4, 4\)/);
  assert.match(service, /ComputeContentHash/);
  assert.match(service, /CreateInitialEntry/);
  assert.match(service, /CacheEntry PersistProgress\(\)/);
  assert.match(service, /refreshOnOpen/);
  assert.match(service, /existing\.ContentHash\.Equals\(entry\.ContentHash/);
  assert.match(service, /Cached data is kept/);
  assert.match(service, /TFS never presents Steam achievements as store unlocks/);
  assert.match(service, /steamcommunity\.com\/stats\/\{steamAppId\}\/achievements/);
  assert.match(service, /"definitions-only"/);
  assert.match(surface, /state\.summary\?\.pluginEnabled === true/);
  assert.match(surface, /isManagedAppId\(appId\)/);
  assert.match(surface, /library\\\/details/);
  assert.match(surface, /customimages\\\/\(\\d\+\)/);
  assert.match(surface, /native OmniLibrary metadata bridge/);
  assert.match(surface, /function installNativePatches\(\)/);
  assert.match(surface, /GetDescriptions/);
  assert.match(surface, /function ensureNativeDescriptions\(appId, snapshot, appData\)/);
  assert.match(surface, /function appIdFromNativeRequest\(value\)/);
  assert.match(surface, /function metadataAppIdForRequest\(value, context\)/);
  assert.match(surface, /function isManagedGameInfoRoute\(appId\)/);
  assert.match(surface, /function renderedGameDetails\(\)/);
  assert.match(surface, /function renderedManagedAppId\(\)/);
  assert.match(surface, /function patchRenderedGameDetails\(preferredAppId = 0/);
  assert.match(
    surface,
    /hasDetailsPage \? artworkAppId\(\) \|\| routeAppId\(\) : 0/,
  );
  assert.match(surface, /const updatingRefreshMs = 750/);
  assert.match(surface, /ToolsForSteamOmniLibraryMetadataSnapshotsV1/);
  assert.match(surface, /function loadPersistedNativeSnapshots\(\)/);
  assert.match(surface, /function persistNativeSnapshot\(snapshot\)/);
  assert.match(surface, /metadataGameUrl\(appId, !force && openingGame\)/);
  assert.match(surface, /open \? "\?open=true" : ""/);
  assert.match(
    surface,
    /Number\(trackedSnapshot\?\.sourceSteamAppId \|\| 0\) !== requestedAppId/,
  );
  assert.match(surface, /strSnippet: description/);
  assert.match(surface, /function ensureNativeAssociations\(appId, snapshot, appData\)/);
  assert.match(surface, /if \(!detailsStore \|\| !appStore\)/);
  assert.doesNotMatch(surface, /if \(!detailsStore \|\| !appStore \|\| !sectionsPrototype\)/);
  assert.match(surface, /window\.setTimeout\(reapply, 120\)/);
  assert.match(surface, /GetAssociations/);
  assert.match(surface, /GetAchievements/);
  assert.match(surface, /GetAppActivity/);
  assert.match(surface, /webpackChunksteamui/);
  assert.match(surface, /findDetailsSectionsPrototype/);
  assert.match(surface, /"GetSections"/);
  assert.match(surface, /nativeSections\.add\("activity"\)/);
  assert.match(surface, /nativeSections\.add\("community"\)/);
  assert.match(surface, /nativeSections\.add\("achievements"\)/);
  assert.match(surface, /nativeSections\.delete\("nonsteam"\)/);
  assert.match(surface, /nativeSections\.add\("info"\)/);
  assert.match(surface, /"BIsModOrShortcut"/);
  assert.match(surface, /isManagedGameInfoRoute\(appId\)/);
  assert.match(surface, /SetCachedDataForApp/);
  assert.match(surface, /m_mapAppActivity/);
  assert.match(surface, /m_achievementProgress/);
  assert.match(surface, /function buildNativeActivity\(appId, snapshot\)/);
  assert.match(surface, /function buildNativeAchievements\(snapshot\)/);
  assert.match(surface, /function findAchievementStore\(\)/);
  assert.match(surface, /"LoadMyAchievements"/);
  assert.match(surface, /m_mapInflightMyAchievementsRequests/);
  assert.match(surface, /restoreAchievementStoreState/);
  assert.match(surface, /function buildNativeCommunityPayload\(appId, snapshot\)/);
  assert.match(surface, /function buildNativeActivityFeedPayload\(appId, snapshot\)/);
  assert.match(surface, /window\.steamAjaxRequest/);
  assert.match(surface, /library\\\/appcommunityfeed/);
  assert.match(surface, /type: nativeMetadataMessageType/);
  assert.match(surface, /state\.channel\?\.postMessage/);
  assert.match(surface, /waitForNativeSnapshot/);
  assert.match(surface, /snapshotWaiters/);
  assert.match(surface, /RegisterForAppLifetimeNotifications/);
  assert.match(surface, /postPlayMinimumMs = 45000/);
  assert.match(surface, /postPlayThrottleMs = 10 \* 60 \* 1000/);
  assert.match(surface, /refreshMetadataForApp\(appId\)/);
  assert.match(surface, /function restoreOriginalState\(\)/);
  assert.match(surface, /uninstallNativePatches\(\)/);
  assert.match(surface, /removeLegacySurface\(\)/);
  assert.doesNotMatch(surface, /appendChild\(/);
  assert.doesNotMatch(surface, /insertBefore\(/);
  assert.doesNotMatch(surface, /data-omni-focusable/);
  assert.doesNotMatch(surface, /steamtools-omni-virtual-focus/);
  assert.doesNotMatch(surface, /suppressNativeContent/);
  assert.doesNotMatch(surface, /display:\s*none\s*!important/);
  assert.doesNotMatch(surface, /document\.addEventListener\("keydown"/);
  assert.match(surface, /button\.addEventListener\("keydown"/);
  assert.match(surface, /state\.refreshPending = true/);
  assert.match(surface, /currentManagedAppId\(\)/);
  assert.match(surface, /hasNativeDetailTabs\(\)/);
  assert.doesNotMatch(surface, /addEventListener\("gamepadbuttondown"/);
  assert.match(surface, /state\.dispose = \(\) =>/);
});

test("games without verified store achievements show one native-flow English notice", () => {
  const surface = read(
    "src/SteamLoader.App/Assets/omnilibrary-metadata-surface.js",
  );

  assert.match(surface, /function renderAchievementNotice\(\)/);
  assert.match(
    surface,
    /OmniLibrary cannot access verified achievements for this game\./,
  );
  assert.match(surface, /Achievements unavailable/);
  assert.match(surface, /achievementUnavailableDetail\(snapshot\)/);
  assert.match(surface, /unsupported-rom/);
  assert.match(surface, /RetroAchievements does not recognize this exact ROM revision/);
  assert.match(surface, /Retry/);
  assert.match(surface, /Open RetroAchievements/);
  assert.match(surface, /insertAdjacentElement\("afterend", notice\)/);
  assert.match(surface, /insertAdjacentElement\("beforebegin", notice\)/);
  assert.match(surface, /uninstallDetailsUiTracking\(\)/);
  assert.doesNotMatch(
    surface,
    /strName:\s*"Achievements unavailable"/,
  );
});

test("brand-new metadata requests show a focus-safe Steam-like loading state", () => {
  const surface = read(
    "src/SteamLoader.App/Assets/omnilibrary-metadata-surface.js",
  );

  assert.match(surface, /function createLoadingDots\(\)/);
  assert.match(surface, /OmniLibrary is preparing this game/);
  assert.match(
    surface,
    /Loading metadata and checking for verified achievements\./,
  );
  assert.match(surface, /state\.requestInFlight/);
  assert.match(surface, /scheduleMetadataUiRender\(0\)/);
  assert.match(surface, /prefers-reduced-motion: reduce/);
  assert.match(surface, /iterations: Number\.POSITIVE_INFINITY/);
  assert.doesNotMatch(surface, /tabIndex\s*=/);
});

test("game-data provider credentials use live drafts and encrypted generic storage", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const settings = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncSettingsStore.cs",
  );
  const provider = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryAchievementProvider.cs",
  );

  assert.match(popup, /function getOmniLibraryGameDataDraft\(provider\)/);
  assert.match(
    popup,
    /draft\.credentialDirty\s*\? draft\.credential\.trim\(\)/,
  );
  assert.match(popup, /api\/unifystore\/game-data\/providers/);
  assert.match(popup, /api\/unifystore\/game-data\/providers\/test/);
  assert.match(popup, /Test \$\{provider\.title\} Connection/);
  assert.match(popup, /ConnectionCheckedAtUtc|connectionCheckedAtUtc/);
  assert.match(popup, /"Achievements & Metadata"/);
  assert.match(api, /TestOmniLibraryGameDataProviderAsync/);
  assert.match(service, /API_GetUserProfile\.php/);
  assert.match(settings, /ConnectionStatus/);
  assert.match(settings, /ConnectionDetail/);
  assert.match(settings, /ProtectJsonSecret/);
  assert.match(settings, /gameData["']?\]?\?\[?["']providers|GameData\.Providers/);
  assert.match(settings, /OpenXblTitleIds/);
  assert.match(provider, /\/api\/v2\/player\/titleHistory\//);
  assert.match(provider, /PersistOpenXblTitleId/);
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

test("Epic-owned EA games use a validated short-lived handoff and honest provider states", () => {
  const integration = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/EaAppIntegration.cs",
  );
  const launcher = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs",
  );
  const service = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs",
  );
  const models = read("src/SteamLoader.App/Models/UnifySteamSnapshot.cs");
  const surface = read("src/SteamLoader.App/Assets/download-core.js");

  assert.match(integration, /OfficialDownloadUrl = "https:\/\/www\.ea\.com\/ea-app"/);
  assert.match(integration, /candidate\.Scheme\.Equals\(\s*"link2ea"/s);
  assert.match(integration, /candidate\.Host\.Equals\(\s*"launchgame"/s);
  assert.match(integration, /targetAppName\.Equals\(\s*appName\.Trim\(\)/s);
  assert.match(launcher, /"launch",\s*game\.Id,\s*"--origin",\s*"--json"/s);
  assert.match(launcher, /EaAppIntegration\.TryParseHandoffUri/);
  assert.match(launcher, /deliberately never persisted or logged/);
  assert.doesNotMatch(launcher, /AUTH_PASSWORD/);
  assert.match(service, /EaAppIntegration\.GetExternalAction/);
  assert.match(models, /string ExternalAction/);
  assert.match(surface, /game\?\.externalAction === "install-client"\) return "Install EA app"/);
  assert.match(surface, /game\?\.externalAction === "link-account"\) return "Link EA"/);
  assert.match(surface, /game\?\.externalAction === "continue-provider"\) return "Open EA app"/);
});

test("all managed stores expose durable phases and only terminal failures become Retry", () => {
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");
  const downloads = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamDownloadStatusStore.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const surface = read("src/SteamLoader.App/Assets/download-core.js");

  assert.match(downloads, /"preparing" or[\s\S]*"queued" or[\s\S]*"downloading" or[\s\S]*"reconnecting" or[\s\S]*"finalizing"/);
  assert.match(launcher, /foreach \(var storeId in new\[\][\s\S]*"xbox-game-pass"[\s\S]*"epic-games"[\s\S]*"gog-galaxy"/);
  assert.match(launcher, /ManagedGogDlHelper\.AuthPath/);
  assert.match(launcher, /Resuming GOG download \(attempt/);
  assert.match(launcher, /GOG download stopped after automatic resume attempts/);
  assert.match(launcher, /AssignDownloadWorkerIfUnclaimed/);
  assert.match(api, /AssignDownloadWorkerIfUnclaimed/);
  assert.match(surface, /status === "failed" \|\| status === "cancel-failed"[\s\S]*"Retry"/);
  assert.match(surface, /status === "action-required"[\s\S]*"Open Xbox"[\s\S]*"Open GOG"/);
  assert.match(surface, /game\?\.requiresAccountLink === true[\s\S]*"Link Ubisoft"/);
});

test("Download Center unifies concurrent stores with durable controller actions and safe cleanup", () => {
  const models = read("src/SteamLoader.App/Models/UnifySteamSnapshot.cs");
  const downloads = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamDownloadStatusStore.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const program = read("src/SteamLoader.App/Program.cs");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const surface = read("src/SteamLoader.App/Assets/download-core.js");
  const sleep = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryDownloadSleepBlocker.cs");

  assert.match(models, /OmniLibraryDownloadCenterSnapshot/);
  assert.match(models, /OmniLibraryDownloadCenterEntry/);
  assert.match(models, /string TransferOwner[\s\S]*bool ManagedByToolsForSteam[\s\S]*bool CanPause[\s\S]*bool CanResume[\s\S]*bool CanCancel[\s\S]*bool CanStopTracking[\s\S]*bool CanDismiss/);
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
  assert.match(api, /TryPrepareManagedDownloadCancellation/);
  assert.match(api, /case "stop-tracking" when entry\.CanStopTracking/);
  assert.match(api, /TryStopTrackingDownload/);
  assert.match(api, /case "dismiss" when entry\.CanDismiss/);
  assert.match(api, /case "manage" when entry\.CanManageExternally/);
  assert.match(api, /case "retry-uninstall" when entry\.CanRetryUninstall/);
  assert.match(launcher, /CancelDownloadArgument = "--unifysteam-cancel-download"/);
  assert.match(program, /UnifySteamLauncher\.CancelDownloadArgument/);
  assert.match(launcher, /process\.Kill\(entireProcessTree: true\)/);
  assert.match(launcher, /ShouldAbortPendingDownloadWorker/);
  assert.match(launcher, /"tracking-stopped"/);
  assert.match(launcher, /"--keep-files"/);
  assert.match(launcher, /"--skip-uninstaller"/);
  assert.match(launcher, /DeleteContainedDownloadDirectory/);
  assert.match(launcher, /normalizedTarget\.StartsWith\([\s\S]*containedPrefix/s);
  assert.match(launcher, /FileAttributes\.ReparsePoint/);
  assert.match(popup, /buildOmniLibraryDownloadCenterSlots/);
  // The overview shows a Downloads entry; the full list is its own page.
  assert.match(popup, /slotKey: "omnilibrary-downloads-entry"/);
  assert.match(popup, /title: "Downloads",[\s\S]*?slots: buildOmniLibraryDownloadCenterSlots\(/);
  assert.match(popup, /createOmniLibraryIdleDownloadSlot/);
  assert.match(popup, /"Waiting for download"/);
  assert.match(popup, /leadingIcon: OmniLibraryDownloadIcon/);
  assert.match(popup, /"Pause Download"/);
  assert.match(popup, /"Resume Download"/);
  assert.match(popup, /"Cancel & Delete Partial Files"/);
  assert.match(popup, /"Force Stop & Clean Up"/);
  assert.match(popup, /"Stop Tracking"/);
  assert.match(popup, /"Cancel All TFS Downloads"/);
  assert.match(popup, /Managed by \$\{entry\?\.transferOwner/);
  assert.match(popup, /`Cancel in \$\{transferOwner\}`/);
  assert.match(popup, /"Remove from Download Center"/);
  assert.match(popup, /"Retry Uninstall"/);
  assert.match(popup, /downloadActionKeys/);
  assert.match(popup, /download-status-changed/);
  assert.match(popup, /AbortController/);
  assert.match(popup, /downloadCenterRefreshQueued/);
  assert.match(popup, /hasActiveDownload \? 2000 : 10000/);
  assert.match(surface, /const activeStatuses = \[[^\]]*"canceling"\]/);
  assert.match(surface, /case "canceling":/);
  assert.match(sleep, /IsBusyOperation/);
});

test("OmniLibrary hot paths use cached lightweight state and batched download status", () => {
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const unifySteam = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const downloads = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamDownloadStatusStore.cs");
  const artwork = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryArtworkPipeline.cs");
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
  assert.match(
    automation,
    /if \(!ShouldRunWatchers\(storeSyncEnabled, omniLibraryEnabled\)\)[\s\S]*ClearWatchers\(\)/,
  );
  assert.match(automation, /Task\.Delay\(TimeSpan\.FromSeconds\(5\), cancellationToken\)/);
  assert.match(controller, /PollInterval = TimeSpan\.FromMilliseconds\(25\)/);
});

test("OmniLibrary reconciles install and uninstall lifecycle deltas promptly", () => {
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const artwork = read("src/SteamLoader.App/Assets/artwork-surface.js");
  const providerTracker = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/ProviderInstallStateTracker.cs",
  );
  const automation = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncAutomationService.cs",
  );
  const storeSync = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs",
  );

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
  assert.match(
    providerTracker,
    /return includeIdle \|\| IsOperationRelevant\(status\)/,
  );
  assert.match(providerTracker, /game\.ProviderGameId = installedGame\.Id/);
  assert.match(
    automation,
    /\["gog-galaxy"\] = \["gog-galaxy", "epic-games"\]/,
  );
  assert.match(storeSync, /includeClassicStore[\s\S]*includeOmniStore/);
  assert.match(automation, /"omnilibrary-shortcuts-watch"/);
  assert.match(storeSync, /WatchersActive = _automationWatchersActive/);
  assert.match(tabs, /applyConfirmedInstalledLifecycleDeltas/);
});

test("disabling OmniLibrary removes its owned UI and standalone tab runtime", () => {
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
  assert.match(tabs, /if \(!isLibraryTabRuntimeEnabled\(\) \|\| !direction\)/);
  assert.match(tabs, /if \(!runtimeActive\)\s*\{\s*setVirtualActiveTabId\("", true\)/s);
  assert.match(tabs, /function getEnabledStoreDefinitions\(\)\s*\{\s*if \(!state\.pluginEnabled\)/s);
  assert.match(tabs, /const omniLibraryRuntimeActive =\s*state\.pluginEnabled && getEnabledStoreDefinitions\(\)\.length > 0/);
  assert.match(
    tabs,
    /omniLibraryRuntimeActive &&[\s\S]*\(libraryStateChanged \|\| !state\.downloadRefreshTimer\)[\s\S]*scheduleDownloadStateRefresh\(0\)/,
  );
  assert.match(
    tabs,
    /function isLibraryTabRuntimeEnabled\(\) \{\s*return getEnabledVirtualDefinitions\(\)\.length > 0 \|\| isTabHeroEnabled\(\);\s*\}/,
  );
  // Tab Hero is part of OmniLibrary: disabling the plugin also disables it.
  assert.match(tabs, /function isTabHeroEnabled\(\)\s*\{[\s\S]*?state\.pluginEnabled === true[\s\S]*?\n  \}/);
  assert.match(tabs, /state\.activationResolved = true/);
  assert.match(tabs, /getNativeNavigationHandler\(props\.onShowTab\)/);
  assert.match(tabs, /backendUnavailable: true/);
  assert.match(tabs, /shared\.snapshot = unavailableSnapshot/);
  assert.match(api, /SetOmniLibraryPluginRuntimeEnabled/);
  assert.match(api, /SetStoreSyncPluginRuntimeEnabled/);
  assert.match(storeSync, /CancelOmniLibraryBackgroundWork\(storeId\)/);
  assert.match(storeSync, /activeSyncCancellation\?\.Cancel\(\)/);
  assert.match(storeSync, /cancellation\.Cancel\(\)/);
  assert.match(storeSync, /DownloadLocalFirstAsync\([\s\S]*cancellationToken/s);
  assert.match(storeSync, /catch \(OperationCanceledException\) when \(cancellationToken\.IsCancellationRequested\)/);
  assert.doesNotMatch(
    tabs,
    /installLibraryBumperInput\(\);\s*ensureXboxTileObserver\(\);\s*patchXboxTabBasis\(\);\s*void refreshXboxAppIds\(true\)/s,
  );
  assert.match(surface, /retired: true/);
  assert.match(read("src/SteamLoader.App/Assets/download-core.js"), /summary\.pluginEnabled !== true\) return false/);
  assert.match(artwork, /snapshot\?\.pluginEnabled !== true/);
  assert.match(artwork, /removeOmniLibraryUninstallContextRows\(\)/);
});

test("Tab Hero lives inside OmniLibrary instead of being a separate plugin", () => {
  const catalog = read("src/SteamLoader.App/Infrastructure/Settings/SteamLoaderPluginCatalog.cs");
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");

  // No own plugin entry: Tab Hero is switched on and off with OmniLibrary.
  assert.doesNotMatch(catalog.toLowerCase(), /tabhero/);
  // The engine is loaded before both consumers in each injected bundle.
  assert.match(
    host,
    /ReadText\("Assets\/tabhero-engine\.js"\)[\s\S]*?ReadText\("Assets\/quickaccess-popup\.js"\)/,
  );
  assert.match(
    host,
    /ReadText\("Assets\/tabhero-engine\.js"\)[\s\S]*?ReadText\("Assets\/omnilibrary-tab-topology\.js"\)[\s\S]*?ReadText\("Assets\/library-tabs\.js"\)/,
  );
  // One compositor: library-tabs.js builds store tabs and Tab Hero tabs together.
  assert.match(tabs, /const tabHero = window\.__steamLoaderTabHero/);
  assert.match(tabs, /state\.tabMasterOwnsLayout !== true/);
  assert.match(tabs, /isTabHeroEnabled\(\)\s*\?\s*tabHero\.composeTabs\(combinedTabs\)/);
  assert.match(tabs, /tabHero\.composeTabs\(omniLibraryTabs, \{ observe: false \}\)/);
  assert.match(tabs, /state\.tabHeroUnsubscribe = tabHero\.subscribe\(/);
  assert.doesNotMatch(tabs, /localStorage\.removeItem\("ToolsForSteamTabheroSettings/);
  // Neither script may dispose the engine or wipe its saved layout any more.
  for (const source of [tabs, popup]) {
    assert.doesNotMatch(source, /heroSettings\.v1|heroCatalog\.v1|"Tab", "Hero"/);
  }
  // The UI is the "Library Tabs" page inside the OmniLibrary plugin.
  assert.match(popup, /const tabHeroPagePrefix = "tabs"/);
  assert.match(popup, /slotKey: "omnilibrary-tabhero-entry"/);
  assert.match(popup, /\.\.\.\(tabHeroEngine \? \[buildTabHeroEntrySlot\(\)\] : \[\]\)/);
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

test("OmniLibrary achievement getters stay read-only and snapshots apply by revision", () => {
  const surface = read(
    "src/SteamLoader.App/Assets/omnilibrary-metadata-surface.js",
  );
  const getterPatch = surface.match(
    /for \(const methodName of \["GetMyAchievements", "GetGlobalAchievements"\]\)([\s\S]*?)\n {4}\}/,
  )?.[1] || "";

  assert.doesNotMatch(getterPatch, /primeAchievementStores/);
  assert.match(surface, /achievementPrimedStores: new WeakMap\(\)/);
  assert.match(surface, /applied\?\.revision === revision/);
  assert.match(surface, /currentUser === storePayload\.user/);
});

test("managed stores use one isolated authorization runtime", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const login = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryLoginRuntime.cs");
  const xboxAuth = read("src/SteamLoader.App/Infrastructure/StoreSync/XboxLiveAuthService.cs");

  assert.match(popup, /\["xbox-game-pass", xboxSlots\],\s*\["epic-games", epicSlots\]/);
  assert.match(popup, /Enable Epic Games/);
  assert.match(popup, /Sign in to Epic/);
  assert.match(popup, /Sign in to Xbox/);
  assert.match(popup, /enables Xbox achievement progress/);
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
  assert.match(login, /XboxLiveAuthService\.CompleteLoginAsync/);
  assert.match(login, /CookieManager\.DeleteAllCookies/);
  assert.match(xboxAuth, /code_challenge_method/);
  assert.match(xboxAuth, /XboxLive\.offline_access/);
  assert.doesNotMatch(xboxAuth, /client_secret/);
});

test("OmniLibrary renders large libraries incrementally and sleeps hidden surfaces", () => {
  const overlay = read("src/SteamLoader.App/Assets/unifystore-overlay.js");
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");

  assert.match(overlay, /initialGameRenderCount = 60/);
  assert.match(overlay, /games\.slice\(0, visibleCount\)/);
  assert.match(overlay, /action === "show-more"[\s\S]*revealMoreGames/);
  assert.match(overlay, /function scheduleOverlayStatePoll\(/);
  assert.doesNotMatch(overlay, /state\.pollTimer = window\.setInterval\(pollOverlayState/);
  assert.match(
    overlay,
    /if \(!state\.open && state\.gamepadTimer\)[\s\S]*clearInterval\(state\.gamepadTimer\)/,
  );
  assert.match(overlay, /clearInterval\(state\.stateBroadcastTimer\)/);
  assert.match(popup, /type: "overlay-open-request"[\s\S]*source: "unifystore"/);
  assert.match(tabs, /const libraryStateChanged =/);
  assert.match(
    tabs,
    /libraryStateChanged \|\|[\s\S]*recoveredTabState \|\|[\s\S]*!state\.navigationRuntime/,
  );
  assert.match(
    tabs,
    /document\.visibilityState !== "hidden"[\s\S]*ensureXboxTileObserver\(\)[\s\S]*else \{[\s\S]*disableActiveRuntimeTimers\(\)[\s\S]*suspendXboxTileObserver\(\)/,
  );
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
  assert.match(popup, /genericStoreDetailSlots/);
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

test("OmniLibrary artwork is local-first and keeps SteamGridDB as the final fallback", () => {
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const integrations = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const artwork = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryArtworkPipeline.cs");
  const localArtwork = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryLocalArtworkResolver.cs",
  );
  const discovery = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryArtworkDiscovery.cs",
  );

  assert.match(storeSync, /QueueOmniLibraryArtworkSync/);
  assert.match(storeSync, /_activeOmniArtworkTask/);
  assert.match(storeSync, /_pendingOmniArtworkTargets/);
  assert.match(storeSync, /_activeOmniArtworkTargetIds/);
  assert.match(storeSync, /ProcessQueuedOmniLibraryArtworkAsync/);
  assert.match(storeSync, /DownloadLocalFirstAsync/);
  assert.match(storeSync, /must not[\s\S]*move the public preparation state backwards/);
  assert.match(artwork, /class OmniLibraryArtworkPipeline/);
  assert.doesNotMatch(integrations, /steamgriddb\.com/);
  assert.match(discovery, /store\.steampowered\.com\/api\/storesearch/);
  assert.match(discovery, /cdn\.cloudflare\.steamstatic\.com/);
  assert.match(discovery, /library_600x900_2x\.jpg/);
  assert.match(discovery, /library_hero\.jpg/);
  assert.match(localArtwork, /logo_2x\.png/);
  assert.match(artwork, /DownloadArtworkSetAsync\(/);
  assert.match(artwork, /DownloadRetroAchievementsArtworkSetAsync/);
  assert.match(artwork, /API_GetGame\.php/);
  assert.match(artwork, /ImageBoxArt/);
  assert.match(artwork, /ImageIngame/);
  assert.match(artwork, /ImageIcon/);
  assert.match(artwork, /HasCompleteArtworkSet/);
  assert.match(artwork, /GenerateTitleLogo/);
  assert.match(artwork, /ImportLocalArtworkSetAsync/);
  assert.match(artwork, /DownloadRemoteStoreArtworkSetAsync/);
  assert.match(localArtwork, /appcache[\s\S]*librarycache/);
  assert.match(localArtwork, /appmanifest_\*\.acf/);
  assert.match(localArtwork, /AddInstallArtwork/);
  assert.match(localArtwork, /AddRomSidecarArtwork/);
  const localStage = artwork.indexOf("// Stage 1:");
  const providerStage = artwork.indexOf("// Stage 2:");
  const steamStage = artwork.indexOf("// Stage 3:");
  const steamGridDbStage = artwork.indexOf("// Stage 4:");
  assert.ok(localStage >= 0 && localStage < providerStage);
  assert.ok(providerStage < steamStage);
  assert.ok(steamStage < steamGridDbStage);
  assert.match(artwork, /MaximumArtworkBytes = 32L \* 1024 \* 1024/);
  assert.match(artwork, /CopyArtworkWithLimitAsync/);
  assert.match(artwork, /IsUsableArtworkFile/);
  assert.match(artwork, /File\.Move\(stagingPath, targetPath, overwrite: true\)/);
  assert.match(artwork, /IsRetroAchievementsHost/);
  assert.match(artwork, /RetroAchievementsGameId/);
  assert.match(artwork, /Deliberately sequential/);
});

test("RetroAchievements hashing is deduplicated, bounded, and cancellation-safe", () => {
  const hasher = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/ManagedRetroAchievementsHasher.cs",
  );
  const source = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryRetroAchievementsSource.cs",
  );
  const service = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs",
  );

  assert.match(hasher, /HashCache/);
  assert.match(hasher, /HashGate/);
  assert.match(hasher, /MaximumCachedHashes = 2048/);
  assert.match(hasher, /MaximumArchiveBytes/);
  assert.match(hasher, /CopyToWithLimitAsync/);
  assert.match(hasher, /catch \(OperationCanceledException\)[\s\S]*TryKill\(process\)/);
  assert.match(source, /internal static bool TryResolveCachedHashMapping/);
  assert.match(service, /ResolveRetroAchievementsArtworkGameId/);
});

test("differential five-minute checks cover every enabled store with isolated backoff", () => {
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const coordinator = read(
    "src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryRefreshCoordinator.cs",
  );
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const unifySteam = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");

  assert.match(host, /new OmniLibraryRefreshCoordinator/);
  assert.match(coordinator, /LocalStartupDelay = TimeSpan\.FromSeconds\(2\)/);
  assert.match(coordinator, /RemoteStartupDelay = TimeSpan\.FromSeconds\(20\)/);
  assert.match(coordinator, /CatalogCheckInterval = TimeSpan\.FromMinutes\(5\)/);
  assert.match(coordinator, /GetEnabledUnifySteamStoreIds\(\)/);
  assert.match(coordinator, /CheckUnifySteamStoreForChanges\(storeId\)/);
  assert.match(coordinator, /ComputeFailureBackoff/);
  assert.match(coordinator, /Dictionary<string, StoreSchedule>/);
  // Stores run independently; watcher checks share the schedule and every
  // check waits while a game is running.
  assert.match(coordinator, /Dictionary<string, Task<StoreCheckOutcome>>/);
  assert.match(coordinator, /public void RequestCheck\(string storeId, string trigger\)/);
  assert.match(coordinator, /var paused = IsPausedForGame\(gameActive, now, resumeAtUtc\)/);
  assert.doesNotMatch(coordinator, /Task\.WhenAll/);
  assert.match(
    host,
    /omniLibraryRefreshCoordinator\.RequestCheck,\s*omniLibraryInstallStateCoordinator\.RequestCheck\)/,
  );
  assert.match(
    read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs"),
    /public static int Run\(string target\)\s*\{[\s\S]{0,200}OmniLibraryGameActivity\.EnterGameSession\(\)/,
  );
  assert.match(service, /OmniLibraryStoreGateSet/);
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
  const surface = read("src/SteamLoader.App/Assets/download-core.js");

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
  assert.match(
    service,
    /XboxCatalogShapeVersion =\s*"xbox-catalog-v3-console-cloud-title-id"/,
  );
  assert.match(service, /ResolveXboxTitleId\(product\)/);
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

test("OmniLibrary repairs only missing artwork and keeps provider images ahead of SteamGridDB", () => {
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const artwork = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryArtworkPipeline.cs");
  const settings = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncSettingsStore.cs");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");

  assert.match(storeSync, /BuildIncompleteOmniLibraryArtworkTargets/);
  assert.match(storeSync, /!OmniLibraryArtworkPipeline\.HasCompleteArtworkSet\(/);
  assert.match(storeSync, /analysisTargets\s*\.Concat\(cachedRepairTargets\)/s);
  assert.match(storeSync, /Artwork is optional cache data and must never hide Xbox/);
  assert.match(storeSync, /MarkOmniLibraryArtworkRepairPending/);
  assert.match(storeSync, /GetMissingArtworkSlots/);
  assert.match(storeSync, /optional artwork remains incomplete/);
  assert.match(settings, /OmniLibrarySettingsVersion < 2/);
  assert.match(settings, /wasBlockedOnlyByArtwork/);
  assert.match(artwork, /DownloadRemoteStoreArtworkSetAsync/);
  assert.match(artwork, /SteamGridDB is intentionally last/);
  // ORBIT rule: a portrait may fall back to wide provider art, never the reverse.
  assert.match(artwork, /\[OmniLibraryArtworkOrientation\.Capsule\] = \[heroUrl\]/);
  assert.match(artwork, /\[OmniLibraryArtworkOrientation\.Portrait\] = \[portraitUrl\]/);
  assert.match(artwork, /fallbackCandidates[\s\S]*\[OmniLibraryArtworkOrientation\.Portrait\] = \[heroUrl\]/);
  assert.match(artwork, /PromoteStoreFallbackArtwork/);
  assert.doesNotMatch(popup, /artwork repair pending/);
});

test("managed OmniLibrary games expose controller-safe metadata correction", () => {
  const surface = read("src/SteamLoader.App/Assets/artwork-surface.js");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const metadata = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryGamePageMetadataService.cs");

  assert.match(surface, /Fix Game Data\.\.\./);
  assert.match(surface, /api\/unifystore\/metadata\/search/);
  assert.match(surface, /metadata\/games\/\$\{encodeURIComponent\(state\.appId\)\}\/match/);
  assert.match(surface, /Use Automatic Match/);
  assert.match(surface, /Apply Match/);
  assert.match(api, /SearchSteamMatchesAsync/);
  assert.match(api, /SetSteamMatchAsync/);
  assert.match(api, /ClearSteamMatchAsync/);
  assert.match(metadata, /SteamAppIdOverrides/);
  assert.match(metadata, /GetSteamLocale/);
});

test("TFS Artworks ranks image quality and explains the recommended format", () => {
  const surface = read("src/SteamLoader.App/Assets/artwork-surface.js");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const service = read(
    "src/SteamLoader.App/Infrastructure/Artwork/SteamGridDbManualArtworkService.cs",
  );
  const models = read("src/SteamLoader.App/Models/ArtworkModels.cs");

  assert.match(surface, /TFS Artworks\.\.\./);
  assert.match(surface, /function getArtworkQuality\(/);
  assert.match(surface, /label: "High quality"/);
  assert.match(surface, /label: "Low quality"/);
  assert.match(surface, /recommended: "600 x 900"/);
  assert.match(surface, /recommended: "1920 x 620"/);
  assert.match(surface, /best quality first/);
  assert.match(surface, /steamtools-artwork-quality-badge/);
  assert.match(surface, /steamtools-artwork-shell is-artwork-mode/);
  assert.match(surface, /steamtools-artwork-command-bar/);
  assert.match(surface, /grid-template-rows: auto auto minmax\(0, 1fr\)/);
  assert.match(surface, /\.is-artwork-mode \.steamtools-artwork-asset-meta[\s\S]*position: absolute/);
  assert.match(surface, /steamtoolsArtworkVersion/);
  assert.match(surface, /existingStyle\?\.remove\(\)/);
  assert.match(surface, /assetRequestId/);
  assert.match(surface, /createAnimatedArtworkPreview/);
  assert.match(surface, /document\.createElement\("video"\)/);
  assert.match(surface, /video\.autoplay = true/);
  assert.match(surface, /video\.loop = true/);
  assert.match(surface, /video\.muted = true/);
  assert.match(surface, /video\.playsInline = true/);
  assert.match(surface, /steamtools-artwork-motion-badge/);
  assert.match(surface, /disconnectAnimatedPreviewObserver/);
  assert.match(service, /types=static,animated/);
  assert.match(service, /epilepsy=false/);
  assert.match(service, /IsAnimatedPreview\(asset\.Thumb\)/);
  assert.match(service, /sourceExtension\.Equals\("\.webp"/);
  assert.match(models, /bool IsAnimated/);
  assert.match(surface, /\^SP BPM\(\?:_\|\$\)/);
  assert.match(surface, /document\.title === "Steam Big Picture Mode"/);
  assert.match(popup, /title: "TFS Artworks"/);
  assert.match(popup, /quality-ranked images/);
});

test("OmniLibrary exposes a confirmed safe reload for every managed artwork set", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const downloader = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryArtworkPipeline.cs");

  assert.match(popup, /Reload All Artwork/);
  assert.match(popup, /Confirm Reload All Artwork/);
  assert.match(popup, /api\/unifystore\/artwork\/reload-all/);
  assert.match(api, /ReloadAllOmniLibraryArtwork/);
  assert.match(service, /includeComplete: true/);
  assert.match(service, /forceReload: true/);
  assert.match(downloader, /\.tfs-artwork-reload-/);
  assert.match(downloader, /PromoteReloadedArtworkSet/);
});

test("OmniLibrary keeps Steam's hot paths cheap while it is enabled", () => {
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  const surface = read("src/SteamLoader.App/Assets/xbox-library-surface.js");
  const metadata = read("src/SteamLoader.App/Assets/omnilibrary-metadata-surface.js");
  const automation = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncAutomationService.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const service = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamService.cs");
  const launcher = read("src/SteamLoader.App/Infrastructure/StoreSync/UnifySteamLauncher.cs");

  // The game page row is part of Steam's React tree: no DOM scans, no
  // MutationObserver, no layout reads; the retired surface does no work.
  const downloadRow = read("src/SteamLoader.App/Assets/download-core.js");
  assert.doesNotMatch(downloadRow, /querySelectorAll|MutationObserver|getBoundingClientRect/);
  assert.doesNotMatch(surface, /MutationObserver|setTimeout\(/);

  // Page answers are shared within a frame and dropped on DOM or input changes.
  assert.match(metadata, /memoizedDomQuery\("native-detail-tabs", detectNativeDetailTabs\)/);
  assert.match(metadata, /memoizedDomQuery\("rendered-game-details", readRenderedGameDetails\)/);
  assert.match(metadata, /new MutationObserver\(\(mutations\) => \{\s*invalidateDomQueries\(\);/);

  // Tab rendering and controller input avoid full-library and full-style work.
  assert.match(tabs, /entry\.inputKey !== inputKey/);
  assert.match(tabs, /collectionHasExactAppIds\(entry\.collection, appIds\)/);
  assert.doesNotMatch(tabs, /const currentSignature = getCollectionAppIds\(entry\.collection\)/);
  assert.match(tabs, /if \(!hasFocusedTabCandidate\(\)\) \{\s*return null;/);
  assert.match(tabs, /element\.checkVisibility\(\{/);
  assert.match(
    tabs,
    /state\.heldBumperDirections\.has\(direction\)\s*\)\s*\{\s*return true;\s*\}\s*const navigation = getLiveLibraryNavigation\(\);/,
  );

  // Store tabs are composed at Steam start and on every Library mount without
  // walking Steam's whole React tree.
  assert.match(tabs, /function findLibraryTabNodesFromDom\(\)/);
  assert.match(
    tabs,
    /const domSearch = findLibraryTabNodesFromDom\(\);[\s\S]*if \(!libraryNodes\.length && domSearch\.tabListCount > 0\)/,
  );
  assert.match(
    tabs,
    /touchesLibraryTabs && mountedLibraryTabLayoutNeedsPatch\(\)\) \{[\s\S]{0,700}state\.layoutVerificationResetRequested = true;/,
  );
  assert.match(tabs, /const summaryCacheStorageKey = "ToolsForSteamOmniLibrarySummaryCacheV1"/);
  assert.match(tabs, /return \{ \.\.\.snapshot, revision: -1, cached: true \};/);
  assert.match(tabs, /void refreshXboxAppIds\(false, true\);/);
  assert.match(tabs, /function scheduleSummaryRetry\(\)/);

  // Display-only sync health stays in memory instead of rewriting the
  // catalog file, and actions validate against the cached OmniLibrary state.
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  assert.doesNotMatch(
    storeSync,
    /private void RecordOmniLibrarySyncHealth\([\s\S]{0,400}_settingsStore\.Update/,
  );
  assert.match(storeSync, /ApplyOmniLibrarySyncHealth\(\s*_unifySteamService\.BuildSnapshot\(configuration, \[\]\)\)/);
  assert.doesNotMatch(api, /var snapshot = _storeSyncService\.GetSnapshot\(\);\s*var storeState = snapshot\.UnifySteam\.Stores/);

  // OmniLibrary only ever takes over shortcuts that carry its own mark.
  assert.match(storeSync, /IsRecognizableOmniLibraryShortcut\(entry\)\)\s*\.ToArray\(\);\s*existingShortcut = legacyMatches\.Length == 1/);
  assert.doesNotMatch(storeSync, /: titleMatches\.Length == 1\s*\? titleMatches\[0\]/);

  // Launcher folders are watched shallowly for OmniLibrary and cannot start
  // a network catalog refresh on every write.
  assert.match(storeSync, /var watchGameFolders = includeClassicStore;/);
  assert.match(automation, /ShouldRunOmniLibraryWatchCheck\(lastCheckAtUtc, now\)/);
  assert.match(automation, /if \(!_lastWatcherEventAtUtc\.HasValue\)\s*\{\s*_storeSyncService\.InvalidateAutomaticSyncState\(\);/);
  assert.match(service, /TryGetRegisteredXboxAppxGame\(/);

  // Steam's Play button never starts a download, update or store install.
  assert.doesNotMatch(launcher, /HandOffInstallToWorker/);
  assert.match(launcher, /internal static int DeclineLaunchUntilInstalled\(string storeId, string gameId\)/);

  // Hidden workers never block on a modal dialog and never leave a transfer
  // looking active after an unexpected failure.
  for (const entryPoint of ["Install", "Repair", "Uninstall", "CancelDownload"]) {
    assert.match(
      launcher,
      new RegExp(`public static int ${entryPoint}\\(string target\\)\\s*\\{\\s*EnterBackgroundWorkerMode\\(\\);`),
    );
  }
  assert.match(launcher, /if \(s_backgroundWorker\)\s*\{\s*WriteWorkerErrorLog\(message\);\s*return;/);
  assert.match(launcher, /MarkWorkerOperationFailed\(\s*storeId,\s*gameId,\s*\$"The download stopped unexpectedly/);
  assert.match(
    launcher,
    /waitingStatus == "queued" &&\s*ShouldAbortPendingDownloadWorker\(storeId, gameId\)\)\s*\{\s*return 0;/,
  );
  assert.doesNotMatch(launcher, /return InstallEpic\(appName\);/);
  assert.doesNotMatch(launcher, /return InstallGog\(gameId\);/);
  assert.doesNotMatch(launcher, /return InstallXbox\(productId\);/);
});

test("live shortcut sync never creates a second shortcut for an OmniLibrary identity", async () => {
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const start = storeSync.indexOf("private static string BuildLiveShortcutSyncExpression");
  const bodyStart = storeSync.indexOf('$$"""', start) + 5;
  const body = storeSync.slice(bodyStart, storeSync.indexOf('""";', bodyStart));
  const expression = new Function(
    "window",
    "plan",
    `return ${body.replace("const plan = {{planJson}};", "").trim()};`,
  );
  const identity = "--unifysteam-launch epic-games:hades";
  const operation = {
    titleId: "epic|hades",
    targetAppId: 0,
    name: "Hades",
    executablePath: "C:\\TFS\\ToolsForSteam.exe",
    startDirectory: "C:\\TFS",
    launchOptions: identity,
  };
  const run = async (plan, shortcuts) => {
    const calls = [];
    let nextAppId = 9000;
    const apps = {
      GetAllShortcuts: async () =>
        shortcuts.map((shortcut) => ({
          appid: shortcut.appid,
          data: { strLaunchOptions: shortcut.launch },
        })),
      AddShortcut: async (_name, _exe, launch) => {
        const appid = nextAppId++;
        shortcuts.push({ appid, launch });
        calls.push(`add:${appid}`);
        return appid;
      },
      RemoveShortcut: async (appid) => {
        calls.push(`remove:${appid}`);
        const index = shortcuts.findIndex((shortcut) => shortcut.appid === appid);
        if (index >= 0) {
          shortcuts.splice(index, 1);
        }
      },
      SetShortcutName: async (appid) => {
        if (!shortcuts.some((shortcut) => shortcut.appid === appid)) {
          throw new Error(`missing ${appid}`);
        }
      },
      SetShortcutExe: async () => {},
      SetShortcutStartDir: async () => {},
      SetShortcutLaunchOptions: async () => {},
    };
    const result = await expression({ SteamClient: { Apps: apps } }, plan);
    return { result, calls, remaining: shortcuts.map((shortcut) => shortcut.appid) };
  };

  // A retry after a partly failed attempt finds the shortcut it already made,
  // and an existing duplicate with the same identity is removed.
  const reused = await run(
    { createOperations: [operation], updateOperations: [], removeOperations: [] },
    [{ appid: 100, launch: identity }, { appid: 101, launch: identity }, { appid: 200, launch: "" }],
  );
  assert.equal(reused.result.success, true);
  assert.equal(reused.result.appIdsByTitleId[operation.titleId], 100);
  assert.deepEqual(reused.calls, ["remove:101"]);
  assert.deepEqual(reused.remaining, [100, 200]);

  // A genuinely missing game is created exactly once.
  const created = await run(
    { createOperations: [operation], updateOperations: [], removeOperations: [] },
    [{ appid: 200, launch: "" }],
  );
  assert.deepEqual(created.calls, ["add:9000"]);

  // A refresh whose recorded app id vanished reuses the identity Steam holds.
  const refreshed = await run(
    {
      createOperations: [],
      updateOperations: [{ ...operation, appId: 555, forceCreate: false }],
      removeOperations: [],
    },
    [{ appid: 100, launch: identity }],
  );
  assert.equal(refreshed.result.appIdsByTitleId[operation.titleId], 100);
  assert.ok(!refreshed.calls.some((call) => call.startsWith("add:")));
});

test("OmniLibrary sign-in cannot hang and Sign In never blocks on catalog preparation", () => {
  const login = read("src/SteamLoader.App/Infrastructure/StoreSync/OmniLibraryLoginRuntime.cs");
  const storeSync = read("src/SteamLoader.App/Infrastructure/StoreSync/StoreSyncService.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const legendary = read("src/SteamLoader.App/Infrastructure/StoreSync/ManagedLegendaryHelper.cs");
  const gogdl = read("src/SteamLoader.App/Infrastructure/StoreSync/ManagedGogDlHelper.cs");

  assert.match(login, /\.WaitAsync\(AuthenticationTimeout\)/);
  assert.match(login, /catch \(TimeoutException\)[\s\S]{0,120}_closeButton\.IsEnabled = true;/);
  assert.match(api, /OmniLibraryLoginRuntime\.TryActivate\(existing\);/);
  const startLogin = storeSync.slice(
    storeSync.indexOf("public StoreSyncSnapshot StartUnifySteamLogin"),
    storeSync.indexOf("public StoreSyncSnapshot CompleteUnifySteamManualAuth"),
  );
  assert.doesNotMatch(startLogin, /_unifyStoreRefreshGates\.Enter/);
  assert.doesNotMatch(startLogin, /target\.Cache = source\.Cache/);
  for (const helper of [legendary, gogdl]) {
    assert.match(helper, /source\.CopyToAsync\(destination, downloadTimeout\.Token\)/);
  }
});
test("OmniLibrary Status page explains sign-in, running work, checks and what is missing", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const start = popup.indexOf("  // ---- OmniLibrary Status page");
  const end = popup.indexOf("  function buildOmniLibraryPreparationProgress(store)");
  assert.ok(start > 0 && end > start, "status helpers are present");
  let snapshot = null;
  const helpers = new Function(
    "getUnifySteamSnapshot",
    `${popup.slice(start, end)}
     return { describeOmniLibraryStoreStatus, summarizeOmniLibraryStatus, buildOmniLibraryActivityCard };`,
  )(() => snapshot);

  const epic = {
    id: "epic-games",
    title: "Epic Games",
    enabled: true,
    capabilities: ["managed-web-sign-in"],
    authReady: false,
    lifecycle: { catalog: "ready", shortcuts: "ready", artwork: "degraded" },
    syncHealth: { trigger: "remote-retry", detail: "legendary list failed.", nextRemoteCheckAtUtc: "2026-11-10T01:00:00Z" },
  };
  const xbox = {
    id: "xbox-game-pass",
    title: "Xbox",
    enabled: true,
    capabilities: [],
    authReady: true,
    accountName: "Xbox account",
    achievementProviderName: "Xbox Live",
    achievementsEnabled: true,
    achievementProviderConfigured: true,
    achievementProviderDetail: "Connected as lokimaks",
    lifecycle: { catalog: "ready", shortcuts: "ready", artwork: "ready" },
    syncHealth: { checkRunning: true },
  };
  const retro = {
    id: "retro-library",
    title: "Emulation",
    enabled: true,
    capabilities: ["local-library"],
    lifecycle: { catalog: "ready", shortcuts: "ready", artwork: "ready" },
    romSystems: [{ title: "GameCube", emulatorTitle: "Dolphin", gameCount: 2, emulatorDetected: false }],
    syncHealth: {},
  };
  snapshot = { stores: [epic, xbox, retro, { id: "gog-galaxy", title: "GOG", enabled: false }], activity: {} };

  const epicStatus = helpers.describeOmniLibraryStoreStatus(epic, {});
  assert.equal(epicStatus.account, "Not signed in");
  assert.equal(epicStatus.tone, "attention");
  assert.match(epicStatus.checks, /^Last check failed, next try /);
  assert.deepEqual(epicStatus.problems, ["legendary list failed."]);
  assert.ok(epicStatus.missing.includes("Sign in to Epic Games on its page"));
  assert.ok(epicStatus.missing.some((item) => /artwork is still missing/.test(item)));

  const xboxStatus = helpers.describeOmniLibraryStoreStatus(xbox, {});
  assert.equal(xboxStatus.account, "Signed in as Xbox account");
  assert.equal(xboxStatus.achievements, "Connected as lokimaks");
  assert.equal(xboxStatus.badge, "Working");
  assert.match(xboxStatus.now, /Checking the store/);

  const retroStatus = helpers.describeOmniLibraryStoreStatus(retro, {});
  assert.equal(retroStatus.account, "No sign-in needed");
  assert.deepEqual(retroStatus.missing, ["Dolphin not found for GameCube"]);

  // A running game pauses every library and the summary says so.
  const paused = helpers.describeOmniLibraryStoreStatus(xbox, { backgroundPaused: true, pauseReason: "game" });
  assert.equal(paused.badge, "Paused");
  assert.equal(paused.now, "Paused while a game is running");
  snapshot.activity = { backgroundPaused: true, pauseReason: "game" };
  const summary = helpers.summarizeOmniLibraryStatus(snapshot);
  assert.equal(summary.badge, "Paused");
  assert.equal(summary.statuses.length, 3);
  assert.match(summary.copy, /2 libraries need attention/);
  const card = helpers.buildOmniLibraryActivityCard(snapshot);
  assert.ok(card.lines.includes("Background work is paused while a game is running."));
  assert.ok(card.lines.includes("Off and doing nothing: GOG."));

  // The page is reachable from the overview, polls only while open and B returns to its row.
  assert.match(popup, /slotKey: "omnilibrary-status-entry"/);
  assert.match(popup, /if \(state\.panelVisible && isOmniLibraryStatusRoute\(\)\)/);
  assert.match(popup, /route\.pageId === omniLibraryStatusPageId\s*\?\s*"omnilibrary-status-entry"/);
});