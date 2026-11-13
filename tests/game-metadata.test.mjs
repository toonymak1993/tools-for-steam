import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const surface = read("src/SteamLoader.App/Assets/omnilibrary-metadata-surface.js");
const endpoints = read("src/SteamLoader.App/Hosting/GameMetadataApiEndpoints.cs");
const server = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
const catalog = read("src/SteamLoader.App/Infrastructure/Settings/SteamLoaderPluginCatalog.cs");

test("TFS Metadata is a built-in feature toggle with its own API prefix", () => {
  assert.match(catalog, /new\("metadata", "Metadata",[^)]*true\)/);
  assert.match(server, /\["\/api\/metadata"\] = "metadata"/);
  assert.match(server, /GameMetadataApiEndpoints\.TryHandleAsync\(/);
  // A disabled feature answers its state route instead of failing the surface.
  assert.match(server, /"\/api\/metadata\/state"[\s\S]{0,200}enabled = false/);
});

test("metadata routes only accept non-Steam shortcut app IDs", () => {
  assert.match(endpoints, /appId >= 0x80000000u/);
  assert.match(endpoints, /SteamLoaderApiServer\.TryNormalizeMetadataUrl/);
});

test("game page surface keeps OmniLibrary games on the OmniLibrary route", () => {
  assert.match(
    surface,
    /function metadataGameUrl\([\s\S]*?isOmniLibraryAppId\(appId\)[\s\S]*?api\/unifystore\/metadata\/games\/[\s\S]*?api\/metadata\/games\//,
  );
  assert.match(surface, /function isManagedAppId\(value\) \{\s*return isOmniLibraryAppId\(value\) \|\| isShortcutMetadataAppId\(value\);/);
  assert.match(surface, /api\/metadata\/state/);
});

function loadEditorHooks() {
  const window = { setTimeout: () => 0, clearTimeout: () => {}, fetch: async () => ({ ok: false, json: async () => null }) };
  window.window = window;
  const context = vm.createContext({ window, URLSearchParams, console, Date, Math, Number, String, Array, Set, JSON });
  vm.runInContext(read("src/SteamLoader.App/Assets/metadata-editor.js"), context);
  return { hooks: window.__tfsMetadataTestHooks, runtime: window.__tfsMetadata };
}

test("metadata editor turns its draft into edits with blank fields as automatic", () => {
  const { hooks } = loadEditorHooks();
  assert.deepEqual([...hooks.splitList(" Indie, indie ;Big Co,,\nThird")], ["Indie", "Big Co", "Third"]);
  const edits = hooks.editsFromDraft({
    description: "  ",
    developers: "A, B",
    publishers: "",
    genres: "RPG",
    releaseDate: " 2024 ",
    rating: 140,
  });
  assert.equal(edits.description, null);
  assert.deepEqual([...edits.developers], ["A", "B"]);
  assert.equal(edits.publishers, null);
  assert.equal(edits.releaseDate, "2024");
  assert.equal(edits.rating, 100);
  const draft = hooks.draftFromEdits({ developers: ["A", "B"], rating: 80 });
  assert.equal(draft.developers, "A, B");
  assert.equal(draft.description, "");
  assert.equal(draft.rating, 80);
});

test("metadata editor columns form one row focus group for D-pad left/right", () => {
  const editor = read("src/SteamLoader.App/Assets/metadata-editor.js");
  assert.match(editor, /h\(Focusable \|\| "div", \{\s*\.\.\.\(Focusable \? \{ "flow-children": "row" \} : \{\}\)/);
});

test("metadata editor only opens for non-Steam games", () => {
  const { runtime } = loadEditorHooks();
  assert.equal(runtime.openEditor(440), false);
  assert.equal(runtime.hasEditor(440), false);
});

test("synthetic activity events satisfy Steam's BCanRender/preview contract", () => {
  // Steam client 2026-11 calls event.BCanRender() and renders event.preview.
  assert.match(surface, /BCanRender: \(\) => true/);
  assert.match(surface, /LoadDetails: async \(\) => false/);
  assert.match(surface, /strTitle: partnerEvent\.GetNameWithFallback\(\)/);
});

test("shortcuts without matched metadata keep Steam's own non-Steam page", () => {
  assert.match(surface, /isShortcutMetadataAppId\(appId\) && !hasShortcutMetadata\(snapshot\)/);
  assert.match(surface, /isShortcutMetadataAppId\(appId\) && !state\.nativeSnapshots\.has\(appId\)/);
  // No "achievements unavailable" card for shortcuts without a source.
  assert.match(surface, /achievementStatus !== "none"/);
});
