import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");

test("TFS Curtain bridges Steam input into a native fail-open launch surface", () => {
  const surface = read("src/SteamLoader.App/Assets/curtain-surface.js");
  const service = read("src/SteamLoader.App/Infrastructure/Curtain/CurtainService.cs");
  const overlay = read("src/SteamLoader.App/Infrastructure/Curtain/CurtainOverlayHost.cs");
  const detector = read("src/SteamLoader.App/Infrastructure/Curtain/CurtainForegroundDetector.cs");

  assert.match(surface, /TFS CURTAIN/);
  assert.match(surface, /RegisterForGameActionStart/);
  assert.match(surface, /api\/curtain\/launch/);
  assert.match(surface, /startGamepadDismissPolling/);
  assert.match(service, /MaximumCurtainDuration = TimeSpan\.FromSeconds\(45\)/);
  assert.match(service, /candidate\.IsFullscreen/);
  assert.match(overlay, /WsExNoActivate/);
  assert.match(overlay, /ShowActivated = false/);
  assert.match(detector, /EpicGamesLauncher/);
  assert.match(detector, /steamwebhelper/);
});

test("TFS Curtain can be disabled for one game without disabling every game", () => {
  const surface = read("src/SteamLoader.App/Assets/curtain-surface.js");
  const artworkSurface = read("src/SteamLoader.App/Assets/artwork-surface.js");
  const store = read("src/SteamLoader.App/Infrastructure/Curtain/CurtainSettingsStore.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");

  assert.match(surface, /disabledAppIds/);
  assert.match(surface, /toggleGame/);
  assert.match(artworkSurface, /TFS Curtain: \$\{enabled \? "On" : "Off"\}/);
  assert.match(artworkSurface, /tfs-curtain-toggle/);
  assert.match(store, /DisabledAppIds/);
  assert.match(api, /api\/curtain\/game-enabled/);
  assert.match(api, /SetGameEnabled\(payload\.AppId, payload\.Enabled\)/);
});
