import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const popupSource = fs.readFileSync(
  path.join(root, "src", "SteamLoader.App", "Assets", "quickaccess-popup.js"),
  "utf8",
);

test("OEM Software and Button Mapping are separate compatible-handheld-only settings directories", () => {
  assert.match(popupSource, /id:\s*"oem-software"[\s\S]*requiresOemSoftware:\s*true/);
  assert.match(popupSource, /id:\s*"button-mapping"[\s\S]*requiresOemSoftware:\s*true/);
  assert.match(popupSource, /pageId === "oem-software"/);
  assert.match(popupSource, /pageId === "button-mapping"/);
  assert.match(popupSource, /button-mapping-button-/);
  assert.match(popupSource, /page\?\.requiresOemSoftware !== true \|\| state\.generalSettings\.snapshot\?\.oemSoftwareAvailable === true/);
  assert.match(popupSource, /api\/oem-software\/enabled/);
  assert.match(popupSource, /api\/oem-software\/buttons\/capture/);
  assert.match(popupSource, /api\/oem-software\/buttons\/binding/);
  assert.match(popupSource, /"Start Live Detect"/);
  assert.match(popupSource, /this replacement is mandatory/);
  assert.doesNotMatch(popupSource, /Use TFS Button Control/);
});

test("supported handheld UI haptics are centralized and configurable", () => {
  assert.match(popupSource, /function requestUiHaptic\(kind\)/);
  assert.match(popupSource, /api\/controller\/ui-haptic/);
  assert.match(popupSource, /api\/oem-software\/ui-haptics-enabled/);
  assert.match(popupSource, /requestUiHaptic\("confirm"\)/);
  assert.match(popupSource, /requestUiHaptic\("back"\)/);
  assert.match(popupSource, /requestUiHaptic\("move"\)/);
  assert.match(popupSource, /"UI Haptics"/);
});
