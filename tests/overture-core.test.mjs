import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const source = read("src/SteamLoader.App/Assets/overture-core.js");

function loadInternals() {
  const window = { __tfsOvertureTestHooks: true };
  const context = vm.createContext({ window, URL, console });
  const result = vm.runInContext(source, context, { filename: "overture-core.js" });
  assert.equal(result, "test");
  return window.__tfsOvertureInternals;
}

test("only the game details page starts a game's music, sub pages keep it", () => {
  const { gameFromPath, targetFor } = loadInternals();
  assert.deepEqual({ ...gameFromPath("/library/app/440") }, { appId: 440, details: true });
  assert.deepEqual({ ...gameFromPath("/library/app/440/achievements") }, { appId: 440, details: false });
  assert.equal(gameFromPath("/library/home"), null);
  assert.equal(gameFromPath("/library/app/0"), null);

  assert.equal(targetFor("/library/app/440", "", {}), "440");
  assert.equal(targetFor("/library/app/440/achievements", "440", {}), "440");
  assert.equal(targetFor("/library/app/440/achievements", "", {}), "");
  assert.equal(targetFor("/library/home", "440", {}), "");
  assert.equal(targetFor("/library/home", "440", { ambient: true }), "ambient");
  assert.equal(targetFor("/library/app/570", "440", { ambient: true }), "570");
});

test("the settings pages never play ambient music", () => {
  const { targetFor, isEditorPath } = loadInternals();
  assert.equal(isEditorPath("/tfs/overture"), true);
  assert.equal(isEditorPath("/tfs/overture/440"), true);
  assert.equal(isEditorPath("/tfs/trailers/440"), false);
  assert.equal(targetFor("/tfs/overture/440", "ambient", { ambient: true }), "");
  assert.equal(targetFor("", "", { ambient: true }), "");
});

test("volume combines master, playlist and trailer ducking", () => {
  const { targetVolume } = loadInternals();
  assert.equal(targetVolume(60, 100, false), 0.6);
  assert.equal(targetVolume(60, 50, false), 0.3);
  assert.equal(targetVolume(60, undefined, false), 0.6);
  assert.equal(targetVolume(150, 100, false), 1);
  assert.equal(targetVolume(60, 100, true), 0);
});

test("playlist order loops only when asked", () => {
  const { nextIndex, formatDuration } = loadInternals();
  assert.equal(nextIndex(0, 3, false), 1);
  assert.equal(nextIndex(2, 3, false), -1);
  assert.equal(nextIndex(2, 3, true), 0);
  assert.equal(nextIndex(0, 0, true), -1);
  assert.equal(formatDuration(181), "3:01");
  assert.equal(formatDuration(0), "");
});

test("prefetch takes the most recently played installed games, shortcuts included", () => {
  const { recentInstalledGames } = loadInternals();
  const apps = [
    { appid: 1, display_name: "Old", installed: true, app_type: 1, rt_last_time_played: 100 },
    { appid: 2, display_name: "New", installed: true, app_type: 1, rt_last_time_played: 300 },
    { appid: 3, display_name: "Not installed", installed: false, app_type: 1, rt_last_time_played: 900 },
    { appid: 4, display_name: "Never played", installed: true, app_type: 1, rt_last_time_played: 0 },
    { appid: 3000000000, display_name: "Shortcut", installed: true, app_type: 1073741824, rt_last_time_played: 200 },
    { appid: 5, display_name: "A tool", installed: true, app_type: 4, rt_last_time_played: 999 },
  ];
  assert.deepEqual(recentInstalledGames(apps, 30).map((game) => game.appId), [2, 3000000000, 1]);
  assert.deepEqual(recentInstalledGames(apps, 1).map((game) => game.title), ["New"]);
  assert.deepEqual(recentInstalledGames(undefined, 30).length, 0);
});

/** Runs the real runtime against a small fake Big Picture and API. */
function startRuntime() {
  const env = { path: "/library/app/440", listeners: [], audios: [], resolveAuto: null };
  class FakeAudio {
    constructor() { this.paused = true; this.volume = 0; env.audios.push(this); }
    addEventListener() {}
    load() {}
    play() { this.paused = false; return Promise.resolve(); }
    pause() { this.paused = true; }
    removeAttribute() {}
  }
  const element = () => ({ className: "", classList: { add() {}, remove() {} }, setAttribute() {}, append() {}, remove() {}, querySelector: () => ({}) });
  const doc = { visibilityState: "visible", getElementById: () => null, createElement: element, head: { append() {} }, body: { append() {} } };
  const main = {
    BrowserWindow: { document: doc, Audio: FakeAudio },
    m_history: { location: { get pathname() { return env.path; } }, listen(callback) { env.listeners.push(callback); return () => {}; } },
  };
  const json = (body) => ({ ok: true, status: 200, json: async () => body });
  const fetch = async (url) => {
    const text = String(url);
    if (text.includes("api/overture/settings")) return json({ enabled: true, delaySeconds: 0, ambientEnabled: false, autoFind: true, silentAppIds: [] });
    if (text.includes("api/overture/playlist")) return json({ enabled: true, tracks: [], canAutoFind: true, volume: 100 });
    if (text.includes("api/overture/auto")) {
      return new Promise((resolve) => {
        env.resolveAuto = () => resolve(json({ enabled: true, volume: 100, loop: true, tracks: [{ id: "yt-x", audioPath: "api/overture/audio?track=yt-x", title: "Theme" }] }));
      });
    }
    return json({});
  };
  const window = {
    __tfsSurfaceRole: "shared",
    __steamLoaderApiBase: "http://127.0.0.1:47652/",
    SteamUIStore: { WindowStore: { GamepadUIMainWindowInstance: main }, RunningApps: [] },
    appStore: { GetAppOverviewByAppID: () => ({ display_name: "Team Fortress 2" }) },
    setTimeout, clearTimeout, setInterval, clearInterval,
  };
  vm.runInContext(source, vm.createContext({ window, fetch, URL, console }), { filename: "overture-core.js" });
  env.runtime = window.__tfsOverture;
  env.navigate = (path) => { env.path = path; env.listeners.forEach((listener) => listener({ pathname: path })); };
  return env;
}

async function waitFor(check, ms = 2000) {
  const started = Date.now();
  while (!check()) {
    if (Date.now() - started > ms) throw new Error("timed out");
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

test("leaving the game page while its theme is still downloading plays nothing", async () => {
  const env = startRuntime();
  try {
    await waitFor(() => env.resolveAuto);
    env.navigate("/library/home");
    env.resolveAuto();
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.equal(env.audios.length, 0);
    assert.equal(env.runtime.session, null);
  } finally {
    env.runtime.destroy();
  }
});

test("staying on the game page (or its sub pages) plays the theme once it is found", async () => {
  const env = startRuntime();
  try {
    await waitFor(() => env.resolveAuto);
    env.navigate("/library/app/440/achievements");
    env.resolveAuto();
    await waitFor(() => env.runtime.session);
    assert.equal(env.audios.length, 1);
    assert.equal(env.runtime.session.key, "440");
  } finally {
    env.runtime.destroy();
  }
});

test("runtime registers its routes, stops on game launch and respects trailer sound", () => {
  assert.match(source, /editorRoutes = \["\/tfs\/overture\/:appId", "\/tfs\/overture"\]/);
  assert.match(source, /RegisterForAppLifetimeNotifications/);
  assert.match(source, /window\.__tfsTrailers\?\.session/);
  assert.match(source, /session\.muted === false/);
  assert.match(source, /window\.__tfsOverture = \{/);
  // Audio elements cannot send the session header: the token goes in the query.
  assert.match(source, /__steamLoaderApiUrl/);
});

test("the shared bundle loads the runtime and the API knows the plugin", () => {
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const server = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const catalog = read("src/SteamLoader.App/Infrastructure/Settings/SteamLoaderPluginCatalog.cs");
  assert.match(host, /Assets\/overture-core\.js/);
  assert.match(host, /Overture = new Infrastructure\.Overture\.OvertureService/);
  assert.match(server, /OvertureApiEndpoints\.TryHandleAsync/);
  assert.match(server, /\["\/api\/overture"\] = "overture"/);
  assert.match(catalog, /new\("overture", "TFS Overture"/);
});
