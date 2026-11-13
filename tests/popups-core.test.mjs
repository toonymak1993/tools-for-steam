import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");

const defaultSettings = () => ({
  theme: "tfs",
  soundMode: "theme",
  soundVolumePercent: 100,
  volumeOverlay: true,
  volumePosition: "top-center",
  steamInExclusiveFullscreen: true,
  categories: {
    achievements: { managed: true, position: "top-center", durationSeconds: 6 },
    messages: { managed: true, position: "bottom-right", durationSeconds: 5 },
    friends: { managed: false, position: "bottom-right", durationSeconds: 4 },
    downloads: { managed: true, position: "bottom-right", durationSeconds: 5 },
    system: { managed: true, position: "top-right", durationSeconds: 4 },
    community: { managed: true, position: "bottom-right", durationSeconds: 5 },
  },
});

/** A small NotificationStore that follows Steam's own decision order. */
function createStore(prefs) {
  const targets = {
    5: { showToast: () => prefs.achievements, sound: 11 },
    4: { showToast: () => true, sound: 12 },
    8: { showToast: () => true, playSound: () => prefs.messageSound, sound: 13 },
    29: { sound: 14 },
  };
  const store = {
    m_rgNotificationToasts: [],
    tray: [],
    sounds: [],
    dispatched: 0,
    GetNotificationTargets: () => targets,
    BShowToast(definition, notification) {
      const value = definition.showToast;
      return (typeof value === "boolean" && value) || (typeof value === "function" && value(notification));
    },
    ChooseSound(definition, notification) {
      const value = definition.playSound ?? !!definition.sound;
      return (typeof value === "boolean" && value) || (typeof value === "function" && value(definition, notification)) ? definition.sound : null;
    },
    BShouldSupressToasts: () => false,
    PlayNotificationSound(notification) {
      const sound = this.ChooseSound(targets[notification.eType], notification);
      if (sound) this.sounds.push(sound);
    },
    DispatchNextToast() {
      this.dispatched += 1;
    },
    ProcessNotification(definition, notification) {
      this.tray.push(notification);
      const show = this.BShowToast(definition, notification);
      if (notification.eSource === 2) {
        if (show) this.m_rgNotificationToasts = this.m_rgNotificationToasts.concat(notification);
        return;
      }
      const sound = this.ChooseSound(definition, notification);
      if (show) {
        this.m_rgNotificationToasts.push(notification);
        this.DispatchNextToast();
      } else if (sound) {
        this.PlayNotificationSound(notification);
      }
    },
    /** Steam reassigns showToast on every server notification. */
    OnServerNotification(type, enabled, notification) {
      targets[type].showToast = enabled;
      this.ProcessNotification(targets[type], notification);
    },
  };
  return { store, targets };
}

function load({ settings = defaultSettings(), stateStatus = 200, showResult = { shown: true }, prefs = { achievements: true, messageSound: true }, leftovers = {} } = {}) {
  const { store, targets } = createStore(prefs);
  const posts = [];
  const tokens = {
    "#Notification_Achievement_Title": "Errungenschaft",
    "#SteamNotifications_TradeOffer_Title": "Handelsangebot",
    "#SteamNotifications_TradeOffer_Body_Short": "Handelsangebot von %1$s",
  };
  const window = {
    __tfsSurfaceRole: "shared",
    __steamLoaderApiBase: "http://127.0.0.1:1/",
    NotificationStore: store,
    LocalizationManager: { LocalizeString: (token) => tokens[token] },
    appStore: { GetAppOverviewByAppID: (id) => (id === 550 ? { display_name: "Left 4 Dead 2", icon_hash: "abc" } : null) },
    friendStore: {
      currentUserSteamID: { constructor: { InitFromAccountID: (id) => ({ id }) }, GetAccountID: () => 1 },
      GetFriendState: ({ id }) => (id === 42 ? { display_name: "Gabe", persona: { avatar_url_medium: "https://avatars.steamstatic.com/x_medium.jpg" } } : { display_name: "" }),
    },
    setTimeout: (fn, ms) => (ms && ms < 1000 ? setTimeout(fn, 0) : 0),
    clearTimeout: () => {},
    ...leftovers,
  };
  const context = vm.createContext({
    window,
    console,
    fetch: async (url, options) => {
      const path = String(url).replace("http://127.0.0.1:1/", "");
      if (options?.method === "POST") posts.push({ path, body: JSON.parse(options.body) });
      if (path === "api/popups/state") {
        return { ok: stateStatus === 200, status: stateStatus, json: async () => (stateStatus === 200 ? { ready: true, scalePercent: 150, settings } : { message: "off" }) };
      }
      return { ok: true, status: 200, json: async () => showResult };
    },
    Promise, Number, String, Object, Array, Math, Set, WeakSet, Symbol, JSON, BigInt, Date, Boolean,
  });
  const result = vm.runInContext(read("src/SteamLoader.App/Assets/popups-core.js"), context);
  return { window, store, targets, posts, result };
}

const flush = async (rounds = 6) => {
  for (let index = 0; index < rounds; index += 1) await new Promise((resolve) => setImmediate(resolve));
};

const achievement = () => ({
  eType: 5,
  eSource: 1,
  data: { toObject: () => ({ appid: 550, name: "Pole Position", description: "Win from pole.", image_url: "https://cdn.cloudflare.steamstatic.com/a.jpg", achieved: true, global_achieved_pct: 4.24 }) },
});

test("a managed achievement becomes a TFS popup; Steam keeps it in its history but shows no toast and plays no sound", async () => {
  const { store, targets, posts, result } = load();
  assert.equal(result, "injected");
  await flush();

  const notification = achievement();
  store.ProcessNotification(targets[5], notification);
  await flush();

  assert.equal(store.tray.length, 1, "Steam's history keeps the notification");
  assert.equal(store.m_rgNotificationToasts.length, 0, "Steam's toast is replaced");
  assert.deepEqual(store.sounds, [], "Steam's sound is replaced");
  const show = posts.find((post) => post.path === "api/popups/show");
  assert.ok(show);
  assert.equal(show.body.category, "achievements");
  assert.equal(show.body.title, "Pole Position");
  assert.equal(show.body.body, "Win from pole.");
  assert.match(show.body.detail, /Errungenschaft {2}· {2}Left 4 Dead 2 {2}· {2}4\.2 %/);
  assert.equal(show.body.imageUrl, "https://cdn.cloudflare.steamstatic.com/a.jpg");
  assert.equal(show.body.sound, true);
});

test("Steam's own preferences win: a toast Steam would not show never reaches TFS", async () => {
  const { store, targets, posts } = load({ prefs: { achievements: false, messageSound: true } });
  await flush();
  store.ProcessNotification(targets[5], achievement());
  await flush();
  assert.equal(posts.filter((post) => post.path === "api/popups/show").length, 0);
  assert.equal(store.m_rgNotificationToasts.length, 0);
});

test("categories left to Steam keep Steam's toast", async () => {
  const { store, targets, posts } = load();
  await flush();
  const online = { eType: 4, eSource: 1, data: { toObject: () => ({ steamid: "76561197960265770" }) } };
  store.ProcessNotification(targets[4], online);
  await flush();
  assert.equal(store.m_rgNotificationToasts.length, 1);
  assert.equal(posts.filter((post) => post.path === "api/popups/show").length, 0);
});

test("when TFS cannot show a popup the toast goes back into Steam's queue", async () => {
  const { store, targets } = load({ showResult: { shown: false, reason: "fullscreen" } });
  await flush();
  const notification = achievement();
  store.ProcessNotification(targets[5], notification);
  await flush();
  assert.deepEqual(store.m_rgNotificationToasts, [notification]);
  assert.ok(store.dispatched >= 1);
  assert.equal(notification.nToastDurationMS, 5000);
  // Steam may play its sound for the returned toast again.
  store.PlayNotificationSound(notification);
  assert.deepEqual(store.sounds, [11]);
});

test("with TFS Popups switched off (403) every notification stays Steam's", async () => {
  const { store, targets, posts } = load({ stateStatus: 403 });
  await flush();
  store.ProcessNotification(targets[5], achievement());
  await flush();
  assert.equal(store.m_rgNotificationToasts.length, 1);
  assert.equal(posts.filter((post) => post.path === "api/popups/show").length, 0);
});

test("server notifications are caught even though Steam reassigns showToast each time", async () => {
  const { store, posts } = load();
  await flush();
  const trade = { eType: 29, eSource: 2, data: { item: { viewed: false, body_data: JSON.stringify({ sender: "76561197960265770" }) } } };
  store.OnServerNotification(29, true, trade);
  await flush(10);
  assert.equal(store.m_rgNotificationToasts.length, 0);
  const show = posts.find((post) => post.path === "api/popups/show");
  assert.equal(show.body.category, "community");
  assert.equal(show.body.title, "Handelsangebot");
  assert.equal(show.body.body, "Handelsangebot von Gabe");
  assert.equal(show.body.roundImage, true);

  // Steam switching the toast off for this type is still respected.
  store.OnServerNotification(29, false, { ...trade });
  await flush(10);
  assert.equal(posts.filter((post) => post.path === "api/popups/show").length, 1);
});

test("Steam sound mode plays Steam's sound for TFS popups, and the theme sound is not requested when Steam's sound is off", async () => {
  const settings = { ...defaultSettings(), soundMode: "steam" };
  const steam = load({ settings });
  await flush();
  const message = { eType: 8, eSource: 1, data: { toObject: () => ({ steamid: "76561197960265770", title: "Gabe", body: "Ready?" }) } };
  steam.store.ProcessNotification(steam.targets[8], message);
  await flush();
  assert.deepEqual(steam.store.sounds, [13]);

  const quiet = load({ prefs: { achievements: true, messageSound: false } });
  await flush();
  quiet.store.ProcessNotification(quiet.targets[8], { ...message });
  await flush();
  const show = quiet.posts.find((post) => post.path === "api/popups/show");
  assert.equal(show.body.sound, false);
  assert.equal(show.body.title, "Gabe");
  assert.equal(show.body.body, "Ready?");
});

test("destroy restores Steam's own toast decisions", async () => {
  const { window, store, targets } = load();
  await flush();
  assert.equal(targets[4].showToast.name, "tfsPopupsShowToast");
  window.__tfsPopups.destroy();
  assert.equal(typeof Object.getOwnPropertyDescriptor(targets[5], "showToast").value, "function");
  assert.notEqual(targets[4].showToast.name, "tfsPopupsShowToast");
  assert.equal(Object.prototype.hasOwnProperty.call(targets[29], "playSound"), false);
  store.ProcessNotification(targets[5], achievement());
  assert.equal(store.m_rgNotificationToasts.length, 1);
});

test("loading TFS Popups stops the removed toast/menu size scripts an older build left running", async () => {
  const destroyed = [];
  const { result } = load({
    leftovers: {
      __tfsNotificationScale: { version: 1, destroy: () => destroyed.push("notification") },
      __tfsMenuScale: { version: 1, destroy: () => destroyed.push("menu") },
    },
  });
  assert.equal(result, "injected");
  assert.deepEqual(destroyed, ["notification", "menu"]);
});

test("TFS Popups is wired into the shared bundle, the API, the feature list and the Quick Access settings", () => {
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const api = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  const catalog = read("src/SteamLoader.App/Infrastructure/Settings/SteamLoaderPluginCatalog.cs");
  const popup = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const script = read("src/SteamLoader.App/Assets/popups-core.js");

  assert.match(host, /Assets\/perf-tab\.js"\),[\s\S]*?Assets\/popups-core\.js"\)\);/);
  // The Steam toast size and menu size were removed; TFS Popups owns the size.
  assert.doesNotMatch(host, /Assets\/(?:notification|menu)-scale\.js/);
  assert.doesNotMatch(api, /\/api\/settings\/menu-scale/);
  assert.doesNotMatch(popup, /__tfsNotificationScale|__tfsMenuScale|Menu Size|Show Test Notification/);
  assert.match(popup, /api\/settings\/notification-scale/);
  assert.match(host, /new Infrastructure\.Popups\.PopupService\(/);
  assert.match(host, /Popups = popupService/);
  assert.match(api, /PopupApiEndpoints\.TryHandleAsync\(request, response, Popups, cancellationToken\)/);
  assert.match(api, /\["\/api\/popups"\] = "popups"/);
  assert.match(catalog, /new\("popups", "TFS Popups"/);
  // A Quick Access plugin of its own (not under Settings) with three pages.
  assert.match(popup, /id: "popups",\s*title: "TFS Popups"/);
  assert.match(popup, /state\.route\.pluginId === "popups"/);
  assert.match(popup, /api\/popups\/settings/);
  assert.match(popup, /\?\.preview\?\.\(category\)/);
  assert.doesNotMatch(popup, /openTfsPopupsSettings/);
  assert.match(script, /role !== "shared"\) return "skipped"/);
  assert.doesNotMatch(script, /\/tfs\/popups/);
});
