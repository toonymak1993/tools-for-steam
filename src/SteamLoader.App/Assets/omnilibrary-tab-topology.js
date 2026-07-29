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
        if (!seen.has(key)) {
          seen.add(key);
          result.push(definition);
        }
      }
    }

    // Fall back only when an older/disabled backend does not provide descriptors.
    // This also gives cleanup code the ids of tabs from an earlier live script.
    if (result.length === 0) {
      for (const definition of fallbackDefinitions) {
        const key = definitionKey(definition);
        if (!seen.has(key)) {
          seen.add(key);
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
    const enabled = (enabledDefinitions || []).filter(
      (definition) => normalizeId(definition?.tabId),
    );
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
    getAdjacentTabId,
    resolveActiveTabId,
    shouldPreserveVirtualSelection,
  });
})(typeof globalThis === "object" ? globalThis : window);
