import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const popupSource = readFileSync(
  new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url),
  "utf8",
);
const serviceSource = readFileSync(
  new URL("../src/SteamLoader.App/Infrastructure/AppStart/AppStartService.cs", import.meta.url),
  "utf8",
);
const apiSource = readFileSync(
  new URL("../src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs", import.meta.url),
  "utf8",
);

test("App Start indexes desktop and packaged apps incrementally", () => {
  assert.match(serviceSource, /Get-StartApps/);
  assert.match(serviceSource, /AppStartSourceKinds\.Packaged/);
  assert.match(serviceSource, /string\.Equals\(previous\.Fingerprint, discovered\.Fingerprint/);
  assert.match(serviceSource, /AutomaticRefreshInterval/);
});

test("App Start exposes one-click launch, favorites, and visibility management", () => {
  assert.match(popupSource, /"Favorites", "Your pinned apps, ready with one click\."/);
  assert.match(popupSource, /\(\) => launchAppStartShortcut\(shortcut\.id\)/);
  assert.match(popupSource, /"Manage Apps"/);
  assert.match(popupSource, /"Restore to Launcher"/);
  assert.match(apiSource, /\/api\/app-start\/apps\/favorite/);
  assert.match(apiSource, /\/api\/app-start\/catalog\/refresh/);
});

test("every App Start launch hands the target window to the foreground service", () => {
  assert.match(serviceSource, /ActivateLaunchedAppWhenReady\(/);
  assert.match(serviceSource, /windowsBeforeLaunch/);
});
