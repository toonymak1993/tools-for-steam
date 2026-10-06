import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const overlayUrl = new URL("../src/SteamLoader.App/Assets/plugin-store-overlay.js", import.meta.url);

test("plugin activator is a simple included-plugin list", async () => {
  const source = await readFile(overlayUrl, "utf8");

  assert.match(source, /Plugin Activator/);
  assert.match(source, /snapshot\?\.builtInPlugins/);
  assert.match(source, /plugin\?\.images\?\.\[0\]\?\.url/);
  assert.match(source, /plugin\?\.description/);
  assert.match(source, /role", "switch"/);
  assert.match(source, /Activate/);
  assert.match(source, /Deactivate/);
  assert.match(source, /api\/settings\/plugins\/enabled/);
});

test("plugin activator has no catalog or community actions", async () => {
  const source = await readFile(overlayUrl, "utf8");

  assert.doesNotMatch(source, /communityPlugins/);
  assert.doesNotMatch(source, /plugins\/install/);
  assert.doesNotMatch(source, /plugins\/uninstall/);
  assert.doesNotMatch(source, /plugins\/update/);
  assert.doesNotMatch(source, /plugin-store\/refresh/);
  assert.doesNotMatch(source, /Search plugins/);
});

test("plugin activator keeps controller navigation and safe host selection", async () => {
  const source = await readFile(overlayUrl, "utf8");

  assert.match(source, /function isQuickAccessSurface\(\)/);
  assert.match(source, /document\.visibilityState !== "hidden"/);
  assert.match(source, /!isQuickAccessSurface\(\)/);
  assert.match(source, /ArrowUp: "up"/);
  assert.match(source, /GamepadA: "a"/);
  assert.match(source, /GamepadB: "b"/);
  assert.match(source, /normalized === "b"/);
  assert.match(source, /void closeOverlay\(\)/);
  assert.match(source, /scrollIntoView\(\{ block: "nearest"/);
});

test("plugin activator preserves the cross-surface overlay bridge", async () => {
  const source = await readFile(overlayUrl, "utf8");

  assert.match(source, /source: "plugin-store"/);
  assert.match(source, /api\/plugin-activator\/overlay\/state/);
  assert.match(source, /api\/plugin-activator\/overlay\/input\?after=/);
  assert.match(source, /stopAnnouncements\(false\)/);
  assert.match(source, /if \(announceClosed && wasAnnouncing\) announceOverlay\(false\)/);
  assert.match(source, /Date\.now\(\) \+ 360/);
});
