// TFS Popups runtime (SharedJSContext).
//
// Replaces the visible part of Steam's notifications with TFS popups:
//  - hooks only Steam's per-type toast decision (NotificationStore's type
//    table). Steam still files every notification in its history, applies its
//    own toast and sound preferences and rate limits, and decides first
//    whether a toast would show at all;
//  - a toast of a category set to "TFS" is described (title, text, artwork
//    from Steam's stores) and sent to the TFS backend, which draws it in its
//    own overlay with the chosen design, position, duration and sound;
//  - whenever that fails (TFS off, backend gone, exclusive fullscreen game,
//    unknown type), the toast goes back into Steam's own queue, so nothing is
//    ever lost;
//  - settings live in the Quick Access plugin "TFS Popups"
//    (quickaccess-popup.js), which calls preview() and refresh() here. The
//    popup size is the backend's notification size setting.
//
// Inspired by Playhub Notifications (MIT, LoZazaMastro); independent code.
(() => {
  // 2: settings moved from a Big Picture route into the Quick Access plugin.
  const version = 2;
  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  // The Steam toast size (notification-scale.js) and menu size (menu-scale.js)
  // were removed; TFS Popups covers notifications now. Stop copies an older
  // build left running in this long-lived context and drop their styles.
  try { window.__tfsNotificationScale?.destroy?.(); } catch {}
  try { window.__tfsMenuScale?.destroy?.(); } catch {}

  const existing = window.__tfsPopups;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const patchMark = "__tfsPopupsPatch";
  const stateRefreshMs = 30000;
  const unavailableRetryMs = 60000;
  const patchRetryMs = 2000;
  const serverDataWaitMs = 4000;
  const defaultToastMs = 5000;

  /**
   * Steam's notification types (EClientUINotificationType) per popup
   * category. Types that only exist inside a game's overlay (splash screen,
   * broadcast, timed trial) are left to Steam.
   */
  const typeCategories = {
    1: "downloads", 14: "downloads", 50: "downloads", 54: "downloads", 55: "downloads", 56: "downloads", 57: "downloads", 58: "downloads",
    2: "messages", 8: "messages", 9: "messages", 10: "messages", 17: "messages", 44: "messages", 59: "messages",
    3: "friends", 4: "friends",
    5: "achievements",
    6: "system", 7: "system", 12: "system", 15: "system", 16: "system", 18: "system", 21: "system", 22: "system", 23: "system",
    24: "system", 26: "system", 38: "system", 40: "system", 41: "system", 42: "system", 43: "system", 45: "system", 61: "system",
    62: "system", 63: "system", 64: "system", 65: "system", 67: "system",
    19: "community", 20: "community", 27: "community", 28: "community", 29: "community", 30: "community", 31: "community",
    32: "community", 37: "community", 39: "community", 46: "community", 47: "community", 48: "community", 49: "community",
    51: "community", 52: "community", 53: "community", 60: "community",
  };

  const state = {
    ready: false,
    settings: null,
    scalePercent: 100,
    loadedAt: 0,
    loading: null,
    suppressed: new WeakSet(),
    patched: [],
    patchTimer: 0,
    refreshTimer: 0,
    listeners: new Set(),
    destroyed: false,
    stats: { taken: 0, shown: 0, fallback: 0 },
  };

  // -----------------------------------------------------------------------
  // API
  // -----------------------------------------------------------------------

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  async function api(path, body) {
    const response = await fetch(`${apiBase()}${String(path).replace(/^\//, "")}`, body === undefined
      ? { cache: "no-store" }
      : { method: "POST", cache: "no-store", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const payload = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, payload };
  }

  function applyState(payload) {
    if (payload?.settings) {
      state.settings = payload.settings;
      state.scalePercent = Number(payload.scalePercent) || 100;
      state.ready = payload.ready === true;
    } else {
      state.ready = false;
    }
    state.loadedAt = Date.now();
    for (const listener of state.listeners) {
      try { listener(); } catch {}
    }
  }

  function loadState() {
    if (state.loading) return state.loading;
    state.loading = (async () => {
      try {
        const { ok, payload } = await api("/api/popups/state");
        // 403 = TFS Popups is switched off: Steam keeps every notification.
        applyState(ok ? payload : null);
      } catch {
        applyState(null);
      } finally {
        state.loading = null;
        scheduleRefresh();
      }
      return state.settings;
    })();
    return state.loading;
  }

  function scheduleRefresh() {
    window.clearTimeout(state.refreshTimer);
    if (state.destroyed) return;
    state.refreshTimer = window.setTimeout(() => { void loadState(); }, state.ready ? stateRefreshMs : unavailableRetryMs);
  }

  // -----------------------------------------------------------------------
  // Steam lookups
  // -----------------------------------------------------------------------

  function localize(token, fallback, ...args) {
    let text = "";
    try {
      const value = window.LocalizationManager?.LocalizeString?.(token);
      if (typeof value === "string" && value && !value.startsWith("#")) text = value;
    } catch {}
    text = text || fallback || "";
    return text.replace(/%(\d+)\$s/g, (_all, index) => String(args[Number(index) - 1] ?? ""));
  }

  function toAccountId(value) {
    const text = String(value ?? "").trim();
    if (!/^\d+$/.test(text)) return 0;
    try {
      const number = BigInt(text);
      return Number(number > 4294967295n ? number - 76561197960265728n : number);
    } catch {
      return 0;
    }
  }

  function persona(steamId) {
    const accountId = toAccountId(steamId);
    const friends = window.friendStore;
    if (!accountId || !friends) return { name: "", avatar: "", loaded: false };
    let friend = null;
    try {
      const SteamId = friends.currentUserSteamID?.constructor;
      if (typeof SteamId?.InitFromAccountID === "function") friend = friends.GetFriendState(SteamId.InitFromAccountID(accountId));
    } catch {}
    if (!friend) {
      try { friend = (friends.allFriends || []).find((entry) => entry?.m_unAccountID === accountId) || null; } catch {}
    }
    const name = String(friend?.display_name || friend?.m_strNickname || friend?.persona?.m_strPlayerName || friend?.m_persona?.m_strPlayerName || "");
    const avatar = String(friend?.persona?.avatar_url_medium || friend?.m_persona?.avatar_url_medium || "");
    return { name, avatar, loaded: Boolean(name) };
  }

  function app(appId) {
    const id = Number(appId) || 0;
    if (!id) return { name: "", icon: "", iconData: "" };
    let overview = null;
    try { overview = window.appStore?.GetAppOverviewByAppID?.(id) || null; } catch {}
    const name = String(overview?.display_name || "");
    const icon = overview?.icon_hash ? `https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/${id}/${overview.icon_hash}.jpg` : "";
    const format = String(overview?.icon_data_format || "").toLowerCase();
    const iconData = !icon && overview?.icon_data && /^(png|jpe?g|webp)$/.test(format) && String(overview.icon_data).length < 700000
      ? `data:image/${format === "jpg" ? "jpeg" : format};base64,${overview.icon_data}`
      : "";
    return { name, icon, iconData };
  }

  /** Plain fields of a notification: client protobufs and server JSON bodies alike. */
  function fields(notification) {
    if (notification?.eSource === 2) {
      try { return JSON.parse(notification.data?.item?.body_data || "{}") || {}; } catch { return {}; }
    }
    try {
      const data = notification?.data;
      return (typeof data?.toObject === "function" ? data.toObject() : data) || {};
    } catch {
      return {};
    }
  }

  const personaFields = ["steamid", "steamid_sender", "requestor_id", "requestor_steamid", "responder_steamid", "gifter_account", "sender", "inviter"];

  function senderOf(data) {
    for (const key of personaFields) {
      if (data?.[key]) return String(data[key]);
    }
    return "";
  }

  function percent(value) {
    const number = Number(value);
    if (!Number.isFinite(number)) return "";
    return String(Math.round(number <= 1 ? number * 100 : number));
  }

  // -----------------------------------------------------------------------
  // Describing a notification
  // -----------------------------------------------------------------------

  /**
   * Text and artwork for one Steam notification, or null when TFS has no good
   * description for it (Steam then shows its own toast).
   */
  function describe(type, notification) {
    const data = fields(notification);
    const sender = senderOf(data);
    const who = sender ? persona(sender) : { name: "", avatar: "" };
    const withAvatar = (popup) => ({ ...popup, imageUrl: popup.imageUrl ?? who.avatar, roundImage: popup.roundImage ?? Boolean(who.avatar) });

    switch (type) {
      case 5: {
        const game = app(data.appid);
        const max = Number(data.max_progress) || 0;
        const current = Number(data.current_progress) || 0;
        const inProgress = data.achieved === false && max > 0;
        const rarity = Number(data.global_achieved_pct);
        const detail = [
          localize("#Notification_Achievement_Title", "Achievement"),
          game.name,
          inProgress ? `${current} / ${max}` : Number.isFinite(rarity) && rarity > 0 ? `${rarity.toFixed(rarity < 10 ? 1 : 0)} %` : "",
        ].filter(Boolean).join("  ·  ");
        return {
          title: String(data.name || localize("#Notification_Achievement_Title", "Achievement")),
          body: String(data.description || ""),
          detail,
          imageUrl: String(data.image_url || game.icon || ""),
          imageData: data.image_url ? "" : game.iconData,
          progress: inProgress ? Math.max(0, Math.min(1, current / max)) : null,
        };
      }
      case 8:
        return withAvatar({ title: String(data.title || who.name || ""), body: String(data.body || ""), imageUrl: String(data.icon || who.avatar || ""), roundImage: true });
      case 9: {
        const from = who.name ? `${who.name}: ` : "";
        return withAvatar({ title: String(data.title || localize("#Notification_GroupMessage_Title", "Group message")), body: `${from}${data.body || data.rawbody || ""}`, imageUrl: String(data.icon || who.avatar || ""), roundImage: true });
      }
      case 3:
        return who.name ? withAvatar({ title: who.name, body: `${localize("#Notification_FriendInGame_Body_ShortToast", "is now playing")} ${data.game_name || ""}`.trim() }) : null;
      case 4:
        return who.name ? withAvatar({ title: who.name, body: localize("#Notification_FriendOnline_Body_Short", "is now online") }) : null;
      case 17:
        return withAvatar({ title: who.name || localize("#Notification_IncomingVoiceChat", "Voice chat"), body: localize("#Notification_IncomingVoiceChat", "Voice chat request") });
      case 10:
        return { title: localize("#Notification_FriendInviteRollup_Title", "Friend invites"), body: localize("#Notification_FriendInviteRollup_Body", "You have %1$s new friend invites", data.new_invite_count ?? "") };
      case 2:
        return withAvatar({ title: localize("#Notification_FriendInvite_Title", "Friend invite"), body: who.name || localize("#SteamNotifications_FriendInvite_Body_Generic", "You received a friend invite") });
      case 44:
        return withAvatar({ title: "Steam Family", body: who.name || "Family invite" });
      case 59: {
        const game = app(data.appid);
        return { title: "Playtest", body: game.name, imageUrl: game.icon, imageData: game.iconData };
      }
      case 1: {
        const game = app(data.dlc_appid || data.appid);
        return {
          title: localize("#Notification_DownloadComplete_Title", "Download complete"),
          body: game.name || app(data.appid).name,
          detail: localize("#Notification_DownloadComplete_Description", "Ready to play"),
          imageUrl: game.icon,
          imageData: game.iconData,
        };
      }
      case 14:
        return { title: "Screenshot", body: String(data.description || "").startsWith("#") ? "" : String(data.description || "") };
      case 54:
        return { title: localize("#Notification_ClipDownloaded_Title", "Clip downloaded"), body: "" };
      case 55:
        return { title: localize("#Notification_RecordingStarted_Title", "Recording started"), body: "" };
      case 56:
        return { title: localize("#Notification_RecordingStopped_Title", "Recording stopped"), body: "" };
      case 58:
        return { title: localize("#Notification_InstantClip_Title", "Clip saved"), body: "" };
      case 50:
        return { title: localize("#Notification_GRE_Title", "Recording problem"), body: localize("#Notification_GRE_Body_Short", "") };
      case 62:
        return { title: localize("#Notification_ControllerBatteryLow", "Low battery: %1$s %", percent(data.pct_remaining)), body: "" };
      case 63:
        return { title: localize("#Notification_ControllerConnected", "Controller connected"), body: "" };
      case 64:
        return { title: localize("#Notification_ControllerDisconnected", "Controller disconnected"), body: String(data.controller_name || "") };
      case 6:
        return { title: localize("#Notification_BatteryLow", "Battery low"), body: data.pct_remaining !== undefined ? `${percent(data.pct_remaining)} %` : "" };
      case 22:
        return { title: localize("#QuickAccess_Tab_Notifications_LowDiskSpace", "Low disk space"), body: "" };
      case 15:
      case 16: {
        const game = app(data.appid);
        return {
          title: type === 15 ? localize("#Notification_CloudSyncFailure_Title", "Cloud sync failed") : localize("#Notification_CloudSyncConflict_Title", "Cloud sync conflict"),
          body: game.name,
          imageUrl: game.icon,
          imageData: game.iconData,
        };
      }
      case 7:
        return {
          title: localize(data.type === 2 ? "#Notification_SystemUpdateRestart_Title" : "#Notification_SystemUpdateAvailable_Title", "System update"),
          body: localize(data.type === 2 ? "#Notification_SystemUpdateRestart_Body_Short" : "#Notification_SystemUpdateAvailable_Body_Short", ""),
        };
      case 61:
        return { title: localize("#HardwareUpdateNotification_Title", "Hardware update"), body: localize("#HardwareUpdateNotification_Body", "") };
      case 40:
        return { title: localize("#SteamInput_ChangeActionSet_Toast", "Action set changed"), body: String(data.action_set_name || "") };
      case 19:
        return withAvatar({ title: localize("#Notification_GiftReceived_Title", "Gift"), body: localize("#Notification_GiftReceived_Body", "You received a gift") });
      case 28: {
        const game = app(data.appid);
        return { title: localize("#SteamNotifications_Wishlist", "Wishlist"), body: game.name || localize("#SteamNotifications_Wishlist_Generic", "A wishlisted game is on sale"), imageUrl: game.icon, imageData: game.iconData };
      }
      case 29:
        return withAvatar({ title: localize("#SteamNotifications_TradeOffer_Title", "Trade offer"), body: who.name ? localize("#SteamNotifications_TradeOffer_Body_Short", "Trade offer from %1$s", who.name) : localize("#SteamNotifications_TradeOffer_Body", "You received a trade offer") });
      case 27:
        return { title: localize("#SteamNotifications_Comment", "Comment"), body: String(data.text || data.comment || data.title || "") };
      case 31:
      case 37:
        return data.title || data.display_name || data.body
          ? { title: String(data.title || data.display_name || "Steam"), body: String(data.body || ""), imageUrl: String(data.image || "") }
          : null;
      default:
        return null;
    }
  }

  /** Server notifications need personas/apps loaded first (Steam waits too). */
  async function waitForServerData(notification) {
    const data = fields(notification);
    const sender = senderOf(data);
    const appId = Number(data.appid) || 0;
    const started = Date.now();
    while (Date.now() - started < serverDataWaitMs && !state.destroyed) {
      const personaReady = !sender || persona(sender).loaded;
      const appReady = !appId || Boolean(app(appId).name);
      if (personaReady && appReady) return;
      await new Promise((resolve) => window.setTimeout(resolve, 250));
    }
  }

  // -----------------------------------------------------------------------
  // Taking over Steam's toast decision
  // -----------------------------------------------------------------------

  function notificationStore() {
    try { return window.NotificationStore || null; } catch { return null; }
  }

  function takeOver(store, type, definition, notification) {
    if (!state.ready || !state.settings || !notification) return false;
    const category = typeCategories[type];
    if (!category || state.settings.categories?.[category]?.managed !== true) return false;
    // Steam drops these after this check; keep its behaviour.
    if (notification.eSource === 2 && notification.data?.item?.viewed) return false;
    if (notification.eSource !== 2 && store.BShouldSupressToasts?.()) return false;

    // Steam's sound preference for this toast, asked before it is marked.
    let wantsSound = false;
    try { wantsSound = store.ChooseSound?.(definition, notification) != null; } catch {}
    state.suppressed.add(notification);
    state.stats.taken += 1;
    void deliver(store, type, definition, notification, category, wantsSound);
    return true;
  }

  async function deliver(store, type, definition, notification, category, wantsSound) {
    let popup = null;
    try {
      if (notification.eSource === 2) await waitForServerData(notification);
      popup = describe(type, notification);
    } catch (error) {
      console.warn("[TFS Popups] could not describe notification", type, error);
    }
    if (!popup || !String(popup.title || "").trim() || state.destroyed) {
      giveBack(store, definition, notification);
      return;
    }

    const soundMode = state.settings?.soundMode || "theme";
    let result = null;
    try {
      result = await api("/api/popups/show", {
        category,
        type,
        title: popup.title,
        body: popup.body || "",
        detail: popup.detail || "",
        imageUrl: popup.imageUrl || "",
        imageData: popup.imageData || "",
        roundImage: Boolean(popup.roundImage),
        progress: popup.progress ?? null,
        sound: wantsSound,
      });
    } catch {}

    if (!result?.ok || result.payload?.shown !== true) {
      if (result && (result.status === 403 || result.status === 404)) applyState(null);
      giveBack(store, definition, notification);
      return;
    }

    state.stats.shown += 1;
    state.suppressed.delete(notification);
    if (wantsSound && soundMode === "steam") {
      try { store.PlayNotificationSound?.(notification); } catch {}
    }
  }

  /** Back into Steam's own toast queue, the same way Steam queues server toasts. */
  function giveBack(store, definition, notification) {
    state.suppressed.delete(notification);
    state.stats.fallback += 1;
    try {
      notification.nToastDurationMS = notification.nToastDurationMS || definition.toastDurationMS || defaultToastMs;
      store.m_rgNotificationToasts = store.m_rgNotificationToasts.concat(notification);
      store.DispatchNextToast();
    } catch (error) {
      console.warn("[TFS Popups] could not hand the toast back to Steam", error);
    }
  }

  function patchDefinition(store, type, definition) {
    const showDescriptor = Object.getOwnPropertyDescriptor(definition, "showToast");
    const soundDescriptor = Object.getOwnPropertyDescriptor(definition, "playSound");
    if (showDescriptor?.configurable === false || soundDescriptor?.configurable === false) return;

    // Steam may assign new values later (server types do on every
    // notification); the setters keep them, the getters wrap them.
    let steamShow = definition.showToast;
    let steamPlay = definition.playSound;
    const hadPlay = Object.prototype.hasOwnProperty.call(definition, "playSound");

    const show = function tfsPopupsShowToast(notification) {
      const steamWants = typeof steamShow === "boolean"
        ? steamShow
        : typeof steamShow === "function" ? Boolean(steamShow(notification)) : false;
      if (!steamWants) return false;
      try {
        if (takeOver(store, type, definition, notification)) return false;
      } catch (error) {
        console.warn("[TFS Popups] take over failed", error);
      }
      return true;
    };

    const play = function tfsPopupsPlaySound(target, notification) {
      if (state.suppressed.has(notification)) return false;
      const value = steamPlay ?? Boolean(definition.sound);
      return typeof value === "boolean" ? value : typeof value === "function" ? value(target, notification) : false;
    };

    Object.defineProperty(definition, "showToast", { configurable: true, enumerable: true, get: () => show, set: (value) => { steamShow = value; } });
    Object.defineProperty(definition, "playSound", { configurable: true, enumerable: true, get: () => play, set: (value) => { steamPlay = value; } });
    const restore = () => {
      delete definition.showToast;
      delete definition.playSound;
      definition.showToast = steamShow;
      if (hadPlay || steamPlay !== undefined) definition.playSound = steamPlay;
      delete definition[patchMark];
    };
    Object.defineProperty(definition, patchMark, { configurable: true, value: { version, restore } });
    state.patched.push(restore);
  }

  function patchTargets() {
    window.clearTimeout(state.patchTimer);
    if (state.destroyed) return false;
    const store = notificationStore();
    let targets = null;
    try { targets = store?.GetNotificationTargets?.() || null; } catch {}
    if (!targets) {
      state.patchTimer = window.setTimeout(patchTargets, patchRetryMs);
      return false;
    }
    for (const [key, definition] of Object.entries(targets)) {
      const type = Number(key);
      if (!typeCategories[type] || !definition || typeof definition !== "object") continue;
      const previous = definition[patchMark];
      if (previous?.version >= version) continue;
      try { previous?.restore?.(); } catch {}
      try { patchDefinition(store, type, definition); } catch (error) { console.warn("[TFS Popups] patch failed", type, error); }
    }
    return true;
  }

  // -----------------------------------------------------------------------
  // Previews
  // -----------------------------------------------------------------------

  function selfPersona() {
    try {
      const friends = window.friendStore;
      const id = friends?.currentUserSteamID?.GetAccountID?.();
      return id ? persona(id) : { name: "", avatar: "" };
    } catch {
      return { name: "", avatar: "" };
    }
  }

  function samplePopup(category) {
    const me = selfPersona();
    switch (category) {
      case "achievements":
        return {
          title: "Pole Position",
          body: "Win a race from first place on the grid.",
          detail: `${localize("#Notification_Achievement_Title", "Achievement")}  ·  4.2 %`,
          imageUrl: "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/270880/2aec8af4b4b23cb6054d9aea06194621cdd66945.jpg",
        };
      case "messages":
        return { title: me.name || "Friend", body: "Ready to play?", imageUrl: me.avatar, roundImage: true };
      case "friends":
        return { title: me.name || "Friend", body: localize("#Notification_FriendOnline_Body_Short", "is now online"), imageUrl: me.avatar, roundImage: true };
      case "downloads":
        return { title: localize("#Notification_DownloadComplete_Title", "Download complete"), body: "Tools for Steam", detail: localize("#Notification_DownloadComplete_Description", "Ready to play") };
      case "system":
        return { title: localize("#Notification_ControllerConnected", "Controller connected"), body: "" };
      default:
        return { title: localize("#Notification_GiftReceived_Title", "Gift"), body: localize("#Notification_GiftReceived_Body", "You received a gift") };
    }
  }

  async function preview(category = "achievements", overrides = {}) {
    const sample = samplePopup(category);
    const { ok, payload } = await api("/api/popups/show", { category, type: 0, sound: true, preview: true, ...sample, ...overrides }).catch(() => ({ ok: false }));
    return ok && payload?.shown === true;
  }

  // -----------------------------------------------------------------------
  // Lifecycle
  // -----------------------------------------------------------------------

  function refresh() {
    state.loadedAt = 0;
    void loadState();
    patchTargets();
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.patchTimer);
    window.clearTimeout(state.refreshTimer);
    state.patched.splice(0).forEach((restore) => { try { restore(); } catch {} });
    state.listeners.clear();
    if (window.__tfsPopups === api_) delete window.__tfsPopups;
  }

  const api_ = {
    version,
    refresh,
    destroy,
    preview,
    get ready() { return state.ready; },
    get stats() { return { ...state.stats }; },
    // exposed for tests
    internals: { typeCategories, describe, takeOver, patchTargets, localize, toAccountId, state },
  };
  window.__tfsPopups = api_;

  patchTargets();
  void loadState();
  return "injected";
})();
