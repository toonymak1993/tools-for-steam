import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

// vm realms: compare plain copies.
const deepEqual = (actual, expected, message) => assert.deepEqual(JSON.parse(JSON.stringify(actual)), expected, message);
const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const source = read("src/SteamLoader.App/Assets/download-core.js");
const hltbSource = read("src/SteamLoader.App/Assets/hltb-core.js");

const fakeReact = {
  createElement(type, props, ...children) {
    return { type, key: props?.key ?? null, props: { ...(props || {}), ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}) } };
  },
  cloneElement(element, props, ...children) {
    return { ...element, props: { ...element.props, ...(props || {}), ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}) } };
  },
  Fragment: "Fragment",
};

function load(tfs = null, extra = {}) {
  const window = {
    __tfs: tfs,
    setTimeout: () => 0,
    clearTimeout: () => {},
    ...extra,
  };
  const context = vm.createContext({ window, URL, URLSearchParams, console, fetch: async () => ({ ok: false, status: 500, json: async () => null }) });
  const result = vm.runInContext(source, context, { filename: "download-core.js" });
  assert.equal(result, "injected");
  return { window, context };
}

const appId = 0x9a000001;
const record = (game, entry = null, storeId = "epic-games") => ({
  storeId,
  entry,
  game: { id: "fn", title: "Game", steamAppId: appId, installed: false, cloudPlayable: false, download: { status: "idle" }, ...game },
});

test("only OmniLibrary shortcuts that are not installed get a Download button", () => {
  const { describe } = load().window.__tfsOmniDownloads.internals;
  const idle = describe(record({}));
  assert.equal(idle.title, "Not installed");
  assert.equal(idle.detail, "Epic Games · Download it here, then press Play.");
  deepEqual(idle.actions.map((a) => [a.id, a.label]), [["install", "Download"]]);

  assert.equal(describe(record({ installed: true })), null, "installed and idle: no row");
  assert.equal(describe(record({ cloudPlayable: true }, null, "xbox-game-pass")), null, "cloud only: no row");
  assert.equal(describe(record({}, null, "rom-library")), null, "ROMs are always local");
  assert.equal(describe({ storeId: "", game: null }), null);

  const update = describe(record({ installed: true, updateAvailable: true }));
  assert.equal(update.title, "Update available");
  deepEqual(update.actions.map((a) => a.label), ["Update"]);

  const external = describe(record({ requiresExternalLauncher: true, providerDisplayName: "Ubisoft Connect" }));
  deepEqual(external.actions.map((a) => a.label), ["Install in Ubisoft Connect"]);
});

test("a running download shows live progress and Download Center controls", () => {
  const { describe } = load().window.__tfsOmniDownloads.internals;
  const view = describe(record({
    download: {
      status: "downloading",
      progressPercent: 42,
      downloadedBytes: 12 * 1024 ** 3,
      totalBytes: 40 * 1024 ** 3,
      downloadBytesPerSecond: 25 * 1024 ** 2,
    },
  }, { canPause: true, canCancel: true }));
  assert.equal(view.title, "Downloading · 42%");
  assert.equal(view.progress, 42);
  assert.equal(view.indeterminate, false);
  assert.match(view.detail, /^Epic Games · 12\.0 GB of 40\.0 GB · 25\.0 MB\/s · 19 min left$/);
  deepEqual(view.actions.map((a) => a.id), ["pause", "cancel"]);

  const preparing = describe(record({ download: { status: "preparing" } }));
  assert.equal(preparing.title, "Preparing download");
  assert.equal(preparing.indeterminate, true);
  deepEqual(preparing.actions, [], "no Pause before the Download Center offers it");

  const paused = describe(record({ download: { status: "paused", progressPercent: 60 } }, { canResume: true, canCancel: true }));
  assert.equal(paused.title, "Paused · 60%");
  deepEqual(paused.actions.map((a) => [a.id, a.label]), [["resume", "Resume"], ["cancel", "Cancel"]]);

  const failed = describe(record({ download: { status: "failed", detailText: "Disk full." } }));
  assert.equal(failed.tone, "error");
  assert.equal(failed.detail, "Epic Games · Disk full.");
  deepEqual(failed.actions.map((a) => [a.id, a.label]), [["install", "Retry"]]);
});

test("a finished download says Ready to play; Play pressed too early points at the row", () => {
  const { describe } = load().window.__tfsOmniDownloads.internals;
  const ready = describe(record({ installed: true, download: { status: "completed", progressPercent: 100 } }));
  assert.equal(ready.title, "Ready to play");
  assert.equal(ready.tone, "ready");
  deepEqual(ready.actions, []);

  const hinted = describe(record({}), { hintActive: true });
  assert.equal(hinted.title, "Not installed yet");
  assert.equal(hinted.tone, "attention");
  assert.match(hinted.detail, /Select Download first/);

  const busy = describe(record({}), { busyAction: "install" });
  assert.equal(busy.actions[0].label, "Working…");
});

test("action-required keeps the store specific labels", () => {
  const { installLabel } = load().window.__tfsOmniDownloads.internals;
  assert.equal(installLabel({ storeId: "xbox-game-pass" }, "action-required"), "Open Xbox");
  assert.equal(installLabel({ storeId: "gog-galaxy" }, "action-required"), "Open GOG");
  assert.equal(installLabel({ externalAction: "install-client" }, "action-required"), "Install EA app");
  assert.equal(installLabel({ requiresAccountLink: true, download: { detailText: "Link your account" } }, "action-required"), "Link Ubisoft");
  assert.equal(installLabel({ requiresExternalLauncher: true, providerDisplayName: "EA app" }, "failed"), "Retry in EA app");
});

test("TFS Curtain is told to stay away from games Play cannot start", () => {
  const api = load().window.__tfsOmniDownloads;
  const { state } = api.internals;
  assert.equal(api.blocksLaunch(appId), false, "unknown game: never block");
  state.cache.set(appId, { at: Date.now(), record: record({}) });
  assert.equal(api.blocksLaunch(appId), true);
  state.cache.set(appId, { at: Date.now(), record: record({ installed: true }) });
  assert.equal(api.blocksLaunch(appId), false);
  assert.equal(api.blocksLaunch(440), false, "Steam games are never blocked");

  const curtain = read("src/SteamLoader.App/Assets/curtain-core.js");
  assert.match(curtain, /window\.__tfsOmniDownloads\?\.blocksLaunch\?\.\(appId\)\) return;/);
  const apiServer = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  assert.match(apiServer, /if \(IsUninstalledOmniLibraryShortcut\(payload\.AppId\)\)/);
});

test("the row sits directly under Play, above HowLongToBeat, with either wrapping order", async () => {
  const patches = [];
  function AppDetailsSections(props) {
    const marker = "AppDetailsRoot";
    void marker;
    return fakeReact.createElement("Panel", {
      className: "root-class",
      children: [fakeReact.createElement("PlaySection", {}), fakeReact.createElement("Rest", { appid: props.overview.appid })],
    });
  }
  const renderFunc = () => fakeReact.createElement("Provider", { value: AppDetailsSections }, fakeReact.createElement("AppDetails", {}));
  const tfs = {
    React: fakeReact,
    whenReady: Promise.resolve(),
    ErrorBoundary: null,
    ui: { DialogButton: "DialogButton" },
    patch: { getUnpatched: (value) => value },
    webpack: { findModule: (filter) => [{ AppDetailsRoot: "root-class", PlaySection: "play" }].find(filter) },
    routes: {
      patch(path, handler, { id }) {
        patches.push({ path, handler, id });
        return () => {};
      },
    },
  };

  // HLTB first (shared bundle order), then the download row.
  const context = vm.createContext({
    window: { __tfs: tfs, setTimeout: () => 0, clearTimeout: () => {} },
    URL,
    URLSearchParams,
    console,
    fetch: async () => ({ ok: false, status: 500, json: async () => null }),
  });
  vm.runInContext(hltbSource, context, { filename: "hltb-core.js" });
  vm.runInContext(source, context, { filename: "download-core.js" });
  await new Promise((resolve) => setTimeout(resolve, 0));
  deepEqual(patches.map((p) => p.id), ["tfs-hltb", "tfs-omnilibrary-download"]);
  assert.ok(patches.every((p) => p.path === "/library/app/:appid"));

  const render = (order) => {
    let route = fakeReact.createElement("Route", { path: "/library/app/:appid" }, fakeReact.createElement("Memo", { renderFunc }));
    for (const patch of order) route = patch.handler(route) || route;
    const output = route.props.children.props.renderFunc({ appid: appId }, {});
    const Sections = output.props.value;
    return Sections({ overview: { appid: appId } }).props.children.map((child) => child.key || child.type);
  };

  deepEqual(render(patches), ["PlaySection", "tfs-omnilibrary-download", "tfs-hltb", "Rest"]);
  // Reversed order still inserts both rows (download wrapper carries the marker HLTB looks for).
  deepEqual(render([...patches].reverse()), ["PlaySection", "tfs-hltb", "tfs-omnilibrary-download", "Rest"]);

  // Steam games never get the row.
  let route = fakeReact.createElement("Route", { path: "/library/app/:appid" }, fakeReact.createElement("Memo", { renderFunc }));
  route = patches[1].handler(route) || route;
  const steamOutput = route.props.children.props.renderFunc({ appid: 440 }, {});
  const keys = steamOutput.props.value({ overview: { appid: 440 } }).props.children.map((child) => child.key || child.type);
  deepEqual(keys, ["PlaySection", "Rest"]);
});

test("Steam's own action button: Install / Pause / Resume for OmniLibrary games, installed Play untouched", () => {
  const { resolveNativeAction, displayStatus } = load().window.__tfsOmniDownloads.internals;
  const status = (game, entry = null, storeId) => resolveNativeAction(record(game, entry, storeId));

  deepEqual(status({}), { status: displayStatus.Install, action: "install" });
  assert.equal(status({ installed: true }), null, "installed: Steam's own Play (Curtain hooks RunGame)");
  assert.equal(status({ installed: true, updateAvailable: true }), null, "update pending: Play stays, the row offers Update");
  assert.equal(status({ cloudPlayable: true }, null, "xbox-game-pass"), null);
  assert.equal(status({}, null, "rom-library"), null);
  assert.equal(resolveNativeAction({ storeId: "", game: null }), null);

  const running = { download: { status: "downloading", progressPercent: 40 } };
  // Pinned Steam values: 7 Downloading renders "Pause", 22 DownloadPaused "Resume".
  assert.equal(displayStatus.Pause, 7);
  assert.equal(displayStatus.Resume, 22);
  deepEqual(status(running, { canPause: false, canCancel: true }), { status: displayStatus.Cancel, action: "cancel" });
  deepEqual(status(running, { canPause: true, canCancel: true }), { status: 7, action: "pause" });
  deepEqual(status({ download: { status: "paused" } }, { canResume: true }), { status: 22, action: "resume" });
  deepEqual(status({ download: { status: "paused" } }, { canResume: false }), { status: displayStatus.Install, action: "install" });
  deepEqual(status({ download: { status: "failed" } }), { status: displayStatus.Install, action: "install" });
  deepEqual(status({ download: { status: "action-required" } }), { status: displayStatus.Install, action: "install" });
  deepEqual(status({ download: { status: "uninstalling" } }), { status: displayStatus.InstallUnavailable, action: "" });
  deepEqual(status({ download: { status: "canceling" } }), { status: displayStatus.InstallUnavailable, action: "" });
});

test("the overview proxy changes only the display status, never identity", () => {
  const { withDisplayStatus, isOverviewLive } = load().window.__tfsOmniDownloads.internals;
  const pcd = { display_status: 11, status_percentage: 100, clientid: "0" };
  const overview = {
    appid: appId,
    per_client_data: [pcd],
    get selected_per_client_data() { return this.per_client_data[0]; },
    GetGameID() { return "123"; },
  };
  const proxied = withDisplayStatus(overview, 9);
  assert.equal(proxied.selected_per_client_data.display_status, 9);
  assert.equal(proxied.selected_per_client_data.status_percentage, undefined);
  assert.equal(proxied.per_client_data[0].display_status, 9);
  assert.equal(proxied.appid, appId);
  assert.equal(proxied.GetGameID(), "123");
  assert.equal(overview.selected_per_client_data.display_status, 11, "Steam's object is untouched");
  assert.equal(withDisplayStatus(overview, 9), proxied, "cached per status");
  assert.equal(isOverviewLive(proxied), false);

  // While Steam launches/runs the game its own status wins.
  pcd.display_status = 4;
  assert.equal(proxied.selected_per_client_data.display_status, 4);
  assert.equal(isOverviewLive(proxied), true);
});

test("the native button patch routes clicks for our proxies only and restores Steam's button", async () => {
  const hooks = [];
  const React = {
    ...fakeReact,
    useState: (value) => [value, () => {}],
    useEffect: (fn) => { hooks.push(fn); },
    useRef: (value) => ({ current: value }),
  };
  const steamClicks = [];
  class ButtonImpl {
    get OnClick() {
      const bound = (...args) => steamClicks.push(args);
      Object.defineProperty(this, "OnClick", { value: bound, configurable: true, writable: true });
      return bound;
    }
  }
  ButtonImpl.prototype.isReactComponent = {};
  Object.defineProperty(ButtonImpl.prototype, "OnClick", { ...Object.getOwnPropertyDescriptor(ButtonImpl.prototype, "OnClick"), configurable: true });
  const originalRender = (props) => React.createElement(ButtonImpl, props);
  const forwardRef = { $$typeof: Symbol.for("react.forward_ref"), render: originalRender };
  const modules = {
    7: Object.assign(function factory() {}, { toString: () => "bShowStreamingSelector ShowStreamingMenu strGameActionDetailsOverride" }),
  };
  const req = (id) => (String(id) === "7" ? { Button: forwardRef } : {});
  req.m = modules;
  const posts = [];
  const tfs = {
    React,
    whenReady: Promise.resolve(),
    webpack: { getWebpackRequire: () => req, getModules: () => [] },
    routes: { patch: () => () => {} },
  };
  const context = vm.createContext({
    window: { __tfs: tfs, setTimeout: () => 0, clearTimeout: () => {} },
    URL,
    URLSearchParams,
    console,
    Symbol,
    fetch: async (url, init) => {
      if (init?.method === "POST") posts.push([String(url), JSON.parse(init.body)]);
      return { ok: true, status: 200, json: async () => ({}) };
    },
  });
  vm.runInContext(source, context, { filename: "download-core.js" });
  await new Promise((resolve) => setTimeout(resolve, 0));
  const api = context.window.__tfsOmniDownloads;
  assert.notEqual(forwardRef.render, originalRender, "render wrapped");

  // A Steam game: stock element, Steam's click.
  const steamElement = forwardRef.render({ overview: { appid: 440 } }, null);
  assert.equal(steamElement.type, ButtonImpl);

  // Our game: a host element; the host hands the button a proxied overview.
  const pcd = { display_status: 11 };
  const overview = { appid: appId, per_client_data: [pcd], get selected_per_client_data() { return this.per_client_data[0]; } };
  const hosted = forwardRef.render({ overview }, null);
  assert.notEqual(hosted.type, ButtonImpl);
  api.internals.state.cache.set(appId, { at: Date.now(), record: record({}) });
  const rendered = hosted.type(hosted.props);
  assert.equal(rendered.type, ButtonImpl);
  assert.equal(rendered.props.overview.selected_per_client_data.display_status, api.internals.displayStatus.Install);
  assert.equal(api.nativeButton, true);

  const ours = Object.create(ButtonImpl.prototype);
  ours.props = rendered.props;
  ours.OnClick({}, { stopPropagation() {}, preventDefault() {} });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(steamClicks.length, 0, "Steam's install wizard never opens for our game");
  assert.ok(posts.some(([url, body]) => url.endsWith("api/unifystore/games/install") && body.gameId === "fn"));

  const theirs = Object.create(ButtonImpl.prototype);
  theirs.props = { overview: { appid: 440 } };
  theirs.OnClick("x");
  assert.equal(steamClicks.length, 1, "Steam games keep Steam's click");

  api.destroy();
  assert.equal(forwardRef.render, originalRender, "render restored");
  assert.ok(Object.getOwnPropertyDescriptor(ButtonImpl.prototype, "OnClick").get, "getter restored");
});
