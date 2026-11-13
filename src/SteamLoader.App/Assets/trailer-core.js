// TFS Trailers runtime (SharedJSContext).
//
// Plays a game's Steam trailer inside the header (hero) of the Big Picture
// game page, a few seconds after the page opens:
//  - follows Big Picture's own router history, so only the real game details
//    page (/library/app/<id>) plays, never Home, grids or sub pages;
//  - streams Steam's DASH trailer straight from Steam's CDN with a small own
//    Media Source player (no ffmpeg, yt-dlp or extra downloads);
//  - fades in over Steam's header artwork, optionally moves the logo to the
//    lower left, X toggles sound, everything is restored when leaving;
//  - registers the per-game page /tfs/trailers/:appId (and /tfs/trailers for
//    the global settings) built from Steam's own components (window.__tfs.ui).
//
// Inspired by TrailerHero for Playhub (MIT, LoZazaMastro); independent code.
(() => {
  const version = 8;

  // -----------------------------------------------------------------------
  // Pure helpers (also used by tests through window.__tfsTrailerTestHooks)
  // -----------------------------------------------------------------------

  /** "PT1M25.5S" → 85.5 seconds; unknown text → 0. */
  function parseIsoDuration(text) {
    const match = /^P(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$/.exec(String(text || "").trim());
    if (!match) return 0;
    const [, d, h, m, s] = match.map((part) => Number(part || 0));
    return d * 86400 + h * 3600 + m * 60 + s;
  }

  function readAttributes(tag) {
    const attributes = {};
    const pattern = /([\w:-]+)\s*=\s*("([^"]*)"|'([^']*)')/g;
    let match;
    while ((match = pattern.exec(tag))) attributes[match[1]] = match[3] ?? match[4] ?? "";
    return attributes;
  }

  function readTemplate(block) {
    const match = /<SegmentTemplate\b([^>]*)>/i.exec(block);
    if (!match) return null;
    const a = readAttributes(match[1]);
    if (!a.media || !a.initialization || !Number(a.duration)) return null;
    return {
      timescale: Number(a.timescale) || 1,
      duration: Number(a.duration),
      initialization: a.initialization,
      media: a.media,
      startNumber: Number.isFinite(Number(a.startNumber)) && a.startNumber !== "" ? Number(a.startNumber) : 1,
    };
  }

  /**
   * Reads the parts of a static, SegmentTemplate based MPD that Steam uses:
   * one period, a video and an audio adaptation set, numbered segments.
   */
  function parseMpd(xml) {
    const text = String(xml || "");
    const mpdTag = /<MPD\b([^>]*)>/i.exec(text);
    if (!mpdTag) return null;
    const mpd = readAttributes(mpdTag[1]);
    if (mpd.type && mpd.type !== "static") return null;
    const result = { duration: parseIsoDuration(mpd.mediaPresentationDuration), video: [], audio: [] };
    const setPattern = /<AdaptationSet\b([^>]*)>([\s\S]*?)<\/AdaptationSet>/gi;
    let set;
    while ((set = setPattern.exec(text))) {
      const setAttributes = readAttributes(set[1]);
      const setBody = set[2];
      const setTemplate = readTemplate(setBody.replace(/<Representation\b[\s\S]*?(<\/Representation>|\/>)/gi, ""));
      const representationPattern = /<Representation\b([^>]*?)(\/>|>([\s\S]*?)<\/Representation>)/gi;
      let representation;
      while ((representation = representationPattern.exec(setBody))) {
        const a = readAttributes(representation[1]);
        const mimeType = a.mimeType || setAttributes.mimeType || "";
        const contentType = setAttributes.contentType || mimeType.split("/")[0];
        const template = readTemplate(representation[3] || "") || setTemplate;
        if (!template || !a.id || !/^(video|audio)\/mp4$/i.test(mimeType)) continue;
        const entry = {
          id: a.id,
          mimeType,
          codecs: a.codecs || setAttributes.codecs || "",
          bandwidth: Number(a.bandwidth) || 0,
          width: Number(a.width) || 0,
          height: Number(a.height) || 0,
          template,
        };
        if (contentType === "video") result.video.push(entry);
        else if (contentType === "audio") result.audio.push(entry);
      }
    }
    return result.duration > 0 && result.video.length ? result : null;
  }

  /** Expands $RepresentationID$, $Number$, $Number%05d$ and $Bandwidth$. */
  function expandTemplate(template, representation, number) {
    return String(template).replace(/\$(RepresentationID|Number|Bandwidth)(?:%0(\d+)d)?\$/g, (_all, name, width) => {
      const value = name === "RepresentationID" ? representation.id : name === "Bandwidth" ? String(representation.bandwidth) : String(number);
      return width && name !== "RepresentationID" ? value.padStart(Number(width), "0") : value;
    }).replace(/\$\$/g, "$");
  }

  function segmentCount(representation, totalSeconds) {
    const seconds = representation.template.duration / representation.template.timescale;
    return seconds > 0 ? Math.max(1, Math.ceil(totalSeconds / seconds - 1e-6)) : 0;
  }

  /** 0-based index of the segment that contains <time>. */
  function segmentIndexAt(representation, time) {
    const seconds = representation.template.duration / representation.template.timescale;
    return seconds > 0 ? Math.max(0, Math.floor(Math.max(0, time) / seconds)) : 0;
  }

  function resolveUrl(base, relative) {
    try {
      return new URL(relative, base).toString();
    } catch {
      return "";
    }
  }

  /** Highest video stream up to maxHeight that the browser can decode, else the smallest playable. */
  function pickVideo(representations, maxHeight, isSupported) {
    const playable = representations
      .filter((item) => isSupported(`${item.mimeType}; codecs="${item.codecs}"`))
      .sort((a, b) => (a.height - b.height) || (a.bandwidth - b.bandwidth));
    if (!playable.length) return null;
    const fitting = playable.filter((item) => !item.height || item.height <= maxHeight);
    return fitting.length ? fitting[fitting.length - 1] : playable[0];
  }

  function pickAudio(representations, isSupported) {
    return representations
      .filter((item) => isSupported(`${item.mimeType}; codecs="${item.codecs}"`))
      .sort((a, b) => b.bandwidth - a.bandwidth)[0] || null;
  }

  /** Only the game details page itself: /library/app/<id>, never its sub pages. */
  function appIdFromPath(pathname) {
    const match = /^\/library\/app\/(\d{1,10})\/?$/.exec(String(pathname || ""));
    const appId = match ? Number(match[1]) : 0;
    return appId > 0 && appId <= 0xffffffff ? appId : 0;
  }

  function editorAppIdFromPath(pathname) {
    const match = /^\/tfs\/trailers(?:\/(\d{1,10}))?\/?$/.exec(String(pathname || ""));
    return match ? Number(match[1] || 0) : -1;
  }

  /** Where looping jumps back to and where it ends, honouring trims. */
  function playWindow(duration, trimStart, trimEnd) {
    const start = Math.max(0, Number(trimStart) || 0);
    const end = Math.max(0, duration - Math.max(0, Number(trimEnd) || 0));
    return end - start >= 3 ? { start, end } : { start: 0, end: duration };
  }

  // -----------------------------------------------------------------------
  // DASH player (Media Source Extensions)
  // -----------------------------------------------------------------------

  /**
   * Streams one Steam DASH trailer into <video>. Keeps a bounded buffer ahead
   * of the play position, refetches after seeks (loop/trim) and frees old
   * data when the browser's buffer quota is reached.
   */
  const bufferAheadSeconds = 24;

  function createDashPlayer(win, video, manifestUrl, options) {
    const controller = { destroyed: false, error: "", window: { start: 0, end: 0 }, ready: false };
    const MediaSourceType = win.MediaSource;
    if (typeof MediaSourceType !== "function") {
      controller.error = "Media Source is not available.";
      return Object.assign(controller, { destroy() {} });
    }

    const aborter = new win.AbortController();
    const mediaSource = new MediaSourceType();
    const objectUrl = win.URL.createObjectURL(mediaSource);
    const tracks = [];
    let manifest = null;
    let pumpTimer = 0;
    let endedSignalled = false;

    async function fetchBytes(url) {
      const response = await win.fetch(url, { signal: aborter.signal, cache: "force-cache" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.arrayBuffer();
    }

    function waitUpdate(buffer) {
      return buffer.updating
        ? new Promise((resolve) => buffer.addEventListener("updateend", resolve, { once: true }))
        : Promise.resolve();
    }

    async function append(track, bytes) {
      for (let attempt = 0; attempt < 3; attempt += 1) {
        await waitUpdate(track.buffer);
        try {
          track.buffer.appendBuffer(bytes);
          await waitUpdate(track.buffer);
          return;
        } catch (error) {
          if (error?.name !== "QuotaExceededError") throw error;
          // Free what lies well behind the play head, then retry.
          const behind = Math.max(0, video.currentTime - 6);
          if (behind <= 0) throw error;
          await waitUpdate(track.buffer);
          track.buffer.remove(0, behind);
          await waitUpdate(track.buffer);
        }
      }
    }

    function bufferedEnd(track, time) {
      const ranges = track.buffer.buffered;
      for (let index = 0; index < ranges.length; index += 1) {
        if (time >= ranges.start(index) - 0.25 && time <= ranges.end(index) + 0.25) return ranges.end(index);
      }
      return -1;
    }

    async function pumpTrack(track) {
      if (track.busy || controller.destroyed) return;
      track.busy = true;
      try {
        while (!controller.destroyed) {
          const now = video.currentTime;
          const end = bufferedEnd(track, now);
          if (end < 0) {
            // Play head is outside buffered data (seek, loop): continue from there.
            track.next = segmentIndexAt(track.representation, now);
          } else {
            if (end - now > bufferAheadSeconds) break;
            // Continue right after the buffered range the play head is in
            // (after a loop the old position may be far away).
            const following = segmentIndexAt(track.representation, end + 0.05);
            if (end < manifest.duration - 0.3 && Math.abs(following - track.next) > 1) track.next = following;
          }
          if (track.next >= track.count) break;
          const number = track.representation.template.startNumber + track.next;
          const url = resolveUrl(manifestUrl, expandTemplate(track.representation.template.media, track.representation, number));
          const bytes = await fetchBytes(url);
          if (controller.destroyed) return;
          await append(track, bytes);
          track.next += 1;
        }
        signalEndIfComplete();
      } catch (error) {
        if (!controller.destroyed && error?.name !== "AbortError") {
          controller.error = String(error?.message || error);
          options.onError?.(controller.error);
        }
      } finally {
        track.busy = false;
      }
    }

    function signalEndIfComplete() {
      if (endedSignalled || controller.destroyed || mediaSource.readyState !== "open") return;
      if (!tracks.every((track) => track.next >= track.count && !track.buffer.updating)) return;
      endedSignalled = true;
      try { mediaSource.endOfStream(); } catch {}
    }

    function pump() {
      for (const track of tracks) void pumpTrack(track);
    }

    function onTimeUpdate() {
      const { start, end } = controller.window;
      if (end > 0 && video.currentTime >= end - 0.15) {
        video.currentTime = start;
        tryPlay();
      }
      pump();
    }

    function onEnded() {
      video.currentTime = controller.window.start;
      tryPlay();
    }

    function onSeeking() {
      // A backwards seek after endOfStream reopens the source on append.
      endedSignalled = false;
      pump();
    }

    async function start() {
      const response = await win.fetch(manifestUrl, { signal: aborter.signal, cache: "no-store" });
      if (!response.ok) throw new Error(`Manifest HTTP ${response.status}`);
      manifest = parseMpd(await response.text());
      if (!manifest) throw new Error("Unsupported trailer manifest.");
      const isSupported = (type) => {
        try { return MediaSourceType.isTypeSupported(type); } catch { return false; }
      };
      const videoRep = pickVideo(manifest.video, options.maxHeight || 1080, isSupported);
      if (!videoRep) throw new Error("No playable video stream.");
      const audioRep = pickAudio(manifest.audio, isSupported);
      controller.window = playWindow(manifest.duration, options.trimStart, options.trimEnd);

      await new Promise((resolve, reject) => {
        if (mediaSource.readyState === "open") { resolve(); return; }
        mediaSource.addEventListener("sourceopen", resolve, { once: true });
        win.setTimeout(() => reject(new Error("Media Source did not open.")), 8000);
      });
      if (controller.destroyed) return;
      mediaSource.duration = manifest.duration;
      // All source buffers must exist before the first init segment is
      // appended; Chromium refuses new ones afterwards.
      for (const representation of [videoRep, audioRep].filter(Boolean)) {
        tracks.push({
          representation,
          buffer: mediaSource.addSourceBuffer(`${representation.mimeType}; codecs="${representation.codecs}"`),
          count: segmentCount(representation, manifest.duration),
          next: segmentIndexAt(representation, controller.window.start),
          busy: false,
        });
      }
      const inits = await Promise.all(tracks.map((track) =>
        fetchBytes(resolveUrl(manifestUrl, expandTemplate(track.representation.template.initialization, track.representation, 0)))));
      if (controller.destroyed) return;
      for (let index = 0; index < tracks.length; index += 1) await append(tracks[index], inits[index]);
      controller.hasAudio = Boolean(audioRep);
      controller.height = videoRep.height;
      if (controller.window.start > 0) video.currentTime = controller.window.start;
      pump();
      pumpTimer = win.setInterval(pump, 1000);
      controller.ready = true;
      // Start once the first media segment is in; an earlier play() call can
      // stay paused in Steam's CEF.
      if (video.readyState >= 3) tryPlay();
    }

    function tryPlay() {
      if (controller.destroyed || !controller.ready || !controller.wantPlaying || !video.paused) return;
      video.play().catch((error) => {
        controller.playError = String(error?.name || error);
        // Autoplay with sound can be refused; muted always plays.
        if (!video.muted && !controller.destroyed) {
          video.muted = true;
          void video.play().catch(() => {});
        }
      });
    }

    controller.wantPlaying = true;
    controller.play = () => {
      controller.wantPlaying = true;
      tryPlay();
    };
    controller.pause = () => {
      controller.wantPlaying = false;
      try { video.pause(); } catch {}
    };

    video.addEventListener("timeupdate", onTimeUpdate);
    video.addEventListener("ended", onEnded);
    video.addEventListener("seeking", onSeeking);
    video.addEventListener("canplay", tryPlay);
    video.src = objectUrl;
    controller.started = start().catch((error) => {
      if (controller.destroyed || error?.name === "AbortError") return;
      controller.error = String(error?.message || error);
      options.onError?.(controller.error);
    });

    controller.destroy = () => {
      if (controller.destroyed) return;
      controller.destroyed = true;
      win.clearInterval(pumpTimer);
      try { aborter.abort(); } catch {}
      video.removeEventListener("timeupdate", onTimeUpdate);
      video.removeEventListener("ended", onEnded);
      video.removeEventListener("seeking", onSeeking);
      video.removeEventListener("canplay", tryPlay);
      try { video.pause(); } catch {}
      try { video.removeAttribute("src"); video.load(); } catch {}
      try { win.URL.revokeObjectURL(objectUrl); } catch {}
    };
    return controller;
  }

  const internals = {
    parseIsoDuration, parseMpd, expandTemplate, segmentCount, segmentIndexAt,
    resolveUrl, pickVideo, pickAudio, appIdFromPath, editorAppIdFromPath, playWindow, createDashPlayer,
  };
  if (window.__tfsTrailerTestHooks === true) {
    window.__tfsTrailerInternals = internals;
    return "test";
  }

  // -----------------------------------------------------------------------
  // Runtime bootstrap
  // -----------------------------------------------------------------------

  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  const existing = window.__tfsTrailers;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const styleId = "tfs-trailer-style";
  const editorRoutes = ["/tfs/trailers/:appId", "/tfs/trailers"];
  const shortcutAppType = 1073741824;
  const settingsRefreshMs = 60000;
  const hintMs = 4500;
  const gamepadButtonSecondary = 3;

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

  async function loadSettings(force = false) {
    if (!force && state.settings && Date.now() - state.settingsLoadedAt < settingsRefreshMs) return state.settings;
    try {
      const { ok, payload } = await api("/api/trailers/settings");
      // 404/403 = plugin turned off in the Plugin Activator.
      state.settings = ok && payload ? payload : { enabled: false, unavailable: true };
    } catch {
      state.settings = state.settings || { enabled: false, unavailable: true };
    }
    state.settingsLoadedAt = Date.now();
    return state.settings;
  }

  function gameQuery(appId) {
    const overview = appOverview(appId);
    const title = overview?.display_name || overview?.strDisplayName || "";
    const shortcut = Number(overview?.app_type) === shortcutAppType;
    return { title, shortcut, query: `/api/trailers/game?appId=${appId}&title=${encodeURIComponent(title)}&shortcut=${shortcut ? 1 : 0}` };
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

  let headerClassesCache = null;
  function headerClasses() {
    if (headerClassesCache) return headerClassesCache;
    try {
      const modules = window.__tfs?.webpack?.getModules?.() || [];
      for (const module of modules) {
        const value = module?.exports ?? module;
        if (value && typeof value === "object" && typeof value.TopCapsule === "string" && typeof value.ImgContainer === "string") {
          headerClassesCache = {
            capsule: value.TopCapsule,
            image: value.ImgContainer,
            title: value.TitleImageContainer,
            fullscreenStart: value.FullscreenEnterStart,
          };
          return headerClassesCache;
        }
      }
    } catch {}
    return null;
  }

  function findHeader(doc) {
    const classes = headerClasses();
    let capsule = classes?.capsule ? doc.querySelector(`.${CSS.escape(classes.capsule)}`) : null;
    let image = capsule && classes?.image ? capsule.querySelector(`.${CSS.escape(classes.image)}`) : null;
    if (!capsule) {
      // Fallback without Steam's class map: the hero image of the page.
      const hero = doc.querySelector('img[src*="library_hero"], img[src*="_hero."]');
      image = hero?.parentElement || null;
      capsule = image?.parentElement || null;
    }
    if (!capsule || !image || !capsule.isConnected) return null;
    const rect = capsule.getBoundingClientRect();
    if (rect.width < 200 || rect.height < 100) return null;
    // In Big Picture the logo sits next to the header, not inside it.
    let title = null;
    if (classes?.title) {
      const selector = `.${CSS.escape(classes.title)}`;
      for (let node = capsule, depth = 0; node && depth < 4 && !title; node = node.parentElement, depth += 1) {
        title = node.querySelector(selector);
      }
    }
    return { capsule, image, title };
  }

  function release(registration) {
    try {
      if (typeof registration === "function") registration();
      else registration?.unregister?.();
    } catch {}
  }

  // -----------------------------------------------------------------------
  // Game page session
  // -----------------------------------------------------------------------

  function ensureStyles(doc) {
    if (!doc || doc.getElementById(styleId)) return;
    const style = doc.createElement("style");
    style.id = styleId;
    style.textContent = `
      .tfs-trailer-stage {
        position: absolute;
        inset: 0;
        z-index: 1;
        overflow: hidden;
        opacity: 0;
        pointer-events: none;
        background: #000;
        transition: opacity 900ms ease;
      }
      .tfs-trailer-stage.is-visible { opacity: 1; }
      .tfs-trailer-stage.is-leaving { opacity: 0; transition-duration: 350ms; }
      .tfs-trailer-stage video {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: cover;
        object-position: center 30%;
      }
      .tfs-trailer-stage::after {
        content: "";
        position: absolute;
        inset: 0;
        background: linear-gradient(180deg, rgba(0,0,0,0) 55%, rgba(0,0,0,.55) 100%);
      }
      .tfs-trailer-hint {
        position: absolute;
        right: 2.4%;
        bottom: 9%;
        z-index: 2;
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 6px 12px 6px 7px;
        border-radius: 999px;
        background: rgba(10, 12, 16, .62);
        color: rgba(255,255,255,.92);
        font: 600 14px/1 "Motiva Sans", Arial, sans-serif;
        letter-spacing: .02em;
        opacity: 0;
        transform: translateY(6px);
        transition: opacity 300ms ease, transform 300ms ease;
        pointer-events: none;
      }
      .tfs-trailer-hint.is-visible { opacity: 1; transform: none; }
      .tfs-trailer-hint b {
        display: inline-grid;
        place-items: center;
        width: 22px;
        height: 22px;
        border-radius: 50%;
        background: #1f6feb;
        color: #fff;
        font-size: 12px;
      }
      .tfs-trailer-logo { transition: transform 900ms cubic-bezier(.2,.7,.2,1); transform-origin: left bottom; will-change: transform; }
    `;
    doc.head.append(style);
  }

  function qualityHeight(settings, win) {
    const chosen = Number(settings?.quality) || 1080;
    // No point streaming 1080p onto a 720p screen.
    const screenHeight = Math.round((win.screen?.height || win.innerHeight || 1080) * (win.devicePixelRatio || 1));
    const screenCap = screenHeight <= 760 ? 720 : 1080;
    return Math.min(chosen, screenCap);
  }

  function stopSession(immediate = false) {
    const session = state.session;
    state.session = null;
    state.sessionToken += 1;
    if (!session) return;
    session.stopped = true;
    window.clearTimeout(session.delayTimer);
    window.clearTimeout(session.hintTimer);
    window.clearInterval(session.focusTimer);
    session.registrations.forEach(release);
    const finish = () => {
      try { session.player?.destroy(); } catch {}
      try { session.stage?.remove(); } catch {}
      restoreLogo(session);
      restoreImageContainer(session);
    };
    if (immediate || !session.stage?.classList.contains("is-visible")) {
      finish();
      return;
    }
    session.stage.classList.add("is-leaving");
    if (session.logo) session.logo.style.transform = "";
    window.setTimeout(finish, 380);
  }

  function restoreLogo(session) {
    const logo = session.logo;
    if (!logo) return;
    logo.style.transform = session.logoStyle?.transform || "";
    logo.style.zIndex = session.logoStyle?.zIndex || "";
    logo.style.position = session.logoStyle?.position || "";
    logo.classList.remove("tfs-trailer-logo");
  }

  function restoreImageContainer(session) {
    if (session.imagePositionPatched && session.image) session.image.style.position = "";
  }

  function moveLogo(session, header, win) {
    const logo = header.title;
    if (!logo) return;
    session.logo = logo;
    session.logoStyle = { transform: logo.style.transform, zIndex: logo.style.zIndex, position: logo.style.position };
    // Keep the logo above the trailer.
    if (win.getComputedStyle(logo).position === "static") logo.style.position = "relative";
    logo.style.zIndex = "2";
    if (!state.settings?.moveLogo) return;
    const capsuleRect = header.capsule.getBoundingClientRect();
    const logoRect = logo.getBoundingClientRect();
    if (!logoRect.width || !logoRect.height) return;
    const scale = Math.min(1, Math.max(0.45, (capsuleRect.width * 0.28) / logoRect.width));
    const targetLeft = capsuleRect.left + capsuleRect.width * 0.035;
    const targetBottom = capsuleRect.bottom - capsuleRect.height * 0.1;
    const dx = targetLeft - logoRect.left;
    const dy = targetBottom - logoRect.bottom;
    logo.classList.add("tfs-trailer-logo");
    logo.style.transform = `translate(${Math.round(dx)}px, ${Math.round(dy)}px) scale(${scale.toFixed(3)})`;
  }

  function showHint(session, text) {
    if (!session.hint) return;
    session.hint.querySelector("span").textContent = text;
    session.hint.classList.add("is-visible");
    window.clearTimeout(session.hintTimer);
    session.hintTimer = window.setTimeout(() => session.hint?.classList.remove("is-visible"), hintMs);
  }

  function toggleSound() {
    const session = state.session;
    if (!session?.video || !session.player?.hasAudio || !session.visible) return false;
    session.video.muted = !session.video.muted;
    if (!session.video.muted) session.video.volume = 1;
    showHint(session, session.video.muted ? "Sound on" : "Sound off");
    return true;
  }

  async function waitForHeader(doc, token, timeoutMs = 6000) {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      if (token !== state.sessionToken) return null;
      const header = findHeader(doc);
      if (header) return header;
      await new Promise((resolve) => window.setTimeout(resolve, 200));
    }
    return null;
  }

  async function startSession(appId) {
    stopSession(true);
    const token = state.sessionToken;
    const openedAt = Date.now();
    const settings = await loadSettings();
    if (token !== state.sessionToken || !settings?.enabled || isGameRunning()) return;
    if ((settings.disabledAppIds || []).includes(appId)) return;

    const { query } = gameQuery(appId);
    const { ok, payload } = await api(query).catch(() => ({ ok: false }));
    if (token !== state.sessionToken || !ok || !payload?.enabled) return;
    const movie = (payload.movies || []).find((item) => item.id === payload.selectedMovieId);
    if (!movie?.dashH264) return;

    const win = bigPictureWindow();
    const doc = win?.document;
    if (!doc) return;
    const header = await waitForHeader(doc, token);
    if (!header || token !== state.sessionToken) return;
    ensureStyles(doc);

    const session = {
      appId, token, stopped: false, visible: false, registrations: [],
      header, image: header.image, delayTimer: 0, hintTimer: 0, focusTimer: 0,
    };
    state.session = session;

    if (win.getComputedStyle(header.image).position === "static") {
      header.image.style.position = "relative";
      session.imagePositionPatched = true;
    }
    const stage = doc.createElement("div");
    stage.className = "tfs-trailer-stage";
    stage.setAttribute("aria-hidden", "true");
    const video = doc.createElement("video");
    video.muted = !settings.startWithSound;
    video.playsInline = true;
    video.preload = "auto";
    video.disablePictureInPicture = true;
    stage.append(video);
    const hint = doc.createElement("div");
    hint.className = "tfs-trailer-hint";
    hint.innerHTML = "<b>X</b><span></span>";
    stage.append(hint);
    header.image.append(stage);
    Object.assign(session, { stage, video, hint });

    // Prepare while Steam's artwork is still showing, reveal after the delay.
    session.player = createDashPlayer(win, video, movie.dashH264, {
      maxHeight: qualityHeight(settings, win),
      trimStart: payload.overrides?.trimStartSeconds,
      trimEnd: payload.overrides?.trimEndSeconds,
      onError: () => { if (state.session === session) stopSession(); },
    });
    const reveal = () => {
      if (session.stopped || session.visible) return;
      const begin = () => {
        if (session.stopped || session.visible) return;
        if (!header.capsule.isConnected) { stopSession(true); return; }
        session.visible = true;
        stage.classList.add("is-visible");
        moveLogo(session, header, win);
        if (session.player.hasAudio) showHint(session, video.muted ? "Sound on" : "Sound off");
      };
      if (video.readyState >= 3 && !video.paused) begin();
      else video.addEventListener("playing", begin, { once: true });
    };
    const delayMs = Math.max(0, (Number(settings.delaySeconds) || 0) * 1000 - (Date.now() - openedAt));
    session.delayTimer = window.setTimeout(reveal, delayMs);

    // Pause while Big Picture is hidden or a game runs. Window focus is not
    // used: Quick Access and other Steam popups take it while the page shows.
    session.focusTimer = window.setInterval(() => {
      if (session.stopped) return;
      if (!header.capsule.isConnected) { stopSession(true); return; }
      const showing = doc.visibilityState !== "hidden" && !isGameRunning();
      if (!showing && session.player.wantPlaying) session.player.pause();
      else if (showing && !session.player.wantPlaying) session.player.play();
    }, 1000);

    // Big Picture's focus navigation reports controller buttons as
    // "vgp_onbuttondown" DOM events on the focused element (detail.button is
    // its GamepadButton: 3 = SECONDARY = X). SteamClient's raw controller
    // messages carry action ids instead, so they are not used here.
    const onGamepadButton = (event) => {
      if (Number(event?.detail?.button) === gamepadButtonSecondary && !event.detail.is_repeat && state.session === session) toggleSound();
    };
    doc.addEventListener("vgp_onbuttondown", onGamepadButton, true);
    session.registrations.push(() => doc.removeEventListener("vgp_onbuttondown", onGamepadButton, true));
    const onKey = (event) => {
      if ((event.key === "x" || event.key === "X") && !event.repeat && !/^(INPUT|TEXTAREA)$/.test(event.target?.tagName || "")) toggleSound();
    };
    doc.addEventListener("keydown", onKey, true);
    session.registrations.push(() => doc.removeEventListener("keydown", onKey, true));
  }

  function onRouteChanged(pathname) {
    if (state.destroyed) return;
    const path = String(pathname || "");
    if (path === state.lastPath) return;
    state.lastPath = path;
    const appId = appIdFromPath(path);
    if (appId && state.session?.appId === appId) return;
    if (!appId) {
      stopSession();
      return;
    }
    void startSession(appId);
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
          state.historyUnlisten = history.listen((location) => onRouteChanged(location?.pathname || currentPath()));
          state.history = history;
        } catch {}
      }
    }
    onRouteChanged(currentPath());
    window.clearTimeout(state.historyTimer);
    state.historyTimer = window.setTimeout(watchHistory, state.history ? 1500 : 3000);
  }

  // -----------------------------------------------------------------------
  // Settings page (/tfs/trailers and /tfs/trailers/:appId)
  // -----------------------------------------------------------------------

  const delayOptions = [[0, "Immediately"], [1, "1 s"], [2, "2 s"], [3, "3 s"], [5, "5 s"], [8, "8 s"]];
  const qualityOptions = [["1080", "1080p"], ["720", "720p"], ["480", "480p"]];
  const trimOptions = [[0, "None"], [1, "1 s"], [2, "2 s"], [3, "3 s"], [5, "5 s"], [8, "8 s"], [10, "10 s"], [15, "15 s"], [20, "20 s"], [30, "30 s"]];

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

    // Plain wrapper: Steam's fields (ToggleField, dropdown field) already are
    // full rows. core.ui.PanelSectionRow resolves to Steam's Field, and a field
    // inside a field pushes the label to the right edge.
    function Row({ children }) {
      return h("div", { className: "tfs-trailer-row", style: { minWidth: 0 } }, children);
    }

    function Button({ onClick, children, disabled }) {
      if (ui.DialogButton) return h(ui.DialogButton, { onClick, disabled, style: { marginTop: "6px" } }, children);
      return h("button", { type: "button", className: "DialogButton gpfocus Focusable", onClick, disabled, tabIndex: 0 }, children);
    }

    function Toggle({ label, description, checked, onChange }) {
      if (ui.ToggleField) return h(Row, null, h(ui.ToggleField, { label, description, checked: Boolean(checked), onChange }));
      return h(Row, null, h(Button, { onClick: () => onChange(!checked) }, `${label}: ${checked ? "On" : "Off"}`));
    }

    const DropdownItem = resolveDropdownItem(core);

    function Choice({ label, description, value, options, onChange }) {
      const rgOptions = options.map(([, optionLabel], index) => ({ data: index, label: optionLabel }));
      const selected = Math.max(0, options.findIndex(([data]) => data === value));
      const select = (option) => onChange(options[Number(option?.data ?? 0)][0]);
      if (DropdownItem) {
        return h(Row, null, h(DropdownItem, {
          label, description, rgOptions, selectedOption: selected, onChange: select, menuLabel: label,
          // "inline" clamps the popup to the button width and cuts option names off.
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

    function Heading({ children }) {
      return h("div", {
        style: { margin: "22px 0 6px", fontSize: "13px", fontWeight: 700, letterSpacing: ".08em", textTransform: "uppercase", color: "rgba(255,255,255,.55)" },
      }, children);
    }

    function Preview({ movie, trimStart, trimEnd, quality }) {
      const ref = React.useRef(null);
      const [muted, setMuted] = React.useState(true);
      const [error, setError] = React.useState("");
      React.useEffect(() => {
        const video = ref.current;
        if (!video || !movie?.dashH264) return undefined;
        const win = video.ownerDocument.defaultView;
        setError("");
        const player = createDashPlayer(win, video, movie.dashH264, {
          maxHeight: Math.min(Number(quality) || 720, 720),
          trimStart,
          trimEnd,
          onError: (message) => setError(message),
        });
        return () => player.destroy();
      }, [movie?.dashH264, trimStart, trimEnd, quality]);
      React.useEffect(() => {
        if (ref.current) ref.current.muted = muted;
      }, [muted]);
      if (!movie?.dashH264) {
        return h("div", { style: { aspectRatio: "16 / 9", borderRadius: "8px", background: "rgba(255,255,255,.06)", display: "grid", placeItems: "center", color: "rgba(255,255,255,.6)" } }, "No trailer to preview");
      }
      return h("div", null,
        h("div", { style: { position: "relative", aspectRatio: "16 / 9", borderRadius: "8px", overflow: "hidden", background: "#000", boxShadow: "0 0 0 1px rgba(255,255,255,.12)" } },
          h("video", { ref, muted: true, playsInline: true, style: { width: "100%", height: "100%", objectFit: "cover" } })),
        error ? h("div", { style: { color: "#ff9a9a", fontSize: "13px", marginTop: "8px" } }, `Preview failed: ${error}`) : null,
        h(Button, { onClick: () => setMuted(!muted) }, muted ? "Preview with sound" : "Mute preview"));
    }

    function GlobalSettings({ settings, onSave }) {
      return [
        h(Heading, { key: "global-heading" }, "All games"),
        h(Toggle, {
          key: "enabled",
          label: "Play trailers on game pages",
          description: "Steam's header artwork stays first, then the trailer fades in.",
          checked: settings.enabled,
          onChange: (enabled) => onSave({ enabled }),
        }),
        h(Choice, { key: "delay", label: "Start after", value: settings.delaySeconds, options: delayOptions, onChange: (delaySeconds) => onSave({ delaySeconds }) }),
        h(Choice, { key: "quality", label: "Highest quality", value: settings.quality, options: qualityOptions, onChange: (quality) => onSave({ quality }) }),
        h(Toggle, { key: "logo", label: "Move logo to the lower left", checked: settings.moveLogo, onChange: (moveLogo) => onSave({ moveLogo }) }),
        h(Toggle, {
          key: "sound",
          label: "Start with sound",
          description: "X switches sound on or off while a trailer plays.",
          checked: settings.startWithSound,
          onChange: (startWithSound) => onSave({ startWithSound }),
        }),
        h(Toggle, {
          key: "shortcuts",
          label: "Trailers for non-Steam games",
          description: "Looks up the same game in the Steam store by its title.",
          checked: settings.matchShortcuts,
          onChange: (matchShortcuts) => onSave({ matchShortcuts }),
        }),
      ];
    }

    function StoreMatch({ game, onChoose }) {
      const [term, setTerm] = React.useState(game.title || "");
      const [results, setResults] = React.useState(null);
      const [busy, setBusy] = React.useState(false);
      const search = async () => {
        setBusy(true);
        const { ok, payload } = await api(`/api/trailers/search?term=${encodeURIComponent(term)}`).catch(() => ({ ok: false }));
        setResults(ok ? payload?.items || [] : []);
        setBusy(false);
      };
      const source = game.matchSource === "manual"
        ? `Chosen: ${game.matchedName || `Steam app ${game.steamAppId}`}`
        : game.steamAppId
          ? `Found automatically: ${game.matchedName || `Steam app ${game.steamAppId}`}`
          : "No Steam store match yet.";
      return h(React.Fragment, null,
        h(Heading, { key: "match-heading" }, "Steam store match"),
        h("div", { key: "match-source", style: { opacity: 0.7, fontSize: "14px", margin: "2px 0 6px" } }, source),
        ui.TextField
          ? h(Row, { key: "match-term" }, h(ui.TextField, { label: "Search the Steam store", value: term, onChange: (event) => setTerm(String(event?.target?.value ?? "")) }))
          : null,
        h(Row, { key: "match-actions" }, h("div", { style: { display: "flex", gap: "12px", flexWrap: "wrap" } },
          h(Button, { onClick: search, disabled: busy || term.trim().length < 2 }, busy ? "Searching…" : "Search"),
          game.matchSource === "manual" ? h(Button, { onClick: () => onChoose(null) }, "Back to automatic") : null)),
        ...(results || []).slice(0, 8).map((item) =>
          h(Row, { key: `result-${item.appId}` }, h(Button, { onClick: () => onChoose(item.appId) }, `Use ${item.name}`))),
        results && !results.length ? h("div", { key: "match-none", style: { opacity: 0.6, fontSize: "14px" } }, "Nothing found.") : null);
    }

    return function TfsTrailerEditor(props) {
      const appId = Number(props?.match?.params?.appId || 0) || 0;
      const overview = appId ? appOverview(appId) : null;
      const title = overview?.display_name || (appId ? `App ${appId}` : "");
      const [settings, setSettings] = React.useState(null);
      const [game, setGame] = React.useState(null);
      const [error, setError] = React.useState("");

      const loadGame = React.useCallback(async () => {
        if (!appId) return;
        const { ok, payload } = await api(gameQuery(appId).query).catch(() => ({ ok: false }));
        if (ok && payload) setGame(payload);
        else setError(payload?.message || "TFS Trailers is not available.");
      }, [appId]);

      React.useEffect(() => {
        let alive = true;
        loadSettings(true).then((value) => {
          if (!alive) return;
          if (!value || value.unavailable) setError("TFS Trailers is not available. Turn it on in the Plugin Activator.");
          else setSettings(value);
        });
        void loadGame();
        return () => { alive = false; };
      }, [appId]);

      const saveSettings = async (patch) => {
        setSettings((current) => ({ ...current, ...patch }));
        const { ok, payload } = await api("/api/trailers/settings", patch).catch(() => ({ ok: false }));
        if (ok && payload) {
          state.settings = payload;
          state.settingsLoadedAt = Date.now();
          setSettings(payload);
        }
      };

      const saveGame = async (patch) => {
        const overrides = { ...(game?.overrides || {}), ...patch };
        setGame((current) => current && { ...current, overrides });
        const { title: gameTitle, shortcut } = gameQuery(appId);
        const { ok, payload } = await api("/api/trailers/game", { appId, settings: overrides, title: gameTitle, isShortcut: shortcut }).catch(() => ({ ok: false }));
        if (ok && payload) {
          setGame(payload);
          state.settingsLoadedAt = 0;
        }
      };

      const resetGame = async () => {
        const { title: gameTitle, shortcut } = gameQuery(appId);
        const { ok, payload } = await api("/api/trailers/game/reset", { appId, title: gameTitle, isShortcut: shortcut }).catch(() => ({ ok: false }));
        if (ok && payload) {
          setGame(payload);
          state.settingsLoadedAt = 0;
        }
      };

      const page = { padding: "56px 36px 40px", height: "100%", boxSizing: "border-box", overflowY: "auto", color: "#fff" };
      if (error && !settings) return h("div", { style: page }, h("h2", null, "TFS Trailers"), h("p", null, error));
      if (!settings || (appId && !game)) return h("div", { style: page }, h("h2", null, "TFS Trailers"), h("p", { style: { opacity: 0.7 } }, "Loading…"));

      const Focusable = ui.Focusable;
      const column = (children) => (Focusable ? h(Focusable, { "flow-children": "column", style: { minWidth: 0 } }, ...children) : h("div", { style: { minWidth: 0 } }, ...children));
      const movies = game?.movies || [];
      const selected = movies.find((item) => item.id === game?.selectedMovieId) || null;
      const overrides = game?.overrides || {};
      const movieOptions = [[null, "Automatic (main trailer)"]].concat(movies.map((item) => [item.id, item.name || `Video ${item.id}`]));

      const gameColumn = appId ? [
        h(Heading, { key: "game-heading" }, title),
        h(Toggle, {
          key: "game-enabled",
          label: "Play a trailer for this game",
          checked: overrides.mode !== "off",
          onChange: (on) => saveGame({ mode: on ? null : "off" }),
        }),
        movies.length
          ? h(Choice, { key: "movie", label: "Video", value: overrides.movieId ?? null, options: movieOptions, onChange: (movieId) => saveGame({ movieId }) })
          : h("div", { key: "no-movie", style: { opacity: 0.7, fontSize: "14px", margin: "8px 0" } }, game?.message || "Steam has no trailer for this game."),
        movies.length ? h(Choice, { key: "trim-start", label: "Skip intro", value: Math.round(overrides.trimStartSeconds || 0), options: trimOptions, onChange: (value) => saveGame({ trimStartSeconds: value || null }) }) : null,
        movies.length ? h(Choice, { key: "trim-end", label: "Cut ending", value: Math.round(overrides.trimEndSeconds || 0), options: trimOptions, onChange: (value) => saveGame({ trimEndSeconds: value || null }) }) : null,
        game?.isShortcut || overrides.steamAppId
          ? h(StoreMatch, { key: "store-match", game, onChoose: (steamAppId) => saveGame({ steamAppId, movieId: null }) })
          : null,
        h(Row, { key: "reset" }, h(Button, { onClick: resetGame }, "Reset this game")),
      ].filter(Boolean) : [];

      return h("div", { style: page },
        h("div", { style: { display: "flex", alignItems: "baseline", gap: "14px", marginBottom: "6px" } },
          h("h2", { style: { margin: 0, fontSize: "26px" } }, "TFS Trailers"),
          title ? h("div", { style: { opacity: 0.6, fontSize: "16px" } }, title) : null),
        h("div", { style: { opacity: 0.65, fontSize: "14px", marginBottom: "16px", maxWidth: "980px" } },
          "Plays the game's Steam trailer in the game page header. Changes save automatically."),
        h("div", { style: { display: "grid", gridTemplateColumns: appId ? "minmax(0, 1.05fr) minmax(0, 1fr)" : "minmax(0, 720px)", gap: "32px", alignItems: "start" } },
          column([...gameColumn, ...GlobalSettings({ settings, onSave: saveSettings })]),
          appId
            ? h("div", { style: { position: "sticky", top: "0" } },
              h(Preview, { movie: selected, trimStart: overrides.trimStartSeconds, trimEnd: overrides.trimEndSeconds, quality: settings.quality }),
              h("div", { style: { opacity: 0.55, fontSize: "13px", marginTop: "10px", lineHeight: 1.4 } },
                "Trailers come from the game's Steam store page. Non-Steam games use the Steam game with the same name."))
            : null));
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
        state.routeRemovers.push(core.routes.add(route, editor, { name: "TFS Trailers", exact: true }));
      }
    } catch (error) {
      console.warn("[TFS Trailers] editor route failed", error);
      state.routeTimer = window.setTimeout(registerEditorRoutes, 10000);
    }
  }

  function openEditor(appId) {
    const id = Number(appId) || 0;
    const routes = window.__tfs?.routes;
    if (!routes?.navigate || !state.routeRemovers.length) return false;
    stopSession(true);
    routes.navigate(id ? `/tfs/trailers/${id}` : "/tfs/trailers");
    return true;
  }

  // -----------------------------------------------------------------------
  // Lifecycle
  // -----------------------------------------------------------------------

  function onAppLifetime(notification) {
    const appId = Number(notification?.unAppID) || 0;
    if (notification?.bRunning) {
      state.runningApps.add(appId);
      stopSession(true);
      return;
    }
    state.runningApps.delete(appId);
    // Back on the game page after playing: start the trailer again.
    if (!isGameRunning() && !state.session) {
      const pageAppId = appIdFromPath(currentPath());
      if (pageAppId) void startSession(pageAppId);
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

  function seedRunningApps() {
    try {
      const running = window.SteamUIStore?.RunningApps;
      if (Array.isArray(running)) for (const app of running) state.runningApps.add(Number(app?.appid) || 0);
    } catch {}
  }

  function refresh() {
    state.settingsLoadedAt = 0;
    void loadSettings(true);
    if (!state.routeRemovers.length) void registerEditorRoutes();
    if (!state.history) watchHistory();
  }

  function destroy() {
    state.destroyed = true;
    stopSession(true);
    window.clearTimeout(state.historyTimer);
    window.clearTimeout(state.routeTimer);
    release(state.historyUnlisten);
    state.historyUnlisten = null;
    state.history = null;
    state.registrations.splice(0).forEach(release);
    state.routeRemovers.splice(0).forEach((remove) => { try { remove(); } catch {} });
    try { bigPictureWindow()?.document?.getElementById(styleId)?.remove(); } catch {}
  }

  window.__tfsTrailers = {
    version,
    refresh,
    destroy,
    openEditor,
    hasEditor: () => state.routeRemovers.length > 0 && state.settings?.unavailable !== true,
    isEnabled: () => state.settings?.enabled === true,
    isGameEnabled: (appId) => state.settings?.enabled === true && !(state.settings.disabledAppIds || []).includes(Number(appId)),
    toggleSound,
    get session() {
      const session = state.session;
      return session ? { appId: session.appId, visible: session.visible, muted: session.video?.muted, height: session.player?.height || 0, error: session.player?.error || "" } : null;
    },
    internals,
  };

  try {
    state.registrations.push(window.SteamClient?.GameSessions?.RegisterForAppLifetimeNotifications?.(onAppLifetime));
  } catch {}
  seedRunningApps();
  void loadSettings(true);
  void registerEditorRoutes();
  watchHistory();
  return "injected";
})();
