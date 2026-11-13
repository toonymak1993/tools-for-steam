// Tools for Steam - Tabhero settings, ownership and filter engine.
//
// Tabhero intentionally does not patch Steam on its own.  library-tabs.js is
// the single compositor for native, Tabhero and OmniLibrary/OmniConsole tabs,
// preventing two independent plugins from repeatedly rewriting the same row.
((root) => {
  "use strict";

  const engineVersion = 5;
  const storageKey = "ToolsForSteamTabheroSettings.v1";
  const catalogStorageKey = "ToolsForSteamTabheroCatalog.v1";
  const channelName = "ToolsForSteamTabheroSettings";
  const maximumCustomTabs = 64;
  const maximumProfiles = 32;
  const maximumUndoEntries = 20;
  const maximumFiltersPerGroup = 64;
  const maximumTotalFilters = 256;
  const maximumRegexLength = 160;
  const protectedOwnerNames = new Set([
    "omnilibrary",
    "omniconsole",
    "tools-for-steam",
  ]);
  const omniLibraryDependencyProtection = "omnilibrary-native-route";
  const knownFilterTypes = new Set([
    "collection",
    "installed",
    "regex",
    "friends",
    "tags",
    "whitelist",
    "blacklist",
    "merge",
    "platform",
    "deck compatibility",
    "steamos compatibility",
    "review score",
    "time played",
    "size on disk",
    "release date",
    "purchase date",
    "last played",
    "family sharing",
    "demo",
    "coming soon",
    "streamable",
    "steam features",
    "achievements",
    "sd card",
    "install folder",
  ]);
  const defaultNativeCatalog = [
    { id: "AllGames", title: "All Games", owner: "steam", protected: false },
    { id: "Installed", title: "Installed", owner: "steam", protected: false },
    { id: "Favorites", title: "Favorites", owner: "steam", protected: false },
    { id: "GreatOnDeck", title: "Great on Deck", owner: "steam", protected: false },
    { id: "DesktopApps", title: "Non-Steam", owner: "steam", protected: false },
    { id: "Soundtracks", title: "Soundtracks", owner: "steam", protected: false },
  ];

  const previous = root.__steamLoaderTabHero;
  if (previous?.version === engineVersion) {
    return;
  }
  try {
    previous?.dispose?.();
  } catch (_) {}

  function text(value, maximumLength = 160) {
    return String(value ?? "").trim().slice(0, maximumLength);
  }

  function normalizeFilterType(value) {
    return text(value, 80)
      .toLowerCase()
      .replace(/[\-_]+/g, " ")
      .replace(/\s+/g, " ");
  }

  const regexCache = new Map();

  function normalizeRegexFlags(value) {
    return Array.from(new Set(String(value || "i")
      .replace(/[^imsuv]/g, "")
      .split("")))
      .join("");
  }

  function getRegexError(patternValue, flagsValue) {
    const pattern = String(patternValue ?? "");
    if (pattern.length > maximumRegexLength) {
      return `regular expressions may contain at most ${maximumRegexLength} characters.`;
    }
    // Nested quantified groups are a common source of catastrophic
    // backtracking. They are not needed for title matching and can freeze the
    // Steam render thread on a long game name.
    if (/\((?:\\.|[^()]){0,120}[+*](?:\\.|[^()])*\)\s*(?:[+*]|\{)/.test(pattern)) {
      return "nested repeating groups are not allowed because they can stall Steam's UI.";
    }
    try {
      new RegExp(pattern, normalizeRegexFlags(flagsValue));
      return "";
    } catch (error) {
      return String(error?.message || error);
    }
  }

  function getCompiledRegex(patternValue, flagsValue) {
    const pattern = String(patternValue ?? "");
    const flags = normalizeRegexFlags(flagsValue);
    if (!pattern) {
      return null;
    }
    const key = `${flags}:${pattern}`;
    if (regexCache.has(key)) {
      return regexCache.get(key);
    }
    if (getRegexError(pattern, flags)) {
      return null;
    }
    const compiled = new RegExp(pattern, flags);
    if (regexCache.size >= maximumTotalFilters) {
      regexCache.clear();
    }
    regexCache.set(key, compiled);
    return compiled;
  }

  function cloneJson(value, fallback) {
    try {
      return JSON.parse(JSON.stringify(value));
    } catch (_) {
      return fallback;
    }
  }

  function positiveInteger(value) {
    const number = Number(value);
    return Number.isInteger(number) && number > 0 ? number : 0;
  }

  function uniqueStrings(values, maximum = 512) {
    const result = [];
    const seen = new Set();
    for (const value of Array.isArray(values) ? values : []) {
      const normalized = text(value, 220);
      if (!normalized || seen.has(normalized)) {
        continue;
      }
      seen.add(normalized);
      result.push(normalized);
      if (result.length >= maximum) {
        break;
      }
    }
    return result;
  }

  function isOmniLibraryNativeDependency(tabOrId) {
    const tab = tabOrId && typeof tabOrId === "object" ? tabOrId : null;
    const id = text(tab?.id ?? tabOrId, 220);
    const libraryState = root.__steamLoaderLibraryTabsState;
    return Boolean(
      id === "DesktopApps" &&
      libraryState?.activationResolved === true &&
      libraryState?.pluginEnabled === true
    );
  }

  function isPersistentlyProtectedTab(tabOrId) {
    const tab = tabOrId && typeof tabOrId === "object" ? tabOrId : null;
    const id = text(tab?.id ?? tabOrId, 220);
    if (tab?.protectionReason === omniLibraryDependencyProtection) {
      return false;
    }
    const owner = text(
      tab?.owner ??
      tab?.__steamLoaderTabOwner ??
      tab?.__steamLoaderCanonicalDefinition?.owner,
      80,
    ).toLowerCase();
    return Boolean(
      tab?.protected === true ||
      tab?.__steamLoaderProtectedTab === true ||
      protectedOwnerNames.has(owner) ||
      (Boolean(owner) && owner !== "steam" && owner !== "tabhero") ||
      id.toLowerCase().startsWith("tfs-")
    );
  }

  function isProtectedTab(tabOrId) {
    return isOmniLibraryNativeDependency(tabOrId) ||
      isPersistentlyProtectedTab(tabOrId);
  }

  function normalizeFilter(value, depth = 0) {
    if (!value || typeof value !== "object" || depth > 5) {
      return null;
    }
    const type = normalizeFilterType(value.type);
    if (!knownFilterTypes.has(type)) {
      return null;
    }
    const params = value.params && typeof value.params === "object"
      ? cloneJson(value.params, {})
      : {};
    if (type === "merge") {
      params.mode = String(params.mode || "and").toLowerCase() === "or" ? "or" : "and";
      params.filters = (Array.isArray(params.filters) ? params.filters : [])
        .slice(0, maximumFiltersPerGroup)
        .map((filter) => normalizeFilter(filter, depth + 1))
        .filter(Boolean)
        .slice(0, maximumFiltersPerGroup);
    }
    return {
      type,
      inverted: value.inverted === true,
      params,
    };
  }

  function makeCustomId(value) {
    const raw = text(value, 120)
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "");
    if (raw.startsWith("tabhero-") && raw.length > "tabhero-".length) {
      return raw;
    }
    return `tabhero-${raw || `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`}`;
  }

  function allocateCustomId(value, currentSnapshot) {
    const baseId = makeCustomId(value);
    const existingIds = new Set((currentSnapshot?.customTabs || []).map((tab) => tab.id));
    if (!existingIds.has(baseId)) {
      return baseId;
    }
    for (let suffix = 2; suffix <= maximumCustomTabs + 1; suffix += 1) {
      const candidate = `${baseId}-${suffix}`;
      if (!existingIds.has(candidate)) {
        return candidate;
      }
    }
    return makeCustomId("");
  }

  function normalizeCustomTab(value, index = 0) {
    const id = makeCustomId(value?.id || `custom-${index + 1}`);
    const filters = (Array.isArray(value?.filters) ? value.filters : [])
      .slice(0, maximumFiltersPerGroup)
      .map((filter) => normalizeFilter(filter))
      .filter(Boolean)
      .slice(0, maximumFiltersPerGroup);
    return {
      id,
      title: text(value?.title, 80) || `Custom ${index + 1}`,
      enabled: value?.enabled !== false,
      matchMode: String(value?.matchMode || value?.filtersMode || "all").toLowerCase() === "any"
        || String(value?.filtersMode || "").toLowerCase() === "or"
        ? "any"
        : "all",
      autoHide: value?.autoHide === true,
      filters,
      categories: uniqueStrings(value?.categories, 8).map((entry) => entry.toLowerCase()),
      sortBy: text(value?.sortBy, 40) || "default",
      owner: "tabhero",
      protected: false,
      custom: true,
    };
  }

  function normalizeCatalogEntry(value) {
    const id = text(value?.id, 220);
    if (!id) {
      return null;
    }
    const omniLibraryDependency = isOmniLibraryNativeDependency(value);
    const protectedTab = omniLibraryDependency || isPersistentlyProtectedTab(value);
    return {
      id,
      title: text(value?.__steamLoaderTabHeroOriginalTitle ?? value?.title, 120) || id,
      owner: omniLibraryDependency
        ? "omnilibrary"
        : protectedTab
        ? text(value?.owner ?? value?.__steamLoaderTabOwner, 80).toLowerCase() || "omnilibrary"
        : value?.protectionReason === omniLibraryDependencyProtection
          ? "steam"
          : text(value?.owner, 80).toLowerCase() || (id.startsWith("tabhero-") ? "tabhero" : "steam"),
      protected: protectedTab,
      protectionReason: omniLibraryDependency
        ? omniLibraryDependencyProtection
        : protectedTab
          ? text(value?.protectionReason, 80)
          : "",
      custom: id.startsWith("tabhero-") || value?.custom === true,
    };
  }

  function normalizeProfile(value, index = 0) {
    const id = text(value?.id, 120) || `profile-${Date.now().toString(36)}-${index}`;
    const native = {};
    for (const [tabId, settings] of Object.entries(value?.native || {})) {
      const idText = text(tabId, 220);
      if (!idText || isPersistentlyProtectedTab(idText)) {
        continue;
      }
      native[idText] = {
        title: text(settings?.title, 80),
        hidden: settings?.hidden === true,
      };
    }
    return {
      id,
      title: text(value?.title, 80) || `Profile ${index + 1}`,
      order: uniqueStrings(value?.order).filter((tabId) => !isPersistentlyProtectedTab(tabId)),
      native,
      customTabIds: uniqueStrings(value?.customTabIds).filter((tabId) => tabId.startsWith("tabhero-")),
    };
  }

  function normalizeSnapshot(value) {
    const customTabs = (Array.isArray(value?.customTabs) ? value.customTabs : [])
      .slice(0, maximumCustomTabs)
      .map(normalizeCustomTab)
      .filter((tab, index, tabs) => tabs.findIndex((candidate) => candidate.id === tab.id) === index)
      .slice(0, maximumCustomTabs);
    const native = {};
    for (const [tabId, settings] of Object.entries(value?.native || {})) {
      const id = text(tabId, 220);
      if (!id || isPersistentlyProtectedTab(id)) {
        continue;
      }
      native[id] = {
        title: text(settings?.title, 80),
        hidden: settings?.hidden === true,
      };
    }
    const catalog = (Array.isArray(value?.catalog) ? value.catalog : defaultNativeCatalog)
      .slice(0, 192)
      .map(normalizeCatalogEntry)
      .filter(Boolean)
      .filter((entry, index, entries) => entries.findIndex((candidate) => candidate.id === entry.id) === index)
      .slice(0, 192);
    return {
      schemaVersion: 1,
      revision: Math.max(0, Number(value?.revision) || 0),
      enabled: value?.enabled !== false,
      order: uniqueStrings(value?.order).filter((tabId) => !isPersistentlyProtectedTab(tabId)),
      native,
      customTabs,
      profiles: (Array.isArray(value?.profiles) ? value.profiles : [])
        .slice(0, maximumProfiles)
        .map(normalizeProfile)
        .filter((profile, index, profiles) => profiles.findIndex((candidate) => candidate.id === profile.id) === index)
        .slice(0, maximumProfiles),
      activeProfileId: text(value?.activeProfileId, 120),
      catalog: catalog.length ? catalog : defaultNativeCatalog.map((entry) => ({ ...entry })),
    };
  }

  function tryReadStoredSnapshot() {
    try {
      const raw = root.localStorage?.getItem(storageKey);
      const rawCatalog = root.localStorage?.getItem(catalogStorageKey);
      if (!raw && !rawCatalog) {
        return null;
      }
      const base = normalizeSnapshot(raw ? JSON.parse(raw) : null);
      if (!rawCatalog) {
        return base;
      }
      const parsedCatalog = JSON.parse(rawCatalog);
      const catalog = Array.isArray(parsedCatalog)
        ? parsedCatalog
        : parsedCatalog?.catalog;
      return normalizeSnapshot({
        ...base,
        catalog: Array.isArray(catalog) ? catalog : base.catalog,
      });
    } catch (_) {
      return null;
    }
  }

  function readStoredSnapshot() {
    return tryReadStoredSnapshot() || normalizeSnapshot(null);
  }

  let snapshot = readStoredSnapshot();
  const listeners = new Set();
  const undoHistory = [];
  let channel = null;
  let storageHandler = null;

  function editableStateOf(value) {
    return cloneJson({
      order: value?.order || [],
      native: value?.native || {},
      customTabs: value?.customTabs || [],
      profiles: value?.profiles || [],
      activeProfileId: value?.activeProfileId || "",
    }, {
      order: [],
      native: {},
      customTabs: [],
      profiles: [],
      activeProfileId: "",
    });
  }

  function editableSignature(value) {
    return JSON.stringify(editableStateOf(value));
  }

  function contentSignature(value) {
    const normalized = normalizeSnapshot(value);
    normalized.revision = 0;
    return JSON.stringify(normalized);
  }

  function latestWritableSnapshot() {
    const stored = tryReadStoredSnapshot();
    if (!stored) {
      return snapshot;
    }
    // localStorage is the cross-webview authority. In the unusual case where
    // an earlier write failed, keep the newer in-memory state instead.
    return stored.revision < snapshot.revision ? snapshot : stored;
  }

  function emit(reason = "changed", broadcast = true) {
    const publicSnapshot = getSnapshot();
    for (const listener of listeners) {
      try {
        listener(publicSnapshot, reason);
      } catch (_) {}
    }
    try {
      root.dispatchEvent?.(new CustomEvent("steamloader:tabhero-changed", {
        detail: { snapshot: publicSnapshot, reason },
      }));
    } catch (_) {}
    if (broadcast) {
      try {
        channel?.postMessage?.({ type: "changed", revision: snapshot.revision, reason });
      } catch (_) {}
    }
  }

  function commit(reason, update, { undoable = false } = {}) {
    const base = normalizeSnapshot(latestWritableSnapshot());
    const candidate = typeof update === "function" ? update(base) : update;
    if (!candidate) {
      snapshot = base;
      return getSnapshot();
    }
    const normalized = normalizeSnapshot(candidate);
    if (contentSignature(normalized) === contentSignature(base)) {
      snapshot = base;
      return getSnapshot();
    }
    if (undoable && editableSignature(normalized) !== editableSignature(base)) {
      undoHistory.push(editableStateOf(base));
      if (undoHistory.length > maximumUndoEntries) {
        undoHistory.splice(0, undoHistory.length - maximumUndoEntries);
      }
    }
    normalized.revision = Math.max(
      snapshot.revision,
      base.revision,
      normalized.revision,
    ) + 1;
    snapshot = normalized;
    try {
      if (reason === "catalog") {
        root.localStorage?.setItem(catalogStorageKey, JSON.stringify(snapshot.catalog));
      } else {
        root.localStorage?.setItem(storageKey, JSON.stringify(snapshot));
      }
    } catch (_) {}
    emit(reason);
    return getSnapshot();
  }

  function reloadFromStorage(reason = "external") {
    const next = readStoredSnapshot();
    if (JSON.stringify(next) === JSON.stringify(snapshot)) {
      return false;
    }
    if (editableSignature(next) !== editableSignature(snapshot)) {
      undoHistory.length = 0;
    }
    snapshot = next;
    emit(reason, false);
    return true;
  }

  function getSnapshot() {
    return cloneJson(snapshot, normalizeSnapshot(null));
  }

  function subscribe(listener) {
    if (typeof listener !== "function") {
      return () => {};
    }
    listeners.add(listener);
    try {
      listener(getSnapshot(), "subscribe");
    } catch (_) {}
    return () => listeners.delete(listener);
  }

  function setEnabled(enabled) {
    return commit("enabled", (base) => ({
      ...base,
      enabled: enabled === true,
    }));
  }

  function updateNativeTab(tabId, patch = {}) {
    const id = text(tabId, 220);
    if (!id || isProtectedTab(id) || id.startsWith("tabhero-")) {
      return { ok: false, reason: "protected-or-custom", snapshot: getSnapshot() };
    }
    const nextSnapshot = commit("native-tab", (base) => {
      const nextNative = { ...base.native };
      const current = nextNative[id] || { title: "", hidden: false };
      nextNative[id] = {
        title: patch.title === undefined ? current.title : text(patch.title, 80),
        hidden: patch.hidden === undefined ? current.hidden === true : patch.hidden === true,
      };
      return { ...base, native: nextNative, activeProfileId: "" };
    }, { undoable: true });
    return { ok: true, snapshot: nextSnapshot };
  }

  function resetNativeTab(tabId) {
    const id = text(tabId, 220);
    if (!id || isProtectedTab(id)) {
      return { ok: false, reason: "protected", snapshot: getSnapshot() };
    }
    const nextSnapshot = commit("native-reset", (base) => {
      const nextNative = { ...base.native };
      delete nextNative[id];
      return { ...base, native: nextNative, activeProfileId: "" };
    }, { undoable: true });
    return { ok: true, snapshot: nextSnapshot };
  }

  function upsertCustomTab(value) {
    const validation = validateFilters(value?.filters || []);
    if (!validation.valid) {
      return {
        ok: false,
        reason: "invalid-filters",
        errors: validation.errors,
        snapshot: getSnapshot(),
      };
    }
    let normalized = null;
    let failure = "";
    const nextSnapshot = commit("custom-tab", (base) => {
      const requestedId = text(value?.id, 220);
      const normalizedRequestedId = requestedId ? makeCustomId(requestedId) : "";
      const existingIndex = normalizedRequestedId
        ? base.customTabs.findIndex((tab) => tab.id === normalizedRequestedId)
        : -1;
      if (existingIndex < 0 && base.customTabs.length >= maximumCustomTabs) {
        failure = "limit";
        return null;
      }
      const id = existingIndex >= 0
        ? normalizedRequestedId
        : normalizedRequestedId || allocateCustomId(value?.title || "custom", base);
      normalized = normalizeCustomTab({ ...value, id }, base.customTabs.length);
      const customTabs = base.customTabs.map((tab) => ({ ...tab }));
      if (existingIndex >= 0) {
        customTabs[existingIndex] = normalized;
      } else {
        customTabs.push(normalized);
      }
      const currentOrder = getEditableOrder(base);
      const order = currentOrder.includes(normalized.id)
        ? currentOrder
        : [...currentOrder, normalized.id];
      return { ...base, customTabs, order, activeProfileId: "" };
    }, { undoable: true });
    if (failure || !normalized) {
      return { ok: false, reason: failure || "invalid", snapshot: nextSnapshot };
    }
    return { ok: true, tab: cloneJson(normalized, null), snapshot: nextSnapshot };
  }

  function deleteCustomTab(tabId) {
    const id = text(tabId, 220);
    let found = false;
    const nextSnapshot = commit("custom-delete", (base) => {
      const customTabs = base.customTabs.filter((tab) => tab.id !== id);
      if (customTabs.length === base.customTabs.length) {
        return null;
      }
      found = true;
      const profiles = base.profiles.map((profile) => ({
        ...profile,
        customTabIds: profile.customTabIds.filter((candidate) => candidate !== id),
      }));
      return {
        ...base,
        customTabs,
        order: base.order.filter((candidate) => candidate !== id),
        profiles,
        activeProfileId: "",
      };
    }, { undoable: true });
    if (!found) {
      return { ok: false, reason: "not-custom", snapshot: getSnapshot() };
    }
    return { ok: true, snapshot: nextSnapshot };
  }

  function getEditableOrder(currentSnapshot = snapshot) {
    const catalogIds = currentSnapshot.catalog
      .filter((entry) => !entry.protected)
      .map((entry) => entry.id);
    const customIds = currentSnapshot.customTabs.map((tab) => tab.id);
    const available = uniqueStrings([...catalogIds, ...customIds]);
    const availableSet = new Set(available);
    return [
      ...currentSnapshot.order.filter((id) => availableSet.has(id)),
      ...available.filter((id) => !currentSnapshot.order.includes(id)),
    ];
  }

  function moveTab(tabId, direction) {
    const id = text(tabId, 220);
    if (!id || isProtectedTab(id)) {
      return { ok: false, reason: "protected", snapshot: getSnapshot() };
    }
    let moved = false;
    const nextSnapshot = commit("order", (base) => {
      const order = getEditableOrder(base);
      const index = order.indexOf(id);
      const nextIndex = index + Math.sign(Number(direction) || 0);
      if (index < 0 || nextIndex < 0 || nextIndex >= order.length || nextIndex === index) {
        return null;
      }
      [order[index], order[nextIndex]] = [order[nextIndex], order[index]];
      moved = true;
      return { ...base, order, activeProfileId: "" };
    }, { undoable: true });
    if (!moved) {
      return { ok: false, reason: "edge", snapshot: getSnapshot() };
    }
    return { ok: true, snapshot: nextSnapshot };
  }

  function moveTabToEdge(tabId, edge) {
    const id = text(tabId, 220);
    if (!id || isProtectedTab(id)) {
      return { ok: false, reason: "protected", snapshot: getSnapshot() };
    }
    let moved = false;
    const nextSnapshot = commit("order-edge", (base) => {
      const order = getEditableOrder(base);
      const index = order.indexOf(id);
      if (index < 0) {
        return null;
      }
      const targetIndex = String(edge).toLowerCase() === "end" ? order.length - 1 : 0;
      if (index === targetIndex) {
        return null;
      }
      order.splice(index, 1);
      order.splice(targetIndex, 0, id);
      moved = true;
      return { ...base, order, activeProfileId: "" };
    }, { undoable: true });
    return moved
      ? { ok: true, snapshot: nextSnapshot }
      : { ok: false, reason: "edge", snapshot: nextSnapshot };
  }

  function duplicateCustomTab(tabId) {
    const id = text(tabId, 220);
    const current = latestWritableSnapshot().customTabs.find((tab) => tab.id === id);
    if (!current) {
      return { ok: false, reason: "not-custom", snapshot: getSnapshot() };
    }
    return upsertCustomTab({
      ...current,
      id: "",
      title: `${text(current.title, 75)} Copy`,
      enabled: true,
    });
  }

  function showAllNativeTabs() {
    let changed = false;
    const nextSnapshot = commit("native-show-all", (base) => {
      const native = {};
      for (const [id, settings] of Object.entries(base.native)) {
        native[id] = {
          ...settings,
          hidden: false,
        };
        changed ||= settings?.hidden === true;
      }
      return changed ? { ...base, native, activeProfileId: "" } : null;
    }, { undoable: true });
    return { ok: true, changed, snapshot: nextSnapshot };
  }

  function canUndo() {
    return undoHistory.length > 0;
  }

  function undoLastChange() {
    const previousEditableState = undoHistory.pop();
    if (!previousEditableState) {
      return { ok: false, reason: "empty", snapshot: getSnapshot() };
    }
    const nextSnapshot = commit("undo", (base) => ({
      ...base,
      ...previousEditableState,
    }));
    return { ok: true, snapshot: nextSnapshot };
  }

  function observeTabs(tabs) {
    const incoming = (Array.isArray(tabs) ? tabs : [])
      .map(normalizeCatalogEntry)
      .filter(Boolean);
    if (!incoming.length) {
      return false;
    }
    const signature = (entries) => entries
      .map((entry) => `${entry.id}:${entry.title}:${entry.owner}:${entry.protected}`)
      .join("|");
    let changed = false;
    commit("catalog", (base) => {
      const customIds = new Set(base.customTabs.map((tab) => tab.id));
      const incomingCatalog = incoming
        .filter((entry) => !customIds.has(entry.id))
        .filter((entry, index, entries) => entries.findIndex((candidate) => candidate.id === entry.id) === index);
      const incomingIds = new Set(incomingCatalog.map((entry) => entry.id));
      const retainedNative = base.catalog.filter((entry) =>
        !entry.protected &&
        !entry.custom &&
        !incomingIds.has(entry.id));
      const catalog = [...incomingCatalog, ...retainedNative];
      if (signature(catalog) === signature(base.catalog)) {
        return null;
      }
      changed = true;
      return { ...base, catalog };
    });
    return changed;
  }

  function composeTabs(tabs, options = {}) {
    const source = Array.isArray(tabs) ? tabs.filter(Boolean) : [];
    if (!snapshot.enabled) {
      return source;
    }
    // Composition is also used by controller navigation to calculate the next
    // visible tab. That read path must stay pure: observing its sometimes
    // partial DOM/runtime input would publish a catalog change, trigger another
    // Library render, and feed the result back into navigation.
    if (options?.observe !== false) {
      observeTabs(source);
    }
    const protectedTabs = source
      .filter(isProtectedTab)
      .filter((tab, index, tabs) => tabs.findIndex((candidate) =>
        text(candidate?.id, 220) === text(tab?.id, 220)) === index);
    const protectedIds = new Set(protectedTabs.map((tab) => text(tab?.id, 220)));
    const customById = new Map(snapshot.customTabs.map((tab) => [tab.id, tab]));
    const seenEditableIds = new Set();
    const editable = source
      .filter((tab) => !protectedIds.has(text(tab?.id, 220)))
      .filter((tab) => {
        const id = text(tab?.id, 220);
        if (seenEditableIds.has(id)) {
          return false;
        }
        seenEditableIds.add(id);
        return true;
      })
      .filter((tab) => {
        const id = text(tab?.id, 220);
        const custom = customById.get(id);
        if (custom) {
          return custom.enabled !== false;
        }
        return snapshot.native[id]?.hidden !== true;
      })
      .map((tab) => {
        const id = text(tab?.id, 220);
        const custom = customById.get(id);
        if (custom) {
          return custom.title && custom.title !== tab.title
            ? { ...tab, title: custom.title }
            : tab;
        }
        const originalTitle = text(
          tab?.__steamLoaderTabHeroOriginalTitle ?? tab?.title,
          120,
        );
        const title = snapshot.native[id]?.title || originalTitle;
        return title !== tab.title || tab?.__steamLoaderTabHeroOriginalTitle
          ? {
              ...tab,
              title,
              __steamLoaderTabHeroOriginalTitle: originalTitle,
            }
          : tab;
      });
    const rank = new Map(snapshot.order.map((id, index) => [id, index]));
    const naturalRank = new Map(editable.map((tab, index) => [text(tab?.id, 220), index]));
    editable.sort((left, right) => {
      const leftId = text(left?.id, 220);
      const rightId = text(right?.id, 220);
      const leftRank = rank.has(leftId) ? rank.get(leftId) : snapshot.order.length + (naturalRank.get(leftId) || 0);
      const rightRank = rank.has(rightId) ? rank.get(rightId) : snapshot.order.length + (naturalRank.get(rightId) || 0);
      return leftRank - rightRank;
    });
    if (!editable.length && !protectedTabs.length && source.length) {
      // Keep one recovery path visible if a user hides every native tab. The
      // saved choices remain intact, so opening Tabhero can restore any tab.
      return [source.find((tab) => text(tab?.id, 220) === "AllGames") || source[0]];
    }
    if (!protectedTabs.length) {
      return editable;
    }
    let anchorIndex = editable.findIndex((tab) => text(tab?.id, 220) === "DesktopApps");
    if (anchorIndex < 0) {
      anchorIndex = editable.findIndex((tab) => text(tab?.id, 220) === "Installed");
    }
    const insertIndex = anchorIndex >= 0 ? anchorIndex + 1 : Math.min(3, editable.length);
    return [
      ...editable.slice(0, insertIndex),
      ...protectedTabs,
      ...editable.slice(insertIndex),
    ];
  }

  function appIdOf(app) {
    return positiveInteger(app?.appid ?? app?.appId ?? app?.nAppID ?? app?.id);
  }

  function arrayOf(value) {
    if (Array.isArray(value)) {
      return value;
    }
    if (value && typeof value[Symbol.iterator] === "function") {
      try {
        return Array.from(value);
      } catch (_) {}
    }
    return [];
  }

  function numberFrom(app, names, fallback = 0) {
    for (const name of names) {
      const value = Number(app?.[name]);
      if (Number.isFinite(value)) {
        return value;
      }
    }
    return fallback;
  }

  function booleanFrom(app, names) {
    for (const name of names) {
      if (typeof app?.[name] === "boolean") {
        return app[name];
      }
    }
    return false;
  }

  function compareNumber(actual, params, thresholdNames) {
    let threshold = 0;
    for (const name of thresholdNames) {
      const value = Number(params?.[name]);
      if (Number.isFinite(value)) {
        threshold = value;
        break;
      }
    }
    const condition = String(params?.condition || params?.operator || "above").toLowerCase();
    if (["below", "under", "less", "lte", "at-most"].includes(condition)) {
      return actual <= threshold;
    }
    if (["equal", "equals", "eq"].includes(condition)) {
      return actual === threshold;
    }
    return actual >= threshold;
  }

  function dateThreshold(params) {
    if (Number.isFinite(Number(params?.timestamp))) {
      const numeric = Number(params.timestamp);
      return numeric > 100000000000 ? numeric : numeric * 1000;
    }
    if (typeof params?.date === "string" && params.date) {
      const parsed = Date.parse(params.date);
      return Number.isFinite(parsed) ? parsed : 0;
    }
    if (params?.date && typeof params.date === "object") {
      const year = Number(params.date.year);
      if (Number.isInteger(year) && year > 1900) {
        const hasMonth = Number.isInteger(Number(params.date.month));
        const hasDay = Number.isInteger(Number(params.date.day));
        const month = Math.max(1, Number(params.date.month || 1));
        const day = Math.max(1, Number(params.date.day || 1));
        const condition = String(params?.condition || "above").toLowerCase();
        if (["below", "under", "less", "lte", "at-most"].includes(condition)) {
          if (hasDay) {
            return new Date(year, month - 1, day + 1).getTime() - 1;
          }
          if (hasMonth) {
            return new Date(year, month, 1).getTime() - 1;
          }
          return new Date(year + 1, 0, 1).getTime() - 1;
        }
        return new Date(
          year,
          month - 1,
          day,
        ).getTime();
      }
    }
    if (Number.isFinite(Number(params?.daysAgo))) {
      return Date.now() - (Math.max(0, Number(params.daysAgo)) * 86400000);
    }
    return 0;
  }

  function compareDate(actualSeconds, params) {
    const actualMs = Number(actualSeconds) > 100000000000
      ? Number(actualSeconds)
      : Number(actualSeconds) * 1000;
    const threshold = dateThreshold(params);
    if (!actualMs || !threshold) {
      return false;
    }
    return compareNumber(actualMs, { ...params, threshold }, ["threshold"]);
  }

  function getCollection(context, id) {
    const store = context?.collectionStore || root.collectionStore;
    try {
      return store?.GetCollection?.(id) ||
        arrayOf(store?.userCollections).find((collection) => String(collection?.id) === String(id));
    } catch (_) {
      return null;
    }
  }

  function collectionHasApp(collection, appId) {
    if (!collection || !appId) {
      return false;
    }
    if (collection?.apps instanceof Map) {
      return collection.apps.has(appId);
    }
    return arrayOf(collection?.allApps || collection?.visibleApps || collection?.apps)
      .some((entry) => positiveInteger(entry?.appid ?? entry?.appId ?? entry) === appId);
  }

  function includesAllOrAny(actualValues, expectedValues, mode) {
    const actual = new Set(arrayOf(actualValues).map((value) => String(value).toLowerCase()));
    const expected = arrayOf(expectedValues).map((value) => String(value).toLowerCase());
    if (!expected.length) {
      return false;
    }
    return String(mode || "any").toLowerCase() === "all"
      || String(mode || "").toLowerCase() === "and"
      ? expected.every((value) => actual.has(value))
      : expected.some((value) => actual.has(value));
  }

  function getFriendOwnedGamesMap(context) {
    const map = context?.friendOwnedGamesMap ||
      root.friendStore?.m_ownedGames?.m_dataMap?._data;
    return map && typeof map.get === "function" ? map : null;
  }

  function cachedFriendOwnsApp(friendId, appId, context) {
    const ownedGamesMap = getFriendOwnedGamesMap(context);
    if (!ownedGamesMap) {
      return null;
    }
    try {
      const entry = ownedGamesMap.get(friendId) ?? ownedGamesMap.get(String(friendId));
      const games = entry?.value?.m_data?.setApps ??
        entry?.m_data?.setApps ??
        entry?.setApps ??
        entry;
      return Boolean(
        games?.has?.(appId) ||
        arrayOf(games).some((game) => positiveInteger(game) === appId),
      );
    } catch (_) {
      return false;
    }
  }

  function evaluateFilter(filterValue, app, context = {}, depth = 0) {
    const type = knownFilterTypes.has(filterValue?.type)
      ? filterValue.type
      : normalizeFilterType(filterValue?.type);
    if (!knownFilterTypes.has(type) || !app || depth > 5) {
      return false;
    }
    const filter = {
      type,
      inverted: filterValue?.inverted === true,
      params: filterValue?.params && typeof filterValue.params === "object"
        ? filterValue.params
        : {},
    };
    const params = filter.params || {};
    const appId = appIdOf(app);
    let matched = false;
    try {
      switch (filter.type) {
        case "collection":
          matched = collectionHasApp(getCollection(context, params.id), appId);
          break;
        case "installed":
          matched = Boolean(app.installed ?? app.bIsInstalled ?? app.local?.installed) === (params.installed !== false);
          break;
        case "regex": {
          const pattern = String(params.regex ?? params.pattern ?? "");
          const regex = getCompiledRegex(pattern, params.flags);
          matched = Boolean(regex) && regex.test(String(app.display_name ?? app.displayName ?? app.name ?? ""));
          break;
        }
        case "friends": {
          const requestedFriends = arrayOf(params.friends);
          const cachedResults = requestedFriends.map((friendId) =>
            cachedFriendOwnsApp(friendId, appId, context));
          if (cachedResults.length && cachedResults.every((result) => result !== null)) {
            matched = String(params.mode || "any").toLowerCase() === "all" ||
              String(params.mode || "").toLowerCase() === "and"
              ? cachedResults.every(Boolean)
              : cachedResults.some(Boolean);
          } else {
            const owners = typeof context.getFriendsWhoOwn === "function"
              ? context.getFriendsWhoOwn(appId)
              : app.friends_who_own ?? app.friendsWhoOwn ?? [];
            matched = includesAllOrAny(owners, requestedFriends, params.mode);
          }
          break;
        }
        case "tags":
          matched = includesAllOrAny(app.store_tag ?? app.storeTags ?? app.tags, params.tags, params.mode);
          break;
        case "whitelist":
          matched = arrayOf(params.games ?? params.appIds).map(positiveInteger).includes(appId);
          break;
        case "blacklist":
          matched = !arrayOf(params.games ?? params.appIds).map(positiveInteger).includes(appId);
          break;
        case "merge": {
          const results = arrayOf(params.filters).map((entry) => evaluateFilter(entry, app, context, depth + 1));
          matched = String(params.mode || "and").toLowerCase() === "or"
            ? results.some(Boolean)
            : results.every(Boolean);
          break;
        }
        case "platform": {
          const appType = numberFrom(app, ["app_type", "appType"]);
          const platform = String(params.platform || params.value || "steam").toLowerCase();
          const nonSteam = appType === 1073741824 || app.nonSteam === true;
          matched = platform === "nonsteam" || platform === "non-steam"
            ? nonSteam
            : !nonSteam && appType !== 4 && appType !== 2048;
          break;
        }
        case "deck compatibility":
          matched = numberFrom(app, ["steam_deck_compat_category", "steamDeckCompatCategory"], -1) === Number(params.category);
          break;
        case "steamos compatibility": {
          const explicitCategory = numberFrom(
            app,
            ["steamos_compat_category", "steam_os_compat_category", "steamOsCompatCategory"],
            -1,
          );
          const packedCategory = (numberFrom(app, ["steam_hw_compat_category_packed", "steamHwCompatCategoryPacked"]) >> 4) & 3;
          matched = (explicitCategory >= 0 ? explicitCategory : packedCategory) === Number(params.category);
          break;
        }
        case "review score": {
          const score = String(params.type || "steam").toLowerCase() === "metacritic"
            ? numberFrom(app, ["metacritic_score", "metacriticScore"])
            : numberFrom(app, ["review_percentage", "reviewPercentage"]);
          matched = compareNumber(score, params, ["scoreThreshold", "threshold", "score"]);
          break;
        }
        case "time played": {
          let threshold = Number(params.timeThreshold ?? params.threshold ?? 0);
          const units = String(params.units || "minutes").toLowerCase();
          if (units === "hours") threshold *= 60;
          if (units === "days") threshold *= 1440;
          matched = compareNumber(
            numberFrom(app, ["minutes_playtime_forever", "minutesPlaytimeForever", "playtimeMinutes"]),
            { ...params, threshold },
            ["threshold"],
          );
          break;
        }
        case "size on disk":
          matched = compareNumber(
            numberFrom(app, ["size_on_disk", "sizeOnDisk"]) / (1024 ** 3),
            params,
            ["gbThreshold", "threshold"],
          );
          break;
        case "release date":
          matched = compareDate(numberFrom(app, ["rt_original_release_date", "rt_steam_release_date", "releaseDate"]), params);
          break;
        case "purchase date":
          matched = compareDate(numberFrom(app, ["rt_purchased_time", "purchaseDate"]), params);
          break;
        case "last played":
          matched = compareDate(numberFrom(app, ["rt_last_time_played", "lastPlayed"]), params);
          break;
        case "family sharing": {
          const store = context.collectionStore || root.collectionStore;
          const shared = arrayOf(store?.sharedLibrariesCollections).some((collection) => collectionHasApp(collection, appId));
          matched = (params.isFamilyShared !== false) === shared;
          break;
        }
        case "demo":
          matched = (numberFrom(app, ["app_type", "appType"]) === 8 || booleanFrom(app, ["isDemo"])) === (params.isDemo !== false);
          break;
        case "coming soon":
          matched = (numberFrom(app, ["display_status", "displayStatus"]) === 13 || booleanFrom(app, ["comingSoon"])) === (params.isComingSoon !== false);
          break;
        case "streamable": {
          const streamable = booleanFrom(app, ["streamable", "isStreamable"]) || arrayOf(app.per_client_data ?? app.perClientData)
            .some((client) => client?.installed === true && !["", "This machine"].includes(String(client?.client_name ?? client?.clientName ?? "")));
          matched = streamable === (params.isStreamable !== false);
          break;
        }
        case "steam features":
          matched = includesAllOrAny(app.store_category ?? app.storeCategories ?? app.features, params.features, params.mode);
          break;
        case "achievements": {
          const achievementCache = context.achievementProgressCache || root.appAchievementProgressCache;
          const progress = typeof context.getAchievementProgress === "function"
            ? context.getAchievementProgress(appId)
            : typeof achievementCache?.GetAchievementProgress === "function"
              ? String(params.thresholdType || "percent").toLowerCase() === "count"
                ? achievementCache?.m_achievementProgress?.mapCache?.get?.(appId) || {}
                : achievementCache.GetAchievementProgress(appId)
              : app.achievementProgress ?? app.achievements ?? {};
          const actual = String(params.thresholdType || "percent").toLowerCase() === "count"
            ? Number(progress?.unlocked ?? progress?.count ?? 0)
            : Number(progress?.percentage ?? progress?.percent ?? progress ?? 0);
          matched = compareNumber(actual, params, ["threshold"]);
          break;
        }
        case "sd card": {
          const cards = context.cardsAndGames || root.MicroSDeck?.CardsAndGames || [];
          const requestedCard = params.card;
          const isOnCard = (entry) => {
            const card = Array.isArray(entry) ? entry[0] : entry?.card;
            const games = Array.isArray(entry) ? entry[1] : entry?.games;
            return {
              card,
              includesApp: arrayOf(games).some((game) => positiveInteger(game?.uid ?? game?.appId ?? game) === appId),
            };
          };
          if (["any", "1"].includes(String(requestedCard).toLowerCase())) {
            matched = arrayOf(cards).some((entry) => isOnCard(entry).includesApp);
          } else if (requestedCard === undefined || requestedCard === null || Number(requestedCard) === 0) {
            matched = isOnCard(context.currentCardAndGames || root.MicroSDeck?.CurrentCardAndGames).includesApp;
          } else {
            matched = arrayOf(cards).some((entry) => {
              const candidate = isOnCard(entry);
              return String(candidate.card?.uid ?? candidate.card?.id ?? "") === String(requestedCard) && candidate.includesApp;
            });
          }
          break;
        }
        case "install folder": {
          const store = context.installFolderStore || root.installFolderStore;
          const folder = arrayOf(store?.AllInstallFolders).find((entry) =>
            String(entry?.strDriveName ?? entry?.driveName ?? "") === String(params.driveName ?? params.path ?? ""));
          matched = Boolean(folder?.bIsMounted !== false && arrayOf(folder?.vecApps ?? folder?.apps)
            .some((entry) => positiveInteger(entry?.nAppID ?? entry?.appId ?? entry) === appId));
          break;
        }
        default:
          matched = false;
          break;
      }
    } catch (_) {
      matched = false;
    }
    return filter.inverted ? !matched : matched;
  }

  function appMatchesCategories(app, categories) {
    const selected = arrayOf(categories).map((entry) => String(entry).toLowerCase());
    if (!selected.length || selected.includes("all")) {
      return true;
    }
    const appType = numberFrom(app, ["app_type", "appType"]);
    const nonSteam = appType === 1073741824 || app.nonSteam === true;
    return selected.some((category) => {
      if (category === "nonsteam" || category === "non-steam") return nonSteam;
      if (category === "music") return appType === 4;
      if (category === "software") return appType === 2 || appType === 2048;
      if (category === "demo") return appType === 8;
      if (category === "games") return !nonSteam && ![2, 4, 2048].includes(appType);
      return true;
    });
  }

  function filterApps(apps, tabValue, context = {}) {
    const tab = normalizeCustomTab(tabValue || {}, 0);
    return arrayOf(apps).filter((app) => {
      if (!appMatchesCategories(app, tab.categories)) {
        return false;
      }
      if (!tab.filters.length) {
        return true;
      }
      const results = tab.filters.map((filter) => evaluateFilter(filter, app, context));
      return tab.matchMode === "any" ? results.some(Boolean) : results.every(Boolean);
    });
  }

  function validateFilters(filters, depth = 0, budget = { count: 0 }) {
    const errors = [];
    if (!Array.isArray(filters)) {
      return { valid: false, errors: ["Filters must be a JSON array."] };
    }
    if (depth > 5) {
      return { valid: false, errors: ["Nested merge filters may be at most 5 levels deep."] };
    }
    if (filters.length > maximumFiltersPerGroup) {
      errors.push(`A filter group may contain at most ${maximumFiltersPerGroup} entries.`);
    }
    budget.count += filters.length;
    if (budget.count > maximumTotalFilters) {
      return {
        valid: false,
        errors: [`A custom tab may contain at most ${maximumTotalFilters} filters in total.`],
      };
    }
    for (const [index, rawFilter] of filters.slice(0, maximumFiltersPerGroup).entries()) {
      if (!rawFilter || typeof rawFilter !== "object" || Array.isArray(rawFilter)) {
        errors.push(`Filter ${index + 1}: expected an object.`);
        continue;
      }
      const type = normalizeFilterType(rawFilter?.type);
      if (!knownFilterTypes.has(type)) {
        errors.push(`Filter ${index + 1}: unknown type "${text(rawFilter?.type, 80)}".`);
        continue;
      }
      if (type === "regex") {
        const regexError = getRegexError(
          rawFilter?.params?.regex ?? rawFilter?.params?.pattern ?? "",
          rawFilter?.params?.flags,
        );
        if (regexError) {
          errors.push(`Filter ${index + 1}: ${regexError}`);
        }
      }
      if (type === "merge") {
        const nested = validateFilters(rawFilter?.params?.filters, depth + 1, budget);
        errors.push(...nested.errors.map((message) => `Filter ${index + 1} / ${message}`));
      }
      if (type === "platform") {
        const platform = String(rawFilter?.params?.platform || "steam").toLowerCase();
        if (!["steam", "nonsteam", "non-steam"].includes(platform)) {
          errors.push(`Filter ${index + 1}: platform must be "steam" or "nonSteam".`);
        }
      }
      for (const parameterName of ["threshold", "scoreThreshold", "timeThreshold", "gbThreshold", "daysAgo"]) {
        const parameter = rawFilter?.params?.[parameterName];
        if (parameter !== undefined && !Number.isFinite(Number(parameter))) {
          errors.push(`Filter ${index + 1}: ${parameterName} must be a number.`);
        }
      }
    }
    return { valid: errors.length === 0, errors };
  }

  function saveProfile(titleValue) {
    let profile = null;
    let failure = "";
    const nextSnapshot = commit("profile-save", (base) => {
      if (base.profiles.length >= maximumProfiles) {
        failure = "limit";
        return null;
      }
      const title = text(titleValue, 80) || `Profile ${base.profiles.length + 1}`;
      const id = `profile-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
      profile = normalizeProfile({
        id,
        title,
        order: base.order,
        native: base.native,
        customTabIds: base.customTabs.filter((tab) => tab.enabled).map((tab) => tab.id),
      }, base.profiles.length);
      return {
        ...base,
        profiles: [...base.profiles, profile],
        activeProfileId: profile.id,
      };
    }, { undoable: true });
    if (failure || !profile) {
      return { ok: false, reason: failure || "invalid", snapshot: nextSnapshot };
    }
    return { ok: true, profile, snapshot: nextSnapshot };
  }

  function applyProfile(profileId) {
    let found = false;
    const nextSnapshot = commit("profile-apply", (base) => {
      const profile = base.profiles.find((entry) => entry.id === profileId);
      if (!profile) {
        return null;
      }
      found = true;
      const visibleCustomIds = new Set(profile.customTabIds);
      const customTabs = base.customTabs.map((tab) => ({ ...tab, enabled: visibleCustomIds.has(tab.id) }));
      return {
        ...base,
        activeProfileId: profile.id,
        order: profile.order,
        native: profile.native,
        customTabs,
      };
    }, { undoable: true });
    if (!found) {
      return { ok: false, reason: "missing", snapshot: getSnapshot() };
    }
    return { ok: true, snapshot: nextSnapshot };
  }

  function deleteProfile(profileId) {
    let found = false;
    const nextSnapshot = commit("profile-delete", (base) => {
      const profiles = base.profiles.filter((entry) => entry.id !== profileId);
      if (profiles.length === base.profiles.length) {
        return null;
      }
      found = true;
      return {
        ...base,
        profiles,
        activeProfileId: base.activeProfileId === profileId ? "" : base.activeProfileId,
      };
    }, { undoable: true });
    if (!found) {
      return { ok: false, reason: "missing", snapshot: getSnapshot() };
    }
    return { ok: true, snapshot: nextSnapshot };
  }

  function resetAll() {
    return commit("reset-all", (base) => ({
      ...normalizeSnapshot(null),
      enabled: base.enabled,
      catalog: base.catalog,
    }), { undoable: true });
  }

  if (typeof root.BroadcastChannel === "function") {
    try {
      channel = new root.BroadcastChannel(channelName);
      channel.addEventListener("message", () => reloadFromStorage("broadcast"));
    } catch (_) {
      channel = null;
    }
  }
  if (typeof root.addEventListener === "function") {
    storageHandler = (event) => {
      if (!event || event.key === storageKey || event.key === catalogStorageKey) {
        reloadFromStorage("storage");
      }
    };
    root.addEventListener("storage", storageHandler);
  }

  root.__steamLoaderTabHero = Object.freeze({
    version: engineVersion,
    storageKey,
    catalogStorageKey,
    channelName,
    knownFilterTypes: Object.freeze(Array.from(knownFilterTypes)),
    getSnapshot,
    subscribe,
    setEnabled,
    isProtectedTab,
    updateNativeTab,
    resetNativeTab,
    upsertCustomTab,
    deleteCustomTab,
    duplicateCustomTab,
    moveTab,
    moveTabToEdge,
    showAllNativeTabs,
    canUndo,
    undoLastChange,
    observeTabs,
    composeTabs,
    evaluateFilter,
    filterApps,
    validateFilters,
    saveProfile,
    applyProfile,
    deleteProfile,
    resetAll,
    dispose() {
      try {
        channel?.close?.();
      } catch (_) {}
      if (storageHandler) {
        root.removeEventListener?.("storage", storageHandler);
      }
      listeners.clear();
    },
  });
})(typeof globalThis === "object" ? globalThis : window);
