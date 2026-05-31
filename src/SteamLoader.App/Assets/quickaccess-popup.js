(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const stateVersion = 21;
  const soundtrackTabKey = 7;

  if (window.__steamLoaderPopupTimer) {
    window.clearInterval(window.__steamLoaderPopupTimer);
    window.__steamLoaderPopupTimer = null;
  }

  const previousState = window.__steamLoaderPopupReactState;

  const state =
    previousState?.version === stateVersion
      ? previousState
      : (window.__steamLoaderPopupReactState = {
          version: stateVersion,
          installed: false,
          reactElementSymbol: null,
          qamNode: null,
          forceHosts: [],
          route: { screen: "root", pluginId: null, pageId: null },
          audio: {
            loading: false,
            devices: [],
            error: "",
            volumeLoading: false,
            volumeError: "",
            volumeInfo: null,
            activeVolumeActionIndex: 0,
            pendingVolumeActionAutoFocus: false,
          },
          display: {
            switching: false,
            error: "",
            status: "",
          },
          hltb: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
          },
          storeSync: {
            loading: false,
            saving: false,
            syncing: false,
            error: "",
            snapshot: null,
            customPathDraft: "",
            customPathInputVersion: 0,
          },
          themes: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
            detailOriginByThemeId: {},
            detailOriginByProfileId: {},
            profileDraft: "",
            profileDraftInputVersion: 0,
          },
          generalSettings: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
          },
          nativeUi: {
            dialogButtonType: null,
          },
          slotActions: [],
          renderRevision: 1,
          panelObserver: null,
          panelObserverHost: null,
          panelVisible: false,
          pendingEntryAutoFocus: true,
          lastSelectedIndexByRoute: {},
          pendingFocusRouteKey: null,
          pendingFocusIndex: null,
        });

  const plugins = [
    {
      id: "settings",
      title: "Settings",
      description: "Steam Tools-wide behavior and startup",
      pages: [
        {
          id: "general",
          title: "General",
          description: "Startup behavior and global loader options",
        },
      ],
    },
    {
      id: "audio",
      title: "Audio",
      description: "Output devices and audio tools",
      pages: [
        {
          id: "output-device-changer",
          title: "Output Device Changer",
          description: "Switch the Windows default device",
        },
        {
          id: "system-volume",
          title: "System Volume",
          description: "Quick controls for quieter, louder and mute",
        },
      ],
    },
    {
      id: "display",
      title: "Display",
      description: "Switch between internal and external output",
      pages: [],
    },
    {
      id: "hltb",
      title: "HLTB",
      description: "Show HowLongToBeat estimates on game pages",
      pages: [
        {
          id: "settings",
          title: "Settings",
          description: "Choose which HLTB stats appear on the open game page",
        },
      ],
    },
    {
      id: "store-sync",
      title: "Store Sync",
      description: "Bring other PC launchers into Steam",
      pages: [
        {
          id: "sync-now",
          title: "Sync Now",
          description: "Scan enabled stores and update Steam shortcuts",
        },
        {
          id: "settings",
          title: "Settings",
          description: "Artwork and sync behavior",
        },
        {
          id: "stores",
          title: "Stores",
          description: "Manage individual launcher sources and custom paths",
        },
      ],
    },
    {
      id: "themes",
      title: "Themes",
      description: "Browse, install, and tune Steam Tools themes",
      pages: [
        {
          id: "store",
          title: "Store",
          description: "Browse built-in and community themes",
        },
        {
          id: "installed",
          title: "Installed",
          description: "Manage active themes and per-theme options",
        },
        {
          id: "profiles",
          title: "Profiles",
          description: "Save, apply, and download full theme setups",
        },
        {
          id: "settings",
          title: "Settings",
          description: "Engine behavior and install defaults",
        },
      ],
    },
  ];

  function ensureStyles() {
    let style = document.getElementById("steamloader-react-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "steamloader-react-style";
      document.head.append(style);
    }

    style.textContent = `
      .steamloader-panel {
        display: flex;
        flex-direction: column;
        min-height: 100%;
        padding: 18px 16px 24px;
        box-sizing: border-box;
        color: #d9e0e8;
        background: #0f151d;
        overflow-y: auto;
      }

      .steamloader-header {
        display: flex;
        align-items: flex-start;
        gap: 12px;
        margin-bottom: 16px;
      }

      .steamloader-header-mark {
        width: 44px;
        height: 44px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        border-radius: 14px;
        background: rgba(255, 255, 255, 0.06);
        color: rgba(232, 237, 242, 0.86);
        flex: 0 0 auto;
      }

      .steamloader-header-mark svg,
      .steamloader-row-trailing svg {
        width: 22px;
        height: 22px;
      }

      .steamloader-title-wrap {
        min-width: 0;
      }

      .steamloader-title {
        margin: 0;
        color: #edf2f7;
        font-size: clamp(24px, 3.1vw, 38px);
        line-height: 1.04;
        font-weight: 700;
        letter-spacing: -0.03em;
      }

      .steamloader-subtitle {
        margin-top: 6px;
        color: rgba(176, 186, 197, 0.84);
        font-size: clamp(12px, 1.45vw, 16px);
        line-height: 1.35;
      }

      .steamloader-stack {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }

      .steamloader-dialog-button {
        width: 100%;
      }

      .steamloader-dialog-button-subtle {
        width: 100%;
      }

      .steamloader-row-shell {
        width: 100%;
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 12px;
        align-items: center;
        padding: 2px 0;
        text-align: left;
      }

      .steamloader-row-shell-with-icon {
        grid-template-columns: auto minmax(0, 1fr) auto;
      }

      .steamloader-row-shell-subtle {
        gap: 10px;
        padding: 0;
      }

      .steamloader-row-icon {
        width: 34px;
        height: 34px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        border-radius: 11px;
        background: rgba(255, 255, 255, 0.055);
        color: rgba(220, 228, 236, 0.9);
        flex: 0 0 auto;
      }

      .steamloader-row-icon svg {
        width: 18px;
        height: 18px;
      }

      .steamloader-row-shell-subtle .steamloader-row-icon {
        width: 30px;
        height: 30px;
        border-radius: 10px;
        background: rgba(255, 255, 255, 0.038);
      }

      .steamloader-row-main {
        min-width: 0;
        text-align: left;
      }

      .steamloader-row-title {
        color: rgba(214, 222, 231, 0.92);
        font-size: clamp(16px, 2vw, 21px);
        line-height: 1.2;
        font-weight: 500;
      }

      .steamloader-row-copy {
        margin-top: 3px;
        color: rgba(154, 166, 178, 0.9);
        font-size: clamp(11px, 1.3vw, 14px);
        line-height: 1.35;
      }

      .steamloader-row-shell-subtle .steamloader-row-title {
        font-size: clamp(15px, 1.75vw, 18px);
        line-height: 1.16;
        font-weight: 600;
      }

      .steamloader-row-shell-subtle .steamloader-row-copy {
        margin-top: 2px;
        color: rgba(145, 157, 169, 0.86);
        font-size: clamp(10px, 1.12vw, 12px);
        line-height: 1.32;
      }

      .steamloader-row-trailing {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        color: rgba(187, 197, 208, 0.8);
      }

      .steamloader-badge {
        display: inline-flex;
        align-items: center;
        padding: 5px 10px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.09);
        color: rgba(214, 222, 231, 0.9);
        font-size: 11px;
        line-height: 1;
      }

      .steamloader-card {
        padding: 12px 13px;
        border-radius: 16px;
        background: rgba(255, 255, 255, 0.05);
      }

      .steamloader-card + .steamloader-card {
        margin-top: 10px;
      }

      .steamloader-editor-card {
        margin-top: 10px;
        padding: 12px 13px;
        border-radius: 16px;
        background: rgba(255, 255, 255, 0.05);
      }

      .steamloader-editor-label {
        color: rgba(214, 222, 231, 0.94);
        font-size: clamp(12px, 1.45vw, 16px);
        line-height: 1.25;
        font-weight: 600;
      }

      .steamloader-editor-help {
        margin-top: 5px;
        color: rgba(160, 171, 182, 0.92);
        font-size: clamp(11px, 1.28vw, 14px);
        line-height: 1.4;
      }

      .steamloader-editor-textarea {
        width: 100%;
        min-height: 84px;
        margin-top: 9px;
        padding: 10px 12px;
        box-sizing: border-box;
        border: 1px solid rgba(255, 255, 255, 0.12);
        border-radius: 12px;
        background: rgba(10, 15, 21, 0.72);
        color: #d9e0e8;
        font: inherit;
        font-size: clamp(11px, 1.28vw, 14px);
        line-height: 1.45;
        resize: vertical;
      }

      .steamloader-editor-textarea:focus {
        outline: none;
        border-color: rgba(106, 169, 255, 0.72);
        box-shadow: 0 0 0 1px rgba(106, 169, 255, 0.26);
      }

      .steamloader-card-title {
        color: rgba(214, 222, 231, 0.94);
        font-size: clamp(12px, 1.45vw, 16px);
        line-height: 1.25;
        font-weight: 600;
      }

      .steamloader-card-line {
        margin-top: 5px;
        color: rgba(160, 171, 182, 0.92);
        font-size: clamp(11px, 1.28vw, 14px);
        line-height: 1.4;
      }

      .steamloader-switch-wrap {
        display: inline-flex;
        align-items: center;
        gap: 8px;
      }

      .steamloader-switch {
        position: relative;
        width: 40px;
        height: 22px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.16);
        transition: background 120ms ease;
      }

      .steamloader-switch.is-on {
        background: rgba(57, 158, 255, 0.85);
      }

      .steamloader-switch-thumb {
        position: absolute;
        top: 2px;
        left: 2px;
        width: 18px;
        height: 18px;
        border-radius: 50%;
        background: #eef3f8;
        transition: transform 120ms ease, background 120ms ease;
      }

      .steamloader-switch.is-on .steamloader-switch-thumb {
        transform: translateX(18px);
      }

      .steamloader-switch-label {
        color: rgba(187, 197, 208, 0.82);
        font-size: 11px;
        line-height: 1;
      }

      .steamloader-volume-card {
        margin-top: 10px;
        width: 100%;
        box-sizing: border-box;
        overflow: hidden;
        padding: 12px 12px 10px;
        border-radius: 16px;
        background: rgba(255, 255, 255, 0.05);
      }

      .steamloader-volume-entry-button {
        width: 100%;
        overflow: hidden;
      }

      .steamloader-volume-head {
        display: flex;
        align-items: flex-start;
        gap: 12px;
      }

      .steamloader-volume-copy-wrap {
        min-width: 0;
      }

      .steamloader-volume-title {
        color: rgba(214, 222, 231, 0.92);
        font-size: clamp(15px, 1.85vw, 19px);
        line-height: 1.2;
        font-weight: 500;
      }

      .steamloader-volume-copy {
        margin-top: 3px;
        color: rgba(154, 166, 178, 0.9);
        font-size: clamp(10px, 1.15vw, 13px);
        line-height: 1.32;
        display: -webkit-box;
        -webkit-box-orient: vertical;
        -webkit-line-clamp: 2;
        overflow: hidden;
      }

      .steamloader-volume-hint {
        margin-top: 6px;
        color: rgba(144, 156, 168, 0.88);
        font-size: 10px;
        line-height: 1.35;
      }

      .steamloader-volume-hint-error {
        color: #f0c28f;
      }

      .steamloader-volume-actions {
        display: grid;
        grid-template-columns: 52px 52px minmax(0, 1fr);
        gap: 6px;
        margin-top: 8px;
        align-items: stretch;
      }

      .steamloader-volume-actions > * {
        min-width: 0;
      }

      .steamloader-volume-action-button {
        width: 100%;
        min-width: 0;
        overflow: hidden;
      }

      .steamloader-volume-action-shell {
        display: flex;
        align-items: center;
        justify-content: center;
        min-height: 24px;
        text-align: center;
      }

      .steamloader-volume-action-title {
        color: rgba(214, 222, 231, 0.92);
        font-size: clamp(12px, 1.4vw, 14px);
        line-height: 1;
        font-weight: 600;
        white-space: nowrap;
      }

      @media (max-width: 430px) {
        .steamloader-volume-actions {
          grid-template-columns: 44px 44px minmax(0, 1fr);
          gap: 5px;
        }

        .steamloader-volume-card {
          padding: 10px 10px 9px;
        }
      }

      .steamloader-dialog-button.gpfocus .steamloader-row-title,
      .steamloader-dialog-button.gpfocus .steamloader-row-copy,
      .steamloader-dialog-button.gpfocus .steamloader-row-trailing {
        color: #293544;
      }

      .steamloader-dialog-button.gpfocus .steamloader-row-icon {
        background: rgba(41, 53, 68, 0.14);
        color: #293544;
      }

      .steamloader-dialog-button.gpfocus .steamloader-badge {
        background: rgba(41, 53, 68, 0.12);
        color: #293544;
      }

      .steamloader-dialog-button.gpfocus .steamloader-switch {
        background: rgba(41, 53, 68, 0.22);
      }

      .steamloader-dialog-button.gpfocus .steamloader-switch.is-on {
        background: rgba(41, 53, 68, 0.82);
      }

      .steamloader-dialog-button.gpfocus .steamloader-switch-thumb {
        background: #eef3f8;
      }

      .steamloader-dialog-button.gpfocus .steamloader-switch-label {
        color: #293544;
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-action-title {
        color: #293544;
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-title,
      .steamloader-dialog-button.gpfocus .steamloader-volume-copy,
      .steamloader-dialog-button.gpfocus .steamloader-volume-hint {
        color: #293544;
      }

      .steamloader-note,
      .steamloader-error,
      .steamloader-status {
        padding: 10px 12px;
        border-radius: 16px;
        font-size: clamp(11px, 1.3vw, 14px);
        line-height: 1.4;
      }

      .steamloader-note {
        background: rgba(255, 255, 255, 0.05);
        color: rgba(168, 179, 190, 0.9);
      }

      .steamloader-status {
        margin-bottom: 10px;
        background: rgba(255, 255, 255, 0.04);
        color: rgba(162, 173, 184, 0.9);
      }

      .steamloader-error {
        margin-bottom: 10px;
        background: rgba(105, 60, 22, 0.45);
        color: #f0c28f;
      }

      .steamloader-fallback-button {
        width: 100%;
        border: 0;
        border-radius: 14px;
        padding: 16px 18px;
        background: #363c44;
        color: #f4f5f7;
        text-align: left;
        font: inherit;
        cursor: pointer;
      }

      .steamloader-divider {
        height: 1px;
        margin: 4px 2px 6px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.12);
      }
    `;
  }

  function cleanupLegacyNodes() {
    for (const selector of [
      "#quickaccess_tab_9001",
      "#quickaccess_content_9001",
      "#steamloader-shell",
      "[data-steamloader-legacy='true']",
    ]) {
      for (const node of document.querySelectorAll(selector)) {
        node.remove();
      }
    }
  }

  function getReactPropertyKey(element, prefix) {
    return element
      ? Object.getOwnPropertyNames(element).find((name) => name.startsWith(prefix))
      : null;
  }

  function getQuickAccessRootFiber() {
    const rootElement = document.getElementById("QuickAccess-NA");
    const rootKey =
      getReactPropertyKey(rootElement, "__reactFiber") ||
      getReactPropertyKey(rootElement, "__reactContainer");

    return rootKey ? rootElement[rootKey] : null;
  }

  function getReactFiber(element) {
    const fiberKey = getReactPropertyKey(element, "__reactFiber");
    return fiberKey ? element[fiberKey] : null;
  }

  function getPanelHost() {
    return document.getElementById("quickaccess_content_7");
  }

  function getRouteKey(route = state.route) {
    if (!route || route.screen === "root") {
      return "root";
    }

    if (route.screen === "plugin") {
      return `plugin:${route.pluginId}`;
    }

    if (route.screen === "page") {
      return `page:${route.pluginId}:${route.pageId}`;
    }

    return "root";
  }

  function rememberCurrentRouteIndex(index) {
    state.lastSelectedIndexByRoute[getRouteKey(state.route)] = index;
  }

  function requestFocusForRoute(route, fallbackIndex = null) {
    const routeKey = getRouteKey(route);
    const rememberedIndex = state.lastSelectedIndexByRoute[routeKey];

    state.pendingFocusRouteKey = routeKey;
    state.pendingFocusIndex = Number.isInteger(rememberedIndex)
      ? rememberedIndex
      : Number.isInteger(fallbackIndex)
        ? fallbackIndex
        : null;
  }

  function getPluginPageIndex(pluginId, pageId) {
    const plugin = plugins.find((entry) => entry.id === pluginId);
    if (!plugin) {
      return null;
    }

    const pageIndex = plugin.pages.findIndex((page) => page.id === pageId);
    return pageIndex >= 0 ? pageIndex : null;
  }

  function getStoreSyncStoreIndex(storeId) {
    const stores = state.storeSync.snapshot?.stores;
    if (!Array.isArray(stores)) {
      return null;
    }

    const storeIndex = stores.findIndex((store) => store.id === storeId);
    return storeIndex >= 0 ? storeIndex : null;
  }

  function isThemesThemeRoute(route = state.route) {
    return Boolean(
      route &&
        route.screen === "page" &&
        route.pluginId === "themes" &&
        typeof route.pageId === "string" &&
        route.pageId.startsWith("theme-") &&
        !route.pageId.startsWith("theme-option-"),
    );
  }

  function isThemesThemeOptionRoute(route = state.route) {
    return Boolean(
      route &&
        route.screen === "page" &&
        route.pluginId === "themes" &&
        typeof route.pageId === "string" &&
        route.pageId.startsWith("theme-option-"),
    );
  }

  function isThemesProfileRoute(route = state.route) {
    return Boolean(
      route &&
        route.screen === "page" &&
        route.pluginId === "themes" &&
        typeof route.pageId === "string" &&
        route.pageId.startsWith("profile-"),
    );
  }

  function getThemeIdFromRoute(route = state.route) {
    if (!route || route.pluginId !== "themes" || typeof route.pageId !== "string") {
      return null;
    }

    if (route.pageId.startsWith("theme-option-")) {
      const payload = route.pageId.replace(/^theme-option-/, "");
      const parts = payload.split("--");
      return parts[0] || null;
    }

    if (route.pageId.startsWith("theme-")) {
      return route.pageId.replace(/^theme-/, "") || null;
    }

    return null;
  }

  function getThemeOptionIdFromRoute(route = state.route) {
    if (!route || route.pluginId !== "themes" || typeof route.pageId !== "string") {
      return null;
    }

    if (!route.pageId.startsWith("theme-option-")) {
      return null;
    }

    const payload = route.pageId.replace(/^theme-option-/, "");
    const parts = payload.split("--");
    return parts[1] || null;
  }

  function getThemeProfileIdFromRoute(route = state.route) {
    if (!route || route.pluginId !== "themes" || typeof route.pageId !== "string") {
      return null;
    }

    if (!route.pageId.startsWith("profile-")) {
      return null;
    }

    return route.pageId.replace(/^profile-/, "") || null;
  }

  function getThemesBrowseIndex(themeId) {
    const themes = state.themes.snapshot?.browseThemes;
    if (!Array.isArray(themes)) {
      return null;
    }

    const index = themes.findIndex((theme) => theme.id === themeId);
    return index >= 0 ? index : null;
  }

  function getThemesInstalledIndex(themeId) {
    const themes = state.themes.snapshot?.installedThemes;
    if (!Array.isArray(themes)) {
      return null;
    }

    const index = themes.findIndex((theme) => theme.id === themeId);
    return index >= 0 ? index : null;
  }

  function getThemeOptionSlotIndex(themeId, optionId) {
    const theme = getThemeById(themeId);
    if (!theme || !Array.isArray(theme.options)) {
      return null;
    }

    const optionIndex = theme.options.findIndex((option) => option.id === optionId);
    if (optionIndex < 0) {
      return null;
    }

    return theme.installed ? optionIndex + 1 : optionIndex;
  }

  function getThemesInstalledProfileIndex(profileId) {
    const profiles = state.themes.snapshot?.profiles?.installedProfiles;
    if (!Array.isArray(profiles)) {
      return null;
    }

    const index = profiles.findIndex((profile) => profile.id === profileId);
    return index >= 0 ? index + 1 : null;
  }

  function getThemesBrowseProfileIndex(profileId) {
    const browseProfiles = state.themes.snapshot?.profiles?.browseProfiles;
    const installedProfiles = state.themes.snapshot?.profiles?.installedProfiles;
    if (!Array.isArray(browseProfiles)) {
      return null;
    }

    const index = browseProfiles.findIndex((profile) => profile.id === profileId);
    return index >= 0 ? index + 1 + (Array.isArray(installedProfiles) ? installedProfiles.length : 0) : null;
  }

  function getBackNavigation(route = state.route) {
    if (!route || route.screen === "root") {
      return null;
    }

    if (route.screen === "plugin") {
      const pluginIndex = plugins.findIndex((plugin) => plugin.id === route.pluginId);
      return {
        route: parseRoute("root"),
        fallbackIndex: pluginIndex >= 0 ? pluginIndex : 0,
      };
    }

    if (route.screen === "page") {
      if (route.pluginId === "settings") {
        const pluginIndex = plugins.findIndex((plugin) => plugin.id === "settings");
        return {
          route: parseRoute("root"),
          fallbackIndex: pluginIndex >= 0 ? pluginIndex : 0,
        };
      }

      if (route.pluginId === "store-sync" && route.pageId?.startsWith("store-")) {
        const storeId = route.pageId.replace(/^store-/, "");
        return {
          route: parseRoute("page:store-sync:stores"),
          fallbackIndex: getStoreSyncStoreIndex(storeId),
        };
      }

      if (route.pluginId === "themes" && isThemesThemeOptionRoute(route)) {
        const themeId = getThemeIdFromRoute(route);
        const optionId = getThemeOptionIdFromRoute(route);

        return {
          route: parseRoute(`page:themes:theme-${themeId}`),
          fallbackIndex: themeId && optionId ? getThemeOptionSlotIndex(themeId, optionId) : null,
        };
      }

      if (route.pluginId === "themes" && isThemesThemeRoute(route)) {
        const themeId = getThemeIdFromRoute(route);
        const origin = themeId ? state.themes.detailOriginByThemeId[themeId] : "store";
        const pageId = origin === "installed" ? "installed" : "store";
        const fallbackIndex =
          origin === "installed"
            ? getThemesInstalledIndex(themeId)
            : getThemesBrowseIndex(themeId);

        return {
          route: parseRoute(`page:themes:${pageId}`),
          fallbackIndex,
        };
      }

      if (route.pluginId === "themes" && isThemesProfileRoute(route)) {
        const profileId = getThemeProfileIdFromRoute(route);
        const origin = profileId ? state.themes.detailOriginByProfileId[profileId] : "installed";
        const fallbackIndex =
          origin === "browse"
            ? getThemesBrowseProfileIndex(profileId)
            : getThemesInstalledProfileIndex(profileId);

        return {
          route: parseRoute("page:themes:profiles"),
          fallbackIndex,
        };
      }

      return {
        route: parseRoute(`plugin:${route.pluginId}`),
        fallbackIndex: getPluginPageIndex(route.pluginId, route.pageId),
      };
    }

    return null;
  }

  function navigateBackFromRoute(route = state.route) {
    const backNavigation = getBackNavigation(route);
    if (!backNavigation) {
      return;
    }

    requestFocusForRoute(backNavigation.route, backNavigation.fallbackIndex);
    setRoute(backNavigation.route);
  }

  function resolveAutoFocusIndex(route) {
    const routeKey = getRouteKey(route);

    if (state.pendingFocusRouteKey === routeKey && Number.isInteger(state.pendingFocusIndex)) {
      return state.pendingFocusIndex;
    }

    if (route.screen === "root" && state.pendingEntryAutoFocus) {
      return 0;
    }

    return null;
  }

  function consumeResolvedFocus(route, autoFocusIndex) {
    if (Number.isInteger(autoFocusIndex) && state.pendingFocusRouteKey === getRouteKey(route)) {
      state.pendingFocusRouteKey = null;
      state.pendingFocusIndex = null;
    }

    if (route.screen === "root" && Number.isInteger(autoFocusIndex) && state.pendingEntryAutoFocus) {
      state.pendingEntryAutoFocus = false;
    }
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

  function findInElementTree(node, predicate, visited = new Set()) {
    if (!node || typeof node !== "object" || visited.has(node)) {
      return null;
    }

    visited.add(node);
    if (predicate(node)) {
      return node;
    }

    const children = node.props?.children;
    if (Array.isArray(children)) {
      for (const child of children) {
        const match = findInElementTree(child, predicate, visited);
        if (match) {
          return match;
        }
      }
    } else if (children) {
      return findInElementTree(children, predicate, visited);
    }

    return null;
  }

  function getPanelForceHosts() {
    const panelRoot = document.querySelector("#quickaccess_content_7 .steamloader-panel");
    const fiber = getReactFiber(panelRoot);
    const hosts = [];
    let current = fiber;

    while (current) {
      if (current.stateNode && typeof current.stateNode.forceUpdate === "function") {
        hosts.push(current.stateNode);
      }

      current = current.return;
    }

    return hosts;
  }

  function isVisible(node) {
    return Boolean(
      node &&
        !node.hidden &&
        node.getClientRects().length &&
        window.getComputedStyle(node).display !== "none" &&
        window.getComputedStyle(node).visibility !== "hidden",
    );
  }

  function monitorPanelVisibility() {
    const visible = isVisible(getPanelHost());

    if (visible && !state.panelVisible) {
      state.panelVisible = true;

      if (state.route.screen === "root") {
        state.pendingEntryAutoFocus = true;
        state.renderRevision += 1;
        refreshQuickAccessPanel();
      }

      return;
    }

    if (!visible) {
      state.panelVisible = false;
    }
  }

  function ensurePanelObserver() {
    const host = getPanelHost();
    if (!host) {
      return;
    }

    if (state.panelObserverHost === host) {
      monitorPanelVisibility();
      return;
    }

    state.panelObserver?.disconnect?.();
    state.panelObserverHost = host;
    state.panelObserver = new MutationObserver(() => {
      monitorPanelVisibility();
    });

    state.panelObserver.observe(host, {
      attributes: true,
      attributeFilter: ["class", "style", "hidden", "aria-hidden"],
    });

    if (host.parentElement) {
      state.panelObserver.observe(host.parentElement, {
        attributes: true,
        attributeFilter: ["class", "style", "hidden", "aria-hidden"],
      });
    }

    monitorPanelVisibility();
  }

  function captureNativeUi() {
    if (state.nativeUi.dialogButtonType) {
      return true;
    }

    const dialogButton = document.querySelector(".DialogButton");
    const fiberKey = getReactPropertyKey(dialogButton, "__reactFiber");
    let current = fiberKey ? dialogButton[fiberKey] : null;

    while (current) {
      const renderSource =
        typeof current.elementType?.render?.toString === "function"
          ? current.elementType.render.toString()
          : "";

      if (renderSource.includes('"DialogButton"') && renderSource.includes('"Secondary"')) {
        state.nativeUi.dialogButtonType = current.elementType;
        return Boolean(state.nativeUi.dialogButtonType);
      }

      current = current.return;
    }

    return false;
  }

  function findRuntime(rootFiber = getQuickAccessRootFiber()) {
    if (!rootFiber) {
      return null;
    }

    let qamNode = null;
    const tabNodes = [];
    const forceHosts = [];
    let soundtrackTab = null;

    walkFiber(rootFiber, (node) => {
      const typeSource = typeof node.type?.toString === "function" ? node.type.toString() : "";
      const elementSource =
        typeof node.elementType?.toString === "function" ? node.elementType.toString() : "";

      if (
        !qamNode &&
        (typeSource.includes("bQuickAccessMenuVisible") ||
          elementSource.includes("bQuickAccessMenuVisible"))
      ) {
        qamNode = node;
      }

      const tabs = node.memoizedProps?.tabs || node.pendingProps?.tabs;
      if (!Array.isArray(tabs) || !tabs.some((tab) => tab?.key === soundtrackTabKey)) {
        return;
      }

      tabNodes.push(node);
      soundtrackTab ??= tabs.find((tab) => tab?.key === soundtrackTabKey) ?? null;

      let current = node;
      while (current) {
        if (current.stateNode && typeof current.stateNode.forceUpdate === "function") {
          forceHosts.push(current.stateNode);
        }
        current = current.return;
      }
    });

    if (!soundtrackTab) {
      return null;
    }

    return {
      qamNode,
      tabNodes,
      forceHosts: [...new Set(forceHosts)],
      soundtrackTab,
    };
  }

  function createElement(type, props = {}, key = null) {
    return {
      $$typeof: state.reactElementSymbol,
      type,
      key: key == null ? null : String(key),
      ref: null,
      props,
      _owner: null,
    };
  }

  function withChildren(props, ...children) {
    const filteredChildren = children.filter(
      (child) => child !== null && child !== undefined && child !== false,
    );

    if (!filteredChildren.length) {
      return props;
    }

    return {
      ...props,
      children: filteredChildren.length === 1 ? filteredChildren[0] : filteredChildren,
    };
  }

  function SteamLoaderIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
          style: { width: "20px", height: "20px" },
        },
        createElement("rect", {
          x: "5",
          y: "6",
          width: "26",
          height: "24",
          rx: "6",
          fill: "currentColor",
          opacity: "0.17",
        }),
        createElement("path", {
          d: "M11 12.5C11 11.6716 11.6716 11 12.5 11H16.5C17.3284 11 18 11.6716 18 12.5V16.5C18 17.3284 17.3284 18 16.5 18H12.5C11.6716 18 11 17.3284 11 16.5V12.5ZM20 12.5C20 11.6716 20.6716 11 21.5 11H23.5C24.3284 11 25 11.6716 25 12.5V14.5C25 15.3284 24.3284 16 23.5 16H21.5C20.6716 16 20 15.3284 20 14.5V12.5ZM11 21.5C11 20.6716 11.6716 20 12.5 20H14.5C15.3284 20 16 20.6716 16 21.5V23.5C16 24.3284 15.3284 25 14.5 25H12.5C11.6716 25 11 24.3284 11 23.5V21.5ZM18 21H24.5C25.3284 21 26 21.6716 26 22.5C26 23.3284 25.3284 24 24.5 24H18V21Z",
          fill: "currentColor",
        }),
      ),
    );
  }

  function AudioPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M12 14.5H17L23 10V26L17 21.5H12C10.8954 21.5 10 20.6046 10 19.5V16.5C10 15.3954 10.8954 14.5 12 14.5Z",
          fill: "currentColor",
        }),
        createElement("path", {
          d: "M26.5 13.5C28.1667 15 29 16.5 29 18C29 19.5 28.1667 21 26.5 22.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function DisplayPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("rect", {
          x: "7",
          y: "9",
          width: "22",
          height: "14",
          rx: "3",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
        createElement("path", {
          d: "M15 27H21",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M18 23V27",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function StoreSyncPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M11 12H24.5C25.3284 12 26 12.6716 26 13.5V17.5C26 18.3284 25.3284 19 24.5 19H13.5L10 22.5V13.5C10 12.6716 10.6716 12 11.5 12",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M19 24H11.5C10.6716 24 10 23.3284 10 22.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M16 15.5L13.5 18L16 20.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M20 15.5L22.5 18L20 20.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function ThemesPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M18 8.5C12.7533 8.5 8.5 12.7533 8.5 18C8.5 23.2467 12.7533 27.5 18 27.5C20.7614 27.5 23 25.2614 23 22.5C23 21.6716 23.6716 21 24.5 21H25C27.4853 21 29.5 18.9853 29.5 16.5C29.5 12.0817 24.9853 8.5 18 8.5Z",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("circle", {
          cx: "14",
          cy: "15",
          r: "1.5",
          fill: "currentColor",
        }),
        createElement("circle", {
          cx: "19",
          cy: "13.5",
          r: "1.5",
          fill: "currentColor",
        }),
        createElement("circle", {
          cx: "14.5",
          cy: "21",
          r: "1.5",
          fill: "currentColor",
        }),
      ),
    );
  }

  function HltbPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("circle", {
          cx: "18",
          cy: "18",
          r: "10.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
        }),
        createElement("path", {
          d: "M18 12.8V18L21.8 20.2",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M12.5 8.8L10.2 6.8",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M23.5 8.8L25.8 6.8",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function SettingsPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M18 11.5L19.6 9H22.4L23.1 12C23.7 12.2 24.2 12.5 24.7 12.8L27.6 11.9L29 14.3L26.8 16.3C26.9 16.9 26.9 17.4 26.8 18L29 20L27.6 22.4L24.7 21.5C24.2 21.8 23.7 22.1 23.1 22.3L22.4 25H19.6L18 23.5C17.4 23.5 16.8 23.5 16.2 23.5L14.6 25H11.8L11.1 22.3C10.5 22.1 10 21.8 9.5 21.5L6.6 22.4L5.2 20L7.4 18C7.3 17.4 7.3 16.9 7.4 16.3L5.2 14.3L6.6 11.9L9.5 12.8C10 12.5 10.5 12.2 11.1 12L11.8 9H14.6L16.2 11.5C16.8 11.4 17.4 11.4 18 11.5Z",
          stroke: "currentColor",
          strokeWidth: "2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("circle", {
          cx: "18",
          cy: "17.15",
          r: "3.2",
          stroke: "currentColor",
          strokeWidth: "2",
        }),
      ),
    );
  }

  function getPluginIconComponent(pluginId) {
    switch (pluginId) {
      case "audio":
        return AudioPluginIcon;
      case "display":
        return DisplayPluginIcon;
      case "hltb":
        return HltbPluginIcon;
      case "store-sync":
        return StoreSyncPluginIcon;
      case "themes":
        return ThemesPluginIcon;
      case "settings":
        return SettingsPluginIcon;
      default:
        return SteamLoaderIcon;
    }
  }

  function getRouteHeaderIcon(route = state.route) {
    return route?.pluginId ? getPluginIconComponent(route.pluginId) : SteamLoaderIcon;
  }

  function ChevronIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M14.25 9.75L22.5 18L14.25 26.25",
          stroke: "currentColor",
          strokeWidth: "3",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function BackIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M21.75 9.75L13.5 18L21.75 26.25",
          stroke: "currentColor",
          strokeWidth: "3",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function NativeDialogButton(content, onClick, options = {}) {
    const commonProps = {
      onClick,
      onOKButton: onClick,
      disabled: Boolean(options.disabled),
      className: options.className || "steamloader-dialog-button",
      children: content,
      ...(options.extraProps || {}),
    };

    if (state.nativeUi.dialogButtonType) {
      return createElement(state.nativeUi.dialogButtonType, {
        ...commonProps,
        focusable: true,
      });
    }

    return createElement("button", {
      type: "button",
      ...commonProps,
      className: "steamloader-fallback-button",
    });
  }

  function renderTrailingContent(slot) {
    if (typeof slot.switchValue === "boolean") {
      return createElement(
        "span",
        withChildren(
          { className: "steamloader-switch-wrap" },
          createElement(
            "span",
            withChildren(
              {
                className: `steamloader-switch${slot.switchValue ? " is-on" : ""}`,
              },
              createElement("span", {
                className: "steamloader-switch-thumb",
              }),
            ),
          ),
          slot.switchLabel
            ? createElement("span", {
                className: "steamloader-switch-label",
                children: slot.switchLabel,
              })
            : null,
        ),
      );
    }

    if (slot.badge) {
      return createElement("span", {
        className: "steamloader-badge",
        children: slot.badge,
      });
    }

    if (slot.trailing === "none") {
      return null;
    }

    return createElement(slot.trailing === "back" ? BackIcon : ChevronIcon, {});
  }

  function createInfoCard(card, index) {
    return createElement(
      "div",
      withChildren(
        {
          className: "steamloader-card",
          key: `card-${index}`,
        },
        createElement("div", {
          className: "steamloader-card-title",
          children: card.title,
        }),
        ...(Array.isArray(card.lines) ? card.lines : []).map((line, lineIndex) =>
          createElement("div", {
            className: "steamloader-card-line",
            key: `card-line-${index}-${lineIndex}`,
            children: line,
          }),
        ),
      ),
      `steamloader-card-${index}`,
    );
  }

  function createEditorCard(editor) {
    return createElement(
      "div",
      withChildren(
        {
          className: "steamloader-editor-card",
        },
        createElement("div", {
          className: "steamloader-editor-label",
          children: editor.label,
        }),
        editor.help
          ? createElement("div", {
              className: "steamloader-editor-help",
              children: editor.help,
            })
          : null,
        createElement("textarea", {
          key: editor.inputKey,
          className: "steamloader-editor-textarea",
          "data-custom-path-input": editor.isCustomPath ? "true" : undefined,
          defaultValue: editor.value || "",
          placeholder: editor.placeholder || "",
          rows: editor.rows || 3,
          spellCheck: false,
          autoCapitalize: "off",
          autoCorrect: "off",
          autoComplete: "off",
          onClick: (event) => {
            event.stopPropagation();
          },
          onInput: (event) => {
            editor.onInput?.(event.target.value);
          },
        }),
      ),
      "steamloader-editor",
    );
  }

  function createButtonSlot(slot, index, autoFocusIndex) {
    const backNavigation = getBackNavigation();
    const rowClassName = slot.leadingIcon
      ? slot.rowClassName
        ? `steamloader-row-shell steamloader-row-shell-with-icon ${slot.rowClassName}`
        : "steamloader-row-shell steamloader-row-shell-with-icon"
      : slot.rowClassName
        ? `steamloader-row-shell ${slot.rowClassName}`
      : "steamloader-row-shell";

    return NativeDialogButton(
      createElement(
        "div",
        withChildren(
          { className: rowClassName },
          slot.leadingIcon
            ? createElement(
                "div",
                withChildren(
                  { className: "steamloader-row-icon" },
                  createElement(slot.leadingIcon, {}),
                ),
              )
            : null,
          createElement(
            "div",
            withChildren(
              { className: "steamloader-row-main" },
              createElement("div", {
                className: "steamloader-row-title",
                children: slot.title,
              }),
              slot.copy
                ? createElement("div", {
                    className: "steamloader-row-copy",
                    children: slot.copy,
                  })
                : null,
            ),
          ),
          createElement(
            "div",
            withChildren(
              { className: "steamloader-row-trailing" },
              renderTrailingContent(slot),
            ),
          ),
        ),
      ),
      () => handleSlotClick(index),
      {
        disabled: slot.disabled,
        className: slot.buttonClassName || "steamloader-dialog-button",
        extraProps: {
          "data-slot-button": String(index),
          autoFocus: Number.isInteger(autoFocusIndex) && index === autoFocusIndex,
          onCancelButton: backNavigation
            ? () => {
                navigateBackFromRoute();
              }
            : undefined,
        },
      },
    );
  }

  function createDivider(key) {
    return createElement("div", {
      className: "steamloader-divider",
      key,
      "aria-hidden": "true",
    });
  }

  function clampVolume(value) {
    return Math.max(0, Math.min(100, Math.round(Number(value) || 0)));
  }

  function getVolumeValue() {
    return clampVolume(state.audio.volumeInfo?.volume ?? 0);
  }

  function renderPanelState() {
    install();
    invalidate();
  }

  function getFocusedSlotIndex() {
    const focusedNode = document.querySelector(".steamloader-panel [data-slot-button].gpfocus");
    const rawValue = focusedNode?.getAttribute?.("data-slot-button");
    const parsedValue = Number.parseInt(rawValue || "", 10);
    return Number.isInteger(parsedValue) ? parsedValue : null;
  }

  function rerenderStoreSyncPanel() {
    if (state.route.pluginId === "store-sync") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderDisplayPanel() {
    if (state.route.pluginId === "display") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderHltbPanel() {
    if (state.route.pluginId === "hltb") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderThemesPanel() {
    applyActiveThemeCss();

    if (state.route.pluginId === "themes") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function consumeVolumeActionAutoFocus() {
    const shouldFocus = state.audio.pendingVolumeActionAutoFocus;
    state.audio.pendingVolumeActionAutoFocus = false;
    return shouldFocus;
  }

  function rememberVolumeActionFocus(index) {
    state.audio.activeVolumeActionIndex = index;
  }

  function createVolumeActionButton(action, index) {
    return NativeDialogButton(
      createElement(
        "div",
        withChildren(
          { className: "steamloader-volume-action-shell" },
          createElement("div", {
            className: "steamloader-volume-action-title",
            children: action.title,
          }),
        ),
      ),
      action.onClick,
      {
        disabled: action.disabled,
        className: "steamloader-dialog-button steamloader-volume-action-button",
        extraProps: {
          autoFocus: action.autoFocus && state.audio.activeVolumeActionIndex === index,
          onGamepadFocus: () => {
            rememberVolumeActionFocus(index);
          },
          onCancelButton: () => {
            action.onCancel?.();
          },
          style: {
            width: "100%",
            minWidth: 0,
            padding: "8px 10px",
          },
        },
      },
    );
  }

  function createVolumePanel(panel) {
    const shouldAutoFocusAction = consumeVolumeActionAutoFocus();

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-volume-card" },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-volume-head" },
            createElement(
              "div",
              withChildren(
                { className: "steamloader-volume-copy-wrap" },
                createElement("div", {
                  className: "steamloader-volume-title",
                  children: panel.title,
                }),
                createElement("div", {
                  className: "steamloader-volume-copy",
                  children: panel.copy,
                }),
              ),
            ),
          ),
        ),
        createElement("div", {
          className: panel.error
            ? "steamloader-volume-hint steamloader-volume-hint-error"
            : "steamloader-volume-hint",
          children: panel.error || panel.hint,
        }),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-volume-actions" },
            ...panel.actions.map((action, index) =>
              createVolumeActionButton(
                {
                  ...action,
                  autoFocus: shouldAutoFocusAction,
                },
                index,
              ),
            ),
          ),
        ),
      ),
    );
  }

  function SteamLoaderPanelShell() {
    const model = buildScreenModel();
    const HeaderIcon = model.headerIcon || SteamLoaderIcon;
    state.slotActions = model.slots.map((slot) => slot.onClick);
    consumeResolvedFocus(state.route, model.autoFocusIndex);
    const slotChildren = model.slots.flatMap((slot, index) => {
      const children = [createButtonSlot(slot, index, model.autoFocusIndex)];
      if (Number.isInteger(model.dividerAfterIndex) && index === model.dividerAfterIndex) {
        children.push(createDivider(`divider-${index}`));
      }

      return children;
    });

    return createElement(
      "div",
      withChildren(
        {
          className: "steamloader-panel",
        },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-header" },
            createElement(
              "div",
              withChildren({ className: "steamloader-header-mark" }, createElement(HeaderIcon, {})),
            ),
            createElement(
              "div",
              withChildren(
                { className: "steamloader-title-wrap" },
                createElement("h1", {
                  className: "steamloader-title",
                  children: model.title,
                }),
                model.subtitle
                  ? createElement("div", {
                      className: "steamloader-subtitle",
                      children: model.subtitle,
                    })
                  : null,
              ),
            ),
          ),
        ),
        model.status
          ? createElement("div", {
              className: "steamloader-status",
              children: model.status,
            })
          : null,
        model.error
          ? createElement("div", {
              className: "steamloader-error",
              children: model.error,
            })
          : null,
        model.note
          ? createElement("div", {
              className: "steamloader-note",
              children: model.note,
            })
          : null,
        ...(Array.isArray(model.cards)
          ? model.cards.map((card, index) => createInfoCard(card, index))
          : []),
        model.editor ? createEditorCard(model.editor) : null,
        createElement(
          "div",
          withChildren(
            { className: "steamloader-stack" },
            ...slotChildren,
          ),
        ),
        model.volumePanel ? createVolumePanel(model.volumePanel) : null,
      ),
    );
  }

  function handleSlotClick(index) {
    const action = state.slotActions[index];
    if (typeof action === "function") {
      action();
    }
  }

  function buildVolumePanelModel() {
    const info = state.audio.volumeInfo;
    const deviceName = info?.deviceName || "Default playback device";

    let hint = "Use Left / Right. Press B to close.";
    if (state.audio.volumeLoading && !info) {
      hint = "Loading current system volume...";
    } else if (state.audio.volumeLoading) {
      hint = "Applying the new audio state...";
    } else if (!info) {
      hint = "The current default playback device will appear here.";
    }

    return {
      title: "System Volume",
      copy: info?.isMuted ? `${deviceName} - Muted` : deviceName,
      error: state.audio.volumeError,
      hint,
      actions: [
        {
          title: "-",
          disabled: state.audio.volumeLoading || !info,
          onCancel: () => {
            navigateBackFromRoute();
          },
          onClick: () => {
            rememberVolumeActionFocus(0);
            adjustVolume(-5);
          },
        },
        {
          title: "+",
          disabled: state.audio.volumeLoading || !info,
          onCancel: () => {
            navigateBackFromRoute();
          },
          onClick: () => {
            rememberVolumeActionFocus(1);
            adjustVolume(5);
          },
        },
        {
          title: info?.isMuted ? "Unmute" : "Mute",
          disabled: state.audio.volumeLoading || !info,
          onCancel: () => {
            navigateBackFromRoute();
          },
          onClick: () => {
            rememberVolumeActionFocus(2);
            toggleMute();
          },
        },
      ],
    };
  }

  function getStoreSyncSnapshot() {
    return state.storeSync.snapshot;
  }

  function getHltbSnapshot() {
    return state.hltb.snapshot;
  }

  function getGeneralSettingsSnapshot() {
    return state.generalSettings.snapshot;
  }

  function getStoreSyncStore(storeId) {
    const stores = getStoreSyncSnapshot()?.stores;
    return Array.isArray(stores) ? stores.find((store) => store.id === storeId) || null : null;
  }

  function isCustomLocationsRoute(route = state.route) {
    return (
      route?.screen === "page" &&
      route.pluginId === "store-sync" &&
      route.pageId === "store-custom-locations"
    );
  }

  function setCustomPathDraft(value, forceRemount = false) {
    state.storeSync.customPathDraft = typeof value === "string" ? value : "";
    if (forceRemount) {
      state.storeSync.customPathInputVersion += 1;
    }
  }

  function syncCustomPathDraftFromSnapshot(forceRemount = false) {
    const store = getStoreSyncStore("custom-locations");
    setCustomPathDraft(store?.pathValue || "", forceRemount);
  }

  function getCustomPathInputElement() {
    return document.querySelector(".steamloader-panel [data-custom-path-input='true']");
  }

  function readCustomPathInputValue() {
    const input = getCustomPathInputElement();
    return typeof input?.value === "string" ? input.value : state.storeSync.customPathDraft || "";
  }

  function isStoreSyncBusy() {
    return state.storeSync.loading || state.storeSync.saving || state.storeSync.syncing;
  }

  function isGeneralSettingsBusy() {
    return state.generalSettings.loading || state.generalSettings.saving;
  }

  function isHltbBusy() {
    return state.hltb.loading || state.hltb.saving;
  }

  function buildSteamProfileCard(profile) {
    if (!profile) {
      return {
        title: "Steam profile",
        lines: ["Steam profile details are not available yet."],
      };
    }

    const headline =
      profile.personaName && profile.accountName && profile.personaName !== profile.accountName
        ? `${profile.personaName} (${profile.accountName})`
        : profile.personaName || profile.accountName || profile.accountId;

    const lines = [];
    if (headline) {
      lines.push(headline);
    }
    if (profile.accountId) {
      lines.push(`Steam ID: ${profile.accountId}`);
    }

    return {
      title: "Steam profile",
      lines,
    };
  }

  function buildStoreSyncLastSyncCard(lastSync) {
    if (!lastSync) {
      return {
        title: "Last sync",
        lines: ["No sync has been run yet."],
      };
    }

    const completedAt = lastSync.completedAtUtc
      ? new Date(lastSync.completedAtUtc).toLocaleString()
      : "";

    return {
      title: lastSync.succeeded ? "Last sync" : "Last sync failed",
      lines: [
        lastSync.message,
        `Imported: ${lastSync.importedCount} - Replaced: ${lastSync.removedCount} - Skipped: ${lastSync.skippedCount}`,
        completedAt || "Completed just now",
      ],
    };
  }

  function buildStoreSyncStoreCard(store) {
    if (!store) {
      return null;
    }

    const lines = [
      store.description,
      `${store.enabled ? "Enabled" : "Disabled"} - ${store.statusText}`,
    ];

    if (store.pathValue) {
      lines.push(store.pathValue);
    } else if (store.detectedTitleCount) {
      lines.push(`${store.detectedTitleCount} title${store.detectedTitleCount === 1 ? "" : "s"} detected.`);
    } else {
      lines.push(store.detailText);
    }

    return {
      title: store.title,
      lines,
    };
  }

  function resolveStoreSyncStatusText() {
    if (state.storeSync.syncing) {
      return "Syncing enabled stores into Steam...";
    }

    if (state.storeSync.saving) {
      return "Saving Store Sync settings...";
    }

    if (state.storeSync.loading) {
      return "Loading Store Sync state...";
    }

    const lastSync = getStoreSyncSnapshot()?.lastSync;
    return lastSync?.message || "";
  }

  function resolveHltbStatusText() {
    if (state.hltb.saving) {
      return "Saving HLTB settings...";
    }

    if (state.hltb.loading) {
      return "Loading HLTB settings...";
    }

    return getHltbSnapshot()?.statusText || "";
  }

  function resolveGeneralSettingsStatusText() {
    if (state.generalSettings.saving) {
      return "Saving Steam Tools settings...";
    }

    if (state.generalSettings.loading) {
      return "Loading Steam Tools settings...";
    }

    return "";
  }

  function getThemesSnapshot() {
    return state.themes.snapshot;
  }

  function getThemeById(themeId) {
    if (!themeId) {
      return null;
    }

    const snapshot = getThemesSnapshot();
    const installedTheme = snapshot?.installedThemes?.find((theme) => theme.id === themeId);
    if (installedTheme) {
      return installedTheme;
    }

    return snapshot?.browseThemes?.find((theme) => theme.id === themeId) || null;
  }

  function getThemeProfilesState() {
    return getThemesSnapshot()?.profiles || null;
  }

  function getThemeProfileById(profileId) {
    if (!profileId) {
      return null;
    }

    const profiles = getThemeProfilesState();
    const installedProfile = profiles?.installedProfiles?.find((profile) => profile.id === profileId);
    if (installedProfile) {
      return installedProfile;
    }

    return profiles?.browseProfiles?.find((profile) => profile.id === profileId) || null;
  }

  function getThemeOptionById(themeId, optionId) {
    const theme = getThemeById(themeId);
    return theme?.options?.find((option) => option.id === optionId) || null;
  }

  function getThemeChoiceTitle(option, choiceId) {
    return option?.choices?.find((choice) => choice.id === choiceId)?.title || "";
  }

  function formatThemeOptionValue(option) {
    if (!option) {
      return "";
    }

    if (option.type === "toggle") {
      return option.boolValue ? "On" : "Off";
    }

    if (option.type === "choice") {
      return getThemeChoiceTitle(option, option.selectedChoiceId);
    }

    if (option.type === "range") {
      return `${option.numberValue ?? 0}${option.unit || ""}`;
    }

    return "";
  }

  function buildThemeSummaryCard(theme) {
    if (!theme) {
      return null;
    }

    const lines = [
      `${theme.author} - v${theme.version}`,
      theme.storeDescription || theme.description,
      `${theme.sourceLabel} - ${theme.downloadCount.toLocaleString()} downloads - ${theme.targets.join(", ")}`,
      theme.statusText,
    ];

    return {
      title: theme.title,
      lines,
    };
  }

  function buildThemeProfileSummaryCard(profile) {
    if (!profile) {
      return null;
    }

    const lines = [
      `${profile.author} - v${profile.version}`,
      profile.description,
      `${profile.sourceLabel} - ${profile.downloadCount.toLocaleString()} downloads`,
      profile.statusText,
      `${profile.themes.length} theme${profile.themes.length === 1 ? "" : "s"} in this profile`,
    ];

    return {
      title: profile.title,
      lines,
    };
  }

  function resolveThemesStatusText() {
    if (state.themes.saving) {
      return "Saving theme changes...";
    }

    if (state.themes.loading) {
      return "Loading themes...";
    }

    return getThemesSnapshot()?.statusText || "";
  }

  function buildOptimisticThemesStatusText(snapshot) {
    const installedThemes = Array.isArray(snapshot?.installedThemes) ? snapshot.installedThemes : [];
    const activeCount = installedThemes.filter((theme) => theme.enabled).length;
    return activeCount > 0
      ? `${installedThemes.length} installed - ${activeCount} active.`
      : `${installedThemes.length} installed - no active themes.`;
  }

  function ensureActiveThemeStyle() {
    let style = document.getElementById("steamloader-active-theme-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "steamloader-active-theme-style";
      document.head.append(style);
    }

    return style;
  }

  function applyActiveThemeCss() {
    const style = ensureActiveThemeStyle();
    style.textContent = getThemesSnapshot()?.activeCss || "";
  }

  async function loadStoreSyncState() {
    state.storeSync.loading = true;
    state.storeSync.error = "";
    rerenderStoreSyncPanel();

    try {
      const response = await fetch(`${apiBase}api/store-sync/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Store Sync could not be loaded (${response.status}).`);
      }

      state.storeSync.snapshot = payload && typeof payload === "object" ? payload : null;
      if (isCustomLocationsRoute(state.route) && !state.storeSync.customPathDraft) {
        syncCustomPathDraftFromSnapshot(true);
      }
    } catch (error) {
      state.storeSync.error = error instanceof Error ? error.message : String(error);
      state.storeSync.snapshot = null;
    } finally {
      state.storeSync.loading = false;
      rerenderStoreSyncPanel();
    }
  }

  async function loadGeneralSettingsState() {
    state.generalSettings.loading = true;
    state.generalSettings.error = "";
    renderPanelState();

    try {
      const response = await fetch(`${apiBase}api/settings/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Steam Tools settings could not be loaded (${response.status}).`);
      }

      state.generalSettings.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.generalSettings.error = error instanceof Error ? error.message : String(error);
      state.generalSettings.snapshot = null;
    } finally {
      state.generalSettings.loading = false;
      renderPanelState();
    }
  }

  async function loadHltbState() {
    state.hltb.loading = true;
    state.hltb.error = "";
    rerenderHltbPanel();

    try {
      const response = await fetch(`${apiBase}api/hltb/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `HLTB settings could not be loaded (${response.status}).`);
      }

      state.hltb.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.hltb.error = error instanceof Error ? error.message : String(error);
      state.hltb.snapshot = null;
    } finally {
      state.hltb.loading = false;
      rerenderHltbPanel();
    }
  }

  async function loadThemesState() {
    state.themes.loading = true;
    state.themes.error = "";
    rerenderThemesPanel();

    try {
      const response = await fetch(`${apiBase}api/themes/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Themes could not be loaded (${response.status}).`);
      }

      state.themes.snapshot = payload && typeof payload === "object" ? payload : null;
      applyActiveThemeCss();
    } catch (error) {
      state.themes.error = error instanceof Error ? error.message : String(error);
      state.themes.snapshot = null;
      applyActiveThemeCss();
    } finally {
      state.themes.loading = false;
      rerenderThemesPanel();
    }
  }

  async function sendStoreSyncRequest(path, bodyPayload = null, options = {}) {
    const requestStateKey = options.syncing ? "syncing" : "saving";
    let succeeded = false;
    state.storeSync[requestStateKey] = true;
    state.storeSync.error = "";
    rerenderStoreSyncPanel();

    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: bodyPayload === null ? "{}" : JSON.stringify(bodyPayload),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.storeSync.snapshot = payload && typeof payload === "object" ? payload : null;
      succeeded = true;
    } catch (error) {
      state.storeSync.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.storeSync[requestStateKey] = false;
      rerenderStoreSyncPanel();
    }

    return succeeded;
  }

  async function sendGeneralSettingsRequest(path, bodyPayload = null) {
    state.generalSettings.saving = true;
    state.generalSettings.error = "";
    renderPanelState();

    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: bodyPayload === null ? "{}" : JSON.stringify(bodyPayload),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.generalSettings.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.generalSettings.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.generalSettings.saving = false;
      renderPanelState();
    }
  }

  async function sendHltbRequest(path, bodyPayload = null) {
    state.hltb.saving = true;
    state.hltb.error = "";
    rerenderHltbPanel();

    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: bodyPayload === null ? "{}" : JSON.stringify(bodyPayload),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.hltb.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.hltb.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.hltb.saving = false;
      rerenderHltbPanel();
    }
  }

  async function sendThemesRequest(path, bodyPayload = null) {
    state.themes.saving = true;
    state.themes.error = "";
    rerenderThemesPanel();

    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: bodyPayload === null ? "{}" : JSON.stringify(bodyPayload),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.themes.snapshot = payload && typeof payload === "object" ? payload : null;
      applyActiveThemeCss();
    } catch (error) {
      state.themes.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.themes.saving = false;
      rerenderThemesPanel();
    }
  }

  async function toggleStoreSyncSetting(key) {
    const settings = getStoreSyncSnapshot()?.settings;
    const propertyMap = {
      "download-artwork": "downloadArtwork",
      "prefer-animated-artwork": "preferAnimatedArtwork",
      "backup-shortcuts": "backupShortcuts",
      "launch-big-picture-after-sync": "launchBigPictureAfterSync",
    };

    const propertyName = propertyMap[key];
    if (settings && propertyName && Object.prototype.hasOwnProperty.call(settings, propertyName)) {
      state.storeSync.snapshot = {
        ...state.storeSync.snapshot,
        settings: {
          ...settings,
          [propertyName]: !Boolean(settings[propertyName]),
        },
      };
      rerenderStoreSyncPanel();
    }

    await sendStoreSyncRequest("api/store-sync/settings/toggle", { key });
  }

  async function toggleRunOnWindowsSignIn() {
    const snapshot = getGeneralSettingsSnapshot();
    const enabled = !Boolean(snapshot?.runOnWindowsSignIn);
    if (snapshot) {
      state.generalSettings.snapshot = {
        ...snapshot,
        runOnWindowsSignIn: enabled,
      };
      renderPanelState();
    }

    await sendGeneralSettingsRequest("api/settings/autostart", { value: enabled });
  }

  async function toggleHltbSetting(key) {
    const settings = getHltbSnapshot()?.settings;
    const propertyMap = {
      enabled: "enabled",
      "show-main-story": "showMainStory",
      "show-main-plus": "showMainPlus",
      "show-completionist": "showCompletionist",
      "show-all-styles": "showAllStyles",
      "show-view-details": "showViewDetails",
    };

    const propertyName = propertyMap[key];
    if (settings && propertyName && Object.prototype.hasOwnProperty.call(settings, propertyName)) {
      state.hltb.snapshot = {
        ...state.hltb.snapshot,
        settings: {
          ...settings,
          [propertyName]: !Boolean(settings[propertyName]),
        },
      };
      renderPanelState();
    }

    await sendHltbRequest("api/hltb/settings/toggle", { key });
  }

  async function clearHltbCache() {
    await sendHltbRequest("api/hltb/cache/clear");
  }

  async function toggleThemesSetting(key) {
    const settings = getThemesSnapshot()?.settings;
    const propertyMap = {
      "theme-engine-enabled": "themeEngineEnabled",
      "show-community-themes": "showCommunityThemes",
      "single-theme-mode": "singleThemeMode",
      "auto-enable-on-install": "autoEnableOnInstall",
    };

    const propertyName = propertyMap[key];
    if (settings && propertyName && Object.prototype.hasOwnProperty.call(settings, propertyName)) {
      state.themes.snapshot = {
        ...state.themes.snapshot,
        settings: {
          ...settings,
          [propertyName]: !Boolean(settings[propertyName]),
        },
      };
      applyActiveThemeCss();
      rerenderThemesPanel();
    }

    await sendThemesRequest("api/themes/settings/toggle", { key });
  }

  async function refreshThemesCatalog() {
    await sendThemesRequest("api/themes/catalog/refresh");
  }

  async function installTheme(themeId) {
    await sendThemesRequest("api/themes/themes/install", {
      themeId,
      installed: true,
    });
  }

  async function uninstallTheme(themeId) {
    await sendThemesRequest("api/themes/themes/install", {
      themeId,
      installed: false,
    });
  }

  async function toggleThemeEnabled(themeId, enabled) {
    const snapshot = getThemesSnapshot();
    if (snapshot?.installedThemes) {
      state.themes.snapshot = {
        ...snapshot,
        installedThemes: snapshot.installedThemes.map((theme) =>
          theme.id === themeId
            ? {
                ...theme,
                enabled,
                statusText: enabled ? "Installed and active" : "Installed",
              }
            : snapshot.settings?.singleThemeMode && enabled
              ? {
                  ...theme,
                  enabled: false,
                  statusText: theme.installed ? "Installed" : theme.statusText,
                }
              : theme,
        ),
        browseThemes: Array.isArray(snapshot.browseThemes)
          ? snapshot.browseThemes.map((theme) =>
              theme.id === themeId
                ? {
                    ...theme,
                    enabled,
                    statusText: enabled ? "Installed and active" : "Installed",
                  }
                : snapshot.settings?.singleThemeMode && enabled
                  ? {
                      ...theme,
                      enabled: false,
                      statusText: theme.installed ? "Installed" : theme.statusText,
                    }
                  : theme,
            )
          : snapshot.browseThemes,
      };
      state.themes.snapshot.statusText = buildOptimisticThemesStatusText(state.themes.snapshot);
      applyActiveThemeCss();
      rerenderThemesPanel();
    }

    await sendThemesRequest("api/themes/themes/enabled", { themeId, enabled });
  }

  async function toggleThemeOption(themeId, optionId) {
    const option = getThemeOptionById(themeId, optionId);
    if (option?.type === "toggle") {
      const snapshot = getThemesSnapshot();
      const patchTheme = (theme) =>
        theme.id === themeId
          ? {
              ...theme,
              options: theme.options.map((entry) =>
                entry.id === optionId
                  ? {
                      ...entry,
                      boolValue: !Boolean(entry.boolValue),
                    }
                  : entry,
              ),
            }
          : theme;

      if (snapshot) {
        state.themes.snapshot = {
          ...snapshot,
          installedThemes: snapshot.installedThemes.map(patchTheme),
          browseThemes: snapshot.browseThemes.map(patchTheme),
        };
        state.themes.snapshot.statusText = buildOptimisticThemesStatusText(state.themes.snapshot);
        rerenderThemesPanel();
      }
    }

    await sendThemesRequest("api/themes/themes/option/toggle", { themeId, optionId });
  }

  async function setThemeChoice(themeId, optionId, choiceId) {
    await sendThemesRequest("api/themes/themes/option/choice", {
      themeId,
      optionId,
      choiceId,
    });
  }

  async function adjustThemeRange(themeId, optionId, delta) {
    await sendThemesRequest("api/themes/themes/option/range/adjust", {
      themeId,
      optionId,
      delta,
    });
  }

  async function resetThemeRange(themeId, optionId) {
    await sendThemesRequest("api/themes/themes/option/range/reset", {
      themeId,
      optionId,
    });
  }

  async function createThemeProfileFromCurrentSetup() {
    const title = (state.themes.profileDraft || "").trim();
    if (title.length < 3) {
      state.themes.error = "Enter a profile name with at least 3 characters before saving.";
      rerenderThemesPanel();
      return;
    }

    await sendThemesRequest("api/themes/profiles/create", {
      value: title,
    });

    if (!state.themes.error) {
      state.themes.profileDraft = "";
      state.themes.profileDraftInputVersion += 1;
      rerenderThemesPanel();
    }
  }

  async function installThemeProfile(profileId) {
    await sendThemesRequest("api/themes/profiles/install", {
      profileId,
    });
  }

  async function applyThemeProfile(profileId) {
    await sendThemesRequest("api/themes/profiles/apply", {
      profileId,
    });
  }

  async function updateThemeProfile(profileId) {
    await sendThemesRequest("api/themes/profiles/update", {
      profileId,
    });
  }

  async function removeThemeProfile(profileId) {
    await sendThemesRequest("api/themes/profiles/remove", {
      profileId,
    });
  }

  async function toggleStoreSyncStoreEnabled(storeId, enabled) {
    const snapshot = getStoreSyncSnapshot();
    if (snapshot?.stores) {
      state.storeSync.snapshot = {
        ...snapshot,
        stores: snapshot.stores.map((store) =>
          store.id === storeId
            ? {
                ...store,
                enabled,
              }
            : store,
        ),
      };
      rerenderStoreSyncPanel();
    }

    await sendStoreSyncRequest("api/store-sync/stores/enabled", {
      storeId,
      enabled,
    });
  }

  async function setCustomStorePath() {
    const value = readCustomPathInputValue().trim();
    if (!value) {
      state.storeSync.error = "Enter a folder path before saving the custom location.";
      rerenderStoreSyncPanel();
      return;
    }

    const succeeded = await sendStoreSyncRequest("api/store-sync/stores/custom-path", { value });
    if (succeeded) {
      syncCustomPathDraftFromSnapshot(true);
    }
  }

  async function clearCustomStorePath() {
    const input = getCustomPathInputElement();
    const typedValue = readCustomPathInputValue().trim();
    if (!getStoreSyncStore("custom-locations")?.pathValue && typedValue) {
      if (input) {
        input.value = "";
      }
      setCustomPathDraft("", true);
      state.storeSync.error = "";
      renderPanelState();
      return;
    }

    const succeeded = await sendStoreSyncRequest("api/store-sync/stores/custom-path/clear");
    if (succeeded) {
      syncCustomPathDraftFromSnapshot(true);
    }
  }

  async function runStoreSyncNow() {
    await sendStoreSyncRequest("api/store-sync/sync", {}, { syncing: true });
  }

  function buildScreenModel() {
    const makeSlot = (title, copy, onClick, options = {}) => ({
      kind: "button",
      title,
      copy: copy || "",
      onClick,
      disabled: Boolean(options.disabled),
      badge: options.badge || "",
      trailing: options.trailing || "chevron",
      switchValue: options.switchValue,
      switchLabel: options.switchLabel || "",
      leadingIcon: options.leadingIcon || null,
      buttonClassName: options.buttonClassName || "",
      rowClassName: options.rowClassName || "",
    });

    const makeToggleSlot = (title, copy, value, onClick, options = {}) =>
      makeSlot(title, copy, onClick, {
        ...options,
        trailing: "none",
        switchValue: value,
      });

    const defaultModel = {
      title: "Steam Tools",
      subtitle: "",
      status: "",
      error: "",
      note: "",
      headerIcon: getRouteHeaderIcon(state.route),
      autoFocusIndex: resolveAutoFocusIndex(state.route),
      dividerAfterIndex: null,
      volumePanel: null,
      cards: [],
      editor: null,
      slots: [],
    };

    if (state.route.screen === "plugin" && state.route.pluginId === "display") {
      return {
        ...defaultModel,
        title: "Display",
        subtitle: "Internal and external output",
        status: resolveDisplayStatusText(),
        error: state.display.error,
        note: "Steam Tools uses the Windows display switch, the same Windows feature behind Win + P.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        slots: [
          makeSlot(
            "External Display",
            "Keep the external screen active and switch away from the built-in display.",
            () => switchDisplayMode("external"),
            {
              disabled: isDisplayBusy(),
              trailing: "none",
            },
          ),
          makeSlot(
            "Internal Display",
            "Return to the built-in screen and disable the external display output.",
            () => switchDisplayMode("internal"),
            {
              disabled: isDisplayBusy(),
              trailing: "none",
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "hltb" &&
      state.route.pageId === "settings"
    ) {
      const settings = getHltbSnapshot()?.settings;

      return {
        ...defaultModel,
        title: "HLTB",
        subtitle: "Settings",
        status: resolveHltbStatusText(),
        error: state.hltb.error,
        note: "Show HowLongToBeat estimates directly on open Big Picture game pages. The results are cached locally for 12 hours.",
        cards: [
          {
            title: "Game Page Overlay",
            lines: [
              "Open any game in Big Picture and Steam Tools will place the HLTB values above the main play bar.",
              `${settings?.cacheEntryCount || 0} cached game${settings?.cacheEntryCount === 1 ? "" : "s"} ready.`,
            ],
          },
        ],
        slots: [
          makeToggleSlot(
            "Enable Game Page Stats",
            "Turn the HowLongToBeat panel on or off everywhere at once.",
            Boolean(settings?.enabled),
            () => toggleHltbSetting("enabled"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeToggleSlot(
            "Show Main Story",
            "Display the main story estimate on the game page.",
            Boolean(settings?.showMainStory),
            () => toggleHltbSetting("show-main-story"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeToggleSlot(
            "Show Main + Extras",
            "Display the main plus extras estimate on the game page.",
            Boolean(settings?.showMainPlus),
            () => toggleHltbSetting("show-main-plus"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeToggleSlot(
            "Show Completionist",
            "Display the completionist estimate on the game page.",
            Boolean(settings?.showCompletionist),
            () => toggleHltbSetting("show-completionist"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeToggleSlot(
            "Show All Styles",
            "Display the all styles estimate on the game page.",
            Boolean(settings?.showAllStyles),
            () => toggleHltbSetting("show-all-styles"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeToggleSlot(
            "Show View Details",
            "Keep a quick link to the full HowLongToBeat page for the current game.",
            Boolean(settings?.showViewDetails),
            () => toggleHltbSetting("show-view-details"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeSlot(
            "Clear Cached Results",
            "Drop the stored HLTB matches so Steam Tools fetches them again fresh.",
            () => clearHltbCache(),
            {
              disabled: isHltbBusy(),
              trailing: "none",
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "audio" &&
      state.route.pageId === "output-device-changer"
    ) {
      const slots = [
        makeSlot("Refresh", resolveAudioStatusText(), () => loadAudioDevices(), {
          disabled: state.audio.loading,
        }),
      ];

      for (const device of state.audio.devices) {
        slots.push(
          makeSlot(
            device.name,
            device.isDefault ? "Current Windows default device" : "Set as Windows default",
            () => setDefaultDevice(device.id),
            {
              disabled: state.audio.loading || device.isDefault,
              badge: device.isDefault ? "Default device" : "",
              trailing: device.isDefault ? "none" : "chevron",
            },
          ),
        );
      }

      return {
        ...defaultModel,
        title: "Audio",
        subtitle: "Output Device Changer",
        status: resolveAudioStatusText(),
        error: state.audio.error,
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        note:
          !state.audio.loading && !state.audio.devices.length
            ? "Active Windows playback devices will appear here."
            : "",
        slots,
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "audio" &&
      state.route.pageId === "system-volume"
    ) {
      return {
        ...defaultModel,
        title: "Audio",
        subtitle: "System Volume",
        autoFocusIndex: null,
        volumePanel: buildVolumePanelModel(),
      };
    }

    const storeSyncSnapshot = getStoreSyncSnapshot();
    const storeSyncStatus = resolveStoreSyncStatusText();

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      state.route.pageId === "sync-now"
    ) {
      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: "Sync Now",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "Steam Tools closes Steam, syncs your managed shortcuts, downloads artwork, and restarts Steam for you.",
        cards: [
          buildStoreSyncLastSyncCard(storeSyncSnapshot?.lastSync),
          buildSteamProfileCard(storeSyncSnapshot?.steamProfile),
        ],
        slots: [
          makeSlot(
            "Run Sync Now",
            "Scan enabled stores and refresh Steam shortcuts.",
            () => runStoreSyncNow(),
            {
              disabled: isStoreSyncBusy() || !storeSyncSnapshot?.steamProfile,
              trailing: "none",
            },
          ),
          makeSlot(
            "Refresh State",
            "Reload store availability, detected titles, and Steam profile details.",
            () => loadStoreSyncState(),
            {
              disabled: isStoreSyncBusy(),
              trailing: "none",
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      state.route.pageId === "settings"
    ) {
      const settings = storeSyncSnapshot?.settings;

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: "Settings",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "SteamGridDB artwork is built in. Sync Now handles the Steam restart automatically.",
        cards: [buildSteamProfileCard(storeSyncSnapshot?.steamProfile)],
        slots: [
          makeToggleSlot(
            "Download Artwork",
            "Download SteamGridDB artwork during sync.",
            Boolean(settings?.downloadArtwork),
            () => toggleStoreSyncSetting("download-artwork"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeToggleSlot(
            "Prefer Animated Artwork",
            "Prefer animated artwork when compatible assets exist.",
            Boolean(settings?.preferAnimatedArtwork),
            () => toggleStoreSyncSetting("prefer-animated-artwork"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeToggleSlot(
            "Back Up shortcuts.vdf",
            "Create a timestamped backup before each sync.",
            Boolean(settings?.backupShortcuts),
            () => toggleStoreSyncSetting("backup-shortcuts"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeToggleSlot(
            "Return to Big Picture After Sync",
            "Restart Steam in GamepadUI when the sync finishes.",
            Boolean(settings?.launchBigPictureAfterSync),
            () => toggleStoreSyncSetting("launch-big-picture-after-sync"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "settings" &&
      state.route.pageId === "general"
    ) {
      const settings = getGeneralSettingsSnapshot();

      return {
        ...defaultModel,
        title: "Settings",
        subtitle: "General",
        status: resolveGeneralSettingsStatusText(),
        error: state.generalSettings.error,
        note: "Steam Tools-wide options live here so plugin settings can stay focused on their own job.",
        slots: [
          makeToggleSlot(
            "Run on Windows Sign-In",
            "Start Steam Tools at Windows login, sync your launchers first, and then launch Steam for you.",
            Boolean(settings?.runOnWindowsSignIn),
            () => toggleRunOnWindowsSignIn(),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
        ],
      };
    }

    const themesSnapshot = getThemesSnapshot();
    const themesStatus = resolveThemesStatusText();

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      isThemesThemeOptionRoute(state.route)
    ) {
      const themeId = getThemeIdFromRoute(state.route);
      const optionId = getThemeOptionIdFromRoute(state.route);
      const theme = getThemeById(themeId);
      const option = getThemeOptionById(themeId, optionId);

      if (!theme || !option) {
        return {
          ...defaultModel,
          title: "Themes",
          subtitle: "Theme Option",
          status: themesStatus,
          error: state.themes.error,
          note: "The requested theme option could not be found.",
          slots: [
            makeSlot("Refresh Themes", "Reload the current theme catalog and state.", () => loadThemesState(), {
              disabled: state.themes.loading || state.themes.saving,
              trailing: "none",
            }),
          ],
        };
      }

      if (option.type === "choice") {
        return {
          ...defaultModel,
          title: theme.title,
          subtitle: option.title,
          status: themesStatus,
          error: state.themes.error,
          note: option.description,
          cards: [
            {
              title: "Current Value",
              lines: [formatThemeOptionValue(option)],
            },
          ],
          slots: option.choices.map((choice) =>
            makeSlot(
              choice.title,
              choice.id === option.selectedChoiceId ? "Current selection" : "Apply this value",
              () => setThemeChoice(theme.id, option.id, choice.id),
              {
                disabled: state.themes.loading || state.themes.saving || !theme.installed,
                badge: choice.id === option.selectedChoiceId ? "Selected" : "",
                trailing: choice.id === option.selectedChoiceId ? "none" : "chevron",
              },
            ),
          ),
        };
      }

      if (option.type === "range") {
        const stepLabel = `${option.step ?? 1}${option.unit || ""}`;
        return {
          ...defaultModel,
          title: theme.title,
          subtitle: option.title,
          status: themesStatus,
          error: state.themes.error,
          note: option.description,
          cards: [
            {
              title: "Current Value",
              lines: [
                `${formatThemeOptionValue(option)}`,
                `Range: ${option.min}${option.unit || ""} to ${option.max}${option.unit || ""}`,
                `Step: ${stepLabel}`,
              ],
            },
          ],
          slots: [
            makeSlot(
              `Decrease by ${stepLabel}`,
              "Move the setting down by one step.",
              () => adjustThemeRange(theme.id, option.id, -1),
              {
                disabled:
                  state.themes.loading ||
                  state.themes.saving ||
                  !theme.installed ||
                  option.numberValue <= option.min,
                trailing: "none",
              },
            ),
            makeSlot(
              `Increase by ${stepLabel}`,
              "Move the setting up by one step.",
              () => adjustThemeRange(theme.id, option.id, 1),
              {
                disabled:
                  state.themes.loading ||
                  state.themes.saving ||
                  !theme.installed ||
                  option.numberValue >= option.max,
                trailing: "none",
              },
            ),
            makeSlot(
              "Reset to Default",
              "Restore the original value from the theme manifest.",
              () => resetThemeRange(theme.id, option.id),
              {
                disabled: state.themes.loading || state.themes.saving || !theme.installed,
                trailing: "none",
              },
            ),
          ],
        };
      }
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      isThemesProfileRoute(state.route)
    ) {
      const profileId = getThemeProfileIdFromRoute(state.route);
      const profile = getThemeProfileById(profileId);

      if (!profile) {
        return {
          ...defaultModel,
          title: "Themes",
          subtitle: "Profile",
          status: themesStatus,
          error: state.themes.error,
          note: "The requested theme profile could not be found.",
          slots: [
            makeSlot("Refresh Catalog", "Reload theme and profile entries.", () => refreshThemesCatalog(), {
              disabled: state.themes.loading || state.themes.saving,
              trailing: "none",
            }),
          ],
        };
      }

      return {
        ...defaultModel,
        title: "Themes",
        subtitle: profile.title,
        status: themesStatus,
        error: state.themes.error,
        note: profile.description,
        cards: [buildThemeProfileSummaryCard(profile)],
        slots: profile.installed
          ? [
              makeSlot(
                "Apply Profile",
                "Install any missing themes from this profile and switch the current setup to match it.",
                () => applyThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                  badge: profile.selected ? "Selected" : "",
                  trailing: "none",
                },
              ),
              makeSlot(
                "Update From Current Setup",
                "Overwrite this installed profile with the themes and values you are using right now.",
                () => updateThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                  trailing: "none",
                },
              ),
              makeSlot(
                "Remove Profile",
                "Remove this profile from your local installed list.",
                () => removeThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                  trailing: "none",
                },
              ),
            ]
          : [
              makeSlot(
                "Download Profile",
                "Add this profile to your installed profile library.",
                () => installThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                  trailing: "none",
                },
              ),
            ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      isThemesThemeRoute(state.route)
    ) {
      const themeId = getThemeIdFromRoute(state.route);
      const theme = getThemeById(themeId);

      if (!theme) {
        return {
          ...defaultModel,
          title: "Themes",
          subtitle: "Theme",
          status: themesStatus,
          error: state.themes.error,
          note: "The requested theme could not be found in the current catalog.",
          slots: [
            makeSlot("Refresh Catalog", "Reload built-in and community theme entries.", () => refreshThemesCatalog(), {
              disabled: state.themes.loading || state.themes.saving,
              trailing: "none",
            }),
          ],
        };
      }

      const optionSlots = theme.installed
        ? theme.options.map((option, optionIndex) => {
            if (option.type === "toggle") {
              return makeToggleSlot(
                option.title,
                `${option.description} - ${formatThemeOptionValue(option)}`,
                Boolean(option.boolValue),
                () => toggleThemeOption(theme.id, option.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                },
              );
            }

            state.themes.detailOriginByThemeId[theme.id] ??= "store";
            return makeSlot(
              option.title,
              `${option.description} - ${formatThemeOptionValue(option)}`,
              () => {
                rememberCurrentRouteIndex(optionIndex + 1);
                setRoute({
                  screen: "page",
                  pluginId: "themes",
                  pageId: `theme-option-${theme.id}--${option.id}`,
                });
              },
              {
                disabled: state.themes.loading || state.themes.saving,
                badge: formatThemeOptionValue(option),
              },
            );
          })
        : [];

      return {
        ...defaultModel,
        title: "Themes",
        subtitle: theme.title,
        status: themesStatus,
        error: state.themes.error,
        note: theme.description,
        cards: [buildThemeSummaryCard(theme)],
        slots: theme.installed
          ? [
              makeToggleSlot(
                "Enabled",
                "Turn this theme on or off and reapply the current theme stack.",
                Boolean(theme.enabled),
                () => toggleThemeEnabled(theme.id, !Boolean(theme.enabled)),
                {
                  disabled: state.themes.loading || state.themes.saving,
                },
              ),
              ...optionSlots,
              makeSlot(
                "Uninstall Theme",
                "Remove this theme from the installed list but keep it available in the store.",
                () => uninstallTheme(theme.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                  trailing: "none",
                },
              ),
            ]
          : [
              makeSlot(
                "Install Theme",
                "Add this theme to the installed list so you can enable and tune it.",
                () => installTheme(theme.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                  trailing: "none",
                },
              ),
            ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      state.route.pageId === "store"
    ) {
      const browseThemes = Array.isArray(themesSnapshot?.browseThemes) ? themesSnapshot.browseThemes : [];

      return {
        ...defaultModel,
        title: "Themes",
        subtitle: "Store",
        status: themesStatus,
        error: state.themes.error,
        note: "Browse built-in and imported themes that can be installed into Steam Tools.",
        cards:
          themesSnapshot?.settings && !themesSnapshot.settings.showCommunityThemes
            ? [
                {
                  title: "Community Themes Hidden",
                  lines: ["Turn on Show Community Themes in Themes settings to see the full catalog."],
                },
              ]
            : [],
        slots: [
          ...browseThemes.map((theme, themeIndex) =>
            makeSlot(
              theme.title,
              `${theme.author} - ${theme.statusText} - ${theme.downloadCount.toLocaleString()} downloads`,
              () => {
                state.themes.detailOriginByThemeId[theme.id] = "store";
                rememberCurrentRouteIndex(themeIndex);
                setRoute({
                  screen: "page",
                  pluginId: "themes",
                  pageId: `theme-${theme.id}`,
                });
              },
              {
                disabled: state.themes.loading || state.themes.saving,
                badge: theme.enabled ? "Active" : theme.installed ? "Installed" : theme.sourceLabel,
              },
            ),
          ),
          makeSlot(
            "Refresh Catalog",
            "Reload the current theme catalog and installation state.",
            () => refreshThemesCatalog(),
            {
              disabled: state.themes.loading || state.themes.saving,
              trailing: "none",
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      state.route.pageId === "installed"
    ) {
      const installedThemes = Array.isArray(themesSnapshot?.installedThemes)
        ? themesSnapshot.installedThemes
        : [];

      return {
        ...defaultModel,
        title: "Themes",
        subtitle: "Installed",
        status: themesStatus,
        error: state.themes.error,
        note:
          installedThemes.length > 0
            ? "Open an installed theme to enable it, change switches, or tune range and choice options."
            : "No themes are installed yet. Use the Store to add your first theme.",
        slots: installedThemes.map((theme, themeIndex) =>
          makeSlot(
            theme.title,
            `${theme.author} - ${theme.enabled ? "Active" : "Installed"} - ${theme.options.length} setting${theme.options.length === 1 ? "" : "s"}`,
            () => {
              state.themes.detailOriginByThemeId[theme.id] = "installed";
              rememberCurrentRouteIndex(themeIndex);
              setRoute({
                screen: "page",
                pluginId: "themes",
                pageId: `theme-${theme.id}`,
              });
            },
            {
              disabled: state.themes.loading || state.themes.saving,
              badge: theme.enabled ? "Active" : "Installed",
            },
          ),
        ),
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      state.route.pageId === "profiles"
    ) {
      const profiles = getThemeProfilesState();
      const installedProfiles = Array.isArray(profiles?.installedProfiles) ? profiles.installedProfiles : [];
      const browseProfiles = Array.isArray(profiles?.browseProfiles) ? profiles.browseProfiles : [];
      const selectedProfile = getThemeProfileById(profiles?.selectedProfileId);

      return {
        ...defaultModel,
        title: "Themes",
        subtitle: "Profiles",
        status: themesStatus,
        error: state.themes.error,
        note:
          "Profiles capture a full theme setup so you can save your current stack, apply another one, or download a shared look later.",
        cards: selectedProfile
          ? [
              {
                title: "Selected Profile",
                lines: [
                  selectedProfile.title,
                  profiles?.currentSetupMatchesSelectedProfile
                    ? "Current setup matches this profile."
                    : "Current setup differs from the selected profile.",
                ],
              },
            ]
          : [
              {
                title: "No Selected Profile",
                lines: ["Create or download a profile to keep reusable theme setups ready."],
              },
            ],
        editor: {
          label: "New Profile Name",
          help: `Save the current installed theme stack as a reusable profile. Local themes are read from ${themesSnapshot?.localThemesFolder || "the local themes folder"}.`,
          value: state.themes.profileDraft,
          placeholder: "My Steam Deck Night Mode",
          inputKey: `theme-profile-name-${state.themes.profileDraftInputVersion}`,
          rows: 2,
          onInput: (value) => {
            state.themes.profileDraft = value;
          },
        },
        slots: [
          makeSlot(
            "Save Current Setup As Profile",
            "Capture the themes you have installed right now into a reusable profile.",
            () => createThemeProfileFromCurrentSetup(),
            {
              disabled: state.themes.loading || state.themes.saving,
              trailing: "none",
            },
          ),
          ...installedProfiles.map((profile, profileIndex) =>
            makeSlot(
              profile.title,
              `${profile.statusText} - ${profile.themes.length} theme${profile.themes.length === 1 ? "" : "s"}`,
              () => {
                state.themes.detailOriginByProfileId[profile.id] = "installed";
                rememberCurrentRouteIndex(profileIndex + 1);
                setRoute({
                  screen: "page",
                  pluginId: "themes",
                  pageId: `profile-${profile.id}`,
                });
              },
              {
                disabled: state.themes.loading || state.themes.saving,
                badge: profile.selected ? "Selected" : profile.matchesCurrentSetup ? "Current" : "Installed",
              },
            ),
          ),
          ...browseProfiles.map((profile, browseIndex) =>
            makeSlot(
              profile.title,
              `${profile.author} - ${profile.downloadCount.toLocaleString()} downloads - ${profile.themes.length} theme${profile.themes.length === 1 ? "" : "s"}`,
              () => {
                state.themes.detailOriginByProfileId[profile.id] = "browse";
                rememberCurrentRouteIndex(installedProfiles.length + 1 + browseIndex);
                setRoute({
                  screen: "page",
                  pluginId: "themes",
                  pageId: `profile-${profile.id}`,
                });
              },
              {
                disabled: state.themes.loading || state.themes.saving,
                badge: "Download",
              },
            ),
          ),
          makeSlot(
            "Refresh Catalog",
            "Reload local themes, theme profiles, and built-in catalog entries.",
            () => refreshThemesCatalog(),
            {
              disabled: state.themes.loading || state.themes.saving,
              trailing: "none",
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      state.route.pageId === "settings"
    ) {
      const settings = themesSnapshot?.settings;

      return {
        ...defaultModel,
        title: "Themes",
        subtitle: "Settings",
        status: themesStatus,
        error: state.themes.error,
        note: `These settings control how the theme framework behaves across the whole Steam Tools shell. Local themes are loaded from ${themesSnapshot?.localThemesFolder || "the local themes folder"}.`,
        slots: [
          makeToggleSlot(
            "Theme Engine Enabled",
            "Apply active theme CSS into the current Steam Tools surfaces.",
            Boolean(settings?.themeEngineEnabled),
            () => toggleThemesSetting("theme-engine-enabled"),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          makeToggleSlot(
            "Show Community Themes",
            "Include community-made catalog entries in the theme store.",
            Boolean(settings?.showCommunityThemes),
            () => toggleThemesSetting("show-community-themes"),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          makeToggleSlot(
            "Single Theme Mode",
            "Keep only one theme active at a time when you enable a new one.",
            Boolean(settings?.singleThemeMode),
            () => toggleThemesSetting("single-theme-mode"),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          makeToggleSlot(
            "Auto-Enable On Install",
            "Turn a freshly installed theme on as soon as it is added.",
            Boolean(settings?.autoEnableOnInstall),
            () => toggleThemesSetting("auto-enable-on-install"),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      state.route.pageId === "stores"
    ) {
      const stores = Array.isArray(storeSyncSnapshot?.stores) ? storeSyncSnapshot.stores : [];

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: "Stores",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "Each store keeps its own enable state and detection rules.",
        slots: stores.map((store, storeIndex) =>
          makeSlot(
            store.title,
            `${store.enabled ? "Enabled" : "Disabled"} - ${store.statusText} - ${store.detailText}`,
            () => {
              rememberCurrentRouteIndex(storeIndex);
              setRoute({
                screen: "page",
                pluginId: "store-sync",
                pageId: `store-${store.id}`,
              });
            },
            {
              disabled: isStoreSyncBusy(),
              badge: store.enabled ? "Enabled" : "Disabled",
            },
          ),
        ),
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      state.route.pageId?.startsWith("store-")
    ) {
      const storeId = state.route.pageId.replace(/^store-/, "");
      const store = getStoreSyncStore(storeId);
      const savedCustomPath = store?.pathValue || "";
      const customPathDraft = state.storeSync.customPathDraft || "";

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: store?.title || "Store",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note:
          storeId === "custom-locations"
            ? "Use a single folder that contains standalone games or library subfolders."
            : "",
        cards: store ? [buildStoreSyncStoreCard(store)] : [],
        editor:
          storeId === "custom-locations"
            ? {
                label: "Custom Folder",
                help: "Type a full folder path here, then save it to use it for future sync runs.",
                value: customPathDraft,
                placeholder: "D:\\Games\\Custom Library",
                isCustomPath: true,
                inputKey: `custom-path-${state.storeSync.customPathInputVersion}`,
                onInput: (value) => {
                  state.storeSync.customPathDraft = value;
                },
              }
            : null,
        slots: [
          makeToggleSlot(
            "Enabled",
            "Turn this source on or off for future sync runs.",
            Boolean(store?.enabled),
            () => toggleStoreSyncStoreEnabled(storeId, !Boolean(store?.enabled)),
            {
              disabled: isStoreSyncBusy() || !store,
            },
          ),
          ...(storeId === "custom-locations"
            ? [
                makeSlot(
                  "Save Custom Folder",
                  "Store the folder path above for the next sync run.",
                  () => setCustomStorePath(),
                  {
                    disabled: isStoreSyncBusy(),
                  },
                ),
                makeSlot(
                  "Clear Custom Folder",
                  "Remove the current custom scan folder.",
                  () => clearCustomStorePath(),
                  {
                    disabled: isStoreSyncBusy(),
                    trailing: "none",
                  },
                ),
              ]
            : []),
          makeSlot(
            "Refresh Store State",
            "Reload the store and validate the current detection status.",
            () => loadStoreSyncState(),
            {
              disabled: isStoreSyncBusy(),
              trailing: "none",
            },
          ),
        ],
      };
    }

    if (state.route.screen === "plugin") {
      const plugin = plugins.find((entry) => entry.id === state.route.pluginId);
      if (plugin) {
        return {
          ...defaultModel,
          title: plugin.title,
          subtitle: plugin.description,
          status: plugin.id === "store-sync" ? storeSyncStatus : "",
          error: plugin.id === "store-sync" ? state.storeSync.error : "",
          note:
            plugin.id === "store-sync"
              ? "Use Sync Now, Settings, and Stores to bring other launchers into Steam."
              : plugin.id === "hltb"
                ? "Use Settings to choose which HowLongToBeat values appear on the open game page."
              : plugin.id === "themes"
                ? "Use Store, Installed, Profiles, and Settings to build up a reusable Steam Tools theme library."
                : plugin.id === "settings"
                  ? "General Steam Tools options live here, separate from plugin-specific settings."
                  : "",
          autoFocusIndex: resolveAutoFocusIndex(state.route),
          slots: [
            ...plugin.pages.map((page, pageIndex) =>
              makeSlot(page.title, page.description, () => {
                rememberCurrentRouteIndex(pageIndex);
                setRoute({ screen: "page", pluginId: plugin.id, pageId: page.id });
              }),
            ),
          ],
        };
      }
    }

    return {
      ...defaultModel,
      dividerAfterIndex: 0,
      slots: plugins.map((plugin, pluginIndex) =>
        makeSlot(plugin.title, plugin.description, () => {
          rememberCurrentRouteIndex(pluginIndex);
          setRoute(
            plugin.id === "settings"
              ? { screen: "page", pluginId: "settings", pageId: "general" }
              : plugin.id === "hltb"
                ? { screen: "page", pluginId: "hltb", pageId: "settings" }
              : { screen: "plugin", pluginId: plugin.id, pageId: null },
          );
        }, plugin.id === "settings"
          ? {
              leadingIcon: getPluginIconComponent(plugin.id),
              buttonClassName: "steamloader-dialog-button steamloader-dialog-button-subtle",
              rowClassName: "steamloader-row-shell-subtle",
            }
          : {
              leadingIcon: getPluginIconComponent(plugin.id),
            }),
      ),
    };
  }

  function parseRoute(route) {
    if (route === "root") {
      return { screen: "root", pluginId: null, pageId: null };
    }

    if (route.startsWith("plugin:")) {
      return { screen: "plugin", pluginId: route.split(":")[1], pageId: null };
    }

    if (route.startsWith("page:")) {
      const [, pluginId, pageId] = route.split(":");
      return { screen: "page", pluginId, pageId };
    }

    return { screen: "root", pluginId: null, pageId: null };
  }

  function setRoute(route) {
    state.audio.pendingVolumeActionAutoFocus =
      route.screen === "page" &&
      route.pluginId === "audio" &&
      route.pageId === "system-volume";
    if (state.audio.pendingVolumeActionAutoFocus) {
      state.audio.activeVolumeActionIndex = 0;
    }
    state.route = route;
    state.renderRevision += 1;

    if (
      (route.screen === "plugin" ||
        (route.screen === "page" &&
          route.pluginId === "audio" &&
          route.pageId === "system-volume")) &&
      route.pluginId === "audio" &&
      !state.audio.volumeLoading &&
      !state.audio.volumeInfo &&
      !state.audio.volumeError
    ) {
      void loadAudioVolume();
    }

    if (
      route.screen === "page" &&
      route.pluginId === "audio" &&
      route.pageId === "output-device-changer" &&
      !state.audio.loading &&
      !state.audio.devices.length &&
      !state.audio.error
    ) {
      void loadAudioDevices();
    }

    if (
      route.pluginId === "store-sync" &&
      !state.storeSync.loading &&
      !state.storeSync.snapshot &&
      !state.storeSync.error
    ) {
      void loadStoreSyncState();
    }

    if (
      route.pluginId === "hltb" &&
      !state.hltb.loading &&
      !state.hltb.snapshot &&
      !state.hltb.error
    ) {
      void loadHltbState();
    }

    if (
      route.pluginId === "themes" &&
      !state.themes.loading &&
      !state.themes.snapshot &&
      !state.themes.error
    ) {
      void loadThemesState();
    }

    if (isCustomLocationsRoute(route)) {
      syncCustomPathDraftFromSnapshot(true);
    }

    if (
      route.pluginId === "settings" &&
      !state.generalSettings.loading &&
      !state.generalSettings.snapshot &&
      !state.generalSettings.error
    ) {
      void loadGeneralSettingsState();
    }

    refreshQuickAccessPanel();
  }

  async function loadAudioVolume() {
    state.audio.volumeLoading = true;
    state.audio.volumeError = "";
    renderPanelState();

    try {
      const response = await fetch(`${apiBase}api/audio/volume`, { cache: "no-store" });
      if (!response.ok) {
        throw new Error(`Volume could not be loaded (${response.status}).`);
      }

      const payload = await response.json();
      state.audio.volumeInfo = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.audio.volumeError = error instanceof Error ? error.message : String(error);
      state.audio.volumeInfo = null;
    } finally {
      state.audio.volumeLoading = false;
      renderPanelState();
    }
  }

  async function loadAudioDevices() {
    state.audio.loading = true;
    state.audio.error = "";
    state.renderRevision += 1;
    refreshQuickAccessPanel();

    try {
      const response = await fetch(`${apiBase}api/audio/devices`, { cache: "no-store" });
      if (!response.ok) {
        throw new Error(`Devices could not be loaded (${response.status}).`);
      }

      const payload = await response.json();
      state.audio.devices = Array.isArray(payload) ? payload : [];
    } catch (error) {
      state.audio.devices = [];
      state.audio.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.audio.loading = false;
      state.renderRevision += 1;
      refreshQuickAccessPanel();
    }
  }

  async function setDefaultDevice(deviceId) {
    state.audio.loading = true;
    state.audio.error = "";
    state.renderRevision += 1;
    refreshQuickAccessPanel();

    try {
      const response = await fetch(`${apiBase}api/audio/default`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ deviceId }),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.audio.devices = Array.isArray(payload) ? payload : [];
      await loadAudioVolume();
    } catch (error) {
      state.audio.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.audio.loading = false;
      state.renderRevision += 1;
      refreshQuickAccessPanel();
    }
  }

  async function performVolumeAction(path, bodyPayload = null) {
    state.audio.volumeLoading = true;
    state.audio.volumeError = "";
    renderPanelState();

    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: bodyPayload === null ? "{}" : JSON.stringify(bodyPayload),
      });

      const responsePayload = await response.json();
      if (!response.ok) {
        throw new Error(responsePayload.message || `The request failed (${response.status}).`);
      }

      state.audio.volumeInfo =
        responsePayload && typeof responsePayload === "object" ? responsePayload : null;
    } catch (error) {
      state.audio.volumeError = error instanceof Error ? error.message : String(error);
    } finally {
      state.audio.volumeLoading = false;
      renderPanelState();
    }
  }

  async function adjustVolume(delta) {
    const info = state.audio.volumeInfo;
    if (info) {
      state.audio.volumeInfo = {
        ...info,
        volume: clampVolume(info.volume + delta),
      };
      renderPanelState();
    }

    await performVolumeAction("api/audio/volume/adjust", { delta });
  }

  async function toggleMute() {
    const info = state.audio.volumeInfo;
    if (info) {
      state.audio.volumeInfo = {
        ...info,
        isMuted: !info.isMuted,
      };
      renderPanelState();
    }

    await performVolumeAction("api/audio/volume/toggle-mute");
  }

  function resolveAudioStatusText() {
    if (state.audio.loading) {
      return "Loading devices...";
    }

    if (!state.audio.devices.length) {
      return "No playback devices found.";
    }

    return "Choose the device that Windows should use as the default output.";
  }

  function isDisplayBusy() {
    return state.display.switching;
  }

  function resolveDisplayStatusText() {
    if (state.display.switching) {
      return state.display.status || "Switching display mode...";
    }

    return state.display.status || "Use the Windows display switch to keep either the internal or external display active.";
  }

  async function switchDisplayMode(mode) {
    const statusText =
      mode === "internal"
        ? "Switching to the internal display..."
        : "Switching to the external display...";

    state.display.switching = true;
    state.display.error = "";
    state.display.status = statusText;
    rerenderDisplayPanel();

    try {
      const response = await fetch(`${apiBase}api/display/${mode}`, {
        method: "POST",
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.display.status = payload?.message || statusText;
    } catch (error) {
      state.display.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.display.switching = false;
      rerenderDisplayPanel();
    }
  }

  function invalidate() {
    const forceHosts = [...state.forceHosts, ...getPanelForceHosts()];

    for (const host of [...new Set(forceHosts)]) {
      try {
        host.forceUpdate();
      } catch {
      }
    }
  }

  function refreshQuickAccessPanel() {
    install();
  }

  function isInjectedTabElement(element, type) {
    return Boolean(
      element &&
        typeof element === "object" &&
        element.$$typeof === state.reactElementSymbol &&
        element.type === type,
    );
  }

  function applyTabMutation(tab) {
    let changed = false;

    tab.strTitle = "Steam Tools";
    tab.title = null;

    if (!isInjectedTabElement(tab.tab, SteamLoaderIcon)) {
      tab.tab = createElement(SteamLoaderIcon, {});
      changed = true;
    }

    const panelRevision = tab.panel?.props?.__steamLoaderRevision;
    if (!isInjectedTabElement(tab.panel, SteamLoaderPanelShell) || panelRevision !== state.renderRevision) {
      tab.panel = createElement(
        SteamLoaderPanelShell,
        { __steamLoaderRevision: state.renderRevision },
        `steamloader-panel-${state.renderRevision}`,
      );
      changed = true;
    }

    tab.className = "";
    return changed;
  }

  function getTabCollections(node) {
    const collections = [];
    const candidates = [
      node.memoizedProps?.tabs,
      node.pendingProps?.tabs,
      node.alternate?.memoizedProps?.tabs,
      node.alternate?.pendingProps?.tabs,
    ];

    for (const tabs of candidates) {
      if (Array.isArray(tabs) && !collections.includes(tabs)) {
        collections.push(tabs);
      }
    }

    return collections;
  }

  function mutateExistingTabNodes(runtime) {
    let changed = false;

    for (const node of runtime.tabNodes) {
      for (const tabs of getTabCollections(node)) {
        const soundtrackTab = tabs.find((tab) => tab?.key === soundtrackTabKey);
        if (soundtrackTab) {
          changed = applyTabMutation(soundtrackTab) || changed;
        }
      }
    }

    return changed;
  }

  function mutateLiveTabs(rootFiber) {
    let changed = false;

    walkFiber(rootFiber, (node) => {
      for (const tabs of getTabCollections(node)) {
        const soundtrackTab = tabs.find((tab) => tab?.key === soundtrackTabKey);
        if (soundtrackTab) {
          changed = applyTabMutation(soundtrackTab) || changed;
        }
      }
    });

    return changed;
  }

  function install() {
    ensureStyles();
    applyActiveThemeCss();
    cleanupLegacyNodes();
    captureNativeUi();

    if (!state.themes.loading && !state.themes.snapshot && !state.themes.error) {
      void loadThemesState();
    }

    const rootFiber = getQuickAccessRootFiber();
    const runtime = findRuntime(rootFiber);
    if (!runtime) {
      return false;
    }

    state.reactElementSymbol = runtime.soundtrackTab.tab.$$typeof;
    state.qamNode = runtime.qamNode;
    state.forceHosts = runtime.forceHosts;
    const liveTabsChanged = mutateLiveTabs(rootFiber);
    ensurePanelObserver();

    const currentType = runtime.qamNode?.elementType?.type;
    const original =
      currentType?.__steamLoaderPopupOriginal && typeof currentType.__steamLoaderPopupOriginal === "function"
        ? currentType.__steamLoaderPopupOriginal
        : currentType;

    if (currentType?.__steamLoaderPopupWrapped === stateVersion) {
      const existingTabsChanged = mutateExistingTabNodes(runtime);
      if (liveTabsChanged || existingTabsChanged) {
        invalidate();
      }

      state.installed = true;
      return true;
    }

    if (!runtime.qamNode || typeof original !== "function") {
      const existingTabsChanged = mutateExistingTabNodes(runtime);
      if (liveTabsChanged || existingTabsChanged) {
        invalidate();
      }

      state.installed = true;
      return true;
    }

    const wrapped = function (...args) {
      const renderResult = original.apply(this, args);
      const tabsNode = findInElementTree(
        renderResult,
        (node) =>
          Array.isArray(node?.props?.tabs) &&
          node.props.tabs.some((tab) => tab?.key === soundtrackTabKey),
      );

      if (tabsNode) {
        const soundtrackTab = tabsNode.props.tabs.find((tab) => tab?.key === soundtrackTabKey);
        if (soundtrackTab) {
          applyTabMutation(soundtrackTab);
        }
      }

      return renderResult;
    };

    wrapped.__steamLoaderPopupWrapped = stateVersion;
    wrapped.__steamLoaderPopupOriginal = original;

    runtime.qamNode.elementType.type = wrapped;
    runtime.qamNode.type = wrapped;

    if (runtime.qamNode.alternate) {
      runtime.qamNode.alternate.type = wrapped;
    }

    mutateExistingTabNodes(runtime);
    invalidate();
    state.installed = true;
    return true;
  }

  return install() ? "injected" : "waiting";
})();
