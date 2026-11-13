import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const frontendLibUrl = new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url);
const backgroundHostUrl = new URL("../src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs", import.meta.url);
const libraryTabsUrl = new URL("../src/SteamLoader.App/Assets/library-tabs.js", import.meta.url);

test("one A press cannot activate a row in the newly opened plugin", async () => {
  const [popupSource, frontendLibSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);
  const start = popupSource.indexOf("  function handleSlotClick(");
  const end = popupSource.indexOf("\n  function buildVolumePanelModel(", start);
  assert.ok(start >= 0 && end > start);
  assert.match(frontendLibSource, /helpers\.handleSlotClick\(index, slot\)/);

  const state = { route: { id: "root" }, renderedSlots: [] };
  const navigation = { activationBlockedUntil: 0 };
  let now = 1000;
  let opened = 0;
  let unintendedActions = 0;
  const pluginSlot = { onClick: () => { unintendedActions += 1; } };
  const homeSlot = {
    onClick: () => {
      opened += 1;
      state.route = { id: "plugin" };
      state.renderedSlots = [pluginSlot];
    },
  };
  state.renderedSlots = [homeSlot];

  const handleSlotClick = new Function(
    "state", "getRouteKey", "getFallbackControllerNavigationState", "rememberCurrentRouteSlot", "Date",
    `${popupSource.slice(start, end)}\nreturn handleSlotClick;`,
  )(
    state,
    (route) => route.id,
    () => navigation,
    () => {},
    { now: () => now },
  );

  assert.equal(handleSlotClick(0, homeSlot, "root"), true);
  assert.equal(handleSlotClick(0, homeSlot, "root"), false);
  assert.equal(handleSlotClick(0, pluginSlot, "plugin"), false);
  assert.equal(opened, 1);
  assert.equal(unintendedActions, 0);

  now += 451;
  assert.equal(handleSlotClick(0, pluginSlot, "plugin"), true);
  assert.equal(unintendedActions, 1);
  assert.equal(handleSlotClick(0, pluginSlot, "plugin"), false);
  assert.equal(unintendedActions, 1);
});

test("Quick Access header actions participate in focus restoration", async () => {
  const [popupSource, frontendLibSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);

  for (const source of [popupSource, frontendLibSource]) {
    const createHeaderActionButton = source.match(
      /function createHeaderActionButton\([\s\S]*?\r?\n  \}/,
    )?.[0];

    assert.ok(createHeaderActionButton, "header action renderer should be present");
    assert.match(createHeaderActionButton, /data-slot-button/);
    assert.match(createHeaderActionButton, /data-slot-key/);
    assert.match(createHeaderActionButton, /data-header-action/);
    assert.match(createHeaderActionButton, /onGamepadFocus/);
    assert.match(createHeaderActionButton, /onMoveLeft/);
    assert.match(createHeaderActionButton, /onMoveRight/);
    assert.match(createHeaderActionButton, /onMoveUp/);
    assert.match(createHeaderActionButton, /onMoveDown/);
  }

  assert.match(
    popupSource,
    /\[data-slot-button\]:not\(\[data-header-action="true"\]\)/,
    "a fresh fallback should still choose the first content button, not a header action",
  );
  assert.match(
    popupSource,
    /pendingSlotKey\.startsWith\("header-action:"\)/,
    "a pending header focus must bypass row auto-focus",
  );
  assert.match(popupSource, /function movePanelHeaderActionFocus\(/);
  assert.match(popupSource, /function movePanelHeaderToContent\(/);
  assert.match(popupSource, /function movePanelContentToHeader\(/);
  assert.match(
    popupSource,
    /function moveHomeGridFocus\([\s\S]*?direction === "left"[\s\S]*?direction === "right"[\s\S]*?direction === "up"[\s\S]*?direction === "down"/,
    "the expanded home grid should provide deterministic movement in every direction",
  );
  assert.match(
    popupSource,
    /onMoveLeft: \(event\) => moveHomeGridFocus\(plugin\.id, "left", event\)[\s\S]*?onMoveRight: \(event\) => moveHomeGridFocus\(plugin\.id, "right", event\)[\s\S]*?onMoveUp: \(event\) => moveHomeGridFocus\(plugin\.id, "up", event\)[\s\S]*?onMoveDown: \(event\) => moveHomeGridFocus\(plugin\.id, "down", event\)/,
    "every home card should opt into two-dimensional controller navigation",
  );
  assert.match(
    popupSource,
    /key: "plugins"[\s\S]*?getPanelHeaderNavigationProps\("plugins"\)/,
    "header navigation must use the rendered Plugin Activator focus key",
  );
  assert.match(
    popupSource,
    /key: "audio-refresh"[\s\S]*?getPanelHeaderNavigationProps\("audio-refresh"\)/,
    "secondary-screen header actions should use the same deterministic navigation lane",
  );
  assert.match(popupSource, /function keepPanelElementInNearestView\(/);
  assert.doesNotMatch(
    popupSource,
    /panel\.scrollTo\(\{ top: 0/,
    "moving through header controls must not force the whole panel to the first row",
  );
  for (const source of [popupSource, frontendLibSource]) {
    assert.match(source, /function getSlotDirectionalButtonProps\(/);
    assert.match(source, /buttonProps\.onMoveLeft = /);
    assert.match(source, /buttonProps\.onMoveRight = /);
    assert.match(source, /buttonProps\.onMoveUp = /);
    assert.match(source, /buttonProps\.onMoveDown = /);
  }
  assert.match(
    popupSource,
    /function movePanelControlFocus\([\s\S]*?getBoundingClientRect\(\)[\s\S]*?focusPanelControlElement\(candidates\[0\]\.element, event\)[\s\S]*?focusSteamQuickAccessTab\(event\)[\s\S]*?movePanelContentToHeader\(event\)/,
    "native navigation should follow the visible two-dimensional layout and cross to Steam only at the left edge",
  );
  assert.match(
    popupSource,
    /const slotWithNavigation = \{[\s\S]*?buttonProps: getSlotDirectionalButtonProps\(slot, index\)[\s\S]*?STFrontendLib\.createButtonSlot\([\s\S]*?slotWithNavigation/,
    "native Steam buttons must receive TFS-owned movement handlers before rendering",
  );
  assert.match(popupSource, /function createFrontendRenderHelpers\(\)[\s\S]*?getSlotDirectionalButtonProps/);
  assert.match(
    frontendLibSource,
    /function createPanelShell\([\s\S]*?helpers\.getSlotDirectionalButtonProps\(slot, slotIndex\)[\s\S]*?createButtonSlot\(state, createElement, withChildren, navigableSlot/,
    "the primary Steam-native panel renderer must attach four-way handlers to every plugin row",
  );
  assert.match(
    popupSource,
    /const directionalProps = \{[\s\S]*?onMoveLeft: \(event\) => movePanelControlFocus\(focusKey, "left", event\)[\s\S]*?onMoveRight: \(event\) => movePanelControlFocus\(focusKey, "right", event\)[\s\S]*?onMoveUp: \(event\) => movePanelControlVerticalFocus[\s\S]*?onMoveDown: \(event\) => movePanelControlVerticalFocus/,
    "custom native controls should inherit layout-aware four-way navigation",
  );
  assert.match(popupSource, /fallbackSlotKey: "header-action:settings"/);
  assert.doesNotMatch(frontendLibSource, /deck_ui_navigation/);
});

test("Quick Access yields to Steam tabs and retains native gamepad focus", async () => {
  const popupSource = await readFile(popupUrl, "utf8");
  assert.match(popupSource, /function focusSteamQuickAccessTab\([\s\S]*?panelNavigationZone = "tabs"[\s\S]*?tab\.focus/);
  assert.match(popupSource, /focusedQuickAccessControl = document\.querySelector\('#QuickAccess-Menu \.gpfocus'\)[\s\S]*?panel\.contains\(focusedQuickAccessControl\) \? "panel" : "tabs"[\s\S]*?return;/);
  assert.match(popupSource, /state\.panelNavigationZone === "tabs"[\s\S]*?return;/);
  assert.match(
    popupSource,
    /function repairPanelFocusIfNeeded\(\)[\s\S]*?if \(!documentHasFocus \|\| activeElement !== document\.body\) \{[\s\S]*?return;/,
    "focus repair must yield whenever another Steam surface owns focus",
  );
  assert.doesNotMatch(
    popupSource,
    /setInterval\(repairPanelFocusIfNeeded/,
    "focus repair should be event-driven instead of continuously reclaiming focus",
  );
  assert.match(popupSource, /panelEntryPendingUntil > Date\.now\(\)[\s\S]*?focusedQuickAccessControl\.id === "quickaccess_tab_4747"/);
  assert.match(popupSource, /function enterPanelFromSteamTab\([\s\S]*?panelEntryPendingUntil = Date\.now\(\) \+ 400[\s\S]*?focusPanelControlElement\(target\)/);
  assert.match(popupSource, /currentIndex <= rowStart\)[\s\S]*?focusSteamQuickAccessTab\(event\)/);
});

test("nested routes start on useful content and keep Back out of the main action list", async () => {
  const popupSource = await readFile(popupUrl, "utf8");

  assert.match(
    popupSource,
    /function createBackHeaderAction\([\s\S]*?key: "back"[\s\S]*?icon: BackIcon[\s\S]*?navigateBackFromRoute\(route\)/,
    "mouse users should retain a compact header Back action",
  );
  assert.match(
    popupSource,
    /function withBackHeaderAction\([\s\S]*?headerActions: \[backAction, \.\.\.headerActions\]/,
    "the compact Back action should live in the header lane",
  );
  assert.doesNotMatch(
    popupSource,
    /globalBackSlotKey|steamloader-dialog-button-global-back|steamloader-row-shell-global-back/,
    "Back must not consume the first content row on every nested screen",
  );
  assert.match(
    popupSource,
    /routeChanged && !hasExplicitFocusRequest[\s\S]*?requestFreshEntryForRoute\(route, 0, 0\)/,
    "forward navigation should focus the first meaningful action",
  );
  assert.match(
    popupSource,
    /hasExplicitFocusRequest = state\.pendingFocusRouteKey === nextRouteKey[\s\S]*?requestRouteEntryFocus\(route\)/,
    "back navigation should preserve the semantic focus target requested by the parent screen",
  );
});

test("performance sliders retain readable contrast while focused", async () => {
  const popupSource = await readFile(popupUrl, "utf8");

  assert.match(
    popupSource,
    /\.steamloader-performance-slider-button\.gpfocus \.steamloader-volume-card,[\s\S]*?background: linear-gradient\(180deg, rgba\(20, 35, 49, 0\.99\)[\s\S]*?inset 0 0 0 2px color-mix\(in srgb, var\(--tfs-plugin-accent\) 78%, white\)/,
    "focused performance cards should stay dark and use a readable theme-colored focus ring",
  );
  assert.match(
    popupSource,
    /\.steamloader-performance-slider-button\.gpfocus \.steamloader-volume-title,[\s\S]*?\.steamloader-volume-slider-value \{[\s\S]*?color: #f5f9fd !important;/,
    "primary slider text should remain bright on the focused card",
  );
  assert.match(
    popupSource,
    /\.steamloader-performance-slider-button\.gpfocus \.steamloader-volume-copy,[\s\S]*?\.steamloader-volume-hint \{[\s\S]*?color: rgba\(207, 220, 232, 0\.94\) !important;/,
    "secondary slider text should retain readable contrast",
  );
});

test("Quick Access lets Steam slide the drawer and shows TFS content immediately", async () => {
  const popupSource = await readFile(popupUrl, "utf8");

  assert.match(popupSource, /style\.dataset\.tfsVersion === String\(stateVersion\)[\s\S]*?style\.textContent = `/);
  assert.match(popupSource, /style\.dataset\.tfsVersion = String\(stateVersion\);/);
  assert.match(popupSource, /function ensureNativeQuickAccessHostStyle\(mainDocument\)[\s\S]*?style\.dataset\.tfsVersion = String\(stateVersion\);/);
  assert.match(popupSource, /scrollbar-gutter: stable;/);
  assert.match(popupSource, /@keyframes steamloader-focus-icon-pop/);
  assert.match(
    popupSource,
    /function beginPanelReveal\(panelHost, nativeHost\) \{[\s\S]*?data-tfs-reveal-phase", "ready"\);\s*void syncNativeQuickAccessHost\(true, nativeHost\);\s*\}/,
    "TFS content should ride along with Steam's slide instead of a staged reveal",
  );
  assert.doesNotMatch(popupSource, /data-tfs-reveal-phase", "(?:sizing|expanding|settling|revealing)"/);
  assert.doesNotMatch(popupSource, /clip-path: inset\(0 0 0 31%\)|steamloader-ui-arrive/);
  assert.match(popupSource, /syncGeneration !== nativeState\.syncGeneration/);
  assert.match(
    popupSource,
    /function observeNativeSetBounds\(browser\)[\s\S]*?browser\.__steamLoaderNativeBoundsAt = Date\.now\(\);/,
    "Steam's own per-frame SetBounds calls must be observed",
  );
  assert.match(
    popupSource,
    /const quietInMs = getNativeBoundsQuietDelay\(nativeState, host\.browser\);\s*if \(quietInMs > 0\) \{\s*scheduleDeferredNativeSync/,
    "TFS must not set bounds while Steam's drawer animation runs",
  );
  assert.match(
    popupSource,
    /function animateNativeBounds\([\s\S]*?const slideWidth = Math\.max\(target\.width, Number\(from\.width\) \|\| 0\);[\s\S]*?0\.5 - Math\.cos\(progress \* Math\.PI\) \/ 2/,
    "remaining corrections slide x with a fixed width like Steam's drawer",
  );
  assert.doesNotMatch(popupSource, /await host\.browser\.SetBounds\(rect\.x, rect\.y, rect\.width, rect\.height\)/);
  assert.match(
    popupSource,
    /if \(!canReusePreviousState\) \{[\s\S]*?previousState\?\.panelObserver\?\.disconnect\?\.\(\);/,
    "a reinjected script must stop the previous script's panel observer",
  );
  assert.match(
    popupSource,
    /#QuickAccess-NA \{\s*width: auto !important;/,
    "Steam's fixed 854px QAM root must follow the widened BrowserView",
  );
  assert.doesNotMatch(popupSource, /function followNativeBounds|data-steamloader-native-qam-sizing="running"/);
  assert.doesNotMatch(popupSource, /animation-delay: (?:24|48|60|72)ms;/);
  assert.match(
    popupSource,
    /body\.steamloader-animations-disabled \.steamloader-panel,[\s\S]*?animation: none !important;[\s\S]*?transition: none !important;/,
    "the TFS animation setting should disable every added motion effect",
  );
  assert.match(
    popupSource,
    /@media \(prefers-reduced-motion: reduce\) \{[\s\S]*?animation: none !important;[\s\S]*?transition: none !important;/,
    "Windows reduced-motion preferences should disable TFS motion",
  );
});

test("wide Quick Access gives each content type a deliberate responsive width", async () => {
  const popupSource = await readFile(popupUrl, "utf8");

  assert.match(popupSource, /--tfs-layout-wide: 1200px;/);
  assert.match(popupSource, /--tfs-layout-medium: 780px;/);
  assert.match(popupSource, /--tfs-layout-compact: 560px;/);
  assert.match(
    popupSource,
    /\[data-slot-layout="slider"\] \{[\s\S]*?grid-column: auto;[\s\S]*?width: 100% !important;[\s\S]*?align-self: stretch;/,
    "peer sliders should use the same two-column card grid as other controls",
  );
  assert.match(
    popupSource,
    /\[data-slot-layout="slider"\]\[data-slot-span="wide"\] \{[\s\S]*?grid-column: 1 \/ -1;[\s\S]*?width: min\(100%, var\(--tfs-layout-medium\)\) !important;/,
    "a deliberately primary slider should retain a centered wide lane",
  );
  assert.match(
    popupSource,
    /\[data-slot-layout="feature"\] \{[\s\S]*?grid-column: auto;[\s\S]*?align-self: start;/,
    "visual feature cards should remain card-sized in the two-column grid",
  );
  assert.match(
    popupSource,
    /"data-slot-layout": "slider"/,
    "custom performance sliders should expose their semantic layout to the responsive CSS",
  );
  assert.match(
    popupSource,
    /"data-slot-layout": "slider",[\s\S]*?"data-slot-span": "wide",[\s\S]*?"data-performance-slider": "true"/,
    "the primary RTSS overlay-mode slider should remain visually distinct from paired detail sliders",
  );
  assert.match(popupSource, /className: "steamloader-volume-card steamloader-volume-panel"/);
  assert.match(
    popupSource,
    /@media \(max-width: 620px\)[\s\S]*?\.steamloader-volume-panel,[\s\S]*?width: 100% !important;/,
    "sized lanes should return to the full available width on narrow panels",
  );
});

test("every built-in plugin uses the selected theme accent with its own product context", async () => {
  const [popupSource, frontendLibSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);

  for (const pluginId of [
    "settings",
    "processes",
    "app-start",
    "store-sync",
    "omnilibrary",
    "auto-sisr",
    "artwork",
    "curtain",
    "audio",
    "performance",
    "power",
    "hltb",
    "discord",
    "themes",
    "smart-home",
    "handheld-performance",
  ]) {
    assert.match(
      popupSource,
      new RegExp(`(?:"${pluginId}"|${pluginId.replaceAll("-", "\\-")}): \\{ eyebrow: "[A-Z &]+"`),
      `${pluginId} should keep its own product context`,
    );
  }

  assert.match(
    popupSource,
    /function getActiveThemeAccent\([\s\S]*?getThemeSettings\(\)\?\.accentColor[\s\S]*?function decoratePanelModel\([\s\S]*?"--tfs-plugin-accent": themeAccent\.accent[\s\S]*?"--tfs-plugin-accent-rgb": themeAccent\.rgb/,
    "panel gradients should use the configured theme accent and its RGB channels",
  );
  assert.match(popupSource, /"data-plugin-id": renderedModel\.pluginId/);
  assert.match(popupSource, /children: renderedModel\.panelEyebrow/);
  assert.match(frontendLibSource, /"data-plugin-id": model\.pluginId/);
  assert.match(frontendLibSource, /style: model\.panelStyle \|\| undefined/);
  assert.match(frontendLibSource, /children: model\.panelEyebrow/);
  assert.match(
    popupSource,
    /Modern TFS visual system:[\s\S]*?selected theme accent[\s\S]*?var\(--tfs-plugin-accent\)[\s\S]*?rgba\(var\(--tfs-plugin-accent-rgb\), 0\.28\)/,
    "the shared visual system should derive gradients and focus treatment from the selected theme color",
  );
  assert.doesNotMatch(
    popupSource,
    /home-plugin-(?:audio|performance)[^\n]*--tfs-card-accent: #[0-9a-f]{6};/i,
    "home cards should no longer override the user's selected theme color",
  );
});

test("all shared plugin screens use the wide card layout", async () => {
  const [popupSource, frontendLibSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);
  assert.match(popupSource, /Shared wide-QAM language for every built-in and SDK plugin screen/);
  assert.match(popupSource, /\.steamloader-panel:not\(\.steamloader-panel-home\) > \.steamloader-stack:not\(\.steamloader-top-stack\) \{[\s\S]*?grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(frontendLibSource, /"data-slot-layout": slot\.layout/);
  assert.match(frontendLibSource, /className: "steamloader-card-grid"/);
});

test("Quick Access loads the Steam-native control library before rendering TFS", async () => {
  const backgroundHost = await readFile(backgroundHostUrl, "utf8");
  const popupScript = backgroundHost.match(/var popupScript = string\.Join\([\s\S]*?\);/)?.[0];
  assert.ok(popupScript, "the Quick Access script bundle should be assembled");
  const nativeLibrary = popupScript.indexOf('ReadText("Assets/st-frontend-lib.js")');
  const panel = popupScript.indexOf('ReadText("Assets/quickaccess-popup.js")');
  assert.ok(nativeLibrary >= 0 && panel > nativeLibrary,
    "Steam-native buttons must be available when the TFS panel first renders");
});

test("Quick Access home expands into a responsive icon dashboard", async () => {
  const popupSource = await readFile(popupUrl, "utf8");

  assert.match(
    popupSource,
    /#quickaccess_content_4747 \{[\s\S]*?width: calc\(100vw - 58px\) !important;[\s\S]*?max-width: calc\(100vw - 58px\) !important;/,
    "the TFS tab should use nearly all available Quick Access width",
  );
  assert.match(
    popupSource,
    /\.steamloader-panel-home > \.steamloader-stack:not\(\.steamloader-top-stack\) \{[\s\S]*?grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/,
    "the home tools should retain a two-column base for smaller screens",
  );
  assert.match(
    popupSource,
    /@media \(min-width: 1100px\)[\s\S]*?grid-template-columns: repeat\(3, minmax\(0, 1fr\)\)/,
    "the dashboard should show more tools at once on wide screens",
  );
  assert.match(
    popupSource,
    /@media \(min-width: 1600px\)[\s\S]*?grid-template-columns: repeat\(4, minmax\(0, 1fr\)\)/,
    "large screens should fit four home or activator cards per row",
  );
  assert.match(
    popupSource,
    /makeNavigationSlot\(plugin\.title, plugin\.description/,
    "home cards should expose their descriptions instead of icon labels alone",
  );
  assert.match(
    popupSource,
    /function findNativeQuickAccessHost\([\s\S]*?isQuickAccessBrowserViewCandidate\(element, props\)[\s\S]*?isRightAnchored/,
    "the widened TFS surface should discover Steam's native right-side BrowserView host",
  );
  assert.match(
    popupSource,
    /function isQuickAccessBrowserViewCandidate\([\s\S]*?props\?\.browser[\s\S]*?props\.animateIn[\s\S]*?#ScreensaverPopup/,
    "Steam's screensaver BrowserView must never be widened as the Quick Access host",
  );
  assert.match(
    popupSource,
    /data-steamloader-native-qam-expanded="true"[\s\S]*?width: calc\(100vw - 58px\) !important;[\s\S]*?transform: translateX\(0px\) !important;[\s\S]*?transition: none !important;/,
    "the native QAM host should receive its final width without an intermediate resize animation",
  );
  assert.match(
    popupSource,
    /setAttribute\("data-steamloader-native-qam-expanded", "true"\)/,
    "the expanded-host selector should receive the explicit value expected by its CSS rule",
  );
  assert.match(
    popupSource,
    /const target = \{ x: rect\.x, y: rect\.y, width: rect\.width, height: rect\.height \};[\s\S]*?animateNativeBounds\(nativeState, host\.browser, bounds, target\)/,
    "the native BrowserView bounds must follow the expanded Steam placeholder",
  );
  assert.match(
    popupSource,
    /expanded && state\.panelVisible && \(!wasExpanded \|\| !nativeState\.panelVisible\)[\s\S]*?host\.browser\.SetFocus\(true\)[\s\S]*?requestRouteEntryFocus\(state\.route\)[\s\S]*?queuePendingFocusRestore\(state\.route\)/,
    "entering the native BrowserView should restore focus once, without repeating it during drawer animation",
  );
  assert.match(
    popupSource,
    /function shouldExpandNativeQuickAccessHost\([\s\S]*?tfsTabSelected[\s\S]*?state\.nativeQuickAccessHost\?\.expanded[\s\S]*?function monitorPanelVisibility\(\)[\s\S]*?shouldExpandNativeQuickAccessHost\(visible, host\)/,
    "closing Quick Access should retain the prepared TFS width while switching tabs should restore the regular width",
  );
  assert.match(
    popupSource,
    /function repairPanelFocusIfNeeded\(\)[\s\S]*?document\.hasFocus\(\)[\s\S]*?!documentHasFocus \|\| activeElement !== document\.body[\s\S]*?queuePendingFocusRestore\(state\.route\)/,
    "focus repair should only restore an empty focused body inside the active TFS BrowserView",
  );
  assert.match(
    popupSource,
    /document\.addEventListener\("focusout", window\.__steamLoaderFocusRepairHandler, true\);/,
    "DOM focus escape should trigger a bounded repair attempt",
  );
  assert.doesNotMatch(
    popupSource.match(/function ensureFocusRepairHandler\(\)[\s\S]*?\r?\n  \}/)?.[0] || "",
    /window\.addEventListener\("blur"/,
    "native BrowserView blur must be treated as an intentional hand-off",
  );
  assert.match(popupSource, /headerIcon: SteamLoaderIcon/);
  assert.match(popupSource, /function getHomeGridColumnCount\(/);
});

test("Quick Access fallback buttons retain controller back navigation", async () => {
  const popupSource = await readFile(popupUrl, "utf8");

  assert.match(
    popupSource,
    /RegisterForControllerInputMessages\(\s*handleFallbackControllerNavigation/,
    "the HTML-button fallback should subscribe to Steam controller input",
  );
  assert.match(
    popupSource,
    /isBackButton[\s\S]*?navigateBackFromRoute\(\)/,
    "controller B should navigate to the parent route",
  );
  assert.match(
    popupSource,
    /isAcceptButton[\s\S]*?activateFocusedFallbackSlot\(\)/,
    "controller A should activate the focused row when native buttons are unavailable",
  );
  assert.match(
    popupSource,
    /function getFallbackDirectionFromSteamButton\([\s\S]*?9: "up"[\s\S]*?12: "right"/,
    "the fallback bridge should map every d-pad direction",
  );
  assert.match(
    popupSource,
    /function moveFallbackPanelFocus\([\s\S]*?invokeFallbackSlotMoveHandler[\s\S]*?movePanelHeaderActionFocus/,
    "fallback movement should retain slider handlers and the horizontal header lane",
  );
  assert.match(
    popupSource,
    /const intendedFocusKey = normalizeFocusSlotKey\([\s\S]*?navigation\.nativeFocusRepairTimer = window\.setTimeout\([\s\S]*?focusPanelControlElement\(target\);[\s\S]*?}, 32\);/,
    "fallback movement should win after Steam's native navigation processes the same direction",
  );
  assert.match(
    popupSource,
    /key === "Escape"[\s\S]*?key === "Backspace"[\s\S]*?key === "GamepadB"/,
    "keyboard and synthetic gamepad cancel events should share the fallback path",
  );
  assert.match(
    popupSource,
    /direction === "right" && enterPanelFromSteamTab\(\)[\s\S]*?if \(state\.nativeUi\.dialogButtonType\) \{\s*return;/,
    "native controls should leave ordinary movement to Steam while retaining an explicit tab-to-panel entry path",
  );
  assert.match(
    popupSource,
    /now - navigation\.lastBackAt < 280/,
    "duplicate native and fallback cancel events should not skip two routes",
  );
  assert.match(
    popupSource,
    /function setRoute\(route\)[\s\S]*?refreshQuickAccessPanel\(\);[\s\S]*?invalidate\(\);[\s\S]*?queuePendingFocusRestore\(state\.route\)/,
    "route changes must repaint even while Steam's tab collection is temporarily unmounted",
  );
  assert.match(
    popupSource,
    /function createFunctionalForceHost\(node\)[\s\S]*?dispatch\(\(value\) => !value\);[\s\S]*?dispatch\(\(value\) => !value\)/,
    "current function-component Quick Access hosts should receive a neutral forced render",
  );
  assert.match(
    popupSource,
    /Object\.hasOwn\(currentProps, "active"\)[\s\S]*?Object\.hasOwn\(currentProps, "showHeaderAndFooter"\)/,
    "the current Quick Access function component should be discovered from its stable props",
  );
  assert.match(popupSource, /state\.forceHosts = runtime\.forceHosts/);
});

test("hidden Quick Access surfaces stop live work and subscribe by route", async () => {
  const [popupSource, libraryTabsSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(libraryTabsUrl, "utf8"),
  ]);

  assert.match(popupSource, /function getLiveUpdateTopicsForRoute\(/);
  assert.match(popupSource, /topics=\$\{encodeURIComponent\(topicsKey\)\}/);
  assert.match(
    popupSource,
    /if \(!visible && state\.panelVisible\)[\s\S]*?closeLiveUpdateSource\(\);[\s\S]*?refreshLiveUpdatePollingFallbacks\(\);/,
    "closing Quick Access should release its event stream and route polling",
  );
  assert.match(
    popupSource,
    /function ensureLiveUpdateConnection\(\)[\s\S]*?!state\.panelVisible/,
    "hidden surfaces must not open a live event connection",
  );
  assert.match(
    libraryTabsSource,
    /state\.libraryRefreshTimer = window\.setInterval\([\s\S]*?document\.visibilityState === "hidden"/,
    "the library cache refresh should sleep while Steam's document is hidden",
  );
});
