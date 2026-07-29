import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const storeOverlayUrl = new URL("../src/SteamLoader.App/Assets/store-overlay.js", import.meta.url);
const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const backgroundHostUrl = new URL("../src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs", import.meta.url);
const apiServerUrl = new URL("../src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs", import.meta.url);
const catalogUrl = new URL("../src/SteamLoader.App/Infrastructure/Settings/SteamLoaderPluginCatalog.cs", import.meta.url);

test("Wishlist is a permanent core header action with a dedicated full-screen opener", async () => {
  const [popup, catalog, backgroundHost, apiServer] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(catalogUrl, "utf8"),
    readFile(backgroundHostUrl, "utf8"),
    readFile(apiServerUrl, "utf8"),
  ]);

  assert.doesNotMatch(catalog, /new\("store",/);
  assert.doesNotMatch(popup, /\{\s*id: "store",\s*title: "Wishlist",\s*description: "Cross-store games/);
  assert.match(popup, /function HeaderWishlistIcon\(\)/);
  assert.match(popup, /key: "wishlist",[\s\S]*?title: "Wishlist",[\s\S]*?icon: HeaderWishlistIcon,[\s\S]*?openStoreOverlay\(\)/);
  assert.match(popup, /key: "wishlist",[\s\S]*?key: "store",[\s\S]*?title: "Store"/);
  assert.doesNotMatch(popup, /plugin\.id === "store"/);
  assert.match(backgroundHost, /storeService\.RunRefreshLoopAsync\(\s*static \(\) => true,/);
  assert.doesNotMatch(backgroundHost, /IsPluginEnabled\("store"\)/);
  assert.doesNotMatch(apiServer, /\["\/api\/store"\]\s*=\s*"store"/);
  assert.match(popup, /async function openStoreOverlay\(\)/);
  assert.match(popup, /api\/store\/overlay\/open/);
});

test("Store overlay includes async price loading, controller navigation, alerts, and currency settings", async () => {
  const source = await readFile(storeOverlayUrl, "utf8");

  assert.match(source, /api\/store\/state/);
  assert.match(source, /api\/store\/offers\?gameId=/);
  assert.match(source, /api\/store\/alerts/);
  assert.match(source, /api\/store\/settings\/currency/);
  assert.match(source, /api\/store\/settings\/region/);
  assert.match(source, /api\/store\/search\?q=/);
  assert.match(source, /api\/store\/wishlist/);
  assert.match(source, /const tabs = \["discover", "search", "wishlist", "alerts", "settings"\]/);
  assert.match(source, /image\.loading = "lazy"/);
  assert.match(source, /navigator\.getGamepads/);
  assert.match(source, /lastActionAt/);
  assert.match(source, /duplicateWindow/);
  assert.match(source, /function scrollFocusedElementIntoView/);
  assert.match(source, /function animateFocusScroll/);
  assert.match(source, /const hero = element\.closest\("\.steamloader-store-hero"\)/);
  assert.match(source, /state\.selectedGame[\s\S]*?root\.querySelector\("\.steamloader-store-modal"\)/);
  assert.match(source, /dataset\.storeNavRow/);
  assert.match(source, /function shuffledGames/);
  assert.match(source, /Shuffle suggestions/);
  assert.match(source, /Add to TFS wishlist/);
  assert.match(source, /const searchKeyboardRows = \[/);
  assert.match(source, /input\.readOnly = true/);
  assert.match(source, /function renderSearchKeyboard/);
  assert.match(source, /function moveSearchKeyboardFocus/);
  assert.match(source, /dataset\.storeKeyboardRow/);
  assert.match(source, /grid-template-columns: repeat\(12/);
  assert.match(source, /case "search-back":[\s\S]*?handleSearchKeyboardKey\("Back"\)/);
  assert.match(source, /case "keyboard-space":[\s\S]*?handleSearchKeyboardKey\("Space"\)/);
  assert.match(source, /case "keyboard-done":[\s\S]*?handleSearchKeyboardKey\("Done"\)/);
  assert.match(source, /\["search-back", Boolean\(gamepad\.buttons\?\.\[2\]\?\.pressed\)\]/);
  assert.match(source, /\["keyboard-space", Boolean\(gamepad\.buttons\?\.\[3\]\?\.pressed\)\]/);
  assert.match(source, /\["keyboard-done", Boolean\(gamepad\.buttons\?\.\[9\]\?\.pressed\)\]/);
  assert.match(source, /A Select   X Delete   Y Space   Start Done   B Cancel/);
  assert.match(source, /Search games/);
  assert.match(source, /UNRELEASED/);
  assert.match(source, /function isPositivePrice/);
  assert.match(source, /function adjustSavedAlert/);
  assert.match(source, /function removeSavedAlert/);
  assert.match(source, /removeSavedAlert\(alert\)/);
  assert.match(source, /enabled: false/);
  assert.match(source, /function renderAlertTrend/);
  assert.match(source, /alert\.priceHistory/);
  assert.match(source, /Started at/);
  assert.match(source, /PRICE HISTORY/);
  assert.match(source, /adjustSavedAlert\(alert, -1\)/);
  assert.match(source, /adjustSavedAlert\(alert, 1\)/);
  assert.match(source, /steamloader-store-alert-trend-line/);
  assert.match(source, /api\/artwork\/search\?term=/);
  assert.match(source, /api\/artwork\/assets\?gameId=/);
  assert.match(source, /function resolveArtworkFallback/);
  assert.match(source, /if \(copy\) heading\.append/);
  assert.doesNotMatch(source, /More rotating games outside the first suggestion row/);
  assert.match(source, /Math\.min\(190, Math\.max\(110/);
  assert.match(source, /scrollFocusedElementIntoView\(focused, direction\)/);
  assert.match(source, /element\.closest\("\.steamloader-store-header"\)/);
  assert.match(source, /state\.activeTab === "wishlist" && direction === "up"/);
  assert.match(source, /const isHorizontalRail = container\.classList\.contains\("steamloader-store-rail"\)/);
  assert.match(source, /container === main && rail && \(direction === "left" \|\| direction === "right"\)/);
  assert.match(source, /const isRegionMenuOuter = container === main && rail\?\.classList\.contains\("steamloader-store-region-menu"\)/);
  assert.match(source, /const padding = isRegionMenuOuter \? 0/);
  assert.match(source, /rail\.dataset\.storeMainScrollAligned === "true"/);
  assert.match(source, /rail\.dataset\.storeMainScrollAligned = "true"/);
  assert.match(source, /active\.targetTop - targetTop/);
  assert.match(source, /steamloader-store-card\.is-controller-focus[^}]*transform: none/);
  assert.match(source, /const rail = element\.closest\("\.steamloader-store-rail, \.steamloader-store-region-menu/);
  assert.match(source, /const storeRegions = \[/);
  assert.match(source, /"JP", "Japan", "JPY", "¥"/);
  assert.match(source, /!event\.repeat/);
  assert.match(source, /function getArtworkCandidates/);
  assert.match(source, /function getCachedArtworkUrl/);
  assert.match(source, /api\/store\/artwork\?source=/);
  assert.match(source, /window\.__steamLoaderApiUrl/);
  assert.match(source, /steamloader-store-artwork-frame is-loading/);
  assert.match(source, /steamloader-store-artwork-loader-dot/);
  assert.match(source, /@keyframes steamloader-store-artwork-dot/);
  assert.match(source, /maxConcurrentArtworkLoads = 3/);
  assert.match(source, /function scheduleArtworkLoad/);
  assert.match(source, /new window\.IntersectionObserver/);
  assert.match(source, /rootMargin: "90px 120px"/);
  assert.match(source, /activeArtworkLoads < maxConcurrentArtworkLoads/);
  assert.match(source, /scheduleArtworkLoad\(frame/);
  assert.match(source, /steamloader-store-artwork-loader-reveal 1ms linear 140ms/);
  assert.match(source, /image\.addEventListener\("load"/);
  assert.match(source, /frame\.classList\.remove\("is-loading"\)/);
  assert.match(source, /shared\.fastly\.steamstatic\.com/);
  assert.match(source, /cdn\.cloudflare\.steamstatic\.com/);
  assert.match(source, /fallbackImageUrl/);
  assert.match(source, /steamloader-store-alert-art/);
  assert.doesNotMatch(source, /≈/);
  assert.match(source, /String\.fromCharCode\(0x20ac\)/);
  assert.doesNotMatch(source, /\["Instant Gaming", `https:\/\/www\.instant-gaming\.com\/en\/search/);
  assert.match(source, /Instant Gaming appears only for an exact, in-stock PC match/);
  assert.match(source, /buttonEl\(`Search \$\{name\}`/);
  assert.match(source, /store\.steampowered\.com\/search/);
  assert.match(source, /www\.gog\.com\/en\/games/);
  assert.match(source, /function getXboxStoreLocale\(\)/);
  assert.match(source, /DE: "de-DE"/);
  assert.match(source, /www\.xbox\.com\/\$\{xboxLocale\}\/search/);
  assert.match(source, /function getEpicStoreLanguage\(\)/);
  assert.match(source, /store\.epicgames\.com\/browse\?q=\$\{query\}.*lang=\$\{epicLanguage\}/);
  assert.doesNotMatch(source, /CheapShark/i);
  assert.match(source, /case "previous-section": switchTab\(-1\)/);
  assert.match(source, /case "next-section": switchTab\(1\)/);
  assert.match(source, /\["USD", "US Dollar"/);
  assert.match(source, /\["EUR", "Euro"/);
  assert.match(source, /\["BOTH", "Dollar \+ Euro"/);
  assert.match(source, /function getPreferredAlertCurrencyCode/);
  assert.match(source, /function getGameAlertIdentity/);
  assert.match(source, /function getStoredAlertIdentity/);
  assert.match(source, /gameId: game\.id/);
  assert.match(source, /gameId: alert\.gameId/);
  assert.match(source, /getGameAlertIdentity\(game\) && game\.isWishlisted/);
  assert.doesNotMatch(source, /if \(!game\?\.steamAppId \|\| !state\.alertDraft\) return/);
  assert.match(source, /function replaceGameEverywhere\(updatedGame\)/);
  assert.match(source, /replaceGameEverywhere\(updatedGame\)/);
  assert.match(source, /if \(state\.alertDraft && !existingAlert && !state\.alertDraft\.edited\)/);
  assert.match(source, /state\.alertDraft\.edited = true/);
  assert.match(source, /bestAlertPrice = state\.alertDraft\.currencyCode === "EUR"/);
  assert.match(source, /displayCurrency === "REGION" \|\| displayCurrency === "BOTH"/);
  assert.match(source, /regionalCurrency === "EUR"/);
  assert.match(source, /steamloader-store-alert-list \{[^}]*padding-bottom: 76px/);
  assert.match(source, /--store-accent: #66c0f4/);
  assert.match(source, /--store-success: #5ee6a8/);
  assert.match(source, /const outerTarget = container === main[\s\S]*?\? rail/);
});

test("Wishlist keyboard shortcuts are forwarded by the Quick Access controller bridge", async () => {
  const source = await readFile(popupUrl, "utf8");

  assert.match(source, /activeOverlaySource === "store"/);
  assert.match(source, /GAMEPADX\|XBUTTON/);
  assert.match(source, /wishlistOverlayActive \? "keyboard-space" : "refresh"/);
  assert.match(source, /wishlistOverlayActive \? "keyboard-done" : ""/);
  assert.match(source, /case 3:[\s\S]*?return "search-back"/);
  assert.match(source, /case 14:[\s\S]*?return wishlistOverlayActive \? "keyboard-done" : ""/);
});

test("Store overlay is injected into full-screen surfaces before Quick Access can open it", async () => {
  const source = await readFile(backgroundHostUrl, "utf8");
  const themeScript = source.match(/var themeSurfaceScript = string\.Join\([\s\S]*?;\r?\n\r?\n        var appStartService/)?.[0];
  const popupScript = source.match(/var popupScript = string\.Join\([\s\S]*?;\r?\n        var themeSurfaceScript/)?.[0];

  assert.ok(themeScript);
  assert.match(themeScript, /Assets\/store-overlay\.js/);
  assert.ok(popupScript);
  assert.doesNotMatch(popupScript, /Assets\/store-overlay\.js/);
});

test("non-visible and Quick Access surfaces cannot host the price Store", async () => {
  const source = await readFile(storeOverlayUrl, "utf8");
  const hostCheck = source.match(/function canHostOverlay\(\) \{[\s\S]*?\r?\n  \}/)?.[0];

  assert.ok(hostCheck);
  assert.match(hostCheck, /document\.visibilityState !== "hidden"/);
  assert.match(hostCheck, /!isQuickAccessSurface\(\)/);
  assert.match(hostCheck, /window\.innerWidth >= 900/);
});
