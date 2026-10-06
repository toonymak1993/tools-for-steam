import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const frontendLibUrl = new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url);
const backgroundHostUrl = new URL("../src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs", import.meta.url);
const libraryTabsUrl = new URL("../src/SteamLoader.App/Assets/library-tabs.js", import.meta.url);

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
  assert.match(popupSource, /state\.panelFocusLostAt \|\|= Date\.now\(\)[\s\S]*?Date\.now\(\) - state\.panelFocusLostAt < 260/);
  assert.match(popupSource, /panelEntryPendingUntil > Date\.now\(\)[\s\S]*?focusedQuickAccessControl\.id === "quickaccess_tab_7"/);
  assert.match(popupSource, /function enterPanelFromSteamTab\([\s\S]*?panelEntryPendingUntil = Date\.now\(\) \+ 400[\s\S]*?focusPanelControlElement\(target\)/);
  assert.match(popupSource, /currentIndex <= rowStart\)[\s\S]*?focusSteamQuickAccessTab\(event\)/);
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
    /#quickaccess_content_7 \{[\s\S]*?width: min\(760px, calc\(100vw - 58px\)\) !important;[\s\S]*?max-width: min\(760px, calc\(100vw - 58px\)\) !important;/,
    "the TFS tab should use the wider Quick Access workspace",
  );
  assert.match(
    popupSource,
    /\.steamloader-panel-home > \.steamloader-stack:not\(\.steamloader-top-stack\) \{[\s\S]*?grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/,
    "the home tools should render as a two-column dashboard",
  );
  assert.match(
    popupSource,
    /makeNavigationSlot\(plugin\.title, plugin\.description/,
    "home cards should expose their descriptions instead of icon labels alone",
  );
  assert.match(
    popupSource,
    /function findNativeQuickAccessHost\([\s\S]*?props\?\.browser[\s\S]*?isRightAnchored/,
    "the widened TFS surface should discover Steam's native right-side BrowserView host",
  );
  assert.match(
    popupSource,
    /data-steamloader-native-qam-expanded="true"[\s\S]*?transform: translateX\(0px\) !important;/,
    "the native QAM host should expand toward the left edge instead of overflowing off-screen",
  );
  assert.match(
    popupSource,
    /setAttribute\("data-steamloader-native-qam-expanded", "true"\)/,
    "the expanded-host selector should receive the explicit value expected by its CSS rule",
  );
  assert.match(
    popupSource,
    /host\.browser\.SetBounds\(rect\.x, rect\.y, rect\.width, rect\.height\)/,
    "the native BrowserView bounds must follow the expanded Steam placeholder",
  );
  assert.match(
    popupSource,
    /expanded && \(!wasExpanded \|\| boundsDiffer\)[\s\S]*?host\.browser\.SetFocus\(true\)[\s\S]*?requestRouteEntryFocus\(state\.route\)[\s\S]*?queuePendingFocusRestore\(state\.route\)/,
    "moving the native BrowserView should restore both Steam input focus and the remembered panel target",
  );
  assert.match(
    popupSource,
    /function monitorPanelVisibility\(\)[\s\S]*?syncNativeQuickAccessHost\(visible\)/,
    "switching away from the TFS tab should restore the regular Quick Access width",
  );
  assert.match(
    popupSource,
    /function repairPanelFocusIfNeeded\(\)[\s\S]*?document\.hasFocus\(\)[\s\S]*?SetFocus\?\.\(true\)[\s\S]*?queuePendingFocusRestore\(state\.route\)/,
    "losing the native BrowserView focus should immediately return control to the active TFS item",
  );
  assert.match(
    popupSource,
    /document\.addEventListener\("focusout", window\.__steamLoaderFocusRepairHandler, true\);[\s\S]*?window\.addEventListener\("blur", window\.__steamLoaderFocusRepairHandler, true\);/,
    "both DOM focus escape and native BrowserView blur should trigger repair",
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
    /if \(!visible && state\.panelVisible\)[\s\S]*?closeLiveUpdateSource\(\);[\s\S]*?stopFocusRepairTimer\(\);[\s\S]*?refreshLiveUpdatePollingFallbacks\(\);/,
    "closing Quick Access should release its event stream and active repair/poll timers",
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
