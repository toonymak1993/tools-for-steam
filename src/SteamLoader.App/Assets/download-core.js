// TFS OmniLibrary download row (SharedJSContext).
//
// OmniLibrary games (Epic via Legendary, GOG via heroic-gogdl, Xbox via the
// Store broker) are downloaded from their own row on the Big Picture game
// page, directly below Steam's play section. Steam's Play button is never
// touched: it only ever starts an installed game.
//  - patches Steam's own route /library/app/:appid through tfs-core
//    (core.routes.patch), like the HowLongToBeat row, so the row is part of
//    Steam's React tree and its buttons are real Steam DialogButtons the
//    controller reaches;
//  - the game comes from Steam's app overview (shortcut appid), no DOM
//    scraping;
//  - state comes from /api/unifystore/games/<appid> (download status and live
//    metrics) and the Download Center entry (pause/resume/cancel rights);
//  - actions use the same endpoints as the Quick Access Download Center:
//    POST /api/unifystore/games/install and /api/unifystore/downloads/action.
(() => {
  const version = 1;
  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const routePath = "/library/app/:appid";
  const patchId = "tfs-omnilibrary-download";
  const rowKey = "tfs-omnilibrary-download";
  const activePollMs = 1000;
  const idlePollMs = 15000;
  const launchHintMs = 8000;
  const summaryCacheMs = 30000;
  const storeChannelName = "ToolsForSteamOmniLibraryStores";
  const cssId = "tfs-omnilibrary-download";

  const existing = window.__tfsOmniDownloads;
  if (existing?.version === version && existing.alive?.()) {
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const state = {
    destroyed: false,
    removePatch: null,
    retryTimer: 0,
    cache: new Map(), // appId -> { record, at }
    inflight: new Map(), // appId -> Promise
    listeners: new Map(), // appId -> Set<fn>
    launchHints: new Map(), // appId -> timestamp
    busy: new Map(), // appId -> action id
    renderFuncWrappers: new WeakMap(),
    sectionWrappers: new WeakMap(),
    lifetimeRegistration: null,
    channel: null,
    summary: null,
    summaryAt: 0,
    loggedMismatch: false,
    rootClass: "",
  };

  // -----------------------------------------------------------------------
  // Pure helpers (exposed for tests through window.__tfsOmniDownloads.internals)
  // -----------------------------------------------------------------------

  const activeStatuses = ["preparing", "queued", "downloading", "reconnecting", "finalizing", "canceling"];
  const busyStatuses = [...activeStatuses, "uninstalling"];

  function isManagedShortcutAppId(value) {
    const appId = Number(value);
    return Number.isInteger(appId) && appId >= 0x80000000 && appId <= 0xffffffff;
  }

  function isActiveStatus(status) {
    return activeStatuses.includes(String(status || "").toLowerCase());
  }

  function storeTitle(storeId, game) {
    if (game?.requiresExternalLauncher === true && String(game?.providerDisplayName || "").trim()) {
      return String(game.providerDisplayName).trim();
    }
    return storeId === "xbox-game-pass"
      ? "Xbox"
      : storeId === "epic-games"
        ? "Epic Games"
        : storeId === "gog-galaxy"
          ? "GOG"
          : storeId === "rom-library"
            ? "Emulator"
            : "OmniLibrary";
  }

  function formatBytes(value) {
    const bytes = Math.max(0, Number(value) || 0);
    if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
    if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
    return `${(bytes / 1024).toFixed(0)} KB`;
  }

  function formatRemaining(seconds) {
    const total = Math.max(0, Math.round(Number(seconds) || 0));
    if (total < 60) return `${total}s left`;
    const minutes = Math.floor(total / 60);
    if (minutes < 60) return `${minutes} min left`;
    return `${Math.floor(minutes / 60)} h ${minutes % 60} min left`;
  }

  /** "12.3 GB of 40.1 GB · 25.0 MB/s · 19 min left" from the live metrics. */
  function transferMetrics(download) {
    const downloaded = Math.max(0, Number(download?.downloadedBytes) || 0);
    const total = Math.max(0, Number(download?.totalBytes) || 0);
    const speed = Math.max(0, Number(download?.downloadBytesPerSecond) || 0);
    const parts = [];
    if (downloaded > 0 && total > 0) parts.push(`${formatBytes(downloaded)} of ${formatBytes(total)}`);
    else if (total > 0) parts.push(formatBytes(total));
    if (speed > 0) {
      parts.push(`${formatBytes(speed)}/s`);
      if (total > downloaded) parts.push(formatRemaining((total - downloaded) / speed));
    }
    return parts.join(" · ");
  }

  /** What the install endpoint does for this game right now, as a button label. */
  function installLabel(game, status) {
    const provider = String(game?.providerDisplayName || "Store").trim() || "Store";
    if (status === "failed" || status === "cancel-failed") {
      return game?.requiresExternalLauncher === true ? `Retry in ${provider}` : "Retry";
    }
    if (status === "paused") return "Resume";
    if (status === "action-required") {
      if (game?.externalAction === "install-client") return "Install EA app";
      if (game?.externalAction === "link-account") return "Link EA";
      if (game?.externalAction === "continue-provider") return "Open EA app";
      if (game?.requiresAccountLink === true && /(?:link|account)/i.test(String(game?.download?.detailText || ""))) {
        return game?.deliveryProvider === "ea-app" ? "Link EA" : "Link Ubisoft";
      }
      if (game?.storeId === "xbox-game-pass") return "Open Xbox";
      if (game?.storeId === "gog-galaxy") return "Open GOG";
      return `Open ${provider}`;
    }
    if (game?.installed && game?.updateAvailable === true) return "Update";
    if (game?.externalAction === "install-client") return "Install EA app";
    if (game?.externalAction === "link-account") return "Link EA";
    if (game?.externalAction === "continue-provider") return "Open EA app";
    if (game?.requiresExternalLauncher === true) return `Install in ${provider}`;
    return "Download";
  }

  /**
   * Everything the row shows for one game. Returns null when the row should
   * stay hidden (installed and idle, not an OmniLibrary game, cloud only).
   * record = { storeId, game, entry } where entry is the Download Center row.
   */
  function describe(record, { hintActive = false, busyAction = "" } = {}) {
    const game = record?.game;
    if (!game || !record.storeId || record.storeId === "rom-library") return null;
    const storeId = record.storeId;
    const entry = record.entry || null;
    const download = game.download || {};
    let status = String(download.status || "idle").toLowerCase();
    const store = storeTitle(storeId, game);
    const progress = Math.max(0, Math.min(100, Math.round(Number(download.progressPercent) || 0)));
    const metrics = transferMetrics(download);
    const detailText = String(download.detailText || "").trim();
    const joinDetail = (...parts) => parts.filter(Boolean).join(" · ");
    const withStore = { ...game, storeId };

    if (status === "completed" && !game.installed) status = "idle";
    if (status === "canceled") status = "idle";

    const view = {
      appId: Number(game.steamAppId) || 0,
      storeId,
      status,
      tone: "neutral",
      title: "",
      detail: "",
      progress: null,
      indeterminate: false,
      actions: [],
    };
    const install = (label = installLabel(withStore, status)) => ({ id: "install", label });

    switch (status) {
      case "preparing":
      case "queued":
      case "downloading":
      case "reconnecting":
      case "finalizing": {
        view.tone = status === "reconnecting" ? "warning" : "active";
        view.title = status === "preparing"
          ? "Preparing download"
          : status === "queued"
            ? "Waiting in download queue"
            : status === "finalizing"
              ? "Finishing installation"
              : status === "reconnecting"
                ? `Reconnecting · ${progress}%`
                : `Downloading · ${progress}%`;
        view.detail = joinDetail(store, metrics, metrics ? "" : detailText);
        view.progress = status === "finalizing" ? Math.max(progress, 99) : progress;
        view.indeterminate = status === "preparing" || status === "queued" || (status === "downloading" && progress <= 0);
        if (entry?.canPause) view.actions.push({ id: "pause", label: "Pause" });
        if (entry?.canCancel) view.actions.push({ id: "cancel", label: "Cancel" });
        break;
      }
      case "canceling":
        view.tone = "active";
        view.title = "Canceling download";
        view.detail = joinDetail(store, detailText || "Removing partial files.");
        view.indeterminate = true;
        view.progress = 0;
        break;
      case "paused":
        view.tone = "warning";
        view.title = `Paused · ${progress}%`;
        view.detail = joinDetail(store, metrics || detailText);
        view.progress = progress;
        view.actions.push(entry && !entry.canResume ? install() : { id: "resume", label: "Resume" });
        if (entry?.canCancel) view.actions.push({ id: "cancel", label: "Cancel" });
        break;
      case "action-required":
        view.tone = "warning";
        view.title = "Action required";
        view.detail = joinDetail(store, detailText || "The store needs one more step before the download can start.");
        view.actions.push(install());
        break;
      case "failed":
      case "cancel-failed":
        view.tone = "error";
        view.title = status === "failed" ? "Download failed" : "Cancel failed";
        view.detail = joinDetail(store, detailText || "The store reported an error.");
        view.actions.push(install());
        if (entry?.canCancel) view.actions.push({ id: "cancel", label: "Cancel" });
        break;
      case "uninstalling":
        view.tone = "active";
        view.title = "Uninstalling";
        view.detail = joinDetail(store, detailText);
        view.indeterminate = true;
        view.progress = 0;
        break;
      case "uninstall-action-required":
      case "uninstall-failed":
        view.tone = status === "uninstall-failed" ? "error" : "warning";
        view.title = status === "uninstall-failed" ? "Uninstall failed" : `Finish the uninstall in ${store}`;
        view.detail = joinDetail(store, detailText);
        if (entry?.canRetryUninstall) view.actions.push({ id: "retry-uninstall", label: "Retry uninstall" });
        if (entry?.canManageExternally) view.actions.push({ id: "manage", label: `Open ${store}` });
        break;
      case "completed":
        view.tone = "ready";
        view.title = "Ready to play";
        view.detail = joinDetail(store, "Installed. Press Play to start.");
        view.progress = 100;
        break;
      default: {
        if (game.installed && game.updateAvailable === true) {
          view.status = "update";
          view.tone = "accent";
          view.title = "Update available";
          view.detail = joinDetail(store, "Play starts the installed version.");
          view.actions.push(install());
          break;
        }
        if (game.installed || game.cloudPlayable) return null;
        view.status = "not-installed";
        view.tone = hintActive ? "attention" : "neutral";
        view.title = hintActive ? "Not installed yet" : "Not installed";
        view.detail = hintActive
          ? `Play starts the game once it is installed. Select ${installLabel(withStore, "idle")} first.`
          : joinDetail(store, "Download it here, then press Play.");
        view.actions.push(install());
        break;
      }
    }

    if (busyAction) {
      for (const action of view.actions) {
        if (action.id === busyAction) action.label = "Working…";
      }
    }
    return view;
  }

  /**
   * Inserts <element> right after the play section (first child) of Steam's
   * AppDetailsRoot panel. Returns the output unchanged when Steam's layout is
   * not the expected one, so a Steam update can never break the page.
   */
  function insertAfterPlaySection(output, element, rootClass, cloneElement) {
    const props = output?.props;
    if (!props || !rootClass || !String(props.className || "").split(/\s+/).includes(rootClass)) return output;
    const children = Array.isArray(props.children) ? props.children : [props.children];
    if (!children.length) return output;
    if (children.some((child) => child?.key === element.key)) return output;
    const next = [children[0], element, ...children.slice(1)];
    return cloneElement(output, { children: next });
  }

  // -----------------------------------------------------------------------
  // API
  // -----------------------------------------------------------------------

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  async function getJson(path) {
    const response = await fetch(`${apiBase()}${path}`, { cache: "no-store" });
    const payload = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, payload };
  }

  async function postJson(path, body) {
    const response = await fetch(`${apiBase()}${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    const payload = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, payload };
  }

  /** OmniLibrary on and the game's store enabled (summary cached briefly). */
  async function isEnabledOmniLibraryApp(appId) {
    const now = Date.now();
    if (!state.summary || now - state.summaryAt > summaryCacheMs) {
      const result = await getJson("api/unifystore/summary").catch(() => null);
      if (result?.ok && result.payload) {
        state.summary = result.payload;
        state.summaryAt = now;
      }
    }
    const summary = state.summary;
    if (!summary) return true; // unknown: let the game endpoint decide
    if (summary.pluginEnabled !== true) return false;
    return (summary.stores || []).some((store) =>
      store?.enabled === true && (store.appIds || []).some((candidate) => Number(candidate) === appId));
  }

  async function fetchRecord(appId) {
    if (!(await isEnabledOmniLibraryApp(appId))) return { appId, storeId: "", game: null, entry: null };
    const result = await getJson(`api/unifystore/games/${encodeURIComponent(appId)}`);
    if (result.status === 404) return { appId, storeId: "", game: null, entry: null };
    if (!result.ok || !result.payload) throw new Error(`OmniLibrary game state failed (${result.status})`);
    const storeId = String(result.payload.storeId || "");
    const game = result.payload.game || null;
    if (!storeId || !game || Number(game.steamAppId) !== Number(appId)) {
      return { appId, storeId: "", game: null, entry: null };
    }
    let entry = null;
    const status = String(game.download?.status || "idle").toLowerCase();
    if (status && status !== "idle" && status !== "completed") {
      // Pause/resume/cancel rights live in the Download Center entry.
      const downloads = await getJson("api/unifystore/downloads").catch(() => null);
      entry = (downloads?.payload?.entries || []).find((candidate) =>
        String(candidate?.storeId || "").toLowerCase() === storeId.toLowerCase() &&
        String(candidate?.gameId || "").toLowerCase() === String(game.id || "").toLowerCase()) || null;
    }
    return { appId, storeId, game, entry };
  }

  function loadRecord(appId, { force = false } = {}) {
    const cached = state.cache.get(appId);
    const maxAge = isActiveStatus(cached?.record?.game?.download?.status) ? activePollMs : idlePollMs;
    if (!force && cached && Date.now() - cached.at < maxAge - 50) return Promise.resolve(cached.record);
    if (state.inflight.has(appId)) return state.inflight.get(appId);
    const request = fetchRecord(appId)
      .then((record) => {
        state.cache.set(appId, { record, at: Date.now() });
        notify(appId);
        return record;
      })
      .catch((error) => {
        if (cached) return cached.record;
        throw error;
      })
      .finally(() => state.inflight.delete(appId));
    state.inflight.set(appId, request);
    return request;
  }

  function notify(appId) {
    for (const listener of [...(state.listeners.get(appId) || [])]) {
      try { listener(); } catch {}
    }
  }

  function subscribe(appId, listener) {
    let set = state.listeners.get(appId);
    if (!set) state.listeners.set(appId, (set = new Set()));
    set.add(listener);
    return () => {
      set.delete(listener);
      if (!set.size) state.listeners.delete(appId);
    };
  }

  function announce(appId, status) {
    try {
      state.channel?.postMessage?.({ type: "download-status-changed", appId, status });
    } catch {}
  }

  /** Runs one row action and shows "preparing" at once while the worker starts. */
  async function runAction(appId, actionId) {
    const record = state.cache.get(appId)?.record;
    const game = record?.game;
    if (!game || !record.storeId || state.busy.has(appId)) return;
    state.busy.set(appId, actionId);
    state.launchHints.delete(appId);
    notify(appId);
    try {
      const body = { storeId: record.storeId, gameId: game.id };
      const result = actionId === "install"
        ? await postJson("api/unifystore/games/install", body)
        : await postJson("api/unifystore/downloads/action", { ...body, action: actionId });
      if (result.ok && (actionId === "install" || actionId === "resume")) {
        state.cache.set(appId, {
          at: Date.now(),
          record: {
            ...record,
            game: { ...game, download: { ...(game.download || {}), status: "preparing", detailText: String(result.payload?.message || "") } },
          },
        });
        announce(appId, "preparing");
      } else if (!result.ok) {
        console.warn("[TFS Downloads] action failed", actionId, result.payload?.message || result.status);
      }
    } catch (error) {
      console.warn("[TFS Downloads] action failed", actionId, error);
    } finally {
      state.busy.delete(appId);
      notify(appId);
      window.setTimeout(() => { void loadRecord(appId, { force: true }).catch(() => {}); }, 250);
    }
  }

  // -----------------------------------------------------------------------
  // Steam lookups
  // -----------------------------------------------------------------------

  function core() {
    return window.__tfs || null;
  }

  function appDetailsRootClass() {
    if (state.rootClass) return state.rootClass;
    try {
      const module = core()?.webpack?.findModule?.((candidate) =>
        candidate && typeof candidate === "object" &&
        typeof candidate.AppDetailsRoot === "string" &&
        typeof candidate.PlaySection === "string");
      state.rootClass = module?.AppDetailsRoot || "";
    } catch {}
    return state.rootClass;
  }

  /** True for Steam's game page sections component, even when another TFS row wrapped it. */
  function isAppDetailsSections(tfs, Sections) {
    if (typeof Sections !== "function") return false;
    try {
      const unpatched = tfs.patch?.getUnpatched?.(Sections) || Sections;
      if (Function.prototype.toString.call(unpatched).includes("AppDetailsRoot")) return true;
      // Wrappers (HowLongToBeat row) report Steam's source through toString().
      return String(unpatched).includes("AppDetailsRoot");
    } catch {
      return false;
    }
  }

  function onAppLifetime(notification) {
    const appId = Number(notification?.unAppID ?? notification?.appid) >>> 0;
    const running = notification?.bRunning ?? notification?.running;
    if (!running || !isManagedShortcutAppId(appId)) return;
    const record = state.cache.get(appId)?.record;
    const game = record?.game;
    if (!game || game.installed || game.cloudPlayable) return;
    // Play was pressed for a game that is not installed: point at the row.
    state.launchHints.set(appId, Date.now());
    notify(appId);
    window.setTimeout(() => notify(appId), launchHintMs + 50);
  }

  function registerSteamEvents() {
    try {
      const sessions = window.SteamClient?.GameSessions;
      if (typeof sessions?.RegisterForAppLifetimeNotifications === "function" && !state.lifetimeRegistration) {
        state.lifetimeRegistration = sessions.RegisterForAppLifetimeNotifications(onAppLifetime);
      }
    } catch (error) {
      console.warn("[TFS Downloads] app lifetime hook failed", error);
    }
    try {
      if (!state.channel && typeof window.BroadcastChannel === "function") {
        state.channel = new window.BroadcastChannel(storeChannelName);
        state.channel.addEventListener("message", (event) => {
          const appId = Number(event?.data?.appId) >>> 0;
          if (event?.data?.type === "stores-changed") {
            state.summary = null;
            for (const id of state.listeners.keys()) void loadRecord(id, { force: true }).catch(() => {});
          } else if (event?.data?.type === "download-status-changed" && state.listeners.has(appId)) {
            void loadRecord(appId, { force: true }).catch(() => {});
          }
        });
      }
    } catch {
      state.channel = null;
    }
  }

  // -----------------------------------------------------------------------
  // React component
  // -----------------------------------------------------------------------

  const css = `
    @keyframes tfs-omni-dl-sweep {
      0% { transform: translateX(-100%); }
      100% { transform: translateX(250%); }
    }
    .tfs-omni-dl-bar.is-indeterminate .tfs-omni-dl-fill {
      width: 40% !important;
      animation: tfs-omni-dl-sweep 1.4s ease-in-out infinite;
    }
    .tfs-omni-dl.is-attention {
      animation: tfs-omni-dl-pulse 1.2s ease-in-out 3;
    }
    @keyframes tfs-omni-dl-pulse {
      0%, 100% { background-color: rgba(14, 20, 27, 0.82); }
      50% { background-color: rgba(26, 159, 255, 0.28); }
    }
    @media (prefers-reduced-motion: reduce) {
      .tfs-omni-dl-bar.is-indeterminate .tfs-omni-dl-fill,
      .tfs-omni-dl.is-attention { animation: none !important; }
    }
  `;

  const toneColors = {
    neutral: "#8b929a",
    active: "#1a9fff",
    accent: "#1a9fff",
    warning: "#e8b65d",
    error: "#e36d74",
    ready: "#59bf40",
    attention: "#1a9fff",
  };

  let RowComponent = null;
  function getRowComponent(tfs) {
    if (RowComponent) return RowComponent;
    const React = tfs.React;
    const h = React.createElement;

    const styles = {
      bar: {
        display: "flex",
        alignItems: "center",
        gap: "24px",
        boxSizing: "border-box",
        width: "100%",
        minHeight: "64px",
        padding: "10px 2.8vw",
        background: "rgba(14, 20, 27, 0.82)",
        borderTop: "1px solid rgba(255, 255, 255, 0.06)",
        borderBottom: "1px solid rgba(0, 0, 0, 0.35)",
        color: "#ffffff",
      },
      dot: {
        flex: "0 0 auto",
        width: "10px",
        height: "10px",
        borderRadius: "50%",
      },
      copy: {
        display: "flex",
        flex: "1 1 auto",
        flexDirection: "column",
        gap: "4px",
        minWidth: 0,
      },
      title: {
        fontSize: "18px",
        lineHeight: "22px",
        fontWeight: 500,
        whiteSpace: "nowrap",
        overflow: "hidden",
        textOverflow: "ellipsis",
      },
      detail: {
        fontSize: "13px",
        lineHeight: "17px",
        color: "#8b929a",
        whiteSpace: "nowrap",
        overflow: "hidden",
        textOverflow: "ellipsis",
      },
      track: {
        position: "relative",
        height: "4px",
        marginTop: "4px",
        overflow: "hidden",
        borderRadius: "2px",
        background: "rgba(255, 255, 255, 0.1)",
      },
      fill: {
        height: "100%",
        borderRadius: "2px",
        transition: "width 600ms ease",
      },
      actions: {
        display: "flex",
        flex: "0 0 auto",
        gap: "12px",
      },
      button: {
        width: "auto",
        minWidth: "120px",
        height: "36px",
        padding: "0 18px",
        fontSize: "14px",
        lineHeight: "36px",
        whiteSpace: "nowrap",
      },
    };

    RowComponent = function TfsOmniLibraryDownloadRow({ overview }) {
      const appId = Number(overview?.appid) >>> 0;
      const managed = isManagedShortcutAppId(appId);
      const [, setTick] = React.useState(0);

      React.useEffect(() => {
        if (!managed) return undefined;
        let alive = true;
        let timer = 0;
        const rerender = () => { if (alive) setTick((value) => (value + 1) % 1000000); };
        const unsubscribe = subscribe(appId, rerender);
        const schedule = () => {
          window.clearTimeout(timer);
          if (!alive) return;
          const status = state.cache.get(appId)?.record?.game?.download?.status;
          const busy = busyStatuses.includes(String(status || "").toLowerCase());
          timer = window.setTimeout(tick, busy ? activePollMs : idlePollMs);
        };
        const tick = () => {
          loadRecord(appId).catch(() => {}).finally(schedule);
        };
        loadRecord(appId, { force: true }).catch(() => {}).finally(schedule);
        return () => {
          alive = false;
          window.clearTimeout(timer);
          unsubscribe();
        };
      }, [appId]);

      if (!managed) return null;
      const record = state.cache.get(appId)?.record;
      const hintAt = state.launchHints.get(appId) || 0;
      const view = describe(record, {
        hintActive: hintAt > 0 && Date.now() - hintAt < launchHintMs,
        busyAction: state.busy.get(appId) || "",
      });
      if (!view) return null;

      const DialogButton = tfs.ui?.DialogButton;
      const color = toneColors[view.tone] || toneColors.neutral;
      const showBar = view.progress !== null;
      return h("div", {
        className: `tfs-omni-dl is-${view.tone}${view.tone === "attention" ? " is-attention" : ""}`,
        "data-tfs-omni-download": appId,
        "data-status": view.status,
        role: "status",
        "aria-live": "polite",
        style: styles.bar,
      },
      h("span", { className: "tfs-omni-dl-dot", style: { ...styles.dot, background: color, boxShadow: `0 0 10px ${color}66` } }),
      h("div", { className: "tfs-omni-dl-copy", style: styles.copy },
        h("div", { className: "tfs-omni-dl-title", style: styles.title }, view.title),
        view.detail ? h("div", { className: "tfs-omni-dl-detail", style: styles.detail }, view.detail) : null,
        showBar
          ? h("div", { className: `tfs-omni-dl-bar${view.indeterminate ? " is-indeterminate" : ""}`, style: styles.track },
            h("div", { className: "tfs-omni-dl-fill", style: { ...styles.fill, width: `${view.progress}%`, background: color } }))
          : null),
      DialogButton && view.actions.length
        ? h("div", { className: "tfs-omni-dl-actions", style: styles.actions },
          // Stable keys keep Steam's focus on the same button when its
          // label changes (Download -> Pause -> Resume).
          view.actions.slice(0, 2).map((action, index) => h(DialogButton, {
            key: index === 0 ? "primary" : "secondary",
            className: `tfs-omni-dl-action tfs-omni-dl-action-${action.id}`,
            style: styles.button,
            onClick: () => { void runAction(appId, action.id); },
          }, action.label)))
        : null);
    };
    return RowComponent;
  }

  // -----------------------------------------------------------------------
  // Route patch: /library/app/:appid
  // -----------------------------------------------------------------------

  function copyIdentity(target, source) {
    try { Object.defineProperty(target, "name", { value: source.name, configurable: true }); } catch {}
    try { target.toString = () => String(source); } catch {}
  }

  /** Stable wrapper around Steam's app details sections component. */
  function wrapSections(tfs, Sections) {
    const cached = state.sectionWrappers.get(Sections);
    if (cached) return cached;
    const React = tfs.React;
    const h = React.createElement;
    const wrapped = function TfsOmniDownloadAppDetailsSections(props, ...rest) {
      // Marker for other TFS rows that look for Steam's sections component:
      const steamComponent = "AppDetailsRoot";
      void steamComponent;
      const output = Sections.call(this, props, ...rest);
      if (state.destroyed) return output;
      try {
        const appId = Number(props?.overview?.appid) >>> 0;
        if (!isManagedShortcutAppId(appId)) return output;
        const ErrorBoundary = tfs.ErrorBoundary;
        const row = h(getRowComponent(tfs), { overview: props?.overview });
        const element = React.createElement(
          ErrorBoundary || React.Fragment,
          ErrorBoundary ? { key: rowKey, name: "OmniLibrary Download" } : { key: rowKey },
          row);
        const result = insertAfterPlaySection(output, element, appDetailsRootClass(), React.cloneElement);
        if (result === output && !state.loggedMismatch) {
          state.loggedMismatch = true;
          console.warn("[TFS Downloads] Steam's game page layout changed; download row not inserted.");
        }
        return result;
      } catch (error) {
        console.warn("[TFS Downloads] render hook failed", error);
        return output;
      }
    };
    copyIdentity(wrapped, Sections);
    state.sectionWrappers.set(Sections, wrapped);
    return wrapped;
  }

  /** Stable wrapper around the route's renderFunc(overview, details). */
  function wrapRenderFunc(tfs, renderFunc) {
    const cached = state.renderFuncWrappers.get(renderFunc);
    if (cached) return cached;
    const React = tfs.React;
    const wrapped = function tfsOmniDownloadRenderFunc(...args) {
      const output = renderFunc.apply(this, args);
      if (state.destroyed) return output;
      try {
        // <Ctx.Provider value={Sections}><AppDetails …/></Ctx.Provider>
        const Sections = output?.props?.value;
        if (!isAppDetailsSections(tfs, Sections)) return output;
        return React.cloneElement(output, { value: wrapSections(tfs, Sections) });
      } catch (error) {
        console.warn("[TFS Downloads] route render hook failed", error);
        return output;
      }
    };
    copyIdentity(wrapped, renderFunc);
    state.renderFuncWrappers.set(renderFunc, wrapped);
    return wrapped;
  }

  function patchAppRoute(routeElement) {
    const tfs = core();
    const child = routeElement?.props?.children;
    const renderFunc = child?.props?.renderFunc;
    if (!tfs?.React || typeof renderFunc !== "function" || state.destroyed) return undefined;
    const React = tfs.React;
    return React.cloneElement(routeElement, undefined, React.cloneElement(child, { renderFunc: wrapRenderFunc(tfs, renderFunc) }));
  }

  async function register() {
    window.clearTimeout(state.retryTimer);
    if (state.destroyed || state.removePatch) return;
    const tfs = core();
    if (typeof tfs?.routes?.patch !== "function") {
      state.retryTimer = window.setTimeout(register, 2000);
      return;
    }
    try {
      await tfs.whenReady;
      if (state.destroyed || state.removePatch || !tfs.React) return;
      try { tfs.css?.add?.(cssId, css, { targets: ["gamepad"] }); } catch {}
      registerSteamEvents();
      state.removePatch = tfs.routes.patch(routePath, patchAppRoute, { id: patchId });
    } catch (error) {
      console.warn("[TFS Downloads] route patch failed", error);
      state.retryTimer = window.setTimeout(register, 10000);
    }
  }

  /**
   * True while Play cannot start this game (OmniLibrary game, not installed).
   * TFS Curtain asks before it paints, so Play on such a game stays quiet.
   */
  function blocksLaunch(appId) {
    const id = Number(appId) >>> 0;
    if (!isManagedShortcutAppId(id)) return false;
    const game = state.cache.get(id)?.record?.game;
    return Boolean(game && !game.installed && !game.cloudPlayable);
  }

  function refresh() {
    for (const appId of state.listeners.keys()) void loadRecord(appId, { force: true }).catch(() => {});
    if (!state.removePatch) void register();
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.retryTimer);
    try { state.removePatch?.(); } catch {}
    state.removePatch = null;
    try {
      const registration = state.lifetimeRegistration;
      if (typeof registration === "function") registration();
      else registration?.unregister?.();
    } catch {}
    state.lifetimeRegistration = null;
    try { state.channel?.close?.(); } catch {}
    state.channel = null;
    try { core()?.css?.remove?.(cssId); } catch {}
    state.listeners.clear();
  }

  window.__tfsOmniDownloads = {
    version,
    refresh,
    destroy,
    blocksLaunch,
    alive: () => !state.destroyed,
    get patched() {
      return Boolean(state.removePatch);
    },
    internals: { describe, installLabel, transferMetrics, insertAfterPlaySection, isManagedShortcutAppId, isAppDetailsSections, state },
  };

  void register();
  return "injected";
})();
