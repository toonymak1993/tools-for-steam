import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const popup = fs.readFileSync(
  path.join(root, "src", "SteamLoader.App", "Assets", "quickaccess-popup.js"),
  "utf8",
);

test("settings expose four configurable overlay combinations and hold times", () => {
  assert.match(popup, /id: "overlay"/);
  assert.match(popup, /id: "steam-menu"/);
  assert.match(popup, /id: "steam-quick-access"/);
  assert.match(popup, /id: "in-game-overlay"/);
  assert.match(popup, /id: "in-game-quick-access"/);
  assert.match(popup, /api\/settings\/overlay\/combination/);
  assert.match(popup, /api\/settings\/overlay\/hold-time/);
  assert.match(popup, /api\/settings\/overlay\/reset/);
  assert.match(popup, /steamMenuButtons/);
  assert.match(popup, /inGameQuickAccessButtons/);
});

test("combination editor is controller-friendly and enforces safe selection limits", () => {
  assert.match(popup, /overlay-combination-/);
  assert.match(popup, /Combination Preview/);
  assert.match(popup, /Record Combination/);
  assert.match(popup, /api\/settings\/overlay\/input-state/);
  assert.match(popup, /Release all controller buttons to arm the recorder/);
  assert.match(popup, /combination\.length <= 1/);
  assert.match(popup, /combination\.length >= 3/);
  assert.match(popup, /Use Recommended Combination/);
});

test("external-game return waits until Quick Access is ready", () => {
  const visibilityHandler = popup.match(
    /function ensureExternalGameQuickAccessVisibilityHandler\(\) \{[\s\S]*?\r?\n  \}\r?\n\r?\n  function scheduleExternalGameToolsTabOpen/,
  )?.[0];

  assert.ok(visibilityHandler, "external-game visibility handler should be present");
  assert.match(visibilityHandler, /snapshot\?\.quickAccessReady !== true/);
  assert.match(visibilityHandler, /api\/external-game-quick-access\/return-game/);
});
