import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

// vm realms: compare plain copies.
const deepEqual = (actual, expected, message) => assert.deepEqual(JSON.parse(JSON.stringify(actual)), expected, message);
const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const source = read("src/SteamLoader.App/Assets/omnilibrary-tabs-core.js");

const fakeReact = {
  createElement(type, props, ...children) {
    return { type, key: props?.key ?? null, props: { ...(props || {}), ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}) } };
  },
  cloneElement(element, props, ...children) {
    return { ...element, props: { ...element.props, ...(props || {}), ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}) } };
  },
};

const overviews = new Map();
for (const appId of [1, 2, 3, 10, 11, 12, 13, 20]) overviews.set(appId, { appid: appId, display_name: `App ${appId}` });

function load(extra = {}) {
  const dispatcher = { current: { useMemo: (factory) => factory(), useEffect() {} } };
  const React = { ...fakeReact, __SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED: { ReactCurrentDispatcher: dispatcher } };
  const window = {
    __tfs: { React, webpack: { findModule: () => ({ TabCount: "count_x", TabTitle: "title_x" }) } },
    appStore: { GetAppOverviewByAppID: (appId) => overviews.get(appId) || null },
    setTimeout: () => 0,
    clearTimeout: () => {},
    ...extra,
  };
  const context = vm.createContext({ window, console, fetch: async () => ({ ok: false, status: 500, json: async () => null }) });
  const result = vm.runInContext(source, context, { filename: "omnilibrary-tabs-core.js" });
  assert.equal(result, "injected");
  return { window, React, dispatcher, internals: window.__tfsOmniTabs.internals };
}

const summary = {
  pluginEnabled: true,
  stores: [
    {
      id: "xbox-game-pass",
      title: "Xbox",
      enabled: true,
      readyForLibraryTab: true,
      xboxCloudGamingEnabled: true,
      appIds: [10, 11, 12],
      installedAppIds: [10],
      cloudAppIds: [12],
      libraryTabs: [
        { id: "tfs-xbox", title: "XBOX", filter: "non-cloud" },
        { id: "tfs-xbox-cloud", title: "XBOX CLOUD", filter: "cloud", requiresCloudSource: true },
      ],
    },
    { id: "epic-games", title: "Epic", enabled: true, readyForLibraryTab: false, appIds: [13], libraryTabs: [{ id: "tfs-epic", title: "EPIC" }] },
    {
      id: "rom-library",
      title: "Emulation",
      enabled: true,
      readyForLibraryTab: true,
      appIds: [20],
      installedAppIds: [20],
      romSystems: [{ id: "snes", appIds: [20] }, { id: "gba", appIds: [] }],
      libraryTabs: [
        { id: "tfs-rom-snes", title: "SNES", filter: "platform:snes" },
        { id: "tfs-rom-gba", title: "GBA", filter: "platform:gba" },
      ],
    },
  ],
};

const tabContext = { Provider: "TabContext.Provider" };
function Grid() {}
Grid.toString = () => "function(){ return Library_FilteredByHeader }";

function steamTabs() {
  const collection = (apps) => ({ visibleApps: apps.map((appId) => overviews.get(appId)), allApps: apps.map((appId) => overviews.get(appId)), displayName: "x" });
  const tab = (id, apps) => ({
    id,
    title: id,
    footer: { onMenuButton: "menu" },
    content: fakeReact.createElement({ _context: tabContext }, null, fakeReact.createElement(Grid, { collection: collection(apps) })),
    renderTabAddon: () => null,
  });
  return [
    tab("AllGames", [1, 2, 10]),
    tab("Installed", [1, 10, 11, 13]),
    tab("DesktopApps", [3, 10, 11, 12, 13]),
    tab("Soundtracks", []),
  ];
}

test("store tabs come from the summary: only prepared stores with games, cloud only with Xbox Cloud", () => {
  const { internals } = load();
  deepEqual(internals.resolveStoreTabs(summary).map((tab) => [tab.id, tab.title, tab.appIds]), [
    ["tfs-native-xbox", "XBOX", [10, 11]],
    ["tfs-native-xbox-cloud", "XBOX CLOUD", [12]],
    ["tfs-native-rom-snes", "SNES", [20]],
  ]);
  const noCloud = structuredClone(summary);
  noCloud.stores[0].xboxCloudGamingEnabled = false;
  deepEqual(internals.resolveStoreTabs(noCloud).map((tab) => tab.id), ["tfs-native-xbox", "tfs-native-rom-snes"]);
  deepEqual(internals.resolveStoreTabs({ ...summary, pluginEnabled: false }), []);

  const sets = internals.resolveManagedSets(summary);
  deepEqual([...sets.managed].sort((a, b) => a - b), [10, 11, 12, 13, 20]);
  deepEqual([...sets.visible].sort((a, b) => a - b), [10, 11, 12, 20], "Epic has no tab yet, so its games stay hidden");
  deepEqual([...sets.playable].sort((a, b) => a - b), [10, 20], "installed on disk only, like Steam's Installed tab");
});

test("store tabs are Steam tabs after Non-Steam with Steam's grid, footer and count style", () => {
  const { internals } = load();
  internals.setSummary(summary);
  const appFilter = { Matches: (app) => app.appid !== 11 };
  const spliced = internals.spliceTabs(steamTabs(), ["sort", "setSort", "menu", appFilter]);
  deepEqual(spliced.map((tab) => tab.id), [
    "AllGames", "Installed", "DesktopApps",
    "tfs-native-xbox", "tfs-native-xbox-cloud", "tfs-native-rom-snes",
    "Soundtracks",
  ]);

  const xbox = spliced.find((tab) => tab.id === "tfs-native-xbox");
  assert.equal(xbox.title, "XBOX");
  deepEqual(xbox.footer, { onMenuButton: "menu" });
  assert.equal(xbox.content.type, "TabContext.Provider");
  deepEqual(xbox.content.props.value, { label: "XBOX" });
  const grid = xbox.content.props.children;
  assert.equal(grid.type, Grid);
  assert.equal(grid.props.eSortBy, "sort");
  assert.equal(grid.props.setSortBy, "setSort");
  assert.equal(grid.props.showSortingContextMenu, "menu");
  deepEqual(grid.props.collection.visibleApps.map((app) => app.appid), [10, 11]);
  assert.equal(grid.props.collection.bIsEditable, false);
  assert.equal(grid.props.collection.AsEditableCollection(), null);
  const count = xbox.renderTabAddon();
  assert.equal(count.props.className, "count_x");
  assert.equal(count.props.children, 1, "count honours Steam's filter");

  const collectionOf = (id) => spliced.find((tab) => tab.id === id).content.props.children.props.collection;
  deepEqual(collectionOf("DesktopApps").visibleApps.map((app) => app.appid), [3], "Non-Steam: only the user's own shortcuts");
  deepEqual(collectionOf("AllGames").visibleApps.map((app) => app.appid), [1, 2, 10, 11, 12, 20]);
  deepEqual(collectionOf("Installed").visibleApps.map((app) => app.appid), [1, 10, 20], "not installed, streaming-only and hidden store games leave Installed");

  // Re-renders of an already spliced list are left alone.
  assert.equal(internals.spliceTabs(spliced, []), spliced);
});

test("Steam's list stays untouched when its shape is unknown or OmniLibrary is off", () => {
  const { internals } = load();
  internals.setSummary({ pluginEnabled: false, stores: [] });
  const tabs = steamTabs();
  assert.equal(internals.spliceTabs(tabs, []), tabs);
  internals.setSummary(summary);
  const odd = [{ id: "Something", content: {} }];
  assert.equal(internals.spliceTabs(odd, []), odd, "no All Games template");
  const notTabs = [1, 2, 3];
  assert.equal(internals.spliceTabs(notTabs, []), notTabs);
  // Tuple shape [tabs, ...rest] keeps its rest.
  const tuple = internals.spliceTabs([steamTabs(), "rest"], []);
  assert.equal(tuple[1], "rest");
  assert.ok(tuple[0].some((tab) => tab.id === "tfs-native-xbox"));
});

test("Steam's useMemo is only intercepted while the Library renders, then restored", () => {
  const { internals, React, dispatcher } = load();
  internals.setSummary(summary);
  const realUseMemo = dispatcher.current.useMemo;
  let seenDeps = null;
  dispatcher.current.useMemo = (factory, deps) => {
    seenDeps = deps;
    return factory();
  };
  const restored = dispatcher.current.useMemo;
  const output = internals.renderWithSplice(React, () => dispatcher.current.useMemo(() => steamTabs(), ["a", "b", "c"]));
  assert.ok(output.some((tab) => tab.id === "tfs-native-rom-snes"));
  assert.equal(seenDeps.length, 4, "our revision joins Steam's deps so a store change recomputes the list");
  assert.equal(dispatcher.current.useMemo, restored);
  dispatcher.current.useMemo = realUseMemo;

  assert.throws(() => internals.renderWithSplice(React, () => { throw new Error("boom"); }), /boom/);
  assert.equal(dispatcher.current.useMemo, realUseMemo, "restored even when Steam's render throws");
});

test("the /library route keeps stable component wrappers and Steam's own element", () => {
  const { internals } = load();
  const inner = function Tabbed() { return "tabs"; };
  const memo = { $$typeof: "memo", type: inner, compare: null };
  const Level1 = function Level1() { return fakeReact.createElement(memo, { a: 1 }); };
  const Level0 = function Level0() { return fakeReact.createElement(Level1, { b: 2 }); };
  const route = fakeReact.createElement("Route", { path: "/library" }, fakeReact.createElement(Level0, { c: 3 }));

  const first = internals.patchLibraryRoute(route);
  const second = internals.patchLibraryRoute(route);
  assert.equal(first.props.path, "/library");
  const mount = first.props.children;
  const element = mount.props.element;
  assert.equal(element.type, second.props.children.props.element.type, "same wrapper every render: no remount");
  assert.equal(element.props.c, 3);

  const level1 = element.type.call(null, { c: 3, __tfsOmniTabsRevision: "1:0" });
  assert.equal(level1.props.b, 2);
  assert.equal(level1.props.__tfsOmniTabsRevision, "1:0");
  const tabbed = level1.type.call(null, { b: 2, __tfsOmniTabsRevision: "1:0" });
  assert.equal(tabbed.type.$$typeof, "memo");
  assert.notEqual(tabbed.type, memo, "Steam's memo object is never mutated");
  assert.equal(memo.type, inner);
  let receivedProps = null;
  memo.type = function Tabbed(props) { receivedProps = props; return "tabs"; };
  // A cached wrapper keeps pointing at the original inner render.
  assert.equal(tabbed.type.type({ a: 1, __tfsOmniTabsRevision: "1:0" }), "tabs");
  assert.equal(receivedProps, null);
  memo.type = inner;
});

test("the native tabs core is bundled in the shared context; the old compositor no longer adds tabs", () => {
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const tabs = read("src/SteamLoader.App/Assets/library-tabs.js");
  assert.match(host, /ReadText\("Assets\/download-core\.js"\),[\s\S]*?ReadText\("Assets\/omnilibrary-tabs-core\.js"\),[\s\S]*?var popupScript/);
  assert.match(source, /if \(role && role !== "shared"\) return "skipped";/);
  assert.match(tabs, /const nativeStoreTabsOwnLibrary = true;/);
  assert.match(tabs, /if \(nativeStoreTabsOwnLibrary \|\| !state\.pluginEnabled\)\s*\{\s*return \[\];/);
  assert.match(tabs, /if \(nativeStoreTabsOwnLibrary && !tabs\.some\(\(tab\) => isInjectedXboxTab\(tab\)\)\)/);
});

test("tile badges: store, install and download state per game of a shown store", () => {
  const { internals } = load();
  const withDownload = structuredClone(summary);
  withDownload.stores[0].activeDownloadAppIds = [11];
  const index = internals.resolveTileIndex(withDownload);
  deepEqual([...index.keys()].sort((a, b) => a - b), [10, 11, 12, 20], "Epic shows no tab yet, so no badge");
  assert.equal(index.get(10).storeId, "xbox-game-pass");
  assert.equal(internals.tileStatus(index.get(10)), "", "installed: logo only");
  assert.equal(internals.tileStatus(index.get(11)), "downloading");
  assert.equal(internals.tileStatus(index.get(12)), "cloud");
  assert.equal(internals.tileStatus({ installed: false, cloud: false, downloading: false }), "download");
  assert.equal(index.get(20).storeId, "rom-library");
});

test("tile badges patch Steam's library tile in Steam's Deck badge slot and restore it", () => {
  const memoSymbol = Symbol.for("react.memo");
  const rowClass = "row_cls";
  const original = function SteamTile(props) {
    return fakeReact.createElement("div", { className: "tile" },
      fakeReact.createElement("img", { src: props.app.appid }),
      fakeReact.createElement("div", { className: `${rowClass} other` }, fakeReact.createElement("span", { key: "native" })));
  };
  const memo = { $$typeof: memoSymbol, type: original };
  const factories = {
    10: "function(){ LibraryItemIcons BIsModOrShortcut BIsMusicAlbum }",
    11: "function(){ unrelated }",
  };
  const webpackRequire = Object.assign((id) => (id === "10" ? { tile: memo } : {}), { m: factories });
  const classes = { LibraryItemIcons: rowClass, SteamDeckCompatIcon: "deck_icon", DeckCompat: "deck_compat", SteamDeckCompatInfo: "deck_info" };
  const { window, internals } = load();
  window.__tfs.webpack.getWebpackRequire = () => webpackRequire;
  window.__tfs.webpack.getModules = () => [{ id: "c", exports: classes }];
  internals.setSummary(summary);

  internals.installTileBadges();
  assert.notEqual(memo.type, original, "the tile memo's render is wrapped");
  assert.equal(internals.tile.badgeClass, "deck_info deck_compat deck_icon");

  const shortcut = (appid) => ({ appid, BIsModOrShortcut: () => true });
  const rendered = memo.type({ app: shortcut(10) });
  const row = rendered.props.children[1];
  assert.equal(row.props.children.length, 2);
  assert.equal(row.props.children[1].key, "tfs-omni-store-badge");
  assert.equal(row.props.children[1].props.appId, 10);

  const steamGame = memo.type({ app: { appid: 1, BIsModOrShortcut: () => false } });
  assert.equal(steamGame.props.children[1].props.children.key, "native", "Steam games keep their own tile");

  internals.uninstallTileBadges();
  assert.equal(memo.type, original, "restored on unload");
  assert.equal(memo.__tfsOmniTileOriginal, undefined);
});
