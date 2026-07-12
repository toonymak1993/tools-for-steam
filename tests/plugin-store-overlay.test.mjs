import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("store snapshot refresh preserves the active controller focus zone", async () => {
  const source = await readFile(
    new URL("../src/SteamLoader.App/Assets/plugin-store-overlay.js", import.meta.url),
    "utf8",
  );
  const loadSnapshot = source.match(
    /async function loadSnapshot\(force = false\) \{[\s\S]*?\n  \}\n\n  async function setOverlayOpen/,
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
