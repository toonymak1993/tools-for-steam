// Tools for Steam - Store collection library tabs
// Bridges Steam collections created by Store Sync into the Big Picture Library
// tab strip. Steam does not expose a public tab API, so this patch is deliberately
// narrow: it only touches the real Library tabs component and only when matching
// Steam collections already exist.
(() => {
  const stateVersion = 8;
  const logPrefix = "[TFS LibraryTabs]";
  const storageKey = "ToolsForSteamLibraryTabs";
  const channelName = "ToolsForSteamLibraryTabsChannel";
  const publishIntervalMs = 2500;
  const patchIntervalMs = 1200;

  const targetCollections = [
    { id: "epic-games", title: "Epic", names: ["Epic Games", "Epic"] },
    { id: "gog-galaxy", title: "GOG", names: ["GOG Galaxy", "GOG"] },
    { id: "xbox-game-pass", title: "Xbox", names: ["Xbox / Game Pass", "Xbox", "Game Pass"] },
    { id: "ubisoft-connect", title: "Ubisoft", names: ["Ubisoft Connect", "Ubisoft"] },
    { id: "ea-app", title: "EA", names: ["EA App", "EA"] },
    { id: "battle-net", title: "Battle.net", names: ["Battle.net", "Battle Net"] },
    { id: "amazon-games", title: "Amazon", names: ["Amazon Games", "Amazon"] },
    { id: "itch-io", title: "itch.io", names: ["itch.io", "itch"] },
    { id: "custom-locations", title: "Custom", names: ["Custom Locations"] },
  ];

  const previousState = window.__steamLoaderLibraryTabsState;
  if (previousState?.version !== stateVersion) {
    if (previousState?.publishTimer) {
      window.clearInterval(previousState.publishTimer);
    }

    if (previousState?.patchTimer) {
      window.clearInterval(previousState.patchTimer);
    }

    if (previousState?.patchSoonTimer) {
      window.clearTimeout(previousState.patchSoonTimer);
    }

    if (typeof previousState?.storageHandler === "function") {
      window.removeEventListener("storage", previousState.storageHandler);
    }

    try {
      previousState?.channel?.close?.();
    } catch (_) {}
  }

  const state =
    previousState?.version === stateVersion
      ? previousState
      : (window.__steamLoaderLibraryTabsState = {
          version: stateVersion,
          publishTimer: 0,
          patchTimer: 0,
          patchSoonTimer: 0,
          storageHandler: null,
          channel: null,
          collectionCache: new Map(),
          lastPublishedAt: 0,
          lastPublishedCount: 0,
          lastPublishedSignature: null,
          lastPatchedAt: 0,
          lastPatchedCount: 0,
          lastTabIds: [],
          lastStatus: "initializing",
          lastError: "",
          wrappedCount: 0,
          mutationCount: 0,
        });

  if (!(state.collectionCache instanceof Map)) {
    state.collectionCache = new Map();
  }

  window.__steamLoaderLibraryTabsInstalled = true;

  function log(...parts) {
    try {
      console.log(logPrefix, ...parts);
    } catch (_) {}
  }

  function setStatus(status, error = "") {
    state.lastStatus = status;
    state.lastError = error;
  }

  function normalizeName(value) {
    return String(value || "")
      .replace(/\s+/g, " ")
      .trim()
      .toLowerCase();
  }

  function sanitizeId(value) {
    return String(value || "")
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "");
  }

  function getReactPropertyKey(element, prefix) {
    return element
      ? Object.getOwnPropertyNames(element).find((name) => name.startsWith(prefix))
      : null;
  }

  function getReactFiber(element) {
    const key =
      getReactPropertyKey(element, "__reactFiber") ||
      getReactPropertyKey(element, "__reactContainer");
    return key ? element[key] : null;
  }

  function getRootFiber() {
    const preferredRoots = [
      document.getElementById("GamepadUI_Full_Root"),
      document.getElementById("root"),
      document.getElementById("Main"),
      document.body,
    ];

    for (const root of preferredRoots) {
      const fiber = getReactFiber(root);
      if (fiber) {
        return fiber;
      }
    }

    for (const element of document.querySelectorAll("div, main, section")) {
      const fiber = getReactFiber(element);
      if (fiber) {
        return fiber;
      }
    }

    return null;
  }

  function walkFiber(node, visitor, visited = new Set()) {
    if (!node || visited.has(node)) {
      return;
    }

    visited.add(node);
    visitor(node);
    walkFiber(node.child, visitor, visited);
    walkFiber(node.sibling, visitor, visited);
  }

  function getTabCollections(node) {
    const collections = [];
    const candidates = [
      node?.memoizedProps?.tabs,
      node?.pendingProps?.tabs,
      node?.alternate?.memoizedProps?.tabs,
      node?.alternate?.pendingProps?.tabs,
    ];

    for (const tabs of candidates) {
      if (Array.isArray(tabs) && !collections.includes(tabs)) {
        collections.push(tabs);
      }
    }

    return collections;
  }

  function isLibraryTabsArray(tabs) {
    if (!Array.isArray(tabs) || tabs.length < 2 || tabs.length > 40) {
      return false;
    }

    const ids = new Set(
      tabs
        .map((tab) => String(tab?.id || tab?.key || ""))
        .filter(Boolean),
    );

    if (!ids.has("AllGames") || !ids.has("Installed")) {
      return false;
    }

    return ids.has("DesktopApps") || ids.has("Soundtracks");
  }

  function isInjectedTab(tab) {
    return typeof tab?.id === "string" && tab.id.startsWith("tfs-collection-");
  }

  function cloneReactElement(element, propOverrides = {}) {
    if (!element || typeof element !== "object") {
      return element;
    }

    return {
      ...element,
      props: {
        ...(element.props || {}),
        ...propOverrides,
      },
    };
  }

  function collectAppIdsFromCollection(collection) {
    const rawApps = Array.isArray(collection?.m_rgApps)
      ? collection.m_rgApps
      : Array.isArray(collection?.apps)
        ? collection.apps
        : Array.isArray(collection?.allApps)
          ? collection.allApps
          : [];

    const appIds = [];
    const seen = new Set();

    for (const app of rawApps) {
      const value = typeof app === "number" ? app : app?.appid;
      const appId = Number(value);
      if (!Number.isFinite(appId) || appId <= 0 || seen.has(appId)) {
        continue;
      }

      seen.add(appId);
      appIds.push(appId);
    }

    return appIds;
  }

  function getCollectionDisplayName(collection) {
    return String(collection?.displayName || collection?.m_strName || collection?.name || "").trim();
  }

  function findMatchingCollection(definition, collections) {
    const aliases = new Set(definition.names.map(normalizeName));
    return collections.find((collection) => aliases.has(normalizeName(getCollectionDisplayName(collection)))) || null;
  }

  function readUserCollections() {
    const store = window.collectionStore;
    if (!store) {
      return [];
    }

    const collections = store.userCollections;
    return Array.isArray(collections) ? collections : [];
  }

  function buildPublishSignature(tabs) {
    return tabs
      .map((tab) => `${tab.id}:${tab.appIds.join(",")}`)
      .join("|");
  }

  function readStoredPublishState() {
    try {
      const raw = localStorage.getItem(storageKey);
      if (!raw) {
        return { version: 0, signature: null };
      }

      const parsed = JSON.parse(raw);
      return {
        version: Number(parsed?.version) || 0,
        signature: buildPublishSignature(Array.isArray(parsed?.tabs) ? parsed.tabs : []),
      };
    } catch (_) {
      return { version: 0, signature: null };
    }
  }

  function nudgeLibraryRouteAfterPublish() {
    try {
      const path = String(window.tempNavStore?.m_locationPathname || location.pathname || "")
        .replace(/^\/routes/i, "");
      const match = path.match(/^\/library\/tab\/([^/]+)/i);
      if (!match) {
        return;
      }

      const nav = window.tempNavStore?.GetNavigator?.() || window.tempNavStore?.m_navigator;
      const tabId = decodeURIComponent(match[1] || "AllGames");
      if (typeof nav?.LibraryTab === "function") {
        window.setTimeout(() => {
          try {
            nav.LibraryTab(tabId);
          } catch (_) {}
        }, 30);
      }
    } catch (_) {}
  }

  function publishCollectionTabs() {
    try {
      const collections = readUserCollections();
      if (!collections.length) {
        return false;
      }

      const tabs = [];
      for (const definition of targetCollections) {
        const collection = findMatchingCollection(definition, collections);
        const appIds = collectAppIdsFromCollection(collection);
        if (!collection || !appIds.length) {
          continue;
        }

        tabs.push({
          id: definition.id,
          title: definition.title,
          collectionName: getCollectionDisplayName(collection),
          appIds,
        });
      }

      const payload = {
        version: stateVersion,
        updatedAt: Date.now(),
        tabs,
      };
      const signature = buildPublishSignature(tabs);
      const storedState = readStoredPublishState();

      if (
        signature === state.lastPublishedSignature &&
        storedState.version === stateVersion &&
        storedState.signature === signature
      ) {
        state.lastPublishedAt = payload.updatedAt;
        state.lastPublishedCount = tabs.length;
        state.lastTabIds = tabs.map((tab) => tab.id);
        setStatus(tabs.length ? `published ${tabs.length} collection tab(s)` : "no matching collections");
        return tabs.length > 0;
      }

      localStorage.setItem(storageKey, JSON.stringify(payload));
      state.lastPublishedSignature = signature;
      state.lastPublishedAt = payload.updatedAt;
      state.lastPublishedCount = tabs.length;
      state.lastTabIds = tabs.map((tab) => tab.id);

      try {
        state.channel?.postMessage?.({ type: "published", storageKey });
      } catch (_) {}

      nudgeLibraryRouteAfterPublish();
      setStatus(tabs.length ? `published ${tabs.length} collection tab(s)` : "no matching collections");
      return tabs.length > 0;
    } catch (error) {
      setStatus("publish failed", String(error?.message || error));
      return false;
    }
  }

  function readPublishedTabs() {
    try {
      const raw = localStorage.getItem(storageKey);
      if (!raw) {
        return [];
      }

      const parsed = JSON.parse(raw);
      if (!parsed || !Array.isArray(parsed.tabs)) {
        return [];
      }

      return parsed.tabs
        .map((tab) => ({
          id: sanitizeId(tab.id || tab.title || tab.collectionName),
          title: String(tab.title || tab.collectionName || tab.id || "").trim(),
          collectionName: String(tab.collectionName || tab.title || tab.id || "").trim(),
          appIds: Array.isArray(tab.appIds)
            ? tab.appIds.map(Number).filter((appId) => Number.isFinite(appId) && appId > 0)
            : [],
        }))
        .filter((tab) => tab.id && tab.title && tab.appIds.length);
    } catch (error) {
      setStatus("read failed", String(error?.message || error));
      return [];
    }
  }

  function cloneCollectionTemplate(templateCollection) {
    const collection = Object.create(Object.getPrototypeOf(templateCollection));
    const descriptors = Object.getOwnPropertyDescriptors(templateCollection);

    for (const [key, descriptor] of Object.entries(descriptors)) {
      if (!("value" in descriptor)) {
        try {
          Object.defineProperty(collection, key, descriptor);
        } catch (_) {}
        continue;
      }

      const value = descriptor.value;
      descriptor.value = value instanceof Map
        ? new Map(value)
        : value instanceof Set
          ? new Set(value)
          : Array.isArray(value)
            ? value.slice()
            : value;

      try {
        Object.defineProperty(collection, key, descriptor);
      } catch (_) {
        try {
          collection[key] = descriptor.value;
        } catch (_) {}
      }
    }

    return collection;
  }

  function setCollectionDisplayName(collection, tabDefinition) {
    const id = `tfs-${tabDefinition.id}`;
    const name = tabDefinition.collectionName || tabDefinition.title;

    for (const [key, value] of [
      ["m_strId", id],
      ["id", id],
      ["m_strName", name],
      ["displayName", name],
      ["name", name],
    ]) {
      try {
        collection[key] = value;
      } catch (_) {}
    }
  }

  function buildAppIdMap(appIds) {
    return new Map(appIds.map((appId) => [Number(appId), Number(appId)]));
  }

  function applySyntheticAppFields(collection, appIds) {
    const apps = appIds.map(Number).filter((appId) => Number.isFinite(appId) && appId > 0);
    const appSet = new Set(apps);
    const appMap = buildAppIdMap(apps);

    const arrayFields = [
      "m_rgApps",
      "m_rgAllApps",
      "m_rgVisibleApps",
      "apps",
      "allApps",
      "visibleApps",
      "filteredApps",
      "rgApps",
    ];
    const setFields = [
      "m_setApps",
      "m_setAllApps",
      "m_setVisibleApps",
      "setApps",
    ];
    const mapFields = [
      "m_mapApps",
      "m_mapAllApps",
      "m_mapVisibleApps",
      "mapApps",
    ];

    for (const field of arrayFields) {
      try {
        collection[field] = apps.slice();
      } catch (_) {}
    }

    for (const field of setFields) {
      try {
        collection[field] = new Set(appSet);
      } catch (_) {}
    }

    for (const field of mapFields) {
      try {
        collection[field] = new Map(appMap);
      } catch (_) {}
    }

    try {
      collection.m_mapFilterToAppCounts = new Map([
        ["", apps.length],
        ["all", apps.length],
      ]);
    } catch (_) {}
  }

  function getSyntheticCollection(templateCollection, tabDefinition) {
    if (!templateCollection) {
      return null;
    }

    const signature = `${tabDefinition.id}:${tabDefinition.appIds.join(",")}`;
    const cached = state.collectionCache.get(tabDefinition.id);
    if (cached?.signature === signature && cached.collection) {
      return cached.collection;
    }

    try {
      const collection = cloneCollectionTemplate(templateCollection);
      setCollectionDisplayName(collection, tabDefinition);
      applySyntheticAppFields(collection, tabDefinition.appIds);

      if (typeof collection.SetApps === "function") {
        try {
          collection.SetApps(tabDefinition.appIds);
        } catch (error) {
          setStatus("collection SetApps fallback", String(error?.message || error));
        }
      }

      setCollectionDisplayName(collection, tabDefinition);
      applySyntheticAppFields(collection, tabDefinition.appIds);
      state.collectionCache.set(tabDefinition.id, { signature, collection });
      return collection;
    } catch (error) {
      setStatus("collection clone failed", String(error?.message || error));
      return null;
    }
  }

  function getCollectionCount(collection, fallbackCount) {
    try {
      if (Array.isArray(collection?.visibleApps)) {
        return collection.visibleApps.length;
      }

      if (Array.isArray(collection?.allApps)) {
        return collection.allApps.length;
      }
    } catch (_) {}

    return fallbackCount;
  }

  function buildCountAddonRenderer(templateTab, collection, fallbackCount) {
    let sample = null;
    try {
      sample = templateTab?.renderTabAddon?.();
    } catch (_) {
      sample = null;
    }

    if (sample && typeof sample === "object" && sample.props && "count" in sample.props) {
      return () =>
        cloneReactElement(sample, {
          count: getCollectionCount(collection, fallbackCount),
        });
    }

    return () => null;
  }

  function buildCollectionTab(templateTab, tabDefinition) {
    const templateCollection = templateTab?.content?.props?.collection;
    const collection = getSyntheticCollection(templateCollection, tabDefinition);
    if (!collection) {
      return null;
    }

    return {
      ...templateTab,
      id: `tfs-collection-${tabDefinition.id}`,
      title: tabDefinition.title,
      content: cloneReactElement(templateTab.content, { collection }),
      renderTabAddon: buildCountAddonRenderer(templateTab, collection, tabDefinition.appIds.length),
      __steamLoaderLibraryTab: true,
      __steamLoaderSignature: `${tabDefinition.id}:${tabDefinition.appIds.join(",")}`,
    };
  }

  function tabArraySignature(tabs) {
    return tabs
      .map((tab) => `${tab?.id || ""}:${tab?.title || ""}:${tab?.__steamLoaderSignature || ""}`)
      .join("|");
  }

  function mergeTabs(tabs, publishedTabs = readPublishedTabs()) {
    if (!isLibraryTabsArray(tabs)) {
      return tabs;
    }

    const baseTabs = tabs.filter((tab) => !isInjectedTab(tab));
    if (!publishedTabs.length) {
      return baseTabs;
    }

    const templateTab =
      baseTabs.find((tab) => tab?.id === "DesktopApps" && tab?.content?.props?.collection) ||
      baseTabs.find((tab) => tab?.content?.props?.collection);

    if (!templateTab) {
      setStatus("waiting for collection template");
      return baseTabs;
    }

    const customTabs = publishedTabs
      .map((tabDefinition) => buildCollectionTab(templateTab, tabDefinition))
      .filter(Boolean);

    if (!customTabs.length) {
      return baseTabs;
    }

    const desktopIndex = baseTabs.findIndex((tab) => tab?.id === "DesktopApps");
    const soundtrackIndex = baseTabs.findIndex((tab) => tab?.id === "Soundtracks");
    const insertIndex = desktopIndex >= 0
      ? desktopIndex + 1
      : soundtrackIndex >= 0
        ? soundtrackIndex
        : baseTabs.length;

    return [
      ...baseTabs.slice(0, insertIndex),
      ...customTabs,
      ...baseTabs.slice(insertIndex),
    ];
  }

  function mutateElementTreeTabs(element, visited = new Set()) {
    if (!element || typeof element !== "object" || visited.has(element)) {
      return false;
    }

    visited.add(element);
    let changed = false;

    try {
      if (element.props && isLibraryTabsArray(element.props.tabs)) {
        const before = tabArraySignature(element.props.tabs);
        const merged = mergeTabs(element.props.tabs);
        const after = tabArraySignature(merged);
        if (before !== after || element.props.tabs.length !== merged.length) {
          element.props = {
            ...element.props,
            tabs: merged,
          };
          changed = true;
        }
      }
    } catch (error) {
      setStatus("element tree merge failed", String(error?.message || error));
    }

    const children = element.props?.children;
    if (Array.isArray(children)) {
      for (const child of children) {
        changed = mutateElementTreeTabs(child, visited) || changed;
      }
    } else if (children && typeof children === "object") {
      changed = mutateElementTreeTabs(children, visited) || changed;
    }

    return changed;
  }

  function reconcileTabsArray(tabs, publishedTabs) {
    const before = tabArraySignature(tabs);
    const merged = mergeTabs(tabs, publishedTabs);
    const after = tabArraySignature(merged);

    if (before === after && tabs.length === merged.length) {
      return false;
    }

    tabs.splice(0, tabs.length, ...merged);
    return true;
  }

  function forceUpdateHosts(hosts) {
    for (const host of hosts) {
      try {
        host.forceUpdate();
      } catch (_) {}
    }
  }

  function findLibraryRuntime() {
    const rootFiber = getRootFiber();
    if (!rootFiber) {
      return null;
    }

    const nodes = [];
    const forceHosts = new Set();

    walkFiber(rootFiber, (node) => {
      const collections = getTabCollections(node);
      if (!collections.some(isLibraryTabsArray)) {
        return;
      }

      nodes.push(node);

      let current = node;
      while (current) {
        if (current.stateNode && typeof current.stateNode.forceUpdate === "function") {
          forceHosts.add(current.stateNode);
        }

        current = current.return;
      }
    });

    return nodes.length ? { nodes, forceHosts: [...forceHosts] } : null;
  }

  function wrapComponent(original) {
    const unwrapped = original?.__steamLoaderLibraryTabsOriginal || original;
    if (original?.__steamLoaderLibraryTabsWrapped === stateVersion) {
      return original;
    }

    if (typeof unwrapped !== "function") {
      return original;
    }

    const wrapped = function (props, ...rest) {
      let nextProps = props;
      try {
        if (props && isLibraryTabsArray(props.tabs)) {
          nextProps = {
            ...props,
            tabs: mergeTabs(props.tabs),
          };
        }
      } catch (error) {
        setStatus("render merge failed", String(error?.message || error));
      }

      const renderResult = unwrapped.call(this, nextProps, ...rest);
      try {
        mutateElementTreeTabs(renderResult);
      } catch (error) {
        setStatus("render result merge failed", String(error?.message || error));
      }

      return renderResult;
    };

    wrapped.__steamLoaderLibraryTabsWrapped = stateVersion;
    wrapped.__steamLoaderLibraryTabsOriginal = unwrapped;

    try {
      wrapped.displayName = unwrapped.displayName || unwrapped.name || "SteamLoaderLibraryTabs";
    } catch (_) {}

    return wrapped;
  }

  function wrapNode(node) {
    let changed = false;
    const currentInnerType = node?.elementType?.type;
    const currentType = node?.type;

    if (typeof currentInnerType === "function") {
      const wrapped = wrapComponent(currentInnerType);
      if (wrapped !== currentInnerType) {
        node.elementType.type = wrapped;
        changed = true;
      }

      if (node.type === currentInnerType) {
        node.type = wrapped;
      }
    } else if (typeof currentType === "function") {
      const wrapped = wrapComponent(currentType);
      if (wrapped !== currentType) {
        node.type = wrapped;
        changed = true;
      }
    }

    if (node?.alternate) {
      if (node.alternate.elementType?.type === currentInnerType && typeof currentInnerType === "function") {
        node.alternate.elementType.type = node.elementType.type;
      }

      if (node.alternate.type === currentType && node.type !== currentType) {
        node.alternate.type = node.type;
      }
    }

    return changed;
  }

  function wrapAncestorComponents(node, maxDepth = 12) {
    let changed = false;
    let current = node;
    let depth = 0;

    while (current && depth < maxDepth) {
      changed = wrapNode(current) || changed;
      current = current.return;
      depth += 1;
    }

    return changed;
  }

  function patchLibraryTabs() {
    try {
      const runtime = findLibraryRuntime();
      if (!runtime) {
        setStatus("waiting for library tabs");
        return false;
      }

      const publishedTabs = readPublishedTabs();
      let changed = false;
      let wrapped = false;
      const tabArrays = new Set();

      for (const node of runtime.nodes) {
        wrapped = wrapAncestorComponents(node) || wrapped;

        for (const tabs of getTabCollections(node)) {
          if (isLibraryTabsArray(tabs)) {
            tabArrays.add(tabs);
          }
        }
      }

      for (const tabs of tabArrays) {
        changed = reconcileTabsArray(tabs, publishedTabs) || changed;
      }

      if (changed || wrapped) {
        forceUpdateHosts(runtime.forceHosts);
      }

      state.lastPatchedAt = Date.now();
      state.lastPatchedCount = publishedTabs.length;
      state.lastTabIds = publishedTabs.map((tab) => tab.id);
      state.wrappedCount += wrapped ? 1 : 0;
      state.mutationCount += changed ? 1 : 0;

      setStatus(
        publishedTabs.length
          ? `patched ${publishedTabs.length} collection tab(s)`
          : "library tabs patched, no collection tabs published",
      );

      return changed || wrapped;
    } catch (error) {
      setStatus("patch failed", String(error?.message || error));
      return false;
    }
  }

  function requestPatchSoon() {
    if (state.patchSoonTimer) {
      return;
    }

    state.patchSoonTimer = window.setTimeout(() => {
      state.patchSoonTimer = 0;
      patchLibraryTabs();
    }, 80);
  }

  function setupChannel() {
    if (state.channel || typeof BroadcastChannel !== "function") {
      return;
    }

    try {
      state.channel = new BroadcastChannel(channelName);
      state.channel.addEventListener("message", (event) => {
        if (event?.data?.storageKey === storageKey) {
          requestPatchSoon();
        }
      });
    } catch (_) {
      state.channel = null;
    }
  }

  function setupStorageListener() {
    if (typeof state.storageHandler === "function") {
      return;
    }

    state.storageHandler = (event) => {
      if (event?.key === storageKey) {
        requestPatchSoon();
      }
    };

    window.addEventListener("storage", state.storageHandler);
  }

  function install() {
    setupChannel();
    setupStorageListener();

    if (!state.publishTimer) {
      state.publishTimer = window.setInterval(publishCollectionTabs, publishIntervalMs);
    }

    if (!state.patchTimer) {
      state.patchTimer = window.setInterval(patchLibraryTabs, patchIntervalMs);
    }

    publishCollectionTabs();
    patchLibraryTabs();
    log("installed");
  }

  install();
})();
