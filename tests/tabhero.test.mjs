import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";

const engineSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Assets/tabhero-engine.js", import.meta.url),
  "utf8",
);
const libraryTabsSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Assets/library-tabs.js", import.meta.url),
  "utf8",
);
const popupSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url),
  "utf8",
);
const backgroundHostSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs", import.meta.url),
  "utf8",
);
const pluginCatalogSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Infrastructure/Settings/SteamLoaderPluginCatalog.cs", import.meta.url),
  "utf8",
);

function createStorage() {
  const values = new Map();
  return {
    getItem(key) {
      return values.has(key) ? values.get(key) : null;
    },
    setItem(key, value) {
      values.set(key, String(value));
    },
    removeItem(key) {
      values.delete(key);
    },
  };
}

function loadEngine(localStorage = createStorage()) {
  class BroadcastChannel {
    addEventListener() {}
    postMessage() {}
    close() {}
  }
  class CustomEvent {
    constructor(type, options = {}) {
      this.type = type;
      this.detail = options.detail;
    }
  }
  const context = vm.createContext({
    console,
    localStorage,
    BroadcastChannel,
    CustomEvent,
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent() {},
  });
  vm.runInContext(engineSource, context);
  return context.__steamLoaderTabHero;
}

test("Tabhero treats OmniLibrary and OmniConsole tabs as foreign-owned protected tabs", () => {
  const engine = loadEngine();
  const omniTab = {
    id: "tfs-xbox",
    title: "Xbox",
    __steamLoaderTabOwner: "omnilibrary",
    __steamLoaderProtectedTab: true,
  };

  assert.equal(engine.isProtectedTab(omniTab), true);
  assert.equal(engine.isProtectedTab({ id: "external", owner: "omniconsole" }), true);
  assert.equal(engine.isProtectedTab({ id: "other-plugin", owner: "community-plugin" }), true);
  assert.equal(engine.updateNativeTab("tfs-xbox", { title: "Changed", hidden: true }).ok, false);
  assert.equal(engine.moveTab("tfs-xbox", -1).ok, false);
  assert.equal(engine.deleteCustomTab("tfs-xbox").ok, false);

  const composed = engine.composeTabs([
    { id: "AllGames", title: "All Games" },
    { id: "Installed", title: "Installed" },
    { id: "DesktopApps", title: "Non-Steam" },
    omniTab,
    { ...omniTab },
    { id: "Soundtracks", title: "Soundtracks" },
  ]);
  assert.deepEqual(
    Array.from(composed, (tab) => tab.id),
    ["AllGames", "Installed", "DesktopApps", "tfs-xbox", "Soundtracks"],
  );
  assert.equal(composed.find((tab) => tab.id === "tfs-xbox").title, "Xbox");
  assert.equal(composed.filter((tab) => tab.id === "tfs-xbox").length, 1);
});

test("native and custom settings are composed once around a stable protected block", () => {
  const engine = loadEngine();
  engine.updateNativeTab("Favorites", { title: "Best", hidden: true });
  const created = engine.upsertCustomTab({
    id: "tabhero-installed",
    title: "Ready",
    filters: [{ type: "installed", params: { installed: true } }],
  });
  assert.equal(created.ok, true);

  const source = [
    { id: "AllGames", title: "All Games" },
    { id: "Installed", title: "Installed" },
    { id: "Favorites", title: "Favorites" },
    { id: "DesktopApps", title: "Non-Steam" },
    { id: "tfs-xbox", title: "Xbox", __steamLoaderTabOwner: "omnilibrary" },
    { id: "tabhero-installed", title: "Ready", __steamLoaderTabOwner: "tabhero" },
  ];
  const composed = engine.composeTabs(source);
  assert.equal(composed.some((tab) => tab.id === "Favorites"), false);
  assert.equal(composed.some((tab) => tab.id === "tabhero-installed"), true);
  assert.equal(
    composed.findIndex((tab) => tab.id === "tfs-xbox"),
    composed.findIndex((tab) => tab.id === "DesktopApps") + 1,
  );
});

test("hiding Non-Steam keeps protected OmniLibrary tabs visible and controller-ordered", () => {
  const engine = loadEngine();
  engine.updateNativeTab("DesktopApps", { hidden: true });
  const composed = engine.composeTabs([
    { id: "AllGames", title: "All Games" },
    { id: "Installed", title: "Installed" },
    { id: "DesktopApps", title: "Non-Steam" },
    {
      id: "tfs-emulation-psp",
      title: "PSP",
      __steamLoaderTabOwner: "omnilibrary",
      __steamLoaderProtectedTab: true,
    },
    { id: "Soundtracks", title: "Soundtracks" },
  ]);

  assert.deepEqual(Array.from(composed, (tab) => tab.id), [
    "AllGames",
    "Installed",
    "tfs-emulation-psp",
    "Soundtracks",
  ]);
});

test("native renames stay idempotent and reset to Steam's original title", () => {
  const engine = loadEngine();
  engine.updateNativeTab("AllGames", { title: "Everything" });
  const renamed = engine.composeTabs([
    { id: "AllGames", title: "All Games" },
    { id: "Installed", title: "Installed" },
  ]);
  assert.equal(renamed[0].title, "Everything");
  assert.equal(renamed[0].__steamLoaderTabHeroOriginalTitle, "All Games");

  engine.composeTabs(renamed);
  assert.equal(engine.getSnapshot().catalog[0].title, "All Games");
  engine.resetNativeTab("AllGames");
  const reset = engine.composeTabs(renamed);
  assert.equal(reset[0].title, "All Games");
});

test("navigation-only composition is pure and cannot publish a catalog render loop", () => {
  const engine = loadEngine();
  const reasons = [];
  const unsubscribe = engine.subscribe((_snapshot, reason) => reasons.push(reason));
  const before = engine.getSnapshot();

  const composed = engine.composeTabs([
    { id: "AllGames", title: "All Games" },
    { id: "Installed", title: "Installed" },
    { id: "tfs-psp", title: "PSP", __steamLoaderTabOwner: "omnilibrary" },
  ], { observe: false });

  const after = engine.getSnapshot();
  unsubscribe();
  assert.deepEqual(Array.from(composed, (tab) => tab.id), [
    "AllGames",
    "Installed",
    "tfs-psp",
  ]);
  assert.equal(after.revision, before.revision);
  assert.equal(after.catalog.some((tab) => tab.id === "tfs-psp"), false);
  assert.deepEqual(reasons, ["subscribe"]);
});

test("cross-webview writes merge with the latest stored state instead of losing changes", () => {
  const localStorage = createStorage();
  const libraryEngine = loadEngine(localStorage);
  const quickAccessEngine = loadEngine(localStorage);

  quickAccessEngine.updateNativeTab("AllGames", { title: "Everything" });
  libraryEngine.observeTabs([
    { id: "AllGames", title: "All Games" },
    { id: "Installed", title: "Installed" },
    { id: "tfs-xbox", title: "Xbox", __steamLoaderTabOwner: "omnilibrary" },
  ]);
  assert.notEqual(localStorage.getItem(libraryEngine.catalogStorageKey), null);
  quickAccessEngine.upsertCustomTab({ title: "Installed", filters: [
    { type: "installed", params: { installed: true } },
  ] });

  const reloaded = loadEngine(localStorage).getSnapshot();
  assert.equal(reloaded.native.AllGames.title, "Everything");
  assert.equal(reloaded.customTabs.length, 1);
  assert.equal(reloaded.catalog.some((tab) => tab.id === "tfs-xbox"), true);
});

test("custom tab creation is collision-safe and QoL actions are reversible", () => {
  const engine = loadEngine();
  const first = engine.upsertCustomTab({ title: "Recent", filters: [] });
  const second = engine.upsertCustomTab({ title: "Recent", filters: [] });
  assert.equal(first.ok, true);
  assert.equal(second.ok, true);
  assert.notEqual(first.tab.id, second.tab.id);

  const duplicate = engine.duplicateCustomTab(first.tab.id);
  assert.equal(duplicate.ok, true);
  assert.notEqual(duplicate.tab.id, first.tab.id);
  assert.equal(duplicate.tab.title, "Recent Copy");

  engine.updateNativeTab("Favorites", { hidden: true });
  assert.equal(engine.showAllNativeTabs().changed, true);
  assert.equal(engine.getSnapshot().native.Favorites.hidden, false);
  assert.equal(engine.canUndo(), true);
  assert.equal(engine.undoLastChange().ok, true);
  assert.equal(engine.getSnapshot().native.Favorites.hidden, true);

  assert.equal(engine.moveTabToEdge(second.tab.id, "start").ok, true);
  assert.equal(engine.getSnapshot().order[0], second.tab.id);
});

test("filter engine supports nested, inverted, platform, date, score, list, and install filters", () => {
  const engine = loadEngine();
  const nowSeconds = Math.floor(Date.now() / 1000);
  const app = {
    appid: 42,
    display_name: "Hero Story",
    installed: true,
    app_type: 1,
    review_percentage: 91,
    minutes_playtime_forever: 600,
    rt_last_time_played: nowSeconds - (5 * 86400),
    store_tag: [19, 21],
  };

  const filters = [
    { type: "installed", params: { installed: true } },
    { type: "regex", params: { regex: "hero" } },
    { type: "platform", params: { platform: "steam" } },
    { type: "review score", params: { condition: "above", scoreThreshold: 90 } },
    { type: "last played", params: { condition: "above", daysAgo: 30 } },
    { type: "whitelist", params: { games: [42] } },
    {
      type: "merge",
      params: {
        mode: "and",
        filters: [
          { type: "tags", params: { mode: "all", tags: [19, 21] } },
          { type: "time played", params: { condition: "above", units: "hours", timeThreshold: 5 } },
        ],
      },
    },
  ];
  for (const filter of filters) {
    assert.equal(engine.evaluateFilter(filter, app), true, filter.type);
  }
  assert.equal(
    engine.evaluateFilter({ type: "blacklist", params: { games: [42] } }, app),
    false,
  );
  assert.equal(
    engine.evaluateFilter({ type: "demo", inverted: true, params: { isDemo: true } }, app),
    true,
  );
  assert.equal(engine.validateFilters([{ type: "regex", params: { regex: "[" } }]).valid, false);
  assert.equal(engine.validateFilters([{ type: "regex", params: { regex: "(a+)+$" } }]).valid, false);
  assert.equal(engine.validateFilters({ type: "installed" }).valid, false);
  assert.equal(engine.validateFilters([{
    type: "merge",
    params: { filters: [{ type: "platform", params: { platform: "console" } }] },
  }]).valid, false);
});

test("Steam-specific compatibility, achievement cache, and SD-card filters use live stores", () => {
  const engine = loadEngine();
  const app = { appid: 42, steam_hw_compat_category_packed: 32 };
  assert.equal(
    engine.evaluateFilter({ type: "steamos compatibility", params: { category: 2 } }, app),
    true,
  );
  assert.equal(
    engine.evaluateFilter(
      { type: "achievements", params: { thresholdType: "percent", threshold: 75, condition: "above" } },
      app,
      { achievementProgressCache: { GetAchievementProgress: () => 80 } },
    ),
    true,
  );
  const installedCard = [{ uid: "installed" }, [{ uid: 42 }]];
  const otherCard = [{ uid: "other" }, [{ uid: 99 }]];
  assert.equal(
    engine.evaluateFilter(
      { type: "sd card", params: { card: 0 } },
      app,
      { currentCardAndGames: installedCard, cardsAndGames: [installedCard, otherCard] },
    ),
    true,
  );
  assert.equal(
    engine.evaluateFilter(
      { type: "sd card", params: { card: "other" } },
      app,
      { currentCardAndGames: installedCard, cardsAndGames: [installedCard, otherCard] },
    ),
    false,
  );
  const friendOwnedGamesMap = new Map([
    [765, { value: { m_data: { setApps: new Set([42]) } } }],
  ]);
  assert.equal(
    engine.evaluateFilter(
      { type: "friends", params: { mode: "all", friends: [765] } },
      app,
      { friendOwnedGamesMap },
    ),
    true,
  );
});

test("profiles restore editable layout state without taking ownership of protected tabs", () => {
  const engine = loadEngine();
  engine.updateNativeTab("DesktopApps", { title: "Other", hidden: false });
  engine.upsertCustomTab({ id: "tabhero-recent", title: "Recent", filters: [] });
  const saved = engine.saveProfile("Handheld");
  assert.equal(saved.ok, true);

  engine.updateNativeTab("DesktopApps", { title: "Changed", hidden: true });
  engine.upsertCustomTab({ ...engine.getSnapshot().customTabs[0], enabled: false });
  const applied = engine.applyProfile(saved.profile.id);
  assert.equal(applied.ok, true);
  assert.equal(applied.snapshot.native.DesktopApps.title, "Other");
  assert.equal(applied.snapshot.native.DesktopApps.hidden, false);
  assert.equal(applied.snapshot.customTabs[0].enabled, true);
  assert.equal(Object.hasOwn(applied.snapshot.native, "tfs-xbox"), false);
});

test("runtime integration uses one compositor and exposes the Tabhero built-in UI", () => {
  assert.match(backgroundHostSource, /Assets\/tabhero-engine\.js[\s\S]*Assets\/library-tabs\.js/);
  assert.match(libraryTabsSource, /const tabHero = window\.__steamLoaderTabHero/);
  assert.match(libraryTabsSource, /__steamLoaderTabOwner:\s*"omnilibrary"/);
  assert.match(libraryTabsSource, /tabHero\.composeTabs\(combinedTabs\)/);
  assert.match(
    libraryTabsSource,
    /tabHero\.composeTabs\(omniLibraryTabs, \{ observe: false \}\)/,
  );
  const navigationStabilizer = libraryTabsSource.slice(
    libraryTabsSource.indexOf("function stabilizeLibraryNavigation"),
    libraryTabsSource.indexOf("function libraryTabLayoutNeedsPatch"),
  );
  assert.doesNotMatch(navigationStabilizer, /onShowTab|setTimeout/);
  const subscriptionStart = libraryTabsSource.indexOf(
    "state.tabHeroUnsubscribe = tabHero.subscribe",
  );
  const tabHeroSubscription = libraryTabsSource.slice(
    subscriptionStart,
    libraryTabsSource.indexOf("if (!state.channel", subscriptionStart),
  );
  assert.match(tabHeroSubscription, /if \(!layoutChanged\)[\s\S]*return;/);
  assert.doesNotMatch(
    tabHeroSubscription,
    /routeRefreshRequested|navigationMigrationRequested/,
  );
  assert.match(popupSource, /id:\s*"tabhero"/);
  assert.match(popupSource, /Protected Plugin Tab/);
  assert.match(popupSource, /Undo Last Change/);
  assert.match(popupSource, /Duplicate Custom Tab/);
  assert.match(libraryTabsSource, /tabHeroFilterCacheMs/);
  assert.match(engineSource, /const regexCache = new Map\(\)/);
  assert.doesNotMatch(engineSource, /setInterval\(|\bfetch\(/);
  assert.match(
    popupSource,
    /route\.pluginId === "tabhero" && route\.pageId\?\.startsWith\("edit-"\)[\s\S]*fallbackSlotKey: `tabhero-tab-\$\{tabId\}`/,
  );
  assert.doesNotMatch(
    popupSource,
    /\[\.\.\.nativeEntries, \.\.\.customEntries\]\.forEach\([\s\S]{0,500}route\.pluginId/,
  );
  assert.match(pluginCatalogSource, /new\("tabhero",\s*"Tabhero"/);
});
