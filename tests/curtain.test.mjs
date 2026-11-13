import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");

function loadCurtainCore(windowOverrides = {}) {
  const timers = [];
  const window = {
    __tfsSurfaceRole: "shared",
    __steamLoaderApiBase: "http://127.0.0.1:1/",
    setTimeout: (fn) => { timers.push(fn); return timers.length; },
    clearTimeout: () => {},
    ...windowOverrides,
  };
  const context = vm.createContext({
    window,
    console,
    fetch: async () => ({ ok: true, status: 200, json: async () => ({ enabled: true, disabledAppIds: [], settings: { mode: "modern" } }) }),
    BigInt,
    Promise,
    Number,
    String,
    Object,
    Array,
    Math,
    Date,
    Set,
    Map,
    JSON,
  });
  const result = vm.runInContext(read("src/SteamLoader.App/Assets/curtain-core.js"), context);
  return { window, result };
}

test("TFS Curtain hooks Steam's launch calls in the shared context, not DOM play buttons", () => {
  const core = read("src/SteamLoader.App/Assets/curtain-core.js");
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");

  for (const method of ["RunGame", "RunGameAndWaitForInstaller", "RunShortcut"]) {
    assert.match(core, new RegExp(`"${method}"`));
  }
  assert.match(core, /RegisterForAppLifetimeNotifications/);
  assert.match(core, /api\/curtain\/confirm/);
  assert.match(core, /GamepadUIMainWindowInstance/);
  assert.doesNotMatch(core, /addEventListener\("click", handleLaunchEvent/);
  // Loaded once, next to SteamClient, as part of the shared bundle.
  assert.match(host, /Assets\/quickaccess-shell\.js"\),[\s\S]*?Assets\/curtain-core\.js"\)/);
});

test("the shared curtain runtime only runs in SharedJSContext", () => {
  const { result, window } = loadCurtainCore({ __tfsSurfaceRole: "gamepad" });
  assert.equal(result, "skipped");
  assert.equal(window.__tfsCurtain, undefined);
});

test("game ids from RunGame/RunShortcut map to Steam app ids", () => {
  const { window } = loadCurtainCore();
  const { normalizeAppId, appIdFromArgs } = window.__tfsCurtain._internals;
  assert.equal(normalizeAppId("1091500"), 1091500);
  assert.equal(normalizeAppId(1091500), 1091500);
  // Shortcut game id = (appid << 32) | 0x02000000.
  const shortcutAppId = 3123456789;
  const gameId = ((BigInt(shortcutAppId) << 32n) | 0x02000000n).toString();
  assert.equal(normalizeAppId(gameId), shortcutAppId);
  assert.equal(normalizeAppId(-1171510507), 3123456789);
  assert.equal(normalizeAppId("not a number"), 0);
  assert.equal(appIdFromArgs([gameId, "", -1, 100]), shortcutAppId);
  assert.equal(appIdFromArgs([{ unAppID: 20920 }]), 20920);
  window.__tfsCurtain.destroy();
});

test("the curtain look is clamped and shared by the live curtain and the editor preview", () => {
  const { window } = loadCurtainCore();
  const { layout } = window.__tfsCurtain._internals;
  const look = layout({ backgroundOpacity: 140, backgroundScale: 50, backgroundX: 10, logoX: 30, logoY: 70, logoWidth: 5, logoShadow: false });
  assert.equal(look.background.opacity, "1");
  assert.equal(look.background.transform, "scale(1)");
  assert.equal(look.background.objectPosition, "10% 50%");
  assert.equal(look.logo.left, "30%");
  assert.equal(look.logo.top, "70%");
  assert.equal(look.logo.width, "10%");
  assert.equal(look.logo.filter, "none");
  window.__tfsCurtain.destroy();
});

test("the editor is a Steam route built from Steam's own components", () => {
  const core = read("src/SteamLoader.App/Assets/curtain-core.js");
  assert.match(core, /"\/tfs\/curtain\/:appId"/);
  assert.match(core, /core\.routes\.add\(editorRoute/);
  for (const component of ["ToggleField", "SliderField", "Dropdown", "DialogButton", "Focusable"]) {
    assert.match(core, new RegExp(`ui\\.${component}`));
  }
  assert.match(core, /api\/curtain\/game\/reset/);
  // Choices must not be cut off: Steam's real dropdown row (not the audio
  // device picker), choice below its label, menu not forced to button width.
  assert.match(core, /ui\.find\?\.\(\["dropDownControlRef", "explainerTitle"\]\)/);
  assert.match(core, /layout: "below"/);
  assert.match(core, /bMatchWidth: false/);
});

test("the curtain steps aside: unconfirmed launches, Steam prompts and B", () => {
  const core = read("src/SteamLoader.App/Assets/curtain-core.js");
  assert.match(core, /speculativeMs = 3500/);
  assert.match(core, /dismiss\("unconfirmed"\)/);
  assert.match(core, /dismiss\("steam-prompt"\)/);
  // Big Picture's own gamepad DOM events; SteamClient.Input messages carry
  // action ids, not buttons.
  assert.match(core, /"vgp_onbuttondown"/);
  assert.match(core, /"vgp_oncancel"/);
  assert.match(core, /Number\(detail\.button\) === 2 && !detail\.is_repeat/);
  assert.doesNotMatch(core, /RegisterForControllerInputMessages/);
  assert.match(core, /is-fading/);
});

test("window surfaces only bridge to the shared runtime and retire the old DOM hook", () => {
  const surface = read("src/SteamLoader.App/Assets/curtain-surface.js");
  const artworkSurface = read("src/SteamLoader.App/Assets/artwork-surface.js");
  assert.match(surface, /const version = 2;/);
  assert.match(surface, /window\.opener\?\.__tfsCurtain/);
  assert.match(surface, /steamtools-curtain-instant/);
  assert.doesNotMatch(surface, /addEventListener\(/);
  assert.match(artworkSurface, /curtain\?\.openEditor\?\.\(appId\)/);
  assert.match(artworkSurface, /TFS Curtain\.\.\. \(/);
  assert.match(artworkSurface, /tfs-curtain-toggle/);
});

test("the backend pins Steam, hides store launchers and always fails open", () => {
  const service = read("src/SteamLoader.App/Infrastructure/Curtain/CurtainService.cs");
  const tracker = read("src/SteamLoader.App/Infrastructure/Curtain/CurtainLaunchTracker.cs");
  const overlay = read("src/SteamLoader.App/Infrastructure/Curtain/CurtainOverlayHost.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");

  assert.match(service, /SetTopmost\(steamWindow, true\)/);
  assert.match(service, /SetTopmost\(session\.PinnedSteamWindow, false\)/);
  assert.match(service, /RecoverFromPreviousRun/);
  assert.match(service, /"unconfirmed"/);
  assert.match(service, /"attention"/);
  assert.match(service, /"timeout"/);
  assert.match(tracker, /EADesktop/);
  assert.match(tracker, /UbisoftConnect/);
  assert.match(tracker, /steamwebhelper/);
  assert.match(overlay, /WsExNoActivate/);
  assert.match(overlay, /ShowActivated = false/);
  for (const route of ["state", "launch", "confirm", "dismiss", "settings", "game", "game/reset", "image", "game-enabled"]) {
    assert.match(api, new RegExp(`/api/curtain/${route.replace("/", "\\/")}"`));
  }
});

test("TFS Curtain has global settings in the Quick Access tab", () => {
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  assert.match(popup, /id: "curtain",[\s\S]*?pages: \[\s*\{\s*id: "settings"/);
  assert.match(popup, /api\/curtain\/settings/);
  assert.match(popup, /state\.route\.pluginId === "curtain" &&/);
});
