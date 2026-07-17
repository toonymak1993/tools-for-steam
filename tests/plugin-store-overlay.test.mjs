import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("store snapshot refresh preserves the active controller focus zone", async () => {
  const source = await readFile(
    new URL("../src/SteamLoader.App/Assets/plugin-store-overlay.js", import.meta.url),
    "utf8",
  );
  const loadSnapshot = source.match(
    /async function loadSnapshot\(force = false\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  async function setOverlayOpen/,
  )?.[0];

  assert.ok(loadSnapshot, "loadSnapshot implementation should be present");
  assert.match(loadSnapshot, /state\.snapshot =/);
  assert.match(loadSnapshot, /ensureSelection\(\)/);
  assert.doesNotMatch(
    loadSnapshot,
    /requestStoreFocus\(`card:\$\{state\.selectedPluginId\}`\)/,
    "a background snapshot must not force focus from the header or tabs into the gallery",
  );
});

test("store activation ignores a stale closed overlay announcement", async () => {
  const source = await readFile(
    new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url),
    "utf8",
  );
  const setRemoteActive = source.match(
    /function setPluginStoreRemoteActive\(active, options = \{\}\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  function consumePluginStoreOverlayState/,
  )?.[0];
  const consumeOverlayState = source.match(
    /function consumePluginStoreOverlayState\(raw\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  function handlePluginStoreBridgeMessage/,
  )?.[0];

  assert.ok(setRemoteActive, "remote activation implementation should be present");
  assert.ok(consumeOverlayState, "overlay state consumer should be present");
  assert.match(setRemoteActive, /nextActive && !options\.fromOverlay/);
  assert.match(setRemoteActive, /bridge\.lastOverlayStateAt = 0/);
  assert.match(consumeOverlayState, /bridge\.lastOverlayStateAt === 0/);
  assert.match(consumeOverlayState, /receivedAt < bridge\.remoteActiveExpiresAt/);
});

test("long store context details remain scrollable with pointer and controller", async () => {
  const source = await readFile(
    new URL("../src/SteamLoader.App/Assets/plugin-store-overlay.js", import.meta.url),
    "utf8",
  );
  const contextStyles = source.match(
    /\.steamloader-plugin-store-context-menu \{[\s\S]*?\.steamloader-plugin-store-context-action \{/,
  )?.[0];
  const applyFocus = source.match(
    /function applyStoreFocus\(shouldFocus = true\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  function syncSelectedStoreCard/,
  )?.[0];
  const contextScroller = source.match(
    /function scrollStoreContextPanel\(direction\) \{[\s\S]*?\r?\n  \}/,
  )?.[0];

  assert.ok(contextStyles, "context menu styles should be present");
  assert.match(contextStyles, /height: min\(720px, calc\(100vh - 124px\)\)/);
  assert.match(contextStyles, /\.steamloader-plugin-store-context-panel \{[\s\S]*?overflow: auto/);
  assert.ok(applyFocus, "store focus implementation should be present");
  assert.match(applyFocus, /item\.closest\("\.steamloader-plugin-store-context-panel"\)/);
  assert.ok(contextScroller, "controller context scroller should be present");
  assert.match(contextScroller, /panel\.scrollBy/);
  assert.match(source, /scrollStoreContextPanel\(-1\)/);
  assert.match(source, /scrollStoreContextPanel\(1\)/);
});

test("only the active store host announces that the overlay closed", async () => {
  const source = await readFile(
    new URL("../src/SteamLoader.App/Assets/plugin-store-overlay.js", import.meta.url),
    "utf8",
  );
  const startAnnouncements = source.match(
    /function startStoreOverlayAnnouncements\(\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  function stopStoreOverlayAnnouncements/,
  )?.[0];
  const stopAnnouncements = source.match(
    /function stopStoreOverlayAnnouncements\(announceClosed = true\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  function setupStoreInputBridge/,
  )?.[0];

  assert.ok(startAnnouncements, "store announcement startup should be present");
  assert.ok(stopAnnouncements, "store announcement shutdown should be present");
  assert.match(startAnnouncements, /stopStoreOverlayAnnouncements\(false\)/);
  assert.match(stopAnnouncements, /const wasAnnouncing = Boolean\(state\.overlayAnnounceTimer\)/);
  assert.match(stopAnnouncements, /if \(announceClosed && wasAnnouncing\)/);
  assert.match(startAnnouncements, /announceStoreOverlayState\(true\)/);
  assert.match(source, /source: "plugin-store"/);
});

test("Quick Access ignores close announcements from the inactive store type", async () => {
  const source = await readFile(
    new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url),
    "utf8",
  );
  const consumeOverlayState = source.match(
    /function consumePluginStoreOverlayState\(raw\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  function handlePluginStoreBridgeMessage/,
  )?.[0];

  assert.ok(consumeOverlayState, "overlay state consumer should be present");
  assert.match(source, /activeOverlaySource: ""/);
  assert.match(source, /setPluginStoreRemoteActive\(true, \{ source: "plugin-store" \}\)/);
  assert.match(source, /setPluginStoreRemoteActive\(true, \{ source: "unifystore" \}\)/);
  assert.match(consumeOverlayState, /overlaySource !== bridge\.activeOverlaySource/);
  assert.match(consumeOverlayState, /source: overlaySource/);
});
