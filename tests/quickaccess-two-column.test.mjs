import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const frontendLibUrl = new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url);

function getWidePanelCss(popupSource) {
  const start = popupSource.indexOf("${tfsNativeQuickAccessLayout && tfsWideQuickAccessPanel ? `");
  const end = popupSource.indexOf("` : \"\"}", start);
  assert.ok(start >= 0 && end > start, "wide panel CSS block exists");
  return popupSource.slice(start, end);
}

test("the Quick Access panel keeps Steam's width with one Decky-style column", async () => {
  const [popupSource, libSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);
  // Luis 2026-11-11: no widened panel, every page is a single column.
  assert.match(popupSource, /const tfsWideQuickAccessPanel = false;/);
  // Steam's tab class keeps its max-width; TFS only fills the width.
  const narrowStart = popupSource.indexOf("${tfsNativeQuickAccessLayout && !tfsWideQuickAccessPanel ? `");
  const narrowEnd = popupSource.indexOf("` : \"\"}", narrowStart);
  assert.ok(narrowStart >= 0 && narrowEnd > narrowStart, "narrow panel CSS block exists");
  const narrowCss = popupSource.slice(narrowStart, narrowEnd);
  assert.doesNotMatch(narrowCss, /max-width: none/);
  assert.match(narrowCss, /\.steamloader-header \{\s+flex-wrap: wrap !important;/);
  // Stacks and grids are flex columns; Steam fields fill the row.
  assert.match(popupSource, /\[class\*="stack"\] \{\s+display: flex !important;\s+flex-direction: column !important;/);
  assert.match(popupSource, /\.tfs-native-field > \* \{\s+flex: 1 1 auto !important;\s+width: 100% !important;/);
  // Value pickers sit below their label in the narrow column.
  assert.match(libSource, /childrenLayout: "below",/);
  // The RTSS overlay mode slider is a Steam SliderField too.
  const start = popupSource.indexOf("  function createPerformanceSliderSlotButton(");
  const end = popupSource.indexOf("return NativeDialogButton(", start);
  assert.match(popupSource.slice(start, end), /createNativeValueSliderSlotButton\(slot, index, autoFocusIndex\)/);
});

test("TFS does not repeat tools Steam's Quick Access already has", async () => {
  const popupSource = await readFile(popupUrl, "utf8");
  assert.match(popupSource, /const quickAccessRedundantPluginIds = new Set\(\["audio", "performance"\]\);/);
  // Hidden from the home list and the Plugin Activator.
  assert.match(popupSource, /!quickAccessRedundantPluginIds\.has\(plugin\.id\) &&\s+isPluginAvailable\(plugin\.id\)/);
  assert.match(popupSource, /getPluginSettings\(\)\s+\.filter\(\(plugin\) => !quickAccessRedundantPluginIds\.has\(plugin\.id\)\)/);
  // No Display plugin: its internal/external switch lives in Steam's Quick Settings.
  assert.doesNotMatch(popupSource, /id: "display",/);
  assert.doesNotMatch(popupSource, /pluginId: "display"|pluginId === "display"/);
});

test("the unused wide panel CSS still describes one symmetric two-column grid", async () => {
  const css = getWidePanelCss(await readFile(popupUrl, "utf8"));
  const panel = "#quickaccess_content_4747 .steamloader-panel.tfs-native";

  // All slot stacks (top stack included), info cards, the audio dashboard and
  // the mixer use the same two equal columns and gap.
  for (const container of [
    `${panel} > .steamloader-stack,`,
    `${panel} > .steamloader-card-grid,`,
    `${panel} > .steamloader-audio-dashboard,`,
    `${panel} .steamloader-audio-mixer-card-shell .steamloader-audio-mixer-stack {`,
  ]) {
    assert.ok(css.includes(container), container);
  }
  assert.match(css, /grid-template-columns: repeat\(2, minmax\(0, 1fr\)\) !important;\s+column-gap: var\(--tfs-col-gap\) !important;/);
  assert.doesNotMatch(css, /:not\(\.steamloader-top-stack\)/, "the top stack is no exception");

  // Controls fill exactly one cell; only headings, dividers, notes and an open
  // accordion span both columns.
  assert.match(css, /> \.steamloader-stack > \*,[\s\S]*?grid-column: auto !important;/);
  assert.match(css, /:is\(\.steamloader-inline-section, \.steamloader-divider, \.steamloader-status, \.steamloader-note, \.steamloader-error, \[data-slot-span="full"\], \[data-slot-layout="accordion"\]\[data-slot-expanded="true"\], /);
  // Sliders and value pickers use left/right for their value, so they span
  // both columns; otherwise the right column could not be reached from them.
  assert.match(css, /\[data-slot-layout="slider"\], \[data-slot-layout="stepper"\], \[data-slot-native="slider"\], \[data-slot-native="stepper"\]\)/);

  // Panel-level text fields take one column each; the sticky header keeps the
  // panel (a flex row, not a grid area) as its containing block.
  assert.match(css, /> \.steamloader-editor-card \{\s+flex: 0 0 calc\(\(100% - var\(--tfs-col-gap\)\) \/ 2\) !important;/);
  assert.match(css, /flex-wrap: wrap !important;/);

  // Steam's own fields stretch over their cell.
  assert.match(css, /\.tfs-native-field > \* \{\s+flex: 1 1 auto !important;\s+width: 100% !important;/);
});

test("Steam's native fields join the TFS grid navigation", async () => {
  const [popupSource, libSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);

  // The field sits in Steam's Focusable with the TFS direction handlers;
  // sliders and steppers keep left/right for their value.
  assert.match(libSource, /const Focusable = ui\?\.Focusable;/);
  assert.match(libSource, /onMoveUp: forward\(navProps\.onMoveUp\)/);
  assert.match(libSource, /onMoveLeft: nativeKind === "toggle" \? forward\(navProps\.onMoveLeft\) : undefined/);
  assert.match(libSource, /return wrapField\(stepperField, "stepper"\);/);
  assert.match(libSource, /return wrapField\(field, isToggle \? "toggle" : "slider"\);/);

  // Moving onto a native field focuses its Steam nav node.
  assert.match(popupSource, /function focusSteamNativeFieldElement\(target\)[\s\S]*?node\.BTakeFocus\(\)/);
  assert.match(popupSource, /if \(!focusSteamNativeFieldElement\(target\)\) \{/);

  // Up/down go to the next visual row first, then the nearest column, so a
  // full-width slider never skips a paired row below it.
  assert.match(popupSource, /const rowCandidates = candidates\.filter\(\s+\(candidate\) => candidate\.rect\.top < bandBottom - 4 && candidate\.rect\.bottom > bandTop \+ 4,/);
  assert.match(popupSource, /rowCandidates\.sort\(\(left, right\) => left\.cross - right\.cross\);/);

  // A column end continues to the neighbour in the same row.
  assert.match(popupSource, /const readingOrderTarget = rows\[currentIndex \+ \(direction === "down" \? 1 : -1\)\]/);

  // The Audio page is made of ordinary slots, so it uses the same grid
  // navigation instead of its own renderer.
  assert.match(popupSource, /function usesCustomShellRoute\(route = state\.route\) \{\s+return isPerformanceOverlayRoute\(route\);/);
  assert.match(popupSource, /const forceCustomShell = isPerformanceOverlayRoute\(\);/);
});

test("Steam's fields look like TFS rows: one padding, one separator, one focus wash", async () => {
  const [popupSource, libSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);
  assert.match(libSource, /bottomSeparator: "none",/);
  assert.doesNotMatch(libSource, /bottomSeparator: "standard"/);
  assert.match(popupSource, /\.tfs-native-field\[data-slot-button\]:not\(\[data-header-action="true"\]\) \{\s+padding: 0 !important;/);
  assert.match(popupSource, /\.tfs-native-field\[data-slot-button\]:has\(\.gpfocus\) \{\s+background: linear-gradient/);
});

test("settings rows stay focusable while a change saves in the background", async () => {
  const popupSource = await readFile(popupUrl, "utf8");
  assert.doesNotMatch(popupSource, /disabled: isGeneralSettingsBusy\(\)/);
  assert.match(popupSource, /function isGeneralSettingsUnavailable\(\) \{\s+return isControllerShortcutSettingsUnavailable\(\);/);
  // Overlapping saves: only the newest response replaces the snapshot.
  assert.match(popupSource, /if \(requestSequence === state\.generalSettings\.requestSequence\) \{\s+setGeneralSettingsSnapshot\(payload/);
  assert.match(popupSource, /state\.generalSettings\.saving = state\.generalSettings\.pendingRequests > 0;/);
});

test("TFS value sliders render as Steam's SliderField in the native layout", async () => {
  const [popupSource, libSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);

  for (const renderer of ["createPerformanceValueSliderSlotButton", "createRichValueSliderSlotButton"]) {
    const start = popupSource.indexOf(`  function ${renderer}(`);
    const end = popupSource.indexOf("return NativeDialogButton(", start);
    assert.ok(start >= 0 && end > start, renderer);
    assert.match(popupSource.slice(start, end), /createNativeValueSliderSlotButton\(slot, index, autoFocusIndex\)/, renderer);
  }
  assert.match(popupSource, /layout: "slider",[\s\S]*?onValueChange: \(next\) =>/);
  // Live value changes re-render the native slider instead of patching TFS DOM.
  assert.match(popupSource, /tfs-native-field\[data-slot-native="slider"\]'\)\) \{\s+scheduleNativeSliderRefresh\(\);/);
  assert.match(libSource, /description: slot\.sliderDescription \|\| undefined/);
  assert.match(libSource, /typeof slot\.formatValue === "function"/);
});
