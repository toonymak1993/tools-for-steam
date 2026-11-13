// TFS Overture runtime (SharedJSContext).
//
// Theme music for Big Picture game pages:
//  - follows Big Picture's router history; the game details page
//    (/library/app/<id>) plays that game's playlist, its sub pages keep it
//    playing, other pages can play the optional ambient playlist;
//  - audio comes from the local TFS API (the user's own files or themes TFS
//    downloaded on request), played by an <audio> element in Big Picture;
//  - fades in and out, loops the playlist, skips silent intros, stops when a
//    game starts, pauses while Big Picture is hidden and steps aside while a
//    TFS trailer plays with sound;
//  - registers /tfs/overture/:appId (per game) and /tfs/overture (global
//    settings and ambient music), built from Steam's own components.
//
// Feature idea from ThemeDeck for Decky (BrenticusMaximus); independent code.
(() => {
  const version = 4;

  // -----------------------------------------------------------------------
  // Pure helpers (also used by tests through window.__tfsOvertureTestHooks)
  // -----------------------------------------------------------------------

  /** The game a Big Picture path belongs to: { appId, details } or null. */
  function gameFromPath(pathname) {
    const match = /^\/library\/app\/(\d{1,10})(\/[^?#]*)?\/?$/.exec(String(pathname || ""));
    const appId = match ? Number(match[1]) : 0;
    if (!(appId > 0 && appId <= 0xffffffff)) return null;
    const rest = String(match[2] || "").replace(/\/+$/, "");
    return { appId, details: rest === "" };
  }

  function isEditorPath(pathname) {
    return /^\/tfs\/overture(\/\d{1,10})?\/?$/.test(String(pathname || ""));
  }

  /**
   * What should play for a path: a game key ("440"), "ambient" or "" (silence).
   * <current> is the key playing now, so a game's sub pages keep its music.
   */
  function targetFor(pathname, current, options) {
    const path = String(pathname || "");
    if (!path || isEditorPath(path)) return "";
    const game = gameFromPath(path);
    if (game) {
      const key = String(game.appId);
      if (game.details || current === key) return key;
    }
    return options?.ambient ? "ambient" : "";
  }

  /** Loudness 0..1 from master volume, playlist volume (both 0..100) and ducking. */
  function targetVolume(master, playlist, ducked) {
    const value = (Math.max(0, Math.min(100, Number(master) || 0)) / 100) *
      (Math.max(0, Math.min(100, Number(playlist ?? 100))) / 100);
    return ducked ? 0 : Math.round(value * 1000) / 1000;
  }

  function formatDuration(seconds) {
    const total = Math.round(Number(seconds) || 0);
    if (total <= 0) return "";
    const minutes = Math.floor(total / 60);
    return `${minutes}:${String(total % 60).padStart(2, "0")}`;
  }

  /** Index of the next track, or -1 at the end of a playlist that does not loop. */
  function nextIndex(index, count, loop) {
    if (count <= 0) return -1;
    if (index + 1 < count) return index + 1;
    return loop ? 0 : -1;
  }

  /**
   * The installed games (Steam games and non-Steam shortcuts) played most
   * recently, newest first: what the prefetch prepares themes for.
   */
  function recentInstalledGames(apps, limit) {
    const shortcutType = 1073741824;
    return (Array.isArray(apps) ? apps : [])
      .filter((app) => app && app.installed && (Number(app.app_type) === 1 || Number(app.app_type) === shortcutType) &&
        Number(app.appid) > 0 && Number(app.rt_last_time_played) > 0 && String(app.display_name || "").trim())
      .sort((a, b) => Number(b.rt_last_time_played) - Number(a.rt_last_time_played))
      .slice(0, limit)
      .map((app) => ({ appId: Number(app.appid), title: String(app.display_name).trim() }));
  }

  const internals = { gameFromPath, isEditorPath, targetFor, targetVolume, formatDuration, nextIndex, recentInstalledGames };
  if (window.__tfsOvertureTestHooks === true) {
    window.__tfsOvertureInternals = internals;
    return "test";
  }

  // -----------------------------------------------------------------------
  // Runtime bootstrap
  // -----------------------------------------------------------------------

  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  const existing = window.__tfsOverture;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const styleId = "tfs-overture-style";
  const editorRoutes = ["/tfs/overture/:appId", "/tfs/overture"];
  const settingsRefreshMs = 60000;
  const tickMs = 500;
  const chipMs = 5000;
  const prefetchCount = 30;
  const prefetchFirstMs = 90000;
  const prefetchRetryMs = 10 * 60000;
  const prefetchEveryMs = 6 * 3600000;

  const state = {
    settings: null,
    settingsLoadedAt: 0,
    session: null,
    sessionToken: 0,
    history: null,
    historyUnlisten: null,
    historyTimer: 0,
    runningApps: new Set(),
    lastPath: "",
    registrations: [],
    routeRemovers: [],
    routeTimer: 0,
    autoFinding: new Set(),
    prefetchTimer: 0,
    pendingKey: "",
    destroyed: false,
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

  /** <audio> cannot send headers, so the session token travels in the query. */
  function audioUrl(relative) {
    const path = String(relative || "").replace(/^\//, "");
    try {
      if (typeof window.__steamLoaderApiUrl === "function") return window.__steamLoaderApiUrl(path);
    } catch {}
    const url = new URL(path, apiBase());
    if (window.__steamLoaderApiToken) url.searchParams.set("tfsSession", window.__steamLoaderApiToken);
    return url.toString();
  }

  async function loadSettings(force = false) {
    if (!force && state.settings && Date.now() - state.settingsLoadedAt < settingsRefreshMs) return state.settings;
    try {
      const { ok, payload } = await api("/api/overture/settings");
      // 404/403 = plugin turned off in the Plugin Activator.
      state.settings = ok && payload ? payload : { enabled: false, unavailable: true };
    } catch {
      state.settings = state.settings || { enabled: false, unavailable: true };
    }
    state.settingsLoadedAt = Date.now();
    return state.settings;
  }

  function gameTitle(appId) {
    const overview = appOverview(appId);
    return overview?.display_name || overview?.strDisplayName || "";
  }

  function playlistQuery(key) {
    const title = key === "ambient" ? "" : gameTitle(Number(key));
    return `/api/overture/playlist?key=${encodeURIComponent(key)}&title=${encodeURIComponent(title)}`;
  }

  // -----------------------------------------------------------------------
  // Steam helpers
  // -----------------------------------------------------------------------

  function mainWindow() {
    try {
      const store = window.SteamUIStore?.m_WindowStore || window.SteamUIStore?.WindowStore;
      return store?.GamepadUIMainWindowInstance || null;
    } catch {
      return null;
    }
  }

  function bigPictureWindow() {
    const main = mainWindow();
    try { return main?.BrowserWindow || main?.m_BrowserWindow || null; } catch { return null; }
  }

  function currentPath() {
    const main = mainWindow();
    return main?.m_history?.location?.pathname || main?.m_locationPathname || "";
  }

  function appOverview(appId) {
    try { return window.appStore?.GetAppOverviewByAppID?.(appId) || null; } catch { return null; }
  }

  function release(registration) {
    try {
      if (typeof registration === "function") registration();
      else registration?.unregister?.();
    } catch {}
  }

  function trailerHasSound() {
    try {
      const session = window.__tfsTrailers?.session;
      return Boolean(session?.visible && session.muted === false);
    } catch {
      return false;
    }
  }

  /** Steam's own list wins when it is there; lifetime events are the fallback. */
  function isGameRunning() {
    try {
      const running = window.SteamUIStore?.RunningApps;
      if (Array.isArray(running)) {
        if (!running.length) state.runningApps.clear();
        return running.length > 0;
      }
    } catch {}
    return state.runningApps.size > 0;
  }

  // -----------------------------------------------------------------------
  // Playback session
  // -----------------------------------------------------------------------

  function ensureStyles(doc) {
    if (!doc || doc.getElementById(styleId)) return;
    const style = doc.createElement("style");
    style.id = styleId;
    style.textContent = `
      .tfs-overture-chip {
        position: fixed;
        left: 2.2vw;
        bottom: 3.2vh;
        z-index: 9000;
        display: flex;
        align-items: center;
        gap: 10px;
        max-width: min(46vw, 640px);
        padding: 8px 16px 8px 10px;
        border-radius: 999px;
        background: rgba(10, 12, 16, .72);
        box-shadow: 0 6px 20px rgba(0, 0, 0, .35);
        color: rgba(255, 255, 255, .92);
        font: 600 14px/1.2 "Motiva Sans", Arial, sans-serif;
        opacity: 0;
        transform: translateY(8px);
        transition: opacity 320ms ease, transform 320ms ease;
        pointer-events: none;
      }
      .tfs-overture-chip.is-visible { opacity: 1; transform: none; }
      .tfs-overture-chip b {
        flex: none;
        display: inline-grid;
        place-items: center;
        width: 24px;
        height: 24px;
        border-radius: 50%;
        background: #1f6feb;
        color: #fff;
        font-size: 13px;
      }
      .tfs-overture-chip span { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      @media (prefers-reduced-motion: reduce) { .tfs-overture-chip { transition: none; transform: none; } }
    `;
    doc.head.append(style);
  }

  function showChip(session, text) {
    if (!state.settings?.showNowPlaying || !text) return;
    const doc = session.doc;
    if (!doc?.body) return;
    ensureStyles(doc);
    if (!session.chip || !session.chip.isConnected) {
      const chip = doc.createElement("div");
      chip.className = "tfs-overture-chip";
      chip.setAttribute("aria-hidden", "true");
      chip.innerHTML = "<b>♪</b><span></span>";
      doc.body.append(chip);
      session.chip = chip;
    }
    session.chip.querySelector("span").textContent = text;
    session.chip.classList.add("is-visible");
    window.clearTimeout(session.chipTimer);
    session.chipTimer = window.setTimeout(() => session.chip?.classList.remove("is-visible"), chipMs);
  }

  /** Moves audio.volume towards session.target over the fade time (no rAF loop). */
  function rampVolume(session, onDone) {
    window.clearInterval(session.fadeTimer);
    const audio = session.audio;
    const from = audio.volume;
    const to = session.target;
    const fadeMs = Math.max(0, (Number(state.settings?.fadeSeconds) || 0) * 1000);
    if (fadeMs <= 0 || Math.abs(to - from) < 0.005) {
      audio.volume = to;
      onDone?.();
      return;
    }
    const startedAt = Date.now();
    session.fadeTimer = window.setInterval(() => {
      const progress = Math.min(1, (Date.now() - startedAt) / fadeMs);
      try { audio.volume = Math.max(0, Math.min(1, from + (to - from) * progress)); } catch {}
      if (progress >= 1) {
        window.clearInterval(session.fadeTimer);
        onDone?.();
      }
    }, 50);
  }

  function computeTarget(session) {
    const ducked = (state.settings?.yieldToTrailers !== false && trailerHasSound()) || session.muted;
    return targetVolume(state.settings?.masterVolume ?? 60, session.playlist.volume, ducked);
  }

  function playTrack(session, index) {
    const track = session.tracks[index];
    if (!track || session.stopped) return;
    session.index = index;
    session.failures = session.failures || 0;
    const audio = session.audio;
    audio.src = audioUrl(track.audioPath);
    const start = Math.max(0, Number(session.playlist.startSeconds) || 0);
    const begin = () => {
      if (session.stopped) return;
      if (start > 0 && Number.isFinite(audio.duration) && audio.duration > start + 5) {
        try { audio.currentTime = start; } catch {}
      }
      if (session.wantPlaying) void audio.play().catch(() => {});
    };
    audio.addEventListener("loadedmetadata", begin, { once: true });
    audio.load();
    if (index === 0 || session.tracks.length > 1) {
      showChip(session, track.title + (track.channel ? ` · ${track.channel}` : ""));
    }
  }

  function startPlayback(key, playlist) {
    const win = bigPictureWindow();
    const doc = win?.document;
    const tracks = (playlist?.tracks || []).filter((track) => !track.missing && track.audioPath);
    if (!doc || !win?.Audio || !tracks.length) return null;

    const audio = new win.Audio();
    audio.preload = "auto";
    audio.volume = 0;
    const session = {
      key, playlist, tracks, audio, doc, index: 0, stopped: false, wantPlaying: true, muted: false,
      target: 0, fadeTimer: 0, chipTimer: 0, tickTimer: 0, chip: null, failures: 0,
    };

    audio.addEventListener("playing", () => {
      session.failures = 0;
      session.target = computeTarget(session);
      rampVolume(session);
    });
    audio.addEventListener("ended", () => {
      const next = nextIndex(session.index, session.tracks.length, playlist.loop !== false);
      if (next < 0) { stopSession(); return; }
      playTrack(session, next);
    });
    audio.addEventListener("error", () => {
      // A broken file must not turn into an endless retry loop.
      session.failures += 1;
      if (session.failures >= session.tracks.length) { stopSession(true); return; }
      const next = nextIndex(session.index, session.tracks.length, true);
      if (next >= 0) playTrack(session, next);
    });

    // Pause while Big Picture is hidden or a game runs; follow trailer sound
    // and volume changes made in the settings page.
    session.tickTimer = window.setInterval(() => {
      if (session.stopped) return;
      if (isGameRunning()) { stopSession(); return; }
      const showing = doc.visibilityState !== "hidden";
      if (!showing && !audio.paused) audio.pause();
      else if (showing && session.wantPlaying && audio.paused && audio.src) void audio.play().catch(() => {});
      const target = computeTarget(session);
      if (Math.abs(target - session.target) > 0.005) {
        session.target = target;
        rampVolume(session);
      }
    }, tickMs);

    state.session = session;
    playTrack(session, 0);
    return session;
  }

  function stopSession(immediate = false) {
    const session = state.session;
    state.session = null;
    state.pendingKey = "";
    state.sessionToken += 1;
    if (!session) return;
    session.stopped = true;
    window.clearInterval(session.tickTimer);
    window.clearTimeout(session.chipTimer);
    session.chip?.classList.remove("is-visible");
    const chip = session.chip;
    const finish = () => {
      window.clearInterval(session.fadeTimer);
      try { session.audio.pause(); } catch {}
      try { session.audio.removeAttribute("src"); session.audio.load(); } catch {}
      window.setTimeout(() => { try { chip?.remove(); } catch {} }, 400);
    };
    if (immediate || session.audio.paused) {
      finish();
      return;
    }
    session.target = 0;
    rampVolume(session, finish);
  }

  async function startFor(key) {
    stopSession();
    const token = state.sessionToken;
    // Remembered while loading/searching, so leaving the page cancels it
    // (stopSession bumps the token) and a sub page of the game keeps it.
    state.pendingKey = key;
    try {
      await prepareAndPlay(key, token);
    } finally {
      if (state.pendingKey === key && token === state.sessionToken) state.pendingKey = "";
    }
  }

  /** Still the page the music was meant for (checked again after slow steps). */
  function stillWanted(key, token) {
    if (token !== state.sessionToken || state.destroyed || isGameRunning()) return false;
    const ambient = Boolean(state.settings?.enabled && state.settings?.ambientEnabled);
    return targetFor(currentPath(), key, { ambient }) === key;
  }

  async function prepareAndPlay(key, token) {
    const openedAt = Date.now();
    const settings = await loadSettings();
    if (token !== state.sessionToken || !settings?.enabled || isGameRunning()) return;
    if (key !== "ambient" && (settings.silentAppIds || []).includes(Number(key))) return;
    if (key === "ambient" && !settings.ambientEnabled) return;

    let { ok, payload } = await api(playlistQuery(key)).catch(() => ({ ok: false }));
    if (token !== state.sessionToken || !ok || !payload?.enabled) return;

    if (!payload.tracks?.some((track) => !track.missing) && payload.canAutoFind && !state.autoFinding.has(key)) {
      // Looking for the theme can take a while; play it if the page is still open.
      state.autoFinding.add(key);
      try {
        ({ ok, payload } = await api("/api/overture/auto", { key, title: gameTitle(Number(key)) }).catch(() => ({ ok: false })));
      } finally {
        state.autoFinding.delete(key);
      }
      state.settingsLoadedAt = 0;
      if (!stillWanted(key, token) || !ok || !payload?.enabled) return;
    }

    const delayMs = Math.max(0, (Number(settings.delaySeconds) || 0) * 1000 - (Date.now() - openedAt));
    if (delayMs > 0) await new Promise((resolve) => window.setTimeout(resolve, delayMs));
    if (!stillWanted(key, token)) return;
    startPlayback(key, payload);
  }

  function evaluate(force = false) {
    if (state.destroyed) return;
    const path = currentPath();
    if (!force && path === state.lastPath) return;
    state.lastPath = path;
    // A start that is still loading counts as playing: its sub pages keep it,
    // every other page cancels it.
    const current = state.session?.key || state.pendingKey || "";
    const target = isGameRunning()
      ? ""
      : targetFor(path, current, { ambient: state.settings?.enabled && state.settings?.ambientEnabled });
    if (!target) {
      if (state.session || state.pendingKey) stopSession();
      return;
    }
    if (target === current) return;
    void startFor(target);
  }

  /**
   * Listens to Big Picture's router history. Steam can replace the history
   * object (Big Picture re-created, mode switches), so a light watchdog
   * re-attaches and also compares the path as a fallback.
   */
  function watchHistory() {
    if (state.destroyed) return;
    const history = mainWindow()?.m_history;
    if (history && history !== state.history) {
      release(state.historyUnlisten);
      state.historyUnlisten = null;
      state.history = null;
      if (typeof history.listen === "function") {
        try {
          state.historyUnlisten = history.listen(() => evaluate());
          state.history = history;
        } catch {}
      }
    }
    evaluate();
    window.clearTimeout(state.historyTimer);
    state.historyTimer = window.setTimeout(watchHistory, state.history ? 1500 : 3000);
  }

  // -----------------------------------------------------------------------
  // Settings pages (/tfs/overture and /tfs/overture/:appId)
  // -----------------------------------------------------------------------

  const volumeOptions = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10].map((value) => [value, `${value} %`]);
  const masterOptions = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0].map((value) => [value, `${value} %`]);
  const startOptions = [[0, "None"], [1, "1 s"], [2, "2 s"], [3, "3 s"], [5, "5 s"], [8, "8 s"], [10, "10 s"], [15, "15 s"], [20, "20 s"], [30, "30 s"]];
  const fadeOptions = [[0, "Off"], [0.5, "0.5 s"], [1, "1 s"], [1.5, "1.5 s"], [2, "2 s"], [3, "3 s"], [5, "5 s"]];
  const delayOptions = [[0, "Immediately"], [0.5, "0.5 s"], [1, "1 s"], [2, "2 s"], [3, "3 s"], [5, "5 s"]];

  /**
   * Steam's labelled dropdown field (label + DropDown control). Found next to
   * the bare Dropdown, by props only it uses; core.ui.DropdownItem can match
   * Steam's audio device picker, which ignores the options passed to it.
   */
  function resolveDropdownItem(core) {
    const webpack = core.webpack;
    const dropdown = core.ui?.Dropdown;
    const isField = (value) => {
      const source = typeof value === "function" ? webpack?.getSourceText?.(value) || "" : "";
      return source.includes("dropDownControlRef") && source.includes("explainerTitle");
    };
    try {
      for (const module of webpack?.getModules?.() || []) {
        const exportsObject = module?.exports ?? module;
        if (!exportsObject || typeof exportsObject !== "object") continue;
        const values = Object.values(exportsObject);
        if (dropdown && !values.includes(dropdown)) continue;
        const match = values.find(isField);
        if (match) return match;
      }
    } catch {}
    const fallback = core.ui?.DropdownItem;
    return fallback && isField(fallback) ? fallback : null;
  }

  function createEditor(core) {
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};
    const DropdownItem = resolveDropdownItem(core);
    const Focusable = ui.Focusable;

    function Row({ children }) {
      return h("div", { className: "tfs-overture-row", style: { minWidth: 0 } }, children);
    }

    function Button({ onClick, children, disabled, grow }) {
      const style = { marginTop: "6px", ...(grow ? { flex: "1 1 auto", minWidth: 0, textAlign: "left", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" } : { flex: "none", width: "auto", minWidth: "120px" }) };
      if (ui.DialogButton) return h(ui.DialogButton, { onClick, disabled, style }, children);
      return h("button", { type: "button", className: "DialogButton gpfocus Focusable", onClick, disabled, tabIndex: 0, style }, children);
    }

    function ButtonRow({ children }) {
      const style = { display: "flex", gap: "10px", alignItems: "center", minWidth: 0 };
      return Focusable
        ? h(Focusable, { "flow-children": "row", style }, children)
        : h("div", { style }, children);
    }

    function Toggle({ label, description, checked, onChange }) {
      if (ui.ToggleField) return h(Row, null, h(ui.ToggleField, { label, description, checked: Boolean(checked), onChange }));
      return h(Row, null, h(Button, { onClick: () => onChange(!checked) }, `${label}: ${checked ? "On" : "Off"}`));
    }

    function Choice({ label, description, value, options, onChange }) {
      const rgOptions = options.map(([, optionLabel], index) => ({ data: index, label: optionLabel }));
      let selected = options.findIndex(([data]) => data === value);
      if (selected < 0) {
        // Closest option for values set elsewhere.
        selected = options.reduce((best, [data], index) =>
          Math.abs(Number(data) - Number(value)) < Math.abs(Number(options[best][0]) - Number(value)) ? index : best, 0);
      }
      const select = (option) => onChange(options[Number(option?.data ?? 0)][0]);
      if (DropdownItem) {
        return h(Row, null, h(DropdownItem, {
          label, description, rgOptions, selectedOption: selected, onChange: select, menuLabel: label,
          layout: "below", childrenContainerWidth: "max", contextMenuPositionOptions: { bMatchWidth: false },
        }));
      }
      if (ui.Dropdown) {
        return h(Row, null, h("div", { style: { display: "flex", alignItems: "center", justifyContent: "space-between", gap: "24px", width: "100%" } },
          h("div", { style: { minWidth: 0 } }, label),
          h("div", { style: { flex: "0 0 300px", maxWidth: "50%" } }, h(ui.Dropdown, { rgOptions, selectedOption: selected, onChange: select, menuLabel: label }))));
      }
      return h(Row, null, h(Button, { onClick: () => select({ data: (selected + 1) % options.length }) }, `${label}: ${options[selected][1]}`));
    }

    function TextInput({ label, value, onChange }) {
      if (ui.TextField) return h(Row, null, h(ui.TextField, { label, value, onChange: (event) => onChange(String(event?.target?.value ?? "")) }));
      return h(Row, null, h("input", { className: "gpfocus Focusable", value, placeholder: label, onChange: (event) => onChange(event.target.value), style: { width: "100%" } }));
    }

    function Heading({ children }) {
      return h("div", {
        style: { margin: "24px 0 6px", fontSize: "13px", fontWeight: 700, letterSpacing: ".08em", textTransform: "uppercase", color: "rgba(255,255,255,.55)" },
      }, children);
    }

    function Note({ children, tone }) {
      return h("div", { style: { opacity: tone === "error" ? 1 : 0.65, color: tone === "error" ? "#ff9a9a" : undefined, fontSize: "14px", margin: "6px 0", lineHeight: 1.4 } }, children);
    }

    function trackLabel(track) {
      const parts = [track.title];
      const duration = formatDuration(track.durationSeconds);
      if (duration) parts.push(duration);
      if (track.kind === "local") parts.push("Your file");
      else if (track.channel) parts.push(track.channel);
      return parts.join(" · ");
    }

    /** One audio preview at a time, stopped when the page closes. */
    function usePreview() {
      const ref = React.useRef(null);
      const [playingId, setPlayingId] = React.useState("");
      const stop = React.useCallback(() => {
        try { ref.current?.pause(); ref.current?.removeAttribute("src"); ref.current?.load(); } catch {}
        ref.current = null;
        setPlayingId("");
      }, []);
      React.useEffect(() => stop, [stop]);
      const toggle = (track, startSeconds, volume, view) => {
        if (playingId === track.id) { stop(); return; }
        stop();
        const AudioType = view?.Audio || window.Audio;
        const audio = new AudioType(audioUrl(track.audioPath));
        audio.volume = targetVolume(state.settings?.masterVolume ?? 60, volume, false);
        audio.addEventListener("loadedmetadata", () => {
          if (startSeconds > 0 && audio.duration > startSeconds + 5) { try { audio.currentTime = startSeconds; } catch {} }
        }, { once: true });
        audio.addEventListener("ended", stop, { once: true });
        void audio.play().catch(() => stop());
        ref.current = audio;
        setPlayingId(track.id);
      };
      return { playingId, toggle, stop };
    }

    function YoutubeSearch({ playlistKey, title, defaultTerm, youtube, onChanged, onInstall }) {
      const [term, setTerm] = React.useState(defaultTerm);
      const [results, setResults] = React.useState(null);
      const [busy, setBusy] = React.useState("");
      const [error, setError] = React.useState("");
      const firstTime = !youtube?.installed;
      const search = async () => {
        setBusy("search");
        setError("");
        const { ok, payload } = await api(`/api/overture/youtube/search?term=${encodeURIComponent(term)}`).catch(() => ({ ok: false }));
        setResults(ok ? payload?.items || [] : []);
        if (!ok) setError(payload?.message || "The search failed.");
        setBusy("");
      };
      const add = async (item) => {
        setBusy(item.videoId);
        setError("");
        const { ok, payload } = await api("/api/overture/youtube/add", {
          key: playlistKey, title, videoId: item.videoId, videoTitle: item.title, channel: item.channel, durationSeconds: item.durationSeconds,
        }).catch(() => ({ ok: false }));
        setBusy("");
        if (ok && payload) onChanged(payload);
        else setError(payload?.message || "The download failed.");
      };
      return h(React.Fragment, null,
        h(TextInput, { label: "Search YouTube", value: term, onChange: setTerm }),
        h(ButtonRow, null, h(Button, { onClick: search, disabled: Boolean(busy) || term.trim().length < 2 },
          busy === "search" ? (firstTime ? "Setting up YouTube support…" : "Searching…") : "Search")),
        firstTime && busy === "search" ? h(Note, null, "The first search downloads yt-dlp and Deno from their official releases. This takes about a minute.") : null,
        error ? h(Note, { tone: "error" }, error) : null,
        error && youtube?.installed ? h(ButtonRow, null, h(Button, { onClick: onInstall }, "Repair YouTube support")) : null,
        ...(results || []).map((item) => h(ButtonRow, { key: `yt-${item.videoId}` },
          h(Button, { grow: true, onClick: () => add(item), disabled: Boolean(busy) },
            busy === item.videoId ? `Downloading ${item.title}…` : `Add: ${trackLabel({ ...item, kind: "youtube" })}`))),
        results && !results.length && !error ? h(Note, null, "Nothing found.") : null);
    }

    function LocalFiles({ playlistKey, title, folders, onChanged }) {
      const [term, setTerm] = React.useState("");
      const [items, setItems] = React.useState(null);
      const [error, setError] = React.useState("");
      const load = async () => {
        setError("");
        const { ok, payload } = await api(`/api/overture/local?term=${encodeURIComponent(term)}`).catch(() => ({ ok: false }));
        setItems(ok ? payload?.items || [] : []);
        if (!ok) setError(payload?.message || "The music folders could not be read.");
      };
      const add = async (item) => {
        const { ok, payload } = await api("/api/overture/local/add", { key: playlistKey, title, path: item.path }).catch(() => ({ ok: false }));
        if (ok && payload) onChanged(payload);
        else setError(payload?.message || "The file could not be added.");
      };
      return h(React.Fragment, null,
        h(Note, null, `Audio files from: ${(folders || []).join(", ") || "your Music folder"}`),
        h(TextInput, { label: "Filter by name", value: term, onChange: setTerm }),
        h(ButtonRow, null, h(Button, { onClick: load }, items ? "Search again" : "Show music files")),
        error ? h(Note, { tone: "error" }, error) : null,
        ...(items || []).map((item) => h(ButtonRow, { key: `local-${item.path}` },
          h(Button, { grow: true, onClick: () => add(item) }, `${item.assigned ? "✓ " : ""}Add: ${item.name}`))),
        items && !items.length ? h(Note, null, "No music files found.") : null);
    }

    function PlaylistEditor({ playlistKey, title, youtube, onInstall, onSettingsChanged }) {
      const [playlist, setPlaylist] = React.useState(null);
      const [error, setError] = React.useState("");
      const preview = usePreview();
      const viewRef = React.useRef(null);
      const isAmbient = playlistKey === "ambient";

      const apply = (payload) => {
        setPlaylist(payload);
        if (payload?.settings) onSettingsChanged?.(payload.settings);
        state.settingsLoadedAt = 0;
      };

      React.useEffect(() => {
        let alive = true;
        api(playlistQuery(playlistKey)).then(({ ok, payload }) => {
          if (!alive) return;
          if (ok && payload) setPlaylist(payload);
          else setError(payload?.message || "TFS Overture is not available.");
        }).catch(() => alive && setError("TFS Overture is not available."));
        return () => { alive = false; };
      }, [playlistKey]);

      const save = async (update) => {
        const { ok, payload } = await api("/api/overture/playlist", { key: playlistKey, title, update }).catch(() => ({ ok: false }));
        if (ok && payload) apply(payload);
      };
      const remove = async (track) => {
        if (preview.playingId === track.id) preview.stop();
        const { ok, payload } = await api("/api/overture/playlist/remove", { key: playlistKey, title, trackId: track.id }).catch(() => ({ ok: false }));
        if (ok && payload) apply(payload);
      };
      const move = (index, delta) => {
        const ids = playlist.tracks.map((track) => track.id);
        const target = index + delta;
        if (target < 0 || target >= ids.length) return;
        [ids[index], ids[target]] = [ids[target], ids[index]];
        void save({ trackIds: ids });
      };
      const reset = async () => {
        preview.stop();
        const { ok, payload } = await api("/api/overture/playlist/reset", { key: playlistKey, title }).catch(() => ({ ok: false }));
        if (ok && payload) apply(payload);
      };

      if (error) return h(Note, { tone: "error" }, error);
      if (!playlist) return h(Note, null, "Loading…");
      const view = viewRef.current?.ownerDocument?.defaultView;

      return h("div", { ref: viewRef, style: { minWidth: 0 } },
        isAmbient ? null : h(Toggle, {
          label: "Play music for this game",
          checked: !playlist.silent,
          onChange: (on) => save(on ? { clearMode: true } : { mode: "off" }),
        }),
        h(Heading, null, isAmbient ? "Ambient playlist" : "Playlist"),
        playlist.message ? h(Note, null, playlist.message) : null,
        playlist.tracks.length
          ? playlist.tracks.map((track, index) => h(ButtonRow, { key: track.id },
            h(Button, { grow: true, onClick: () => preview.toggle(track, playlist.startSeconds, playlist.volume, view), disabled: track.missing },
              `${preview.playingId === track.id ? "■ " : "▶ "}${index + 1}. ${trackLabel(track)}${track.missing ? " (file missing)" : ""}`),
            h(Button, { onClick: () => move(index, -1), disabled: index === 0 }, "Up"),
            h(Button, { onClick: () => move(index, 1), disabled: index === playlist.tracks.length - 1 }, "Down"),
            h(Button, { onClick: () => remove(track) }, "Remove")))
          : h(Note, null, isAmbient
            ? "No ambient music yet. Add some below."
            : playlist.canAutoFind
              ? "No music yet. TFS looks for the theme on YouTube the next time the game page opens, or choose one below."
              : "No music yet. Choose a theme below."),
        playlist.source === "auto" && playlist.tracks.length ? h(Note, null, "Found automatically. Remove it or add your own music if it does not fit.") : null,
        h(Choice, { label: "Volume", value: playlist.volume, options: volumeOptions, onChange: (volume) => save({ volume }) }),
        h(Choice, { label: "Skip the start of each track", value: Math.round(playlist.startSeconds || 0), options: startOptions, onChange: (startSeconds) => save({ startSeconds }) }),
        h(Toggle, { label: "Repeat the playlist", checked: playlist.loop, onChange: (loop) => save({ loop }) }),
        h(Heading, null, "Find on YouTube"),
        h(YoutubeSearch, {
          playlistKey, title, youtube, onInstall, onChanged: apply,
          defaultTerm: isAmbient ? "calm game menu music" : `${title || ""} main theme`.trim(),
        }),
        h(Heading, null, "Your music files"),
        h(LocalFiles, { playlistKey, title, folders: playlist.settings?.effectiveMusicFolders, onChanged: apply }),
        h(ButtonRow, null, h(Button, { onClick: reset }, isAmbient ? "Clear ambient music" : "Reset this game")));
    }

    function FolderSettings({ settings, onSave }) {
      const [path, setPath] = React.useState("");
      const folders = settings.musicFolders || [];
      return h(React.Fragment, null,
        h(Note, null, folders.length
          ? "TFS offers audio files from these folders (and its own Overture music folder)."
          : `Using your Music folder: ${(settings.effectiveMusicFolders || [])[0] || "not found"}`),
        ...folders.map((folder) => h(ButtonRow, { key: folder },
          h("div", { style: { flex: "1 1 auto", minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" } }, folder),
          h(Button, { onClick: () => onSave({ musicFolders: folders.filter((item) => item !== folder) }) }, "Remove"))),
        h(TextInput, { label: "Add a folder (full path, for example D:\\Music)", value: path, onChange: setPath }),
        h(ButtonRow, null, h(Button, {
          disabled: !/^([a-zA-Z]:\\|\\\\)/.test(path.trim()),
          onClick: () => { onSave({ musicFolders: [...folders, path.trim()] }); setPath(""); },
        }, "Add folder")));
    }

    function Storage() {
      const [storage, setStorage] = React.useState(null);
      const [confirm, setConfirm] = React.useState(false);
      const [error, setError] = React.useState("");
      const load = React.useCallback(async () => {
        const { ok, payload } = await api("/api/overture/storage").catch(() => ({ ok: false }));
        if (ok) setStorage(payload);
      }, []);
      React.useEffect(() => { void load(); }, [load]);
      const mb = (bytes) => `${(Number(bytes || 0) / 1048576).toFixed(1)} MB`;
      const clean = async () => {
        if (!confirm) { setConfirm(true); return; }
        setConfirm(false);
        const { ok, payload } = await api("/api/overture/storage/clean", {}).catch(() => ({ ok: false }));
        if (ok) { setStorage(payload); setError(""); } else setError(payload?.message || "Nothing was deleted.");
      };
      if (!storage) return h(Note, null, "Loading…");
      return h(React.Fragment, null,
        h(Note, null, `Downloaded themes: ${storage.downloadCount} (${mb(storage.downloadBytes)}). Not used by any game: ${storage.unusedCount} (${mb(storage.unusedBytes)}).`),
        error ? h(Note, { tone: "error" }, error) : null,
        h(ButtonRow, null, h(Button, { onClick: clean, disabled: !storage.unusedCount },
          confirm ? `Press again to delete ${storage.unusedCount} unused download(s)` : "Delete unused downloads")),
        h(Note, null, "Only themes TFS downloaded are deleted. Your own music files are never touched."));
    }

    function prefetchText(prefetch) {
      if (prefetch?.running) return `Preparing themes for your recently played games: ${prefetch.done} of ${prefetch.total}…`;
      if (prefetch?.total) return `Themes prepared for recently played games: ${prefetch.found} new of ${prefetch.total} checked.${prefetch.message ? ` ${prefetch.message}` : ""}`;
      return "Themes for your 30 most recently played installed games are prepared in the background.";
    }

    function GlobalSettings({ settings, onSave, onInstall }) {
      const youtube = settings.youtube || {};
      return [
        h(Heading, { key: "global-heading" }, "All games"),
        h(Toggle, { key: "enabled", label: "Play music on game pages", description: "Fades in when a game page opens and stops when a game starts.", checked: settings.enabled, onChange: (enabled) => onSave({ enabled }) }),
        h(Choice, { key: "master", label: "Music volume", value: settings.masterVolume, options: masterOptions, onChange: (masterVolume) => onSave({ masterVolume }) }),
        h(Choice, { key: "delay", label: "Start after", value: settings.delaySeconds, options: delayOptions, onChange: (delaySeconds) => onSave({ delaySeconds }) }),
        h(Choice, { key: "fade", label: "Fade in and out", value: settings.fadeSeconds, options: fadeOptions, onChange: (fadeSeconds) => onSave({ fadeSeconds }) }),
        h(Toggle, { key: "auto", label: "Find themes automatically", description: "Looks on YouTube when a game page has no music yet and only takes a result that names the game and its theme.", checked: settings.autoFind, onChange: (autoFind) => onSave({ autoFind }) }),
        h(Toggle, { key: "chip", label: "Show what is playing", checked: settings.showNowPlaying, onChange: (showNowPlaying) => onSave({ showNowPlaying }) }),
        h(Toggle, { key: "trailers", label: "Quiet while a trailer has sound", checked: settings.yieldToTrailers, onChange: (yieldToTrailers) => onSave({ yieldToTrailers }) }),
        h(Toggle, { key: "ambient", label: "Ambient music on other pages", description: "Plays the ambient playlist below on Home, the library and other Big Picture pages.", checked: settings.ambientEnabled, onChange: (ambientEnabled) => onSave({ ambientEnabled }) }),
        h(Heading, { key: "youtube-heading" }, "YouTube support"),
        h(Note, { key: "youtube-state", tone: youtube.state === "failed" ? "error" : undefined },
          youtube.state === "installing"
            ? "Setting up yt-dlp and Deno…"
            : youtube.installed
              ? `Ready${youtube.installedAt ? ` (updated ${new Date(youtube.installedAt).toLocaleDateString()})` : ""}${youtube.hasJsRuntime ? "" : ", without Deno"}. Updates itself every two weeks.${youtube.message ? ` ${youtube.message}` : ""}`
              : youtube.message || "Sets itself up the first time a theme is needed (yt-dlp and Deno from their official releases, checked by checksum)."),
        h(ButtonRow, { key: "youtube-action" }, h(Button, { onClick: onInstall, disabled: youtube.state === "installing" },
          youtube.state === "installing" ? "Setting up…" : youtube.installed ? "Repair YouTube support" : "Set up now")),
        h(Note, { key: "prefetch" }, prefetchText(settings.prefetch)),
        h(Heading, { key: "folders-heading" }, "Music folders"),
        h(FolderSettings, { key: "folders", settings, onSave }),
        h(Heading, { key: "storage-heading" }, "Storage"),
        h(Storage, { key: "storage" }),
      ];
    }

    return function TfsOvertureEditor(props) {
      const appId = Number(props?.match?.params?.appId || 0) || 0;
      const title = appId ? gameTitle(appId) || `App ${appId}` : "";
      const [settings, setSettings] = React.useState(null);
      const [error, setError] = React.useState("");

      React.useEffect(() => {
        let alive = true;
        loadSettings(true).then((value) => {
          if (!alive) return;
          if (!value || value.unavailable) setError("TFS Overture is not available. Turn it on in the Plugin Activator.");
          else setSettings(value);
        });
        return () => { alive = false; };
      }, [appId]);

      // Follow the YouTube installation while it runs.
      const installing = settings?.youtube?.state === "installing" || settings?.prefetch?.running === true;
      React.useEffect(() => {
        if (!installing) return undefined;
        const timer = window.setInterval(async () => {
          const value = await loadSettings(true);
          if (value && !value.unavailable) setSettings(value);
        }, 2000);
        return () => window.clearInterval(timer);
      }, [installing]);

      const saveSettings = async (patch) => {
        setSettings((current) => ({ ...current, ...patch }));
        const { ok, payload } = await api("/api/overture/settings", patch).catch(() => ({ ok: false }));
        if (ok && payload) {
          state.settings = payload;
          state.settingsLoadedAt = Date.now();
          setSettings(payload);
        }
      };

      const install = async () => {
        const { ok, payload } = await api("/api/overture/youtube/install", {}).catch(() => ({ ok: false }));
        if (ok && payload) setSettings((current) => current && { ...current, youtube: payload });
      };

      const onSettingsChanged = (value) => {
        state.settings = value;
        state.settingsLoadedAt = Date.now();
        setSettings(value);
      };

      const page = { padding: "56px 36px 40px", height: "100%", boxSizing: "border-box", overflowY: "auto", color: "#fff" };
      if (error && !settings) return h("div", { style: page }, h("h2", null, "TFS Overture"), h("p", null, error));
      if (!settings) return h("div", { style: page }, h("h2", null, "TFS Overture"), h("p", { style: { opacity: 0.7 } }, "Loading…"));

      const column = (children) => (Focusable
        ? h(Focusable, { "flow-children": "column", style: { minWidth: 0 } }, ...children)
        : h("div", { style: { minWidth: 0 } }, ...children));
      const playlistKey = appId ? String(appId) : "ambient";
      const editor = h(PlaylistEditor, {
        key: `playlist-${playlistKey}`, playlistKey, title, youtube: settings.youtube, onInstall: install, onSettingsChanged,
      });
      const global = GlobalSettings({ settings, onSave: saveSettings, onInstall: install });

      return h("div", { style: page },
        h("div", { style: { display: "flex", alignItems: "baseline", gap: "14px", marginBottom: "6px" } },
          h("h2", { style: { margin: 0, fontSize: "26px" } }, "TFS Overture"),
          title ? h("div", { style: { opacity: 0.6, fontSize: "16px" } }, title) : null),
        h("div", { style: { opacity: 0.65, fontSize: "14px", marginBottom: "16px", maxWidth: "980px" } },
          "Theme music for game pages. Changes save automatically."),
        // Both columns sit in one row-flowing focus group, so left/right on the
        // D-pad moves between them (a plain div is not a focus group).
        h(Focusable || "div", {
          ...(Focusable ? { "flow-children": "row" } : {}),
          style: { display: "grid", gridTemplateColumns: "minmax(0, 1.1fr) minmax(0, 1fr)", gap: "36px", alignItems: "start" },
        },
          column(appId ? [h(Heading, { key: "game-heading" }, title), editor] : global),
          column(appId ? global : [h(Heading, { key: "ambient-heading" }, "Ambient music"), editor])));
    };
  }

  async function registerEditorRoutes() {
    window.clearTimeout(state.routeTimer);
    if (state.destroyed || state.routeRemovers.length) return;
    const core = window.__tfs;
    if (!core?.routes?.add) {
      state.routeTimer = window.setTimeout(registerEditorRoutes, 2000);
      return;
    }
    try {
      await core.whenReady;
      if (state.destroyed || state.routeRemovers.length || !core.React) return;
      const editor = createEditor(core);
      for (const route of editorRoutes) {
        if (core.routes.has?.(route)) core.routes.remove(route);
        state.routeRemovers.push(core.routes.add(route, editor, { name: "TFS Overture", exact: true }));
      }
    } catch (error) {
      console.warn("[TFS Overture] editor route failed", error);
      state.routeTimer = window.setTimeout(registerEditorRoutes, 10000);
    }
  }

  function openEditor(appId) {
    const id = Number(appId) || 0;
    const routes = window.__tfs?.routes;
    if (!routes?.navigate || !state.routeRemovers.length) return false;
    stopSession(true);
    routes.navigate(id ? `/tfs/overture/${id}` : "/tfs/overture");
    return true;
  }

  // -----------------------------------------------------------------------
  // Lifecycle
  // -----------------------------------------------------------------------

  function onAppLifetime(notification) {
    const appId = Number(notification?.unAppID) || 0;
    if (notification?.bRunning) {
      state.runningApps.add(appId);
      stopSession();
      return;
    }
    state.runningApps.delete(appId);
    // Back in Big Picture after playing: pick the music for the current page again.
    if (!isGameRunning()) evaluate(true);
  }

  /**
   * Prepares themes for the recently played installed games in the
   * background: shortly after start, then every few hours. The backend works
   * through them one by one and skips games that already have music.
   */
  function schedulePrefetch(delayMs) {
    window.clearTimeout(state.prefetchTimer);
    if (state.destroyed) return;
    state.prefetchTimer = window.setTimeout(async () => {
      if (state.destroyed) return;
      const settings = await loadSettings(true);
      if (!settings?.enabled || !settings.autoFind || isGameRunning()) {
        schedulePrefetch(isGameRunning() ? prefetchRetryMs : prefetchEveryMs);
        return;
      }
      const games = recentInstalledGames(window.collectionStore?.allGamesCollection?.allApps, prefetchCount);
      if (games.length) {
        try { await api("/api/overture/prefetch", { games }); } catch {}
      }
      schedulePrefetch(games.length ? prefetchEveryMs : prefetchRetryMs);
    }, delayMs);
  }

  function seedRunningApps() {
    try {
      const running = window.SteamUIStore?.RunningApps;
      if (Array.isArray(running)) for (const app of running) state.runningApps.add(Number(app?.appid) || 0);
    } catch {}
  }

  function refresh() {
    state.settingsLoadedAt = 0;
    void loadSettings(true).then(() => evaluate(true));
    if (!state.routeRemovers.length) void registerEditorRoutes();
    if (!state.history) watchHistory();
  }

  function destroy() {
    state.destroyed = true;
    stopSession(true);
    window.clearTimeout(state.historyTimer);
    window.clearTimeout(state.routeTimer);
    window.clearTimeout(state.prefetchTimer);
    release(state.historyUnlisten);
    state.historyUnlisten = null;
    state.history = null;
    state.registrations.splice(0).forEach(release);
    state.routeRemovers.splice(0).forEach((remove) => { try { remove(); } catch {} });
    try { bigPictureWindow()?.document?.getElementById(styleId)?.remove(); } catch {}
  }

  window.__tfsOverture = {
    version,
    refresh,
    destroy,
    openEditor,
    hasEditor: () => state.routeRemovers.length > 0 && state.settings?.unavailable !== true,
    isEnabled: () => state.settings?.enabled === true,
    isGameEnabled: (appId) => state.settings?.enabled === true && !(state.settings.silentAppIds || []).includes(Number(appId)),
    hasMusic: (appId) => (state.settings?.appIdsWithMusic || []).includes(Number(appId)),
    stop: () => stopSession(),
    get session() {
      const session = state.session;
      if (!session) return null;
      const track = session.tracks[session.index];
      return { key: session.key, index: session.index, title: track?.title || "", paused: session.audio.paused, volume: session.audio.volume };
    },
    internals,
  };

  try {
    state.registrations.push(window.SteamClient?.GameSessions?.RegisterForAppLifetimeNotifications?.(onAppLifetime));
  } catch {}
  seedRunningApps();
  void loadSettings(true).then(() => evaluate(true));
  void registerEditorRoutes();
  watchHistory();
  schedulePrefetch(prefetchFirstMs);
  return "injected";
})();
