(() => {
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";
  const stateVersion = 22;
  const inputStorageKey = "ToolsForSteamPluginStoreInput";
  const overlayStateStorageKey = "ToolsForSteamPluginStoreOverlayState";
  const channelName = "ToolsForSteamPluginStoreChannel";
  const previousState = window.__steamLoaderPluginStoreSurfaceState;

  if (previousState?.version !== stateVersion) {
    if (previousState?.pollTimer) window.clearTimeout(previousState.pollTimer);
    if (previousState?.inputPollTimer) window.clearInterval(previousState.inputPollTimer);
    if (previousState?.announceTimer) window.clearInterval(previousState.announceTimer);
    if (previousState?.keyHandler) document.removeEventListener("keydown", previousState.keyHandler, true);
    if (previousState?.storageHandler) window.removeEventListener("storage", previousState.storageHandler);
    try { previousState?.channel?.close?.(); } catch {}
    document.getElementById("steamloader-plugin-store-root")?.remove();
    document.getElementById("steamloader-plugin-store-style")?.remove();
  }

  const state = previousState?.version === stateVersion
    ? previousState
    : (window.__steamLoaderPluginStoreSurfaceState = {
        version: stateVersion,
        open: false,
        loading: false,
        snapshot: null,
        error: "",
        busyPluginId: "",
        focusKey: "",
        pollTimer: 0,
        inputPollTimer: 0,
        announceTimer: 0,
        lastApiInputNonce: 0,
        lastInputNonce: "",
        lastAction: "",
        lastActionAt: 0,
        ignoreInputUntil: 0,
        channel: null,
        storageHandler: null,
        keyHandler: null,
      });

  function isQuickAccessSurface() {
    const identity = `${document.title || ""} ${window.location?.href || ""}`;
    return Boolean(
      document.getElementById("QuickAccess-NA") ||
      document.querySelector("[id^='QuickAccess']") ||
      /quick[\s_-]*access/i.test(identity)
    );
  }

  function canHostPluginActivator() {
    return Boolean(
      document.body &&
      document.visibilityState !== "hidden" &&
      !isQuickAccessSurface() &&
      window.innerWidth >= 900 &&
      window.innerHeight >= 500
    );
  }

  function ensureStyle() {
    let style = document.getElementById("steamloader-plugin-store-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "steamloader-plugin-store-style";
      document.head.append(style);
    }
    style.textContent = `
      .steamloader-plugin-store-root {
        position: absolute; inset: 0; z-index: 2147483640; display: none;
        color: #f3f7fb; font-family: "Motiva Sans", "Segoe UI", sans-serif;
      }
      .steamloader-plugin-store-root.is-open { display: block; }
      .steamloader-plugin-activator {
        position: absolute; inset: 0; display: grid;
        grid-template-rows: auto minmax(0, 1fr); overflow: hidden;
        background: radial-gradient(circle at 10% -10%, rgba(102,192,244,.22), transparent 34%),
          radial-gradient(circle at 94% 12%, rgba(64,103,135,.32), transparent 38%),
          linear-gradient(180deg, #0e151d 0%, #101923 48%, #0b1118 100%);
      }
      .steamloader-plugin-activator-header {
        display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: end; gap: 24px;
        padding: clamp(24px,3.2vw,54px) clamp(30px,4.2vw,72px) 22px;
        border-bottom: 1px solid rgba(102,192,244,.16);
      }
      .steamloader-plugin-activator-kicker {
        margin-bottom: 8px; color: #66c0f4; font-size: 13px; font-weight: 900;
        letter-spacing: .2em; text-transform: uppercase;
      }
      .steamloader-plugin-activator-title {
        margin: 0; color: #fff; font-size: clamp(42px,5vw,78px); font-weight: 950;
        letter-spacing: -.065em; line-height: .92;
      }
      .steamloader-plugin-activator-subtitle {
        max-width: 780px; margin-top: 14px; color: rgba(213,225,235,.76);
        font-size: clamp(15px,1.3vw,20px); line-height: 1.42;
      }
      .steamloader-plugin-activator-summary { display: flex; align-items: center; gap: 12px; }
      .steamloader-plugin-activator-count {
        padding: 10px 15px; border: 1px solid rgba(102,192,244,.22); border-radius: 999px;
        color: #9dd8fb; background: rgba(102,192,244,.09); font-size: 13px; font-weight: 900;
        white-space: nowrap;
      }
      .steamloader-plugin-activator-button {
        min-height: 44px; padding: 0 18px; border: 0; border-radius: 11px; color: #f5fbff;
        background: rgba(255,255,255,.1); font: inherit; font-size: 14px; font-weight: 900; cursor: pointer;
      }
      .steamloader-plugin-activator-button:hover,
      .steamloader-plugin-activator-button:focus-visible,
      .steamloader-plugin-activator-button.is-controller-focus,
      .steamloader-plugin-row-toggle:focus-visible,
      .steamloader-plugin-row-toggle.is-controller-focus {
        outline: none; box-shadow: inset 0 0 0 2px #8ed2fa, 0 0 0 3px rgba(102,192,244,.18);
      }
      .steamloader-plugin-activator-content {
        min-height: 0; overflow: auto; padding: 22px clamp(30px,4.2vw,72px) 92px;
        scrollbar-color: rgba(102,192,244,.55) transparent;
      }
      .steamloader-plugin-activator-status {
        min-height: 24px; max-width: 1280px; margin: 0 auto 12px;
        color: rgba(208,221,232,.68); font-size: 13px; font-weight: 800;
      }
      .steamloader-plugin-activator-status.is-error { color: #ff9d9d; }
      .steamloader-plugin-list { display: grid; gap: 12px; max-width: 1280px; margin: 0 auto; }
      .steamloader-plugin-row {
        display: grid; grid-template-columns: clamp(126px,13vw,210px) minmax(0,1fr) auto;
        min-height: 118px; overflow: hidden; border: 1px solid rgba(255,255,255,.075);
        border-radius: 17px; background: linear-gradient(100deg, rgba(30,44,58,.96), rgba(18,29,40,.94));
        box-shadow: 0 16px 34px rgba(0,0,0,.18);
      }
      .steamloader-plugin-row.is-inactive { opacity: .72; }
      .steamloader-plugin-row-art {
        position: relative; min-height: 118px; overflow: hidden;
        background: linear-gradient(135deg, rgba(102,192,244,.2), rgba(22,39,54,.92));
      }
      .steamloader-plugin-row-art img { width: 100%; height: 100%; object-fit: cover; display: block; }
      .steamloader-plugin-row-fallback {
        position: absolute; inset: 0; display: grid; place-items: center;
        color: rgba(225,242,252,.9); font-size: 34px; font-weight: 950; letter-spacing: -.04em;
      }
      .steamloader-plugin-row-copy {
        display: flex; min-width: 0; flex-direction: column; justify-content: center; gap: 7px; padding: 18px 24px;
      }
      .steamloader-plugin-row-heading { display: flex; align-items: center; gap: 10px; }
      .steamloader-plugin-row-title {
        overflow: hidden; color: #fff; font-size: clamp(19px,1.55vw,25px); font-weight: 950;
        text-overflow: ellipsis; white-space: nowrap;
      }
      .steamloader-plugin-row-state {
        padding: 4px 8px; border-radius: 999px; color: #6ce5b1; background: rgba(40,194,132,.12);
        font-size: 11px; font-weight: 950; letter-spacing: .08em; text-transform: uppercase;
      }
      .steamloader-plugin-row.is-inactive .steamloader-plugin-row-state {
        color: rgba(219,228,235,.68); background: rgba(255,255,255,.07);
      }
      .steamloader-plugin-row-description {
        max-width: 800px; color: rgba(211,223,232,.72); font-size: clamp(13px,1.1vw,16px); line-height: 1.4;
      }
      .steamloader-plugin-row-action { display: flex; align-items: center; padding: 18px 24px 18px 12px; }
      .steamloader-plugin-row-toggle {
        display: inline-flex; align-items: center; gap: 11px; min-width: 148px; min-height: 50px;
        justify-content: center; border: 0; border-radius: 13px; color: #062118;
        background: linear-gradient(135deg,#68e0ae,#3bb88a); font: inherit; font-size: 14px;
        font-weight: 950; cursor: pointer;
      }
      .steamloader-plugin-row.is-inactive .steamloader-plugin-row-toggle {
        color: #e8f4fb; background: rgba(102,192,244,.14);
      }
      .steamloader-plugin-row-toggle:disabled { cursor: default; opacity: .55; }
      .steamloader-plugin-row-toggle-track {
        position: relative; width: 34px; height: 19px; border-radius: 999px; background: rgba(4,26,18,.26);
      }
      .steamloader-plugin-row-toggle-track::after {
        content: ""; position: absolute; top: 3px; right: 3px; width: 13px; height: 13px;
        border-radius: 50%; background: currentColor;
      }
      .steamloader-plugin-row.is-inactive .steamloader-plugin-row-toggle-track::after { right: 18px; }
      .steamloader-plugin-activator-empty {
        display: grid; min-height: 280px; place-items: center; gap: 18px; padding: 38px;
        border: 1px dashed rgba(102,192,244,.24); border-radius: 18px;
        color: rgba(214,226,235,.7); text-align: center;
      }
      .steamloader-plugin-activator-footer {
        position: absolute; right: clamp(30px,4.2vw,72px); bottom: 24px; display: flex; gap: 18px;
        padding: 10px 14px; border: 1px solid rgba(255,255,255,.08); border-radius: 12px;
        background: rgba(8,14,20,.82); color: rgba(221,232,240,.78); font-size: 12px;
        font-weight: 850; backdrop-filter: blur(12px);
      }
      @media (max-width: 1080px), (max-height: 720px) {
        .steamloader-plugin-activator-header { padding-top: 20px; padding-bottom: 16px; }
        .steamloader-plugin-activator-title { font-size: 44px; }
        .steamloader-plugin-activator-subtitle { margin-top: 8px; font-size: 14px; }
        .steamloader-plugin-activator-content { padding-top: 14px; }
        .steamloader-plugin-row { min-height: 96px; grid-template-columns: 132px minmax(0,1fr) auto; }
        .steamloader-plugin-row-art { min-height: 96px; }
      }
    `;
  }

  function ensureRoot() {
    let root = document.getElementById("steamloader-plugin-store-root");
    if (!root) {
      root = document.createElement("div");
      root.id = "steamloader-plugin-store-root";
      root.className = "steamloader-plugin-store-root";
      document.body.append(root);
    }
    return root;
  }

  function node(tag, className = "", text = "") {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text) element.textContent = text;
    return element;
  }

  function requestJson(path, method = "GET", body = undefined) {
    return fetch(`${apiBase}${path}`, {
      method, cache: "no-store",
      headers: body === undefined ? undefined : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    }).then(async (response) => {
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(payload?.message || `Request failed (${response.status}).`);
      return payload;
    });
  }

  function getPlugins() {
    return Array.isArray(state.snapshot?.builtInPlugins) ? state.snapshot.builtInPlugins : [];
  }

  function getFocusables() {
    return Array.from(ensureRoot().querySelectorAll("[data-plugin-activator-focus]"))
      .filter((element) => element instanceof HTMLElement && !element.disabled);
  }

  function setFocus(element) {
    if (!(element instanceof HTMLElement)) return false;
    for (const item of getFocusables()) item.classList.remove("is-controller-focus");
    element.classList.add("is-controller-focus");
    state.focusKey = element.dataset.pluginActivatorFocus || "";
    element.focus({ preventScroll: true });
    element.scrollIntoView({ block: "nearest", inline: "nearest" });
    return true;
  }

  function restoreFocus() {
    const items = getFocusables();
    if (!items.length) return;
    setFocus(items.find((item) => item.dataset.pluginActivatorFocus === state.focusKey) || items[0]);
  }

  function decorateFocus(element, key) {
    element.dataset.pluginActivatorFocus = key;
    element.addEventListener("focus", () => {
      state.focusKey = key;
      for (const item of getFocusables()) item.classList.toggle("is-controller-focus", item === element);
    });
  }

  function moveFocus(direction) {
    const items = getFocusables();
    if (!items.length) return false;
    let index = items.findIndex((item) => item === document.activeElement || item.classList.contains("is-controller-focus"));
    if (index < 0) index = 0;
    const delta = direction === "up" || direction === "left" ? -1 : 1;
    return setFocus(items[(index + delta + items.length) % items.length]);
  }

  function initials(title) {
    const parts = String(title || "Plugin").trim().split(/\s+/).filter(Boolean);
    return parts.slice(0, 2).map((part) => part[0]?.toUpperCase() || "").join("") || "P";
  }

  function buildPluginRow(plugin) {
    const active = Boolean(plugin?.isEnabled);
    const canToggle = Boolean(plugin?.canToggleVisibility);
    const row = node("article", `steamloader-plugin-row${active ? "" : " is-inactive"}`);
    const art = node("div", "steamloader-plugin-row-art");
    const fallback = node("div", "steamloader-plugin-row-fallback", initials(plugin?.title));
    art.append(fallback);
    const imageUrl = plugin?.images?.[0]?.url;
    if (imageUrl) {
      const image = document.createElement("img");
      image.alt = plugin?.images?.[0]?.altText || `${plugin?.title || "Plugin"} preview`;
      image.loading = "lazy";
      image.src = String(imageUrl).startsWith("http")
        ? imageUrl
        : `${apiBase}${String(imageUrl).replace(/^\/+/, "")}`;
      image.addEventListener("load", () => fallback.remove());
      image.addEventListener("error", () => image.remove());
      art.append(image);
    }
    const copy = node("div", "steamloader-plugin-row-copy");
    const heading = node("div", "steamloader-plugin-row-heading");
    heading.append(
      node("div", "steamloader-plugin-row-title", plugin?.title || plugin?.id || "Plugin"),
      node("span", "steamloader-plugin-row-state", canToggle ? (active ? "Active" : "Inactive") : "Required")
    );
    copy.append(heading, node("div", "steamloader-plugin-row-description", plugin?.description || "No description available."));
    const action = node("div", "steamloader-plugin-row-action");
    const toggle = node(
      "button", "steamloader-plugin-row-toggle",
      state.busyPluginId === plugin?.id ? "Saving..." : canToggle ? (active ? "Deactivate" : "Activate") : "Always active"
    );
    toggle.type = "button";
    toggle.disabled = !canToggle || Boolean(state.busyPluginId);
    toggle.setAttribute("role", "switch");
    toggle.setAttribute("aria-checked", String(active));
    toggle.setAttribute("aria-label", `${active ? "Deactivate" : "Activate"} ${plugin?.title || "plugin"}`);
    const track = node("span", "steamloader-plugin-row-toggle-track");
    track.setAttribute("aria-hidden", "true");
    toggle.prepend(track);
    if (!toggle.disabled) decorateFocus(toggle, `plugin:${plugin.id}`);
    toggle.addEventListener("click", () => void togglePlugin(plugin));
    action.append(toggle);
    row.append(art, copy, action);
    return row;
  }

  async function loadSnapshot(showLoading = true) {
    if (state.loading) return;
    state.loading = true;
    state.error = "";
    if (showLoading) render();
    try {
      state.snapshot = await requestJson("api/plugin-activator/state");
      if (!state.focusKey) {
        const first = getPlugins().find((plugin) => plugin?.canToggleVisibility);
        state.focusKey = first ? `plugin:${first.id}` : "close";
      }
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.loading = false;
      render();
    }
  }

  async function togglePlugin(plugin) {
    if (!plugin?.id || !plugin.canToggleVisibility || state.busyPluginId) return;
    state.busyPluginId = plugin.id;
    state.error = "";
    state.focusKey = `plugin:${plugin.id}`;
    render();
    try {
      await requestJson("api/settings/plugins/enabled", "POST", {
        pluginId: plugin.id,
        enabled: !Boolean(plugin.isEnabled),
      });
      state.snapshot = await requestJson("api/plugin-activator/state");
      try { window.__steamLoaderPluginStoreOverlayBridge?.refreshSettings?.(); } catch {}
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.busyPluginId = "";
      render();
    }
  }

  function render() {
    ensureStyle();
    const root = ensureRoot();
    root.classList.toggle("is-open", state.open);
    root.replaceChildren();
    if (!state.open) return;
    const surface = node("main", "steamloader-plugin-activator");
    const header = node("header", "steamloader-plugin-activator-header");
    const brand = node("div");
    brand.append(
      node("div", "steamloader-plugin-activator-kicker", "Tools for Steam"),
      node("h1", "steamloader-plugin-activator-title", "Plugin Activator"),
      node("div", "steamloader-plugin-activator-subtitle", "Activate only the included plugins you want to use. There is no marketplace, community catalog, or third-party installation here.")
    );
    const summary = node("div", "steamloader-plugin-activator-summary");
    const plugins = getPlugins();
    summary.append(node("span", "steamloader-plugin-activator-count", `${plugins.filter((plugin) => plugin?.isEnabled).length} / ${plugins.length} active`));
    const closeButton = node("button", "steamloader-plugin-activator-button", "Close");
    closeButton.type = "button";
    closeButton.addEventListener("click", () => void closeOverlay());
    decorateFocus(closeButton, "close");
    summary.append(closeButton);
    header.append(brand, summary);
    const content = node("section", "steamloader-plugin-activator-content");
    content.append(node(
      "div",
      `steamloader-plugin-activator-status${state.error ? " is-error" : ""}`,
      state.error || (state.loading ? "Loading plugins..." : state.snapshot?.statusText || "")
    ));
    const list = node("div", "steamloader-plugin-list");
    if (plugins.length) {
      for (const plugin of plugins) list.append(buildPluginRow(plugin));
    } else {
      const empty = node("div", "steamloader-plugin-activator-empty", state.loading ? "Loading included plugins..." : "No included plugins are available.");
      if (state.error) {
        const retry = node("button", "steamloader-plugin-activator-button", "Try again");
        retry.type = "button";
        retry.addEventListener("click", () => void loadSnapshot());
        decorateFocus(retry, "retry");
        empty.append(retry);
      }
      list.append(empty);
    }
    content.append(list);
    const footer = node("div", "steamloader-plugin-activator-footer");
    footer.append(node("span", "", "A  Activate / Deactivate"), node("span", "", "B  Close"));
    surface.append(header, content, footer);
    root.append(surface);
    window.requestAnimationFrame(restoreFocus);
  }

  function getChannel() {
    if (state.channel || typeof BroadcastChannel !== "function") return state.channel;
    try {
      state.channel = new BroadcastChannel(channelName);
      state.channel.addEventListener("message", (event) => consumeInput(event.data));
    } catch { state.channel = null; }
    return state.channel;
  }

  function announceOverlay(active) {
    const payload = {
      type: "overlay-state", active: Boolean(active), source: "plugin-store",
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      expiresAt: Date.now() + 1800,
    };
    try { getChannel()?.postMessage(payload); } catch {}
    try { localStorage.setItem(overlayStateStorageKey, JSON.stringify(payload)); } catch {}
  }

  function startAnnouncements() {
    stopAnnouncements(false);
    announceOverlay(true);
    state.announceTimer = window.setInterval(() => announceOverlay(true), 800);
  }

  function stopAnnouncements(announceClosed = true) {
    const wasAnnouncing = Boolean(state.announceTimer);
    if (state.announceTimer) window.clearInterval(state.announceTimer);
    state.announceTimer = 0;
    if (announceClosed && wasAnnouncing) announceOverlay(false);
  }

  async function closeOverlay() {
    state.open = false;
    state.ignoreInputUntil = Date.now() + 280;
    stopAnnouncements();
    render();
    try { await requestJson("api/plugin-activator/overlay/close", "POST", {}); } catch {}
  }

  function handleAction(action) {
    if (!state.open || Date.now() < state.ignoreInputUntil) return;
    const normalized = String(action || "").toLowerCase();
    const now = Date.now();
    if (normalized === state.lastAction && now - state.lastActionAt < 220) return;
    state.lastAction = normalized;
    state.lastActionAt = now;
    if (["up", "left", "down", "right"].includes(normalized)) moveFocus(normalized);
    else if (normalized === "a") {
      getFocusables().find((item) => item === document.activeElement || item.classList.contains("is-controller-focus"))?.click?.();
    } else if (normalized === "b") void closeOverlay();
  }

  function consumeInput(raw) {
    if (!raw || !state.open) return;
    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type !== "input" || !payload.action || payload.nonce === state.lastInputNonce) return;
      state.lastInputNonce = payload.nonce;
      handleAction(payload.action);
    } catch {}
  }

  async function pollApiInput() {
    if (!state.open) return;
    try {
      const batch = await requestJson(`api/plugin-activator/overlay/input?after=${state.lastApiInputNonce}`);
      state.lastApiInputNonce = Number(batch?.latestNonce) || state.lastApiInputNonce;
      for (const input of Array.isArray(batch?.inputs) ? batch.inputs : []) handleAction(input?.action);
    } catch {}
  }

  function setupInput() {
    getChannel();
    if (!state.storageHandler) {
      state.storageHandler = (event) => { if (event.key === inputStorageKey) consumeInput(event.newValue); };
      window.addEventListener("storage", state.storageHandler);
    }
    if (!state.inputPollTimer) {
      state.inputPollTimer = window.setInterval(() => {
        if (!state.open) return;
        try { consumeInput(localStorage.getItem(inputStorageKey)); } catch {}
        void pollApiInput();
      }, 120);
    }
    if (!state.keyHandler) {
      state.keyHandler = (event) => {
        if (!state.open || event.type !== "keydown" || event.repeat) return;
        const mapping = {
          ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right",
          Enter: "a", " ": "a", Space: "a", GamepadA: "a",
          Escape: "b", Backspace: "b", GamepadB: "b",
        };
        const action = mapping[event.key || event.code];
        if (!action) return;
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation?.();
        handleAction(action);
      };
      document.addEventListener("keydown", state.keyHandler, true);
    }
  }

  async function syncOverlayState() {
    try {
      const overlay = await requestJson("api/plugin-activator/overlay/state");
      const shouldOpen = Boolean(overlay?.isOpen);
      if (shouldOpen && canHostPluginActivator()) {
        if (!state.open) {
          state.open = true;
          state.ignoreInputUntil = Date.now() + 360;
          startAnnouncements();
          render();
          await loadSnapshot();
        } else if (!state.snapshot && !state.loading) {
          await loadSnapshot(false);
        }
      } else if (!shouldOpen && state.open) {
        state.open = false;
        stopAnnouncements();
        render();
      }
    } catch {
    } finally {
      if (state.pollTimer) window.clearTimeout(state.pollTimer);
      state.pollTimer = window.setTimeout(syncOverlayState, state.open ? 1800 : 700);
    }
  }

  ensureStyle();
  setupInput();
  void syncOverlayState();
})();
