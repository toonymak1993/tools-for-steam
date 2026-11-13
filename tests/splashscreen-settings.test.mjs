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

test("splash settings expose one dynamic or custom artwork choice", () => {
  assert.match(popup, /Dynamic Library Artwork/);
  assert.match(popup, /Save and Use Custom Image/);
  assert.match(popup, /api\/settings\/splash\/artwork-mode/);
  assert.match(popup, /api\/settings\/splash\/custom-image/);
  assert.match(popup, /api\/settings\/splash\/select-custom-image/);
  assert.doesNotMatch(popup, /Show Splash Text|Save Wallpaper|Save Icon/);
  assert.doesNotMatch(popup, /api\/settings\/splash\/(?:enabled|show-text|wallpaper|icon)/);
});

test("splash settings describe the Console Mode start without the removed Xbox Mode", () => {
  assert.match(popup, /The splashscreen covers the Console Mode start until Steam is ready\./);
  assert.doesNotMatch(popup, /Xbox Mode/);
});

test("startup settings offer only Console Mode and eTray (Shell Takeover was removed)", () => {
  assert.doesNotMatch(popup, /Shell Takeover/);
  assert.doesNotMatch(popup, /setStartupMode\("shell"\)/);
  assert.doesNotMatch(popup, /windows-shell-start-delay/);
  assert.match(popup, /setStartupMode\("console"\)/);
  assert.match(popup, /setStartupMode\("tray"\)/);
});
