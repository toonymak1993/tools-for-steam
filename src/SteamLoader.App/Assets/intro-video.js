// TFS controller intro video (SharedJSContext).
//
// A Big Picture route (/tfs/intro) that plays the short controller tutorial
// (tap View = Steam menu, hold View = Quick Access with TFS, Plugin Activator,
// display/power settings, in-game hold). It opens by itself once after TFS was
// installed, as soon as Big Picture is up and no game runs; leaving the page or
// reaching the end marks it as seen. Settings > Controller Intro replays it
// through window.__tfsIntro.open().
(() => {
  const version = 1;
  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const introRoute = "/tfs/intro";

  const previous = window.__tfsIntro;
  if (previous?.version === version) return;
  try { previous?.destroy?.(); } catch {}

  const state = {
    routeRemover: null,
    routeTimer: 0,
    autoTimer: 0,
    autoAttempts: 0,
    autoDone: false,
    defaultLanguage: "en",
    steamLanguage: "",
    destroyed: false,
  };

  const TEXT = {
    en: { title: "Controller intro", pause: "Pause", play: "Play", replay: "Watch again", restart: "Restart", close: "Close", failed: "The intro video could not be loaded.", hint: "B closes the video. You can watch it again in TFS Settings." },
    de: { title: "Controller-Einführung", pause: "Pause", play: "Abspielen", replay: "Nochmal ansehen", restart: "Neu starten", close: "Schließen", failed: "Das Intro-Video konnte nicht geladen werden.", hint: "B schließt das Video. Du findest es jederzeit in den TFS-Einstellungen." },
  };

  // -----------------------------------------------------------------------
  // Backend
  // -----------------------------------------------------------------------

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  async function api(path, method = "GET") {
    const response = await fetch(`${apiBase()}${String(path).replace(/^\//, "")}`, { method, cache: "no-store" });
    const payload = await response.json().catch(() => null);
    return { ok: response.ok, payload };
  }

  /** <video> cannot send headers, so the session token travels in the query. */
  function videoUrl(language) {
    const path = `api/intro/video?lang=${encodeURIComponent(language)}`;
    try {
      if (typeof window.__steamLoaderApiUrl === "function") return window.__steamLoaderApiUrl(path);
    } catch {}
    const url = new URL(path, apiBase());
    if (window.__steamLoaderApiToken) url.searchParams.set("tfsSession", window.__steamLoaderApiToken);
    return url.toString();
  }

  function markSeen() {
    void api("/api/intro/seen", "POST").catch(() => {});
  }

  function loadSteamLanguage() {
    try {
      Promise.resolve(window.SteamClient?.Settings?.GetCurrentLanguage?.()).then((language) => {
        if (typeof language === "string") state.steamLanguage = language.toLowerCase();
      }).catch(() => {});
    } catch {}
  }

  /** Steam's own language decides; Windows' language only when Steam does not answer. */
  function resolveLanguage() {
    if (state.steamLanguage) return state.steamLanguage === "german" ? "de" : "en";
    return state.defaultLanguage === "de" ? "de" : "en";
  }

  function isGameRunning() {
    try {
      const running = window.SteamUIStore?.RunningApps;
      return Array.isArray(running) && running.length > 0;
    } catch {
      return false;
    }
  }

  // -----------------------------------------------------------------------
  // Page
  // -----------------------------------------------------------------------

  function createPage(core) {
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};

    function Button({ onClick, children, autoFocus }) {
      const style = { flex: "none", width: "auto", minWidth: "150px", margin: 0 };
      if (ui.DialogButton) return h(ui.DialogButton, { onClick, autoFocus, style }, children);
      return h("button", { type: "button", className: "DialogButton gpfocus Focusable", onClick, autoFocus, tabIndex: 0, style }, children);
    }

    return function TfsIntroPage() {
      const videoRef = React.useRef(null);
      const hideTimer = React.useRef(0);
      const [language] = React.useState(resolveLanguage);
      const [playing, setPlaying] = React.useState(false);
      const [ended, setEnded] = React.useState(false);
      const [failed, setFailed] = React.useState(false);
      const [barVisible, setBarVisible] = React.useState(true);
      const text = TEXT[language] || TEXT.en;

      const revealBar = React.useCallback(() => {
        setBarVisible(true);
        window.clearTimeout(hideTimer.current);
        hideTimer.current = window.setTimeout(() => setBarVisible(false), 3500);
      }, []);

      React.useEffect(() => {
        const video = videoRef.current;
        if (video) {
          const attempt = video.play();
          if (attempt && typeof attempt.catch === "function") attempt.catch(() => setPlaying(false));
        }
        revealBar();
        // Leaving the page in any way (B, Guide, Settings) counts as seen.
        return () => {
          window.clearTimeout(hideTimer.current);
          try { videoRef.current?.pause(); } catch {}
          markSeen();
        };
      }, [revealBar]);

      const togglePlay = () => {
        const video = videoRef.current;
        if (!video) return;
        if (ended) {
          video.currentTime = 0;
          setEnded(false);
        }
        if (video.paused) void video.play().catch(() => {});
        else video.pause();
        revealBar();
      };
      const restart = () => {
        const video = videoRef.current;
        if (!video) return;
        video.currentTime = 0;
        setEnded(false);
        void video.play().catch(() => {});
        revealBar();
      };
      const close = () => {
        markSeen();
        if (!core.routes?.back?.()) {
          try { window.history.back(); } catch {}
        }
      };

      const showBar = barVisible || !playing || ended || failed;
      const Focusable = ui.Focusable;
      const buttons = [
        h(Button, { key: "play", onClick: togglePlay, autoFocus: true }, ended ? text.replay : playing ? text.pause : text.play),
        h(Button, { key: "restart", onClick: restart }, text.restart),
        h(Button, { key: "close", onClick: close }, text.close),
      ];
      const barStyle = {
        position: "absolute", left: 0, right: 0, bottom: 0,
        display: "flex", alignItems: "center", gap: "12px", padding: "18px 36px 22px",
        background: "linear-gradient(to top, rgba(0,0,0,0.85), rgba(0,0,0,0))",
        opacity: showBar ? 1 : 0, transition: "opacity 240ms ease",
      };

      return h("div", {
        style: { position: "relative", width: "100%", height: "100%", background: "#000", overflow: "hidden", color: "#fff" },
      },
        h("video", {
          ref: videoRef,
          src: videoUrl(language),
          autoPlay: true,
          playsInline: true,
          preload: "auto",
          style: { position: "absolute", inset: 0, width: "100%", height: "100%", objectFit: "contain", background: "#000" },
          onPlay: () => { setPlaying(true); setEnded(false); },
          onPause: () => setPlaying(false),
          onEnded: () => { setPlaying(false); setEnded(true); setBarVisible(true); markSeen(); },
          onError: () => setFailed(true),
        }),
        failed
          ? h("div", { style: { position: "absolute", inset: 0, display: "flex", alignItems: "center", justifyContent: "center", fontSize: "22px", opacity: 0.85 } }, text.failed)
          : null,
        h("div", { style: barStyle },
          Focusable
            ? h(Focusable, {
                "flow-children": "row",
                style: { display: "flex", gap: "12px" },
                onButtonDown: revealBar,
                onGamepadDirection: revealBar,
              }, ...buttons)
            : h("div", { style: { display: "flex", gap: "12px" } }, ...buttons),
          h("div", { style: { marginLeft: "auto", fontSize: "15px", opacity: 0.75, textAlign: "right" } }, text.hint)));
    };
  }

  async function registerRoute() {
    window.clearTimeout(state.routeTimer);
    if (state.destroyed || state.routeRemover) return;
    const core = window.__tfs;
    if (!core?.routes?.add) {
      state.routeTimer = window.setTimeout(registerRoute, 2000);
      return;
    }
    try {
      await core.whenReady;
      if (state.destroyed || state.routeRemover || !core.React) return;
      if (core.routes.has?.(introRoute)) core.routes.remove(introRoute);
      state.routeRemover = core.routes.add(introRoute, createPage(core), { name: "TFS Intro", exact: true });
      scheduleAutoOpen(4000);
    } catch (error) {
      console.warn("[TFS Intro] route failed", error);
      state.routeTimer = window.setTimeout(registerRoute, 10000);
    }
  }

  function open() {
    const routes = window.__tfs?.routes;
    if (!state.routeRemover || typeof routes?.navigate !== "function") return false;
    return routes.navigate(introRoute) !== false;
  }

  // -----------------------------------------------------------------------
  // First start after installation
  // -----------------------------------------------------------------------

  function scheduleAutoOpen(delay) {
    window.clearTimeout(state.autoTimer);
    if (state.destroyed || state.autoDone) return;
    state.autoTimer = window.setTimeout(tryAutoOpen, delay);
  }

  async function tryAutoOpen() {
    if (state.destroyed || state.autoDone) return;
    state.autoAttempts += 1;
    // Quick checks for the first minutes, then a slow heartbeat until Big
    // Picture shows up (TFS may start long before the user opens it).
    const nextDelay = state.autoAttempts < 40 ? 3000 : 15000;
    let pending = false;
    try {
      const { ok, payload } = await api("/api/intro/state");
      if (!ok) { scheduleAutoOpen(nextDelay); return; }
      pending = payload?.pending === true;
      if (payload?.defaultLanguage) state.defaultLanguage = String(payload.defaultLanguage);
    } catch {
      scheduleAutoOpen(nextDelay);
      return;
    }
    if (!pending) { state.autoDone = true; return; }
    if (isGameRunning() || !open()) { scheduleAutoOpen(nextDelay); return; }
    state.autoDone = true;
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.routeTimer);
    window.clearTimeout(state.autoTimer);
    try { state.routeRemover?.(); } catch {}
    state.routeRemover = null;
    if (window.__tfsIntro?.version === version) delete window.__tfsIntro;
  }

  window.__tfsIntro = {
    version,
    destroy,
    open,
    get ready() { return Boolean(state.routeRemover); },
  };

  loadSteamLanguage();
  void registerRoute();
})();
