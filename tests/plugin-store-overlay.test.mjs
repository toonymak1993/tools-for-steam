import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const backgroundHostUrl = new URL("../src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs", import.meta.url);

test("plugin activator opens inside the wide Quick Access workspace", async () => {
  const source = await readFile(popupUrl, "utf8");

  assert.match(source, /id: "plugins",[\s\S]*?title: "Plugin Activator"/);
  assert.match(
    source,
    /key: "plugins"[\s\S]*?const targetRoute = \{ screen: "page", pluginId: "settings", pageId: "plugins" \}[\s\S]*?setRoute\(targetRoute\)/,
  );
  assert.match(source, /panelClassName: "steamloader-panel-plugin-activator"/);
  assert.doesNotMatch(source, /function openPluginStoreOverlay\(/);
  assert.doesNotMatch(source, /api\/plugin-activator\/overlay\/open/);
});

test("integrated plugin activator uses visual two-column toggle cards", async () => {
  const source = await readFile(popupUrl, "utf8");

  assert.match(source, /getPluginActivatorImageUrl\(plugin\.id\)/);
  assert.match(source, /api\/plugin-activator\/images\//);
  assert.match(source, /makeSettingToggleSlot\([\s\S]*?"tfs-plugin"[\s\S]*?layout: "feature"/);
  assert.match(source, /slotKey: `plugin-activator-\$\{plugin\.id\}`/);
  assert.match(source, /badge: canDisable \? \(active \? "Active" : "Inactive"\) : "Required"/);
  assert.match(source, /footerLabel: isSaving[\s\S]*?"Deactivate"[\s\S]*?"Activate"[\s\S]*?"Always active"/);
  assert.match(
    source,
    /\.steamloader-panel-plugin-activator[\s\S]*?steamloader-plugin-activator-card\[data-slot-layout="feature"\][\s\S]*?height: 100%;/,
  );
});

test("plugin activation keeps safe controller navigation and live state", async () => {
  const source = await readFile(popupUrl, "utf8");

  assert.match(source, /fallbackSlotKey: "header-action:plugins"/);
  assert.match(source, /\{ button: "A", label: "Activate \/ Deactivate" \}/);
  assert.match(source, /\{ button: "B", label: "Back" \}/);
  assert.match(source, /state\.generalSettings\.pluginToggleBusyId = pluginId/);
  assert.match(source, /"api\/settings\/plugins\/enabled"/);
  assert.match(source, /topics\.add\("plugin-store\.state"\)/);
});

test("the retired full-screen plugin activator is no longer injected", async () => {
  const source = await readFile(backgroundHostUrl, "utf8");
  const popupScript = source.match(/var popupScript = string\.Join\([\s\S]*?\);/)?.[0];
  const themeSurfaceScript = source.match(/var themeSurfaceScript = string\.Join\([\s\S]*?;\r?\n\r?\n        var appStartService/)?.[0];

  assert.ok(popupScript);
  assert.ok(themeSurfaceScript);
  assert.doesNotMatch(popupScript, /plugin-store-overlay\.js/);
  assert.doesNotMatch(themeSurfaceScript, /plugin-store-overlay\.js/);
});
