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
const xboxHost = fs.readFileSync(
  path.join(root, "src", "ToolsForSteam.XboxHost", "Program.cs"),
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

test("Xbox Mode reads the same artwork mode and custom image path", () => {
  assert.match(xboxHost, /GetString\(splash, "artworkMode"\)/);
  assert.match(xboxHost, /GetString\(splash, "customImagePath"\)/);
  assert.match(xboxHost, /StartupSplashArtworkMode\.Custom/);
  assert.doesNotMatch(xboxHost, /settings\.Enabled|settings\.ShowText|settings\.IconPath/);
});
