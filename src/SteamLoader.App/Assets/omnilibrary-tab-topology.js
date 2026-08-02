// Pure OmniLibrary tab topology helpers. Kept independent from Steam's DOM so
// every enabled-store combination can be behavior-tested without a live client.
((root) => {
  function normalizeId(value) {
    return String(value || "").trim();
  }

  function definitionKey(definition) {
    return `${normalizeId(definition?.sourceStoreId)}:${normalizeId(definition?.tabId)}`;
  }

  function buildDefinitionsFromSummary(stores, fallbackDefinitions = []) {
    const result = [];
    const seen = new Set();
    const seenTabIds = new Set();
    for (const store of Array.isArray(stores) ? stores : []) {
      const sourceStoreId = normalizeId(store?.id).toLowerCase();
      for (const tab of Array.isArray(store?.libraryTabs) ? store.libraryTabs : []) {
        const tabId = normalizeId(tab?.id);
        if (!sourceStoreId || !tabId) {
          continue;
        }
        const definition = {
          id: tabId,
          sourceStoreId,
          tabId,
          title: normalizeId(tab?.title) || normalizeId(store?.title) || sourceStoreId,
          mode: tabId,
          appFilter: normalizeId(tab?.filter).toLowerCase() || "all",
          requiresXboxCloud: tab?.requiresCloudSource === true,
        };
        const key = definitionKey(definition);
        if (!seen.has(key) && !seenTabIds.has(tabId)) {
          seen.add(key);
          seenTabIds.add(tabId);
          result.push(definition);
        }
      }
    }

    // Fall back only when an older/disabled backend does not provide descriptors.
    // This also gives cleanup code the ids of tabs from an earlier live script.
    if (result.length === 0) {
      for (const definition of fallbackDefinitions) {
        const key = definitionKey(definition);
        const tabId = normalizeId(definition?.tabId);
        if (tabId && !seen.has(key) && !seenTabIds.has(tabId)) {
          seen.add(key);
          seenTabIds.add(tabId);
          result.push({ ...definition });
        }
      }
    }
    return result;
  }

  function buildCanonicalTabOrder(sourceTabs, enabledDefinitions, allDefinitions) {
    const managedIds = new Set(
      (allDefinitions || []).map((definition) => normalizeId(definition?.tabId)),
    );
    const enabled = [];
    const seenEnabledIds = new Set();
    for (const definition of enabledDefinitions || []) {
      const id = normalizeId(definition?.tabId);
      if (!id || seenEnabledIds.has(id)) {
        continue;
      }
      seenEnabledIds.add(id);
      enabled.push(definition);
    }
    const nativeTabs = [];
    const seenNativeIds = new Set();
    for (const tab of Array.isArray(sourceTabs) ? sourceTabs : []) {
      const id = normalizeId(tab?.id);
      if (!id || managedIds.has(id) || seenNativeIds.has(id)) {
        continue;
      }
      seenNativeIds.add(id);
      nativeTabs.push(tab);
    }

    const injected = enabled.map((definition) => ({
      id: definition.tabId,
      __steamLoaderCanonicalDefinition: definition,
    }));
    const desktopIndex = nativeTabs.findIndex((tab) => normalizeId(tab?.id) === "DesktopApps");
    const soundtracksIndex = nativeTabs.findIndex((tab) => normalizeId(tab?.id) === "Soundtracks");
    const insertIndex = desktopIndex >= 0
      ? desktopIndex + 1
      : soundtracksIndex >= 0
        ? soundtracksIndex
        : nativeTabs.length;
    return [
      ...nativeTabs.slice(0, insertIndex),
      ...injected,
      ...nativeTabs.slice(insertIndex),
    ];
  }

  function restoreMissingTabs(sourceTabs, rememberedTabs) {
    const result = Array.isArray(sourceTabs) ? [...sourceTabs] : [];
    const remembered = (Array.isArray(rememberedTabs) ? rememberedTabs : [])
      .filter((tab) => normalizeId(tab?.id));
    const resultIds = new Set(result.map((tab) => normalizeId(tab?.id)).filter(Boolean));

    for (let index = 0; index < remembered.length; index += 1) {
      const tab = remembered[index];
      const id = normalizeId(tab?.id);
      if (!id || resultIds.has(id)) {
        continue;
      }

      let insertIndex = -1;
      for (let nextIndex = index + 1; nextIndex < remembered.length; nextIndex += 1) {
        const nextId = normalizeId(remembered[nextIndex]?.id);
        const currentIndex = result.findIndex(
          (candidate) => normalizeId(candidate?.id) === nextId,
        );
        if (currentIndex >= 0) {
          insertIndex = currentIndex;
          break;
        }
      }
      if (insertIndex < 0) {
        for (let previousIndex = index - 1; previousIndex >= 0; previousIndex -= 1) {
          const previousId = normalizeId(remembered[previousIndex]?.id);
          const currentIndex = result.findIndex(
            (candidate) => normalizeId(candidate?.id) === previousId,
          );
          if (currentIndex >= 0) {
            insertIndex = currentIndex + 1;
            break;
          }
        }
      }
      result.splice(insertIndex >= 0 ? insertIndex : result.length, 0, tab);
      resultIds.add(id);
    }
    return result;
  }

  function getAdjacentTabId(tabs, activeTabId, direction, wrapAround = true) {
    if (!Array.isArray(tabs) || tabs.length === 0 || !direction) {
      return "";
    }
    const ids = tabs
      .map((tab) => normalizeId(tab?.id))
      .filter(Boolean);
    if (ids.length === 0) {
      return "";
    }
    const currentIndex = ids.indexOf(normalizeId(activeTabId));
    if (currentIndex < 0) {
      return ids[0];
    }
    let nextIndex = currentIndex + Math.sign(direction);
    if (wrapAround) {
      nextIndex = (nextIndex + ids.length) % ids.length;
    } else if (nextIndex < 0 || nextIndex >= ids.length) {
      return "";
    }
    return ids[nextIndex];
  }

  function chooseDistinctBackingRoute(routeIds, preferredIndex, routeToAvoid) {
    const routes = [];
    const seen = new Set();
    for (const value of Array.isArray(routeIds) ? routeIds : []) {
      const id = normalizeId(value);
      if (id && !seen.has(id)) {
        seen.add(id);
        routes.push(id);
      }
    }
    if (!routes.length) {
      return "";
    }

    const startIndex = ((Number(preferredIndex) || 0) % routes.length + routes.length) % routes.length;
    const avoided = normalizeId(routeToAvoid);
    for (let offset = 0; offset < routes.length; offset += 1) {
      const candidate = routes[(startIndex + offset) % routes.length];
      if (routes.length === 1 || candidate !== avoided) {
        return candidate;
      }
    }
    return routes[startIndex];
  }

  function resolveActiveTabId(tabs, candidates) {
    const available = new Set(
      (tabs || []).map((tab) => normalizeId(tab?.id)).filter(Boolean),
    );
    for (const candidate of candidates || []) {
      const id = normalizeId(candidate);
      if (available.has(id)) {
        return id;
      }
    }
    return normalizeId(tabs?.[0]?.id);
  }

  function shouldPreserveVirtualSelection(
    activeVirtualTabId,
    incomingTabId,
    expectedNativeRouteTabId,
    explicitNavigation,
  ) {
    const activeVirtual = normalizeId(activeVirtualTabId);
    const incoming = normalizeId(incomingTabId);
    const expectedNativeRoute = normalizeId(expectedNativeRouteTabId);
    return Boolean(
      activeVirtual &&
      incoming &&
      incoming !== activeVirtual &&
      !explicitNavigation &&
      expectedNativeRoute &&
      incoming === expectedNativeRoute
    );
  }

  root.__steamLoaderOmniLibraryTabTopology = Object.freeze({
    buildDefinitionsFromSummary,
    buildCanonicalTabOrder,
    restoreMissingTabs,
    getAdjacentTabId,
    chooseDistinctBackingRoute,
    resolveActiveTabId,
    shouldPreserveVirtualSelection,
  });
})(typeof globalThis === "object" ? globalThis : window);
