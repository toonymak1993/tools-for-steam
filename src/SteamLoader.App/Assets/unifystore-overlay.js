(() => {
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";
  const stateVersion = 8;
  const overlayStateStorageKey = "ToolsForSteamPluginStoreOverlayState";
  const inputStorageKey = "ToolsForSteamPluginStoreInput";
  const channelName = "ToolsForSteamPluginStoreChannel";
  const rootId = "steamloader-unifystore-root";
  const styleId = "steamloader-unifystore-style";
  const storeOrder = ["epic-games", "gog-galaxy"];
  const storeFallbacks = {
    "epic-games": {
      id: "epic-games",
      title: "Epic",
      enabled: true,
      authReady: false,
      statusText: "Waiting for login",
      detailText: "Login with Epic and refresh the account library.",
      installedCount: 0,
      availableCount: 0,
      games: [],
    },
    "gog-galaxy": {
      id: "gog-galaxy",
      title: "GOG",
      enabled: true,
      authReady: false,
      statusText: "Waiting for login",
      detailText: "Login with GOG and refresh the account library.",
      installedCount: 0,
      availableCount: 0,
      games: [],
    },
  };

  const previous = window.__steamLoaderUnifyStoreOverlay;
  if (previous?.version === stateVersion) {
    previous.ensureMounted?.();
    return;
  }

  if (previous) {
    previous?.cleanup?.();
    document.getElementById(rootId)?.remove();
    document.getElementById(styleId)?.remove();
  }

  const state = {
    version: stateVersion,
    root: null,
    open: false,
    loading: false,
    busy: false,
    error: "",
    status: "",
    snapshot: null,
    activeStoreId: "epic-games",
    focusIndex: 0,
    authDraftByStoreId: {},
    authPanelStoreId: "",
    lastInputNonce: "",
    lastStateNonce: "",
    pollTimer: 0,
    stateBroadcastTimer: 0,
    gamepadTimer: 0,
    reloadTimer: 0,
    buttonState: {},
    keyHandler: null,
    storageHandler: null,
    channel: null,
    channelHandler: null,
  };

  window.__steamLoaderUnifyStoreOverlay = {
    version: stateVersion,
    ensureMounted: () => {
      ensureStyle();
      ensureRoot();
    },
    cleanup,
  };

  function cleanup() {
    if (state.pollTimer) {
      window.clearInterval(state.pollTimer);
      state.pollTimer = 0;
    }
    if (state.stateBroadcastTimer) {
      window.clearInterval(state.stateBroadcastTimer);
      state.stateBroadcastTimer = 0;
    }
    if (state.gamepadTimer) {
      window.clearInterval(state.gamepadTimer);
      state.gamepadTimer = 0;
    }
    if (state.reloadTimer) {
      window.clearTimeout(state.reloadTimer);
      state.reloadTimer = 0;
    }
    if (state.keyHandler) {
      window.removeEventListener("keydown", state.keyHandler, true);
      state.keyHandler = null;
    }
    if (state.storageHandler) {
      window.removeEventListener("storage", state.storageHandler);
      state.storageHandler = null;
    }
    if (state.channel && state.channelHandler) {
      try {
        state.channel.removeEventListener("message", state.channelHandler);
      } catch {
      }
    }
    try {
      state.channel?.close?.();
    } catch {
    }
    document.body?.classList?.remove("steamloader-unifystore-open");
  }

  function install() {
    ensureStyle();
    ensureRoot();
    installInputListeners();
    state.pollTimer = window.setInterval(pollOverlayState, 700);
    state.gamepadTimer = window.setInterval(pollGamepads, 58);
    void pollOverlayState();
  }

  function normalizeApiPath(path) {
    return `${apiBase}${path}`.replace(/([^:]\/)\/+/g, "$1");
  }

  async function fetchJson(path, options = {}) {
    const response = await fetch(normalizeApiPath(path), {
      cache: "no-store",
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {}),
      },
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(payload.message || `Request failed (${response.status}).`);
    }
    return payload;
  }

  async function pollOverlayState() {
    try {
      const payload = await fetchJson("api/unifystore/overlay/state");
      const isOpen = Boolean(payload?.isOpen);
      if (isOpen !== state.open) {
        setOpen(isOpen);
      }
      if (isOpen && !state.snapshot && !state.loading) {
        void loadLibrary();
      }
    } catch {
      if (state.open) {
        state.error = "Storefront backend is not reachable.";
        render();
      }
    }
  }

  function setOpen(open) {
    state.open = Boolean(open);
    document.body?.classList?.toggle("steamloader-unifystore-open", state.open);
    if (state.open) {
      state.status = state.status || "Loading account libraries...";
      ensureStateBroadcast();
      void loadLibrary();
    } else {
      state.busy = false;
      state.status = "";
      state.error = "";
      broadcastOverlayState(false);
    }
    render();
  }

  function ensureStateBroadcast() {
    broadcastOverlayState(true);
    if (state.stateBroadcastTimer) {
      return;
    }
    state.stateBroadcastTimer = window.setInterval(() => {
      if (!state.open) {
        return;
      }
      broadcastOverlayState(true);
    }, 450);
  }

  function getBridgeChannel() {
    if (state.channel || typeof BroadcastChannel !== "function") {
      return state.channel;
    }
    try {
      state.channel = new BroadcastChannel(channelName);
      state.channelHandler = (event) => {
        consumeBridgeInput(event.data);
      };
      state.channel.addEventListener("message", state.channelHandler);
    } catch {
      state.channel = null;
    }
    return state.channel;
  }

  function broadcastOverlayState(active) {
    const payload = {
      type: "overlay-state",
      active: Boolean(active),
      expiresAt: active ? Date.now() + 2200 : 0,
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      source: "unifystore",
    };
    try {
      getBridgeChannel()?.postMessage(payload);
    } catch {
    }
    try {
      window.localStorage?.setItem(overlayStateStorageKey, JSON.stringify(payload));
    } catch {
    }
  }

  function installInputListeners() {
    state.keyHandler = (event) => {
      if (!state.open) {
        return;
      }
      if (isTextInputElement(event.target)) {
        return;
      }
      const action = actionFromKey(event);
      if (!action) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      handleAction(action, "keyboard");
    };
    window.addEventListener("keydown", state.keyHandler, true);

    state.storageHandler = (event) => {
      if (event.key === inputStorageKey) {
        consumeBridgeInput(event.newValue);
      }
    };
    window.addEventListener("storage", state.storageHandler);
    getBridgeChannel();
  }

  function consumeBridgeInput(raw) {
    if (!state.open || !raw) {
      return;
    }
    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type !== "input" || payload.nonce === state.lastInputNonce) {
        return;
      }
      state.lastInputNonce = payload.nonce || `${Date.now()}`;
      handleAction(payload.action, payload.source || "bridge");
    } catch {
    }
  }

  async function loadLibrary() {
    state.loading = true;
    state.error = "";
    render();
    try {
      state.snapshot = await fetchJson("api/store-sync/state");
      state.status = state.snapshot?.unifySteam?.statusText || "Libraries loaded.";
      if (!getStores().some((store) => store.id === state.activeStoreId)) {
        state.activeStoreId = getStores()[0]?.id || "epic-games";
        state.focusIndex = 0;
      }
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.loading = false;
      render();
    }
  }

  async function refreshLibrary(storeId = "") {
    state.busy = true;
    state.error = "";
    state.status = storeId ? "Refreshing store library..." : "Refreshing Epic and GOG...";
    render();
    try {
      state.snapshot = await fetchJson("api/unifystore/stores/refresh", {
        method: "POST",
        body: JSON.stringify({ value: storeId || "" }),
      });
      state.status = state.snapshot?.unifySteam?.statusText || "Libraries refreshed.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.busy = false;
      render();
    }
  }

  async function loginStore(storeId) {
    state.busy = true;
    state.error = "";
    state.status = "Opening store login...";
    state.authPanelStoreId = storeId;
    render();
    try {
      state.snapshot = await fetchJson("api/unifystore/stores/login", {
        method: "POST",
        body: JSON.stringify({ value: storeId }),
      });
      state.status = "Login flow opened. Finish it, then refresh the library.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.busy = false;
      render();
    }
  }

  async function submitAuthCode(storeId) {
    const draft = String(state.authDraftByStoreId[storeId] || "").trim();
    if (!draft) {
      state.error = "Paste the login code, final page URL, or authorizationCode JSON first.";
      render();
      return;
    }

    state.busy = true;
    state.error = "";
    state.status = "Saving store login...";
    render();
    try {
      state.snapshot = await fetchJson("api/store-sync/unifysteam/stores/auth-code", {
        method: "POST",
        body: JSON.stringify({ storeId, value: draft }),
      });
      state.authDraftByStoreId[storeId] = "";
      state.authPanelStoreId = "";
      state.status = "Login saved. Refresh this store to load the account library.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.busy = false;
      render();
    }
  }

  async function launchGame(storeId, gameId, title) {
    state.busy = true;
    state.error = "";
    state.status = `Starting ${title || "game"}...`;
    render();
    try {
      const payload = await fetchJson("api/unifystore/games/launch", {
        method: "POST",
        body: JSON.stringify({ storeId, gameId }),
      });
      if (payload?.snapshot) {
        state.snapshot = payload.snapshot;
      }
      state.status = payload?.message || "Launcher started.";
      scheduleLibraryReload();
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.busy = false;
      render();
    }
  }

  function scheduleLibraryReload() {
    if (state.reloadTimer) {
      window.clearTimeout(state.reloadTimer);
    }
    state.reloadTimer = window.setTimeout(() => {
      state.reloadTimer = 0;
      if (state.open) {
        void loadLibrary();
      }
    }, 2500);
  }

  async function closeOverlay() {
    state.open = false;
    render();
    broadcastOverlayState(false);
    try {
      await fetchJson("api/unifystore/overlay/close", {
        method: "POST",
        body: "{}",
      });
    } catch {
    }
    document.body?.classList?.remove("steamloader-unifystore-open");
  }

  function getStores() {
    const stores = Array.isArray(state.snapshot?.unifySteam?.stores)
      ? state.snapshot.unifySteam.stores
      : [];
    return storeOrder.map((storeId) => ({
      ...storeFallbacks[storeId],
      ...(stores.find((store) => store.id === storeId) || {}),
    }));
  }

  function getActiveStore() {
    return getStores().find((store) => store.id === state.activeStoreId) || getStores()[0] || storeFallbacks["epic-games"];
  }

  function normalizeGameTitleKey(value) {
    return String(value || "")
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, " ")
      .replace(/\s+/g, " ")
      .trim();
  }

  function preferGame(left, right) {
    const leftScore =
      (left.installed ? 8 : 0) +
      (left.executablePath ? 4 : 0) +
      (left.installPath ? 2 : 0) +
      (left.imageUrl ? 1 : 0);
    const rightScore =
      (right.installed ? 8 : 0) +
      (right.executablePath ? 4 : 0) +
      (right.installPath ? 2 : 0) +
      (right.imageUrl ? 1 : 0);
    if (leftScore !== rightScore) {
      return leftScore > rightScore ? left : right;
    }
    return String(left.id || "").length <= String(right.id || "").length ? left : right;
  }

  function getDedupedGames(games) {
    const byTitle = new Map();
    for (const game of games) {
      const key = normalizeGameTitleKey(game?.title || game?.id);
      if (!key) {
        continue;
      }
      byTitle.set(key, byTitle.has(key) ? preferGame(byTitle.get(key), game) : game);
    }
    return [...byTitle.values()];
  }

  function getSortedGames(store) {
    const games = Array.isArray(store?.games) ? store.games : [];
    return getDedupedGames(games).sort((left, right) => {
      if (Boolean(left.installed) !== Boolean(right.installed)) {
        return left.installed ? -1 : 1;
      }
      return String(left.title || "").localeCompare(String(right.title || ""), undefined, { sensitivity: "base" });
    });
  }

  function getGameGroups(store) {
    const games = getSortedGames(store);
    return {
      installed: games.filter((game) => game.installed),
      available: games.filter((game) => !game.installed),
    };
  }

  function switchStore(delta) {
    const stores = getStores();
    const currentIndex = Math.max(0, stores.findIndex((store) => store.id === state.activeStoreId));
    const nextIndex = (currentIndex + delta + stores.length) % stores.length;
    state.activeStoreId = stores[nextIndex]?.id || state.activeStoreId;
    state.focusIndex = 0;
    render();
  }

  function ensureStyle() {
    if (document.getElementById(styleId)) {
      return;
    }
    const style = document.createElement("style");
    style.id = styleId;
    style.textContent = `
      .steamloader-unifystore-root {
        --unifystore-bg: #0b1118;
        --unifystore-bg-soft: #101822;
        --unifystore-panel: #171d25;
        --unifystore-panel-soft: #1b2838;
        --unifystore-panel-raised: #223044;
        --unifystore-text: #dbe8f6;
        --unifystore-muted: #8f9fb1;
        --unifystore-dim: #677487;
        --unifystore-blue: #66c0f4;
        --unifystore-blue-strong: #1a9fff;
        --unifystore-green: #8bc53f;
        --unifystore-border: rgba(102, 192, 244, 0.18);
        --unifystore-border-soft: rgba(255, 255, 255, 0.08);
        --unifystore-focus-gutter: 18px;
        position: fixed;
        inset: 0;
        z-index: 2147483200;
        display: none;
        color: var(--unifystore-text);
        font-family: "Motiva Sans", "Segoe UI", sans-serif;
        pointer-events: none;
      }

      .steamloader-unifystore-root,
      .steamloader-unifystore-root * {
        box-sizing: border-box;
      }

      .steamloader-unifystore-root.is-open {
        display: block;
        pointer-events: auto;
      }

      .steamloader-unifystore-shell {
        position: fixed;
        inset: 0;
        width: 100vw;
        height: 100vh;
        max-width: 100vw;
        max-height: 100vh;
        display: grid;
        grid-template-rows: auto auto minmax(0, 1fr) auto;
        gap: clamp(10px, 1.35vw, 18px);
        padding:
          clamp(44px, 4.8vh, 64px)
          clamp(46px, 4.4vw, 78px)
          clamp(34px, 4vh, 54px);
        background:
          radial-gradient(circle at 22% 0%, rgba(102, 192, 244, 0.16), transparent 31%),
          linear-gradient(180deg, #1b2838 0%, #101822 42%, #070b10 100%);
        overflow: hidden;
      }

      .steamloader-unifystore-shell::before {
        content: "";
        position: absolute;
        inset: 0;
        background:
          linear-gradient(90deg, rgba(255,255,255,0.035), transparent 18%, transparent 82%, rgba(255,255,255,0.025)),
          linear-gradient(180deg, rgba(255,255,255,0.055), transparent 18%);
        pointer-events: none;
      }

      .steamloader-unifystore-topbar,
      .steamloader-unifystore-tabs,
      .steamloader-unifystore-main,
      .steamloader-unifystore-footer {
        position: relative;
        z-index: 1;
      }

      .steamloader-unifystore-topbar {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        align-items: center;
        justify-content: space-between;
        gap: clamp(18px, 2.4vw, 34px);
        min-height: 0;
      }

      .steamloader-unifystore-topbar > div:first-child {
        min-width: 0;
      }

      .steamloader-unifystore-kicker {
        color: var(--unifystore-blue);
        font-size: 12px;
        font-weight: 900;
        letter-spacing: 0.14em;
        text-transform: uppercase;
      }

      .steamloader-unifystore-title {
        margin-top: 4px;
        font-size: clamp(30px, 3.15vw, 48px);
        font-weight: 950;
        line-height: 1;
        letter-spacing: -0.035em;
      }

      .steamloader-unifystore-subtitle {
        max-width: min(760px, 58vw);
        margin-top: 8px;
        color: var(--unifystore-muted);
        font-size: clamp(13px, 1.25vw, 16px);
        line-height: 1.35;
      }

      .steamloader-unifystore-actions {
        display: flex;
        align-items: center;
        gap: 10px;
        flex-wrap: wrap;
        justify-content: flex-end;
        max-width: min(43vw, 560px);
      }

      .steamloader-unifystore-chip,
      .steamloader-unifystore-button {
        border: 1px solid var(--unifystore-border-soft);
        border-radius: 999px;
        background: rgba(23, 29, 37, 0.82);
        color: var(--unifystore-text);
        box-shadow: 0 10px 26px rgba(0, 0, 0, 0.28);
      }

      .steamloader-unifystore-chip {
        padding: 9px 13px;
        color: var(--unifystore-muted);
        font-size: 12px;
        font-weight: 800;
        letter-spacing: 0.02em;
      }

      .steamloader-unifystore-button {
        padding: 10px 15px;
        font-size: 12px;
        font-weight: 900;
        text-transform: uppercase;
        cursor: pointer;
        transition: transform 120ms ease, box-shadow 120ms ease, border-color 120ms ease, background 120ms ease;
      }

      .steamloader-unifystore-button:hover,
      .steamloader-unifystore-button:focus-visible,
      .steamloader-unifystore-button.is-controller-focus,
      .steamloader-unifystore-tab:hover,
      .steamloader-unifystore-tab:focus-visible,
      .steamloader-unifystore-tab.is-controller-focus {
        outline: none;
        transform: translateY(-1px);
        border-color: rgba(102, 192, 244, 0.95);
        box-shadow: 0 0 0 3px rgba(102, 192, 244, 0.22), 0 18px 38px rgba(0, 0, 0, 0.34);
      }

      .steamloader-unifystore-button.is-primary {
        background: linear-gradient(180deg, #67c1f5 0%, #2b86d5 100%);
        color: #06111c;
        border-color: transparent;
      }

      .steamloader-unifystore-tabs {
        display: grid;
        grid-template-columns: auto 1fr auto;
        gap: 12px;
        align-items: stretch;
      }

      .steamloader-unifystore-bumper {
        align-self: center;
        min-width: 42px;
        padding: 8px 10px;
        border-radius: 10px;
        background: rgba(11, 17, 24, 0.62);
        color: var(--unifystore-dim);
        font-size: 13px;
        font-weight: 950;
        letter-spacing: 0.12em;
        text-align: center;
      }

      .steamloader-unifystore-tab-list {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 10px;
      }

      .steamloader-unifystore-tab {
        min-height: 48px;
        border: 1px solid var(--unifystore-border-soft);
        border-radius: 10px;
        padding: 8px 12px;
        background: rgba(23, 29, 37, 0.72);
        color: var(--unifystore-text);
        text-align: left;
        cursor: pointer;
        transition: transform 120ms ease, box-shadow 120ms ease, border-color 120ms ease, background 120ms ease;
      }

      .steamloader-unifystore-tab.is-active {
        background: linear-gradient(180deg, rgba(47, 93, 130, 0.92), rgba(27, 40, 56, 0.92));
        border-color: rgba(102, 192, 244, 0.62);
      }

      .steamloader-unifystore-tab-title {
        display: flex;
        justify-content: space-between;
        gap: 12px;
        font-size: clamp(14px, 1.22vw, 17px);
        font-weight: 950;
      }

      .steamloader-unifystore-tab-copy {
        margin-top: 3px;
        color: var(--unifystore-muted);
        font-size: 11px;
        line-height: 1.3;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }

      .steamloader-unifystore-main {
        overflow-x: hidden;
        overflow-y: auto;
        min-height: 0;
        margin-left: calc(var(--unifystore-focus-gutter) * -1);
        margin-right: calc(var(--unifystore-focus-gutter) * -1);
        padding-left: var(--unifystore-focus-gutter);
        padding-right: calc(var(--unifystore-focus-gutter) + 8px);
        padding-bottom: 14px;
        scrollbar-gutter: stable;
      }

      .steamloader-unifystore-main::-webkit-scrollbar {
        width: 8px;
      }

      .steamloader-unifystore-main::-webkit-scrollbar-thumb {
        background: rgba(102, 192, 244, 0.24);
        border-radius: 999px;
      }

      .steamloader-unifystore-store-head {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 14px;
        align-items: center;
        margin-bottom: 18px;
        padding: 14px 16px;
        border: 1px solid var(--unifystore-border-soft);
        border-radius: 16px;
        background: rgba(23, 29, 37, 0.78);
        box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
      }

      .steamloader-unifystore-store-title {
        font-size: clamp(20px, 2vw, 28px);
        font-weight: 950;
      }

      .steamloader-unifystore-store-copy,
      .steamloader-unifystore-section-copy,
      .steamloader-unifystore-footer {
        color: var(--unifystore-muted);
        font-size: 13px;
        line-height: 1.4;
      }

      .steamloader-unifystore-store-actions {
        display: flex;
        gap: 10px;
        flex-wrap: wrap;
        justify-content: flex-end;
      }

      .steamloader-unifystore-auth {
        display: grid;
        grid-template-columns: minmax(220px, 0.72fr) minmax(260px, 1fr) auto auto;
        gap: 12px;
        align-items: center;
        margin: -6px 0 18px;
        padding: 14px 16px;
        border: 1px solid rgba(102, 192, 244, 0.18);
        border-radius: 16px;
        background:
          linear-gradient(180deg, rgba(31, 48, 66, 0.86), rgba(17, 25, 35, 0.86));
        box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.045), 0 14px 30px rgba(0, 0, 0, 0.22);
      }

      .steamloader-unifystore-auth-title {
        margin-bottom: 4px;
        color: var(--unifystore-text);
        font-size: 14px;
        font-weight: 950;
      }

      .steamloader-unifystore-auth-copy {
        color: var(--unifystore-muted);
        font-size: 12px;
        line-height: 1.35;
      }

      .steamloader-unifystore-auth-input {
        min-height: 48px;
        resize: vertical;
        border: 1px solid rgba(255, 255, 255, 0.12);
        border-radius: 12px;
        padding: 11px 12px;
        background: rgba(6, 11, 17, 0.74);
        color: var(--unifystore-text);
        font: 850 13px/1.35 "Motiva Sans", "Segoe UI", sans-serif;
        outline: none;
        box-shadow: inset 0 1px 8px rgba(0, 0, 0, 0.28);
      }

      .steamloader-unifystore-auth-input::placeholder {
        color: rgba(143, 159, 177, 0.72);
      }

      .steamloader-unifystore-auth-input:focus,
      .steamloader-unifystore-auth-input.is-controller-focus {
        border-color: rgba(102, 192, 244, 0.95);
        box-shadow: 0 0 0 3px rgba(102, 192, 244, 0.22), inset 0 1px 8px rgba(0, 0, 0, 0.28);
      }

      .steamloader-unifystore-section {
        margin-top: 20px;
      }

      .steamloader-unifystore-section-title {
        display: flex;
        align-items: baseline;
        gap: 12px;
        font-size: clamp(18px, 1.7vw, 22px);
        font-weight: 950;
        letter-spacing: -0.02em;
      }

      .steamloader-unifystore-section-title span {
        color: var(--unifystore-dim);
        font-size: 13px;
        font-weight: 850;
      }

      .steamloader-unifystore-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(clamp(132px, 12.4vw, 220px), 220px));
        gap: clamp(12px, 1.4vw, 20px);
        justify-content: start;
        margin-top: 12px;
      }

      .steamloader-unifystore-card {
        position: relative;
        width: 100%;
        aspect-ratio: 2 / 3;
        min-height: 0;
        border: 1px solid rgba(255, 255, 255, 0.10);
        border-radius: 10px;
        padding: 0;
        overflow: hidden;
        background: var(--unifystore-panel);
        color: var(--unifystore-text);
        text-align: left;
        box-shadow: 0 12px 28px rgba(0, 0, 0, 0.34);
        cursor: pointer;
        transition: transform 120ms ease, box-shadow 120ms ease, border-color 120ms ease, filter 120ms ease;
      }

      .steamloader-unifystore-card.is-installed {
        border-color: rgba(139, 197, 63, 0.88);
        box-shadow: 0 0 0 2px rgba(139, 197, 63, 0.18), 0 12px 28px rgba(0, 0, 0, 0.34);
      }

      .steamloader-unifystore-card:hover,
      .steamloader-unifystore-card:focus-visible,
      .steamloader-unifystore-card.is-controller-focus {
        outline: none;
        transform: translateY(-5px) scale(1.025);
        border-color: rgba(102, 192, 244, 1);
        box-shadow: 0 0 0 4px rgba(102, 192, 244, 0.28), 0 22px 44px rgba(0, 0, 0, 0.46);
        filter: brightness(1.05);
      }

      .steamloader-unifystore-card img,
      .steamloader-unifystore-placeholder {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        display: block;
        object-fit: cover;
        background: linear-gradient(180deg, #26394f, #121a24);
      }

      .steamloader-unifystore-placeholder {
        display: grid;
        place-items: center;
        padding: 18px;
        color: rgba(219, 232, 246, 0.72);
        font-size: clamp(16px, 1.8vw, 22px);
        font-weight: 950;
        text-align: center;
      }

      .steamloader-unifystore-card-info {
        position: absolute;
        left: 0;
        right: 0;
        bottom: 0;
        padding: 52px 11px 11px;
        background: linear-gradient(to top, rgba(5, 9, 14, 0.94), rgba(5, 9, 14, 0.52) 54%, rgba(5, 9, 14, 0));
      }

      .steamloader-unifystore-card-title {
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
        font-size: clamp(12px, 1.1vw, 15px);
        font-weight: 950;
        line-height: 1.15;
        text-shadow: 0 2px 8px rgba(0, 0, 0, 0.72);
      }

      .steamloader-unifystore-card-badge {
        display: inline-flex;
        margin-top: 7px;
        padding: 4px 8px;
        border-radius: 999px;
        background: rgba(102, 192, 244, 0.18);
        color: rgba(219, 232, 246, 0.88);
        font-size: 10px;
        font-weight: 900;
        text-transform: uppercase;
      }

      .steamloader-unifystore-card.is-installed .steamloader-unifystore-card-badge {
        background: rgba(139, 197, 63, 0.92);
        color: #071008;
      }

      .steamloader-unifystore-empty,
      .steamloader-unifystore-error {
        margin-top: 16px;
        padding: 18px;
        border-radius: 14px;
        border: 1px solid var(--unifystore-border-soft);
        background: rgba(23, 29, 37, 0.74);
        color: var(--unifystore-muted);
      }

      .steamloader-unifystore-error {
        border-color: rgba(255, 93, 111, 0.45);
        background: rgba(255, 93, 111, 0.12);
        color: #ffd4db;
      }

      .steamloader-unifystore-footer {
        display: flex;
        gap: 16px;
        align-items: center;
        justify-content: flex-end;
        font-weight: 850;
        min-height: 24px;
      }

      @media (max-width: 1180px) {
        .steamloader-unifystore-subtitle {
          max-width: 48vw;
        }

        .steamloader-unifystore-grid {
          grid-template-columns: repeat(auto-fill, minmax(136px, 190px));
        }
      }

      @media (max-width: 900px) {
        .steamloader-unifystore-shell {
          padding: 28px 22px 22px;
          gap: 12px;
        }

        .steamloader-unifystore-topbar,
        .steamloader-unifystore-store-head {
          grid-template-columns: 1fr;
          display: grid;
          min-height: 0;
        }

        .steamloader-unifystore-subtitle {
          max-width: none;
        }

        .steamloader-unifystore-actions,
        .steamloader-unifystore-store-actions {
          justify-content: flex-start;
          max-width: none;
        }

        .steamloader-unifystore-auth {
          grid-template-columns: 1fr;
        }

        .steamloader-unifystore-bumper {
          display: none;
        }

        .steamloader-unifystore-tabs {
          grid-template-columns: 1fr;
        }

        .steamloader-unifystore-tab-list {
          display: flex;
          overflow-x: auto;
          scroll-snap-type: x proximity;
          padding-bottom: 2px;
        }

        .steamloader-unifystore-tab {
          min-width: min(78vw, 300px);
          scroll-snap-align: start;
        }

        .steamloader-unifystore-grid {
          grid-template-columns: repeat(auto-fill, minmax(118px, 160px));
          gap: 12px;
        }

        .steamloader-unifystore-footer {
          justify-content: flex-start;
          flex-wrap: wrap;
          font-size: 12px;
        }
      }
    `;
    document.head.append(style);
  }

  function ensureRoot() {
    let root = document.getElementById(rootId);
    if (!root) {
      root = document.createElement("div");
      root.id = rootId;
      root.className = "steamloader-unifystore-root";
      document.body.append(root);
    }
    state.root = root;
    return root;
  }

  function render() {
    const root = ensureRoot();
    root.classList.toggle("is-open", state.open);
    if (!state.open) {
      root.replaceChildren();
      return;
    }

    const stores = getStores();
    const activeStore = getActiveStore();
    const groups = getGameGroups(activeStore);
    const shell = el("div", "steamloader-unifystore-shell");

    shell.append(
      renderTopbar(activeStore),
      renderTabs(stores),
      renderMain(activeStore, groups),
      renderFooter(),
    );

    root.replaceChildren(shell);
    syncFocus();
  }

  function renderTopbar(activeStore) {
    const topbar = el("div", "steamloader-unifystore-topbar");
    const brand = el("div");
    brand.append(
      textEl("div", "steamloader-unifystore-kicker", "Tools for Steam"),
      textEl("div", "steamloader-unifystore-title", "Storefront"),
      textEl(
        "div",
        "steamloader-unifystore-subtitle",
        `Browse your connected ${activeStore.title || "store"} account library, including games that are not installed locally yet.`,
      ),
    );

    const totalInstalled = storesTotal("installedCount");
    const totalAvailable = storesTotal("availableCount");
    const actions = el("div", "steamloader-unifystore-actions");
    actions.append(
      ...(totalInstalled > 0
        ? [textEl("div", "steamloader-unifystore-chip", `${totalInstalled} installed`)]
        : []),
      textEl("div", "steamloader-unifystore-chip", `${totalAvailable} total`),
      buttonEl("Refresh All", "steamloader-unifystore-button is-primary", "refresh-all", "", { focusable: false }),
      buttonEl("Close", "steamloader-unifystore-button", "close", "", { focusable: false }),
    );

    topbar.append(brand, actions);
    return topbar;
  }

  function renderTabs(stores) {
    const tabs = el("div", "steamloader-unifystore-tabs");
    const list = el("div", "steamloader-unifystore-tab-list");
    for (const store of stores) {
      const tab = buttonEl(
        "",
        `steamloader-unifystore-tab${store.id === state.activeStoreId ? " is-active" : ""}`,
        "tab",
        "",
        { focusable: false },
      );
      tab.dataset.storeId = store.id;
      tab.append(
        withChildren(
          el("div", "steamloader-unifystore-tab-title"),
          textNode(store.title || store.id),
          textEl("span", "", `${store.installedCount || 0}/${store.availableCount || 0}`),
        ),
        textEl(
          "div",
          "steamloader-unifystore-tab-copy",
          store.authReady
            ? store.accountName
              ? `Signed in as ${store.accountName}`
              : "Signed in and ready"
            : store.statusText || "Login required",
        ),
      );
      list.append(tab);
    }

    tabs.append(
      textEl("div", "steamloader-unifystore-bumper", "LB"),
      list,
      textEl("div", "steamloader-unifystore-bumper", "RB"),
    );
    return tabs;
  }

  function renderMain(store, groups) {
    const main = el("div", "steamloader-unifystore-main");
    if (state.error) {
      main.append(textEl("div", "steamloader-unifystore-error", state.error));
    }
    const showAuthPanel = store.supportsManualCodeAuth && (!store.authReady || state.authPanelStoreId === store.id);

    const head = el("div", "steamloader-unifystore-store-head");
    const headText = el("div");
    headText.append(
      textEl("div", "steamloader-unifystore-store-title", store.title || store.id),
      textEl(
        "div",
        "steamloader-unifystore-store-copy",
        [
          `${store.installedCount || 0} installed / ${store.availableCount || 0} total`,
          store.authReady ? "Account ready" : "Login required",
          store.detailText || "",
        ].filter(Boolean).join(" - "),
      ),
    );
    const actions = el("div", "steamloader-unifystore-store-actions");
    const hasGames = groups.installed.length + groups.available.length > 0;
    actions.append(
      buttonEl(
        store.authReady ? "Account Settings" : "Login",
        "steamloader-unifystore-button",
        store.authReady ? "toggle-auth-panel" : "login",
        store.id,
        { focusable: !hasGames },
      ),
      buttonEl(
        "Refresh Store",
        "steamloader-unifystore-button is-primary",
        "refresh-store",
        store.id,
        { focusable: !hasGames },
      ),
    );
    head.append(headText, actions);
    main.append(head);

    if (showAuthPanel) {
      main.append(renderAuthCodePanel(store));
    }

    if (state.loading && !state.snapshot) {
      main.append(textEl("div", "steamloader-unifystore-empty", "Loading Epic and GOG libraries..."));
      return main;
    }

    if (!store.authReady && groups.installed.length + groups.available.length === 0) {
      main.append(textEl("div", "steamloader-unifystore-empty", "Login first, then refresh this store to load your account library."));
      return main;
    }

    if (groups.installed.length) {
      appendSection(main, "Installed", "Ready to launch", groups.installed, store.id);
    }
    if (groups.available.length) {
      appendSection(
        main,
        "Available",
        store.id === "gog-galaxy" ? "Open in GOG Galaxy to install" : "Install on first launch",
        groups.available,
        store.id
      );
    }

    if (groups.installed.length + groups.available.length === 0) {
      main.append(textEl("div", "steamloader-unifystore-empty", "No games are cached yet. Refresh this store once login is complete."));
    }

    return main;
  }

  function renderAuthCodePanel(store) {
    const panel = el("section", "steamloader-unifystore-auth");
    const copy = store.id === "epic-games"
      ? "After Epic login, paste the authorizationCode JSON, the raw code, or the final page URL here."
      : "After GOG login, paste the final page URL or code here.";
    const textarea = document.createElement("textarea");
    textarea.className = "steamloader-unifystore-auth-input";
    textarea.placeholder = store.id === "epic-games"
      ? '{"authorizationCode":"..."}'
      : "https://embed.gog.com/on_login_success?code=...";
    textarea.value = state.authDraftByStoreId[store.id] || "";
    textarea.rows = 2;
    textarea.spellcheck = false;
    textarea.dataset.unifystoreFocus = "true";
    textarea.dataset.action = "auth-input";
    textarea.dataset.storeId = store.id;
    textarea.addEventListener("input", () => {
      state.authDraftByStoreId[store.id] = textarea.value;
    });
    textarea.addEventListener("focus", () => setFocusToElement(textarea));

    const text = el("div", "steamloader-unifystore-auth-copy");
    text.append(
      textEl("div", "steamloader-unifystore-auth-title", `${store.title || "Store"} Account Settings`),
      textEl("div", "", copy),
    );

    panel.append(
      text,
      textarea,
      buttonEl("Open Login", "steamloader-unifystore-button", "login", store.id),
      buttonEl("Save Login Code", "steamloader-unifystore-button is-primary", "auth-code", store.id),
    );
    return panel;
  }

  function appendSection(parent, title, copy, games, storeId) {
    const section = el("section", "steamloader-unifystore-section");
    section.append(
      withChildren(
        el("div", "steamloader-unifystore-section-title"),
        textNode(title),
        textEl("span", "", `${games.length} games`),
      ),
      textEl("div", "steamloader-unifystore-section-copy", copy),
    );
    if (games.length) {
      const grid = el("div", "steamloader-unifystore-grid");
      for (const game of games) {
        grid.append(renderGameCard(storeId, game));
      }
      section.append(grid);
    }
    parent.append(section);
  }

  function renderGameCard(storeId, game) {
    const card = buttonEl("", `steamloader-unifystore-card${game.installed ? " is-installed" : ""}`, "launch", storeId);
    card.dataset.gameId = game.id || "";
    card.dataset.title = game.title || "Game";

    const originalArtworkUrl = normalizeArtworkUrl(game.imageUrl);
    const artworkUrl = resolveArtworkUrl(game.imageUrl);
    if (artworkUrl) {
      const image = document.createElement("img");
      image.src = artworkUrl;
      image.srcset = buildArtworkSrcSet(artworkUrl);
      image.sizes = "(max-width: 900px) 44vw, 220px";
      image.alt = game.title || "Game artwork";
      image.loading = "lazy";
      image.decoding = "async";
      image.referrerPolicy = "no-referrer";
      image.dataset.fallbackSrc = originalArtworkUrl && originalArtworkUrl !== artworkUrl ? originalArtworkUrl : "";
      image.onerror = () => {
        const fallbackSrc = image.dataset.fallbackSrc || "";
        if (fallbackSrc && image.src !== fallbackSrc) {
          image.dataset.fallbackSrc = "";
          image.srcset = "";
          image.src = fallbackSrc;
          return;
        }

        image.replaceWith(textEl("div", "steamloader-unifystore-placeholder", initials(game.title)));
      };
      card.append(image);
    } else {
      card.append(textEl("div", "steamloader-unifystore-placeholder", initials(game.title)));
    }

    const info = el("div", "steamloader-unifystore-card-info");
    info.append(
      textEl("div", "steamloader-unifystore-card-title", game.title || "Unknown game"),
      textEl("div", "steamloader-unifystore-card-badge", getGameActionLabel(storeId, game)),
    );
    card.append(info);
    return card;
  }

  function getGameActionLabel(storeId, game) {
    if (game?.installed) {
      return "Installed";
    }

    return storeId === "gog-galaxy" ? "Open in Galaxy" : "Install";
  }

  function renderFooter() {
    return withChildren(
      el("div", "steamloader-unifystore-footer"),
      textEl("span", "", "A Select"),
      textEl("span", "", "B Back"),
      textEl("span", "", "LB/RB Stores"),
      textEl("span", "", "X Refresh"),
    );
  }

  function storesTotal(key) {
    return getStores().reduce((total, store) => total + (Number(store[key]) || 0), 0);
  }

  function resolveArtworkUrl(url) {
    const normalized = normalizeArtworkUrl(url);
    if (!normalized) {
      return "";
    }

    if (!/gog-statics\.com/i.test(normalized)) {
      return upgradeEpicArtworkUrl(normalized);
    }

    return upgradeGogArtworkUrl(normalized);
  }

  function normalizeArtworkUrl(url) {
    const value = String(url || "").trim();
    return value.startsWith("//") ? `https:${value}` : value;
  }

  function upgradeGogArtworkUrl(url) {
    return url
      .replace(/_(196|392)(\.[a-z0-9]+)(\?.*)?$/i, "_784$2$3")
      .replace(/_product_card_v2_mobile_slider_\d+(\.[a-z0-9]+)(\?.*)?$/i, "_product_card_v2_mobile_slider_1280$1$2")
      .replace(/_glx_vertical_cover_\d+(\.[a-z0-9]+)(\?.*)?$/i, "_glx_vertical_cover_1200$1$2");
  }

  function upgradeEpicArtworkUrl(url) {
    if (!/(epicgames|unrealengine)/i.test(url)) {
      return url;
    }

    return url
      .replace(/([?&])w=\d+/i, "$1w=1200")
      .replace(/([?&])h=\d+/i, "$1h=1600");
  }

  function buildArtworkSrcSet(url) {
    if (!url) {
      return "";
    }

    const doubleSizeUrl = /gog-statics\.com/i.test(url)
      ? upgradeGogArtworkUrl(url)
      : upgradeEpicArtworkUrl(url);
    return doubleSizeUrl !== url ? `${url} 1x, ${doubleSizeUrl} 2x` : "";
  }

  function buttonEl(label, className, action, storeId = "", options = {}) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    button.dataset.action = action;
    if (options.focusable !== false) {
      button.dataset.unifystoreFocus = "true";
    } else {
      button.tabIndex = -1;
      button.dataset.unifystoreFocus = "false";
    }
    if (storeId) {
      button.dataset.storeId = storeId;
    }
    if (label) {
      button.textContent = label;
    }
    button.addEventListener("click", () => activateElement(button));
    return button;
  }

  function el(tagName, className = "") {
    const element = document.createElement(tagName);
    if (className) {
      element.className = className;
    }
    return element;
  }

  function textEl(tagName, className, text) {
    const element = el(tagName, className);
    element.textContent = text || "";
    return element;
  }

  function textNode(text) {
    return document.createTextNode(text || "");
  }

  function withChildren(element, ...children) {
    element.append(...children);
    return element;
  }

  function initials(title) {
    const words = String(title || "Game")
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 3);
    return words.map((word) => word[0]?.toUpperCase() || "").join("") || "GAME";
  }

  function getFocusables() {
    return Array.from(ensureRoot().querySelectorAll("[data-unifystore-focus='true']"))
      .filter((element) => !element.disabled && element.offsetParent !== null);
  }

  function syncFocus() {
    const focusables = getFocusables();
    if (!focusables.length) {
      return;
    }
    state.focusIndex = Math.max(0, Math.min(state.focusIndex, focusables.length - 1));
    focusables.forEach((element, index) => {
      element.classList.toggle("is-controller-focus", index === state.focusIndex);
    });
    const active = focusables[state.focusIndex];
    active?.focus?.({ preventScroll: true });
    active?.scrollIntoView?.({ block: "nearest", inline: "nearest" });
  }

  function setFocusToElement(element) {
    const focusables = getFocusables();
    const index = focusables.indexOf(element);
    if (index >= 0) {
      state.focusIndex = index;
      syncFocus();
    }
  }

  function moveLinear(delta) {
    const focusables = getFocusables();
    if (!focusables.length) {
      return;
    }
    state.focusIndex = (state.focusIndex + delta + focusables.length) % focusables.length;
    syncFocus();
  }

  function moveDirectional(direction) {
    const focusables = getFocusables();
    const current = focusables[state.focusIndex];
    if (!current) {
      moveLinear(direction === "left" || direction === "up" ? -1 : 1);
      return;
    }

    const currentRect = current.getBoundingClientRect();
    const currentX = currentRect.left + currentRect.width / 2;
    const currentY = currentRect.top + currentRect.height / 2;
    let best = null;
    let bestScore = Number.POSITIVE_INFINITY;

    focusables.forEach((candidate, index) => {
      if (candidate === current) {
        return;
      }
      const rect = candidate.getBoundingClientRect();
      const x = rect.left + rect.width / 2;
      const y = rect.top + rect.height / 2;
      const dx = x - currentX;
      const dy = y - currentY;
      const primary = direction === "left" ? -dx
        : direction === "right" ? dx
          : direction === "up" ? -dy
            : dy;
      const secondary = direction === "left" || direction === "right" ? Math.abs(dy) : Math.abs(dx);
      if (primary <= 8) {
        return;
      }
      const score = primary * 1.25 + secondary;
      if (score < bestScore) {
        bestScore = score;
        best = index;
      }
    });

    if (best === null) {
      moveLinear(direction === "left" || direction === "up" ? -1 : 1);
      return;
    }

    state.focusIndex = best;
    syncFocus();
  }

  function activateFocused() {
    const element = getFocusables()[state.focusIndex];
    if (element) {
      activateElement(element);
    }
  }

  function activateElement(element) {
    if (!element || state.busy) {
      return;
    }
    setFocusToElement(element);
    const action = element.dataset.action || "";
    const storeId = element.dataset.storeId || state.activeStoreId;
    if (action === "close") {
      void closeOverlay();
      return;
    }
    if (action === "tab") {
      state.activeStoreId = storeId;
      render();
      return;
    }
    if (action === "refresh-all") {
      void refreshLibrary("");
      return;
    }
    if (action === "refresh-store") {
      void refreshLibrary(storeId);
      return;
    }
    if (action === "login") {
      void loginStore(storeId);
      return;
    }
    if (action === "toggle-auth-panel") {
      state.authPanelStoreId = state.authPanelStoreId === storeId ? "" : storeId;
      render();
      return;
    }
    if (action === "auth-code") {
      void submitAuthCode(storeId);
      return;
    }
    if (action === "launch") {
      void launchGame(storeId, element.dataset.gameId || "", element.dataset.title || "");
    }
  }

  function isTextInputElement(element) {
    const tagName = element?.tagName?.toLowerCase?.() || "";
    return tagName === "input" ||
      tagName === "textarea" ||
      tagName === "select" ||
      element?.isContentEditable;
  }

  function handleAction(action) {
    if (!state.open || !action) {
      return;
    }
    if (action === "up" || action === "down" || action === "left" || action === "right") {
      moveDirectional(action);
      return;
    }
    if (action === "a") {
      activateFocused();
      return;
    }
    if (action === "b") {
      void closeOverlay();
      return;
    }
    if (action === "previous-section") {
      switchStore(-1);
      return;
    }
    if (action === "next-section") {
      switchStore(1);
      return;
    }
    if (action === "refresh") {
      void refreshLibrary(state.activeStoreId);
    }
  }

  function actionFromKey(event) {
    const key = event.key || event.code || "";
    if (key === "ArrowUp") {
      return "up";
    }
    if (key === "ArrowDown") {
      return "down";
    }
    if (key === "ArrowLeft") {
      return "left";
    }
    if (key === "ArrowRight") {
      return "right";
    }
    if (key === "Enter" || key === " " || key === "Space") {
      return "a";
    }
    if (key === "Escape" || key === "Backspace") {
      return "b";
    }
    if (key === "PageUp" || key === "[") {
      return "previous-section";
    }
    if (key === "PageDown" || key === "]") {
      return "next-section";
    }
    if (key.toLowerCase?.() === "x") {
      return "refresh";
    }
    return "";
  }

  function pollGamepads() {
    if (!state.open || typeof navigator.getGamepads !== "function") {
      return;
    }
    const pads = Array.from(navigator.getGamepads()).filter(Boolean);
    pads.forEach((pad, padIndex) => {
      const buttons = [
        [0, "a"],
        [1, "b"],
        [2, "refresh"],
        [4, "previous-section"],
        [5, "next-section"],
        [12, "up"],
        [13, "down"],
        [14, "left"],
        [15, "right"],
      ];
      buttons.forEach(([buttonIndex, action]) => {
        const pressed = Boolean(pad.buttons?.[buttonIndex]?.pressed);
        maybeHandleGamepadAction(`b-${padIndex}-${buttonIndex}`, pressed, action);
      });

      const axisX = Number(pad.axes?.[0] || 0);
      const axisY = Number(pad.axes?.[1] || 0);
      maybeHandleGamepadAction(`ax-${padIndex}-left`, axisX < -0.62, "left");
      maybeHandleGamepadAction(`ax-${padIndex}-right`, axisX > 0.62, "right");
      maybeHandleGamepadAction(`ay-${padIndex}-up`, axisY < -0.62, "up");
      maybeHandleGamepadAction(`ay-${padIndex}-down`, axisY > 0.62, "down");
    });
  }

  function maybeHandleGamepadAction(key, active, action) {
    const now = Date.now();
    const last = state.buttonState[key] || { active: false, at: 0 };
    const repeatMs = action === "up" || action === "down" || action === "left" || action === "right"
      ? 210
      : 360;
    if (!active) {
      state.buttonState[key] = { active: false, at: 0 };
      return;
    }
    if (!last.active || now - last.at >= repeatMs) {
      state.buttonState[key] = { active: true, at: now };
      handleAction(action, "gamepad");
    }
  }

  install();
})();
