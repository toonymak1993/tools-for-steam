import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

const popupSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url),
  "utf8",
);
const apiSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs", import.meta.url),
  "utf8",
);
const hdrSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Infrastructure/SystemTools/HdrDisplayService.cs", import.meta.url),
  "utf8",
);
const bluetoothSource = fs.readFileSync(
  new URL(
    "../src/SteamLoader.App/Infrastructure/SystemTools/BluetoothDeviceService.cs",
    import.meta.url,
  ),
  "utf8",
);

test("System exposes Windows Update, HDR, and a Bluetooth subpage", () => {
  assert.match(popupSource, /makeNavigationSlot\(\s*"Windows Update"/);
  assert.match(popupSource, /makeSettingToggleSlot\(\s*"system",\s*"hdr"/);
  assert.match(popupSource, /pageId:\s*"system-bluetooth"/);
  assert.match(popupSource, /pageId === "system-windows-update"/);
  assert.match(popupSource, /pageId === "system-bluetooth"/);
});

test("System APIs expose refresh, bounded discovery, pairing, and native update handoff", () => {
  for (const endpoint of [
    "/api/system/windows-update/scan",
    "/api/system/windows-update/run",
    "/api/system/hdr/enabled",
    "/api/system/bluetooth/refresh",
    "/api/system/bluetooth/scan",
    "/api/system/bluetooth/settings",
    "/api/system/bluetooth/pair",
  ]) {
    assert.match(apiSource, new RegExp(endpoint.replaceAll("/", "\\/")));
  }

  assert.match(popupSource, /runArmedUntil/);
  assert.match(popupSource, /Press again within five seconds/);
});

test("System discovery work stays scoped and performance-conscious", () => {
  assert.match(hdrSource, /QueryOnlyActivePaths/);
  assert.match(bluetoothSource, /TimeSpan\.FromSeconds\(12\)/);
  assert.match(bluetoothSource, /DeviceWatcher/);
  assert.match(popupSource, /getBluetoothDeviceSnapshot\(\)\?\.scanning !== true/);
  assert.match(popupSource, /}, 900\)/);
});
