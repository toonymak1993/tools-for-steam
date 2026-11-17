// TFS OmniLibrary store tabs as Steam's own Library tabs (SharedJSContext).
//
// UnifyDeck's approach, ported: Steam's Library route builds its tab list in
// one useMemo inside the tabbed Library component. That call is intercepted
// while the component renders, and one tab per enabled, prepared OmniLibrary
// store (Xbox, Xbox Cloud, Epic, GOG, each emulated platform) is spliced into
// the list Steam is about to render. Each tab is Steam's own tab object:
//  - content is Steam's own game grid (the component the All Games tab uses)
//    over a collection of the store's shortcuts, so sorting, filters, tiles,
//    focus and the game page are Steam's;
//  - the footer is copied from All Games, so LB/RB and the menu button work
//    exactly as on every other tab (no bumper hijacking, no DOM repair);
//  - the count beside the title uses Steam's own count style.
// Steam's Non-Steam tab no longer lists OmniLibrary games, and Steam's
// Installed tab no longer lists OmniLibrary games that are not installed.
//
// Nothing is polled while the Library is closed. If Steam's Library ever
// changes shape, the list is left exactly as Steam built it.
(() => {
  const version = 1;
  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const routePath = "/library";
  const patchId = "tfs-omnilibrary-tabs";
  const tabIdPrefix = "tfs-native-";
  const storeChannelName = "ToolsForSteamOmniLibraryStores";
  const refreshWhileMountedMs = 60000;
  const summaryRetryMs = [2000, 5000, 10000, 30000];
  const summaryCacheStorageKey = "ToolsForSteamOmniLibrarySummaryCacheV1";
  const summaryCacheMaxAgeMs = 14 * 24 * 60 * 60 * 1000;
  const summaryCacheWriteIntervalMs = 60000;
  const hydrationRetryLimit = 6;

  const existing = window.__tfsOmniTabs;
  if (existing?.version === version && existing.alive?.()) {
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const state = {
    destroyed: false,
    removePatch: null,
    retryTimer: 0,
    refreshTimer: 0,
    summary: null,
    lastCacheWriteAt: 0,
    hydrationTimer: 0,
    hydrationRetries: 0,
    channelTimer: 0,
    summaryRetry: 0,
    revision: 0,
    listeners: new Set(),
    badgeListeners: new Set(),
    mounted: 0,
    channel: null,
    wrappers: new WeakMap(),
    memoWrappers: new WeakMap(),
    loggedBail: new Set(),
    lastSpliceCount: -1,
    TabAppGrid: null,
    countClass: null,
    collectionCache: new Map(), // tabId -> { key, collection }
  };

  // -----------------------------------------------------------------------
  // Pure helpers (exposed for tests through window.__tfsOmniTabs.internals)
  // -----------------------------------------------------------------------

  function normalizeAppIds(values) {
    return Array.from(new Set((Array.isArray(values) ? values : [])
      .map((value) => Number(value) >>> 0)
      .filter((value) => value > 0)));
  }

  /**
   * Summary -> the tabs to show, in store order. Only stores that are on,
   * prepared and have games get a tab; the cloud tab also needs Xbox Cloud.
   */
  function resolveStoreTabs(summary) {
    if (!summary || summary.pluginEnabled !== true) return [];
    const tabs = [];
    const seen = new Set();
    for (const store of Array.isArray(summary.stores) ? summary.stores : []) {
      if (store?.enabled !== true || store?.readyForLibraryTab !== true) continue;
      const storeId = String(store.id || "").toLowerCase();
      const appIds = normalizeAppIds(store.appIds);
      const cloud = new Set(normalizeAppIds(store.cloudAppIds));
      const systems = new Map((Array.isArray(store.romSystems) ? store.romSystems : [])
        .map((system) => [String(system?.id || "").toLowerCase(), system]));
      for (const descriptor of Array.isArray(store.libraryTabs) ? store.libraryTabs : []) {
        const sourceId = String(descriptor?.id || "").trim();
        if (!sourceId) continue;
        const id = `${tabIdPrefix}${sourceId.replace(/^tfs-/, "")}`;
        if (seen.has(id)) continue;
        if (descriptor.requiresCloudSource === true && store.xboxCloudGamingEnabled !== true) continue;
        const filter = String(descriptor.filter || "all").toLowerCase();
        let ids;
        if (filter.startsWith("platform:")) {
          const system = systems.get(filter.slice("platform:".length));
          const inStore = new Set(appIds);
          ids = normalizeAppIds(system?.appIds).filter((appId) => inStore.has(appId));
        } else if (filter === "cloud") {
          ids = appIds.filter((appId) => cloud.has(appId));
        } else if (filter === "non-cloud") {
          ids = appIds.filter((appId) => !cloud.has(appId));
        } else {
          ids = appIds;
        }
        if (!ids.length) continue;
        seen.add(id);
        tabs.push({
          id,
          storeId,
          title: String(descriptor.title || store.title || storeId),
          appIds: ids,
        });
      }
    }
    return tabs;
  }

  /**
   * managed: every OmniLibrary game; visible: games of stores that show a
   * tab; playable: visible games that are installed.
   */
  function resolveManagedSets(summary) {
    const managed = new Set();
    const visible = new Set();
    const playable = new Set();
    if (!summary || summary.pluginEnabled !== true) return { managed, visible, playable };
    const shown = new Set(resolveStoreTabs(summary).map((tab) => tab.storeId));
    for (const store of Array.isArray(summary.stores) ? summary.stores : []) {
      const appIds = normalizeAppIds(store?.appIds);
      for (const appId of appIds) managed.add(appId);
      if (!shown.has(String(store?.id || "").toLowerCase())) continue;
      for (const appId of appIds) visible.add(appId);
      const inStore = new Set(appIds);
      // Installed means on disk, as in Steam's own tab: streaming-only games
      // stay out of it (they are in their store tab and All Games).
      for (const appId of normalizeAppIds(store.installedAppIds)) {
        if (inStore.has(appId)) playable.add(appId);
      }
    }
    return { managed, visible, playable };
  }

  /** Our tabs go right after Steam's Non-Steam tab (else before Soundtracks, else last). */
  function mergeTabs(steamTabs, ourTabs) {
    const native = steamTabs.filter((tab) => !String(tab?.id || "").startsWith(tabIdPrefix));
    if (!ourTabs.length) return native;
    const desktop = native.findIndex((tab) => tab?.id === "DesktopApps");
    const soundtracks = native.findIndex((tab) => tab?.id === "Soundtracks");
    const index = desktop >= 0 ? desktop + 1 : soundtracks >= 0 ? soundtracks : native.length;
    return [...native.slice(0, index), ...ourTabs, ...native.slice(index)];
  }

  /** Steam's tab list as the useMemo returns it: a bare array or [tabs, ...rest]. */
  function detectTabsShape(result) {
    if (!Array.isArray(result) || !result.length) return null;
    if (Array.isArray(result[0]) && result[0][0]?.id && result[0][0]?.content) {
      return { tuple: true, tabs: result[0], rest: result.slice(1) };
    }
    if (result[0]?.id && result[0]?.content) return { tuple: false, tabs: result, rest: [] };
    return null;
  }

  // -----------------------------------------------------------------------
  // Steam lookups
  // -----------------------------------------------------------------------

  function core() {
    return window.__tfs || null;
  }

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  function bailOnce(reason, extra) {
    if (state.loggedBail.has(reason)) return;
    state.loggedBail.add(reason);
    console.warn(`[TFS OmniLibrary Tabs] left Steam's tabs unchanged: ${reason}`, extra ?? "");
  }

  function getHooksDispatcher(React) {
    try {
      const legacy = React?.__SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED?.ReactCurrentDispatcher;
      if (legacy?.current?.useMemo) return { holder: legacy, key: "current" };
      const client = React?.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;
      if (client && typeof client === "object") {
        for (const key of Object.keys(client)) {
          const candidate = client[key];
          if (candidate?.useMemo && candidate.useEffect) return { holder: client, key };
        }
      }
    } catch {}
    return null;
  }

  function overviewFor(appId) {
    try {
      return window.appStore?.GetAppOverviewByAppID?.(appId) || null;
    } catch {
      return null;
    }
  }

  function findCountClass(tfs) {
    if (state.countClass !== null) return state.countClass;
    try {
      const classes = tfs?.webpack?.findModule?.((candidate) =>
        candidate && typeof candidate === "object" &&
        typeof candidate.TabCount === "string" &&
        typeof candidate.TabTitle === "string");
      state.countClass = classes?.TabCount || "";
    } catch {
      state.countClass = "";
    }
    return state.countClass;
  }

  function findInTree(node, predicate, depth = 0) {
    if (!node || typeof node !== "object" || depth > 40) return null;
    if (predicate(node)) return node;
    const children = node.props?.children;
    const list = Array.isArray(children) ? children : [children];
    for (const child of list) {
      const found = findInTree(child, predicate, depth + 1);
      if (found) return found;
    }
    return null;
  }

  function typeSource(type) {
    try {
      const inner = typeof type === "function" ? type : type?.type || type?.render;
      // type.toString() like UnifyDeck: patched components forward it to
      // Steam's original source.
      return typeof inner === "function" ? String(inner.toString()) : "";
    } catch {
      return "";
    }
  }

  function safeMatches(filter, app) {
    try {
      return filter.Matches(app);
    } catch {
      return true;
    }
  }

  // -----------------------------------------------------------------------
  // Collections
  // -----------------------------------------------------------------------

  function countWithFilter(apps, appFilter) {
    if (typeof appFilter?.Matches !== "function") return apps.length;
    let count = 0;
    for (const app of apps) if (safeMatches(appFilter, app)) count += 1;
    return count;
  }

  // Steam may not know a freshly added shortcut yet. Re-render a few times,
  // spaced out, instead of leaving the tab short until something else moves.
  function scheduleHydrationRetry() {
    if (state.hydrationTimer || state.hydrationRetries >= hydrationRetryLimit) return;
    state.hydrationRetries += 1;
    state.hydrationTimer = window.setTimeout(() => {
      state.hydrationTimer = 0;
      if (!state.destroyed) notify();
    }, 1500 * state.hydrationRetries);
  }

  /** A Steam-shaped, read-only collection over our shortcuts. */
  function buildCollection(tab) {
    const key = `${state.revision}:${tab.appIds.join(",")}`;
    const cached = state.collectionCache.get(tab.id);
    if (cached?.key === key && cached.collection.allApps.length) return cached.collection;
    const apps = [];
    for (const appId of tab.appIds) {
      const overview = overviewFor(appId);
      if (overview) apps.push(overview);
    }
    if (apps.length < tab.appIds.length) scheduleHydrationRetry();
    const map = new Map(apps.map((app) => [app.appid, app]));
    const collection = {
      AsDeletableCollection: () => null,
      AsDragDropCollection: () => null,
      AsEditableCollection: () => null,
      GetAppCountWithToolsFilter: (appFilter) => countWithFilter(collection.visibleApps, appFilter),
      bAllowsDragAndDrop: false,
      bIsDeletable: false,
      bIsDynamic: false,
      bIsEditable: false,
      displayName: tab.title,
      id: tab.id,
      allApps: apps,
      visibleApps: [...apps],
      apps: map,
    };
    state.collectionCache.set(tab.id, { key, collection });
    return collection;
  }

  /**
   * Steam's own collection with OmniLibrary games adjusted: `keep` drops
   * Steam's entries, `extraIds` adds OmniLibrary games Steam does not list.
   */
  function adjustedCollection(original, keep, extraIds) {
    if (!original || typeof original !== "object") return original;
    const extras = () => {
      const result = [];
      for (const appId of extraIds) {
        const overview = overviewFor(appId);
        if (overview) result.push(overview);
      }
      return result;
    };
    const memo = new Map(); // key -> { source, value }
    const adjust = (key) => {
      const source = Reflect.get(original, key, original);
      const cached = memo.get(key);
      if (cached?.source === source) return cached.value;
      let value = source;
      if (Array.isArray(source)) {
        value = source.filter(keep);
        if (extraIds.length) {
          const present = new Set(value.map((app) => Number(app?.appid) >>> 0));
          for (const app of extras()) if (!present.has(Number(app.appid) >>> 0)) value.push(app);
        }
      }
      memo.set(key, { source, value });
      return value;
    };
    return new Proxy(original, {
      get(target, key) {
        if (key === "visibleApps" || key === "allApps") return adjust(key);
        if (key === "GetAppCountWithToolsFilter") {
          return (appFilter) => countWithFilter(adjust("visibleApps") || [], appFilter);
        }
        const value = Reflect.get(target, key, target);
        return typeof value === "function" ? value.bind(target) : value;
      },
    });
  }

  function countAddon(React, collection, appFilter) {
    return () => React.createElement(
      "span",
      { className: findCountClass(core()) },
      (() => {
        try { return collection.GetAppCountWithToolsFilter(appFilter); } catch { return collection.visibleApps?.length || 0; }
      })(),
    );
  }

  /** The tab's content element with its collection replaced. */
  function withCollection(React, content, collection) {
    const replace = (node, depth = 0) => {
      if (!node || typeof node !== "object" || depth > 40) return node;
      if (node.props && "collection" in node.props) return React.cloneElement(node, { collection });
      const children = node.props?.children;
      if (children == null) return node;
      if (Array.isArray(children)) {
        let changed = false;
        const next = children.map((child) => {
          const replaced = replace(child, depth + 1);
          changed ||= replaced !== child;
          return replaced;
        });
        return changed ? React.cloneElement(node, undefined, ...next) : node;
      }
      const replaced = replace(children, depth + 1);
      return replaced !== children ? React.cloneElement(node, undefined, replaced) : node;
    };
    return replace(content);
  }

  function collectionOf(content) {
    return findInTree(content, (node) => node?.props && "collection" in node.props)?.props?.collection || null;
  }

  // -----------------------------------------------------------------------
  // The splice
  // -----------------------------------------------------------------------

  function spliceTabs(result, deps) {
    const shape = detectTabsShape(result);
    if (!shape) return result;
    if (shape.tabs.some((tab) => String(tab?.id || "").startsWith(tabIdPrefix))) return result;
    const tfs = core();
    const React = tfs?.React;
    if (!React) return result;

    const appFilter = (deps || []).find((dep) => typeof dep?.Matches === "function");
    const ourTabs = resolveStoreTabs(state.summary);
    const { managed, visible, playable } = resolveManagedSets(state.summary);
    if (!ourTabs.length && !managed.size) return result;

    const template = shape.tabs.find((tab) => tab?.id === "AllGames");
    if (!template) {
      bailOnce("no All Games tab", shape.tabs.map((tab) => tab?.id));
      return result;
    }
    const grid = state.TabAppGrid || findInTree(template.content, (node) =>
      typeSource(node?.type).includes("Library_FilteredByHeader"))?.type;
    if (!grid) {
      bailOnce("Steam's library grid was not found in All Games");
      return result;
    }
    state.TabAppGrid = grid;
    const TabContext = template.content?.type?._context || null;
    const [eSortBy, setSortBy, showSortingContextMenu] = deps || [];
    const footer = { ...(template.footer || {}) };
    let ErrorBoundary = null;
    try { ErrorBoundary = tfs.ErrorBoundary || null; } catch {}

    const injected = ourTabs.map((tab) => {
      const collection = buildCollection(tab);
      const gridElement = React.createElement(grid, { collection, eSortBy, setSortBy, showSortingContextMenu });
      // A failing store tab must not take Steam's whole Library down.
      const inner = ErrorBoundary
        ? React.createElement(ErrorBoundary, { name: `OmniLibrary ${tab.title}` }, gridElement)
        : gridElement;
      return {
        id: tab.id,
        title: tab.title,
        footer: { ...footer },
        content: TabContext
          ? React.createElement(TabContext.Provider, { value: { label: tab.title } }, inner)
          : inner,
        renderTabAddon: countAddon(React, collection, appFilter),
      };
    });

    // Steam's All Games / Installed / Non-Steam tabs, as before this module:
    // store games sit in All Games (Installed: when installed),
    // never in Non-Steam, and games of hidden stores are nowhere.
    const appIdOf = (app) => Number(app?.appid) >>> 0;
    const adjustments = {
      AllGames: { keep: (app) => !managed.has(appIdOf(app)) || visible.has(appIdOf(app)), extra: [...visible] },
      Installed: { keep: (app) => !managed.has(appIdOf(app)) || playable.has(appIdOf(app)), extra: [...playable] },
      DesktopApps: { keep: (app) => !managed.has(appIdOf(app)), extra: [] },
    };
    const steamTabs = shape.tabs.map((tab) => {
      const adjustment = managed.size ? adjustments[tab?.id] : null;
      if (!adjustment) return tab;
      const original = collectionOf(tab.content);
      if (!original) return tab;
      const collection = adjustedCollection(original, adjustment.keep, adjustment.extra);
      return {
        ...tab,
        content: withCollection(React, tab.content, collection),
        renderTabAddon: tab.renderTabAddon ? countAddon(React, collection, appFilter) : tab.renderTabAddon,
      };
    });

    const merged = mergeTabs(steamTabs, injected);
    if (state.lastSpliceCount !== injected.length) {
      state.lastSpliceCount = injected.length;
      console.log(`[TFS OmniLibrary Tabs] ${injected.length} store tab(s) in Steam's Library`);
    }
    return shape.tuple ? [merged, ...shape.rest] : merged;
  }

  /** Runs `render` with Steam's useMemo calls passing through spliceTabs. */
  function renderWithSplice(React, render) {
    const dispatcher = getHooksDispatcher(React);
    if (!dispatcher) {
      bailOnce("React's hook dispatcher is not reachable");
      return render();
    }
    const { holder, key } = dispatcher;
    const current = holder[key];
    const realUseMemo = current.useMemo;
    const revision = state.revision;
    current.useMemo = function tfsOmniTabsUseMemo(factory, deps) {
      if (!Array.isArray(deps)) return realUseMemo.call(this, factory, deps);
      const nextDeps = [...deps, revision];
      return realUseMemo.call(this, () => {
        const value = factory();
        try {
          return spliceTabs(value, deps);
        } catch (error) {
          bailOnce("splice failed", error);
          return value;
        }
      }, nextDeps);
    };
    try {
      return render();
    } finally {
      current.useMemo = realUseMemo;
    }
  }

  // -----------------------------------------------------------------------
  // Route patch: /library -> its component -> the tabbed (memo) component
  // -----------------------------------------------------------------------

  // Carried on the elements we render so a change to our tabs re-renders
  // Steam's memoised Library; stripped before Steam's components see props.
  const revisionProp = "__tfsOmniTabsRevision";

  function copyIdentity(target, source) {
    try { Object.defineProperty(target, "name", { value: source?.name || "", configurable: true }); } catch {}
    try { target.toString = () => Function.prototype.toString.call(source); } catch {}
  }

  function withoutRevision(args) {
    const props = args[0];
    if (!props || typeof props !== "object" || !(revisionProp in props)) return { args, revision: undefined };
    const { [revisionProp]: revision, ...clean } = props;
    return { args: [clean, ...args.slice(1)], revision };
  }

  function isClass(fn) {
    return typeof fn === "function" && !!fn.prototype?.isReactComponent;
  }

  /** The tabbed Library component: a memo whose inner render owns the tabs useMemo. */
  function wrapTabbedMemo(React, memo) {
    const cached = state.memoWrappers.get(memo);
    if (cached) return cached;
    const inner = memo.type;
    const render = function tfsOmniTabsLibrary(...rawArgs) {
      const { args } = withoutRevision(rawArgs);
      if (state.destroyed) return inner.apply(this, args);
      return renderWithSplice(React, () => inner.apply(this, args));
    };
    copyIdentity(render, inner);
    const wrapped = { ...memo, type: render };
    // A custom compare could ignore our revision prop and keep stale tabs.
    if (typeof memo.compare === "function") {
      const compare = memo.compare;
      wrapped.compare = (previous, next) =>
        previous?.[revisionProp] === next?.[revisionProp] && compare(previous, next);
    }
    state.memoWrappers.set(memo, wrapped);
    return wrapped;
  }

  /** Steam's outer Library component; its output names the tabbed memo component. */
  function wrapLibraryComponent(React, fn) {
    const cached = state.wrappers.get(fn);
    if (cached) return cached;
    const wrapped = function tfsOmniTabsLibraryRoot(...rawArgs) {
      const { args, revision } = withoutRevision(rawArgs);
      const output = fn.apply(this, args);
      if (state.destroyed) return output;
      try {
        let node = output;
        // Steam renders the memo either directly or one function level down.
        if (typeof node?.type === "function" && !isClass(node.type)) {
          node = React.createElement(wrapLibraryComponent(React, node.type), {
            ...node.props,
            key: node.key,
            ref: node.ref,
            [revisionProp]: revision,
          });
          return node;
        }
        const memo = node?.type;
        if (memo && typeof memo === "object" && typeof memo.type === "function" && !isClass(memo.type)) {
          return React.createElement(wrapTabbedMemo(React, memo), {
            ...node.props,
            key: node.key,
            ref: node.ref,
            [revisionProp]: revision,
          });
        }
        bailOnce("the Library component has an unexpected shape");
        return output;
      } catch (error) {
        bailOnce("Library patch failed", error);
        return output;
      }
    };
    copyIdentity(wrapped, fn);
    state.wrappers.set(fn, wrapped);
    return wrapped;
  }

  /**
   * Keeps the summary fresh while the Library is open and re-renders it when
   * our tabs change. Rendered around Steam's own Library element.
   */
  function LibraryMount({ element }) {
    const React = core()?.React;
    const [revision, rerender] = React.useReducer((value) => value + 1, 0);
    React.useEffect(() => {
      state.listeners.add(rerender);
      state.mounted += 1;
      void refreshSummary();
      scheduleMountedRefresh();
      return () => {
        state.listeners.delete(rerender);
        state.mounted = Math.max(0, state.mounted - 1);
        if (!state.mounted) window.clearTimeout(state.refreshTimer);
      };
    }, []);
    return React.cloneElement(element, { [revisionProp]: `${state.revision}:${revision}` });
  }

  function patchLibraryRoute(routeElement) {
    const tfs = core();
    const React = tfs?.React;
    const child = routeElement?.props?.children;
    if (!React || state.destroyed || !child || typeof child !== "object") return undefined;
    const type = child.type;
    if (typeof type !== "function" || isClass(type)) {
      bailOnce("the Library route has an unexpected shape");
      return undefined;
    }
    const element = { ...child, type: wrapLibraryComponent(React, type) };
    return React.cloneElement(
      routeElement,
      undefined,
      React.createElement(LibraryMount, { key: "tfs-omni-tabs", element }),
    );
  }

  // -----------------------------------------------------------------------
  // Data
  // -----------------------------------------------------------------------

  function summarySignature(summary) {
    const tabs = resolveStoreTabs(summary).map((tab) => `${tab.id}:${tab.title}:${tab.appIds.join(",")}`).join("|");
    const { managed, visible, playable } = resolveManagedSets(summary);
    return [tabs, managed, visible, playable].map((part) => typeof part === "string" ? part : [...part].sort().join(",")).join("#");
  }

  function notifyBadges() {
    for (const listener of [...state.badgeListeners]) {
      try { listener(); } catch {}
    }
  }

  function notify() {
    state.revision += 1;
    state.collectionCache.clear();
    for (const listener of [...state.listeners]) {
      try { listener(); } catch {}
    }
    notifyBadges();
  }

  /** Install and download state only changes the tile badges, not the tabs. */
  function badgeSignature(summary) {
    return [...resolveTileIndex(summary)]
      .map(([appId, info]) => `${appId}:${info.storeId}:${tileStatus(info)}`)
      .join(",");
  }

  // The last good summary (shared with the Library surface) lets the store
  // tabs appear the moment Steam starts, before TFS has answered.
  function readCachedSummary() {
    try {
      const document = JSON.parse(window.localStorage?.getItem(summaryCacheStorageKey) || "null");
      const snapshot = document?.snapshot;
      if (Date.now() - Number(document?.savedAt || 0) >= summaryCacheMaxAgeMs ||
        snapshot?.pluginEnabled !== true || !Array.isArray(snapshot?.stores)) {
        return null;
      }
      return { ...snapshot, revision: -1, cached: true };
    } catch {
      return null;
    }
  }

  function writeCachedSummary(snapshot) {
    if (!snapshot || snapshot.backendUnavailable || snapshot.cached) return;
    const now = Date.now();
    if (now - state.lastCacheWriteAt < summaryCacheWriteIntervalMs) return;
    state.lastCacheWriteAt = now;
    try {
      window.localStorage?.setItem(summaryCacheStorageKey, JSON.stringify({ savedAt: now, snapshot }));
    } catch {}
  }

  function scheduleRefresh(delayMs) {
    window.clearTimeout(state.channelTimer);
    state.channelTimer = window.setTimeout(() => { void refreshSummary(); }, delayMs);
  }

  async function refreshSummary() {
    if (state.destroyed) return;
    window.clearTimeout(state.retryTimer);
    try {
      const response = await fetch(`${apiBase()}api/unifystore/summary`, { cache: "no-store" });
      if (!response.ok) throw new Error(`summary ${response.status}`);
      const summary = await response.json();
      state.summaryRetry = 0;
      writeCachedSummary(summary);
      const changed = summarySignature(summary) !== summarySignature(state.summary);
      const badgesChanged = !changed && badgeSignature(summary) !== badgeSignature(state.summary);
      state.summary = summary;
      if (changed) {
        state.hydrationRetries = 0;
        notify();
      } else if (badgesChanged) {
        notifyBadges();
      }
    } catch {
      const delay = summaryRetryMs[Math.min(state.summaryRetry, summaryRetryMs.length - 1)];
      state.summaryRetry += 1;
      state.retryTimer = window.setTimeout(() => { void refreshSummary(); }, delay);
    }
  }

  function scheduleMountedRefresh() {
    window.clearTimeout(state.refreshTimer);
    if (state.destroyed || !state.mounted) return;
    state.refreshTimer = window.setTimeout(() => {
      void refreshSummary().finally(scheduleMountedRefresh);
    }, refreshWhileMountedMs);
  }

  function listenForStoreChanges() {
    try {
      if (state.channel || typeof window.BroadcastChannel !== "function") return;
      state.channel = new window.BroadcastChannel(storeChannelName);
      state.channel.addEventListener("message", (event) => {
        const type = event?.data?.type;
        if (type === "stores-changed") scheduleRefresh(300);
        else if (type === "download-status-changed") scheduleRefresh(1500);
      });
    } catch {
      state.channel = null;
    }
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
      listenForStoreChanges();
      state.summary ||= readCachedSummary();
      void refreshSummary();
      state.removePatch = tfs.routes.patch(routePath, patchLibraryRoute, { id: patchId });
      installTileBadges();
    } catch (error) {
      console.warn("[TFS OmniLibrary Tabs] route patch failed", error);
      state.retryTimer = window.setTimeout(register, 10000);
    }
  }

  function refresh() {
    void refreshSummary();
    if (!state.removePatch) void register();
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.retryTimer);
    window.clearTimeout(state.refreshTimer);
    window.clearTimeout(state.channelTimer);
    window.clearTimeout(state.hydrationTimer);
    try { state.removePatch?.(); } catch {}
    state.removePatch = null;
    uninstallTileBadges();
    try { state.channel?.close?.(); } catch {}
    state.channel = null;
    const listeners = [...state.listeners, ...state.badgeListeners];
    state.listeners.clear();
    state.badgeListeners.clear();
    for (const listener of listeners) {
      try { listener(); } catch {}
    }
  }

  // -----------------------------------------------------------------------
  // Library tile store badges (UnifyDeck's tile-store-badge-patch, ported)
  // -----------------------------------------------------------------------
  //
  // Steam hides the Deck badge on shortcuts, so their tiles have no corner
  // badge. The library tile (a React.memo whose module source names
  // LibraryItemIcons, BIsModOrShortcut and BIsMusicAlbum) gets a wrapped
  // render that adds the game's store logo to the tile's icon row, in
  // Steam's own Deck-badge container: bottom right, shown on highlight,
  // exactly like the Deck badge of a Steam game. A small glyph beside it
  // says "not installed", "streams" or "downloading". Only shortcuts are
  // touched, nothing is written to Steam's stores, and any failure leaves
  // the tile as Steam rendered it.

  const tileBadgeKey = "tfs-omni-store-badge";
  const tileWrapperBrand = "__tfsOmniTileWrapper";
  const tileOriginalKey = "__tfsOmniTileOriginal";
  const tileInstallAttempts = 40;
  const tileInstallRetryMs = 3000;
  const storeBadgeMarks = {
    "xbox-game-pass": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAYAAABXAvmHAAAEQElEQVR4AcXBUWicBwEA4O/+u4dDjvELRQ48oQ+BRawjSB6KDRrx0Jc+FBqk0wgdDNqHIN1W2MYGK+jAQYerFpZqZUq7ZULrKl3phh25LBmbkEBhDXSj4B6SkYc+nBAww59g4A/8u93/X/7/kjTfV7KzQgxiAA1UxdawhHu4i7YdUrI9FYziKEYxKJ+7aOEqWoj0qaQ/ISZwAg3bs4QLOI+2gkqKqeI0nkJoZ7XxMs5iTU4lxXyMA3bXHXxXToFilu2+ZQWUFBdiGMNo4oeo6E+EGdzCPObRVkBJtgpew0G8iL9IV8MRnMQh+XyASVzDqnTH8Rw+wmOIpCjJdgnjErfxBFqyDeEUfoGKThFexyu4Ldsofochicv4pRRl6Z7BkzrVcRwNzGJNtxVcw3V8G/vFZnAUk1iRLsQf8HvUdXoEX2DOV5R0G8E0KrIt4THc0tthsbf11sRraMgW4UeY8yVlnaq4gW/o7SH8HF/DDNal+xSfylbBi/gjHtJbgO/jAtZtKuv0DH4mnwAj+AHexapi6vgHxuW3D//D+zaVJUL8DVXF7McxzOJz+QzjPTyiuO/hAtZsCCQmEOpPA5fldxkN/QkxYVMgccL2XJbfm7bnhE2BWBMN/TuH38jvDM7pXwOjNpTFfoWD+nMOpxT3Dr6Og/rzH7wTiP1Uf/6MU/p3Cn/Vnx/bECDEoOJu4KRsNfwJr6Im2+N4V3EHEAYYUNwCjiGSroabeBwncRM16SKMYUFxAwEGFbOCI1iVro5ZjEiMYBZ16VZxBCuKGQxQk1+ER7Ek3QCmMaTbEKaxX7olPIpIfrUyDmNUPk/jDekGMYP9su3DMdzAfd0+w3/xE/ksBPJ7D2elG8GHqNtaHbMYke4sZuQTBfjM1to4Lt0IbiKU3z7cxIh042jb2lKAyNaexZJuY5hGTXE1/BNjui3hWVtbC3BXbwu4qNsYplDRvyqmMKbbRSzo7V6AO4hkO41IpzFMoWL7KpjCmE4RTssW4U6ANSxKN4OWTk1MoWLnVDCFpk4tzEi3iLVArCXdGZ2G8RYqdl4Fb2FIpzPStWwIxK7otoiWRB3XUbN7ariOukQLi7pdsSEQm8OyTpMSFVxC3e5r4BIqEpM6LWPOhkDiok5vSkyg6cFpYkLiik7nbQokXkFb7APcF2vg1x68F9AQW8G/xNqYtCmQaOMlsbclnkfNgxfieYlrYi+hbVNJt2HcRoQ6/o2qvbGGb+E+KhjCvC8JdJtHJDaOqr1TxbhYhHlfEejtiL03podAthCH7L1DCGUIZGvjqr13FW0Zynr7Ox7GAXvjdYxjXYay3tZxBV9gFIEHI8JzeBLreijLZw7TOIR9dtcnOIo35FBSTBWn8RRCO6uNl/FbRHIq6U+ICZzEN23PMiZxHm0FlWxfE4fRxHfks4hbuIaWbSjZWSEGMIga6mIrWMVd3EPbDvk/u8L61oJzvgcAAAAASUVORK5CYII=",
    "epic-games": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAYAAABXAvmHAAADqklEQVR4AdXBT2jVdQAA8M/7+YXeYYcHvcNgLxw5aNDAHYQWCQ3qICSpkLiD4CGhhQaBCoYH7dQhwx2UTRIyDBy0g4HCDh2UEoIWLZiguGDCExbs8A4L3uFRDX4Pvr3e3m9vf565zyentQJKKIq6kdeohGBjaihrVMWiaAllVKwip9kgPsPbCJ4PNXyPM5jzLzmN9uNb5D2fqjiEaXWJqIgbyHt+5XETRXWJaBQFz78CRtUloiHbx5C6RNRt+yiqC6Ki1n7HLJYR8CZKontYkCrhNXRpVsU97Ea3Zr/gIfIYxC6r61YXZKvhI0xoVMBjFPEFTmuUx1mcQxAdxm2U8Bh5qTIOYUajk7iEoIVEtiuY0KyCRalpzaq4gE80mpMq4w/RIcxodhlXZUhEPZpdEw1gGMO4iAGpmqgfJdEYnsj2ADNSAXsxIJrQ7EV1QRQ0mxfdxk7ZzuIIXscsariNE1pbEu3BD3iCXql5zbrUJbJVrV8eg6Il7ctrVpUhsc0F7SuLehB0zguiogxB+/aK+vEr8jqjGxcwhXMyBBvzEI+wW+ecx3lrCFIFayshSO3Fq5ot4R5uifp1UJAqWNuP2CnbaY2KeEdnlFAOOifgK3TZmMO4hQs4p1mwIrH1unAQP2O/jXmKKdRwUYZga13HMZtXE1VkSGxfS1YkUlWr6xJVdV5FqirqsrplK4LUotX1Y0ZqCAWpYXyJYPN6RLN4CcuifhkS2U6JKljAAq7jgVTe5vThoKiMiuiUDIlsIxhHQaM+7JI6goAi3pJtn9Qe9IhuYARBVMA4RmTIif7WWg1lUTfyokV0ocvayuhG0KyCilQJQWs5K4L2BPRqrVv7SloroGAdEttcYptLRE9tH0/UJaKabShoz6dY8Gz14rw1BNGy1nZhDBXPRgHjWquoC6IlrR3FMM5gUmeN4HOUtFZRF0RV2Uq4ifdxBrO21iAuYdjaqup2iPZgyNpexigG8QiLNmcQ4xhDr/bcwbQVO0SLGNW+foxiCH9iHn9pT8C7uISL6Lc+H2LRipxGE/jAxpQxhTu4i5pGAcM4gIMo2ZirGFWX0yhgDCdsThVz+ElqCAPI25wr+Bg1dTmrG8BJHEPe/6uKr3EZc/4jJ1sBx3Ecr3i2HuEarqGihZz2DWMUB5DXGVV8hwnc1Yac9StgBEfxhq1xH99gEhXrkLM5fRjBe9htfX7DFCYxb4Nytk4fRrAPb1jdfUxjEvO2QE5nFDGMAak53MWSLfYP/QTBMrEBxfwAAAAASUVORK5CYII=",
    "gog-galaxy": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAYAAABXAvmHAAAD1klEQVR4Ad3BMWubZwIA4Edf3sHDN3jI4CFbBBbUww0eM3i5TaAMBQ3u0B9QiMEKuGRwSjlusBeTdL5bOvToIIO2W/QDBF1caCDaMpjDg4dvyPCCT/AK3qr6/EVpHWr1eay7ltVsYhOb2JQ8QpBtYcNq3uNSFvFOcoUK17j2AS31dvAMT9Dx5/oFY3yHC7/RsuwlXiC4XyL+gZd+5YFFL3GMwv1TYA8tjM0Vsh28cP+9wI65QnaA4P4L+MpcIXtifTwxV8i2rY/PzBWSTeunNBMkm5pNMUQlK/EUjy2bYohKUuIpHls2xRCVrMRTPHa7h6iCZNPtzjBAtOwIp3gmO8MA0aIjnOKZ7AwDRMuOcIpn6pVmCsmmelMMENWLGGAqmWKAaFnEAFPJFANE9SIGmKr30EzQbIgo2UVXNsIEEUMcYogo2UVXMsIEEUMcYogo2UVXNsIEEUMcukWQPFKvknVxbNFEUkkqWRfHsomkklSyLo4tmkgq9bbMFJJg/WyYCe5WKRvJRrLSHQruVg9HiJhgYlFAzx0KkkfqlbKRpIO+em2c4DmiRQGv0JaUspGkg77VbJkJkqBeD0eImGAiiW53gC7OUUlK9NCW9XCEiAkmkmg1G2aCZm2c4DmiLGjWxqFmbZzgOaIs+AjBhx2gi3NU2EYf3/jjDtDFOSpso49vrChYTRuHPo02Dv1OQbOIH/DWspGs9PEifsBby0ayUoOg2ff4UrOAno/3Pb7ULKCnQaFZ0CzgFdo+XtAs4BXaGgTN9nGDqWUlemj7ffZxg6llJXpo+4Dgw77w6XzhDyqsuSCJmk0xRCXroC+LOMeFbAc9BIumGKLSrIO+epWZIHnndmcYIFoWsY9L/B0Xlu3gv9iSnGGAaDUR+5ZdmSk0m2KAqF6QfI0L9S7wtWSKAaLVvdUgaDZElOyiK9tGX/KjbIASFU4lP+JfGCJKdtHFGGPJHvYwxtgKgiSqV8m6OFavkp3ITiWVpJJ1cSwZS/ZwLBlr9t5MIXln/VyaCZqVspFFHfQlJSrJc5SoZKVPJEiu1evhCBETTCyK2Mfn+Lfk1LLP3b0rM4XkWr02ThDUC5J/Yke9v+FE0pZ1JG1ZR9KWddSrzATJtdsdoItzVLJt9CVb+AlD/Cz7DE8RJPu4QUBfso8bBPQl+7hBQF+9KzMt2Y310jJTyN5YH2/MFbKx9TE2V8heI7r/Il6beyD7Hx5gz/32Lf5j7oFFY7TwBIX7JeJbvPQrLfV28BX2sO3P9QZjvMaF32hZTYmHKPFQsoUN2RY2rOY9LmXvcSm5QoUrVP7q/g/JwvUD0Mkz2QAAAABJRU5ErkJggg==",
    "rom-library": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAYAAABXAvmHAAADBUlEQVR4Ac3BS2ibBQAA4C/5f0eZRXMoUnCHgrWUroyCO4juMGoOwwUG0o0hMjqYW4UeZK1QUQRBDwMPE3aopxEQdNpNJi0oTnxVqOJg9iDpQa2jxx3KViViyDyskKRNm8ef1/fFNEcC/RhEN3o9sIYsMshgQ0QxjZPEUSQxrDoZzONL3FCHmGgSmMQ57BPNKj7ALNZVKaY+XZjGFBIaax0XcBFZFcTU7hAuo19zZXAaS3YRqM0M0ujRfD0Yx318bweB6oRI4zziWieOUfRhAXlbBCoL8RFOap8RDOEz5BUJVJbGSe03hAFcVSSwuxmc1zmG8TuWbQrs7BDSiKvddVzB3xjQWA/jQ5tC5XXhMkK1O445BWP4VOP0KRJX3jT61e465pSaw4LGuaVI3HYJTKnPLeX9onEuKRLabhIJ9RlR3kHR5fAaFhUJbXdO/Y5hDHMKxnBUNCsYx5ItYkol8ZXo5nETTyGlfjm8i3eQU0aoVEpjpJASzU8YR8YuQqWe034beB2XVCFUkMCw9lrABNZUKVTQr33u4BXMqVGoYFB7pPEq1tUhVNCttVZxGt+KIFTQqzVyeB9vIiuiuPa4r0ECBYNIab44nsFL+BWrIggUDOIFrZPAOPrwHbLqECjYg7NabwRn8Cd+U6NAwT+Y0R57cQIH8QPuqlKgIIvjeEz7DOAs7uFnVQiUGsDT2msPnscR/Ig7dhEolcMpnWEfJhBgEXllBEr9gZfxiM4Qx2GcwE2s2SKw3V6M6iw9GMc9LCkS2G4ZE+jSWeI4gq9x26bAdlk8hFGd6VF8YlNcee9hRWcaViSuvCzOIKfzrCoS2Nlt/IekzvI2lm0K7G4RT+KAznAFbykSqOxz7MeQ9rqKF5FXJFBZHtfwBA5ojzROIWeLQHXyuIZ/cRhxrZHDG5hGXhmB2iziGzyLHs21gmP42C5i6tOFaUwhobHWcQEXkVVBTDQJTGICj4vmL8xiFuuqFNM4SaSQxH7VWcE8vsANdYhpjgT6MYhu9HpgDVlkkMGGiP4HEgCcz8sKOv4AAAAASUVORK5CYII=",
  };
  const tile = {
    memo: null,
    original: null,
    wrapper: null,
    rowClass: "",
    badgeClass: "",
    attempts: 0,
    timer: 0,
    indexSource: null,
    index: new Map(),
  };

  /** appId -> { storeId, installed, cloud, downloading } for stores that show a tab. */
  function resolveTileIndex(summary) {
    const index = new Map();
    if (!summary || summary.pluginEnabled !== true) return index;
    const shown = new Set(resolveStoreTabs(summary).map((tab) => tab.storeId));
    for (const store of Array.isArray(summary.stores) ? summary.stores : []) {
      const storeId = String(store?.id || "").toLowerCase();
      if (!shown.has(storeId)) continue;
      const installed = new Set(normalizeAppIds(store.installedAppIds));
      const cloud = new Set(normalizeAppIds(store.cloudAppIds));
      const downloading = new Set(normalizeAppIds(store.activeDownloadAppIds));
      for (const appId of normalizeAppIds(store.appIds)) {
        index.set(appId, {
          storeId,
          installed: installed.has(appId),
          cloud: cloud.has(appId),
          downloading: downloading.has(appId),
        });
      }
    }
    return index;
  }

  function tileIndex() {
    if (tile.indexSource !== state.summary) {
      tile.indexSource = state.summary;
      tile.index = resolveTileIndex(state.summary);
    }
    return tile.index;
  }

  function tileStatus(info) {
    if (!info) return "";
    if (info.downloading) return "downloading";
    if (info.installed) return "";
    return info.cloud ? "cloud" : "download";
  }

  const tileStatusPaths = {
    download: "M12 4v11m0 0 4-4m-4 4-4-4M5 20h14",
    downloading: "M12 6v9m0 0 3-3m-3 3-3-3M5 20h14",
    cloud: "M6.5 18h10.8a4.2 4.2 0 0 0 .7-8.34A6.3 6.3 0 0 0 6.2 8.8 4.6 4.6 0 0 0 6.5 18Z",
  };

  /** The badge; follows store changes by itself, null for other shortcuts. */
  function TfsOmniStoreBadge({ appId }) {
    const React = core()?.React;
    const [, rerender] = React.useReducer((value) => value + 1, 0);
    React.useEffect(() => {
      state.badgeListeners.add(rerender);
      return () => { state.badgeListeners.delete(rerender); };
    }, []);
    if (state.destroyed) return null;
    try {
      const info = tileIndex().get(appId);
      const mark = info ? storeBadgeMarks[info.storeId] : null;
      if (!mark) return null;
      const status = tileStatus(info);
      const h = React.createElement;
      return h(
        "div",
        { className: tile.badgeClass, "data-tfs-omni-store": info.storeId, "data-tfs-omni-status": status || "installed" },
        h("span", {
          style: {
            display: "inline-block",
            width: 16,
            height: 16,
            backgroundColor: "#fff",
            WebkitMaskImage: `url("${mark}")`,
            maskImage: `url("${mark}")`,
            WebkitMaskSize: "contain",
            maskSize: "contain",
            WebkitMaskRepeat: "no-repeat",
            maskRepeat: "no-repeat",
            WebkitMaskPosition: "center",
            maskPosition: "center",
          },
        }),
        status
          ? h("svg", { viewBox: "0 0 24 24", width: 14, height: 14, fill: "none", style: { marginInlineStart: 4, opacity: status === "downloading" ? 1 : 0.85 }, "aria-hidden": "true" },
            h("path", { d: tileStatusPaths[status], stroke: status === "downloading" ? "#67c1f5" : "#fff", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round" }))
          : null,
      );
    } catch {
      return null;
    }
  }

  function findTileRow(node, rowClass, depth = 0) {
    if (!node || typeof node !== "object" || depth > 30) return null;
    const className = node.props?.className;
    if (typeof className === "string" && className.split(/\s+/).includes(rowClass)) return node;
    const children = node.props?.children;
    const list = Array.isArray(children) ? children : [children];
    for (const child of list) {
      const found = findTileRow(child, rowClass, depth + 1);
      if (found) return found;
    }
    return null;
  }

  function addTileBadge(React, rendered, appId) {
    const row = findTileRow(rendered, tile.rowClass);
    if (!row?.props) return;
    const children = row.props.children;
    const list = Array.isArray(children) ? children : children == null ? [] : [children];
    if (list.some((child) => child?.key === tileBadgeKey)) return;
    const badge = React.createElement(TfsOmniStoreBadge, { key: tileBadgeKey, appId });
    row.props.children = [...list, badge];
  }

  function resolveTileOriginal(self) {
    for (const candidate of [tile.memo?.[tileOriginalKey], self?.[tileOriginalKey], tile.original]) {
      if (typeof candidate === "function" && !candidate[tileWrapperBrand]) return candidate;
    }
    return null;
  }

  function createTileWrapper() {
    const wrapper = function tfsOmniLibraryTile(...args) {
      const original = resolveTileOriginal(this);
      const React = core()?.React;
      if (!original) {
        bailOnce("library tile original missing");
        return React ? React.createElement(React.Fragment) : null;
      }
      const rendered = original.apply(this, args);
      if (state.destroyed || !React || state.summary?.pluginEnabled !== true) return rendered;
      try {
        const app = args[0]?.app;
        if (!app || typeof app.BIsModOrShortcut !== "function" || app.BIsModOrShortcut() !== true) return rendered;
        addTileBadge(React, rendered, Number(app.appid) >>> 0);
      } catch (error) {
        bailOnce("tile badge failed", error);
      }
      return rendered;
    };
    Object.defineProperty(wrapper, tileWrapperBrand, { value: true });
    return wrapper;
  }

  function findTileMemo(require) {
    const memoSymbol = Symbol.for("react.memo");
    for (const id of Object.keys(require.m || {})) {
      let source = "";
      try { source = String(require.m[id]); } catch { continue; }
      if (!source.includes("LibraryItemIcons") || !source.includes("BIsModOrShortcut") || !source.includes("BIsMusicAlbum")) continue;
      let exports;
      try { exports = require(id); } catch { continue; }
      for (const key of Object.keys(exports || {})) {
        let value;
        try { value = exports[key]; } catch { continue; }
        if (value?.$$typeof === memoSymbol && typeof value.type === "function") return value;
      }
    }
    return null;
  }

  function resolveTileClasses(tfs) {
    let row = "";
    let icon = "";
    let compat = "";
    let info = "";
    for (const { exports } of tfs.webpack.getModules?.({ refresh: tile.attempts > 0 }) || []) {
      for (const candidate of [exports, exports?.default]) {
        if (!candidate || typeof candidate !== "object") continue;
        try {
          if (!row && typeof candidate.LibraryItemIcons === "string" && typeof candidate.SteamDeckCompatIcon === "string") {
            row = candidate.LibraryItemIcons;
            icon = candidate.SteamDeckCompatIcon;
          }
          if (!compat && typeof candidate.DeckCompat === "string") compat = candidate.DeckCompat;
          if (!info && typeof candidate.SteamDeckCompatInfo === "string") info = candidate.SteamDeckCompatInfo;
        } catch {}
      }
      if (row && compat && info) break;
    }
    if (!row || !icon) return false;
    tile.rowClass = row;
    // Steam's own classes for its Deck badge: placement (bottom right) and
    // show-on-highlight come from Steam's stylesheet.
    tile.badgeClass = [info, compat, icon].filter(Boolean).join(" ");
    return true;
  }

  function installTileBadges() {
    window.clearTimeout(tile.timer);
    if (state.destroyed || tile.memo) return;
    const tfs = core();
    const require = tfs?.webpack?.getWebpackRequire?.();
    const memo = require ? findTileMemo(require) : null;
    if (!memo || !resolveTileClasses(tfs)) {
      tile.attempts += 1;
      if (tile.attempts < tileInstallAttempts) {
        tile.timer = window.setTimeout(installTileBadges, tileInstallRetryMs);
      } else {
        bailOnce("library tile not found; no tile badges");
      }
      return;
    }
    const current = memo.type;
    const original = typeof memo[tileOriginalKey] === "function"
      ? memo[tileOriginalKey]
      : current?.[tileWrapperBrand] ? null : current;
    if (typeof original !== "function") {
      bailOnce("library tile carries a foreign wrapper; no tile badges");
      return;
    }
    try {
      Object.defineProperty(memo, tileOriginalKey, { value: original, configurable: true, writable: true });
      tile.wrapper = createTileWrapper();
      memo.type = tile.wrapper;
      tile.memo = memo;
      tile.original = original;
    } catch (error) {
      bailOnce("library tile could not be patched", error);
    }
  }

  function uninstallTileBadges() {
    window.clearTimeout(tile.timer);
    const memo = tile.memo;
    tile.memo = null;
    if (!memo) return;
    try {
      if (memo.type === tile.wrapper && typeof tile.original === "function") memo.type = tile.original;
      if (memo.type !== tile.wrapper) delete memo[tileOriginalKey];
    } catch {}
  }

  window.__tfsOmniTabs = {
    version,
    refresh,
    destroy,
    alive: () => !state.destroyed,
    get patched() {
      return Boolean(state.removePatch);
    },
    get tileBadges() {
      return Boolean(tile.memo);
    },
    get tabCount() {
      return Math.max(0, state.lastSpliceCount);
    },
    internals: {
      resolveStoreTabs,
      resolveManagedSets,
      mergeTabs,
      detectTabsShape,
      spliceTabs,
      adjustedCollection,
      resolveTileIndex,
      tileStatus,
      installTileBadges,
      uninstallTileBadges,
      tile,
      renderWithSplice,
      patchLibraryRoute,
      state,
      setSummary(summary) {
        state.summary = summary;
        notify();
      },
    },
  };

  void register();
  return "injected";
})();
