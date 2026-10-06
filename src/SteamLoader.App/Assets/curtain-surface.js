(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const version = 1;
  const overlayId = "steamtools-curtain-instant";
  const styleId = "steamtools-curtain-style";
  const playLabels = new Set([
    "play",
    "spielen",
    "start",
    "launch",
    "resume",
    "fortsetzen",
  ]);

  const existing = window.ToolsForSteamCurtain;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  existing?.destroy?.();

  const state = {
    snapshot: null,
    stateTimer: 0,
    hideTimer: 0,
    gamepadFrame: 0,
    launchKey: "",
    launchAt: 0,
    nativeActive: false,
    registrations: [],
    previousButtons: new Set(),
  };

  function normalizeAppId(value) {
    const numeric = Number(value || 0);
    if (!Number.isFinite(numeric)) return 0;
    const appId = Math.trunc(numeric);
    return appId < 0 ? appId >>> 0 : appId;
  }

  function disabledAppIds() {
    return new Set((state.snapshot?.disabledAppIds || []).map(normalizeAppId));
  }

  function isGameEnabled(appId) {
    const normalized = normalizeAppId(appId);
    return Boolean(state.snapshot?.enabled && normalized && !disabledAppIds().has(normalized));
  }

  async function loadState() {
    try {
      const response = await fetch(`${apiBase}api/curtain/state`, { cache: "no-store" });
      const snapshot = await response.json().catch(() => null);
      if (response.ok && snapshot) {
        state.snapshot = snapshot;
        state.nativeActive = snapshot.active === true;
        if (!state.nativeActive) stopGamepadDismissPolling();
        return snapshot;
      }
    } catch {
    }
    state.snapshot = { enabled: false, disabledAppIds: [] };
    state.nativeActive = false;
    stopGamepadDismissPolling();
    return state.snapshot;
  }

  function injectStyles() {
    const current = document.getElementById(styleId);
    if (current?.dataset.tfsCurtainVersion === String(version)) return;
    current?.remove();
    const style = document.createElement("style");
    style.id = styleId;
    style.dataset.tfsCurtainVersion = String(version);
    style.textContent = `
      #${overlayId} {
        position: fixed;
        inset: 0;
        z-index: 2147483647;
        display: grid;
        grid-template-rows: auto 1fr auto;
        box-sizing: border-box;
        padding: 42px 54px;
        overflow: hidden;
        color: #f5f8fb;
        background: #050a11;
        font-family: "Motiva Sans", Arial, sans-serif;
        opacity: 0;
        pointer-events: none;
        transition: opacity 150ms ease-out;
      }
      #${overlayId}.is-visible { opacity: 1; }
      #${overlayId}.is-closing { opacity: 0; transition-duration: 320ms; }
      #${overlayId} .tfs-curtain-backdrop {
        position: absolute;
        inset: -3%;
        background-position: center;
        background-size: cover;
        opacity: .62;
        transform: scale(1.01);
        animation: tfs-curtain-zoom 18s ease-in-out infinite alternate;
      }
      #${overlayId} .tfs-curtain-shade {
        position: absolute;
        inset: 0;
        background:
          radial-gradient(circle at 50% 44%, rgba(5,10,18,.08), rgba(3,7,13,.78) 74%),
          linear-gradient(180deg, rgba(3,9,16,.38), rgba(4,10,18,.9));
      }
      #${overlayId} .tfs-curtain-brand,
      #${overlayId} .tfs-curtain-title,
      #${overlayId} .tfs-curtain-status { position: relative; z-index: 1; }
      #${overlayId} .tfs-curtain-brand {
        display: flex;
        align-items: center;
        gap: 16px;
        color: #7ad8fa;
        font-size: 18px;
        font-weight: 800;
        letter-spacing: .16em;
      }
      #${overlayId} .tfs-curtain-mark {
        display: grid;
        width: 42px;
        height: 42px;
        place-items: center;
        border-radius: 12px;
        color: #071a27;
        background: #5bcdf5;
        font-size: 14px;
        letter-spacing: 0;
      }
      #${overlayId} .tfs-curtain-title {
        align-self: center;
        justify-self: center;
        max-width: min(1080px, 84vw);
        text-align: center;
        font-size: clamp(42px, 5.2vw, 76px);
        font-weight: 900;
        line-height: 1.02;
        text-shadow: 0 8px 32px rgba(0,0,0,.9);
      }
      #${overlayId} .tfs-curtain-status {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 12px;
        color: rgba(230,238,246,.9);
        font-size: 16px;
        font-weight: 700;
        letter-spacing: .12em;
      }
      #${overlayId} .tfs-curtain-status::before {
        content: "";
        width: 9px;
        height: 9px;
        border-radius: 50%;
        background: #5ee6a8;
        box-shadow: 0 0 18px rgba(94,230,168,.72);
        animation: tfs-curtain-pulse 900ms ease-in-out infinite alternate;
      }
      @keyframes tfs-curtain-zoom { to { transform: scale(1.055); } }
      @keyframes tfs-curtain-pulse { to { opacity: .35; } }
      @media (prefers-reduced-motion: reduce) {
        #${overlayId} .tfs-curtain-backdrop,
        #${overlayId} .tfs-curtain-status::before { animation: none; }
      }
    `;
    document.head.appendChild(style);
  }

  function escapeText(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function findBackdrop(appId) {
    let best = null;
    let bestArea = 0;
    for (const element of document.querySelectorAll("img, [style]")) {
      const source = element instanceof HTMLImageElement
        ? element.currentSrc || element.src
        : getComputedStyle(element).backgroundImage;
      if (!source || !String(source).includes(String(appId))) continue;
      const rect = element.getBoundingClientRect();
      const area = rect.width * rect.height;
      if (area <= bestArea) continue;
      bestArea = area;
      best = element instanceof HTMLImageElement
        ? `url("${source.replace(/"/g, "%22")}")`
        : source;
    }
    return best || "";
  }

  function showInstant(appId, title) {
    injectStyles();
    document.getElementById(overlayId)?.remove();
    const overlay = document.createElement("div");
    overlay.id = overlayId;
    overlay.setAttribute("role", "status");
    overlay.setAttribute("aria-live", "polite");
    const backdrop = findBackdrop(appId);
    overlay.innerHTML = `
      <div class="tfs-curtain-backdrop"></div>
      <div class="tfs-curtain-shade"></div>
      <div class="tfs-curtain-brand"><span class="tfs-curtain-mark">TFS</span><span>TFS CURTAIN</span></div>
      <div class="tfs-curtain-title">${escapeText(title || "Starting game")}</div>
      <div class="tfs-curtain-status">PREPARING GAME</div>
    `;
    if (backdrop) overlay.querySelector(".tfs-curtain-backdrop").style.backgroundImage = backdrop;
    document.body.appendChild(overlay);
    overlay.getBoundingClientRect();
    requestAnimationFrame(() => overlay.classList.add("is-visible"));
  }

  function hideInstant() {
    window.clearTimeout(state.hideTimer);
    state.hideTimer = 0;
    const overlay = document.getElementById(overlayId);
    if (!overlay) return;
    overlay.classList.add("is-closing");
    window.setTimeout(() => overlay.remove(), 340);
  }

  function showToast(message, enabled) {
    document.getElementById("steamtools-curtain-toast")?.remove();
    const toast = document.createElement("div");
    toast.id = "steamtools-curtain-toast";
    toast.textContent = message;
    toast.style.cssText = [
      "position:fixed", "z-index:2147483647", "right:34px", "bottom:92px",
      "padding:14px 18px", "border-radius:12px", "font:700 14px Motiva Sans,Arial,sans-serif",
      "color:#f4f8fb", "background:rgba(11,20,30,.96)",
      `border:1px solid ${enabled ? "rgba(94,230,168,.56)" : "rgba(255,159,67,.56)"}`,
      "box-shadow:0 14px 38px rgba(0,0,0,.48)",
    ].join(";");
    document.body.appendChild(toast);
    window.setTimeout(() => toast.remove(), 2600);
  }

  function normalizeLabel(value) {
    return String(value || "").toLowerCase().replace(/[^\p{L}\p{N}\s]/gu, "").replace(/\s+/g, " ").trim();
  }

  function isPlayButton(element) {
    if (!(element instanceof HTMLElement)) return false;
    if (element.tagName === "A" || normalizeLabel(element.getAttribute("role")) === "link") return false;
    const context = normalizeLabel(`${element.id} ${element.className} ${element.getAttribute("aria-label") || ""}`);
    if (/(trailer|video|preview|media)/i.test(context)) return false;
    const labels = [element.innerText, element.textContent, element.getAttribute("aria-label"), element.title];
    return labels.some((label) => playLabels.has(normalizeLabel(label)));
  }

  function findPlayButton(target, path) {
    const candidates = (path?.length ? path : []).filter((item) => item instanceof HTMLElement);
    let current = target instanceof HTMLElement ? target : null;
    while (current && candidates.length < 12) {
      if (!candidates.includes(current)) candidates.push(current);
      current = current.parentElement;
    }
    return candidates.find((element) =>
      (element.tagName === "BUTTON" || element.getAttribute("role") === "button" || element.dataset.focusable === "true") &&
      isPlayButton(element));
  }

  function appIdFromUnknown(value, depth = 0, seen = new Set()) {
    if (value == null || depth > 5) return 0;
    if (typeof value === "number" || typeof value === "string") {
      const text = String(value);
      if (/^-?\d{2,20}$/.test(text)) return normalizeAppId(text);
      const match = text.match(/(?:library\/app|games\/details|rungameid|steam\/apps|appid["'=:\s]+)\/?(-?\d{2,20})/i);
      return match ? normalizeAppId(match[1]) : 0;
    }
    if ((typeof value !== "object" && typeof value !== "function") || seen.has(value)) return 0;
    seen.add(value);
    for (const key of ["appid", "appId", "unAppID", "m_unAppID", "nAppID"]) {
      const direct = normalizeAppId(value?.[key]);
      if (direct) return direct;
    }
    for (const key of Object.keys(value).slice(0, 40)) {
      if (["return", "child", "sibling", "stateNode", "_owner"].includes(key)) continue;
      let nested = 0;
      try { nested = appIdFromUnknown(value[key], depth + 1, seen); } catch {}
      if (nested) return nested;
    }
    return 0;
  }

  function resolveAppId(element) {
    const routeMatch = `${location.pathname}${location.hash}`.match(/(?:library\/app|games\/details|appdetails)\/?(-?\d{2,20})/i);
    if (routeMatch) return normalizeAppId(routeMatch[1]);
    for (let current = element; current instanceof HTMLElement; current = current.parentElement) {
      for (const key of Object.getOwnPropertyNames(current).filter((name) => name.startsWith("__react"))) {
        const appId = appIdFromUnknown(current[key]);
        if (appId) return appId;
      }
      for (const value of [current.dataset.appid, current.dataset.appId, current.getAttribute("href"), current.getAttribute("src")]) {
        const appId = appIdFromUnknown(value);
        if (appId) return appId;
      }
    }
    return 0;
  }

  function resolveTitle() {
    const candidates = [
      document.querySelector("[data-steamtools-artwork-title]"),
      document.querySelector("h1"),
      document.querySelector("[class*='GameName']"),
      document.querySelector("[class*='appTitle']"),
    ];
    return candidates.map((item) => String(item?.textContent || "").trim()).find(Boolean) || "Starting game";
  }

  async function requestLaunch(appId, title, reason) {
    if (!isGameEnabled(appId)) return false;
    const now = Date.now();
    const key = `${appId}:${title}`;
    if (state.launchKey === key && now - state.launchAt < 1600) return false;
    state.launchKey = key;
    state.launchAt = now;
    showInstant(appId, title);
    try {
      const response = await fetch(`${apiBase}api/curtain/launch`, {
        method: "POST",
        cache: "no-store",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ appId, title, reason }),
      });
      const payload = await response.json().catch(() => null);
      if (!response.ok || payload?.shown !== true) {
        hideInstant();
        await loadState();
        return false;
      }
      state.snapshot = payload.state || state.snapshot;
      state.nativeActive = true;
      startGamepadDismissPolling();
      state.hideTimer = window.setTimeout(hideInstant, 650);
      return true;
    } catch (error) {
      console.warn("[Tools for Steam] TFS Curtain launch failed.", error);
      hideInstant();
      return false;
    }
  }

  function handleLaunchEvent(event) {
    const path = typeof event.composedPath === "function" ? event.composedPath() : [];
    const button = findPlayButton(event.target, path);
    if (!button) return;
    const appId = resolveAppId(button);
    if (!appId) return;
    void requestLaunch(appId, resolveTitle(), event.type);
  }

  async function dismiss() {
    hideInstant();
    state.nativeActive = false;
    stopGamepadDismissPolling();
    try {
      const response = await fetch(`${apiBase}api/curtain/dismiss`, { method: "POST" });
      if (response.ok) state.snapshot = await response.json().catch(() => state.snapshot);
    } catch {
    }
  }

  function startGamepadDismissPolling() {
    if (state.gamepadFrame) return;
    const poll = () => {
      if (!state.nativeActive) {
        state.gamepadFrame = 0;
        return;
      }
      const pressed = new Set();
      for (const pad of navigator.getGamepads?.() || []) {
        pad?.buttons?.forEach((button, index) => { if (button?.pressed) pressed.add(index); });
      }
      if (pressed.has(1) && !state.previousButtons.has(1)) void dismiss();
      state.previousButtons = pressed;
      state.gamepadFrame = requestAnimationFrame(poll);
    };
    state.gamepadFrame = requestAnimationFrame(poll);
  }

  function stopGamepadDismissPolling() {
    if (state.gamepadFrame) cancelAnimationFrame(state.gamepadFrame);
    state.gamepadFrame = 0;
    state.previousButtons.clear();
  }

  async function toggleGame(appId, enabled, title = "Selected game") {
    const normalized = normalizeAppId(appId);
    if (!normalized) return false;
    try {
      const response = await fetch(`${apiBase}api/curtain/game-enabled`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ appId: normalized, enabled: Boolean(enabled) }),
      });
      const snapshot = await response.json().catch(() => null);
      if (!response.ok || !snapshot) return false;
      state.snapshot = snapshot;
      showToast(`TFS Curtain ${enabled ? "enabled" : "disabled"} for ${title}.`, enabled);
      window.setTimeout(() => window.ToolsForSteamArtwork?.refresh?.(), 0);
      return true;
    } catch {
      return false;
    }
  }

  function releaseRegistration(registration) {
    try {
      if (typeof registration === "function") registration();
      else registration?.Unregister?.();
      registration?.unregister?.();
      registration?.Dispose?.();
    } catch {
    }
  }

  function registerSteamLaunchEvents() {
    const apps = window.SteamClient?.Apps;
    if (!apps) return;
    const handle = (...args) => {
      const appId = appIdFromUnknown(args);
      if (appId) void requestLaunch(appId, resolveTitle(), "Steam GameAction");
    };
    try {
      if (typeof apps.RegisterForGameActionStart === "function") {
        state.registrations.push(apps.RegisterForGameActionStart(handle));
      }
    } catch {
    }
  }

  function handleKeyDown(event) {
    if ((event.key === "Escape" || event.key === "BrowserBack") && state.nativeActive) {
      event.preventDefault();
      event.stopPropagation();
      void dismiss();
      return;
    }
    if ((event.key === "Enter" || event.key === " ") && findPlayButton(event.target, event.composedPath?.() || [])) {
      handleLaunchEvent(event);
    }
  }

  function refresh() {
    void loadState();
  }

  function destroy() {
    document.removeEventListener("pointerdown", handleLaunchEvent, true);
    document.removeEventListener("mousedown", handleLaunchEvent, true);
    document.removeEventListener("click", handleLaunchEvent, true);
    document.removeEventListener("keydown", handleKeyDown, true);
    window.clearInterval(state.stateTimer);
    window.clearTimeout(state.hideTimer);
    stopGamepadDismissPolling();
    state.registrations.splice(0).forEach(releaseRegistration);
    document.getElementById(overlayId)?.remove();
    document.getElementById(styleId)?.remove();
  }

  document.addEventListener("pointerdown", handleLaunchEvent, true);
  document.addEventListener("mousedown", handleLaunchEvent, true);
  document.addEventListener("click", handleLaunchEvent, true);
  document.addEventListener("keydown", handleKeyDown, true);
  registerSteamLaunchEvents();
  void loadState();
  state.stateTimer = window.setInterval(loadState, 2500);

  window.ToolsForSteamCurtain = {
    version,
    refresh,
    destroy,
    dismiss,
    isGameEnabled,
    toggleGame,
    get snapshot() { return state.snapshot; },
  };
  return "injected";
})();
