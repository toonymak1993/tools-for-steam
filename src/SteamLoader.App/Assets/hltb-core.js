// TFS HowLongToBeat runtime (SharedJSContext).
//
// Shows HowLongToBeat times on the Big Picture game page, directly below
// Steam's play section, the way the Decky "HLTB for Deck" plugin does:
//  - patches Steam's own route /library/app/:appid through tfs-core
//    (core.routes.patch), so the row is part of Steam's React tree and the
//    "View Details" button is a real Steam DialogButton the controller reaches;
//  - the game comes from Steam's app overview (appid + display name), no DOM
//    scraping;
//  - numbers come from the TFS backend (/api/hltb/game), which caches them.
// Independent code; inspired by hltb-for-deck (Decky Loader plugin).
(() => {
  const version = 1;
  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const routePath = "/library/app/:appid";
  const patchId = "tfs-hltb";
  const clientCacheMs = 15000;

  const existing = window.__tfsHltb;
  if (existing?.version === version && existing.alive?.()) {
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const state = {
    destroyed: false,
    removePatch: null,
    retryTimer: 0,
    cache: new Map(), // key -> { snapshot, at }
    inflight: new Map(), // key -> Promise
    listeners: new Set(),
    renderFuncWrappers: new WeakMap(),
    sectionWrappers: new WeakMap(),
    loggedMismatch: false,
    rootClass: "",
  };

  // -----------------------------------------------------------------------
  // Pure helpers (exposed for tests through window.__tfsHltb.internals)
  // -----------------------------------------------------------------------

  /** 23.6 -> "23½ Hours", 0.4 -> "24 Mins", 1 -> "1 Hour" (HowLongToBeat style). */
  function formatDuration(value) {
    const hours = Number.parseFloat(String(value ?? "").replace(",", "."));
    if (!Number.isFinite(hours) || hours <= 0) return "";
    if (hours < 1) {
      const minutes = Math.max(1, Math.round(hours * 60));
      return `${minutes} ${minutes === 1 ? "Min" : "Mins"}`;
    }
    let whole = Math.floor(hours);
    const fraction = hours - whole;
    let half = false;
    if (fraction >= 0.75) whole += 1;
    else if (fraction >= 0.25) half = true;
    const text = half ? `${whole}½` : String(whole);
    return `${text} ${whole === 1 && !half ? "Hour" : "Hours"}`;
  }

  /** The stats to show, in HowLongToBeat's order, honouring the QAM settings. */
  function visibleStats(snapshot) {
    const settings = snapshot?.settings || {};
    if (!snapshot?.found || !settings.enabled) return [];
    return [
      settings.showMainStory && { key: "main", label: "Main Story", value: formatDuration(snapshot.mainStory) },
      settings.showMainPlus && { key: "plus", label: "Main + Extras", value: formatDuration(snapshot.mainPlus) },
      settings.showCompletionist && { key: "100", label: "Completionist", value: formatDuration(snapshot.completionist) },
      settings.showAllStyles && { key: "all", label: "All Styles", value: formatDuration(snapshot.allStyles) },
    ].filter((entry) => entry && entry.value);
  }

  function cacheKey(appId, title) {
    return `${Number(appId) || 0}|${String(title || "").trim().toLowerCase()}`;
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

  async function fetchSnapshot(appId, title) {
    const query = new URLSearchParams();
    if (title) query.set("title", title);
    if (appId) query.set("appId", String(appId));
    const response = await fetch(`${apiBase()}api/hltb/game?${query}`, { cache: "no-store" });
    const payload = await response.json().catch(() => null);
    if (!response.ok || !payload) throw new Error(payload?.message || `HLTB request failed (${response.status})`);
    return payload;
  }

  function loadSnapshot(appId, title, { force = false } = {}) {
    const key = cacheKey(appId, title);
    const cached = state.cache.get(key);
    if (!force && cached && Date.now() - cached.at < clientCacheMs) return Promise.resolve(cached.snapshot);
    if (state.inflight.has(key)) return state.inflight.get(key);
    const request = fetchSnapshot(appId, title)
      .then((snapshot) => {
        state.cache.set(key, { snapshot, at: Date.now() });
        return snapshot;
      })
      .catch((error) => {
        // Keep showing what we had; try again on the next page visit.
        if (cached) return cached.snapshot;
        throw error;
      })
      .finally(() => state.inflight.delete(key));
    state.inflight.set(key, request);
    return request;
  }

  function openDetails(url) {
    if (!/^https:\/\/howlongtobeat\.com\//i.test(String(url || ""))) return;
    try {
      // Steam's own web browser (overlay in Big Picture), like Decky's
      // Navigation.NavigateToExternalWeb.
      if (typeof window.SteamClient?.URL?.ExecuteSteamURL === "function") {
        window.SteamClient.URL.ExecuteSteamURL(`steam://openurl/${url}`);
        return;
      }
    } catch {}
    void fetch(`${apiBase()}api/hltb/open-details`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ value: url }),
    }).catch(() => {});
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

  // -----------------------------------------------------------------------
  // React component
  // -----------------------------------------------------------------------

  let StatsComponent = null;
  function getStatsComponent(tfs) {
    if (StatsComponent) return StatsComponent;
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
      list: {
        display: "flex",
        flex: "1 1 auto",
        alignItems: "center",
        justifyContent: "space-around",
        gap: "16px",
        minWidth: 0,
        margin: 0,
        padding: 0,
        listStyle: "none",
      },
      item: {
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        gap: "2px",
        minWidth: 0,
        textAlign: "center",
      },
      value: {
        fontSize: "20px",
        lineHeight: "24px",
        fontWeight: 500,
        whiteSpace: "nowrap",
        color: "#ffffff",
      },
      label: {
        fontSize: "12px",
        lineHeight: "16px",
        fontWeight: 400,
        letterSpacing: "0.04em",
        textTransform: "uppercase",
        whiteSpace: "nowrap",
        color: "#8b929a",
      },
      buttonWrap: {
        flex: "0 0 auto",
      },
      button: {
        width: "auto",
        minWidth: "0",
        height: "36px",
        padding: "0 18px",
        fontSize: "14px",
        lineHeight: "36px",
        whiteSpace: "nowrap",
      },
    };

    StatsComponent = function TfsHltbStats({ overview }) {
      const appId = Number(overview?.appid) || 0;
      const title = String(overview?.display_name || overview?.strDisplayName || "").trim();
      const key = cacheKey(appId, title);
      const [snapshot, setSnapshot] = React.useState(() => state.cache.get(key)?.snapshot || null);

      React.useEffect(() => {
        let alive = true;
        setSnapshot(state.cache.get(key)?.snapshot || null);
        if (appId || title) {
          loadSnapshot(appId, title).then((next) => { if (alive) setSnapshot(next); }).catch(() => {});
        }
        const listener = () => {
          loadSnapshot(appId, title, { force: true }).then((next) => { if (alive) setSnapshot(next); }).catch(() => {});
        };
        state.listeners.add(listener);
        return () => {
          alive = false;
          state.listeners.delete(listener);
        };
      }, [key]);

      const stats = visibleStats(snapshot);
      if (!stats.length) return null;

      const DialogButton = tfs.ui?.DialogButton;
      const showDetails = snapshot.settings?.showViewDetails && snapshot.detailUrl && DialogButton;
      return h("div", { className: "tfs-hltb", "data-tfs-hltb": appId, style: styles.bar },
        h("ul", { className: "tfs-hltb-stats", style: styles.list },
          stats.map((stat) => h("li", { key: stat.key, className: "tfs-hltb-stat", style: styles.item },
            h("div", { className: "tfs-hltb-value", style: styles.value }, stat.value),
            h("div", { className: "tfs-hltb-label", style: styles.label }, stat.label)))),
        showDetails
          ? h("div", { className: "tfs-hltb-actions", style: styles.buttonWrap },
            h(DialogButton, {
              className: "tfs-hltb-details",
              style: styles.button,
              onClick: () => openDetails(snapshot.detailUrl),
            }, "View Details"))
          : null);
    };
    return StatsComponent;
  }

  // -----------------------------------------------------------------------
  // Route patch: /library/app/:appid
  // -----------------------------------------------------------------------

  function copyIdentity(target, source) {
    try { Object.defineProperty(target, "name", { value: source.name, configurable: true }); } catch {}
    try { target.toString = () => Function.prototype.toString.call(source); } catch {}
  }

  /** Stable wrapper around Steam's app details sections component. */
  function wrapSections(tfs, Sections) {
    const cached = state.sectionWrappers.get(Sections);
    if (cached) return cached;
    const React = tfs.React;
    const h = React.createElement;
    const wrapped = function TfsHltbAppDetailsSections(props, ...rest) {
      const output = Sections.call(this, props, ...rest);
      if (state.destroyed) return output;
      try {
        const ErrorBoundary = tfs.ErrorBoundary;
        const stats = h(getStatsComponent(tfs), { overview: props?.overview });
        const element = React.createElement(
          ErrorBoundary || React.Fragment,
          ErrorBoundary ? { key: "tfs-hltb", name: "HowLongToBeat" } : { key: "tfs-hltb" },
          stats);
        const result = insertAfterPlaySection(output, element, appDetailsRootClass(), React.cloneElement);
        if (result === output && !state.loggedMismatch) {
          state.loggedMismatch = true;
          console.warn("[TFS HLTB] Steam's game page layout changed; HLTB row not inserted.");
        }
        return result;
      } catch (error) {
        console.warn("[TFS HLTB] render hook failed", error);
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
    const wrapped = function tfsHltbRenderFunc(...args) {
      const output = renderFunc.apply(this, args);
      if (state.destroyed) return output;
      try {
        // Steam hands the page its sections component through a context
        // provider: <Ctx.Provider value={Sections}><AppDetails …/></Ctx.Provider>.
        const Sections = output?.props?.value;
        if (typeof Sections !== "function") return output;
        const source = Function.prototype.toString.call(tfs.patch?.getUnpatched?.(Sections) || Sections);
        if (!source.includes("AppDetailsRoot")) return output;
        return React.cloneElement(output, { value: wrapSections(tfs, Sections) });
      } catch (error) {
        console.warn("[TFS HLTB] route render hook failed", error);
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
      state.removePatch = tfs.routes.patch(routePath, patchAppRoute, { id: patchId });
    } catch (error) {
      console.warn("[TFS HLTB] route patch failed", error);
      state.retryTimer = window.setTimeout(register, 10000);
    }
  }

  /** Called after the HLTB settings change in the Quick Access menu. */
  function refresh() {
    state.cache.clear();
    for (const listener of [...state.listeners]) {
      try { listener(); } catch {}
    }
    if (!state.removePatch) void register();
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.retryTimer);
    try { state.removePatch?.(); } catch {}
    state.removePatch = null;
    state.listeners.clear();
  }

  window.__tfsHltb = {
    version,
    refresh,
    destroy,
    alive: () => !state.destroyed,
    get patched() {
      return Boolean(state.removePatch);
    },
    internals: { formatDuration, visibleStats, insertAfterPlaySection, cacheKey },
  };

  void register();
  return "injected";
})();
