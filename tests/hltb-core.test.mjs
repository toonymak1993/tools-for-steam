import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const source = read("src/SteamLoader.App/Assets/hltb-core.js");

const fakeReact = {
  createElement(type, props, ...children) {
    return { type, key: props?.key ?? null, props: { ...(props || {}), ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}) } };
  },
  cloneElement(element, props, ...children) {
    return { ...element, props: { ...element.props, ...(props || {}), ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}) } };
  },
  Fragment: "Fragment",
};

function load(tfs = null) {
  const timers = [];
  const window = {
    __tfs: tfs,
    setTimeout: (fn) => { timers.push(fn); return timers.length; },
    clearTimeout: () => {},
  };
  const context = vm.createContext({ window, URL, URLSearchParams, console, fetch: async () => ({ ok: false, json: async () => null }) });
  const result = vm.runInContext(source, context, { filename: "hltb-core.js" });
  assert.equal(result, "injected");
  return window;
}

test("formats HowLongToBeat hours like the site", () => {
  const { formatDuration } = load().__tfsHltb.internals;
  assert.equal(formatDuration("23.6"), "23½ Hours");
  assert.equal(formatDuration("23.8"), "24 Hours");
  assert.equal(formatDuration("23.1"), "23 Hours");
  assert.equal(formatDuration("1.0"), "1 Hour");
  assert.equal(formatDuration("1.5"), "1½ Hours");
  assert.equal(formatDuration("0.4"), "24 Mins");
  assert.equal(formatDuration("--"), "");
  assert.equal(formatDuration(""), "");
});

test("shows only found, enabled stats in HowLongToBeat order", () => {
  const { visibleStats } = load().__tfsHltb.internals;
  const settings = { enabled: true, showMainStory: true, showMainPlus: false, showCompletionist: true, showAllStyles: true };
  const snapshot = { found: true, mainStory: "23.6", mainPlus: "48.7", completionist: "--", allStyles: "44.4", settings };
  assert.deepEqual(Array.from(visibleStats(snapshot), (stat) => stat.label), ["Main Story", "All Styles"]);
  assert.equal(visibleStats({ ...snapshot, found: false }).length, 0);
  assert.equal(visibleStats({ ...snapshot, settings: { ...settings, enabled: false } }).length, 0);
});

test("inserts the row right after Steam's play section and nowhere else", () => {
  const { insertAfterPlaySection } = load().__tfsHltb.internals;
  const play = fakeReact.createElement("PlaySection", {});
  const rest = fakeReact.createElement("Rest", {});
  const root = fakeReact.createElement("Panel", { className: "root-class other", children: [play, rest] });
  const row = fakeReact.createElement("Hltb", { key: "tfs-hltb" });

  const result = insertAfterPlaySection(root, row, "root-class", fakeReact.cloneElement);
  assert.deepEqual(Array.from(result.props.children, (child) => child.type), ["PlaySection", "Hltb", "Rest"]);
  assert.equal(root.props.children.length, 2, "Steam's element is not mutated");

  // Unknown layout (class changed) or already inserted: unchanged output.
  assert.equal(insertAfterPlaySection(root, row, "missing", fakeReact.cloneElement), root);
  assert.equal(insertAfterPlaySection(result, row, "root-class", fakeReact.cloneElement), result);
  assert.equal(insertAfterPlaySection(null, row, "root-class", fakeReact.cloneElement), null);
});

test("patches the game page route through tfs-core and wraps Steam's sections component", async () => {
  const patches = [];
  function AppDetailsSections(props) {
    const marker = "AppDetailsRoot";
    void marker;
    return fakeReact.createElement("Panel", {
      className: "root-class",
      children: [fakeReact.createElement("PlaySection", {}), fakeReact.createElement("Rest", { appid: props.overview.appid })],
    });
  }
  const renderFunc = (overview, details) =>
    fakeReact.createElement("Provider", { value: AppDetailsSections, children: fakeReact.createElement("AppDetails", { overview, details }) });
  const tfs = {
    React: fakeReact,
    ErrorBoundary: null,
    whenReady: Promise.resolve(),
    ui: {},
    patch: { getUnpatched: (value) => value },
    webpack: { findModule: (predicate) => [{ AppDetailsRoot: "root-class", PlaySection: "play" }].find(predicate) || null },
    routes: {
      patch(path, handler, options) {
        patches.push({ path, handler, options });
        return () => patches.splice(0);
      },
    },
  };

  const window = load(tfs);
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(patches.length, 1);
  assert.equal(patches[0].path, "/library/app/:appid");
  assert.equal(window.__tfsHltb.patched, true);

  const route = fakeReact.createElement("Route", { path: "/library/app/:appid", children: fakeReact.createElement("Lazy", { renderFunc }) });
  const patched = patches[0].handler(route);
  assert.notEqual(patched, route);
  assert.equal(route.props.children.props.renderFunc, renderFunc, "Steam's route element is not mutated");

  const wrappedRender = patched.props.children.props.renderFunc;
  assert.equal(patches[0].handler(route).props.children.props.renderFunc, wrappedRender, "stable wrapper identity");
  const provider = wrappedRender({ appid: 1145360, display_name: "Hades" }, {});
  const Sections = provider.props.value;
  assert.notEqual(Sections, AppDetailsSections);
  assert.equal(wrappedRender({ appid: 1 }, {}).props.value, Sections, "stable sections wrapper");

  const page = Sections({ overview: { appid: 1145360, display_name: "Hades" } });
  const types = Array.from(page.props.children, (child) => child.key === "tfs-hltb" ? "hltb" : child.type);
  assert.deepEqual(types, ["PlaySection", "hltb", "Rest"]);

  window.__tfsHltb.destroy();
  assert.equal(patches.length, 0);
});
