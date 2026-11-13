// TFS Curtain runtime (SharedJSContext).
//
// Runs where Steam's own React and SteamClient live, like a Decky plugin:
//  - wraps SteamClient.Apps.RunGame / RunGameAndWaitForInstaller / RunShortcut,
//    so every start (button, controller, context menu, shortcut) is seen
//    exactly once, without guessing play buttons in the DOM;
//  - draws the curtain into the Big Picture window right away and follows the
//    backend session (pinning, launcher hiding and readiness live in
//    CurtainService.cs) until it fades to black and the game takes over;
//  - registers the per-game editor as a real Steam route (/tfs/curtain/:appId)
//    built from Steam's own components (window.__tfs.ui).
//
// Inspired by Launch Curtain for Playhub (MIT, LoZazaMastro); independent code.
(() => {
  const version = 7;
  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  const existing = window.__tfsCurtain;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const rootId = "tfs-curtain-root";
  const styleId = "tfs-curtain-style";
  const editorRoute = "/tfs/curtain/:appId";
  const pollActiveMs = 120;
  const pollIdleMs = 60000;
  const speculativeMs = 3500;
  const promptWatchMs = 30000;
  const promptSelector = '[role="dialog"], [aria-modal="true"], [class*="ModalPosition"], [class*="GenericConfirmDialog"]';

  const state = {
    snapshot: null,
    uiMode: null,
    active: null, // { appId, title, sessionId, startedAt, confirmed, prompts:Set, doc, element, fading }
    pollTimer: 0,
    idleTimer: 0,
    speculativeTimer: 0,
    restorers: [],
    registrations: [],
    inputRegistration: null,
    routeRemover: null,
    routeTimer: 0,
    destroyed: false,
  };

  // -----------------------------------------------------------------------
  // API
  // -----------------------------------------------------------------------

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  function apiPath(path) {
    return `${apiBase()}${String(path).replace(/^\//, "")}`;
  }

  /** URLs used as <img src> need the session token in the query. */
  function imageUrl(path) {
    if (!path) return "";
    if (typeof window.__steamLoaderApiUrl === "function") return window.__steamLoaderApiUrl(path);
    return apiPath(path);
  }

  async function api(path, body) {
    const response = await fetch(apiPath(path), body === undefined
      ? { cache: "no-store" }
      : { method: "POST", cache: "no-store", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const payload = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, payload };
  }

  async function loadState() {
    try {
      const { ok, payload } = await api("/api/curtain/state");
      if (ok && payload) state.snapshot = payload;
      else if (!ok) state.snapshot = { enabled: false, disabledAppIds: [] };
    } catch {
      state.snapshot = null;
    }
    return state.snapshot;
  }

  function scheduleIdleRefresh() {
    window.clearTimeout(state.idleTimer);
    if (state.destroyed) return;
    state.idleTimer = window.setTimeout(async () => {
      if (!state.active) await loadState();
      scheduleIdleRefresh();
    }, pollIdleMs);
  }

  // -----------------------------------------------------------------------
  // Steam helpers
  // -----------------------------------------------------------------------

  function normalizeAppId(value) {
    if (value === null || value === undefined || value === "") return 0;
    try {
      const text = String(value).trim();
      if (!/^-?\d{1,20}$/.test(text)) return 0;
      let big = BigInt(text);
      if (big < 0n) big = BigInt.asUintN(32, big);
      // 64-bit game ids (shortcuts, mods) carry the app id in the high half.
      if (big > 0xffffffffn) big >>= 32n;
      const appId = Number(big);
      return appId > 0 ? appId : 0;
    } catch {
      return 0;
    }
  }

  function appIdFromArgs(args) {
    for (const arg of args) {
      if (typeof arg === "number" || typeof arg === "string" || typeof arg === "bigint") {
        const appId = normalizeAppId(arg);
        if (appId) return appId;
      } else if (arg && typeof arg === "object") {
        for (const key of ["appid", "appId", "unAppID", "nAppID", "gameid", "strGameID"]) {
          const appId = normalizeAppId(arg[key]);
          if (appId) return appId;
        }
      }
    }
    return 0;
  }

  function appOverview(appId) {
    try { return window.appStore?.GetAppOverviewByAppID?.(appId) || null; } catch { return null; }
  }

  function appTitle(appId) {
    return String(appOverview(appId)?.display_name || "").trim();
  }

  function windowStore() {
    return window.SteamUIStore?.m_WindowStore || window.SteamUIStore?.WindowStore || null;
  }

  function bigPictureWindow() {
    try { return windowStore()?.GamepadUIMainWindowInstance?.BrowserWindow || null; } catch { return null; }
  }

  function isBigPicture() {
    // EUIMode: 4 = Gamepad UI (Big Picture), 7 = desktop.
    if (state.uiMode === 4) return true;
    if (state.uiMode === 7) return false;
    return Boolean(bigPictureWindow()?.document);
  }

  function isGameEnabled(appId) {
    const snapshot = state.snapshot;
    if (!snapshot?.enabled || !appId) return false;
    return !(snapshot.disabledAppIds || []).map(Number).includes(Number(appId));
  }

  // -----------------------------------------------------------------------
  // Curtain look (shared by the live curtain and the editor preview)
  // -----------------------------------------------------------------------

  function clampNumber(value, min, max, fallback) {
    const number = Number(value);
    return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : fallback;
  }

  function layout(game) {
    const g = game || {};
    const scale = clampNumber(g.backgroundScale, 100, 200, 100) / 100;
    return {
      background: {
        objectPosition: `${clampNumber(g.backgroundX, 0, 100, 50)}% ${clampNumber(g.backgroundY, 0, 100, 50)}%`,
        opacity: String(clampNumber(g.backgroundOpacity, 0, 100, 72) / 100),
        transformOrigin: `${clampNumber(g.backgroundX, 0, 100, 50)}% ${clampNumber(g.backgroundY, 0, 100, 50)}%`,
        "--tfs-curtain-scale": String(scale),
        transform: `scale(${scale})`,
      },
      logo: {
        left: `${clampNumber(g.logoX, 0, 100, 50)}%`,
        top: `${clampNumber(g.logoY, 0, 100, 45)}%`,
        width: `${clampNumber(g.logoWidth, 10, 90, 38)}%`,
        filter: g.logoShadow === false ? "none" : "drop-shadow(0 6px 22px rgba(0,0,0,.85))",
      },
      zoom: g.backgroundZoom !== false,
      logoZoom: g.logoZoom === true,
    };
  }

  const curtainCss = `
    #${rootId}, .tfs-curtain-stage {
      position: fixed; inset: 0; z-index: 2147483646; overflow: hidden;
      background: #000; color: #fff; font-family: "Motiva Sans", Arial, sans-serif;
      opacity: 0; transition: opacity 160ms ease-out; pointer-events: auto; contain: strict;
    }
    .tfs-curtain-stage.is-preview { position: absolute; z-index: auto; opacity: 1; contain: content; }
    #${rootId}.is-visible { opacity: 1; }
    #${rootId}.is-closing { opacity: 0; transition-duration: 260ms; }
    .tfs-curtain-bg {
      position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover;
      transition: opacity 260ms ease-out;
    }
    .tfs-curtain-bg.is-zoom { animation: tfs-curtain-zoom 18s ease-in-out infinite alternate; }
    .tfs-curtain-vignette {
      position: absolute; inset: 0; pointer-events: none;
      background: radial-gradient(ellipse at 50% 50%, rgba(0,0,0,0) 55%, rgba(0,0,0,.6) 100%);
    }
    .tfs-curtain-logo {
      position: absolute; transform: translate(-50%, -50%); max-height: 45%; object-fit: contain;
    }
    .tfs-curtain-logo.is-zoom { animation: tfs-curtain-logo-zoom 6s ease-in-out infinite alternate; }
    .tfs-curtain-title {
      position: absolute; transform: translate(-50%, -50%); text-align: center;
      font-size: clamp(28px, 4.6vw, 72px); font-weight: 800; line-height: 1.05;
      text-shadow: 0 8px 30px rgba(0,0,0,.9); max-width: 80%;
    }
    .tfs-curtain-status {
      position: absolute; left: 50%; bottom: 6%; transform: translateX(-50%);
      display: flex; align-items: center; gap: 12px; padding: 11px 20px; border-radius: 22px;
      background: rgba(0,0,0,.5); font-size: 15px; font-weight: 700; letter-spacing: .12em;
      text-transform: uppercase; white-space: nowrap;
    }
    .tfs-curtain-status::before {
      content: ""; width: 9px; height: 9px; border-radius: 50%; background: #5ee6a8;
      box-shadow: 0 0 16px rgba(94,230,168,.7); animation: tfs-curtain-pulse 900ms ease-in-out infinite alternate;
    }
    .tfs-curtain-status .tfs-curtain-hint { color: rgba(255,255,255,.6); letter-spacing: .08em; }
    .tfs-curtain-blackout {
      position: absolute; inset: 0; background: #000; opacity: 0; pointer-events: none;
      transition: opacity 500ms ease-in;
    }
    .is-fading .tfs-curtain-blackout { opacity: 1; }
    .is-preview .tfs-curtain-status { font-size: 10px; padding: 6px 12px; gap: 8px; }
    @keyframes tfs-curtain-zoom { from { transform: scale(var(--tfs-curtain-scale, 1)); } to { transform: scale(calc(var(--tfs-curtain-scale, 1) * 1.045)); } }
    @keyframes tfs-curtain-logo-zoom { from { transform: translate(-50%, -50%) scale(1); } to { transform: translate(-50%, -50%) scale(1.06); } }
    @keyframes tfs-curtain-pulse { to { opacity: .35; } }
    @media (prefers-reduced-motion: reduce) {
      .tfs-curtain-bg.is-zoom, .tfs-curtain-logo.is-zoom, .tfs-curtain-status::before { animation: none; }
    }
  `;

  function ensureStyles(doc) {
    if (!doc?.head) return;
    const current = doc.getElementById(styleId);
    if (current?.dataset.version === String(version)) return;
    current?.remove();
    const style = doc.createElement("style");
    style.id = styleId;
    style.dataset.version = String(version);
    style.textContent = curtainCss;
    doc.head.appendChild(style);
  }

  /** Fills `stage` (a div) with the curtain for `game`. */
  function paintStage(stage, game, title, options = {}) {
    const doc = stage.ownerDocument;
    const look = layout(game);
    stage.replaceChildren();

    const backgroundUrl = imageUrl(game?.backgroundUrl);
    if (backgroundUrl) {
      const background = doc.createElement("img");
      background.className = `tfs-curtain-bg${look.zoom ? " is-zoom" : ""}`;
      background.alt = "";
      background.decoding = "async";
      Object.assign(background.style, look.background);
      background.style.setProperty("--tfs-curtain-scale", look.background["--tfs-curtain-scale"]);
      background.addEventListener("error", () => background.remove(), { once: true });
      background.src = backgroundUrl;
      stage.appendChild(background);
    }

    const vignette = doc.createElement("div");
    vignette.className = "tfs-curtain-vignette";
    stage.appendChild(vignette);

    const showTitle = () => {
      const text = doc.createElement("div");
      text.className = "tfs-curtain-title";
      text.style.left = look.logo.left;
      text.style.top = look.logo.top;
      text.textContent = title || "Starting game";
      stage.insertBefore(text, stage.querySelector(".tfs-curtain-status, .tfs-curtain-blackout"));
    };
    const logoUrl = game?.logoSource === "none" ? "" : imageUrl(game?.logoUrl);
    if (logoUrl) {
      const logo = doc.createElement("img");
      logo.className = `tfs-curtain-logo${look.logoZoom ? " is-zoom" : ""}`;
      logo.alt = "";
      Object.assign(logo.style, look.logo);
      logo.addEventListener("error", () => { logo.remove(); showTitle(); }, { once: true });
      logo.src = logoUrl;
      stage.appendChild(logo);
    } else if (game?.logoSource !== "none") {
      showTitle();
    }

    if (game?.showStatus !== false) {
      const status = doc.createElement("div");
      status.className = "tfs-curtain-status";
      const label = doc.createElement("span");
      label.textContent = game?.statusText || "Preparing game";
      const hint = doc.createElement("span");
      hint.className = "tfs-curtain-hint";
      hint.textContent = options.preview ? "" : "B  Close";
      status.append(label, hint);
      stage.appendChild(status);
    }

    const blackout = doc.createElement("div");
    blackout.className = "tfs-curtain-blackout";
    stage.appendChild(blackout);
  }

  // -----------------------------------------------------------------------
  // Live curtain
  // -----------------------------------------------------------------------

  function visiblePrompts(doc) {
    const found = new Set();
    if (!doc) return found;
    let nodes = [];
    try { nodes = Array.from(doc.querySelectorAll(promptSelector)); } catch { return found; }
    for (const node of nodes) {
      if (node.closest?.(`#${rootId}`)) continue;
      const rect = node.getBoundingClientRect?.();
      if (!rect || rect.width < 260 || rect.height < 100) continue;
      const style = doc.defaultView?.getComputedStyle?.(node);
      if (style?.display === "none" || style?.visibility === "hidden" || Number(style?.opacity ?? "1") <= 0.01) continue;
      found.add(node);
    }
    return found;
  }

  function showCurtain(appId, title, game) {
    const doc = bigPictureWindow()?.document;
    if (!doc?.body) return null;
    ensureStyles(doc);
    let element = doc.getElementById(rootId);
    if (!element) {
      element = doc.createElement("div");
      element.id = rootId;
      element.setAttribute("role", "status");
      element.setAttribute("aria-live", "polite");
      // Swallow pointer input so a stray click cannot start something else.
      for (const type of ["pointerdown", "mousedown", "click", "wheel"]) {
        element.addEventListener(type, (event) => { event.preventDefault(); event.stopPropagation(); }, true);
      }
      doc.body.appendChild(element);
    }
    element.classList.remove("is-closing", "is-fading");
    paintStage(element, game || { logoSource: "auto", showStatus: true }, title);
    element.getBoundingClientRect();
    doc.defaultView?.requestAnimationFrame?.(() => element.classList.add("is-visible"));
    return { doc, element };
  }

  function hideCurtain(immediate = false) {
    const active = state.active;
    state.active = null;
    window.clearTimeout(state.pollTimer);
    window.clearTimeout(state.speculativeTimer);
    state.pollTimer = 0;
    state.speculativeTimer = 0;
    stopInput();
    const element = active?.element || bigPictureWindow()?.document?.getElementById(rootId);
    if (!element) return;
    if (immediate) {
      element.remove();
      return;
    }
    element.classList.add("is-closing");
    window.setTimeout(() => {
      if (!state.active) element.remove();
    }, 300);
  }

  async function dismiss(reason = "dismissed") {
    const wasActive = Boolean(state.active);
    hideCurtain(false);
    if (!wasActive && !state.snapshot?.active) return;
    try { await api("/api/curtain/dismiss", { reason }); } catch {}
  }

  function startInput(doc) {
    stopInput();
    const onKey = (event) => {
      if (!state.active) return;
      if (event.key === "Escape" || event.key === "GoBack" || event.key === "BrowserBack") {
        event.preventDefault();
        event.stopPropagation();
        void dismiss("closed");
      }
    };
    const onCancel = (event) => {
      if (!state.active || !isCancelButton(event)) return;
      // Keep Big Picture from also navigating back underneath the curtain.
      event.preventDefault?.();
      event.stopPropagation?.();
      void dismiss("closed");
    };
    doc?.addEventListener("keydown", onKey, true);
    doc?.addEventListener("vgp_onbuttondown", onCancel, true);
    doc?.addEventListener("vgp_oncancel", onCancel, true);
    state.inputRegistration = () => {
      doc?.removeEventListener("keydown", onKey, true);
      doc?.removeEventListener("vgp_onbuttondown", onCancel, true);
      doc?.removeEventListener("vgp_oncancel", onCancel, true);
    };
  }

  /**
   * Big Picture dispatches gamepad input as DOM events on its document:
   * "vgp_onbuttondown" (detail.button = GamepadButton, 2 = B) and
   * "vgp_oncancel". SteamClient.Input controller messages carry action ids,
   * not buttons, so they are not used here.
   */
  function isCancelButton(event) {
    if (event?.type === "vgp_oncancel") return true;
    const detail = event?.detail || {};
    return Number(detail.button) === 2 && !detail.is_repeat;
  }

  function stopInput() {
    const stop = state.inputRegistration;
    state.inputRegistration = null;
    try { stop?.(); } catch {}
  }

  function release(registration) {
    try {
      if (typeof registration === "function") registration();
      else registration?.unregister?.();
    } catch {}
  }

  async function beginLaunch(appId, source) {
    if (!appId || state.destroyed) return;
    // OmniLibrary game that is not installed: Play starts nothing (the game
    // page Download row installs it), so there is nothing to cover.
    try { if (window.__tfsOmniDownloads?.blocksLaunch?.(appId)) return; } catch {}
    if (!state.snapshot) await loadState();
    if (!isGameEnabled(appId)) return;
    const now = Date.now();
    if (state.active && state.active.appId === appId && now - state.active.startedAt < 15000) return;

    const title = appTitle(appId) || "Starting game";
    const bigPicture = isBigPicture();
    const modern = bigPicture && (state.snapshot?.settings?.mode || "modern") === "modern";
    let surface = null;
    if (modern) {
      // Paint at once; the per-game look follows with the backend reply.
      surface = showCurtain(appId, title, null);
    }

    state.active = {
      appId,
      title,
      sessionId: 0,
      startedAt: now,
      confirmed: false,
      prompts: visiblePrompts(surface?.doc),
      doc: surface?.doc || null,
      element: surface?.element || null,
      modern,
    };
    if (surface) startInput(surface.doc);

    let result = null;
    try {
      result = await api("/api/curtain/launch", { appId, title, reason: source, bigPicture });
    } catch {
      result = null;
    }

    const active = state.active;
    if (!active || active.appId !== appId) return;
    const session = result?.payload?.state?.session;
    if (!result?.ok || result.payload?.shown !== true || !session) {
      hideCurtain(true);
      if (result?.payload?.state) state.snapshot = result.payload.state;
      return;
    }

    state.snapshot = result.payload.state;
    active.sessionId = session.id;
    active.modern = session.mode === "modern";
    if (active.modern) {
      if (!active.element) {
        const painted = showCurtain(appId, title, session.game);
        active.doc = painted?.doc || null;
        active.element = painted?.element || null;
        if (painted) startInput(painted.doc);
      } else {
        paintStage(active.element, session.game, title);
      }
    } else if (active.element) {
      // The backend chose the classic overlay (e.g. desktop mode).
      active.element.remove();
      active.element = null;
    }

    state.speculativeTimer = window.setTimeout(() => {
      const current = state.active;
      if (current && current.sessionId === session.id && !current.confirmed && !state.snapshot?.session?.confirmed) {
        void dismiss("unconfirmed");
      }
    }, speculativeMs);
    poll(session.id);
  }

  function poll(sessionId) {
    window.clearTimeout(state.pollTimer);
    state.pollTimer = window.setTimeout(async () => {
      const active = state.active;
      if (!active || active.sessionId !== sessionId) return;
      await loadState();
      const current = state.active;
      if (!current || current.sessionId !== sessionId) return;
      const session = state.snapshot?.session;
      if (!session || session.id !== sessionId) {
        // Ended by the backend: game took over (already black), timeout,
        // launcher waiting for input, or closed elsewhere.
        hideCurtain(state.snapshot?.lastEndReason === "ready");
        return;
      }

      if (session.confirmed) current.confirmed = true;
      if (session.fadeToBlack && current.element) current.element.classList.add("is-fading");

      // Steam asks something first (EULA, launch options, cloud conflict):
      // step aside so the question is visible.
      if (current.doc && Date.now() - current.startedAt < promptWatchMs) {
        for (const prompt of visiblePrompts(current.doc)) {
          if (!current.prompts.has(prompt)) {
            void dismiss("steam-prompt");
            return;
          }
        }
      }

      poll(sessionId);
    }, pollActiveMs);
  }

  function onAppLifetime(notification) {
    const appId = normalizeAppId(notification?.unAppID ?? notification?.appid);
    const running = notification?.bRunning ?? notification?.running;
    const active = state.active;
    if (!active || !running || (appId && appId !== active.appId)) return;
    active.confirmed = true;
    void api("/api/curtain/confirm", { appId: active.appId }).catch(() => {});
  }

  // -----------------------------------------------------------------------
  // Steam hooks
  // -----------------------------------------------------------------------

  function patchLaunchMethods() {
    const apps = window.SteamClient?.Apps;
    if (!apps) return false;
    for (const method of ["RunGame", "RunGameAndWaitForInstaller", "RunShortcut"]) {
      const current = apps[method];
      if (typeof current !== "function") continue;
      const original = current.__tfsCurtainOriginal || current;
      const wrapped = function tfsCurtainLaunch(...args) {
        try {
          const appId = appIdFromArgs(args);
          if (appId) void beginLaunch(appId, `SteamClient.Apps.${method}`);
        } catch (error) {
          console.warn("[TFS Curtain] launch hook failed", error);
        }
        return original.apply(this, args);
      };
      wrapped.__tfsCurtainOriginal = original;
      try {
        apps[method] = wrapped;
        state.restorers.push(() => {
          if (apps[method] === wrapped) apps[method] = original;
        });
      } catch (error) {
        console.warn(`[TFS Curtain] could not wrap ${method}`, error);
      }
    }
    return true;
  }

  function registerSteamEvents() {
    try {
      state.registrations.push(window.SteamClient?.GameSessions?.RegisterForAppLifetimeNotifications?.(onAppLifetime));
    } catch {}
    try {
      const ui = window.SteamClient?.UI;
      Promise.resolve(ui?.GetUIMode?.()).then((mode) => { if (Number.isFinite(mode)) state.uiMode = mode; }).catch(() => {});
      state.registrations.push(ui?.RegisterForUIModeChanged?.((mode) => { state.uiMode = mode; }));
    } catch {}
  }

  // -----------------------------------------------------------------------
  // Editor route (/tfs/curtain/:appId)
  // -----------------------------------------------------------------------

  const timeoutOptions = [
    [null, "Default"], [0, "No timeout"], [25, "25 s"], [45, "45 s"], [75, "75 s"], [120, "2 min"], [180, "3 min"],
  ];
  const exitDelayOptions = [[null, "Default"], [0, "None"], [0.5, "0.5 s"], [1, "1 s"], [2, "2 s"], [3, "3 s"], [5, "5 s"]];
  const modeOptions = [[null, "Default"], ["modern", "Modern (in Big Picture)"], ["classic", "Classic (overlay window)"], ["off", "Off"]];

  /** "Default" options name the global value they stand for. */
  function withDefaultLabel(options, globalLabel) {
    return options.map(([value, label]) => (value === null && globalLabel ? [value, `Default (${globalLabel})`] : [value, label]));
  }

  function globalLabels() {
    const settings = state.snapshot?.settings || {};
    const timeout = Number(settings.timeoutSeconds);
    const delay = Number(settings.exitDelaySeconds);
    return {
      mode: settings.mode === "classic" ? "Classic" : settings.mode === "off" ? "Off" : "Modern",
      timeout: !Number.isFinite(timeout) ? "" : timeout === 0 ? "no timeout" : timeout >= 120 ? `${timeout / 60} min` : `${timeout} s`,
      delay: Number.isFinite(delay) ? `${delay} s` : "",
    };
  }

  function createEditor(core) {
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};
    // Steam's own labelled dropdown row. Resolved by its source so a wrong
    // match (e.g. the audio device picker, which ignores rgOptions and shows
    // "Select a value") cannot slip in.
    let DropdownRow = null;
    try { DropdownRow = ui.find?.(["dropDownControlRef", "explainerTitle"]) || null; } catch { DropdownRow = null; }

    // PanelSectionRow is sized for the Quick Access Menu; on a full page it
    // pushed every label to the right edge. Steam's fields bring their own
    // padding and separators, so a plain wrapper is enough here.
    function Row({ children }) {
      return h("div", { className: "tfs-curtain-editor-row" }, children);
    }

    function Toggle({ label, description, checked, onChange }) {
      if (ui.ToggleField) return h(Row, null, h(ui.ToggleField, { label, description, checked: Boolean(checked), onChange }));
      return h(Row, null, h(Button, { onClick: () => onChange(!checked) }, `${label}: ${checked ? "On" : "Off"}`));
    }

    function Slider({ label, value, min, max, step = 1, onChange, suffix = "" }) {
      if (ui.SliderField) {
        return h(Row, null, h(ui.SliderField, {
          label, value: Number(value), min, max, step, showValue: true, editableValue: false,
          valueSuffix: suffix, onChange: (next) => onChange(Number(next)),
        }));
      }
      return h(Row, null, h(Button, { onClick: () => onChange(Number(value) + step > max ? min : Number(value) + step) }, `${label}: ${value}${suffix}`));
    }

    function Choice({ label, value, options, onChange }) {
      const rgOptions = options.map(([data, optionLabel], index) => ({ data: index, label: optionLabel }));
      const selected = Math.max(0, options.findIndex(([data]) => data === value));
      const select = (option) => onChange(options[Number(option?.data ?? 0)][0]);
      if (DropdownRow) {
        // Steam's row defaults to an inline layout whose menu is forced to the
        // (narrow) button width (bMatchWidth), which cut option names off.
        // Put the choice under its label at full width and let the menu size
        // itself to its longest option.
        return h(Row, null, h(DropdownRow, {
          label,
          rgOptions,
          selectedOption: selected,
          onChange: select,
          layout: "below",
          childrenContainerWidth: "max",
          contextMenuPositionOptions: { bMatchWidth: false },
        }));
      }
      if (ui.Dropdown) {
        return h(Row, null, h("div", { style: { display: "flex", flexDirection: "column", gap: "8px", padding: "6px 0" } },
          h("div", null, label),
          h("div", { style: { width: "100%" } }, h(ui.Dropdown, {
            rgOptions, selectedOption: selected, onChange: select, menuLabel: label,
            contextMenuPositionOptions: { bMatchWidth: false },
          }))));
      }
      return h(Row, null, h(Button, { onClick: () => select({ data: (selected + 1) % options.length }) }, `${label}: ${options[selected][1]}`));
    }

    function Button({ onClick, children, disabled }) {
      if (ui.DialogButton) return h(ui.DialogButton, { onClick, disabled, style: { marginTop: "6px" } }, children);
      return h("button", { type: "button", className: "DialogButton gpfocus Focusable", onClick, disabled, tabIndex: 0 }, children);
    }

    function Heading({ children }) {
      return h("div", {
        style: { margin: "22px 0 6px", fontSize: "13px", fontWeight: 700, letterSpacing: ".08em", textTransform: "uppercase", color: "rgba(255,255,255,.55)" },
      }, children);
    }

    function Preview({ game, title }) {
      const ref = React.useRef(null);
      React.useEffect(() => {
        const stage = ref.current;
        if (!stage) return;
        ensureStyles(stage.ownerDocument);
        paintStage(stage, game, title, { preview: true });
      }, [game, title]);
      return h("div", { style: { position: "relative", width: "100%", aspectRatio: "16 / 9", borderRadius: "8px", overflow: "hidden", background: "#000", boxShadow: "0 0 0 1px rgba(255,255,255,.12)" } },
        h("div", { ref, className: "tfs-curtain-stage is-preview" }));
    }

    return function TfsCurtainEditor(props) {
      const appId = normalizeAppId(props?.match?.params?.appId);
      const title = appTitle(appId) || `App ${appId}`;
      const [details, setDetails] = React.useState(null);
      const [draft, setDraft] = React.useState(null);
      const [enabled, setEnabled] = React.useState(true);
      const [error, setError] = React.useState("");
      const saveTimer = React.useRef(0);

      React.useEffect(() => {
        let alive = true;
        api(`/api/curtain/game?appId=${appId}`).then(({ ok, payload }) => {
          if (!alive) return;
          if (!ok || !payload) { setError(payload?.message || "TFS Curtain is not available."); return; }
          setDetails(payload);
          setDraft({ ...(payload.overrides || {}) });
          setEnabled(payload.resolved?.enabled !== false);
        }).catch(() => alive && setError("TFS is not reachable."));
        return () => { alive = false; window.clearTimeout(saveTimer.current); };
      }, [appId]);

      const save = React.useCallback((nextDraft, nextEnabled) => {
        window.clearTimeout(saveTimer.current);
        saveTimer.current = window.setTimeout(async () => {
          const { ok, payload } = await api("/api/curtain/game", { appId, settings: nextDraft, enabled: nextEnabled }).catch(() => ({ ok: false }));
          if (ok && payload) {
            setDetails(payload);
            void loadState();
          }
        }, 350);
      }, [appId]);

      const update = (patch) => {
        const next = { ...(draft || {}), ...patch };
        setDraft(next);
        // Show the change at once; the server reply brings resolved images.
        setDetails((current) => current && { ...current, resolved: { ...current.resolved, ...resolvedPatch(patch, current) } });
        save(next, enabled);
      };

      const toggleEnabled = (value) => {
        setEnabled(value);
        save(draft || {}, value);
      };

      const reset = async () => {
        window.clearTimeout(saveTimer.current);
        const { ok, payload } = await api("/api/curtain/game/reset", { appId }).catch(() => ({ ok: false }));
        if (ok && payload) {
          setDetails(payload);
          setDraft({});
          setEnabled(true);
          void loadState();
        }
      };

      const preview = () => {
        const game = details?.resolved;
        const painted = showCurtain(appId, title, game);
        if (!painted) return;
        state.previewing = true;
        const close = () => {
          window.clearTimeout(timer);
          stop();
          painted.element.classList.add("is-closing");
          window.setTimeout(() => painted.element.remove(), 300);
          state.previewing = false;
        };
        const timer = window.setTimeout(close, 4000);
        const onKey = (event) => {
          if (event.key !== "Escape" && event.key !== "GoBack") return;
          event.preventDefault();
          event.stopPropagation();
          close();
        };
        const onCancel = (event) => {
          if (!isCancelButton(event)) return;
          // B closes the preview only; it must not leave the editor page.
          event.preventDefault?.();
          event.stopPropagation?.();
          close();
        };
        painted.doc.addEventListener("keydown", onKey, true);
        painted.doc.addEventListener("vgp_onbuttondown", onCancel, true);
        painted.doc.addEventListener("vgp_oncancel", onCancel, true);
        const stop = () => {
          painted.doc.removeEventListener("keydown", onKey, true);
          painted.doc.removeEventListener("vgp_onbuttondown", onCancel, true);
          painted.doc.removeEventListener("vgp_oncancel", onCancel, true);
        };
      };

      const page = { padding: "56px 36px 40px", height: "100%", boxSizing: "border-box", overflowY: "auto", color: "#fff" };
      if (error) return h("div", { style: page }, h("h2", null, "TFS Curtain"), h("p", null, error));
      if (!details || !draft) return h("div", { style: page }, h("h2", null, "TFS Curtain"), h("p", { style: { opacity: 0.7 } }, "Loading…"));

      const resolved = details.resolved || {};
      const backgroundChoices = [["auto", "Automatic"], ["none", "None (black)"]]
        .concat((details.candidates || []).filter((item) => item.kind === "background").map((item) => [item.id, item.label]));
      if (draft.backgroundSource === "custom") backgroundChoices.push(["custom", "Custom file"]);
      const logoChoices = [["auto", "Automatic"], ["none", "No logo"]]
        .concat((details.candidates || []).filter((item) => item.kind === "logo").map((item) => [item.id, item.label]));
      if (draft.logoSource === "custom") logoChoices.push(["custom", "Custom file"]);

      const value = (key, fallback) => (draft[key] ?? resolved[key] ?? fallback);
      const globals = globalLabels();
      const Focusable = ui.Focusable;
      const column = (children) => (Focusable ? h(Focusable, { "flow-children": "column", style: { minWidth: 0 } }, ...children) : h("div", { style: { minWidth: 0 } }, ...children));

      return h("div", { style: page },
        h("div", { style: { display: "flex", alignItems: "baseline", gap: "14px", marginBottom: "6px" } },
          h("h2", { style: { margin: 0, fontSize: "26px" } }, "TFS Curtain"),
          h("div", { style: { opacity: 0.6, fontSize: "16px" } }, title)),
        h("div", { style: { opacity: 0.65, fontSize: "14px", marginBottom: "16px", maxWidth: "980px" } },
          "Covers launchers and desktop flashes from Play until the game is ready, then fades to black and hands over. Changes save automatically."),
        h("div", { style: { display: "grid", gridTemplateColumns: "minmax(0, 1.45fr) minmax(0, 1fr)", gap: "28px", alignItems: "start" } },
          column([
            h(Toggle, { label: "Use TFS Curtain for this game", checked: enabled, onChange: toggleEnabled }),
            h(Choice, { label: "Mode", value: draft.mode ?? null, options: withDefaultLabel(modeOptions, globals.mode), onChange: (mode) => update({ mode }) }),
            h(Heading, null, "Background"),
            h(Choice, { label: "Image", value: draft.backgroundSource ?? "auto", options: backgroundChoices, onChange: (backgroundSource) => update({ backgroundSource }) }),
            h(Slider, { label: "Opacity", value: value("backgroundOpacity", 72), min: 0, max: 100, step: 5, suffix: " %", onChange: (backgroundOpacity) => update({ backgroundOpacity }) }),
            h(Slider, { label: "Size", value: value("backgroundScale", 100), min: 100, max: 200, step: 5, suffix: " %", onChange: (backgroundScale) => update({ backgroundScale }) }),
            h(Slider, { label: "Horizontal position", value: value("backgroundX", 50), min: 0, max: 100, step: 5, suffix: " %", onChange: (backgroundX) => update({ backgroundX }) }),
            h(Slider, { label: "Vertical position", value: value("backgroundY", 50), min: 0, max: 100, step: 5, suffix: " %", onChange: (backgroundY) => update({ backgroundY }) }),
            h(Toggle, { label: "Slow zoom", checked: value("backgroundZoom", true), onChange: (backgroundZoom) => update({ backgroundZoom }) }),
            h(Heading, null, "Logo"),
            h(Choice, { label: "Logo", value: draft.logoSource ?? "auto", options: logoChoices, onChange: (logoSource) => update({ logoSource }) }),
            h(Slider, { label: "Logo width", value: value("logoWidth", 38), min: 10, max: 90, step: 2, suffix: " %", onChange: (logoWidth) => update({ logoWidth }) }),
            h(Slider, { label: "Logo horizontal", value: value("logoX", 50), min: 0, max: 100, step: 2, suffix: " %", onChange: (logoX) => update({ logoX }) }),
            h(Slider, { label: "Logo vertical", value: value("logoY", 45), min: 0, max: 100, step: 2, suffix: " %", onChange: (logoY) => update({ logoY }) }),
            h(Toggle, { label: "Logo shadow", checked: value("logoShadow", true), onChange: (logoShadow) => update({ logoShadow }) }),
            h(Toggle, { label: "Logo pulse", checked: value("logoZoom", false), onChange: (logoZoom) => update({ logoZoom }) }),
            h(Heading, null, "Timing"),
            h(Choice, { label: "Timeout", value: draft.timeoutSeconds ?? null, options: withDefaultLabel(timeoutOptions, globals.timeout), onChange: (timeoutSeconds) => update({ timeoutSeconds }) }),
            h(Choice, { label: "Stay after the game is ready", value: draft.exitDelaySeconds ?? null, options: withDefaultLabel(exitDelayOptions, globals.delay), onChange: (exitDelaySeconds) => update({ exitDelaySeconds }) }),
            ui.TextField
              ? h(Row, null, h(ui.TextField, {
                label: "Status text",
                description: "Empty uses the global text.",
                value: draft.statusText ?? "",
                onChange: (event) => update({ statusText: String(event?.target?.value ?? "") }),
              }))
              : null,
            h(Row, null, h("div", { style: { display: "flex", gap: "12px", marginTop: "14px" } },
              h(Button, { onClick: preview }, "Preview"),
              h(Button, { onClick: reset }, "Reset this game"))),
          ].filter(Boolean)),
          h("div", { style: { position: "sticky", top: "0" } },
            h(Preview, { game: resolved, title }),
            h("div", { style: { opacity: 0.55, fontSize: "13px", marginTop: "10px", lineHeight: 1.4 } },
              "Automatic images follow Steam and everything you set in TFS Artworks. B closes the curtain during a launch."))));
    };
  }

  /** Optimistic preview values while the save request is running. */
  function resolvedPatch(patch, details) {
    const result = {};
    for (const [key, value] of Object.entries(patch)) {
      if (value === null || value === undefined) continue;
      if (key === "backgroundSource" || key === "logoSource") {
        const kind = key === "backgroundSource" ? "background" : "logo";
        result[key] = value;
        if (value === "none") result[`${kind}Url`] = "";
        else {
          const candidate = (details.candidates || []).find((item) => item.kind === kind && item.id === value);
          if (candidate) result[`${kind}Url`] = candidate.url;
        }
        continue;
      }
      result[key] = value;
    }
    return result;
  }

  async function registerEditorRoute() {
    window.clearTimeout(state.routeTimer);
    if (state.destroyed || state.routeRemover) return;
    const core = window.__tfs;
    if (!core?.routes?.add) {
      // The core loads separately; try again until it is there.
      state.routeTimer = window.setTimeout(registerEditorRoute, 2000);
      return;
    }
    try {
      await core.whenReady;
      if (state.destroyed || state.routeRemover || !core.React) return;
      if (core.routes.has?.(editorRoute)) core.routes.remove(editorRoute);
      state.routeRemover = core.routes.add(editorRoute, createEditor(core), { name: "TFS Curtain", exact: true });
    } catch (error) {
      console.warn("[TFS Curtain] editor route failed", error);
      state.routeTimer = window.setTimeout(registerEditorRoute, 10000);
    }
  }

  function openEditor(appId) {
    const id = normalizeAppId(appId);
    const routes = window.__tfs?.routes;
    if (!id || !routes?.navigate || !state.routeRemover) return false;
    routes.navigate(`/tfs/curtain/${id}`);
    return true;
  }

  async function toggleGame(appId, enabled) {
    const id = normalizeAppId(appId);
    if (!id) return false;
    const { ok, payload } = await api("/api/curtain/game-enabled", { appId: id, enabled: Boolean(enabled) }).catch(() => ({ ok: false }));
    if (ok && payload) state.snapshot = payload;
    return Boolean(ok);
  }

  // -----------------------------------------------------------------------
  // Lifecycle
  // -----------------------------------------------------------------------

  function refresh() {
    void loadState();
    if (!state.routeRemover) void registerEditorRoute();
  }

  function destroy() {
    state.destroyed = true;
    hideCurtain(true);
    window.clearTimeout(state.idleTimer);
    window.clearTimeout(state.routeTimer);
    state.restorers.splice(0).forEach((restore) => { try { restore(); } catch {} });
    state.registrations.splice(0).forEach(release);
    try { state.routeRemover?.(); } catch {}
    state.routeRemover = null;
    try { bigPictureWindow()?.document?.getElementById(styleId)?.remove(); } catch {}
  }

  window.__tfsCurtain = {
    version,
    refresh,
    destroy,
    dismiss,
    openEditor,
    toggleGame,
    isGameEnabled,
    hasEditor: () => Boolean(state.routeRemover),
    get snapshot() { return state.snapshot; },
    // Exposed for tests and diagnostics.
    _internals: { normalizeAppId, appIdFromArgs, layout, resolvedPatch },
  };

  void loadState();
  scheduleIdleRefresh();
  registerSteamEvents();
  if (!patchLaunchMethods()) {
    // SteamClient can appear a moment after the shared context starts.
    let attempts = 0;
    const retry = () => {
      if (state.destroyed || patchLaunchMethods() || ++attempts > 60) return;
      window.setTimeout(retry, 1000);
    };
    window.setTimeout(retry, 1000);
  }
  void registerEditorRoute();
  return "injected";
})();
