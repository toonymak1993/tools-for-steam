(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const stateVersion = 67;
  const soundtrackTabKey = 7;
  const storeSyncPinnedTitlesStorageKey = "steamloader.storeSyncPinnedTitles.v1";

  if (window.__steamLoaderPopupTimer) {
    window.clearInterval(window.__steamLoaderPopupTimer);
    window.__steamLoaderPopupTimer = null;
  }

  if (window.__steamToolsProcessesPollTimer) {
    window.clearInterval(window.__steamToolsProcessesPollTimer);
    window.__steamToolsProcessesPollTimer = null;
  }

  if (window.__steamToolsAudioMixerPollTimer) {
    window.clearInterval(window.__steamToolsAudioMixerPollTimer);
    window.__steamToolsAudioMixerPollTimer = null;
  }

  if (window.__steamToolsStoreSyncPollTimer) {
    window.clearInterval(window.__steamToolsStoreSyncPollTimer);
    window.__steamToolsStoreSyncPollTimer = null;
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
            captureDevices: [],
            error: "",
            dashboardLoading: false,
            dashboardError: "",
            volumeLoading: false,
            volumeError: "",
            volumeInfo: null,
            captureVolumeLoading: false,
            captureVolumeError: "",
            captureVolumeInfo: null,
            activeVolumeActionIndex: 0,
            pendingVolumeActionAutoFocus: false,
            volumeCommitTimer: 0,
            captureVolumeCommitTimer: 0,
            volumeMutationSequence: 0,
            captureVolumeMutationSequence: 0,
            sliderEditActive: false,
            sliderHotkeysInstalled: false,
            dashboardHotkeysInstalled: false,
            sliderActivationTimer: 0,
            mixerLoading: false,
            mixerError: "",
            mixerSessions: [],
            mixerMutationSequenceById: {},
            mixerVolumeCommitTimersById: {},
          },
          display: {
            switching: false,
            modesLoading: false,
            modesSaving: false,
            error: "",
            status: "",
            modesSnapshot: null,
          },
          performance: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
            sliderEditActive: false,
            sliderHotkeysInstalled: false,
            sliderActivationTimer: 0,
            draftOverlayLevel: null,
            pendingSliderAutoFocus: false,
          },
          power: {
            actioning: false,
            error: "",
            status: "",
            confirmingPath: "",
          },
          processes: {
            loading: false,
            activating: false,
            error: "",
            snapshot: null,
          },
          appStart: {
            loading: false,
            catalogLoading: false,
            saving: false,
            error: "",
            snapshot: null,
            catalog: null,
          },
          hltb: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
          },
          artwork: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
            apiKeyDraft: "",
            apiKeyInputVersion: 0,
            steamPathDraft: "",
            steamPathInputVersion: 0,
          },
          storeSync: {
            loading: false,
            saving: false,
            syncing: false,
            error: "",
            snapshot: null,
            customPathDraft: "",
            customPathInputVersion: 0,
            additionalPathsDraftByStoreId: {},
            additionalPathsInputVersionByStoreId: {},
            titleOverrideDraftById: {},
            artworkTitleOverrideDraftById: {},
            titleOverrideInputVersionById: {},
            artworkTitleOverrideInputVersionById: {},
            excludedDraftById: {},
            artworkPreviewByTitleId: {},
            artworkPreviewLoadingByTitleId: {},
            pinnedTitleIds: readStoreSyncPinnedTitleIds(),
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
            splashWallpaperDraft: "",
            splashIconDraft: "",
            splashWallpaperInputVersion: 0,
            splashIconInputVersion: 0,
          },
          updates: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
          },
          developerDebug: {
            messages: {},
          },
          homeReorder: {
            active: false,
            movingPluginId: "",
            originalOrderIds: [],
            activationLocked: false,
            hotkeysInstalled: false,
            catchAllInstalled: false,
            previousCatchAllGamepadInput: null,
            catchAllButtonState: {},
          },
          autoSisir: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
            pathDraft: "",
            pathInputVersion: 0,
          },
          nativeUi: {
            dialogButtonType: null,
            componentCandidates: null,
            registrySnapshot: null,
            registryLoading: false,
            registryError: "",
            registryLastAttemptMs: 0,
            renderError: "",
          },
          slotActions: [],
          renderRevision: 1,
          panelObserver: null,
          panelObserverHost: null,
          panelVisible: false,
          pendingEntryAutoFocus: true,
          lastSelectedIndexByRoute: {},
          lastScrollTopByRoute: {},
          pendingFocusRouteKey: null,
          pendingFocusIndex: null,
          pendingScrollRouteKey: null,
          pendingScrollTop: null,
          pendingScrollAnimationFrame: 0,
        });

  const plugins = [
    {
      id: "settings",
      title: "Settings",
      description: "Global TFS behavior and startup",
      pages: [
        {
          id: "general",
          title: "General",
          description: "Startup behavior and global loader options",
        },
        {
          id: "updates",
          title: "Updates",
          description: "Stable or beta releases and in-app installs",
        },
        {
          id: "splashscreen-themes",
          title: "Splashscreen Themes",
          description: "Wallpaper, icon, text, and splash timing",
        },
      ],
    },
    {
      id: "processes",
      title: "Processes",
      description: "Jump between currently open app windows",
      pages: [],
    },
    {
      id: "app-start",
      title: "App Start",
      description: "Launch selected Windows apps",
      pages: [
        {
          id: "add-app",
          title: "Add App",
          description: "Choose an installed Start Menu app",
        },
      ],
    },
    {
      id: "store-sync",
      title: "Store Sync",
      description: "Bring other PC launchers into Steam",
      pages: [
        {
          id: "preview",
          title: "Preview",
          description: "Review detected games and every planned sync action before sync",
        },
        {
          id: "journal",
          title: "Journal",
          description: "See the latest sync, cleanup, repair, and watcher events",
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
      id: "auto-sisr",
      title: "Auto SISR",
      description: "Run SISR marker mode with selected games",
      defaultEnabled: false,
      pages: [
        {
          id: "settings",
          title: "Settings",
          description: "Marker path and automatic Game Pass behavior",
        },
        {
          id: "watched-games",
          title: "Watched Games",
          description: "Choose extra non-Steam games to watch",
        },
        {
          id: "log",
          title: "Log",
          description: "Inspect marker detection and start/stop decisions",
        },
      ],
    },
    {
      id: "artwork",
      title: "SteamGridDB",
      description: "Change game artwork from the context menu",
      pages: [
        {
          id: "settings",
          title: "Settings",
          description: "Context menu and SteamGridDB behavior",
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
        {
          id: "audio-mixer",
          title: "Audio Mixer",
          description: "Mix active app sessions without duplicates",
        },
      ],
    },
    {
      id: "display",
      title: "Display",
      description: "Screen output, resolution, and refresh rate",
      pages: [
        {
          id: "output-mode",
          title: "Output Mode",
          description: "Choose internal or external display output",
        },
        {
          id: "resolution",
          title: "Resolution",
          description: "Choose Full HD, 2K, or 4K when available",
        },
        {
          id: "refresh-rate",
          title: "Refresh Rate",
          description: "Choose 60Hz or 120Hz when available",
        },
      ],
    },
    {
      id: "performance",
      title: "Performance",
      description: "Built-in TFS FPS meter and Steam-style overlay controls",
      pages: [
        {
          id: "overlay",
          title: "TFS FPS Overlay",
          description: "Built-in overlay levels and live settings",
        },
      ],
    },
    {
      id: "power",
      title: "Power",
      description: "Steam, Windows, and recovery actions",
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
      id: "themes",
      title: "Themes",
      description: "Browse, install, and tune Tools for Steam themes",
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

  function getPluginSettings() {
    const entries = state.generalSettings.snapshot?.plugins;
    return Array.isArray(entries) ? entries : [];
  }

  function getPluginSettingsEntry(pluginId) {
    return getPluginSettings().find((entry) => entry.id === pluginId) || null;
  }

  function isPluginEnabled(pluginId) {
    if (!pluginId || pluginId === "settings") {
      return true;
    }

    const entry = getPluginSettingsEntry(pluginId);
    if (entry) {
      return entry.enabled !== false || entry.canDisable === false;
    }

    const definition = plugins.find((plugin) => plugin.id === pluginId);
    return definition ? definition.defaultEnabled !== false : true;
  }

  function getDefaultPluginOrderIds() {
    return plugins
      .filter((plugin) => plugin.id !== "settings")
      .map((plugin) => plugin.id);
  }

  function normalizePluginOrderIds(pluginIds) {
    const canonicalIds = getDefaultPluginOrderIds();
    const canonicalMap = new Map(canonicalIds.map((id) => [id.toLowerCase(), id]));
    const seen = new Set();
    const normalized = [];

    for (const candidate of Array.isArray(pluginIds) ? pluginIds : []) {
      const canonicalId = canonicalMap.get(String(candidate || "").toLowerCase());
      if (!canonicalId || seen.has(canonicalId)) {
        continue;
      }

      seen.add(canonicalId);
      normalized.push(canonicalId);
    }

    for (const pluginId of canonicalIds) {
      if (!seen.has(pluginId)) {
        seen.add(pluginId);
        normalized.push(pluginId);
      }
    }

    return normalized;
  }

  function getPersistedPluginOrderIds() {
    const orderedIds = state.generalSettings.snapshot?.plugins?.map((plugin) => plugin.id);
    return normalizePluginOrderIds(orderedIds);
  }

  function sortPluginsBySavedOrder(entries) {
    const orderIds = getPersistedPluginOrderIds();
    const orderMap = new Map(orderIds.map((id, index) => [id, index]));

    return [...entries].sort((left, right) => {
      if (left.id === "settings") {
        return right.id === "settings" ? 0 : -1;
      }

      if (right.id === "settings") {
        return 1;
      }

      const leftIndex = orderMap.has(left.id)
        ? orderMap.get(left.id)
        : orderIds.length + plugins.findIndex((plugin) => plugin.id === left.id);
      const rightIndex = orderMap.has(right.id)
        ? orderMap.get(right.id)
        : orderIds.length + plugins.findIndex((plugin) => plugin.id === right.id);
      return leftIndex - rightIndex;
    });
  }

  function getVisiblePlugins() {
    return sortPluginsBySavedOrder(plugins.filter((plugin) => isPluginEnabled(plugin.id)));
  }

  function getHomePlugins() {
    return getVisiblePlugins().filter((plugin) => plugin.id !== "settings");
  }

  function getVisiblePluginIndex(pluginId) {
    return getVisiblePlugins().findIndex((plugin) => plugin.id === pluginId);
  }

  function getHomePluginIndex(pluginId) {
    return getHomePlugins().findIndex((plugin) => plugin.id === pluginId);
  }

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
        justify-content: space-between;
        gap: 12px;
        margin-bottom: 16px;
      }

      .steamloader-header-main {
        min-width: 0;
        display: flex;
        align-items: flex-start;
        gap: 12px;
        flex: 1 1 auto;
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

      .steamloader-header-actions {
        margin-left: auto;
        display: inline-flex;
        align-items: center;
        gap: 8px;
        flex: 0 0 auto;
      }

      .steamloader-header-action-button {
        width: auto;
        min-width: 0;
        flex: 0 0 auto;
      }

      .steamloader-header-action-shell {
        width: 36px;
        height: 36px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        color: rgba(220, 228, 236, 0.92);
      }

      .steamloader-header-action-shell svg {
        width: 17px;
        height: 17px;
        display: block;
        flex: 0 0 auto;
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

      .steamloader-dialog-button-subtle {
        min-height: 34px !important;
        padding: 6px 10px !important;
        border-radius: 12px !important;
      }

      .steamloader-section-slot {
        margin-top: 4px;
        padding: 2px 4px 0;
      }

      .steamloader-section-slot-title {
        color: rgba(226, 233, 240, 0.92);
        font-size: clamp(12px, 1.36vw, 14px);
        line-height: 1.2;
        font-weight: 700;
        letter-spacing: 0.04em;
        text-transform: uppercase;
      }

      .steamloader-section-slot-copy {
        margin-top: 3px;
        color: rgba(143, 155, 167, 0.82);
        font-size: clamp(10px, 1.08vw, 12px);
        line-height: 1.3;
      }

      .steamloader-dialog-button-home {
        min-height: 44px !important;
        padding: 7px 10px !important;
        border-radius: 12px !important;
      }

      .steamloader-dialog-button-performance-summary {
        position: relative;
        overflow: hidden;
      }

      .steamloader-dialog-button-performance-summary::after {
        content: "";
        position: absolute;
        left: 16px;
        right: 16px;
        bottom: 0;
        height: 1px;
        background: linear-gradient(
          90deg,
          rgba(96, 162, 224, 0) 0%,
          rgba(96, 162, 224, 0.5) 18%,
          rgba(152, 198, 236, 0.78) 50%,
          rgba(96, 162, 224, 0.5) 82%,
          rgba(96, 162, 224, 0) 100%
        );
        pointer-events: none;
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

      .steamloader-row-icon .steamloader-app-start-icon {
        width: 100%;
        height: 100%;
        border-radius: inherit;
        object-fit: cover;
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

      .steamloader-row-shell-home {
        gap: 7px;
        padding: 0;
      }

      .steamloader-row-shell-home .steamloader-row-icon {
        width: 22px;
        height: 22px;
        border-radius: 7px;
        background: rgba(255, 255, 255, 0.04);
      }

      .steamloader-row-shell-home .steamloader-row-icon svg {
        width: 13px;
        height: 13px;
      }

      .steamloader-row-shell-home .steamloader-row-title {
        font-size: clamp(13px, 1.45vw, 16px);
        line-height: 1.08;
        font-weight: 600;
      }

      .steamloader-row-shell-home .steamloader-row-copy {
        display: none;
      }

      .steamloader-dialog-button-home.is-reordering {
        box-shadow: inset 0 0 0 1px rgba(90, 173, 255, 0.42);
        background: linear-gradient(180deg, rgba(57, 86, 116, 0.3) 0%, rgba(26, 37, 49, 0.72) 100%);
      }

      .steamloader-dialog-button-home.is-reordering .steamloader-row-icon {
        background: rgba(103, 181, 255, 0.18);
        color: rgba(245, 250, 255, 0.98);
      }

      .steamloader-dialog-button-home.is-reordering.gpfocus {
        animation: steamloader-home-reorder-pulse 1.05s ease-in-out infinite;
      }

      @keyframes steamloader-home-reorder-pulse {
        0% {
          box-shadow: inset 0 0 0 1px rgba(90, 173, 255, 0.38), 0 0 0 0 rgba(90, 173, 255, 0.04);
        }

        50% {
          box-shadow: inset 0 0 0 1px rgba(143, 210, 255, 0.72), 0 0 0 4px rgba(90, 173, 255, 0.14);
        }

        100% {
          box-shadow: inset 0 0 0 1px rgba(90, 173, 255, 0.38), 0 0 0 0 rgba(90, 173, 255, 0.04);
        }
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

      .steamloader-card-image-shell {
        margin-top: 9px;
        border-radius: 12px;
        overflow: hidden;
        background: rgba(8, 13, 19, 0.72);
        aspect-ratio: 2.12 / 1;
      }

      .steamloader-card-image {
        width: 100%;
        height: 100%;
        display: block;
        object-fit: cover;
      }

      .steamloader-switch-wrap {
        display: inline-flex;
        align-items: center;
        gap: 8px;
      }

      .steamtools-native-toggle-wrap {
        min-width: 42px;
        justify-content: flex-end;
      }

      .steamtools-native-toggle {
        position: relative !important;
        display: block !important;
        flex: 0 0 auto;
        width: 40px !important;
        height: 22px !important;
        min-width: 40px;
        min-height: 22px;
        border-radius: 999px;
        overflow: hidden;
      }

      .steamtools-native-toggle > span {
        box-sizing: border-box;
      }

      .steamtools-native-toggle > span:first-child {
        position: absolute !important;
        inset: 0 !important;
        width: 100% !important;
        height: 100% !important;
        border-radius: 999px !important;
        background: rgba(255, 255, 255, 0.15);
      }

      .steamtools-native-toggle > span:last-child {
        position: absolute !important;
        top: 2px !important;
        left: 2px !important;
        width: 18px !important;
        height: 18px !important;
        border-radius: 50% !important;
        background: #f1f5f8;
        transform: translateX(0);
        transition: transform 120ms ease, background 120ms ease;
      }

      .steamtools-native-toggle.is-on > span:first-child {
        background: rgba(57, 158, 255, 0.86);
      }

      .steamtools-native-toggle.is-on > span:last-child {
        transform: translateX(18px);
      }

      .steamtools-native-toggle.is-disabled {
        opacity: 0.46;
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

      .steamloader-volume-slider-wrap {
        margin-top: 10px;
      }

      .steamloader-volume-slider-wrap > * {
        min-width: 0;
      }

      .steamloader-volume-actions {
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin-top: 10px;
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

      .steamloader-volume-slider-fallback-button {
        width: 100%;
        min-width: 0;
        overflow: hidden;
        scroll-margin-top: 108px;
        display: block;
        box-sizing: border-box;
        background: rgba(255, 255, 255, 0.05) !important;
        border-radius: 22px !important;
      }

      .steamloader-volume-slider-fallback-button > * {
        width: 100%;
        min-width: 0;
        box-sizing: border-box;
      }

      .steamloader-performance-slider-button {
        width: 100%;
        min-width: 0;
        overflow: hidden;
        scroll-margin-top: 108px;
        display: block;
        margin: 0 !important;
        padding: 0 !important;
        box-sizing: border-box;
        background: transparent !important;
        box-shadow: none !important;
      }

      .steamloader-performance-slider-button > * {
        width: 100%;
        min-width: 0;
        box-sizing: border-box;
      }

      .steamloader-performance-slider-button .steamloader-volume-card {
        margin-top: 0;
        padding-left: 13px;
        padding-right: 13px;
      }

      .steamloader-volume-slider-fallback-button.is-editing {
        box-shadow: 0 0 0 1px rgba(84, 180, 255, 0.45), 0 10px 28px rgba(16, 30, 46, 0.32);
      }

      .steamloader-volume-slider-fallback-shell {
        display: flex;
        flex-direction: column;
        gap: 10px;
        width: 100%;
      }

      .steamloader-audio-dashboard {
        margin-top: 10px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }

      .steamloader-audio-card {
        padding: 14px 14px 13px;
        border-radius: 22px;
        background: rgba(255, 255, 255, 0.05);
      }

      .steamloader-audio-card-title {
        color: rgba(236, 242, 248, 0.96);
        font-size: clamp(18px, 2vw, 24px);
        line-height: 1.12;
        font-weight: 700;
      }

      .steamloader-audio-card-copy {
        margin-top: 5px;
        color: rgba(154, 166, 178, 0.9);
        font-size: clamp(11px, 1.3vw, 14px);
        line-height: 1.45;
      }

      .steamloader-audio-quick-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 10px;
      }

      .steamloader-audio-quick-button {
        width: 100%;
        min-width: 0;
        min-height: 56px;
        padding: 0 !important;
        background: rgba(255, 255, 255, 0.05) !important;
        border-radius: 18px !important;
        box-sizing: border-box;
      }

      .steamloader-audio-quick-shell {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 10px;
        min-height: 56px;
        padding: 0 12px;
      }

      .steamloader-audio-quick-icon {
        width: 18px;
        height: 18px;
        color: rgba(232, 239, 246, 0.95);
      }

      .steamloader-audio-quick-title {
        color: rgba(236, 242, 248, 0.96);
        font-size: clamp(13px, 1.55vw, 18px);
        line-height: 1.1;
        font-weight: 700;
      }

      .steamloader-audio-quick-button.is-active {
        background: linear-gradient(180deg, rgba(245, 248, 252, 0.92) 0%, rgba(212, 219, 228, 0.9) 100%) !important;
      }

      .steamloader-audio-quick-button.is-active .steamloader-audio-quick-title,
      .steamloader-audio-quick-button.is-active .steamloader-audio-quick-icon {
        color: #111824;
      }

      .steamloader-audio-slider-stack,
      .steamloader-audio-selector-stack,
      .steamloader-audio-mixer-stack {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }

      .steamloader-audio-slider-stack {
        margin-top: 12px;
      }

      .steamloader-audio-slider-button,
      .steamloader-audio-selector-button,
      .steamloader-audio-mixer-button {
        width: 100%;
        min-width: 0;
        padding: 0 !important;
        background: transparent !important;
        box-shadow: none !important;
      }

      .steamloader-audio-slider-card,
      .steamloader-audio-selector-card,
      .steamloader-audio-mixer-card {
        width: 100%;
        box-sizing: border-box;
        padding: 12px 12px 10px;
        border-radius: 18px;
        background: rgba(255, 255, 255, 0.05);
      }

      .steamloader-audio-selector-card {
        padding: 11px 12px;
      }

      .steamloader-audio-mixer-card {
        padding: 12px;
      }

      .steamloader-audio-slider-copy,
      .steamloader-audio-mixer-copy {
        margin-top: 8px;
        color: rgba(150, 162, 174, 0.88);
        font-size: clamp(11px, 1.22vw, 13px);
        line-height: 1.35;
      }

      .steamloader-audio-selector-label {
        color: rgba(174, 186, 198, 0.86);
        font-size: clamp(11px, 1.2vw, 13px);
        line-height: 1;
        text-transform: uppercase;
        letter-spacing: 0.04em;
      }

      .steamloader-audio-selector-value-row {
        margin-top: 8px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
      }

      .steamloader-audio-selector-value {
        color: rgba(236, 242, 248, 0.95);
        font-size: clamp(13px, 1.55vw, 17px);
        line-height: 1.25;
        font-weight: 600;
      }

      .steamloader-audio-selector-icon {
        width: 14px;
        height: 14px;
        color: rgba(190, 201, 212, 0.86);
      }

      .steamloader-audio-selector-icon svg {
        width: 14px;
        height: 14px;
        transform: rotate(90deg);
      }

      .steamloader-audio-selector-copy {
        margin-top: 5px;
        color: rgba(143, 156, 168, 0.82);
        font-size: clamp(10px, 1.14vw, 12px);
        line-height: 1.35;
      }

      .steamloader-audio-mixer-header {
        margin-bottom: 10px;
      }

      .steamloader-audio-empty-state {
        padding: 8px 2px 2px;
        color: rgba(154, 166, 178, 0.9);
        font-size: clamp(11px, 1.28vw, 14px);
        line-height: 1.45;
      }

      .steamloader-dialog-button.gpfocus .steamloader-audio-slider-card,
      .steamloader-dialog-button.gpfocus .steamloader-audio-selector-card,
      .steamloader-dialog-button.gpfocus .steamloader-audio-mixer-card {
        background: rgba(255, 255, 255, 0.08);
      }

      .steamloader-dialog-button.gpfocus .steamloader-audio-selector-value,
      .steamloader-dialog-button.gpfocus .steamloader-audio-slider-copy,
      .steamloader-dialog-button.gpfocus .steamloader-audio-mixer-copy,
      .steamloader-dialog-button.gpfocus .steamloader-audio-selector-copy,
      .steamloader-dialog-button.gpfocus .steamloader-audio-selector-label {
        color: rgba(232, 239, 246, 0.94);
      }

      .steamloader-volume-slider-fallback-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
      }

      .steamloader-volume-slider-label {
        color: rgba(214, 222, 231, 0.92);
        font-size: clamp(12px, 1.45vw, 15px);
        line-height: 1.2;
        font-weight: 600;
      }

      .steamloader-volume-slider-value {
        color: rgba(187, 197, 208, 0.86);
        font-size: clamp(12px, 1.4vw, 14px);
        line-height: 1;
        font-weight: 600;
      }

      .steamloader-volume-slider-track-shell {
        position: relative;
        height: 20px;
        display: flex;
        align-items: center;
      }

      .steamloader-volume-slider-track {
        position: absolute;
        inset: 6px 0;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.12);
        transition: background 140ms ease;
      }

      .steamloader-volume-slider-fill {
        position: absolute;
        left: 0;
        top: 6px;
        bottom: 6px;
        border-radius: 999px;
        background: linear-gradient(90deg, rgba(72, 178, 255, 0.96) 0%, rgba(234, 240, 246, 0.92) 100%);
        transition: width 110ms ease-out, background 140ms ease;
      }

      .steamloader-volume-slider-thumb {
        position: absolute;
        top: 50%;
        width: 18px;
        height: 18px;
        border-radius: 50%;
        background: #eef4fa;
        box-shadow: 0 4px 16px rgba(0, 0, 0, 0.28);
        transform: translate(-50%, -50%);
        transition: left 110ms ease-out, box-shadow 160ms ease, transform 160ms ease, background 140ms ease;
      }

      .steamloader-volume-slider-notch {
        position: absolute;
        top: 50%;
        width: 2px;
        height: 12px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.2);
        transform: translate(-50%, -50%);
      }

      @media (max-width: 430px) {
        .steamloader-volume-card {
          padding: 10px 10px 9px;
        }
      }

      .steamloader-dialog-button.gpfocus .steamloader-row-title,
      .steamloader-dialog-button.gpfocus .steamloader-row-copy,
      .steamloader-dialog-button.gpfocus .steamloader-row-trailing {
        color: #293544;
      }

      .steamloader-dialog-button.gpfocus .steamloader-header-action-shell {
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

      .steamloader-dialog-button.gpfocus .steamtools-native-toggle > span:first-child {
        background: rgba(41, 53, 68, 0.24);
      }

      .steamloader-dialog-button.gpfocus .steamtools-native-toggle.is-on > span:first-child {
        background: rgba(41, 53, 68, 0.82);
      }

      .steamloader-dialog-button.gpfocus .steamtools-native-toggle > span:last-child {
        background: #f5f7f9;
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-action-title {
        color: #293544;
      }

      .steamloader-dialog-button.gpfocus.steamloader-volume-slider-fallback-button {
        background: rgba(255, 255, 255, 0.05) !important;
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-title,
      .steamloader-dialog-button.gpfocus .steamloader-volume-slider-label {
        color: rgba(214, 222, 231, 0.92);
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-copy,
      .steamloader-dialog-button.gpfocus .steamloader-volume-hint {
        color: rgba(154, 166, 178, 0.9);
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-slider-value {
        color: rgba(187, 197, 208, 0.86);
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-slider-track {
        background: rgba(255, 255, 255, 0.12);
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-slider-fill {
        background: linear-gradient(90deg, rgba(72, 178, 255, 0.96) 0%, rgba(234, 240, 246, 0.92) 100%);
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-slider-thumb {
        background: #f8fbff;
        box-shadow: 0 0 0 6px rgba(88, 176, 255, 0.18), 0 8px 22px rgba(10, 18, 28, 0.24);
      }

      .steamloader-dialog-button.gpfocus .steamloader-volume-slider-notch {
        background: rgba(255, 255, 255, 0.2);
      }

      .steamloader-volume-slider-fallback-button.is-editing .steamloader-volume-slider-track {
        background: rgba(255, 255, 255, 0.12);
      }

      .steamloader-volume-slider-fallback-button.is-editing .steamloader-volume-slider-thumb {
        box-shadow: 0 0 0 8px rgba(88, 176, 255, 0.24), 0 10px 24px rgba(10, 18, 28, 0.28);
      }

      .steamloader-volume-slider-fallback-button.is-activating .steamloader-volume-slider-thumb {
        animation: steamloader-volume-slider-thumb-engage 320ms cubic-bezier(0.18, 0.88, 0.24, 1.18) 1;
      }

      @keyframes steamloader-volume-slider-thumb-engage {
        0% {
          transform: translate(-50%, -50%) scale(1);
          box-shadow: 0 0 0 0 rgba(82, 170, 255, 0), 0 6px 20px rgba(0, 0, 0, 0.28);
        }

        45% {
          transform: translate(-50%, -50%) scale(1.22);
          box-shadow: 0 0 0 9px rgba(82, 170, 255, 0.18), 0 8px 24px rgba(0, 0, 0, 0.34);
        }

        100% {
          transform: translate(-50%, -50%) scale(1);
          box-shadow: 0 0 0 4px rgba(82, 170, 255, 0.14), 0 6px 20px rgba(0, 0, 0, 0.32);
        }
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

      .steamloader-footer-legend {
        margin-top: auto;
        padding-top: 10px;
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
      }

      .steamloader-footer-legend-item {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        color: rgba(198, 208, 218, 0.92);
        font-size: clamp(10px, 1.2vw, 12px);
        line-height: 1;
        font-weight: 700;
        letter-spacing: 0.03em;
        text-transform: uppercase;
      }

      .steamloader-footer-legend-item.is-active {
        color: rgba(240, 247, 255, 0.98);
      }

      .steamloader-footer-legend-button {
        min-width: 20px;
        height: 20px;
        padding: 0 6px;
        border-radius: 999px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: rgba(240, 246, 255, 0.92);
        color: #203141;
        font-size: 11px;
        font-weight: 900;
      }

      .steamloader-footer-legend-item.is-active .steamloader-footer-legend-button {
        background: #ffffff;
        box-shadow: 0 0 0 3px rgba(92, 173, 255, 0.15);
      }

      .steamloader-footer-legend-label {
        opacity: 0.94;
      }

      .steamloader-panel-home {
        padding: 8px 10px 14px;
      }

      .steamloader-panel-home .steamloader-header {
        align-items: center;
        margin-bottom: 8px;
        gap: 8px;
      }

      .steamloader-panel-home .steamloader-header-main {
        align-items: center;
        gap: 8px;
      }

      .steamloader-panel-home .steamloader-header-mark {
        width: 28px;
        height: 28px;
        border-radius: 9px;
      }

      .steamloader-panel-home .steamloader-header-mark svg {
        width: 14px;
        height: 14px;
      }

      .steamloader-panel-home .steamloader-header-action-shell {
        width: 30px;
        height: 30px;
      }

      .steamloader-panel-home .steamloader-header-actions {
        gap: 4px;
      }

      .steamloader-panel-home .steamloader-title {
        font-size: clamp(16px, 1.9vw, 24px);
        line-height: 1.08;
      }

      .steamloader-panel-home .steamloader-stack {
        gap: 4px;
      }

      .steamloader-panel-home .steamloader-status,
      .steamloader-panel-home .steamloader-note {
        margin-bottom: 6px;
        padding: 8px 10px;
        border-radius: 13px;
      }

      .steamloader-panel-home .steamloader-footer-legend {
        padding-top: 8px;
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
    if (window.STFrontendLib?.getReactPropertyKey) {
      return window.STFrontendLib.getReactPropertyKey(element, prefix);
    }

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
    if (window.STFrontendLib?.getReactFiber) {
      return window.STFrontendLib.getReactFiber(element);
    }

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

  function requestFreshEntryForRoute(route, focusIndex = 0, scrollTop = 0) {
    const routeKey = getRouteKey(route);
    state.pendingFocusRouteKey = routeKey;
    state.pendingFocusIndex = Number.isInteger(focusIndex) ? focusIndex : 0;
    state.pendingScrollRouteKey = routeKey;
    state.pendingScrollTop = Number.isFinite(scrollTop) ? Math.max(0, scrollTop) : 0;
  }

  function getPanelScrollContainer() {
    return document.querySelector("#quickaccess_content_7 .steamloader-panel");
  }

  function rememberRouteScroll(route = state.route, scrollTop = null) {
    const routeKey = getRouteKey(route);
    const resolvedTop = Number.isFinite(scrollTop)
      ? scrollTop
      : getPanelScrollContainer()?.scrollTop;

    if (!Number.isFinite(resolvedTop)) {
      return;
    }

    state.lastScrollTopByRoute[routeKey] = Math.max(0, resolvedTop);
  }

  function requestScrollRestoreForRoute(route, fallbackTop = null) {
    const routeKey = getRouteKey(route);
    const rememberedTop = state.lastScrollTopByRoute[routeKey];
    const nextTop = Number.isFinite(rememberedTop)
      ? rememberedTop
      : Number.isFinite(fallbackTop)
        ? fallbackTop
        : 0;

    state.pendingScrollRouteKey = routeKey;
    state.pendingScrollTop = Math.max(0, nextTop);
  }

  function clearPendingScrollRestore() {
    if (state.pendingScrollAnimationFrame) {
      window.cancelAnimationFrame(state.pendingScrollAnimationFrame);
      state.pendingScrollAnimationFrame = 0;
    }

    state.pendingScrollRouteKey = null;
    state.pendingScrollTop = null;
  }

  function queuePendingScrollRestore() {
    const routeKey = state.pendingScrollRouteKey;
    const targetTop = state.pendingScrollTop;
    if (!routeKey || !Number.isFinite(targetTop)) {
      return;
    }

    if (state.pendingScrollAnimationFrame) {
      window.cancelAnimationFrame(state.pendingScrollAnimationFrame);
      state.pendingScrollAnimationFrame = 0;
    }

    const applyRestore = (attempt = 0) => {
      state.pendingScrollAnimationFrame = window.requestAnimationFrame(() => {
        if (state.pendingScrollRouteKey !== routeKey) {
          return;
        }

        if (getRouteKey(state.route) !== routeKey) {
          clearPendingScrollRestore();
          return;
        }

        const panel = getPanelScrollContainer();
        if (!(panel instanceof HTMLElement)) {
          if (attempt < 8) {
            applyRestore(attempt + 1);
          } else {
            clearPendingScrollRestore();
          }
          return;
        }

        const maxScrollTop = Math.max(0, panel.scrollHeight - panel.clientHeight);
        const nextScrollTop = Math.max(0, Math.min(targetTop, maxScrollTop));
        if (Math.abs(panel.scrollTop - nextScrollTop) > 1) {
          panel.scrollTop = nextScrollTop;
        }

        if (attempt < 4) {
          applyRestore(attempt + 1);
          return;
        }

        state.lastScrollTopByRoute[routeKey] = panel.scrollTop;
        clearPendingScrollRestore();
      });
    };

    applyRestore(0);
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
      if (route.pluginId === "settings") {
        return {
          route: parseRoute("root"),
          fallbackIndex: 0,
        };
      }

      const pluginIndex = getHomePluginIndex(route.pluginId);
      return {
        route: parseRoute("root"),
        fallbackIndex: pluginIndex >= 0 ? pluginIndex : 0,
      };
    }

    if (route.screen === "page") {
      if (route.pluginId === "settings") {
        return {
          route: parseRoute("root"),
          fallbackIndex: 0,
        };
      }

      if (route.pluginId === "store-sync" && route.pageId?.startsWith("store-")) {
        const storeId = route.pageId.replace(/^store-/, "");
        return {
          route: parseRoute("page:store-sync:stores"),
          fallbackIndex: getStoreSyncStoreIndex(storeId),
        };
      }

      if (route.pluginId === "store-sync" && route.pageId?.startsWith("detected-title-")) {
        const titleId = route.pageId.replace(/^detected-title-/, "");
        return {
          route: parseRoute("page:store-sync:preview"),
          fallbackIndex: getStoreSyncDetectedTitleIndex(titleId),
        };
      }

      if (route.pluginId === "app-start" && route.pageId?.startsWith("app-")) {
        const shortcutId = route.pageId.replace(/^app-/, "");
        return {
          route: parseRoute("plugin:app-start"),
          fallbackIndex: getAppStartShortcutIndex(shortcutId),
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

    if (
      isPerformanceOverlayRoute(route) &&
      autoFocusIndex === 0 &&
      state.performance.pendingSliderAutoFocus
    ) {
      state.performance.pendingSliderAutoFocus = false;
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
      updateHomeReorderInputCapture();

      if (state.route.screen === "root") {
        state.pendingEntryAutoFocus = true;
        state.renderRevision += 1;
        refreshQuickAccessPanel();
      }

      return;
    }

    if (!visible) {
      if (state.homeReorder.active) {
        clearHomeReorderState({ restoreOriginalOrder: true });
      }
      state.panelVisible = false;
      updateHomeReorderInputCapture();
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
    if (window.STFrontendLib?.captureNativeUi) {
      return window.STFrontendLib.captureNativeUi(state);
    }

    return Boolean(state.nativeUi.dialogButtonType);
  }

  function shouldLoadFrontendComponentRegistry() {
    if (!window.STFrontendLib?.refreshComponentRegistry || state.nativeUi.registryLoading) {
      return false;
    }

    const snapshot = state.nativeUi.registrySnapshot;
    const lastAttemptMs = Number(state.nativeUi.registryLastAttemptMs) || 0;
    const isComplete =
      snapshot?.runtimeReady &&
      Number.isInteger(snapshot.availableCount) &&
      Number.isInteger(snapshot.totalCount) &&
      snapshot.availableCount >= snapshot.totalCount;

    return !isComplete && Date.now() - lastAttemptMs > 5000;
  }

  async function loadFrontendComponentRegistry() {
    if (!window.STFrontendLib?.refreshComponentRegistry) {
      return;
    }

    const previousVersion = state.nativeUi.registrySnapshot?.version || 0;
    const previousAvailableCount = state.nativeUi.registrySnapshot?.availableCount || 0;
    await window.STFrontendLib.refreshComponentRegistry(apiBase, state);

    const nextVersion = state.nativeUi.registrySnapshot?.version || 0;
    const nextAvailableCount = state.nativeUi.registrySnapshot?.availableCount || 0;
    if (nextVersion !== previousVersion || nextAvailableCount !== previousAvailableCount) {
      state.renderRevision += 1;
      refreshQuickAccessPanel();
    }
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
          viewBox: "0 0 24 24",
          fill: "none",
        },
        createElement("circle", {
          cx: "12",
          cy: "12",
          r: "10.15",
          fill: "currentColor",
        }),
        createElement("path", {
          d: "M10.35 7.1C10.35 6.74 10.64 6.45 11 6.45C11.36 6.45 11.65 6.74 11.65 7.1V8.8H12.35V7.1C12.35 6.74 12.64 6.45 13 6.45C13.36 6.45 13.65 6.74 13.65 7.1V8.8H14.1C14.85 8.8 15.45 9.4 15.45 10.15V12.65C15.45 14.11 14.5 15.37 13.15 15.81V17.75C13.15 18.14 12.84 18.45 12.45 18.45H11.55C11.16 18.45 10.85 18.14 10.85 17.75V15.81C9.5 15.37 8.55 14.11 8.55 12.65V10.15C8.55 9.4 9.15 8.8 9.9 8.8H10.35V7.1Z",
          fill: "#10161f",
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

  function AudioMuteIcon() {
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
          d: "M25 12.5L30 23.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M30 12.5L25 23.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function MicrophoneIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("rect", {
          x: "14",
          y: "8",
          width: "8",
          height: "14",
          rx: "4",
          fill: "currentColor",
        }),
        createElement("path", {
          d: "M11.5 17.5C11.5 21.0899 14.4101 24 18 24C21.5899 24 24.5 21.0899 24.5 17.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M18 24V29",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M14.5 29H21.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function MicrophoneMuteIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("rect", {
          x: "14",
          y: "8",
          width: "8",
          height: "14",
          rx: "4",
          fill: "currentColor",
          opacity: "0.92",
        }),
        createElement("path", {
          d: "M11.5 17.5C11.5 21.0899 14.4101 24 18 24C21.5899 24 24.5 21.0899 24.5 17.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M18 24V29",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M14.5 29H21.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M10.5 10.5L25.5 25.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
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

  function PerformancePluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M9.5 24.5L15 18.6L18.8 21.5L26.5 12.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M9 28H27",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("circle", {
          cx: "9.5",
          cy: "24.5",
          r: "1.7",
          fill: "currentColor",
        }),
        createElement("circle", {
          cx: "15",
          cy: "18.6",
          r: "1.7",
          fill: "currentColor",
        }),
        createElement("circle", {
          cx: "18.8",
          cy: "21.5",
          r: "1.7",
          fill: "currentColor",
        }),
        createElement("circle", {
          cx: "26.5",
          cy: "12.5",
          r: "1.7",
          fill: "currentColor",
        }),
      ),
    );
  }

  function PowerPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M18 8.5V17.5",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M13 11.5C10.3 13.2 8.5 16.2 8.5 19.6C8.5 24.8 12.8 29 18 29C23.2 29 27.5 24.8 27.5 19.6C27.5 16.2 25.7 13.2 23 11.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
          strokeLinejoin: "round",
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

  function ArtworkPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("rect", {
          x: "8",
          y: "9",
          width: "20",
          height: "18",
          rx: "4",
          stroke: "currentColor",
          strokeWidth: "2.2",
        }),
        createElement("path", {
          d: "M11.5 23L15.5 18.8L18.2 21.4L21.4 16.8L25 23",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("circle", {
          cx: "14",
          cy: "14",
          r: "1.7",
          fill: "currentColor",
        }),
        createElement("path", {
          d: "M24.5 7.5L25.2 9.3L27 10L25.2 10.7L24.5 12.5L23.8 10.7L22 10L23.8 9.3L24.5 7.5Z",
          fill: "currentColor",
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

  function HeaderSettingsIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 24 24",
          fill: "none",
        },
        ...[
          "12 3.35V6.1",
          "12 17.9V20.65",
          "3.35 12H6.1",
          "17.9 12H20.65",
          "5.9 5.9L7.85 7.85",
          "16.15 16.15L18.1 18.1",
          "5.9 18.1L7.85 16.15",
          "16.15 7.85L18.1 5.9",
        ].map((segment, index) =>
          createElement(
            "path",
            {
              d: segment,
              stroke: "currentColor",
              strokeWidth: "1.85",
              strokeLinecap: "round",
            },
            `header-settings-spoke-${index}`,
          ),
        ),
        createElement("circle", {
          cx: "12",
          cy: "12",
          r: "5.1",
          stroke: "currentColor",
          strokeWidth: "1.85",
        }),
        createElement("circle", {
          cx: "12",
          cy: "12",
          r: "2.15",
          fill: "currentColor",
        }),
      ),
    );
  }

  function HeaderUpdateIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 24 24",
          fill: "none",
        },
        createElement("path", {
          d: "M12 4.25V14.6",
          stroke: "currentColor",
          strokeWidth: "1.9",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M8.6 11.4L12 14.85L15.4 11.4",
          stroke: "currentColor",
          strokeWidth: "1.9",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M5.25 17.15C5.25 16.37 5.87 15.75 6.65 15.75H17.35C18.13 15.75 18.75 16.37 18.75 17.15V18.1C18.75 18.88 18.13 19.5 17.35 19.5H6.65C5.87 19.5 5.25 18.88 5.25 18.1V17.15Z",
          stroke: "currentColor",
          strokeWidth: "1.9",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function ProcessesPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("rect", {
          x: "6.5",
          y: "8",
          width: "23",
          height: "16",
          rx: "3.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
        }),
        createElement("path", {
          d: "M12 28H24",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M14 18L17 15L19.8 17.5L24 13.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function AppStartPluginIcon() {
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
          y: "8",
          width: "22",
          height: "20",
          rx: "4.5",
          stroke: "currentColor",
          strokeWidth: "2.2",
        }),
        createElement("path", {
          d: "M15 14.5L22.5 18L15 21.5V14.5Z",
          fill: "currentColor",
        }),
        createElement("path", {
          d: "M11 28.5H25",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function AutoSisirPluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M18 6.5V12.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M18 23.5V29.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M29.5 18H23.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M12.5 18H6.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
        createElement("circle", {
          cx: "18",
          cy: "18",
          r: "6.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
        createElement("circle", {
          cx: "18",
          cy: "18",
          r: "2.2",
          fill: "currentColor",
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
      case "performance":
        return PerformancePluginIcon;
      case "power":
        return PowerPluginIcon;
      case "processes":
        return ProcessesPluginIcon;
      case "app-start":
        return AppStartPluginIcon;
      case "hltb":
        return HltbPluginIcon;
      case "store-sync":
        return StoreSyncPluginIcon;
      case "auto-sisr":
        return AutoSisirPluginIcon;
      case "artwork":
        return ArtworkPluginIcon;
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
    if (window.STFrontendLib?.createDialogButton) {
      return window.STFrontendLib.createDialogButton(
        state,
        createElement,
        content,
        onClick,
        options,
      );
    }

    return createElement("button", {
      type: "button",
      onClick,
      onOKButton: onClick,
      onActivate: onClick,
      disabled: Boolean(options.disabled),
      className: "steamloader-fallback-button",
      children: content,
      ...(options.extraProps || {}),
    }, options.slotKey || options.key || null);
  }

  function renderTrailingContent(slot) {
    if (typeof slot.switchValue === "boolean") {
      if (window.STFrontendLib?.renderSwitchAccessory) {
        return window.STFrontendLib.renderSwitchAccessory(createElement, withChildren, slot);
      }

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

  function createHeaderActionButton(action) {
    if (!action || typeof action.onClick !== "function") {
      return null;
    }

    const HeaderActionIcon = action.icon || SettingsPluginIcon;
    return NativeDialogButton(
      createElement(
        "div",
        withChildren(
          { className: "steamloader-header-action-shell" },
          createElement(HeaderActionIcon, {}),
        ),
      ),
      action.onClick,
      {
        disabled: action.disabled,
        className: action.buttonClassName || "steamloader-dialog-button steamloader-header-action-button",
        extraProps: {
          "aria-label": action.title || "Action",
          title: action.title || "Action",
          style: action.buttonStyle || undefined,
        },
      },
    );
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
        card.imageSrc
          ? createElement("div", {
              className: "steamloader-card-image-shell",
              children: createElement("img", {
                className: "steamloader-card-image",
                src: card.imageSrc,
                alt: card.imageAlt || card.title || "",
              }),
            })
          : null,
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

  function createFooterLegend(items) {
    if (!Array.isArray(items) || !items.length) {
      return null;
    }

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-footer-legend" },
        ...items
          .map((item, index) => {
            if (!item?.button || !item?.label) {
              return null;
            }

            return createElement(
              "div",
              withChildren(
                {
                  className: `steamloader-footer-legend-item${item.active ? " is-active" : ""}`,
                  key: item.key || `footer-legend-${index}`,
                },
                createElement("span", {
                  className: "steamloader-footer-legend-button",
                  children: item.button,
                }),
                createElement("span", {
                  className: "steamloader-footer-legend-label",
                  children: item.label,
                }),
              ),
            );
          })
          .filter(Boolean),
      ),
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
      editor.inputKey || editor.cardKey || "steamloader-editor",
    );
  }

  function createButtonSlot(slot, index, autoFocusIndex) {
    if (typeof slot?.customRenderer === "function") {
      return slot.customRenderer(slot, index, autoFocusIndex);
    }

    if (!slot?.forceFallback && window.STFrontendLib?.createButtonSlot) {
      return window.STFrontendLib.createButtonSlot(
        state,
        createElement,
        withChildren,
        slot,
        index,
        autoFocusIndex,
        {
          getBackNavigation,
          renderTrailingContent,
          handleSlotClick,
          navigateBackFromRoute,
        },
      );
    }

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
        slotKey: slot.slotKey || null,
        className: slot.buttonClassName || "steamloader-dialog-button",
        extraProps: {
          "data-slot-button": String(index),
          autoFocus: Number.isInteger(autoFocusIndex) && index === autoFocusIndex,
          style: slot.buttonStyle || undefined,
          onCancelButton: backNavigation
            ? () => {
                navigateBackFromRoute();
              }
            : undefined,
          ...(slot.buttonProps || {}),
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

  function hasDividerAfter(model, index) {
    if (Number.isInteger(model?.dividerAfterIndex) && index === model.dividerAfterIndex) {
      return true;
    }

    return Array.isArray(model?.dividerAfterIndices) && model.dividerAfterIndices.includes(index);
  }

  function clampVolume(value) {
    return Math.max(0, Math.min(100, Math.round(Number(value) || 0)));
  }

  function snapVolumeToStep(value) {
    return Math.max(0, Math.min(100, Math.round(clampVolume(value) / 10) * 10));
  }

  function clampAudioMixerVolume(value) {
    return Math.max(0, Math.min(100, Math.round(Number(value) || 0)));
  }

  function snapAudioMixerVolumeToStep(value) {
    return Math.max(0, Math.min(100, Math.round(clampAudioMixerVolume(value) / 5) * 5));
  }

  function isSystemVolumeRoute(route = state.route) {
    return (
      route?.screen === "page" &&
      route?.pluginId === "audio" &&
      route?.pageId === "system-volume"
    );
  }

  function isAudioMixerRoute(route = state.route) {
    return (
      route?.screen === "page" &&
      route?.pluginId === "audio" &&
      route?.pageId === "audio-mixer"
    );
  }

  function isAudioDashboardRoute(route = state.route) {
    return route?.screen === "plugin" && route?.pluginId === "audio";
  }

  function isPerformanceOverlayRoute(route = state.route) {
    return (
      route?.screen === "page" &&
      route?.pluginId === "performance" &&
      (route?.pageId === "overlay" || route?.pageId === "tfs-overlay")
    );
  }

  function getVolumeValue() {
    return snapVolumeToStep(state.audio.volumeInfo?.volume ?? 0);
  }

  function getCaptureVolumeValue() {
    return snapVolumeToStep(state.audio.captureVolumeInfo?.volume ?? 0);
  }

  function getAudioPlaybackDevices() {
    return Array.isArray(state.audio.devices) ? state.audio.devices : [];
  }

  function getAudioCaptureDevices() {
    return Array.isArray(state.audio.captureDevices) ? state.audio.captureDevices : [];
  }

  function getSelectedAudioDevice(devices, selectedId) {
    return (Array.isArray(devices) ? devices : []).find((device) => device?.id === selectedId)
      || (Array.isArray(devices) ? devices.find((device) => device?.isDefault) : null)
      || (Array.isArray(devices) ? devices[0] : null)
      || null;
  }

  function getCurrentPlaybackDevice() {
    return getSelectedAudioDevice(getAudioPlaybackDevices(), state.audio.volumeInfo?.deviceId);
  }

  function getCurrentCaptureDevice() {
    return getSelectedAudioDevice(getAudioCaptureDevices(), state.audio.captureVolumeInfo?.deviceId);
  }

  function getAudioDashboardError() {
    return (
      state.audio.dashboardError ||
      state.audio.volumeError ||
      state.audio.captureVolumeError ||
      state.audio.mixerError ||
      state.audio.error ||
      ""
    );
  }

  function clearVolumeCommitTimer() {
    if (state.audio.volumeCommitTimer) {
      window.clearTimeout(state.audio.volumeCommitTimer);
      state.audio.volumeCommitTimer = 0;
    }
  }

  function clearCaptureVolumeCommitTimer() {
    if (state.audio.captureVolumeCommitTimer) {
      window.clearTimeout(state.audio.captureVolumeCommitTimer);
      state.audio.captureVolumeCommitTimer = 0;
    }
  }

  function previewSliderVolume(value) {
    const info = state.audio.volumeInfo;
    if (!info) {
      return;
    }

    state.audio.volumeInfo = {
      ...info,
      volume: snapVolumeToStep(value),
    };
    refreshAudioVolumePanel();
  }

  function queueSliderVolumeCommit(value) {
    const nextValue = snapVolumeToStep(value);
    clearVolumeCommitTimer();
    state.audio.volumeCommitTimer = window.setTimeout(() => {
      state.audio.volumeCommitTimer = 0;
      void setVolume(nextValue);
    }, 140);
  }

  function previewCaptureSliderVolume(value) {
    const info = state.audio.captureVolumeInfo;
    if (!info) {
      return;
    }

    const nextValue = snapVolumeToStep(value);
    state.audio.captureVolumeInfo = {
      ...info,
      volume: nextValue,
      isMuted: nextValue <= 0 ? true : info.isMuted,
    };
    rerenderAudioDashboard();
  }

  function queueCaptureSliderVolumeCommit(value) {
    const nextValue = snapVolumeToStep(value);
    clearCaptureVolumeCommitTimer();
    state.audio.captureVolumeCommitTimer = window.setTimeout(() => {
      state.audio.captureVolumeCommitTimer = 0;
      void setCaptureVolume(nextValue);
    }, 140);
  }

  function startVolumeSliderEditing() {
    if (!isSystemVolumeRoute() || !state.audio.volumeInfo || state.audio.sliderEditActive) {
      return;
    }

    state.audio.sliderEditActive = true;
    refreshAudioVolumePanel({ fullRender: true, cueSlider: true });
  }

  function finishVolumeSliderEditing(commit = true) {
    const shouldCommit = Boolean(commit && state.audio.volumeInfo);
    clearVolumeCommitTimer();

    if (!state.audio.sliderEditActive && !shouldCommit) {
      return;
    }

    state.audio.sliderEditActive = false;
    refreshAudioVolumePanel({ fullRender: true });

    if (shouldCommit) {
      void setVolume(getVolumeValue());
    }
  }

  function handleVolumeSliderKeyDown(event) {
    if (!state.audio.sliderEditActive || !isSystemVolumeRoute()) {
      return;
    }

    const key = event?.key || event?.code || "";
    const isLeft = key === "ArrowLeft" || key === "GamepadLeft" || key === "GamepadDPadLeft";
    const isRight = key === "ArrowRight" || key === "GamepadRight" || key === "GamepadDPadRight";
    const isFinish =
      key === "Escape" ||
      key === "GamepadB" ||
      key === "GamepadA" ||
      key === "Enter" ||
      key === " " ||
      key === "Space";

    if (!isLeft && !isRight && !isFinish) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation?.();

    if (isLeft || isRight) {
      const delta = isLeft ? -10 : 10;
      const nextValue = getVolumeValue() + delta;
      previewSliderVolume(nextValue);
      queueSliderVolumeCommit(nextValue);
      return;
    }

    finishVolumeSliderEditing(true);
  }

  function ensureVolumeSliderHotkeys() {
    if (state.audio.sliderHotkeysInstalled) {
      return;
    }

    document.addEventListener("keydown", handleVolumeSliderKeyDown, true);
    state.audio.sliderHotkeysInstalled = true;
  }

  function getFocusedAudioDashboardControlIndex() {
    const rememberedIndex = state.lastSelectedIndexByRoute[getRouteKey(state.route)];
    if (Number.isInteger(rememberedIndex)) {
      return rememberedIndex;
    }

    const fallbackIndex = resolveAutoFocusIndex(state.route);
    return Number.isInteger(fallbackIndex) ? fallbackIndex : 0;
  }

  function getAudioDashboardControlByIndex(index) {
    if (!isAudioDashboardRoute() || !Number.isInteger(index)) {
      return null;
    }

    const dashboard = buildAudioDashboardModel();
    const controls = [
      dashboard?.playbackToggle,
      dashboard?.captureToggle,
      dashboard?.playbackSlider,
      dashboard?.captureSlider,
      dashboard?.playbackSelector,
      dashboard?.captureSelector,
      ...(Array.isArray(dashboard?.mixerControls) ? dashboard.mixerControls : []),
      dashboard?.refreshControl,
    ];

    return controls.find((control) => control?.index === index) || null;
  }

  function handleAudioDashboardKeyDown(event) {
    if (!isAudioDashboardRoute()) {
      return;
    }

    const key = event?.key || event?.code || "";
    const isLeft = key === "ArrowLeft" || key === "GamepadLeft" || key === "GamepadDPadLeft";
    const isRight = key === "ArrowRight" || key === "GamepadRight" || key === "GamepadDPadRight";

    if (!isLeft && !isRight) {
      return;
    }

    const control = getAudioDashboardControlByIndex(getFocusedAudioDashboardControlIndex());
    if (!control || control.disabled) {
      return;
    }

    const moveHandler = isLeft ? control.onMoveLeft : control.onMoveRight;
    if (typeof moveHandler !== "function") {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation?.();

    rememberCurrentRouteIndex(control.index);
    moveHandler(event);
  }

  function ensureAudioDashboardHotkeys() {
    if (state.audio.dashboardHotkeysInstalled) {
      return;
    }

    document.addEventListener("keydown", handleAudioDashboardKeyDown, true);
    state.audio.dashboardHotkeysInstalled = true;
  }

  function rerenderAudioVolumePanel() {
    if (isSystemVolumeRoute()) {
      state.audio.pendingVolumeActionAutoFocus = true;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function getVolumePanelCopy() {
    const info = state.audio.volumeInfo;
    const deviceName = info?.deviceName || "Default playback device";
    return info?.isMuted ? `${deviceName} - Muted` : deviceName;
  }

  function getVolumePanelHint() {
    const info = state.audio.volumeInfo;

    if (state.audio.volumeLoading && !info) {
      return "Loading current system volume...";
    }

    if (state.audio.volumeLoading) {
      return "Applying the new audio state...";
    }

    if (!info) {
      return "The current default playback device will appear here.";
    }

    return state.audio.sliderEditActive
      ? "Editing Main Slider. Use Left / Right in 10% steps. Press A or B to finish."
      : "Press A on Main Slider, then use Left / Right in 10% steps. Press B to close.";
  }

  function queueVolumeSliderActivationCue() {
    if (state.audio.sliderActivationTimer) {
      window.clearTimeout(state.audio.sliderActivationTimer);
      state.audio.sliderActivationTimer = 0;
    }

    window.requestAnimationFrame(() => {
      const sliderButton = document.querySelector('.steamloader-volume-slider-fallback-button[data-volume-slider="true"]');
      if (!(sliderButton instanceof HTMLElement)) {
        return;
      }

      sliderButton.classList.remove("is-activating");
      void sliderButton.offsetWidth;
      sliderButton.classList.add("is-activating");

      state.audio.sliderActivationTimer = window.setTimeout(() => {
        state.audio.sliderActivationTimer = 0;
        sliderButton.classList.remove("is-activating");
      }, 320);
    });
  }

  function syncLiveVolumePanelUi() {
    if (!isSystemVolumeRoute()) {
      return false;
    }

    const volumeCard = document.querySelector(".steamloader-volume-card");
    if (!(volumeCard instanceof HTMLElement)) {
      return false;
    }

    const sliderButton = volumeCard.querySelector('.steamloader-volume-slider-fallback-button[data-volume-slider="true"]');
    const volumeValue = getVolumeValue();
    const volumePercent = `${Math.max(0, Math.min(100, volumeValue))}%`;
    const hintText = state.audio.volumeError || getVolumePanelHint();
    const hintNode = volumeCard.querySelector(".steamloader-volume-hint, .steamloader-volume-hint-error");
    const copyNode = volumeCard.querySelector(".steamloader-volume-copy");
    const valueNode = volumeCard.querySelector(".steamloader-volume-slider-value");
    const fillNode = volumeCard.querySelector(".steamloader-volume-slider-fill");
    const thumbNode = volumeCard.querySelector(".steamloader-volume-slider-thumb");

    if (sliderButton instanceof HTMLElement) {
      sliderButton.classList.toggle("is-editing", state.audio.sliderEditActive);
    }

    if (copyNode instanceof HTMLElement) {
      copyNode.textContent = getVolumePanelCopy();
    }

    if (hintNode instanceof HTMLElement) {
      const hasError = Boolean(state.audio.volumeError);
      hintNode.classList.toggle("steamloader-volume-hint-error", hasError);
      hintNode.classList.toggle("steamloader-volume-hint", !hasError);
      hintNode.textContent = hintText;
    }

    if (valueNode instanceof HTMLElement) {
      valueNode.textContent = `${volumeValue}%`;
    }

    if (fillNode instanceof HTMLElement) {
      fillNode.style.width = volumePercent;
    }

    if (thumbNode instanceof HTMLElement) {
      thumbNode.style.left = volumePercent;
    }

    return true;
  }

  function refreshAudioVolumePanel(options = {}) {
    if (options.fullRender !== true && syncLiveVolumePanelUi()) {
      if (options.cueSlider) {
        queueVolumeSliderActivationCue();
      }

      return;
    }

    rerenderAudioVolumePanel();

    if (options.cueSlider) {
      queueVolumeSliderActivationCue();
    }
  }

  function sortAudioMixerSessions(sessions) {
    return [...(Array.isArray(sessions) ? sessions : [])].sort((left, right) => {
      const systemCompare = Number(Boolean(left?.isSystemSession)) - Number(Boolean(right?.isSystemSession));
      if (systemCompare !== 0) {
        return systemCompare;
      }

      const displayNameCompare = String(left?.displayName || "").localeCompare(
        String(right?.displayName || ""),
        undefined,
        { sensitivity: "base", numeric: true },
      );
      if (displayNameCompare !== 0) {
        return displayNameCompare;
      }

      const leftProcessId = Number.isInteger(left?.processId) ? left.processId : Number.MAX_SAFE_INTEGER;
      const rightProcessId = Number.isInteger(right?.processId) ? right.processId : Number.MAX_SAFE_INTEGER;
      return leftProcessId - rightProcessId;
    });
  }

  function getAudioMixerSessions() {
    return sortAudioMixerSessions(state.audio.mixerSessions);
  }

  function findAudioMixerSession(sessionId) {
    return state.audio.mixerSessions.find((session) => session?.sessionId === sessionId) || null;
  }

  function upsertAudioMixerSession(session) {
    if (!session || typeof session !== "object" || !session.sessionId) {
      return;
    }

    let replaced = false;
    const nextSessions = state.audio.mixerSessions.map((current) => {
      if (current?.sessionId !== session.sessionId) {
        return current;
      }

      replaced = true;
      return {
        ...current,
        ...session,
      };
    });

    state.audio.mixerSessions = sortAudioMixerSessions(
      replaced ? nextSessions : [...nextSessions, session],
    );
  }

  function clearAudioMixerVolumeCommitTimer(sessionId) {
    const timerHandle = state.audio.mixerVolumeCommitTimersById[sessionId];
    if (!timerHandle) {
      return;
    }

    window.clearTimeout(timerHandle);
    delete state.audio.mixerVolumeCommitTimersById[sessionId];
  }

  function hasPendingAudioMixerCommits() {
    return Object.values(state.audio.mixerVolumeCommitTimersById).some((timerHandle) => Boolean(timerHandle));
  }

  function rerenderAudioMixerPanel() {
    if (isAudioDashboardRoute()) {
      rerenderAudioDashboard();
      return;
    }

    if (isAudioMixerRoute() && state.audio.mixerSessions.length) {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function previewAudioMixerSessionVolume(sessionId, value) {
    const nextValue = snapAudioMixerVolumeToStep(value);
    state.audio.mixerSessions = state.audio.mixerSessions.map((session) =>
      session?.sessionId === sessionId
        ? {
            ...session,
            volume: nextValue,
            isMuted: nextValue <= 0 ? true : false,
          }
        : session,
    );
    rerenderAudioMixerPanel();
  }

  function queueAudioMixerVolumeCommit(sessionId, value) {
    const nextValue = snapAudioMixerVolumeToStep(value);
    clearAudioMixerVolumeCommitTimer(sessionId);
    state.audio.mixerVolumeCommitTimersById[sessionId] = window.setTimeout(() => {
      delete state.audio.mixerVolumeCommitTimersById[sessionId];
      void setAudioMixerSessionVolume(sessionId, nextValue, { optimistic: false });
    }, 140);
  }

  function adjustAudioMixerSessionVolume(sessionId, direction, step = 5) {
    if (!sessionId || !direction) {
      return;
    }

    const session = findAudioMixerSession(sessionId);
    if (!session) {
      return;
    }

    const currentValue = snapAudioMixerVolumeToStep(session.volume);
    const nextValue = snapAudioMixerVolumeToStep(currentValue + direction * step);
    if (nextValue === currentValue && !session.isMuted) {
      return;
    }

    previewAudioMixerSessionVolume(sessionId, nextValue);
    queueAudioMixerVolumeCommit(sessionId, nextValue);
  }

  function getAudioMixerSessionCopy(session) {
    if (!session) {
      return "";
    }

    return session.secondaryLabel || (session.isSystemSession ? "Windows audio" : "Active audio session");
  }

  function getAudioMixerSessionHint(session) {
    if (!session) {
      return "Use Left / Right to adjust this app.";
    }

    return session.isMuted
      ? "Muted. Press A to unmute, or use Left / Right to set a new level."
      : "Use Left / Right to mix this process. Press A to mute it.";
  }

  function getAudioMixerSessionDisplayValue(session) {
    if (!session) {
      return "0%";
    }

    return session.isMuted ? "Muted" : `${snapAudioMixerVolumeToStep(session.volume)}%`;
  }

  function getPerformanceSnapshot() {
    return state.performance.snapshot;
  }

  function getPerformanceInstallation() {
    return getPerformanceSnapshot()?.installation || null;
  }

  function getPerformanceSettings() {
    return getPerformanceSnapshot()?.settings || null;
  }

  function getPerformanceRuntime() {
    return getPerformanceSnapshot()?.runtime || null;
  }

  function getPerformanceVendorOverlays() {
    const overlays = getPerformanceSnapshot()?.vendorOverlays;
    return Array.isArray(overlays) ? overlays : [];
  }

  function getPerformanceVendorOverlay(vendorId) {
    return getPerformanceVendorOverlays().find((overlay) => overlay.id === vendorId) || null;
  }

  function getPerformanceVendorOverlayBadge(overlay) {
    if (!overlay) {
      return "Unavailable";
    }

    return !overlay.supported
      ? "Unavailable"
      : overlay.stateDetected
        ? overlay.active ? "On" : "Off"
        : overlay.installed ? "Ready" : "Missing";
  }

  const performancePositionOptions = Object.freeze([
    { value: 0, title: "Top Left" },
    { value: 1, title: "Top Right" },
    { value: 2, title: "Bottom Left" },
    { value: 3, title: "Bottom Right" },
  ]);

  const performanceBackgroundThemeOptions = Object.freeze([
    { value: 0, title: "Steam Blue" },
    { value: 1, title: "Slate" },
    { value: 2, title: "Midnight" },
    { value: 3, title: "Graphite" },
    { value: 4, title: "Frost" },
  ]);

  const performanceGraphModeOptions = Object.freeze([
    { value: 0, title: "Off" },
    { value: 1, title: "FPS" },
    { value: 2, title: "Frametime" },
  ]);

  function getPerformanceLevelDefinitions() {
    const settings = getPerformanceSettings();
    return Array.isArray(settings?.overlayLevels) ? settings.overlayLevels : [];
  }

  function getPerformanceOverlayLevel() {
    const configuredLevel = getPerformanceSettings()?.overlayLevel;
    return Number.isInteger(configuredLevel) ? configuredLevel : 0;
  }

  function getPerformanceDraftLevel() {
    return Number.isInteger(state.performance.draftOverlayLevel)
      ? state.performance.draftOverlayLevel
      : getPerformanceOverlayLevel();
  }

  function getPerformanceLevelDefinitionByValue(value) {
    const levels = getPerformanceLevelDefinitions();
    return levels.find((level) => level.value === value) || levels[0] || null;
  }

  function getPerformanceLevelDisplayText() {
    return getPerformanceLevelDefinitionByValue(getPerformanceDraftLevel())?.title || "Basic";
  }

  function getPerformancePosition() {
    const value = getPerformanceSettings()?.overlayPosition;
    return Number.isInteger(value) ? value : 0;
  }

  function getPerformancePositionTitle() {
    return (
      getPerformanceSettings()?.overlayPositionTitle ||
      performancePositionOptions.find((option) => option.value === getPerformancePosition())?.title ||
      "Top Left"
    );
  }

  function getPerformanceOverlayWidth() {
    const value = getPerformanceSettings()?.overlayWidth;
    return Number.isFinite(value) ? Number(value) : 400;
  }

  function getPerformanceOverlayScale() {
    const value = getPerformanceSettings()?.overlayScale;
    return Number.isFinite(value) ? Number(value) : 100;
  }

  function getPerformanceGraphMode() {
    const value = getPerformanceSettings()?.graphMode;
    return Number.isInteger(value) ? value : 1;
  }

  function getPerformanceGraphModeTitle() {
    return (
      getPerformanceSettings()?.graphModeTitle ||
      performanceGraphModeOptions.find((option) => option.value === getPerformanceGraphMode())?.title ||
      "FPS"
    );
  }

  function getPerformanceBackgroundTheme() {
    const value = getPerformanceSettings()?.backgroundTheme;
    return Number.isInteger(value) ? value : 0;
  }

  function getPerformanceBackgroundThemeTitle() {
    return (
      getPerformanceSettings()?.backgroundThemeTitle ||
      performanceBackgroundThemeOptions.find((option) => option.value === getPerformanceBackgroundTheme())?.title ||
      "Steam Blue"
    );
  }

  function getPerformanceBackgroundOpacity() {
    const value = getPerformanceSettings()?.backgroundOpacity;
    return Number.isFinite(value) ? Number(value) : 90;
  }

  function getPerformanceMetricPollRate() {
    const value = getPerformanceSettings()?.metricPollRate;
    return Number.isFinite(value) ? Number(value) : 40;
  }

  function getPerformanceTelemetrySamplingPeriodMs() {
    const value = getPerformanceSettings()?.telemetrySamplingPeriodMs;
    return Number.isFinite(value) ? Number(value) : 100;
  }

  function getPerformanceMetricsWindow() {
    const value = getPerformanceSettings()?.metricsWindow;
    return Number.isFinite(value) ? Number(value) : 1000;
  }

  function getPerformanceOverlayDrawRate() {
    const value = getPerformanceSettings()?.overlayDrawRate;
    return Number.isFinite(value) ? Number(value) : 10;
  }

  function clampPerformanceSettingValue(value, min, max) {
    return Math.max(min, Math.min(max, value));
  }

  async function cyclePerformanceOptionSetting(key, currentValue, options, direction = 1) {
    if (isPerformanceBusy() || !Array.isArray(options) || !options.length || !direction) {
      return;
    }

    const currentIndex = Math.max(0, options.findIndex((option) => option.value === currentValue));
    const nextIndex = Math.max(0, Math.min(options.length - 1, currentIndex + direction));
    const nextValue = options[nextIndex]?.value ?? options[0].value;
    if (nextValue === currentValue) {
      return;
    }

    await setPerformanceSettingValue(key, nextValue);
  }

  async function adjustPerformanceNumberSetting(key, currentValue, direction, step, min, max) {
    if (isPerformanceBusy() || !direction) {
      return;
    }

    const nextValue = clampPerformanceSettingValue(currentValue + direction * step, min, max);
    if (nextValue === currentValue) {
      return;
    }

    await setPerformanceSettingValue(key, nextValue);
  }

  function getPerformancePanelCopy() {
    const installation = getPerformanceInstallation();
    const runtime = getPerformanceRuntime();
    if (installation && installation.elevatedHelperReady === false) {
      return "Elevated Helper Setup Required";
    }

    if (installation?.running && runtime?.targetProcessName) {
      return `${runtime.targetProcessName} - ${getPerformanceLevelDisplayText()}`;
    }

    return installation?.running
      ? `Running - ${getPerformanceLevelDisplayText()}`
      : `Ready - ${getPerformanceLevelDisplayText()}`;
  }

  function getPerformancePanelHint() {
    if (state.performance.loading) {
      return "Loading TFS FPS Overlay...";
    }

    const installation = getPerformanceInstallation();
    if (installation && installation.elevatedHelperReady === false) {
      return "Press Prepare Elevated Helper once. Windows will ask for admin permission, then future starts stay silent.";
    }

    return state.performance.sliderEditActive
      ? "Editing Overlay Level. Use Left / Right to choose a preset. Press A or B to apply."
      : "Press A on Overlay Level, then use Left / Right to switch presets.";
  }

  function startPerformanceSliderEditing() {
    if (
      !isPerformanceOverlayRoute() ||
      isPerformanceBusy() ||
      state.performance.sliderEditActive
    ) {
      return;
    }

    state.performance.draftOverlayLevel = getPerformanceOverlayLevel();
    state.performance.sliderEditActive = true;
    refreshPerformancePanel({ fullRender: true, cueSlider: true });
  }

  function finishPerformanceSliderEditing(commit = true) {
    const nextLevel = getPerformanceDraftLevel();
    const shouldCommit = Boolean(commit && !state.performance.saving);

    if (!state.performance.sliderEditActive && !shouldCommit) {
      state.performance.draftOverlayLevel = null;
      return;
    }

    state.performance.sliderEditActive = false;
    state.performance.draftOverlayLevel = null;
    refreshPerformancePanel({ fullRender: true });

    if (shouldCommit && nextLevel !== getPerformanceOverlayLevel()) {
      void setPerformanceOverlayLevel(nextLevel);
    }
  }

  function movePerformanceSlider(direction) {
    const levels = getPerformanceLevelDefinitions();
    if (!levels.length || !direction || isPerformanceBusy()) {
      return;
    }

    if (!state.performance.sliderEditActive) {
      state.performance.draftOverlayLevel = getPerformanceOverlayLevel();
      state.performance.sliderEditActive = true;
    }

    const currentIndex = Math.max(
      0,
      levels.findIndex((level) => level.value === getPerformanceDraftLevel()),
    );
    const nextIndex = Math.max(0, Math.min(levels.length - 1, currentIndex + direction));
    const nextLevel = levels[nextIndex]?.value ?? levels[0].value;
    state.performance.draftOverlayLevel = nextLevel;
    refreshPerformancePanel();

    if (nextLevel !== getPerformanceOverlayLevel()) {
      void setPerformanceOverlayLevel(nextLevel);
    }
  }

  function handlePerformanceSliderKeyDown(event) {
    if (!state.performance.sliderEditActive || !isPerformanceOverlayRoute()) {
      return;
    }

    const key = event?.key || event?.code || "";
    const isLeft = key === "ArrowLeft" || key === "GamepadLeft" || key === "GamepadDPadLeft";
    const isRight = key === "ArrowRight" || key === "GamepadRight" || key === "GamepadDPadRight";
    const isFinish =
      key === "Escape" ||
      key === "GamepadB" ||
      key === "GamepadA" ||
      key === "Enter" ||
      key === " " ||
      key === "Space";

    if (!isLeft && !isRight && !isFinish) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation?.();

    if (isLeft || isRight) {
      movePerformanceSlider(isLeft ? -1 : 1);
      return;
    }

    finishPerformanceSliderEditing(true);
  }

  function ensurePerformanceSliderHotkeys() {
    if (state.performance.sliderHotkeysInstalled) {
      return;
    }

    document.addEventListener("keydown", handlePerformanceSliderKeyDown, true);
    state.performance.sliderHotkeysInstalled = true;
  }

  function queuePerformanceSliderActivationCue() {
    if (state.performance.sliderActivationTimer) {
      window.clearTimeout(state.performance.sliderActivationTimer);
      state.performance.sliderActivationTimer = 0;
    }

    window.requestAnimationFrame(() => {
      const sliderButton = document.querySelector('.steamloader-volume-slider-fallback-button[data-volume-slider="true"]');
      if (!(sliderButton instanceof HTMLElement)) {
        return;
      }

      sliderButton.classList.remove("is-activating");
      void sliderButton.offsetWidth;
      sliderButton.classList.add("is-activating");

      state.performance.sliderActivationTimer = window.setTimeout(() => {
        state.performance.sliderActivationTimer = 0;
        sliderButton.classList.remove("is-activating");
      }, 320);
    });
  }

  function syncLivePerformancePanelUi() {
    if (!isPerformanceOverlayRoute()) {
      return false;
    }

    const volumeCard = document.querySelector(".steamloader-volume-card");
    if (!(volumeCard instanceof HTMLElement)) {
      return false;
    }

    const levels = getPerformanceLevelDefinitions();
    const levelValue = getPerformanceDraftLevel();
    const levelIndex = Math.max(0, levels.findIndex((level) => level.value === levelValue));
    const percent = `${levels.length > 1 ? (levelIndex / (levels.length - 1)) * 100 : 0}%`;
    const hintText = state.performance.error || getPerformancePanelHint();
    const sliderButton = volumeCard.querySelector('.steamloader-volume-slider-fallback-button[data-volume-slider="true"]');
    const hintNode = volumeCard.querySelector(".steamloader-volume-hint, .steamloader-volume-hint-error");
    const copyNode = volumeCard.querySelector(".steamloader-volume-copy");
    const valueNode = volumeCard.querySelector(".steamloader-volume-slider-value");
    const fillNode = volumeCard.querySelector(".steamloader-volume-slider-fill");
    const thumbNode = volumeCard.querySelector(".steamloader-volume-slider-thumb");

    if (sliderButton instanceof HTMLElement) {
      sliderButton.classList.toggle("is-editing", state.performance.sliderEditActive);
    }

    if (copyNode instanceof HTMLElement) {
      copyNode.textContent = getPerformancePanelCopy();
    }

    if (hintNode instanceof HTMLElement) {
      const hasError = Boolean(state.performance.error);
      hintNode.classList.toggle("steamloader-volume-hint-error", hasError);
      hintNode.classList.toggle("steamloader-volume-hint", !hasError);
      hintNode.textContent = hintText;
    }

    if (valueNode instanceof HTMLElement) {
      valueNode.textContent = getPerformanceLevelDisplayText();
    }

    if (fillNode instanceof HTMLElement) {
      fillNode.style.width = percent;
    }

    if (thumbNode instanceof HTMLElement) {
      thumbNode.style.left = percent;
    }

    return true;
  }

  function refreshPerformancePanel(options = {}) {
    if (options.cueSlider) {
      state.performance.pendingSliderAutoFocus = true;
    }

    if (options.fullRender !== true && syncLivePerformancePanelUi()) {
      if (options.cueSlider) {
        queuePerformanceSliderActivationCue();
      }

      return;
    }

    rerenderPerformancePanel();

    if (options.cueSlider) {
      queuePerformanceSliderActivationCue();
    }
  }

  function renderPanelState(options = {}) {
    if (options.preserveFocus !== false && !state.pendingFocusRouteKey) {
      const focusedIndex = getFocusedSlotIndex();
      if (Number.isInteger(focusedIndex)) {
        requestFocusForRoute(state.route, focusedIndex);
      }
    }

    if (options.preserveScroll !== false) {
      rememberRouteScroll(state.route);
      requestScrollRestoreForRoute(state.route);
    }

    install();
    invalidate();

    if (options.preserveScroll !== false) {
      queuePendingScrollRestore();
    }
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

  function rerenderPerformancePanel() {
    if (isPerformanceOverlayRoute()) {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      const fallbackIndex = Number.isInteger(focusedIndex)
        ? focusedIndex
        : state.performance.pendingSliderAutoFocus
          ? 0
          : null;

      state.performance.pendingSliderAutoFocus = false;
      requestFocusForRoute(currentRoute, fallbackIndex);
      setRoute(currentRoute);
      return;
    }

    if (state.route.pluginId === "performance") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderPowerPanel() {
    if (state.route.pluginId === "power") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderProcessesPanel() {
    if (state.route.pluginId === "processes") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderAppStartPanel() {
    if (state.route.pluginId === "app-start") {
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

  function rerenderArtworkPanel() {
    if (state.route.pluginId === "artwork") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderAutoSisirPanel() {
    if (state.route.pluginId === "auto-sisr") {
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

  function rerenderGeneralSettingsPanel() {
    if (state.route.pluginId === "settings" || state.route.screen === "root") {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderHomePanel(fallbackIndex = null) {
    if (state.route.screen === "root") {
      const currentRoute = { ...state.route };
      const focusedIndex = Number.isInteger(fallbackIndex) ? fallbackIndex : getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, Number.isInteger(focusedIndex) ? focusedIndex : 0);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  function resetHomeReorderArmState() {
    state.homeReorder.catchAllButtonState = {};
  }

  function isDeveloperDebugEnabled() {
    return Boolean(getGeneralSettingsSnapshot()?.developerDebugEnabled);
  }

  function formatDeveloperDebugMessage(source, detail = "") {
    const timestamp = new Date().toLocaleTimeString("de-DE", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
    return detail ? `${timestamp} ${source}: ${detail}` : `${timestamp} ${source}`;
  }

  function setDeveloperDebug(scope, source, detail = "", options = {}) {
    if (!scope) {
      return;
    }

    state.developerDebug.messages[scope] = formatDeveloperDebugMessage(source, detail);

    if (options.skipRender) {
      return;
    }

    if (typeof options.render === "function") {
      options.render();
      return;
    }

    renderPanelState();
  }

  function getDeveloperDebugNote(scope, fallback = "") {
    if (!isDeveloperDebugEnabled()) {
      return "";
    }

    const message = state.developerDebug.messages[scope];
    if (message) {
      return `Debug: ${message}`;
    }

    return fallback ? `Debug: ${fallback}` : "";
  }

  function setHomeReorderDebug(source, detail = "", options = {}) {
    setDeveloperDebug("home-reorder", source, detail, {
      ...options,
      render: options.skipRender || state.route.screen !== "root"
        ? undefined
        : () => rerenderHomePanel(),
    });
  }

  function startHomeReorderFromFocusedPlugin(source, fallbackPluginId = "") {
    const focusedPluginId = getHomeFocusedPluginId() || fallbackPluginId || "";
    setHomeReorderDebug(source, focusedPluginId || "no focused plugin");

    if (
      !focusedPluginId ||
      state.homeReorder.active ||
      state.generalSettings.loading ||
      state.generalSettings.saving
    ) {
      return false;
    }

    startHomeReorderMode(focusedPluginId);
    return true;
  }

  function getHomeFocusedPluginId() {
    const focusedIndex = getFocusedSlotIndex();
    const homePlugins = getHomePlugins();
    return Number.isInteger(focusedIndex) && focusedIndex >= 0 && focusedIndex < homePlugins.length
      ? homePlugins[focusedIndex].id
      : "";
  }

  function applyLocalPluginOrder(pluginIds) {
    const snapshot = getGeneralSettingsSnapshot();
    if (!snapshot?.plugins?.length) {
      return;
    }

    const normalizedIds = normalizePluginOrderIds(pluginIds);
    const entriesById = new Map(snapshot.plugins.map((plugin) => [plugin.id, plugin]));

    state.generalSettings.snapshot = {
      ...snapshot,
      plugins: normalizedIds
        .map((pluginId) => entriesById.get(pluginId))
        .filter(Boolean),
    };
  }

  function buildPersistedOrderFromVisibleIds(visibleIds) {
    const orderedVisibleIds = normalizePluginOrderIds(visibleIds)
      .filter((pluginId) => visibleIds.includes(pluginId));
    const hiddenIds = getPersistedPluginOrderIds()
      .filter((pluginId) => !orderedVisibleIds.includes(pluginId));
    return [...orderedVisibleIds, ...hiddenIds];
  }

  function clearHomeReorderState(options = {}) {
    const restoreOriginalOrder = options.restoreOriginalOrder === true;
    if (restoreOriginalOrder && state.homeReorder.originalOrderIds.length) {
      applyLocalPluginOrder(state.homeReorder.originalOrderIds);
    }

    resetHomeReorderArmState();
    state.homeReorder.active = false;
    state.homeReorder.movingPluginId = "";
    state.homeReorder.originalOrderIds = [];
    state.homeReorder.activationLocked = false;
  }

  function cancelHomeReorder() {
    if (!state.homeReorder.active) {
      return;
    }

    const movingPluginId = state.homeReorder.movingPluginId;
    setHomeReorderDebug("cancel", movingPluginId || "active move");
    clearHomeReorderState({ restoreOriginalOrder: true });
    rerenderHomePanel(Math.max(0, getHomePluginIndex(movingPluginId)));
  }

  async function commitHomeReorder() {
    if (!state.homeReorder.active || state.homeReorder.activationLocked) {
      return;
    }

    const movingPluginId = state.homeReorder.movingPluginId;
    const originalOrderIds = [...state.homeReorder.originalOrderIds];
    const nextOrderIds = getPersistedPluginOrderIds();
    setHomeReorderDebug("drop", nextOrderIds.join(" -> "), { skipRender: true });

    clearHomeReorderState();
    rerenderHomePanel(Math.max(0, getHomePluginIndex(movingPluginId)));

    const saved = await sendGeneralSettingsRequest("api/settings/plugins/order", {
      pluginIds: nextOrderIds,
    });

    if (!saved) {
      applyLocalPluginOrder(originalOrderIds);
      rerenderHomePanel(Math.max(0, getHomePluginIndex(movingPluginId)));
    }
  }

  function moveHomeReorderSelection(direction) {
    if (!state.homeReorder.active || !direction) {
      return;
    }

    const currentVisibleIds = getHomePlugins().map((plugin) => plugin.id);
    const currentIndex = currentVisibleIds.indexOf(state.homeReorder.movingPluginId);
    if (currentIndex < 0) {
      return;
    }

    const nextIndex = Math.max(0, Math.min(currentVisibleIds.length - 1, currentIndex + direction));
    if (nextIndex === currentIndex) {
      return;
    }

    const reorderedVisibleIds = [...currentVisibleIds];
    const [movingPluginId] = reorderedVisibleIds.splice(currentIndex, 1);
    reorderedVisibleIds.splice(nextIndex, 0, movingPluginId);
    setHomeReorderDebug(direction < 0 ? "move up" : "move down", movingPluginId, { skipRender: true });
    applyLocalPluginOrder(buildPersistedOrderFromVisibleIds(reorderedVisibleIds));
    rerenderHomePanel(nextIndex);
  }

  function startHomeReorderMode(pluginId) {
    if (
      !pluginId ||
      state.homeReorder.active ||
      state.generalSettings.loading ||
      state.generalSettings.saving ||
      state.route.screen !== "root"
    ) {
      return;
    }

    if (!getGeneralSettingsSnapshot()?.plugins?.length) {
      void loadGeneralSettingsState();
      return;
    }

    resetHomeReorderArmState();
    state.homeReorder.active = true;
    state.homeReorder.movingPluginId = pluginId;
    state.homeReorder.originalOrderIds = getPersistedPluginOrderIds();
    state.homeReorder.activationLocked = false;
    rerenderHomePanel(Math.max(0, getHomePluginIndex(pluginId)));
  }

  function isHomeReorderConfirmKey(event) {
    return event?.key === "Enter" || event?.key === " " || event?.code === "Enter" || event?.code === "Space";
  }

  function isHomeReorderActivateKey(event) {
    return event?.key?.toLowerCase?.() === "y" || event?.code === "KeyY";
  }

  function getHomeReorderActionFromSteamButton(button) {
    const namedButton = String(button || "").toUpperCase();
    if (/(UP|DPAD_UP)/.test(namedButton)) {
      return "up";
    }

    if (/(DOWN|DPAD_DOWN)/.test(namedButton)) {
      return "down";
    }

    if (/\b(A|CROSS|SOUTH)\b/.test(namedButton)) {
      return "a";
    }

    if (/\b(B|CIRCLE|EAST)\b/.test(namedButton)) {
      return "b";
    }

    if (/\b(Y|TRIANGLE|NORTH)\b/.test(namedButton)) {
      return "y";
    }

    switch (Number(button)) {
      case 1:
        return "a";
      case 2:
        return "b";
      case 4:
        return "y";
      case 9:
        return "up";
      case 10:
        return "down";
      default:
        return "";
    }
  }

  function shouldHandleHomeReorderSteamButton(button, action) {
    const now = Date.now();
    const repeatMs = action === "up" || action === "down" ? 180 : 320;
    const lastMs = state.homeReorder.catchAllButtonState[button] || 0;
    if (now - lastMs < repeatMs) {
      return false;
    }

    state.homeReorder.catchAllButtonState[button] = now;
    return true;
  }

  function ensureHomeReorderHotkeys() {
    if (state.homeReorder.hotkeysInstalled) {
      return;
    }

    document.addEventListener("keydown", (event) => {
      if (!state.panelVisible || state.route.screen !== "root") {
        return;
      }

      if (state.homeReorder.active) {
        const isUp = event.key === "ArrowUp";
        const isDown = event.key === "ArrowDown";
        const isCancel = event.key === "Escape" || event.key === "Backspace";
        const isConfirm = isHomeReorderConfirmKey(event);

        if (!isUp && !isDown && !isCancel && !isConfirm) {
          return;
        }

        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation?.();

        if (isUp) {
          moveHomeReorderSelection(-1);
          return;
        }

        if (isDown) {
          moveHomeReorderSelection(1);
          return;
        }

        if (isCancel) {
          cancelHomeReorder();
          return;
        }

        void commitHomeReorder();
        return;
      }

      if (!isHomeReorderActivateKey(event) || event.repeat) {
        return;
      }

      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      startHomeReorderFromFocusedPlugin("keyboard Y");
    }, true);

    state.homeReorder.hotkeysInstalled = true;
  }

  function installHomeReorderCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || state.homeReorder.catchAllInstalled) {
      return;
    }

    const previous = focusNav.m_fnCatchAllGamepadInput;
    const callback = (button) => {
      const action = getHomeReorderActionFromSteamButton(button);
      const homeVisible = state.panelVisible && state.route.screen === "root";

      if (!homeVisible) {
        return typeof previous === "function" ? previous(button) : false;
      }

      if (!action) {
        return typeof previous === "function" ? previous(button) : false;
      }

      setHomeReorderDebug("catch-all", `${String(button)} -> ${action}`);

      if (!shouldHandleHomeReorderSteamButton(button, action)) {
        return true;
      }

      if (state.homeReorder.active) {
        if (action === "up") {
          moveHomeReorderSelection(-1);
          return true;
        }

        if (action === "down") {
          moveHomeReorderSelection(1);
          return true;
        }

        if (action === "a") {
          void commitHomeReorder();
          return true;
        }

        if (action === "b") {
          cancelHomeReorder();
          return true;
        }

        return typeof previous === "function" ? previous(button) : false;
      }

      if (action === "y" && !state.generalSettings.loading && !state.generalSettings.saving) {
        if (startHomeReorderFromFocusedPlugin("catch-all Y")) {
          return true;
        }
      }

      return typeof previous === "function" ? previous(button) : false;
    };

    callback.__steamToolsHomeReorderCatchAll = true;
    state.homeReorder.previousCatchAllGamepadInput =
      previous?.__steamToolsHomeReorderCatchAll ? null : previous;
    focusNav.SetCatchAllGamepadInput(callback);
    state.homeReorder.catchAllInstalled = true;
  }

  function uninstallHomeReorderCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || !state.homeReorder.catchAllInstalled) {
      return;
    }

    if (focusNav.m_fnCatchAllGamepadInput?.__steamToolsHomeReorderCatchAll) {
      focusNav.SetCatchAllGamepadInput(state.homeReorder.previousCatchAllGamepadInput || undefined);
    }

    state.homeReorder.catchAllInstalled = false;
    state.homeReorder.previousCatchAllGamepadInput = null;
    resetHomeReorderArmState();
  }

  function updateHomeReorderInputCapture() {
    if (state.panelVisible) {
      installHomeReorderCatchAllInput();
      return;
    }

    uninstallHomeReorderCatchAllInput();
  }

  function consumeVolumeActionAutoFocus() {
    const shouldFocus = state.audio.pendingVolumeActionAutoFocus;
    state.audio.pendingVolumeActionAutoFocus = false;
    return shouldFocus;
  }

  function rememberVolumeActionFocus(index) {
    if (index !== 0 && state.audio.sliderEditActive) {
      finishVolumeSliderEditing(true);
    }

    if (index !== 0 && state.performance.sliderEditActive) {
      finishPerformanceSliderEditing(true);
    }

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

  function createFallbackVolumeSliderContent(slider, options = {}) {
    const step = Number.isFinite(slider.step) && slider.step > 0 ? slider.step : 10;
    const min = Number.isFinite(slider.min) ? slider.min : 0;
    const max = Number.isFinite(slider.max) ? slider.max : 100;
    const range = Math.max(1, max - min);
    const notchCount = Number.isInteger(slider.notchCount) && slider.notchCount > 1 ? slider.notchCount : 11;
    const value = Math.max(min, Math.min(max, Math.round(Number(slider.value) || 0)));
    const percent = ((value - min) / range) * 100;
    const showHead = options.showHead !== false;
    const valueText =
      typeof slider.displayValue === "string" && slider.displayValue.length > 0
        ? slider.displayValue
        : `${value}${slider.valueSuffix || ""}`;

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-volume-slider-fallback-shell" },
        showHead
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-volume-slider-fallback-head" },
                createElement("div", {
                  className: "steamloader-volume-slider-label",
                  children: slider.title,
                }),
                createElement("div", {
                  className: "steamloader-volume-slider-value",
                  children: valueText,
                }),
              ),
            )
          : createElement("div", {
              className: "steamloader-volume-slider-value",
              children: valueText,
            }),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-volume-slider-track-shell", "aria-hidden": "true" },
            createElement("div", {
              className: "steamloader-volume-slider-track",
            }),
            ...Array.from({ length: notchCount }, (_, index) =>
              createElement("span", {
                key: `volume-slider-notch-${index}`,
                className: "steamloader-volume-slider-notch",
                style: {
                  left: `${(index / Math.max(1, notchCount - 1)) * 100}%`,
                },
              }),
            ),
            createElement("div", {
              className: "steamloader-volume-slider-fill",
              style: {
                width: `${percent}%`,
              },
            }),
            createElement("div", {
              className: "steamloader-volume-slider-thumb",
              style: {
                left: `${percent}%`,
              },
            }),
          ),
        ),
      ),
    );
  }

  function createFallbackVolumeSlider(slider, shouldAutoFocusAction) {
    return NativeDialogButton(
      createFallbackVolumeSliderContent(slider),
      () => {
        rememberVolumeActionFocus(0);
        slider.onActivate?.();
      },
      {
        disabled: slider.disabled,
        className: `steamloader-dialog-button steamloader-volume-slider-fallback-button${slider.isEditing ? " is-editing" : ""}`,
        extraProps: {
          "data-volume-slider": "true",
          autoFocus: shouldAutoFocusAction && state.audio.activeVolumeActionIndex === 0,
          onGamepadFocus: () => {
            rememberVolumeActionFocus(0);
          },
          onCancelButton: () => {
            if (slider.isEditing) {
              slider.onDeactivate?.();
              return;
            }

            slider.onCancel?.();
          },
          onMoveLeft: (event) => {
            rememberVolumeActionFocus(0);
            slider.onMoveLeft?.(event);
          },
          onMoveRight: (event) => {
            rememberVolumeActionFocus(0);
            slider.onMoveRight?.(event);
          },
          style: {
            width: "100%",
            minWidth: 0,
            padding: "10px 12px",
          },
        },
      },
    );
  }

  function createVolumeSliderControl(slider, shouldAutoFocusAction) {
    return createFallbackVolumeSlider(slider, shouldAutoFocusAction);
  }

  function createPerformanceSliderSlotButton(slot, index, autoFocusIndex) {
    const panel = slot.panel;
    const slider = panel?.slider;
    if (!panel || !slider) {
      return null;
    }

    const shouldAutoFocus = Number.isInteger(autoFocusIndex) && autoFocusIndex === index;

    return NativeDialogButton(
      createElement(
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
          createElement(
            "div",
            withChildren(
              { className: "steamloader-volume-slider-wrap" },
              createFallbackVolumeSliderContent(slider),
            ),
          ),
          createElement("div", {
            className: panel.error
              ? "steamloader-volume-hint steamloader-volume-hint-error"
              : "steamloader-volume-hint",
            children: panel.error || panel.hint,
          }),
        ),
      ),
      () => {
        rememberVolumeActionFocus(0);
        slider.onActivate?.();
      },
      {
        disabled: slider.disabled,
        slotKey: slot.slotKey || `performance-slider-${index}`,
        className: `steamloader-dialog-button steamloader-volume-slider-fallback-button steamloader-performance-slider-button${slider.isEditing ? " is-editing" : ""}`,
        extraProps: {
          "data-slot-button": String(index),
          "data-volume-slider": "true",
          "data-performance-slider": "true",
          autoFocus: shouldAutoFocus,
          onGamepadFocus: () => {
            rememberCurrentRouteIndex(index);
            rememberVolumeActionFocus(0);
          },
          onCancelButton: () => {
            if (slider.isEditing) {
              slider.onDeactivate?.();
              return;
            }

            slider.onCancel?.();
          },
          onMoveLeft: (event) => {
            rememberCurrentRouteIndex(index);
            rememberVolumeActionFocus(0);
            slider.onMoveLeft?.(event);
            return true;
          },
          onMoveRight: (event) => {
            rememberCurrentRouteIndex(index);
            rememberVolumeActionFocus(0);
            slider.onMoveRight?.(event);
            return true;
          },
          style: {
            width: "100%",
            minWidth: 0,
            padding: "0",
          },
        },
      },
    );
  }

  function createPerformanceValueSliderSlotButton(slot, index, autoFocusIndex) {
    const panel = slot.panel;
    const slider = panel?.slider;
    if (!panel || !slider) {
      return null;
    }

    const shouldAutoFocus = Number.isInteger(autoFocusIndex) && autoFocusIndex === index;

    return NativeDialogButton(
      createElement(
        "div",
        withChildren(
          { className: "steamloader-volume-card" },
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
          createElement(
            "div",
            withChildren(
              { className: "steamloader-volume-slider-wrap" },
              createFallbackVolumeSliderContent(slider, { showHead: false }),
            ),
          ),
          createElement("div", {
            className: panel.error
              ? "steamloader-volume-hint steamloader-volume-hint-error"
              : "steamloader-volume-hint",
            children: panel.error || panel.hint || "Use Left / Right to adjust this value.",
          }),
        ),
      ),
      () => {
        rememberCurrentRouteIndex(index);
        panel.onClick?.();
      },
      {
        disabled: slider.disabled,
        slotKey: slot.slotKey || `performance-value-slider-${index}`,
        className: "steamloader-dialog-button steamloader-performance-slider-button",
        extraProps: {
          "data-slot-button": String(index),
          "data-performance-slider": "true",
          autoFocus: shouldAutoFocus,
          onGamepadFocus: () => {
            rememberCurrentRouteIndex(index);
          },
          onMoveLeft: (event) => {
            rememberCurrentRouteIndex(index);
            slider.onMoveLeft?.(event);
            return true;
          },
          onMoveRight: (event) => {
            rememberCurrentRouteIndex(index);
            slider.onMoveRight?.(event);
            return true;
          },
          onCancelButton: () => {
            slider.onCancel?.();
          },
          style: {
            width: "100%",
            minWidth: 0,
            padding: "0",
          },
        },
      },
    );
  }

  function createVolumePanel(panel) {
    const shouldAutoFocusAction = consumeVolumeActionAutoFocus();
    const hasSlider = Boolean(panel.slider);
    const hasActions = Array.isArray(panel.actions) && panel.actions.length > 0;

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
        hasSlider
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-volume-slider-wrap" },
                createVolumeSliderControl(panel.slider, shouldAutoFocusAction),
              ),
            )
          : null,
        createElement("div", {
          className: panel.error
            ? "steamloader-volume-hint steamloader-volume-hint-error"
            : "steamloader-volume-hint",
          children: panel.error || panel.hint,
        }),
        hasActions ? createDivider("volume-panel-actions-divider") : null,
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
                hasSlider ? index + 1 : index,
              ),
            ),
          ),
        ),
      ),
    );
  }

  function createAudioDashboardButton(control, content, className, autoFocusIndex) {
    const shouldAutoFocus = Number.isInteger(autoFocusIndex) && autoFocusIndex === control.index;

    return NativeDialogButton(
      content,
      () => {
        rememberCurrentRouteIndex(control.index);
        control.onClick?.();
      },
      {
        disabled: control.disabled,
        slotKey: control.slotKey || `audio-dashboard-${control.index}`,
        className,
        extraProps: {
          "data-slot-button": String(control.index),
          autoFocus: shouldAutoFocus,
          onGamepadFocus: () => {
            rememberCurrentRouteIndex(control.index);
          },
          onMoveLeft: control.onMoveLeft
            ? (event) => {
                rememberCurrentRouteIndex(control.index);
                control.onMoveLeft?.(event);
                return true;
              }
            : undefined,
          onMoveRight: control.onMoveRight
            ? (event) => {
                rememberCurrentRouteIndex(control.index);
                control.onMoveRight?.(event);
                return true;
              }
            : undefined,
          onCancelButton: () => {
            navigateBackFromRoute();
          },
          style: {
            width: "100%",
            minWidth: 0,
          },
        },
      },
    );
  }

  function createAudioDashboardQuickButton(control, autoFocusIndex) {
    const ControlIcon = control.icon || AudioPluginIcon;

    return createAudioDashboardButton(
      control,
      createElement(
        "div",
        withChildren(
          { className: "steamloader-audio-quick-shell" },
          createElement(
            "div",
            withChildren(
              { className: "steamloader-audio-quick-icon" },
              createElement(ControlIcon, {}),
            ),
          ),
          createElement("div", {
            className: "steamloader-audio-quick-title",
            children: control.title,
          }),
        ),
      ),
      `steamloader-dialog-button steamloader-audio-quick-button${control.active ? " is-active" : ""}`,
      autoFocusIndex,
    );
  }

  function createAudioDashboardSliderButton(control, autoFocusIndex, className = "steamloader-dialog-button steamloader-audio-slider-button") {
    const slider = {
      title: control.title,
      value: control.value,
      min: control.min ?? 0,
      max: control.max ?? 100,
      step: control.step ?? 5,
      notchCount: control.notchCount ?? 21,
      displayValue: control.displayValue,
      valueSuffix: control.valueSuffix || "%",
    };

    return createAudioDashboardButton(
      control,
      createElement(
        "div",
        withChildren(
          { className: "steamloader-audio-slider-card" },
          createFallbackVolumeSliderContent(slider),
          control.copy
            ? createElement("div", {
                className: "steamloader-audio-slider-copy",
                children: control.copy,
              })
            : null,
        ),
      ),
      className,
      autoFocusIndex,
    );
  }

  function createAudioDashboardSelectorButton(control, autoFocusIndex) {
    return createAudioDashboardButton(
      control,
      createElement(
        "div",
        withChildren(
          { className: "steamloader-audio-selector-card" },
          createElement("div", {
            className: "steamloader-audio-selector-label",
            children: control.label,
          }),
          createElement(
            "div",
            withChildren(
              { className: "steamloader-audio-selector-value-row" },
              createElement("div", {
                className: "steamloader-audio-selector-value",
                children: control.value,
              }),
              createElement(
                "div",
                withChildren(
                  { className: "steamloader-audio-selector-icon" },
                  createElement(ChevronIcon, {}),
                ),
              ),
            ),
          ),
          control.copy
            ? createElement("div", {
                className: "steamloader-audio-selector-copy",
                children: control.copy,
              })
            : null,
        ),
      ),
      "steamloader-dialog-button steamloader-audio-selector-button",
      autoFocusIndex,
    );
  }

  function createAudioDashboardCommandButton(control, autoFocusIndex) {
    return createAudioDashboardButton(
      control,
      createElement(
        "div",
        withChildren(
          { className: "steamloader-audio-selector-card" },
          createElement("div", {
            className: "steamloader-audio-selector-value",
            children: control.title,
          }),
          control.copy
            ? createElement("div", {
                className: "steamloader-audio-selector-copy",
                children: control.copy,
              })
            : null,
        ),
      ),
      "steamloader-dialog-button steamloader-audio-selector-button",
      autoFocusIndex,
    );
  }

  function createAudioDashboard(dashboard) {
    const autoFocusIndex = Number.isInteger(dashboard?.autoFocusIndex) ? dashboard.autoFocusIndex : null;
    const mixerControls = Array.isArray(dashboard?.mixerControls) ? dashboard.mixerControls : [];

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-audio-dashboard" },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-audio-card" },
            createElement(
              "div",
              withChildren(
                { className: "steamloader-audio-quick-grid" },
                createAudioDashboardQuickButton(dashboard.playbackToggle, autoFocusIndex),
                createAudioDashboardQuickButton(dashboard.captureToggle, autoFocusIndex),
              ),
            ),
            createDivider("audio-dashboard-quick-divider"),
            createElement(
              "div",
              withChildren(
                { className: "steamloader-audio-slider-stack" },
                createAudioDashboardSliderButton(dashboard.playbackSlider, autoFocusIndex),
                createAudioDashboardSliderButton(dashboard.captureSlider, autoFocusIndex),
              ),
            ),
          ),
        ),
        createDivider("audio-dashboard-device-divider"),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-audio-card" },
            createElement("div", {
              className: "steamloader-audio-card-title",
              children: "Default Devices",
            }),
            createElement(
              "div",
              withChildren(
                { className: "steamloader-audio-selector-stack" },
                createAudioDashboardSelectorButton(dashboard.playbackSelector, autoFocusIndex),
                createAudioDashboardSelectorButton(dashboard.captureSelector, autoFocusIndex),
              ),
            ),
          ),
        ),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-audio-card" },
            createElement(
              "div",
              withChildren(
                { className: "steamloader-audio-mixer-header" },
                createElement("div", {
                  className: "steamloader-audio-card-title",
                  children: "Volume Mixer",
                }),
                createElement("div", {
                  className: "steamloader-audio-card-copy",
                  children: dashboard.mixerSummary,
                }),
              ),
            ),
            mixerControls.length
              ? createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-audio-mixer-stack" },
                    ...mixerControls.map((control) =>
                      createAudioDashboardSliderButton(
                        control,
                        autoFocusIndex,
                        "steamloader-dialog-button steamloader-audio-mixer-button",
                      ),
                    ),
                  ),
                )
              : createElement("div", {
                  className: "steamloader-audio-empty-state",
                  children: dashboard.emptyMixerText,
              }),
          ),
        ),
        createDivider("audio-dashboard-refresh-divider"),
        createAudioDashboardCommandButton(dashboard.refreshControl, autoFocusIndex),
      ),
    );
  }

  function createFrontendRenderHelpers() {
    return {
      DefaultIcon: SteamLoaderIcon,
      BackIcon,
      ChevronIcon,
      getBackNavigation,
      handleSlotClick,
      navigateBackFromRoute,
      consumeResolvedFocus,
      consumeVolumeActionAutoFocus,
      rememberVolumeActionFocus,
      getActiveVolumeActionIndex: () => state.audio.activeVolumeActionIndex,
    };
  }

  function SteamLoaderPanelShell() {
    let model = buildScreenModel();
    const forceCustomShell = isPerformanceOverlayRoute() || isAudioDashboardRoute();

    if (!forceCustomShell && window.STFrontendLib?.createPanelShell) {
      try {
        return window.STFrontendLib.createPanelShell(
          state,
          createElement,
          withChildren,
          model,
          createFrontendRenderHelpers(),
        );
      } catch (error) {
        state.nativeUi.renderError = error instanceof Error ? error.message : String(error);
        console.warn("[Tools for Steam] Recovered from st-frontend-lib render error.", error);
        model = {
          ...model,
          error: model.error || "Tools for Steam recovered from an internal UI renderer error.",
        };
      }
    }

    const HeaderIcon = model.headerIcon === null ? null : model.headerIcon || SteamLoaderIcon;
    const headerActions = Array.isArray(model.headerActions) ? model.headerActions : [];
    const resolvedAutoFocusIndex =
      Number.isInteger(model.autoFocusIndex)
        ? model.autoFocusIndex
        : Number.isInteger(model.audioDashboard?.autoFocusIndex)
          ? model.audioDashboard.autoFocusIndex
          : null;
    state.slotActions = model.slots.map((slot) => slot.onClick);
    consumeResolvedFocus(state.route, resolvedAutoFocusIndex);
    const slotChildren = model.slots.flatMap((slot, index) => {
      const children = [createButtonSlot(slot, index, model.autoFocusIndex)];
      if (hasDividerAfter(model, index)) {
        children.push(createDivider(`divider-${index}`));
      }

      return children;
    });

    return createElement(
      "div",
      withChildren(
        {
          className: model.panelClassName
            ? `steamloader-panel ${model.panelClassName}`
            : "steamloader-panel",
        },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-header" },
            createElement(
              "div",
              withChildren(
                { className: "steamloader-header-main" },
                HeaderIcon
                  ? createElement(
                      "div",
                      withChildren({ className: "steamloader-header-mark" }, createElement(HeaderIcon, {})),
                    )
                  : null,
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
            headerActions.length
              ? createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-header-actions" },
                    ...headerActions
                      .map((action, index) =>
                        createHeaderActionButton({
                          ...action,
                          key: action.key || `header-action-${index}`,
                        }),
                      )
                      .filter(Boolean),
                  ),
                )
              : null,
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
        model.audioDashboard ? createAudioDashboard(model.audioDashboard) : null,
        model.editor ? createEditorCard(model.editor) : null,
        ...(Array.isArray(model.editors)
          ? model.editors.map((editor, index) => createEditorCard({ ...editor, inputKey: editor.inputKey || `editor-${index}` }))
          : []),
        model.volumePanel ? createVolumePanel(model.volumePanel) : null,
        createElement(
          "div",
          withChildren(
            { className: "steamloader-stack" },
            ...slotChildren,
          ),
        ),
        createFooterLegend(model.footerLegend),
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

    return {
      title: "System Volume",
      copy: getVolumePanelCopy(),
      error: state.audio.volumeError,
      hint: getVolumePanelHint(),
      slider: {
        title: "Main Slider",
        description: "",
        value: getVolumeValue(),
        min: 0,
        max: 100,
        step: 10,
        notchCount: 11,
        notchTicksVisible: true,
        showValue: true,
        disabled: !info,
        editableValue: true,
        validValues: "steps",
        valueSuffix: "%",
        minimumDpadGranularity: 10,
        isEditing: state.audio.sliderEditActive,
        onCancel: () => {
          navigateBackFromRoute();
        },
        onActivate: () => {
          startVolumeSliderEditing();
        },
        onDeactivate: () => {
          finishVolumeSliderEditing(true);
        },
        onMoveLeft: () => {
          if (!state.audio.sliderEditActive) {
            startVolumeSliderEditing();
          }

          const nextValue = getVolumeValue() - 10;
          previewSliderVolume(nextValue);
          queueSliderVolumeCommit(nextValue);
        },
        onMoveRight: () => {
          if (!state.audio.sliderEditActive) {
            startVolumeSliderEditing();
          }

          const nextValue = getVolumeValue() + 10;
          previewSliderVolume(nextValue);
          queueSliderVolumeCommit(nextValue);
        },
      },
      actions: [
        {
          title: info?.isMuted ? "Unmute" : "Mute",
          disabled: state.audio.volumeLoading || !info,
          onCancel: () => {
            navigateBackFromRoute();
          },
          onClick: () => {
            rememberVolumeActionFocus(1);
            finishVolumeSliderEditing(false);
            toggleMute();
          },
        },
      ],
    };
  }

  function buildPerformancePanelModel() {
    const installation = getPerformanceInstallation();
    const settings = getPerformanceSettings();
    const levels = getPerformanceLevelDefinitions();
    const sliderEnabled = Boolean(levels.length);
    const elevatedHelperReady = installation?.elevatedHelperReady !== false;

    return {
      title: "TFS FPS Overlay",
      copy: getPerformancePanelCopy(),
      error: state.performance.error,
      hint: getPerformancePanelHint(),
      slider: {
        title: "Overlay Level",
        description: "",
        value: getPerformanceDraftLevel(),
        min: 0,
        max: Math.max(1, levels.length - 1),
        step: 1,
        notchCount: Math.max(2, levels.length || 3),
        notchTicksVisible: true,
        showValue: true,
        displayValue: getPerformanceLevelDisplayText(),
        disabled: !sliderEnabled || isPerformanceBusy(),
        editableValue: true,
        validValues: "steps",
        minimumDpadGranularity: 1,
        isEditing: state.performance.sliderEditActive,
        onCancel: () => {
          navigateBackFromRoute();
        },
        onActivate: () => {
          startPerformanceSliderEditing();
        },
        onDeactivate: () => {
          finishPerformanceSliderEditing(true);
        },
        onMoveLeft: () => {
          movePerformanceSlider(-1);
        },
        onMoveRight: () => {
          movePerformanceSlider(1);
        },
      },
      actions: [
        {
          title: elevatedHelperReady
            ? installation?.running ? "Restart TFS FPS Overlay" : "Start TFS FPS Overlay"
            : "Prepare Elevated Helper",
          disabled: isPerformanceBusy(),
          onClick: () => {
            rememberVolumeActionFocus(1);
            finishPerformanceSliderEditing(false);
            if (elevatedHelperReady) {
              void startPerformanceOverlay();
              return;
            }

            void preparePerformanceElevatedHelper();
          },
        },
        {
          title: elevatedHelperReady ? "Stop TFS FPS Overlay" : "Repair Elevated Helper",
          disabled: elevatedHelperReady
            ? isPerformanceBusy() || !installation?.running
            : isPerformanceBusy(),
          onClick: () => {
            rememberVolumeActionFocus(2);
            finishPerformanceSliderEditing(false);
            if (elevatedHelperReady) {
              void stopPerformanceOverlay();
              return;
            }

            void preparePerformanceElevatedHelper();
          },
        },
        {
          title: settings?.autoTargetEnabled ? "Disable Auto Target" : "Enable Auto Target",
          disabled: isPerformanceBusy(),
          onClick: () => {
            rememberVolumeActionFocus(3);
            finishPerformanceSliderEditing(false);
            void togglePerformanceAutoTarget();
          },
        },
        {
          title: "Refresh State",
          disabled: isPerformanceBusy(),
          onClick: () => {
            rememberVolumeActionFocus(4);
            finishPerformanceSliderEditing(false);
            void loadPerformanceState();
          },
        },
      ],
    };
  }

  function createPerformanceSliderSlot(panel) {
    return {
      title: panel?.title || "TFS FPS Overlay",
      copy: panel?.copy || "",
      onClick: () => {
        rememberVolumeActionFocus(0);
        panel?.slider?.onActivate?.();
      },
      disabled: Boolean(panel?.slider?.disabled),
      trailing: "none",
      slotKey: "performance-slider",
      forceFallback: true,
      customRenderer: createPerformanceSliderSlotButton,
      panel,
    };
  }

  function createPerformanceValueSliderSlot(options) {
    const min = Number.isFinite(options.min) ? options.min : 0;
    const max = Number.isFinite(options.max) ? options.max : 100;
    const step = Number.isFinite(options.step) && options.step > 0 ? options.step : 1;
    const value = Math.max(min, Math.min(max, Number(options.getValue?.() ?? min)));
    const notchCount = Math.max(2, Math.round((max - min) / step) + 1);

    return {
      title: options.title,
      copy: options.copy,
      onClick: () => {},
      disabled: Boolean(options.disabled),
      trailing: "none",
      slotKey: options.slotKey,
      forceFallback: true,
      customRenderer: createPerformanceValueSliderSlotButton,
      panel: {
        title: options.title,
        copy: options.copy,
        hint: options.hint || "",
        error: "",
        onClick: options.onClick || null,
        slider: {
          title: options.title,
          value,
          min,
          max,
          step,
          notchCount,
          displayValue: options.displayValue ? options.displayValue(value) : `${value}`,
          disabled: Boolean(options.disabled),
          onCancel: () => {
            navigateBackFromRoute();
          },
          onMoveLeft: () => {
            options.onAdjust?.(-1);
          },
          onMoveRight: () => {
            options.onAdjust?.(1);
          },
        },
      },
    };
  }

  function createPerformanceOptionSliderSlot(options) {
    const optionList = Array.isArray(options.options) ? options.options : [];
    const fallbackOption = optionList[0] || { value: 0, title: "Off" };
    const currentValue = options.getValue?.() ?? fallbackOption.value;
    const currentIndex = Math.max(0, optionList.findIndex((option) => option.value === currentValue));
    const activeOption = optionList[currentIndex] || fallbackOption;

    return createPerformanceValueSliderSlot({
      title: options.title,
      copy: options.copy,
      hint: options.hint || "Use Left / Right to switch this setting live.",
      slotKey: options.slotKey,
      min: 0,
      max: Math.max(0, optionList.length - 1),
      step: 1,
      disabled: Boolean(options.disabled) || optionList.length <= 1,
      getValue: () => {
        const liveValue = options.getValue?.() ?? fallbackOption.value;
        const liveIndex = optionList.findIndex((option) => option.value === liveValue);
        return liveIndex >= 0 ? liveIndex : currentIndex;
      },
      displayValue: (index) => {
        const selectedOption = optionList[Math.max(0, Math.min(optionList.length - 1, index))] || activeOption;
        return selectedOption.title;
      },
      onAdjust: (direction) => {
        const liveValue = options.getValue?.() ?? fallbackOption.value;
        void cyclePerformanceOptionSetting(options.settingKey, liveValue, optionList, direction);
      },
    });
  }

  function buildAudioMixerSlots(makeCommandSlot, sessions) {
    const sliderSlots = sessions.map((session) =>
      createPerformanceValueSliderSlot({
        title: session.displayName,
        copy: getAudioMixerSessionCopy(session),
        hint: getAudioMixerSessionHint(session),
        slotKey: `audio-mixer-${session.sessionId}`,
        min: 0,
        max: 100,
        step: 5,
        getValue: () => {
          const currentSession = findAudioMixerSession(session.sessionId) || session;
          return snapAudioMixerVolumeToStep(currentSession.volume);
        },
        displayValue: () => {
          const currentSession = findAudioMixerSession(session.sessionId) || session;
          return getAudioMixerSessionDisplayValue(currentSession);
        },
        onAdjust: (direction) => {
          adjustAudioMixerSessionVolume(session.sessionId, direction, 5);
        },
        onClick: () => {
          void toggleAudioMixerSessionMute(session.sessionId);
        },
      }),
    );

    return [
      ...sliderSlots,
      makeCommandSlot(
        "Refresh Sessions",
        "Reload active app sessions on the current playback device.",
        () => {
          void loadAudioMixerSessions();
        },
        {
          disabled: state.audio.mixerLoading,
        },
      ),
    ];
  }

  function buildAudioDashboardModel() {
    const playbackDevice = getCurrentPlaybackDevice();
    const captureDevice = getCurrentCaptureDevice();
    const mixerSessions = getAudioMixerSessions();
    const autoFocusIndex = resolveAutoFocusIndex(state.route) ?? 0;
    const mixerStartIndex = 6;

    return {
      autoFocusIndex,
      mixerSummary: resolveAudioMixerStatusText(),
      emptyMixerText: "Start a game, browser tab, or media app and its audio session will appear here automatically.",
      playbackToggle: {
        index: 0,
        title: state.audio.volumeInfo?.isMuted ? "Speakers Off" : "Speakers On",
        icon: state.audio.volumeInfo?.isMuted ? AudioMuteIcon : AudioPluginIcon,
        active: !state.audio.volumeInfo?.isMuted,
        disabled: !state.audio.volumeInfo || state.audio.dashboardLoading || state.audio.volumeLoading,
        onClick: () => {
          void toggleMute();
        },
      },
      captureToggle: {
        index: 1,
        title: state.audio.captureVolumeInfo?.isMuted ? "Microphone Off" : "Microphone On",
        icon: state.audio.captureVolumeInfo?.isMuted ? MicrophoneMuteIcon : MicrophoneIcon,
        active: !state.audio.captureVolumeInfo?.isMuted,
        disabled: !state.audio.captureVolumeInfo || state.audio.dashboardLoading || state.audio.captureVolumeLoading,
        onClick: () => {
          void toggleCaptureMute();
        },
      },
      playbackSlider: {
        index: 2,
        title: "System Volume",
        value: getVolumeValue(),
        displayValue: state.audio.volumeInfo?.isMuted ? "Muted" : `${getVolumeValue()}%`,
        copy: playbackDevice?.name || state.audio.volumeInfo?.deviceName || "No playback device detected.",
        step: 10,
        notchCount: 11,
        disabled: !state.audio.volumeInfo || state.audio.dashboardLoading,
        onClick: () => {
          void toggleMute();
        },
        onMoveLeft: () => {
          const nextValue = getVolumeValue() - 10;
          const snappedValue = snapVolumeToStep(nextValue);
          previewSliderVolume(snappedValue);
          queueSliderVolumeCommit(snappedValue);
        },
        onMoveRight: () => {
          const nextValue = getVolumeValue() + 10;
          const snappedValue = snapVolumeToStep(nextValue);
          previewSliderVolume(snappedValue);
          queueSliderVolumeCommit(snappedValue);
        },
      },
      captureSlider: {
        index: 3,
        title: "Microphone Volume",
        value: getCaptureVolumeValue(),
        displayValue: state.audio.captureVolumeInfo?.isMuted ? "Muted" : `${getCaptureVolumeValue()}%`,
        copy: captureDevice?.name || state.audio.captureVolumeInfo?.deviceName || "No microphone detected.",
        step: 10,
        notchCount: 11,
        disabled: !state.audio.captureVolumeInfo || state.audio.dashboardLoading,
        onClick: () => {
          void toggleCaptureMute();
        },
        onMoveLeft: () => {
          const nextValue = getCaptureVolumeValue() - 10;
          previewCaptureSliderVolume(nextValue);
          queueCaptureSliderVolumeCommit(nextValue);
        },
        onMoveRight: () => {
          const nextValue = getCaptureVolumeValue() + 10;
          previewCaptureSliderVolume(nextValue);
          queueCaptureSliderVolumeCommit(nextValue);
        },
      },
      playbackSelector: {
        index: 4,
        label: "Output Device",
        value: playbackDevice?.name || "No playback device",
        copy: "Press A or Left / Right to switch the Windows default speaker device.",
        disabled: !getAudioPlaybackDevices().length || state.audio.dashboardLoading || state.audio.loading,
        onClick: () => {
          void cyclePlaybackDevice(1);
        },
        onMoveLeft: () => {
          void cyclePlaybackDevice(-1);
        },
        onMoveRight: () => {
          void cyclePlaybackDevice(1);
        },
      },
      captureSelector: {
        index: 5,
        label: "Input Device",
        value: captureDevice?.name || "No microphone device",
        copy: "Press A or Left / Right to switch the Windows default microphone.",
        disabled: !getAudioCaptureDevices().length || state.audio.dashboardLoading || state.audio.loading,
        onClick: () => {
          void cycleCaptureDevice(1);
        },
        onMoveLeft: () => {
          void cycleCaptureDevice(-1);
        },
        onMoveRight: () => {
          void cycleCaptureDevice(1);
        },
      },
      mixerControls: mixerSessions.map((session, index) => ({
        index: mixerStartIndex + index,
        title: session.displayName,
        value: snapAudioMixerVolumeToStep(session.volume),
        displayValue: session.isMuted ? "Muted" : `${snapAudioMixerVolumeToStep(session.volume)}%`,
        copy: getAudioMixerSessionCopy(session),
        disabled: state.audio.dashboardLoading || state.audio.mixerLoading,
        onClick: () => {
          void toggleAudioMixerSessionMute(session.sessionId);
        },
        onMoveLeft: () => {
          adjustAudioMixerSessionVolume(session.sessionId, -1, 5);
        },
        onMoveRight: () => {
          adjustAudioMixerSessionVolume(session.sessionId, 1, 5);
        },
      })),
      refreshControl: {
        index: mixerStartIndex + mixerSessions.length,
        title: "Refresh Audio",
        copy: "Reload devices, microphone state, and the live mixer.",
        disabled: state.audio.dashboardLoading,
        onClick: () => {
          void loadAudioDashboardState();
        },
      },
    };
  }

  function markPerformanceOverlaySlots(slots) {
    return (Array.isArray(slots) ? slots : []).map((slot) => {
      if (!slot || slot.forceFallback) {
        return slot;
      }

      return {
        ...slot,
        forceFallback: true,
      };
    });
  }

  function buildPerformanceOverviewSlots(makeCommandSlot) {
    return [];
  }

  function buildPerformanceTfsSettingSlots(makeCommandSlot, makeToggleSlot, options = {}) {
    const installation = getPerformanceInstallation();
    const settings = getPerformanceSettings();
    const includeQuickActions = options.includeQuickActions !== false;
    const elevatedHelperReady = installation?.elevatedHelperReady !== false;

    const quickActionSlots = [
      makeCommandSlot(
        elevatedHelperReady
          ? installation?.running ? "Restart TFS FPS Overlay" : "Start TFS FPS Overlay"
          : "Prepare Elevated Helper",
        elevatedHelperReady
          ? "Launch the built-in TFS helper and overlay."
          : "Run the one-time Windows admin setup for the silent elevated helper.",
        () => {
          if (elevatedHelperReady) {
            void startPerformanceOverlay();
            return;
          }

          void preparePerformanceElevatedHelper();
        },
        {
          disabled: isPerformanceBusy(),
        },
      ),
      makeCommandSlot(
        elevatedHelperReady ? "Stop TFS FPS Overlay" : "Repair Elevated Helper",
        elevatedHelperReady
          ? "Stop the TFS overlay."
          : "Rebuild the one-time Windows admin setup for the silent helper.",
        () => {
          if (elevatedHelperReady) {
            void stopPerformanceOverlay();
            return;
          }

          void preparePerformanceElevatedHelper();
        },
        {
          disabled: elevatedHelperReady
            ? isPerformanceBusy() || !installation?.running
            : isPerformanceBusy(),
        },
      ),
      makeCommandSlot(
        settings?.autoTargetEnabled ? "Disable Auto Target" : "Enable Auto Target",
        "Follow the active game automatically.",
        () => {
          void togglePerformanceAutoTarget();
        },
        {
          badge: settings?.autoTargetEnabled ? "On" : "Off",
          disabled: isPerformanceBusy(),
        },
      ),
    ];

    const advancedSettingSlots = [
      createPerformanceOptionSliderSlot({
        title: "Position",
        copy: "Move the overlay to another corner without leaving the panel.",
        hint: "Use Left / Right to switch corners live.",
        slotKey: "performance-overlay-position",
        options: performancePositionOptions,
        settingKey: "overlay-position",
        disabled: isPerformanceBusy(),
        getValue: () => getPerformancePosition(),
      }),
      createPerformanceValueSliderSlot({
        title: "Overlay Width",
        copy: "Widen or tighten the overlay footprint on screen.",
        hint: "Use Left / Right to resize the overlay live.",
        slotKey: "performance-overlay-width",
        min: 200,
        max: 1920,
        step: 40,
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceOverlayWidth(),
        displayValue: (value) => `${value} px`,
        onAdjust: (direction) => {
          void adjustPerformanceNumberSetting(
            "overlay-width",
            getPerformanceOverlayWidth(),
            direction,
            40,
            200,
            1920,
          );
        },
      }),
      createPerformanceValueSliderSlot({
        title: "Overlay Scale",
        copy: "Scale up the TFS meter without changing where it sits on screen.",
        hint: "Use Left / Right to resize the whole overlay live.",
        slotKey: "performance-overlay-scale",
        min: 80,
        max: 160,
        step: 10,
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceOverlayScale(),
        displayValue: (value) => `${value}%`,
        onAdjust: (direction) => {
          void adjustPerformanceNumberSetting(
            "overlay-scale",
            getPerformanceOverlayScale(),
            direction,
            10,
            80,
            160,
          );
        },
      }),
      makeToggleSlot(
        "Graph Mode",
        "Toggle the live overlay graph on or off.",
        getPerformanceGraphMode() !== 0,
        () => {
          void setPerformanceSettingValue(
            "graph-mode",
            getPerformanceGraphMode() === 0 ? 1 : 0,
          );
        },
        {
          switchLabel: getPerformanceGraphMode() === 0 ? "Off" : "On",
          slotKey: "performance-graph-mode-toggle",
          disabled: isPerformanceBusy(),
          buttonProps: {
            onMoveLeft: () => {
              if (getPerformanceGraphMode() !== 0) {
                void setPerformanceSettingValue("graph-mode", 0);
              }
              return true;
            },
            onMoveRight: () => {
              if (getPerformanceGraphMode() === 0) {
                void setPerformanceSettingValue("graph-mode", 1);
              }
              return true;
            },
          },
        },
      ),
      createPerformanceOptionSliderSlot({
        title: "Background Theme",
        copy: "Switch the overlay background tint without opening another metrics app.",
        hint: "Use Left / Right to cycle the overlay theme live.",
        slotKey: "performance-background-theme",
        options: performanceBackgroundThemeOptions,
        settingKey: "background-theme",
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceBackgroundTheme(),
      }),
      createPerformanceValueSliderSlot({
        title: "Transparency",
        copy: "Control how visible the background plate should be behind the metrics.",
        hint: "Use Left / Right to change transparency live.",
        slotKey: "performance-background-opacity",
        min: 0,
        max: 100,
        step: 10,
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceBackgroundOpacity(),
        displayValue: (value) => `${value}%`,
        onAdjust: (direction) => {
          void adjustPerformanceNumberSetting(
            "background-opacity",
            getPerformanceBackgroundOpacity(),
            direction,
            10,
            0,
            100,
          );
        },
      }),
      createPerformanceValueSliderSlot({
        title: "Polling Rate",
        copy: "How often the TFS Overlay polls live metrics from the API.",
        hint: "Use Left / Right to change the metric polling rate.",
        slotKey: "performance-polling-rate",
        min: 10,
        max: 120,
        step: 10,
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceMetricPollRate(),
        displayValue: (value) => `${value} Hz`,
        onAdjust: (direction) => {
          void adjustPerformanceNumberSetting(
            "metric-poll-rate",
            getPerformanceMetricPollRate(),
            direction,
            10,
            10,
            120,
          );
        },
      }),
      createPerformanceValueSliderSlot({
        title: "Telemetry Period",
        copy: "Sets the service-side telemetry sampling interval.",
        hint: "Use Left / Right to change the telemetry interval.",
        slotKey: "performance-telemetry-period",
        min: 10,
        max: 500,
        step: 10,
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceTelemetrySamplingPeriodMs(),
        displayValue: (value) => `${value} ms`,
        onAdjust: (direction) => {
          void adjustPerformanceNumberSetting(
            "telemetry-period",
            getPerformanceTelemetrySamplingPeriodMs(),
            direction,
            10,
            10,
            500,
          );
        },
      }),
      createPerformanceValueSliderSlot({
        title: "Window Size",
        copy: "Controls the stats sample window used for averages and 99% values.",
        hint: "Use Left / Right to change the metrics window size.",
        slotKey: "performance-window-size",
        min: 100,
        max: 5000,
        step: 100,
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceMetricsWindow(),
        displayValue: (value) => `${value} ms`,
        onAdjust: (direction) => {
          void adjustPerformanceNumberSetting(
            "metrics-window",
            getPerformanceMetricsWindow(),
            direction,
            100,
            100,
            5000,
          );
        },
      }),
      createPerformanceValueSliderSlot({
        title: "Draw Rate",
        copy: "How often the overlay is redrawn after fresh metric updates.",
        hint: "Use Left / Right to change the draw rate live.",
        slotKey: "performance-draw-rate",
        min: 1,
        max: 120,
        step: 5,
        disabled: isPerformanceBusy(),
        getValue: () => getPerformanceOverlayDrawRate(),
        displayValue: (value) => `${value} Hz`,
        onAdjust: (direction) => {
          void adjustPerformanceNumberSetting(
            "overlay-draw-rate",
            getPerformanceOverlayDrawRate(),
            direction,
            5,
            1,
            120,
          );
        },
      }),
      makeCommandSlot(
        "Refresh State",
        "Reload overlay status.",
        () => {
          void loadPerformanceState();
        },
        {
          disabled: isPerformanceBusy(),
        },
      ),
    ];

    return includeQuickActions ? [...quickActionSlots, ...advancedSettingSlots] : advancedSettingSlots;
  }

  function getStoreSyncSnapshot() {
    return state.storeSync.snapshot;
  }

  function getStoreSyncPreview() {
    return getStoreSyncSnapshot()?.preview || null;
  }

  function getHltbSnapshot() {
    return state.hltb.snapshot;
  }

  function getDisplayModesSnapshot() {
    return state.display.modesSnapshot;
  }

  function isPerformanceBusy() {
    return state.performance.loading || state.performance.saving;
  }

  function resolvePerformanceStatusText() {
    if (state.performance.loading) {
      return "Loading TFS Overlay status...";
    }

    if (state.performance.saving) {
      return "Applying performance changes...";
    }

    return getPerformanceSnapshot()?.statusText || "TFS Overlay status is not available yet.";
  }

  function getAppStartSnapshot() {
    return state.appStart.snapshot;
  }

  function getAppStartCatalog() {
    return state.appStart.catalog;
  }

  function getAppStartShortcut(shortcutId) {
    const shortcuts = getAppStartSnapshot()?.shortcuts;
    return Array.isArray(shortcuts) ? shortcuts.find((shortcut) => shortcut.id === shortcutId) || null : null;
  }

  function getAppStartShortcutIndex(shortcutId) {
    const shortcuts = getAppStartSnapshot()?.shortcuts;
    if (!Array.isArray(shortcuts)) {
      return null;
    }

    const index = shortcuts.findIndex((shortcut) => shortcut.id === shortcutId);
    return index >= 0 ? index + 1 : null;
  }

  function getGeneralSettingsSnapshot() {
    return state.generalSettings.snapshot;
  }

  function getUpdateSnapshot() {
    return state.updates.snapshot;
  }

  function getUpdateChannel() {
    return getUpdateSnapshot()?.channel === "beta" ? "beta" : "stable";
  }

  function getUpdateChannelTitle(channel = getUpdateChannel()) {
    return channel === "beta" ? "Beta / Preview" : "Stable";
  }

  function getUpdateHeadline(snapshot = getUpdateSnapshot()) {
    if (!snapshot) {
      return "Updates unavailable.";
    }

    if (snapshot.installInProgress) {
      return formatUpdateInstallStatus(snapshot);
    }

    if (snapshot.updateAvailable && snapshot.latestVersion) {
      return snapshot.isPrerelease
        ? `Preview ${snapshot.latestVersion} is ready.`
        : `Release ${snapshot.latestVersion} is ready.`;
    }

    if (snapshot.latestVersion) {
      return snapshot.isPrerelease
        ? `Tracking preview ${snapshot.latestVersion}.`
        : `Tracking release ${snapshot.latestVersion}.`;
    }

    return "Release info not loaded yet.";
  }

  function formatUpdateInstallStatus(snapshot = getUpdateSnapshot()) {
    if (!snapshot) {
      return "Preparing the update...";
    }

    const progressText =
      typeof snapshot.installProgressPercent === "number"
        ? ` ${Math.max(0, Math.min(100, snapshot.installProgressPercent))}%`
        : "";
    return snapshot.message || `Please wait. Installing update...${progressText}`;
  }

  function getSplashScreenSettings() {
    return getGeneralSettingsSnapshot()?.splashScreen || null;
  }

  function syncSplashDraftsFromSnapshot(force = false) {
    const splash = getSplashScreenSettings();
    if (!splash) {
      return;
    }

    if (force || !state.generalSettings.splashWallpaperDraft) {
      state.generalSettings.splashWallpaperDraft = splash.wallpaperPath || "";
      state.generalSettings.splashWallpaperInputVersion += 1;
    }

    if (force || !state.generalSettings.splashIconDraft) {
      state.generalSettings.splashIconDraft = splash.iconPath || "";
      state.generalSettings.splashIconInputVersion += 1;
    }
  }

  function getAutoSisirSnapshot() {
    return state.autoSisir.snapshot;
  }

  function syncAutoSisirPathDraftFromSnapshot(force = false) {
    const path = getAutoSisirSnapshot()?.settings?.executablePath || "";
    if (force || !state.autoSisir.pathDraft) {
      state.autoSisir.pathDraft = path;
      state.autoSisir.pathInputVersion += 1;
    }
  }

  function getGeneralPluginSettings() {
    return getPluginSettings().filter((plugin) => plugin.canDisable !== false);
  }

  function getArtworkSnapshot() {
    return state.artwork.snapshot;
  }

  function getStoreSyncStore(storeId) {
    const stores = getStoreSyncSnapshot()?.stores;
    return Array.isArray(stores) ? stores.find((store) => store.id === storeId) || null : null;
  }

  function readStoreSyncPinnedTitleIds() {
    try {
      const rawValue = window.localStorage?.getItem(storeSyncPinnedTitlesStorageKey);
      if (!rawValue) {
        return {};
      }

      const parsedValue = JSON.parse(rawValue);
      const titleIds = Array.isArray(parsedValue) ? parsedValue : [];
      return Object.fromEntries(
        titleIds
          .filter((titleId) => typeof titleId === "string" && titleId.trim().length > 0)
          .map((titleId) => [titleId, true]),
      );
    } catch {
      return {};
    }
  }

  function saveStoreSyncPinnedTitleIds() {
    try {
      const pinnedTitleIds = Object.entries(state.storeSync.pinnedTitleIds || {})
        .filter(([, pinned]) => Boolean(pinned))
        .map(([titleId]) => titleId);
      window.localStorage?.setItem(storeSyncPinnedTitlesStorageKey, JSON.stringify(pinnedTitleIds));
    } catch {
    }
  }

  function isStoreSyncPinnedTitle(titleId) {
    return Boolean(titleId && state.storeSync.pinnedTitleIds?.[titleId]);
  }

  function setStoreSyncPinnedTitle(titleId, pinned) {
    if (!titleId) {
      return;
    }

    state.storeSync.pinnedTitleIds ||= {};
    if (pinned) {
      state.storeSync.pinnedTitleIds[titleId] = true;
    } else {
      delete state.storeSync.pinnedTitleIds[titleId];
    }

    saveStoreSyncPinnedTitleIds();
    rerenderStoreSyncPanel();
  }

  function compareStoreSyncTitles(left, right) {
    return (left?.title || "").localeCompare(right?.title || "", undefined, { sensitivity: "base" });
  }

  function getStoreSyncDetectedTitles() {
    const stores = getStoreSyncSnapshot()?.stores;
    if (!Array.isArray(stores)) {
      return [];
    }

    return stores
      .flatMap((store) =>
        Array.isArray(store.detectedTitles)
          ? store.detectedTitles.map((title) => ({
              ...title,
              storeTitle: title.storeTitle || store.title,
            }))
          : [],
      )
      .sort(compareStoreSyncTitles);
  }

  function getStoreSyncDetectedTitle(titleId) {
    return getStoreSyncDetectedTitles().find((title) => title.id === titleId) || null;
  }

  function getStoreSyncDetectedTitleIndex(titleId) {
    return getStoreSyncPreviewNavigationIndex(titleId);
  }

  function getStoreSyncTitleRouteId(route = state.route) {
    return route?.screen === "page" &&
      route.pluginId === "store-sync" &&
      typeof route.pageId === "string" &&
      route.pageId.startsWith("detected-title-")
      ? route.pageId.replace(/^detected-title-/, "")
      : "";
  }

  function getStoreSyncArtworkPreview(titleId) {
    return titleId ? state.storeSync.artworkPreviewByTitleId?.[titleId] || null : null;
  }

  function isStoreSyncArtworkPreviewLoading(titleId) {
    return Boolean(titleId && state.storeSync.artworkPreviewLoadingByTitleId?.[titleId]);
  }

  function clearStoreSyncArtworkPreview(titleId) {
    if (!titleId) {
      return;
    }

    state.storeSync.artworkPreviewByTitleId ||= {};
    state.storeSync.artworkPreviewLoadingByTitleId ||= {};
    delete state.storeSync.artworkPreviewByTitleId[titleId];
    delete state.storeSync.artworkPreviewLoadingByTitleId[titleId];
  }

  async function ensureStoreSyncArtworkPreview(titleId, forceReload = false) {
    if (!titleId) {
      return null;
    }

    state.storeSync.artworkPreviewByTitleId ||= {};
    state.storeSync.artworkPreviewLoadingByTitleId ||= {};

    if (forceReload) {
      delete state.storeSync.artworkPreviewByTitleId[titleId];
    }

    if (state.storeSync.artworkPreviewByTitleId[titleId]) {
      return state.storeSync.artworkPreviewByTitleId[titleId];
    }

    if (state.storeSync.artworkPreviewLoadingByTitleId[titleId]) {
      return null;
    }

    state.storeSync.artworkPreviewLoadingByTitleId[titleId] = true;
    rerenderStoreSyncPanel();

    try {
      const response = await fetch(
        `${apiBase}api/store-sync/titles/artwork-preview?titleId=${encodeURIComponent(titleId)}`,
        { cache: "no-store" },
      );
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Artwork preview could not be loaded (${response.status}).`);
      }

      state.storeSync.artworkPreviewByTitleId[titleId] = payload && typeof payload === "object" ? payload : null;
      return state.storeSync.artworkPreviewByTitleId[titleId];
    } catch (error) {
      state.storeSync.artworkPreviewByTitleId[titleId] = {
        titleId,
        available: false,
        usesCurrentArtwork: false,
        imageDataUri: "",
        sourceLabel: "SteamGridDB Preview",
        message: error instanceof Error ? error.message : String(error),
      };
      return state.storeSync.artworkPreviewByTitleId[titleId];
    } finally {
      state.storeSync.artworkPreviewLoadingByTitleId[titleId] = false;
      if (getStoreSyncTitleRouteId() === titleId) {
        rerenderStoreSyncPanel();
      }
    }
  }

  function buildStoreSyncArtworkPreviewCard(titleId) {
    if (!titleId) {
      return null;
    }

    const preview = getStoreSyncArtworkPreview(titleId);
    if (!preview && isStoreSyncArtworkPreviewLoading(titleId)) {
      return {
        title: "Artwork Preview",
        lines: ["Loading the current SteamGridDB preview..."],
      };
    }

    if (!preview) {
      return {
        title: "Artwork Preview",
        lines: ["Open this title for a moment and Tools for Steam will fetch the artwork preview here."],
      };
    }

    return {
      title: "Artwork Preview",
      imageSrc: preview.available ? preview.imageDataUri || "" : "",
      imageAlt: preview.sourceLabel || "Artwork Preview",
      lines: [preview.sourceLabel || "Artwork Preview", preview.message || ""].filter(Boolean),
    };
  }

  function syncStoreSyncTitleDraftsFromSnapshot(titleId, forceRemount = false) {
    if (!titleId) {
      return;
    }

    const detectedTitle = getStoreSyncDetectedTitle(titleId);
    if (!detectedTitle) {
      return;
    }

    const titleDrafts = state.storeSync.titleOverrideDraftById;
    const artworkDrafts = state.storeSync.artworkTitleOverrideDraftById;
    const excludedDrafts = state.storeSync.excludedDraftById;
    const titleInputVersions = state.storeSync.titleOverrideInputVersionById;
    const artworkInputVersions = state.storeSync.artworkTitleOverrideInputVersionById;

    if (forceRemount || typeof titleDrafts[titleId] !== "string") {
      titleDrafts[titleId] = detectedTitle.titleOverride || "";
      titleInputVersions[titleId] = (titleInputVersions[titleId] || 0) + 1;
    }

    if (forceRemount || typeof artworkDrafts[titleId] !== "string") {
      artworkDrafts[titleId] = detectedTitle.artworkTitleOverride || "";
      artworkInputVersions[titleId] = (artworkInputVersions[titleId] || 0) + 1;
    }

    if (forceRemount || typeof excludedDrafts[titleId] !== "boolean") {
      excludedDrafts[titleId] = Boolean(detectedTitle.excluded);
    }
  }

  function getStoreSyncAdditionalPathsDraft(storeId) {
    return state.storeSync.additionalPathsDraftByStoreId?.[storeId] || "";
  }

  function syncStoreSyncAdditionalPathsDraftFromSnapshot(storeId, forceRemount = false) {
    if (!storeId) {
      return;
    }

    const store = getStoreSyncStore(storeId);
    const additionalPaths = Array.isArray(store?.additionalPaths) ? store.additionalPaths : [];
    const nextValue = additionalPaths.join("\n");

    state.storeSync.additionalPathsDraftByStoreId ||= {};
    state.storeSync.additionalPathsInputVersionByStoreId ||= {};

    if (forceRemount || typeof state.storeSync.additionalPathsDraftByStoreId[storeId] !== "string") {
      state.storeSync.additionalPathsDraftByStoreId[storeId] = nextValue;
      state.storeSync.additionalPathsInputVersionByStoreId[storeId] =
        (state.storeSync.additionalPathsInputVersionByStoreId[storeId] || 0) + 1;
    }
  }

  function parseStoreSyncAdditionalPathsDraft(storeId) {
    const seen = new Set();
    return (getStoreSyncAdditionalPathsDraft(storeId) || "")
      .split(/\r?\n/)
      .map((value) => value.trim())
      .filter((value) => {
        if (!value) {
          return false;
        }

        const normalizedValue = value.toLowerCase();
        if (seen.has(normalizedValue)) {
          return false;
        }

        seen.add(normalizedValue);
        return true;
      });
  }

  function buildStoreSyncAttentionFlags(item, detectedTitle) {
    const flags = [];

    if (detectedTitle?.hasExistingShortcut && !detectedTitle?.isManagedShortcut) {
      flags.push("Existing shortcut");
    }

    if (detectedTitle?.hasOverrides) {
      flags.push("Manual rules");
    }

    if (detectedTitle?.excluded || item?.syncAction === "Excluded") {
      flags.push("Excluded");
    }

    return flags;
  }

  function resolveStoreSyncPreviewGroupKey(item, detectedTitle) {
    if (!item) {
      return "create";
    }

    if (item.syncAction === "Cleanup") {
      return "cleanup";
    }

    if (buildStoreSyncAttentionFlags(item, detectedTitle).length > 0) {
      return "attention";
    }

    switch (item.syncAction) {
      case "Create":
        return "create";
      case "Refresh Managed":
        return "refresh";
      case "Adopt Existing":
        return "adopt";
      case "Skip Existing":
        return "skip";
      default:
        return "create";
    }
  }

  function getStoreSyncPreviewGroupMeta(groupKey) {
    switch (groupKey) {
      case "attention":
        return {
          title: "Needs Attention",
          copy: "Manual rules, exclusions, and existing Steam shortcuts are grouped first.",
          order: 0,
        };
      case "cleanup":
        return {
          title: "Cleanup",
          copy: "Managed shortcuts that no longer belong to a detected game.",
          order: 1,
        };
      case "create":
        return {
          title: "Create",
          copy: "New managed shortcuts ready to be written into Steam.",
          order: 2,
        };
      case "refresh":
        return {
          title: "Refresh",
          copy: "Existing Tools for Steam shortcuts that will be refreshed in place.",
          order: 3,
        };
      case "adopt":
        return {
          title: "Adopt",
          copy: "Existing Steam shortcuts that will be claimed instead of duplicated.",
          order: 4,
        };
      case "skip":
        return {
          title: "Skip",
          copy: "Existing Steam shortcuts that stay untouched because takeover is off.",
          order: 5,
        };
      default:
        return {
          title: "Preview",
          copy: "",
          order: 9,
        };
    }
  }

  function buildStoreSyncPreviewEntries() {
    const preview = getStoreSyncPreview();
    const previewItems = Array.isArray(preview?.items) ? preview.items : [];

    return previewItems
      .map((item) => {
        const detectedTitle = getStoreSyncDetectedTitle(item.id);
        const attentionFlags = buildStoreSyncAttentionFlags(item, detectedTitle);
        const groupKey = resolveStoreSyncPreviewGroupKey(item, detectedTitle);

        return {
          ...item,
          detectedTitle,
          attentionFlags,
          groupKey,
          pinned: isStoreSyncPinnedTitle(item.id),
        };
      })
      .sort((left, right) => {
        const groupOrder = getStoreSyncPreviewGroupMeta(left.groupKey).order - getStoreSyncPreviewGroupMeta(right.groupKey).order;
        if (groupOrder !== 0) {
          return groupOrder;
        }

        if (left.pinned !== right.pinned) {
          return left.pinned ? -1 : 1;
        }

        const storeOrder = (left.storeTitle || "").localeCompare(right.storeTitle || "", undefined, { sensitivity: "base" });
        if (storeOrder !== 0) {
          return storeOrder;
        }

        return (left.title || "").localeCompare(right.title || "", undefined, { sensitivity: "base" });
      });
  }

  function buildStoreSyncPreviewSlotPlan() {
    const previewEntries = buildStoreSyncPreviewEntries();
    const slotPlan = [];
    let currentGroupKey = "";

    previewEntries.forEach((entry) => {
      if (entry.groupKey !== currentGroupKey) {
        currentGroupKey = entry.groupKey;
        slotPlan.push({
          kind: "section",
          groupKey: currentGroupKey,
          ...getStoreSyncPreviewGroupMeta(currentGroupKey),
        });
      }

      slotPlan.push({
        kind: "title",
        entry,
      });

      if (entry.detectedTitle) {
        slotPlan.push({
          kind: "quick-action",
          action: entry.detectedTitle.excluded ? "include" : "exclude",
          entry,
        });

        if (entry.detectedTitle.hasOverrides || entry.detectedTitle.excluded) {
          slotPlan.push({
            kind: "quick-action",
            action: "reset",
            entry,
          });
        }
      }
    });

    return slotPlan;
  }

  function getStoreSyncPreviewNavigationIndex(titleId) {
    const slotPlan = buildStoreSyncPreviewSlotPlan();
    const index = slotPlan.findIndex((entry) => entry.kind === "title" && entry.entry?.id === titleId);
    return index >= 0 ? index : null;
  }

  function getStoreSyncEnabledStoreCount(snapshot = getStoreSyncSnapshot()) {
    const stores = Array.isArray(snapshot?.stores) ? snapshot.stores : [];
    return stores.filter((store) => store.enabled).length;
  }

  function getStoreSyncReadyStoreCount(snapshot = getStoreSyncSnapshot()) {
    const stores = Array.isArray(snapshot?.stores) ? snapshot.stores : [];
    return stores.filter((store) => store.enabled && store.isReady).length;
  }

  function getStoreSyncDetectedTitleCount(snapshot = getStoreSyncSnapshot()) {
    return getStoreSyncDetectedTitles().length;
  }

  function getStoreSyncAttentionSummary(snapshot = getStoreSyncSnapshot()) {
    const detectedTitles = getStoreSyncDetectedTitles();
    const previewEntries = buildStoreSyncPreviewEntries();
    return {
      attentionCount: previewEntries.filter((entry) => entry.groupKey === "attention").length,
      cleanupCount: previewEntries.filter((entry) => entry.groupKey === "cleanup").length,
      unavailableStoreCount: (snapshot?.stores || []).filter((store) => store.enabled && !store.canCleanupMissingTitles).length,
      pinnedCount: detectedTitles.filter((title) => isStoreSyncPinnedTitle(title.id)).length,
    };
  }

  function buildStoreSyncOverviewCard(snapshot = getStoreSyncSnapshot()) {
    const preview = snapshot?.preview || null;
    const detectedCount = getStoreSyncDetectedTitleCount(snapshot);
    const enabledStores = getStoreSyncEnabledStoreCount(snapshot);
    const readyStores = getStoreSyncReadyStoreCount(snapshot);

    return {
      title: "Sync Summary",
      lines: [
        detectedCount === 1
          ? "1 detected title is ready for review."
          : `${detectedCount} detected titles are ready for review.`,
        `${enabledStores} enabled store${enabledStores === 1 ? "" : "s"} - ${readyStores} ready now`,
        `Next sync: ${preview?.createCount || 0} create - ${preview?.refreshCount || 0} refresh - ${preview?.adoptCount || 0} adopt - ${preview?.cleanupCount || 0} cleanup`,
      ],
    };
  }

  function buildStoreSyncAttentionCard(snapshot = getStoreSyncSnapshot()) {
    const summary = getStoreSyncAttentionSummary(snapshot);
    const lines = [];

    if (summary.attentionCount > 0) {
      lines.push(
        `${summary.attentionCount} title${summary.attentionCount === 1 ? "" : "s"} need attention because of manual rules, exclusions, or existing Steam shortcuts.`,
      );
    }

    if (summary.cleanupCount > 0) {
      lines.push(
        `${summary.cleanupCount} managed shortcut${summary.cleanupCount === 1 ? "" : "s"} will be cleaned up on the next sync.`,
      );
    }

    if (summary.unavailableStoreCount > 0) {
      lines.push(
        `${summary.unavailableStoreCount} enabled store${summary.unavailableStoreCount === 1 ? "" : "s"} are not cleanup-ready yet.`,
      );
    }

    if (!lines.length) {
      return null;
    }

    return {
      title: "Attention",
      lines,
    };
  }

  function buildStoreSyncStoreHealthCard(snapshot = getStoreSyncSnapshot()) {
    const stores = Array.isArray(snapshot?.stores) ? snapshot.stores : [];
    const enabledStores = stores.filter((store) => store.enabled);
    const unavailableStores = enabledStores.filter((store) => !store.canCleanupMissingTitles);
    const extraPathCount = enabledStores.reduce(
      (total, store) => total + (Array.isArray(store.additionalPaths) ? store.additionalPaths.length : 0),
      0,
    );

    return {
      title: "Store Health",
      lines: [
        `${enabledStores.length} source${enabledStores.length === 1 ? "" : "s"} enabled - ${unavailableStores.length} blocked or missing`,
        extraPathCount > 0
          ? `${extraPathCount} extra scan folder${extraPathCount === 1 ? "" : "s"} are configured across your stores.`
          : "No extra scan folders are configured yet.",
      ],
    };
  }

  function buildStoreSyncCompactCard(snapshot = getStoreSyncSnapshot(), title = "Overview") {
    const health = snapshot?.health || null;
    if (health) {
      const finalLine =
        health.deferredCleanupCount > 0 || health.offlineStoreCount > 0
          ? health.detail
          : health.lastJournalSummary || health.detail;

      return {
        title,
        lines: [health.summary, health.automation, finalLine].filter(Boolean),
      };
    }

    const preview = snapshot?.preview || null;
    const detectedCount = getStoreSyncDetectedTitleCount(snapshot);
    const enabledStores = getStoreSyncEnabledStoreCount(snapshot);
    const readyStores = getStoreSyncReadyStoreCount(snapshot);
    const attention = getStoreSyncAttentionSummary(snapshot);
    const queuedCount = Array.isArray(preview?.items) ? preview.items.length : 0;
    const infoParts = [];

    if (attention.unavailableStoreCount > 0) {
      infoParts.push(`${attention.unavailableStoreCount} setup`);
    }

    if (attention.attentionCount > 0) {
      infoParts.push(`${attention.attentionCount} attention`);
    }

    if (attention.cleanupCount > 0) {
      infoParts.push(`${attention.cleanupCount} cleanup`);
    }

    if (attention.pinnedCount > 0) {
      infoParts.push(`${attention.pinnedCount} pinned`);
    }

    return {
      title,
      lines: [
        `${detectedCount} title${detectedCount === 1 ? "" : "s"} - ${enabledStores} stores on - ${readyStores} ready`,
        `Auto sync every 10s - ${preview?.createCount || 0} new - ${preview?.refreshCount || 0} refresh - ${preview?.adoptCount || 0} adopt - ${preview?.cleanupCount || 0} cleanup`,
        infoParts.length
          ? infoParts.join(" - ")
          : `${queuedCount} review item${queuedCount === 1 ? "" : "s"} queued`,
      ],
    };
  }

  function buildStoreSyncPreviewBadge(entry) {
    if (!entry) {
      return "";
    }

    if (entry.groupKey === "attention") {
      if (entry.detectedTitle?.excluded || entry.syncAction === "Excluded") {
        return "Excluded";
      }

      if (entry.detectedTitle?.hasOverrides) {
        return "Override";
      }

      if (entry.detectedTitle?.hasExistingShortcut && !entry.detectedTitle?.isManagedShortcut) {
        return "Existing";
      }

      return "Attention";
    }

    if (entry.groupKey === "cleanup") {
      return "Cleanup";
    }

    return entry.syncAction || "";
  }

  function buildStoreSyncPreviewCopy(entry) {
    if (!entry) {
      return "";
    }

    const parts = [entry.storeTitle || "", entry.syncDetail || ""].filter(Boolean);
    if (entry.attentionFlags?.length) {
      parts.push(entry.attentionFlags.join(" + "));
    } else if (entry.detectedTitle?.artworkState) {
      parts.push(entry.detectedTitle.artworkState);
    }

    return parts.join(" - ");
  }

  function buildStoreSyncStoreListCopy(store) {
    if (!store) {
      return "";
    }

    const parts = [
      store.enabled ? "Enabled" : "Disabled",
      store.enabled && !store.canCleanupMissingTitles && store.isReady ? "Cleanup paused" : store.statusText || "",
      store.detectedTitleCount > 0
        ? `${store.detectedTitleCount} title${store.detectedTitleCount === 1 ? "" : "s"}`
        : store.detailText || "",
    ].filter(Boolean);

    if (store.pathValue) {
      parts.push(getPathFileName(store.pathValue));
    }

    if (Array.isArray(store.additionalPaths) && store.additionalPaths.length) {
      parts.push(`${store.additionalPaths.length} extra folder${store.additionalPaths.length === 1 ? "" : "s"}`);
    }

    if ((store.missingPathCount || 0) > 0) {
      parts.push(`${store.missingPathCount} missing`);
    }

    return parts.join(" - ");
  }

  function buildStoreSyncStoreBadge(store) {
    if (!store?.enabled) {
      return "Off";
    }

    if (!store.isReady) {
      return "Setup";
    }

    if (!store.canCleanupMissingTitles) {
      return "Check";
    }

    if (store.detectedTitleCount > 0) {
      return `${store.detectedTitleCount}`;
    }

    return "Ready";
  }

  function createStoreSyncSectionSlot(title, copy, slotKey, showDivider = false) {
    return {
      title,
      copy,
      disabled: true,
      slotKey,
      onClick: () => {},
      customRenderer: () =>
        createElement(
          "div",
          withChildren(
            {
              className: "steamloader-section-slot",
              key: slotKey || title,
            },
            showDivider ? createDivider(`${slotKey || title}-divider`) : null,
            createElement("div", {
              className: "steamloader-section-slot-title",
              children: title,
            }),
            copy
              ? createElement("div", {
                  className: "steamloader-section-slot-copy",
                  children: copy,
                })
              : null,
          ),
          slotKey || title,
        ),
    };
  }

  function getPathFileName(pathValue) {
    if (!pathValue) {
      return "";
    }

    const parts = String(pathValue).split(/[\\/]/).filter(Boolean);
    return parts.length ? parts[parts.length - 1] : String(pathValue);
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

  function isUpdatesBusy() {
    return state.updates.loading || state.updates.saving || Boolean(getUpdateSnapshot()?.installInProgress);
  }

  function isAutoSisirBusy() {
    return state.autoSisir.loading || state.autoSisir.saving;
  }

  function isHltbBusy() {
    return state.hltb.loading || state.hltb.saving;
  }

  function isArtworkBusy() {
    return state.artwork.loading || state.artwork.saving;
  }

  function syncArtworkApiKeyDraft(forceRemount = false) {
    if (!state.artwork.apiKeyDraft) {
      state.artwork.apiKeyDraft = "";
    }

    if (forceRemount) {
      state.artwork.apiKeyInputVersion += 1;
    }
  }

  function getArtworkSteamPathState() {
    return getArtworkSnapshot()?.settings?.steamPath || null;
  }

  function syncArtworkSteamPathDraft(forceRemount = false) {
    const steamPath = getArtworkSteamPathState();
    state.artwork.steamPathDraft =
      steamPath?.manualOverridePath || steamPath?.effectivePath || "";

    if (forceRemount) {
      state.artwork.steamPathInputVersion += 1;
    }
  }

  function isAppStartBusy() {
    return state.appStart.loading || state.appStart.catalogLoading || state.appStart.saving;
  }

  function buildAppStartSummaryCard(shortcuts) {
    const count = Array.isArray(shortcuts) ? shortcuts.length : 0;

    return {
      title: "App Shortcuts",
      lines: [
        count === 1 ? "1 app is ready to launch." : `${count} apps are ready to launch.`,
        "Add apps from the Windows Start Menu, then launch them directly with the controller.",
      ],
    };
  }

  function buildAppStartIcon(iconDataUri) {
    if (!iconDataUri) {
      return AppStartPluginIcon;
    }

    return function AppStartShortcutIcon() {
      return createElement("img", {
        className: "steamloader-app-start-icon",
        src: iconDataUri,
        alt: "",
      });
    };
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
        `Created: ${lastSync.importedCount} - Refreshed: ${lastSync.removedCount} - Adopted: ${lastSync.adoptedCount} - Skipped: ${lastSync.skippedCount}`,
        `Cleaned up: ${lastSync.cleanedUpCount} - Artwork updated: ${lastSync.artworkUpdatedTitleCount}`,
        completedAt || "Completed just now",
      ],
    };
  }

  function buildStoreSyncPreviewCard(preview) {
    if (!preview) {
      return {
        title: "Preview",
        lines: ["The current sync preview is not available yet."],
      };
    }

    return {
      title: "Preview",
      lines: [
        `Create: ${preview.createCount} - Refresh: ${preview.refreshCount} - Adopt: ${preview.adoptCount}`,
        `Skip: ${preview.skipCount} - Excluded: ${preview.excludedCount} - Cleanup: ${preview.cleanupCount}`,
        `${Array.isArray(preview.items) ? preview.items.length : 0} reviewed item${preview?.items?.length === 1 ? "" : "s"} are queued in the current sync plan.`,
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
      lines.push(`Primary folder: ${store.pathValue}`);
    } else if (store.supportsCustomPath) {
      lines.push("Primary folder: not set.");
    }

    if (Array.isArray(store.additionalPaths) && store.additionalPaths.length) {
      store.additionalPaths.slice(0, 2).forEach((pathValue, pathIndex) => {
        lines.push(`Extra folder ${pathIndex + 1}: ${pathValue}`);
      });
      if (store.additionalPaths.length > 2) {
        lines.push(`+${store.additionalPaths.length - 2} more extra folder${store.additionalPaths.length - 2 === 1 ? "" : "s"}.`);
      }
    } else if (store.supportsAdditionalPaths) {
      lines.push("Extra folders: none configured.");
    }

    if (store.enabled && !store.canCleanupMissingTitles) {
      lines.push("Cleanup is paused until every required store path is reachable again.");
    }

    if (store.detectedTitleCount) {
      lines.push(`${store.detectedTitleCount} title${store.detectedTitleCount === 1 ? "" : "s"} detected.`);
    } else {
      lines.push(store.detailText);
    }

    return {
      title: store.title,
      lines,
    };
  }

  function buildDisplayCurrentModeCard() {
    const modes = getDisplayModesSnapshot();
    const lines = [];

    if (modes?.display?.deviceLabel) {
      lines.push(modes.display.deviceLabel);
    }

    if (modes?.currentResolution?.label && modes?.currentRefreshRate?.label) {
      lines.push(`${modes.currentResolution.label} @ ${modes.currentRefreshRate.label}`);
    } else if (state.display.modesLoading) {
      lines.push("Loading current display mode...");
    } else {
      lines.push("Current mode is not available yet.");
    }

    return {
      title: "Current Display",
      lines,
    };
  }

  function buildPerformanceInstallCard() {
    const installation = getPerformanceInstallation();
    const statusText = resolvePerformanceStatusText();
    const showInstallStatus =
      Boolean(statusText) &&
      /TFS Overlay|PresentMon/i.test(statusText) &&
      statusText !== "TFS Overlay is not installed yet.";

    if (!installation?.installed) {
      if (showInstallStatus) {
        return {
          title: "TFS FPS Overlay",
          lines: [statusText],
        };
      }

      return {
        title: "TFS FPS Overlay",
        lines: ["Not installed."],
      };
    }

    return {
      title: "TFS FPS Overlay",
      lines: [
        installation.version ? `Core ${installation.version}` : "Installed.",
        installation.running ? "Running" : "Ready",
      ],
    };
  }

  function buildPerformanceVendorOverlayCard(vendorId) {
    const overlay = getPerformanceVendorOverlay(vendorId);
    if (!overlay) {
      return null;
    }

    return {
      title: overlay.title,
      lines: [
        overlay.statusText,
        overlay.hotkey ? overlay.hotkey : "No hotkey",
      ],
    };
  }

  function buildPerformanceOverlayCard() {
    const installation = getPerformanceInstallation();
    const settings = getPerformanceSettings();
    const runtime = getPerformanceRuntime();

    return {
      title: "TFS FPS Overlay",
      lines: [
        installation?.running
          ? runtime?.targetProcessName
            ? `${runtime.targetProcessName} - ${runtime.framesPerSecond ? `${Math.round(runtime.framesPerSecond)} FPS` : "Waiting for frames"}`
            : "Running"
          : "Stopped",
        `${settings?.overlayLevelTitle || "FPS Only"} - ${settings?.graphModeTitle || "FPS"} - ${settings?.overlayScale || 100}%`,
      ],
    };
  }

  function buildPerformanceTelemetryCard() {
    return {
      title: "Telemetry",
      lines: [
        `${getPerformanceMetricPollRate()} Hz - ${getPerformanceTelemetrySamplingPeriodMs()} ms`,
        `${getPerformanceMetricsWindow()} ms - ${getPerformanceOverlayDrawRate()} Hz`,
      ],
    };
  }

  function getDisplayResolutionPresets() {
    const presets = getDisplayModesSnapshot()?.resolutionPresets;
    return Array.isArray(presets) ? presets : [];
  }

  function getDisplayRefreshRatePresets() {
    const presets = getDisplayModesSnapshot()?.refreshRatePresets;
    return Array.isArray(presets) ? presets : [];
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

    return "Auto live sync checks enabled stores every 10 seconds.";
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

  function resolveArtworkStatusText() {
    if (state.artwork.saving) {
      return "Saving SteamGridDB settings...";
    }

    if (state.artwork.loading) {
      return "Loading SteamGridDB settings...";
    }

    return getArtworkSnapshot()?.statusText || "";
  }

  function resolveAutoSisirStatusText() {
    if (state.autoSisir.saving) {
      return "Saving Auto SISR settings...";
    }

    if (state.autoSisir.loading) {
      return "Loading Auto SISR settings...";
    }

    return getAutoSisirSnapshot()?.statusText || "";
  }

  function resolveGeneralSettingsStatusText() {
    if (state.generalSettings.saving) {
      return "Saving Tools for Steam settings...";
    }

    if (state.generalSettings.loading) {
      return "Loading Tools for Steam settings...";
    }

    return "";
  }

  function resolveUpdatesStatusText() {
    const snapshot = getUpdateSnapshot();
    if (snapshot?.installInProgress) {
      return formatUpdateInstallStatus(snapshot);
    }

    if (state.updates.saving) {
      return "Please wait. Starting the Tools for Steam update...";
    }

    if (state.updates.loading) {
      return "Checking GitHub releases...";
    }

    return getUpdateHeadline();
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

  async function loadStoreSyncState(options = {}) {
    const showLoading = options.showLoading !== false;
    const preserveDrafts = options.preserveDrafts === true;
    state.storeSync.error = "";

    if (showLoading) {
      state.storeSync.loading = true;
      rerenderStoreSyncPanel();
    }

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
      const activeTitleId = getStoreSyncTitleRouteId();
      if (activeTitleId && !preserveDrafts) {
        clearStoreSyncArtworkPreview(activeTitleId);
        syncStoreSyncTitleDraftsFromSnapshot(activeTitleId, true);
      }
    } catch (error) {
      state.storeSync.error = error instanceof Error ? error.message : String(error);
      if (showLoading) {
        state.storeSync.snapshot = null;
      }
    } finally {
      state.storeSync.loading = false;
      rerenderStoreSyncPanel();
    }
  }

  async function loadGeneralSettingsState() {
    state.generalSettings.loading = true;
    state.generalSettings.error = "";
    rerenderGeneralSettingsPanel();

    try {
      const response = await fetch(`${apiBase}api/settings/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Tools for Steam settings could not be loaded (${response.status}).`);
      }

      state.generalSettings.snapshot = payload && typeof payload === "object" ? payload : null;
      syncSplashDraftsFromSnapshot(true);
    } catch (error) {
      state.generalSettings.error = error instanceof Error ? error.message : String(error);
      state.generalSettings.snapshot = null;
    } finally {
      state.generalSettings.loading = false;
      rerenderGeneralSettingsPanel();
    }
  }

  async function loadUpdateState(options = {}) {
    const force = options.force === true;
    const showLoading = options.showLoading !== false;
    if (showLoading) {
      state.updates.loading = true;
    }
    state.updates.error = "";
    if (showLoading) {
      rerenderGeneralSettingsPanel();
    }

    try {
      const path = force ? "api/updates/check" : "api/updates/state";
      const response = await fetch(`${apiBase}${path}`, {
        method: force ? "POST" : "GET",
        headers: force
          ? {
              "Content-Type": "application/json",
            }
          : undefined,
        body: force ? "{}" : undefined,
        cache: "no-store",
      });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Tools for Steam updates could not be loaded (${response.status}).`);
      }

      state.updates.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.updates.error = error instanceof Error ? error.message : String(error);
      if (force) {
        state.updates.snapshot = null;
      }
    } finally {
      state.updates.loading = false;
      rerenderGeneralSettingsPanel();
    }
  }

  async function loadAutoSisirState() {
    state.autoSisir.loading = true;
    state.autoSisir.error = "";
    rerenderAutoSisirPanel();

    try {
      const response = await fetch(`${apiBase}api/auto-sisr/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Auto SISR could not be loaded (${response.status}).`);
      }

      state.autoSisir.snapshot = payload && typeof payload === "object" ? payload : null;
      syncAutoSisirPathDraftFromSnapshot(true);
    } catch (error) {
      state.autoSisir.error = error instanceof Error ? error.message : String(error);
      state.autoSisir.snapshot = null;
    } finally {
      state.autoSisir.loading = false;
      rerenderAutoSisirPanel();
    }
  }

  async function loadDisplayModes() {
    state.display.modesLoading = true;
    state.display.error = "";
    rerenderDisplayPanel();

    try {
      const response = await fetch(`${apiBase}api/display/modes`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Display modes could not be loaded (${response.status}).`);
      }

      state.display.modesSnapshot = payload && typeof payload === "object" ? payload : null;
      state.display.status = state.display.modesSnapshot?.statusText || state.display.status;
    } catch (error) {
      state.display.error = error instanceof Error ? error.message : String(error);
      state.display.modesSnapshot = null;
    } finally {
      state.display.modesLoading = false;
      rerenderDisplayPanel();
    }
  }

  async function loadPerformanceState() {
    state.performance.loading = true;
    state.performance.error = "";
    rerenderPerformancePanel();

    try {
      const response = await fetch(`${apiBase}api/performance/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Performance could not be loaded (${response.status}).`);
      }

      state.performance.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.performance.error = error instanceof Error ? error.message : String(error);
      state.performance.snapshot = null;
    } finally {
      state.performance.loading = false;
      rerenderPerformancePanel();
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

  async function loadArtworkState() {
    state.artwork.loading = true;
    state.artwork.error = "";
    rerenderArtworkPanel();

    try {
      const response = await fetch(`${apiBase}api/artwork/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `SteamGridDB settings could not be loaded (${response.status}).`);
      }

      state.artwork.snapshot = payload && typeof payload === "object" ? payload : null;
      syncArtworkApiKeyDraft(true);
      syncArtworkSteamPathDraft(true);
    } catch (error) {
      state.artwork.error = error instanceof Error ? error.message : String(error);
      state.artwork.snapshot = null;
    } finally {
      state.artwork.loading = false;
      rerenderArtworkPanel();
    }
  }

  async function loadProcessesState() {
    if (state.processes.loading) {
      return;
    }

    state.processes.loading = true;
    state.processes.error = "";
    rerenderProcessesPanel();

    try {
      const response = await fetch(`${apiBase}api/processes/windows`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Processes could not be loaded (${response.status}).`);
      }

      state.processes.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.processes.error = error instanceof Error ? error.message : String(error);
      state.processes.snapshot = null;
    } finally {
      state.processes.loading = false;
      rerenderProcessesPanel();
    }
  }

  async function loadAppStartState() {
    state.appStart.loading = true;
    state.appStart.error = "";
    rerenderAppStartPanel();

    try {
      const response = await fetch(`${apiBase}api/app-start/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `App Start could not be loaded (${response.status}).`);
      }

      state.appStart.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.appStart.error = error instanceof Error ? error.message : String(error);
      state.appStart.snapshot = null;
    } finally {
      state.appStart.loading = false;
      rerenderAppStartPanel();
    }
  }

  async function loadAppStartCatalog() {
    state.appStart.catalogLoading = true;
    state.appStart.error = "";
    rerenderAppStartPanel();

    try {
      const response = await fetch(`${apiBase}api/app-start/catalog`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `App catalog could not be loaded (${response.status}).`);
      }

      state.appStart.catalog = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.appStart.error = error instanceof Error ? error.message : String(error);
      state.appStart.catalog = null;
    } finally {
      state.appStart.catalogLoading = false;
      rerenderAppStartPanel();
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

  async function sendPerformanceRequest(path, bodyPayload = null) {
    state.performance.saving = true;
    state.performance.error = "";
    rerenderPerformancePanel();

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

      state.performance.snapshot = payload && typeof payload === "object" ? payload : null;
      return true;
    } catch (error) {
      state.performance.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.performance.saving = false;
      rerenderPerformancePanel();
    }
  }

  async function startPerformanceOverlay() {
    await sendPerformanceRequest("api/performance/overlay/start");
  }

  async function preparePerformanceElevatedHelper() {
    await sendPerformanceRequest("api/performance/elevated-helper/prepare");
  }

  async function stopPerformanceOverlay() {
    await sendPerformanceRequest("api/performance/overlay/stop");
  }

  async function setPerformanceOverlayLevel(level) {
    await sendPerformanceRequest("api/performance/settings/overlay-level", { level });
  }

  async function setPerformanceSettingValue(key, value) {
    await sendPerformanceRequest("api/performance/settings/value", { key, value });
  }

  async function togglePerformanceAutoTarget() {
    await sendPerformanceRequest("api/performance/settings/auto-target");
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
      const activeTitleId = getStoreSyncTitleRouteId();
      if (activeTitleId) {
        clearStoreSyncArtworkPreview(activeTitleId);
        syncStoreSyncTitleDraftsFromSnapshot(activeTitleId, true);
      }
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
    let succeeded = false;
    state.generalSettings.saving = true;
    state.generalSettings.error = "";
    rerenderGeneralSettingsPanel();

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
      syncSplashDraftsFromSnapshot(true);
      succeeded = true;
    } catch (error) {
      state.generalSettings.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.generalSettings.saving = false;
      rerenderGeneralSettingsPanel();
    }

    return succeeded;
  }

  async function sendUpdateRequest(path, bodyPayload = null) {
    let succeeded = false;
    state.updates.saving = true;
    state.updates.error = "";
    rerenderGeneralSettingsPanel();

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
        throw new Error(payload.message || `The update request failed (${response.status}).`);
      }

      state.updates.snapshot = payload && typeof payload === "object" ? payload : null;
      succeeded = true;
    } catch (error) {
      state.updates.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.updates.saving = false;
      rerenderGeneralSettingsPanel();
    }

    return succeeded;
  }

  async function setUpdateChannel(channel) {
    const normalizedChannel = channel === "beta" ? "beta" : "stable";
    const snapshot = getUpdateSnapshot();
    if (snapshot) {
      state.updates.snapshot = {
        ...snapshot,
        channel: normalizedChannel,
      };
      rerenderGeneralSettingsPanel();
    }

    await sendUpdateRequest("api/updates/channel", { channel: normalizedChannel });
  }

  async function checkForUpdates() {
    await loadUpdateState({ force: true });
  }

  async function installUpdate() {
    const succeeded = await sendUpdateRequest("api/updates/install");
    if (succeeded) {
      updateUpdatesPolling();
    }
  }

  function updateUpdatesPolling() {
    if (window.__steamToolsUpdatesPollTimer) {
      window.clearInterval(window.__steamToolsUpdatesPollTimer);
      window.__steamToolsUpdatesPollTimer = null;
    }

    const snapshot = getUpdateSnapshot();
    const updatesVisible =
      state.route?.screen === "root" ||
      (state.route?.screen === "page" &&
        state.route?.pluginId === "settings" &&
        state.route?.pageId === "updates");
    if (!updatesVisible || !snapshot?.installInProgress) {
      return;
    }

    window.__steamToolsUpdatesPollTimer = window.setInterval(() => {
      if (!state.updates.loading && !state.updates.saving) {
        void loadUpdateState({ force: false, showLoading: false });
      }
    }, 900);
  }

  async function sendAutoSisirRequest(path, bodyPayload = null) {
    state.autoSisir.saving = true;
    state.autoSisir.error = "";
    rerenderAutoSisirPanel();

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

      state.autoSisir.snapshot = payload && typeof payload === "object" ? payload : null;
      syncAutoSisirPathDraftFromSnapshot(true);
    } catch (error) {
      state.autoSisir.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.autoSisir.saving = false;
      rerenderAutoSisirPanel();
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

  async function sendArtworkRequest(path, bodyPayload = null) {
    state.artwork.saving = true;
    state.artwork.error = "";
    rerenderArtworkPanel();

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

      state.artwork.snapshot = payload && typeof payload === "object" ? payload : null;
      return true;
    } catch (error) {
      state.artwork.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.artwork.saving = false;
      rerenderArtworkPanel();
    }
  }

  async function sendProcessesRequest(path, bodyPayload = null) {
    state.processes.activating = true;
    state.processes.error = "";
    rerenderProcessesPanel();

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

      state.processes.snapshot = payload && typeof payload === "object" ? payload : null;
      return true;
    } catch (error) {
      state.processes.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.processes.activating = false;
      rerenderProcessesPanel();
    }
  }

  async function sendAppStartRequest(path, bodyPayload = null) {
    state.appStart.saving = true;
    state.appStart.error = "";
    rerenderAppStartPanel();

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

      state.appStart.snapshot = payload && typeof payload === "object" ? payload : null;
      return true;
    } catch (error) {
      state.appStart.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.appStart.saving = false;
      rerenderAppStartPanel();
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
      "close-steam-before-sync": "closeSteamBeforeSync",
      "backup-shortcuts": "backupShortcuts",
      "launch-big-picture-after-sync": "launchBigPictureAfterSync",
      "take-over-existing-shortcuts": "takeOverExistingShortcuts",
      "cleanup-missing-titles": "cleanupMissingTitles",
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

  async function setStartupMode(mode) {
    const normalizedMode = ["shell", "tray"].includes(mode) ? mode : "shell";
    const snapshot = getGeneralSettingsSnapshot();
    if (snapshot) {
      state.generalSettings.snapshot = {
        ...snapshot,
        startupMode: normalizedMode,
        runOnWindowsSignIn: true,
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/startup-mode", { mode: normalizedMode });
  }

  async function toggleHideWindowsShellInConsoleMode() {
    const snapshot = getGeneralSettingsSnapshot();
    const enabled = !Boolean(snapshot?.hideWindowsShellInConsoleMode);
    if (snapshot) {
      state.generalSettings.snapshot = {
        ...snapshot,
        hideWindowsShellInConsoleMode: enabled,
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/hide-windows-shell", { value: enabled });
  }

  async function toggleDeveloperDebugEnabled() {
    const snapshot = getGeneralSettingsSnapshot();
    const enabled = !Boolean(snapshot?.developerDebugEnabled);
    if (snapshot) {
      state.generalSettings.snapshot = {
        ...snapshot,
        developerDebugEnabled: enabled,
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/developer-debug", { value: enabled });
  }

  async function toggleSplashScreenSetting(key) {
    const snapshot = getGeneralSettingsSnapshot();
    const splash = snapshot?.splashScreen;
    const propertyMap = {
      enabled: "enabled",
      "show-text": "showText",
    };
    const propertyName = propertyMap[key];
    if (!propertyName) {
      return;
    }

    const enabled = !Boolean(splash?.[propertyName]);
    if (snapshot && splash) {
      state.generalSettings.snapshot = {
        ...snapshot,
        splashScreen: {
          ...splash,
          [propertyName]: enabled,
        },
      };
      rerenderGeneralSettingsPanel();
    }

    const path = key === "enabled" ? "api/settings/splash/enabled" : "api/settings/splash/show-text";
    await sendGeneralSettingsRequest(path, { value: enabled });
  }

  async function saveSplashWallpaperPath() {
    await sendGeneralSettingsRequest("api/settings/splash/wallpaper", {
      value: state.generalSettings.splashWallpaperDraft || "",
    });
  }

  async function showSplashPreview() {
    await sendGeneralSettingsRequest("api/settings/splash/preview");
  }

  async function clearSplashWallpaperPath() {
    state.generalSettings.splashWallpaperDraft = "";
    state.generalSettings.splashWallpaperInputVersion += 1;
    await sendGeneralSettingsRequest("api/settings/splash/wallpaper", { value: "" });
  }

  async function saveSplashIconPath() {
    await sendGeneralSettingsRequest("api/settings/splash/icon", {
      value: state.generalSettings.splashIconDraft || "",
    });
  }

  async function clearSplashIconPath() {
    state.generalSettings.splashIconDraft = "";
    state.generalSettings.splashIconInputVersion += 1;
    await sendGeneralSettingsRequest("api/settings/splash/icon", { value: "" });
  }

  async function adjustSplashExtraDelay(delta) {
    const snapshot = getGeneralSettingsSnapshot();
    const splash = snapshot?.splashScreen;
    const nextValue = Math.max(0, Math.min(30, Number(splash?.extraCloseDelaySeconds || 0) + delta));
    if (snapshot && splash) {
      state.generalSettings.snapshot = {
        ...snapshot,
        splashScreen: {
          ...splash,
          extraCloseDelaySeconds: nextValue,
        },
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/splash/extra-delay", { value: nextValue });
  }

  async function resetSplashExtraDelay() {
    const snapshot = getGeneralSettingsSnapshot();
    const splash = snapshot?.splashScreen;
    if (snapshot && splash) {
      state.generalSettings.snapshot = {
        ...snapshot,
        splashScreen: {
          ...splash,
          extraCloseDelaySeconds: 0,
        },
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/splash/extra-delay", { value: 0 });
  }

  async function togglePluginEnabled(pluginId, enabled) {
    const snapshot = getGeneralSettingsSnapshot();
    if (snapshot?.plugins) {
      state.generalSettings.snapshot = {
        ...snapshot,
        plugins: snapshot.plugins.map((plugin) =>
          plugin.id === pluginId
            ? {
                ...plugin,
                enabled,
              }
            : plugin,
        ),
      };

      if (!enabled && state.route.pluginId === pluginId) {
        requestFocusForRoute(parseRoute("root"), 0);
        state.route = parseRoute("root");
      }

      if (!enabled && pluginId === "themes") {
        state.themes.snapshot = null;
        applyActiveThemeCss();
      }

      if (!enabled && pluginId === "hltb") {
        state.hltb.snapshot = null;
      }

      if (!enabled && pluginId === "artwork") {
        state.artwork.snapshot = null;
      }

      if (!enabled && pluginId === "app-start") {
        state.appStart.snapshot = null;
        state.appStart.catalog = null;
      }

      if (!enabled && pluginId === "auto-sisr") {
        state.autoSisir.snapshot = null;
      }

      rerenderGeneralSettingsPanel();
    }

    const saved = await sendGeneralSettingsRequest("api/settings/plugins/enabled", { pluginId, enabled });
    if (saved && pluginId === "auto-sisr") {
      state.autoSisir.snapshot = null;
      state.autoSisir.error = "";
      state.autoSisir.pathDraft = "";
      state.autoSisir.pathInputVersion += 1;
      if (enabled) {
        void loadAutoSisirState();
      } else {
        renderPanelState();
      }
    }
  }

  async function toggleAutoSisirSetting(key) {
    const snapshot = getAutoSisirSnapshot();
    const propertyMap = {
      enabled: "enabled",
      "auto-start-game-pass": "autoStartForGamePass",
    };
    const propertyName = propertyMap[key];
    if (snapshot?.settings && propertyName) {
      state.autoSisir.snapshot = {
        ...snapshot,
        settings: {
          ...snapshot.settings,
          [propertyName]: !Boolean(snapshot.settings[propertyName]),
        },
      };
      renderPanelState();
    }

    await sendAutoSisirRequest("api/auto-sisr/settings/toggle", { value: key });
  }

  async function saveAutoSisirPath() {
    await sendAutoSisirRequest("api/auto-sisr/path", { value: state.autoSisir.pathDraft || "" });
  }

  async function resetAutoSisirPath() {
    state.autoSisir.pathDraft = "";
    state.autoSisir.pathInputVersion += 1;
    await sendAutoSisirRequest("api/auto-sisr/path/reset");
  }

  async function toggleAutoSisirWatchedTitle(titleId) {
    if (!titleId) {
      return;
    }

    await sendAutoSisirRequest("api/auto-sisr/titles/toggle", { value: titleId });
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

  async function toggleArtworkSetting(key) {
    const settings = getArtworkSnapshot()?.settings;
    const propertyMap = {
      "context-menu-enabled": "contextMenuEnabled",
      "prefer-verified-matches": "preferVerifiedMatches",
    };

    const propertyName = propertyMap[key];
    if (settings && propertyName && Object.prototype.hasOwnProperty.call(settings, propertyName)) {
      state.artwork.snapshot = {
        ...state.artwork.snapshot,
        settings: {
          ...settings,
          [propertyName]: !Boolean(settings[propertyName]),
        },
      };
      rerenderArtworkPanel();
    }

    await sendArtworkRequest("api/artwork/settings/toggle", { key });
  }

  async function saveArtworkApiKey() {
    const value = state.artwork.apiKeyDraft || "";
    await sendArtworkRequest("api/artwork/settings/api-key", { value });
  }

  async function clearArtworkApiKey() {
    state.artwork.apiKeyDraft = "";
    state.artwork.apiKeyInputVersion += 1;
    await sendArtworkRequest("api/artwork/settings/api-key/clear");
  }

  async function saveArtworkSteamPath() {
    const value = (state.artwork.steamPathDraft || "").trim();
    if (!value) {
      state.artwork.error = "Enter the Steam install folder before saving an override.";
      rerenderArtworkPanel();
      return;
    }

    const succeeded = await sendArtworkRequest("api/artwork/settings/steam-path", { value });
    if (succeeded) {
      syncArtworkSteamPathDraft(true);
    }
  }

  async function clearArtworkSteamPath() {
    const succeeded = await sendArtworkRequest("api/artwork/settings/steam-path/clear");
    if (succeeded) {
      syncArtworkSteamPathDraft(true);
    }
  }

  async function setArtworkResultLimit(value) {
    const normalizedValue = Math.max(12, Math.min(72, Number(value) || 36));
    const snapshot = getArtworkSnapshot();
    if (snapshot?.settings) {
      state.artwork.snapshot = {
        ...snapshot,
        settings: {
          ...snapshot.settings,
          resultLimit: normalizedValue,
        },
      };
      rerenderArtworkPanel();
    }

    await sendArtworkRequest("api/artwork/settings/result-limit", { value: normalizedValue });
  }

  async function activateProcessWindow(handle) {
    await sendProcessesRequest("api/processes/activate", { value: handle });
  }

  async function addAppStartShortcut(appId) {
    const succeeded = await sendAppStartRequest("api/app-start/apps/add", { value: appId });
    if (succeeded) {
      await loadAppStartCatalog();
      const shortcut = getAppStartSnapshot()?.shortcuts?.find((entry) => entry.id === appId);
      requestFocusForRoute(parseRoute("plugin:app-start"), getAppStartShortcutIndex(shortcut?.id || appId));
      setRoute(parseRoute("plugin:app-start"));
    }
  }

  async function launchAppStartShortcut(shortcutId) {
    await sendAppStartRequest("api/app-start/apps/launch", { value: shortcutId });
  }

  async function removeAppStartShortcut(shortcutId) {
    const succeeded = await sendAppStartRequest("api/app-start/apps/remove", { value: shortcutId });
    if (succeeded) {
      state.appStart.catalog = null;
      requestFocusForRoute(parseRoute("plugin:app-start"), 0);
      setRoute(parseRoute("plugin:app-start"));
    }
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

  async function setStoreSyncPrimaryPath(storeId) {
    const store = getStoreSyncStore(storeId);
    const value = storeId === "custom-locations"
      ? readCustomPathInputValue().trim()
      : (store?.pathValue || "").trim();

    if (!value) {
      state.storeSync.error = "Enter a folder path before saving it.";
      rerenderStoreSyncPanel();
      return;
    }

    const succeeded = await sendStoreSyncRequest("api/store-sync/stores/path", {
      storeId,
      value,
    });
    if (succeeded && storeId === "custom-locations") {
      syncCustomPathDraftFromSnapshot(true);
    }
  }

  async function clearStoreSyncPrimaryPath(storeId) {
    if (!storeId) {
      return;
    }

    if (storeId === "custom-locations") {
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
    }

    const succeeded = await sendStoreSyncRequest("api/store-sync/stores/path/clear", {
      value: storeId,
    });
    if (succeeded && storeId === "custom-locations") {
      syncCustomPathDraftFromSnapshot(true);
    }
  }

  async function setCustomStorePath() {
    await setStoreSyncPrimaryPath("custom-locations");
  }

  async function clearCustomStorePath() {
    await clearStoreSyncPrimaryPath("custom-locations");
  }

  async function saveStoreSyncAdditionalPaths(storeId) {
    if (!storeId) {
      return;
    }

    const values = parseStoreSyncAdditionalPathsDraft(storeId);
    const succeeded = await sendStoreSyncRequest("api/store-sync/stores/additional-paths", {
      storeId,
      values,
    });
    if (succeeded) {
      syncStoreSyncAdditionalPathsDraftFromSnapshot(storeId, true);
    }
  }

  async function clearStoreSyncAdditionalPaths(storeId) {
    if (!storeId) {
      return;
    }

    state.storeSync.additionalPathsDraftByStoreId[storeId] = "";
    state.storeSync.additionalPathsInputVersionByStoreId[storeId] =
      (state.storeSync.additionalPathsInputVersionByStoreId[storeId] || 0) + 1;
    rerenderStoreSyncPanel();

    const succeeded = await sendStoreSyncRequest("api/store-sync/stores/additional-paths", {
      storeId,
      values: [],
    });
    if (succeeded) {
      syncStoreSyncAdditionalPathsDraftFromSnapshot(storeId, true);
    }
  }

  async function setStoreSyncTitleExcluded(titleId, excluded) {
    if (!titleId) {
      return;
    }

    syncStoreSyncTitleDraftsFromSnapshot(titleId);
    state.storeSync.excludedDraftById[titleId] = Boolean(excluded);
    await saveStoreSyncTitleOverrides(titleId);
  }

  async function saveStoreSyncTitleOverrides(titleId) {
    if (!titleId) {
      return;
    }

    const titleOverride = (state.storeSync.titleOverrideDraftById[titleId] || "").trim();
    const artworkTitleOverride = (state.storeSync.artworkTitleOverrideDraftById[titleId] || "").trim();
    const excluded = Boolean(state.storeSync.excludedDraftById[titleId]);
    const succeeded = await sendStoreSyncRequest("api/store-sync/titles/override", {
      titleId,
      titleOverride,
      artworkTitleOverride,
      excluded,
    });

    if (succeeded) {
      clearStoreSyncArtworkPreview(titleId);
      syncStoreSyncTitleDraftsFromSnapshot(titleId, true);
    }
  }

  async function clearStoreSyncTitleOverrides(titleId) {
    if (!titleId) {
      return;
    }

    const succeeded = await sendStoreSyncRequest("api/store-sync/titles/override/clear", {
      value: titleId,
    });

    if (succeeded) {
      clearStoreSyncArtworkPreview(titleId);
      syncStoreSyncTitleDraftsFromSnapshot(titleId, true);
    }
  }

  async function runStoreSyncNow() {
    await sendStoreSyncRequest("api/store-sync/sync", {}, { syncing: true });
  }

  function buildScreenModel() {
    const ui = window.STFrontendLib || {};
    const makeSlot = ui.createSlot || ((title, copy, onClick, options = {}) => ({
      kind: "button",
      role: options.role || "action",
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
      buttonStyle: options.buttonStyle || null,
      buttonProps: options.buttonProps || null,
      rowClassName: options.rowClassName || "",
      slotKey: options.slotKey || options.key || "",
      selected: Boolean(options.selected),
      value: options.value,
    }));

    const makeToggleSlot = ui.createToggleSlot || ((title, copy, value, onClick, options = {}) =>
      makeSlot(title, copy, onClick, {
        ...options,
        role: "toggle",
        trailing: "none",
        switchValue: value,
      }));

    const makeSettingToggleSlot = ui.createSettingToggleSlot || ((scope, key, title, copy, value, onClick, options = {}) => ({
      ...makeToggleSlot(title, copy, value, onClick, options),
      settingScope: scope || "",
      settingKey: key || "",
    }));

    const makeChoiceSlot = ui.createChoiceSlot || ((title, copy, onClick, options = {}) =>
      makeSlot(title, copy, onClick, {
        ...options,
        role: "choice",
        badge: options.badge || options.value || "",
        selected: Boolean(options.selected || options.badge === "Selected"),
      }));

    const makeCommandSlot = ui.createCommandSlot || ((title, copy, onClick, options = {}) =>
      makeSlot(title, copy, onClick, {
        ...options,
        role: "command",
        trailing: options.trailing || "none",
      }));

    const makeNavigationSlot = ui.createNavigationSlot || ((title, copy, onClick, options = {}) =>
      makeSlot(title, copy, onClick, {
        ...options,
        role: "navigation",
        trailing: options.trailing || "chevron",
      }));

    const makeBackSlot = ui.createBackSlot || ((title, copy, onClick, options = {}) =>
      makeSlot(title, copy, onClick, {
        ...options,
        role: "back",
        trailing: options.trailing || "back",
      }));

    const defaultModel = ui.createScreenModel
      ? ui.createScreenModel({
          headerIcon: getRouteHeaderIcon(state.route),
          autoFocusIndex: resolveAutoFocusIndex(state.route),
        })
      : {
      title: "Tools for Steam",
      subtitle: "",
      status: "",
      error: "",
      note: "",
      headerIcon: getRouteHeaderIcon(state.route),
      headerActions: [],
      footerLegend: [],
      autoFocusIndex: resolveAutoFocusIndex(state.route),
      panelClassName: "",
      dividerAfterIndex: null,
      dividerAfterIndices: null,
      audioDashboard: null,
      volumePanel: null,
      cards: [],
      editor: null,
      slots: [],
    };

    if (state.route.screen === "plugin" && state.route.pluginId === "audio") {
      return {
        ...defaultModel,
        title: "Audio",
        subtitle: "Playback, microphone, and live mixer",
        status: "",
        error: getAudioDashboardError(),
        note: "",
        autoFocusIndex: null,
        audioDashboard: buildAudioDashboardModel(),
        slots: [],
      };
    }

    if (state.route.screen === "plugin" && state.route.pluginId === "display") {
      return {
        ...defaultModel,
        title: "Display",
        subtitle: "Screen output and display mode",
        status: resolveDisplayStatusText(),
        error: state.display.error,
        note: "Open a section to change only one display area at a time.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        cards: [buildDisplayCurrentModeCard()],
        dividerAfterIndex: 2,
        slots: [
          makeNavigationSlot(
            "Output Mode",
            "Choose internal or external display output.",
            () => {
              rememberCurrentRouteIndex(0);
              setRoute({ screen: "page", pluginId: "display", pageId: "output-mode" });
            },
          ),
          makeNavigationSlot(
            "Resolution",
            "Choose Full HD, 2K, or 4K when Windows reports them.",
            () => {
              rememberCurrentRouteIndex(1);
              setRoute({ screen: "page", pluginId: "display", pageId: "resolution" });
            },
          ),
          makeNavigationSlot(
            "Refresh Rate",
            "Choose 60Hz or 120Hz for the active resolution.",
            () => {
              rememberCurrentRouteIndex(2);
              setRoute({ screen: "page", pluginId: "display", pageId: "refresh-rate" });
            },
          ),
          makeCommandSlot(
            "Refresh Display Modes",
            "Reload available resolutions and refresh rates from Windows.",
            () => loadDisplayModes(),
            {
              disabled: isDisplayBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "display" &&
      state.route.pageId === "output-mode"
    ) {
      return {
        ...defaultModel,
        title: "Display",
        subtitle: "Output Mode",
        status: resolveDisplayStatusText(),
        error: state.display.error,
        note: "This uses the same Windows display switch behind Win + P.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        cards: [buildDisplayCurrentModeCard()],
        dividerAfterIndex: 1,
        slots: [
          makeCommandSlot(
            "External Display",
            "Keep the external screen active and switch away from the built-in display.",
            () => switchDisplayMode("external"),
            {
              disabled: isDisplayBusy(),
            },
          ),
          makeCommandSlot(
            "Internal Display",
            "Return to the built-in screen and disable the external display output.",
            () => switchDisplayMode("internal"),
            {
              disabled: isDisplayBusy(),
            },
          ),
          makeCommandSlot(
            "Refresh Display Modes",
            "Reload available display data from Windows.",
            () => loadDisplayModes(),
            {
              disabled: isDisplayBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "display" &&
      state.route.pageId === "resolution"
    ) {
      const resolutionPresets = getDisplayResolutionPresets();

      return {
        ...defaultModel,
        title: "Display",
        subtitle: "Resolution",
        status: resolveDisplayStatusText(),
        error: state.display.error,
        note: "Only resolutions reported by Windows for the active display are selectable.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        cards: [buildDisplayCurrentModeCard()],
        dividerAfterIndex: resolutionPresets.length ? resolutionPresets.length - 1 : null,
        slots: [
          ...resolutionPresets.map((preset) =>
            makeChoiceSlot(
              preset.title,
              preset.available ? preset.description : "Not available on the current display.",
              () => setDisplayResolutionPreset(preset.id, preset.title),
              {
                disabled: isDisplayBusy() || !preset.available || preset.selected,
                selected: Boolean(preset.selected),
                badge: preset.selected ? "Current" : "",
                trailing: preset.selected ? "none" : "chevron",
              },
            ),
          ),
          makeCommandSlot(
            "Refresh Resolutions",
            "Reload available resolutions from Windows.",
            () => loadDisplayModes(),
            {
              disabled: isDisplayBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "display" &&
      state.route.pageId === "refresh-rate"
    ) {
      const refreshRatePresets = getDisplayRefreshRatePresets();

      return {
        ...defaultModel,
        title: "Display",
        subtitle: "Refresh Rate",
        status: resolveDisplayStatusText(),
        error: state.display.error,
        note: "Refresh choices are filtered for the current resolution.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        cards: [buildDisplayCurrentModeCard()],
        dividerAfterIndex: refreshRatePresets.length ? refreshRatePresets.length - 1 : null,
        slots: [
          ...refreshRatePresets.map((preset) =>
            makeChoiceSlot(
              preset.title,
              preset.available ? preset.description : "Not available at the current resolution.",
              () => setDisplayRefreshRatePreset(preset.id),
              {
                disabled: isDisplayBusy() || !preset.available || preset.selected,
                selected: Boolean(preset.selected),
                badge: preset.selected ? "Current" : "",
                trailing: preset.selected ? "none" : "chevron",
              },
            ),
          ),
          makeCommandSlot(
            "Refresh Rates",
            "Reload available refresh rates from Windows.",
            () => loadDisplayModes(),
            {
              disabled: isDisplayBusy(),
            },
          ),
        ],
      };
    }

    if (state.route.screen === "plugin" && state.route.pluginId === "power") {
      return {
        ...defaultModel,
        title: "Power",
        subtitle: "Steam, Windows, and recovery",
        status: resolvePowerStatusText(),
        error: state.power.error,
        note: "Use these actions when console mode needs a safe escape hatch or a quick restart.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        cards: [
          {
            title: "Recovery Ready",
            lines: [
              "Start Windows Desktop brings Explorer back without leaving Tools for Steam.",
              "Restart Steam relaunches Big Picture with the required Tools for Steam bridge.",
            ],
          },
        ],
        slots: [
          makeCommandSlot(
            "Restart Steam",
            "Close Steam and relaunch Big Picture with the Tools for Steam bridge enabled.",
            () => sendPowerRequest("api/power/restart-steam", "Restarting Steam..."),
            {
              disabled: isPowerBusy(),
            },
          ),
          makeCommandSlot(
            "Start Windows Desktop",
            "Recover Explorer and the Windows taskbar if console mode gets stuck.",
            () => sendPowerRequest("api/power/start-desktop", "Starting Windows desktop..."),
            {
              disabled: isPowerBusy(),
            },
          ),
          makeCommandSlot(
            "Restart Tools for Steam",
            "Restart the background host without rebooting Windows.",
            () => sendPowerRequest("api/power/restart-steam-tools", "Restarting Tools for Steam..."),
            {
              disabled: isPowerBusy(),
            },
          ),
          makeCommandSlot(
            "Sleep Windows",
            "Put the PC into sleep mode.",
            () => sendPowerRequest("api/power/sleep", "Sending Windows to sleep...", {
              confirmText: "Press A again to put Windows to sleep.",
            }),
            {
              disabled: isPowerBusy(),
            },
          ),
          makeCommandSlot(
            "Restart Windows",
            "Reboot the PC.",
            () => sendPowerRequest("api/power/restart-windows", "Restarting Windows...", {
              confirmText: "Press A again to restart Windows.",
            }),
            {
              disabled: isPowerBusy(),
            },
          ),
          makeCommandSlot(
            "Shut Down Windows",
            "Power off the PC.",
            () => sendPowerRequest("api/power/shutdown-windows", "Shutting down Windows...", {
              confirmText: "Press A again to shut down Windows.",
            }),
            {
              disabled: isPowerBusy(),
            },
          ),
        ],
      };
    }

    if (state.route.screen === "plugin" && state.route.pluginId === "processes") {
      const snapshot = getProcessesSnapshot();
      const windows = Array.isArray(snapshot?.windows) ? snapshot.windows : [];

      return {
        ...defaultModel,
        title: "Processes",
        subtitle: "Open App Windows",
        status: resolveProcessesStatusText(),
        error: state.processes.error,
        note: "Only visible top-level app windows are listed here so taskbar hosts and ghost surfaces stay out of the way.",
        cards: [
          {
            title: "Window Switcher",
            lines: [
              windows.length === 1 ? "1 app window is ready." : `${windows.length} app windows are ready.`,
              "Press A on any row to bring that app to the front.",
            ],
          },
        ],
        slots: [
          ...windows.map((windowInfo) =>
            makeSlot(
              windowInfo.title,
              `${windowInfo.processName}${windowInfo.isMinimized ? " - Minimized" : ""}`,
              () => activateProcessWindow(windowInfo.handle),
              {
                disabled: isProcessesBusy(),
                badge: windowInfo.isForeground
                  ? "Current"
                  : windowInfo.isMinimized
                    ? "Minimized"
                    : "",
                trailing: "none",
              },
            ),
          ),
          makeCommandSlot(
            "Refresh Windows",
            "Reload the current list of open app windows.",
            () => loadProcessesState(),
            {
              disabled: isProcessesBusy(),
            },
          ),
        ],
      };
    }

    if (state.route.screen === "plugin" && state.route.pluginId === "app-start") {
      const shortcuts = Array.isArray(getAppStartSnapshot()?.shortcuts)
        ? getAppStartSnapshot().shortcuts
        : [];

      return {
        ...defaultModel,
        title: "App Start",
        subtitle: "Controller app launcher",
        status: resolveAppStartStatusText(),
        error: state.appStart.error,
        note: "Add Windows apps once, then start them from Big Picture without reaching for the desktop.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        cards: [buildAppStartSummaryCard(shortcuts)],
        slots: [
          makeNavigationSlot(
            "Add App",
            "Choose an installed Start Menu app and add it to this launcher.",
            () => {
              rememberCurrentRouteIndex(0);
              setRoute({ screen: "page", pluginId: "app-start", pageId: "add-app" });
            },
            {
              disabled: isAppStartBusy(),
              leadingIcon: AppStartPluginIcon,
            },
          ),
          ...shortcuts.map((shortcut, shortcutIndex) =>
            makeNavigationSlot(
              shortcut.name,
              "Open launch and removal actions.",
              () => {
                rememberCurrentRouteIndex(shortcutIndex + 1);
                setRoute({
                  screen: "page",
                  pluginId: "app-start",
                  pageId: `app-${shortcut.id}`,
                });
              },
              {
                disabled: isAppStartBusy(),
                leadingIcon: buildAppStartIcon(shortcut.iconDataUri),
              },
            ),
          ),
          makeCommandSlot(
            "Refresh Apps",
            "Reload saved shortcuts and the current Start Menu catalog.",
            async () => {
              state.appStart.catalog = null;
              await loadAppStartState();
            },
            {
              disabled: isAppStartBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "app-start" &&
      state.route.pageId === "add-app"
    ) {
      const apps = Array.isArray(getAppStartCatalog()?.apps) ? getAppStartCatalog().apps : [];

      return {
        ...defaultModel,
        title: "App Start",
        subtitle: "Add App",
        status: resolveAppStartStatusText(),
        error: state.appStart.error,
        note:
          apps.length > 0
            ? "Apps are discovered from the Windows Start Menu so helpers and uninstallers stay mostly out of the list."
            : "Refresh the catalog if an app was installed while Tools for Steam was already running.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        slots: [
          ...apps.map((app) =>
            makeCommandSlot(
              app.name,
              app.added ? "Already added to App Start." : "Add this app to the launcher.",
              () => addAppStartShortcut(app.id),
              {
                disabled: isAppStartBusy() || Boolean(app.added),
                badge: app.added ? "Added" : "",
                leadingIcon: buildAppStartIcon(app.iconDataUri),
                trailing: app.added ? "none" : "chevron",
              },
            ),
          ),
          makeCommandSlot(
            "Refresh App List",
            "Scan the Windows Start Menu again.",
            () => loadAppStartCatalog(),
            {
              disabled: isAppStartBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "app-start" &&
      state.route.pageId?.startsWith("app-")
    ) {
      const shortcutId = state.route.pageId.replace(/^app-/, "");
      const shortcut = getAppStartShortcut(shortcutId);

      return {
        ...defaultModel,
        title: "App Start",
        subtitle: shortcut?.name || "App",
        status: resolveAppStartStatusText(),
        error: state.appStart.error,
        note: shortcut ? shortcut.sourcePath : "The selected app shortcut could not be found.",
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        cards: shortcut
          ? [
              {
                title: shortcut.name,
                lines: ["Ready to launch from Windows.", shortcut.sourcePath],
              },
            ]
          : [],
        slots: [
          makeCommandSlot(
            "Launch App",
            "Start this app and keep Tools for Steam ready in the background.",
            () => launchAppStartShortcut(shortcutId),
            {
              disabled: isAppStartBusy() || !shortcut,
              leadingIcon: buildAppStartIcon(shortcut?.iconDataUri),
              trailing: "chevron",
            },
          ),
          makeCommandSlot(
            "Remove App",
            "Remove this shortcut from App Start. The app stays installed in Windows.",
            () => removeAppStartShortcut(shortcutId),
            {
              disabled: isAppStartBusy() || !shortcut,
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
              "Open any game in Big Picture and Tools for Steam will place the HLTB values above the main play bar.",
              `${settings?.cacheEntryCount || 0} cached game${settings?.cacheEntryCount === 1 ? "" : "s"} ready.`,
            ],
          },
        ],
        dividerAfterIndices: [0, 5],
        slots: [
          makeSettingToggleSlot(
            "hltb",
            "enabled",
            "Enable Game Page Stats",
            "Turn the HowLongToBeat panel on or off everywhere at once.",
            Boolean(settings?.enabled),
            () => toggleHltbSetting("enabled"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeSettingToggleSlot(
            "hltb",
            "show-main-story",
            "Show Main Story",
            "Display the main story estimate on the game page.",
            Boolean(settings?.showMainStory),
            () => toggleHltbSetting("show-main-story"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeSettingToggleSlot(
            "hltb",
            "show-main-plus",
            "Show Main + Extras",
            "Display the main plus extras estimate on the game page.",
            Boolean(settings?.showMainPlus),
            () => toggleHltbSetting("show-main-plus"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeSettingToggleSlot(
            "hltb",
            "show-completionist",
            "Show Completionist",
            "Display the completionist estimate on the game page.",
            Boolean(settings?.showCompletionist),
            () => toggleHltbSetting("show-completionist"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeSettingToggleSlot(
            "hltb",
            "show-all-styles",
            "Show All Styles",
            "Display the all styles estimate on the game page.",
            Boolean(settings?.showAllStyles),
            () => toggleHltbSetting("show-all-styles"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeSettingToggleSlot(
            "hltb",
            "show-view-details",
            "Show View Details",
            "Keep a quick link to the full HowLongToBeat page for the current game.",
            Boolean(settings?.showViewDetails),
            () => toggleHltbSetting("show-view-details"),
            {
              disabled: isHltbBusy(),
            },
          ),
          makeCommandSlot(
            "Clear Cached Results",
            "Drop the stored HLTB matches so Tools for Steam fetches them again fresh.",
            () => clearHltbCache(),
            {
              disabled: isHltbBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "auto-sisr" &&
      state.route.pageId === "settings"
    ) {
      const autoSisir = getAutoSisirSnapshot();
      const settings = autoSisir?.settings;
      const watchedTitles = Array.isArray(autoSisir?.watchableTitles) ? autoSisir.watchableTitles : [];
      const watchedCount = watchedTitles.filter((title) => title.watched).length;

      return {
        ...defaultModel,
        title: "Auto SISR",
        subtitle: "Settings",
        status: resolveAutoSisirStatusText(),
        error: state.autoSisir.error,
        note: "Start SISR in marker mode while selected non-Steam games are running. Wrong paths are reported here and will not crash Tools for Steam.",
        cards: [
          {
            title: "Marker State",
            lines: [
              settings?.executablePath ? `Target: ${settings.executablePath}` : "Target: loading...",
              `Start options: ${settings?.launchArguments || "--marker"}`,
              autoSisir?.executableExists ? "SISR executable found." : "SISR executable not found.",
              autoSisir?.activeGameTitle
                ? `Active title: ${autoSisir.activeGameTitle} (${autoSisir.activeGameProcessId || "unknown pid"})`
                : `${watchedCount} watched title${watchedCount === 1 ? "" : "s"} ready.`,
            ],
          },
          {
            title: "Steam Input Compatibility",
            lines: [
              "If SISR does not react while the marker is running, disable Steam Input for that game in Steam.",
              "Steam can keep controller ownership active, which may block SISR from seeing the expected input mode.",
            ],
          },
        ],
        editor: {
          label: "SISR Executable Path",
          help: "Leave this as the default LocalAppData path, or enter the full path to your own SISR.exe.",
          value: state.autoSisir.pathDraft || settings?.executablePath || "",
          placeholder: settings?.defaultExecutablePath || "C:\\Users\\you\\AppData\\Local\\SISR\\SISR.exe",
          inputKey: `auto-sisr-path-${state.autoSisir.pathInputVersion}`,
          rows: 2,
          onInput: (value) => {
            state.autoSisir.pathDraft = value;
          },
        },
        slots: [
          makeSettingToggleSlot(
            "auto-sisr",
            "enabled",
            "Enable Auto SISR",
            "Allow Tools for Steam to start and stop SISR marker mode for watched games.",
            Boolean(settings?.enabled),
            () => toggleAutoSisirSetting("enabled"),
            {
              disabled: isAutoSisirBusy(),
            },
          ),
          makeSettingToggleSlot(
            "auto-sisr",
            "auto-start-game-pass",
            "Game Pass Auto Marker",
            "Automatically watch every detected Xbox / Game Pass title.",
            settings?.autoStartForGamePass !== false,
            () => toggleAutoSisirSetting("auto-start-game-pass"),
            {
              disabled: isAutoSisirBusy() || !settings?.enabled,
            },
          ),
          makeCommandSlot(
            "Save SISR Path",
            "Use the path above for future marker launches.",
            () => saveAutoSisirPath(),
            {
              disabled: isAutoSisirBusy(),
            },
          ),
          makeCommandSlot(
            "Reset SISR Path",
            "Return to the default LocalAppData SISR.exe location.",
            () => resetAutoSisirPath(),
            {
              disabled: isAutoSisirBusy(),
            },
          ),
          makeCommandSlot(
            "Refresh Auto SISR",
            "Reload the marker status and the detected non-Steam game list.",
            () => loadAutoSisirState(),
            {
              disabled: isAutoSisirBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "auto-sisr" &&
      state.route.pageId === "watched-games"
    ) {
      const autoSisir = getAutoSisirSnapshot();
      const settings = autoSisir?.settings;
      const titles = Array.isArray(autoSisir?.watchableTitles) ? autoSisir.watchableTitles : [];
      const manualCount = titles.filter((title) => title.selected).length;
      const automaticCount = titles.filter((title) => title.automatic).length;

      return {
        ...defaultModel,
        title: "Auto SISR",
        subtitle: "Watched Games",
        status: resolveAutoSisirStatusText(),
        error: state.autoSisir.error,
        note:
          titles.length > 0
            ? "Game Pass titles can be watched automatically. Select extra non-Steam games here when they should also start the SISR marker."
            : "No detected non-Steam games are available yet. Run Store Sync once or refresh Auto SISR after adding games.",
        cards: [
          {
            title: "Selection",
            lines: [
              `${automaticCount} automatic Game Pass title${automaticCount === 1 ? "" : "s"}.`,
              `${manualCount} manually selected title${manualCount === 1 ? "" : "s"}.`,
              settings?.enabled ? "Auto SISR is enabled." : "Auto SISR is disabled in Settings.",
            ],
          },
        ],
        slots: [
          ...titles.map((title) =>
            makeSettingToggleSlot(
              "auto-sisr-title",
              title.id,
              title.title,
              `${title.storeTitle}${title.executablePath ? ` - ${title.executablePath}` : ""}`,
              Boolean(title.watched),
              () => toggleAutoSisirWatchedTitle(title.id),
              {
                disabled: isAutoSisirBusy() || Boolean(title.automatic),
                badge: title.automatic ? "Game Pass" : title.selected ? "Selected" : "",
              },
            ),
          ),
          makeCommandSlot(
            "Refresh Game List",
            "Scan Store Sync sources again and reload available non-Steam games.",
            () => loadAutoSisirState(),
            {
              disabled: isAutoSisirBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "auto-sisr" &&
      state.route.pageId === "log"
    ) {
      const autoSisir = getAutoSisirSnapshot();
      const logLines = Array.isArray(autoSisir?.recentLogLines) ? autoSisir.recentLogLines : [];
      const visibleLines = logLines.slice(-40).reverse();

      return {
        ...defaultModel,
        title: "Auto SISR",
        subtitle: "Log",
        status: resolveAutoSisirStatusText(),
        error: state.autoSisir.error,
        note: autoSisir?.logPath
          ? `Log file: ${autoSisir.logPath}`
          : "The log file will appear after Auto SISR writes its first trace entry.",
        cards: visibleLines.length
          ? visibleLines.map((line, index) => {
              const parts = String(line).split(" | ");
              return {
                title: parts.length >= 2 ? `${parts[1]} - ${parts[0]}` : `Entry ${index + 1}`,
                lines: [parts.slice(2).join(" | ") || line],
              };
            })
          : [
              {
                title: "No Trace Entries",
                lines: ["Start a watched game or refresh Auto SISR to generate log entries."],
              },
            ],
        slots: [
          makeCommandSlot(
            "Refresh Log",
            "Reload the latest Auto SISR trace entries.",
            () => loadAutoSisirState(),
            {
              disabled: isAutoSisirBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "artwork" &&
      state.route.pageId === "settings"
    ) {
      const settings = getArtworkSnapshot()?.settings;
      const resultLimit = Number(settings?.resultLimit) || 36;
      const steamPath = settings?.steamPath;
      const steamPathValue = steamPath?.effectivePath || "Not detected yet";
      const steamPathSource = steamPath?.usingManualOverride
        ? "Manual override"
        : steamPath?.autoDetectedPath
          ? "Auto-detected"
          : "Not detected";

      return {
        ...defaultModel,
        title: "SteamGridDB",
        subtitle: "Settings",
        status: resolveArtworkStatusText(),
        error: state.artwork.error,
        note: "Control the Change Artwork context-menu entry and the SteamGridDB key used for manual artwork browsing.",
        cards: [
          {
            title: "Artwork Picker",
            lines: [
              "Open a game context menu in Big Picture and choose Change Artwork to browse SteamGridDB.",
              `API key: ${settings?.steamGridDbApiKeyPreview || "Built-in key"}`,
              `Results per tab: ${resultLimit}`,
              `Steam path: ${steamPathValue}`,
              `Steam path source: ${steamPathSource}`,
            ],
          },
        ],
        editors: [
          {
            label: "SteamGridDB API Key",
            help: "Optional. Leave this blank to keep using the built-in key, or paste your own key and save it.",
            value: state.artwork.apiKeyDraft,
            placeholder: "Paste your SteamGridDB API key",
            inputKey: `steamgriddb-api-key-${state.artwork.apiKeyInputVersion}`,
            rows: 2,
            onInput: (value) => {
              state.artwork.apiKeyDraft = value;
            },
          },
          {
            label: "Steam Install Path",
            help: "Starts with the detected Steam folder. Save a manual override only when Steam lives on another drive or the detected path is wrong.",
            value: state.artwork.steamPathDraft,
            placeholder: "D:\\Steam",
            inputKey: `steam-install-path-${state.artwork.steamPathInputVersion}`,
            rows: 2,
            onInput: (value) => {
              state.artwork.steamPathDraft = value;
            },
          },
        ],
        slots: [
          makeSettingToggleSlot(
            "artwork",
            "context-menu-enabled",
            "Context Menu Entry",
            "Show Change Artwork in Steam game context menus.",
            settings?.contextMenuEnabled !== false,
            () => toggleArtworkSetting("context-menu-enabled"),
            {
              disabled: isArtworkBusy(),
            },
          ),
          makeSettingToggleSlot(
            "artwork",
            "prefer-verified-matches",
            "Prefer Verified Matches",
            "Put verified SteamGridDB game matches first when a title has several results.",
            settings?.preferVerifiedMatches !== false,
            () => toggleArtworkSetting("prefer-verified-matches"),
            {
              disabled: isArtworkBusy(),
            },
          ),
          makeCommandSlot(
            "Save API Key",
            "Use this key for SteamGridDB searches and artwork downloads. Empty values keep the built-in key.",
            () => saveArtworkApiKey(),
            {
              disabled: isArtworkBusy(),
            },
          ),
          makeCommandSlot(
            "Clear API Key",
            "Return to the built-in SteamGridDB key.",
            () => clearArtworkApiKey(),
            {
              disabled: isArtworkBusy(),
            },
          ),
          makeCommandSlot(
            "Save Steam Path",
            "Store a manual Steam folder override for artwork writes and background sync.",
            () => saveArtworkSteamPath(),
            {
              disabled: isArtworkBusy(),
            },
          ),
          makeCommandSlot(
            "Use Auto-Detected Path",
            "Clear the manual override and go back to the detected Steam install path.",
            () => clearArtworkSteamPath(),
            {
              disabled: isArtworkBusy(),
            },
          ),
          makeCommandSlot(
            "Show Fewer Results",
            "Reduce image results per artwork tab.",
            () => setArtworkResultLimit(resultLimit - 12),
            {
              disabled: isArtworkBusy() || resultLimit <= 12,
              badge: `${resultLimit}`,
            },
          ),
          makeCommandSlot(
            "Show More Results",
            "Increase image results per artwork tab.",
            () => setArtworkResultLimit(resultLimit + 12),
            {
              disabled: isArtworkBusy() || resultLimit >= 72,
              badge: `${resultLimit}`,
            },
          ),
          makeCommandSlot(
            "Refresh Settings",
            "Reload SteamGridDB settings from the background host.",
            () => loadArtworkState(),
            {
              disabled: isArtworkBusy(),
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
        makeCommandSlot("Refresh", resolveAudioStatusText(), () => loadAudioDevices(), {
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
        dividerAfterIndex: state.audio.devices.length ? 0 : null,
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

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "audio" &&
      state.route.pageId === "audio-mixer"
    ) {
      const mixerSessions = getAudioMixerSessions();
      const mixerSlots = buildAudioMixerSlots(makeCommandSlot, mixerSessions);

      return {
        ...defaultModel,
        title: "Audio",
        subtitle: "Audio Mixer",
        status: resolveAudioMixerStatusText(),
        error: state.audio.mixerError,
        autoFocusIndex: resolveAutoFocusIndex(state.route),
        note:
          !state.audio.mixerLoading && !mixerSessions.length
            ? "Start a game, browser tab, or media app and its audio process will appear here."
            : "",
        dividerAfterIndex: mixerSessions.length ? mixerSessions.length - 1 : null,
        slots: mixerSlots,
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "performance" &&
      (state.route.pageId === "overlay" || state.route.pageId === "tfs-overlay")
    ) {
      const performancePanel = buildPerformancePanelModel();
      const tfsSettingSlots = markPerformanceOverlaySlots(buildPerformanceTfsSettingSlots(makeCommandSlot, makeToggleSlot, {
        includeQuickActions: true,
      }));
      const overviewSlots = markPerformanceOverlaySlots(buildPerformanceOverviewSlots(makeCommandSlot));
      const performancePrimarySlots = [createPerformanceSliderSlot(performancePanel), ...tfsSettingSlots];
      const overlayAutoFocusIndex =
        resolveAutoFocusIndex(state.route) ??
        (state.performance.pendingSliderAutoFocus ? 0 : null);

      return {
        ...defaultModel,
        title: "Performance",
        subtitle: "Overlays",
        status: "",
        error: state.performance.error,
        note: "",
        autoFocusIndex: overlayAutoFocusIndex,
        cards: [],
        volumePanel: null,
        dividerAfterIndices: [0, 3, 9, 13],
        slots: [...performancePrimarySlots, ...overviewSlots],
      };
    }

    const storeSyncSnapshot = getStoreSyncSnapshot();
    const storeSyncStatus = resolveStoreSyncStatusText();

    if (state.route.screen === "plugin" && state.route.pluginId === "store-sync") {
      const previewItemCount = Array.isArray(storeSyncSnapshot?.preview?.items) ? storeSyncSnapshot.preview.items.length : 0;
      const enabledStoreCount = getStoreSyncEnabledStoreCount(storeSyncSnapshot);

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: "Bring other PC launchers into Steam",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "Store Sync watches your enabled launchers in the background and keeps the 10 second poll as a fallback.",
        cards: [buildStoreSyncCompactCard(storeSyncSnapshot, "Overview")],
        autoFocusIndex: resolveAutoFocusIndex(state.route) ?? 0,
        dividerAfterIndices: [1],
        slots: [
          makeNavigationSlot(
            "Preview",
            "Review detected games, direct actions, and the exact sync plan before it is written.",
            () => {
              rememberCurrentRouteIndex(0);
              const targetRoute = { screen: "page", pluginId: "store-sync", pageId: "preview" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              disabled: isStoreSyncBusy(),
              badge: previewItemCount > 0 ? `${previewItemCount}` : "",
            },
          ),
          makeNavigationSlot(
            "Journal",
            "Read the latest auto-sync, cleanup, repair, and watcher events.",
            () => {
              rememberCurrentRouteIndex(1);
              const targetRoute = { screen: "page", pluginId: "store-sync", pageId: "journal" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              disabled: isStoreSyncBusy(),
              badge: Array.isArray(storeSyncSnapshot?.journal) ? `${storeSyncSnapshot.journal.length}` : "",
            },
          ),
          makeNavigationSlot(
            "Stores",
            "Check launcher health, primary folders, and extra scan folders per store.",
            () => {
              rememberCurrentRouteIndex(2);
              const targetRoute = { screen: "page", pluginId: "store-sync", pageId: "stores" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              disabled: isStoreSyncBusy(),
              badge: enabledStoreCount > 0 ? `${enabledStoreCount}` : "",
            },
          ),
          makeNavigationSlot(
            "Settings",
            "Control artwork, takeover, cleanup, backup, and startup behavior.",
            () => {
              rememberCurrentRouteIndex(3);
              const targetRoute = { screen: "page", pluginId: "store-sync", pageId: "settings" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeCommandSlot(
            "Refresh State",
            "Reload store availability, detected titles, Steam profile details, and the sync plan.",
            () => loadStoreSyncState(),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      state.route.pageId === "journal"
    ) {
      const journalEntries = Array.isArray(storeSyncSnapshot?.journal) ? storeSyncSnapshot.journal : [];

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: "Journal",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: journalEntries.length
          ? ""
          : "Recent sync activity will appear here after Auto Sync, cleanup deferrals, ownership repair, or manual syncs.",
        cards: [buildStoreSyncCompactCard(storeSyncSnapshot, "Health")],
        dividerAfterIndex: journalEntries.length ? 0 : null,
        slots: journalEntries.map((entry, index) =>
          makeCommandSlot(
            `${(entry.level || "info").toUpperCase()} - ${entry.message || "Store Sync Event"}`,
            [entry.trigger ? `Source: ${entry.trigger}` : "", entry.detail || "", entry.timestampUtc ? new Date(entry.timestampUtc).toLocaleString() : ""]
              .filter(Boolean)
              .join(" - "),
            () => {},
            {
              disabled: true,
              badge: index === 0 ? "Latest" : "",
            },
          ),
        ),
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      state.route.pageId === "sync-now"
    ) {
      const enabledStoreCount = getStoreSyncEnabledStoreCount(storeSyncSnapshot);

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: "Automation",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "Auto Sync reacts to watcher events first and still runs a 10 second safety poll.",
        cards: [buildStoreSyncCompactCard(storeSyncSnapshot, "Sync Overview")],
        dividerAfterIndex: 1,
        slots: [
          makeNavigationSlot(
            "Preview",
            "Review every create, refresh, adopt, skip, and cleanup action before syncing.",
            () => {
              const targetRoute = { screen: "page", pluginId: "store-sync", pageId: "preview" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeNavigationSlot(
            "Stores",
            "Check launcher health, primary folders, and extra scan folders per store.",
            () => {
              const targetRoute = { screen: "page", pluginId: "store-sync", pageId: "stores" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              disabled: isStoreSyncBusy(),
              badge: enabledStoreCount > 0 ? `${enabledStoreCount}` : "",
            },
          ),
          makeCommandSlot(
            "Refresh State",
            "Reload store availability, detected titles, and Steam profile details.",
            () => loadStoreSyncState(),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      (state.route.pageId === "preview" || state.route.pageId === "detected-games")
    ) {
      const previewSlotPlan = buildStoreSyncPreviewSlotPlan();
      const previewEntries = buildStoreSyncPreviewEntries();

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: "Preview",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "",
        cards: [buildStoreSyncCompactCard(storeSyncSnapshot, "Preview Overview")],
        dividerAfterIndex: previewSlotPlan.length ? previewSlotPlan.length - 1 : null,
        slots: [
          ...previewSlotPlan.map((planEntry, planIndex) => {
            if (planEntry.kind === "section") {
              return createStoreSyncSectionSlot(
                planEntry.title,
                planEntry.copy,
                `store-sync-preview-section-${planEntry.groupKey}`,
                planIndex > 0,
              );
            }

            if (planEntry.kind === "title" && planEntry.entry?.detectedTitle) {
              return makeNavigationSlot(
                planEntry.entry.pinned ? `[Pinned] ${planEntry.entry.title}` : planEntry.entry.title,
                buildStoreSyncPreviewCopy(planEntry.entry),
                () => {
                  rememberCurrentRouteIndex(planIndex);
                  syncStoreSyncTitleDraftsFromSnapshot(planEntry.entry.id, true);
                  const targetRoute = {
                    screen: "page",
                    pluginId: "store-sync",
                    pageId: `detected-title-${planEntry.entry.id}`,
                  };
                  requestFreshEntryForRoute(targetRoute, 0, 0);
                  setRoute(targetRoute);
                },
                {
                  disabled: isStoreSyncBusy(),
                  badge: buildStoreSyncPreviewBadge(planEntry.entry),
                },
              );
            }

            if (planEntry.kind === "title") {
              return makeCommandSlot(
                planEntry.entry?.title || "Preview Item",
                buildStoreSyncPreviewCopy(planEntry.entry),
                () => {},
                {
                  disabled: true,
                  badge: buildStoreSyncPreviewBadge(planEntry.entry),
                },
              );
            }

            if (planEntry.kind === "quick-action" && planEntry.entry?.detectedTitle) {
              return makeCommandSlot(
                planEntry.action === "include" ? "Include In Sync" : planEntry.action === "reset" ? "Reset Overrides" : "Exclude From Sync",
                planEntry.action === "include"
                  ? "Put this title back into the next sync plan without opening its detail page."
                  : planEntry.action === "reset"
                    ? "Clear manual rename, artwork, and exclude rules right from Preview."
                    : "Keep this detected title out of sync directly from Preview.",
                () =>
                  planEntry.action === "reset"
                    ? clearStoreSyncTitleOverrides(planEntry.entry.id)
                    : setStoreSyncTitleExcluded(planEntry.entry.id, planEntry.action === "exclude"),
                {
                  disabled: isStoreSyncBusy(),
                  rowClassName: "steamloader-row-shell-subtle",
                  buttonClassName: "steamloader-dialog-button steamloader-dialog-button-subtle",
                },
              );
            }

            return createStoreSyncSectionSlot("Preview", "", `store-sync-preview-fallback-${planIndex}`);
          }),
          makeCommandSlot(
            "Refresh Detection",
            "Rescan all stores and rebuild the preview.",
            () => loadStoreSyncState(),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "store-sync" &&
      state.route.pageId?.startsWith("detected-title-")
    ) {
      const titleId = state.route.pageId.replace(/^detected-title-/, "");
      const detectedTitle = getStoreSyncDetectedTitle(titleId);
      syncStoreSyncTitleDraftsFromSnapshot(titleId);
      const titleOverrideDraft = state.storeSync.titleOverrideDraftById[titleId] || "";
      const artworkTitleOverrideDraft = state.storeSync.artworkTitleOverrideDraftById[titleId] || "";
      const excludedDraft = Boolean(state.storeSync.excludedDraftById[titleId]);
      const titleInputVersion = state.storeSync.titleOverrideInputVersionById[titleId] || 0;
      const artworkInputVersion = state.storeSync.artworkTitleOverrideInputVersionById[titleId] || 0;
      const showDebug = isDeveloperDebugEnabled();
      void ensureStoreSyncArtworkPreview(titleId);

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: detectedTitle?.title || "Detected Game",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "Adjust manual title rules here, pin important games for Preview, then save before you sync.",
        dividerAfterIndices: [1, 3],
        cards: detectedTitle
          ? [
              {
                title: detectedTitle.title,
                lines: [
                  `Store: ${detectedTitle.storeTitle}`,
                  `Planned action: ${detectedTitle.syncAction}`,
                  `Steam title: ${detectedTitle.effectiveTitle}`,
                  `Artwork title: ${detectedTitle.effectiveArtworkTitle}`,
                  `Target app ID: ${(Number(detectedTitle.targetAppId || 0) >>> 0).toString(16).toUpperCase()}`,
                  detectedTitle.syncDetail,
                ],
              },
              {
                title: "Launch Details",
                lines: [
                  `Executable: ${detectedTitle.executablePath}`,
                  `Start Directory: ${detectedTitle.startDirectory}`,
                  detectedTitle.launchOptions ? `Launch Options: ${detectedTitle.launchOptions}` : "Launch Options: none",
                  detectedTitle.artworkState,
                ],
              },
              buildStoreSyncArtworkPreviewCard(titleId),
              ...(showDebug && Array.isArray(detectedTitle.debugLines) && detectedTitle.debugLines.length
                ? [
                    {
                      title: "Debug",
                      lines: detectedTitle.debugLines,
                    },
                  ]
                : []),
            ]
          : [],
        editors: detectedTitle
          ? [
              {
                label: "Steam Title Override",
                help: "Rename the Steam shortcut for this title without changing the original launcher scan.",
                value: titleOverrideDraft,
                placeholder: detectedTitle.title || "Custom Steam title",
                rows: 2,
                inputKey: `store-sync-title-override-${titleId}-${titleInputVersion}`,
                onInput: (value) => {
                  state.storeSync.titleOverrideDraftById[titleId] = value;
                },
              },
              {
                label: "Artwork Match Override",
                help: "Use this when SteamGridDB should search for a different game name than the Steam shortcut title.",
                value: artworkTitleOverrideDraft,
                placeholder: detectedTitle.effectiveArtworkTitle || detectedTitle.title || "Artwork search title",
                rows: 2,
                inputKey: `store-sync-artwork-override-${titleId}-${artworkInputVersion}`,
                onInput: (value) => {
                  state.storeSync.artworkTitleOverrideDraftById[titleId] = value;
                },
              },
            ]
          : [],
        slots: [
          makeSettingToggleSlot(
            "store-sync.title",
            `${titleId}-pinned`,
            "Pin In Preview",
            "Keep this title near the top of its Preview group so frequent edits stay easy to reach.",
            isStoreSyncPinnedTitle(titleId),
            () => setStoreSyncPinnedTitle(titleId, !isStoreSyncPinnedTitle(titleId)),
            {
              disabled: isStoreSyncBusy() || !detectedTitle,
            },
          ),
          makeSettingToggleSlot(
            "store-sync.title",
            `${titleId}-excluded`,
            "Exclude From Sync",
            "Keep this title out of sync until you turn it back on and save the override rules.",
            excludedDraft,
            () => {
              state.storeSync.excludedDraftById[titleId] = !Boolean(state.storeSync.excludedDraftById[titleId]);
              rerenderStoreSyncPanel();
            },
            {
              disabled: isStoreSyncBusy() || !detectedTitle,
            },
          ),
          makeCommandSlot(
            "Save Override Rules",
            "Save the title, artwork, and exclude rules for this detected game.",
            () => saveStoreSyncTitleOverrides(titleId),
            {
              disabled: isStoreSyncBusy() || !detectedTitle,
            },
          ),
          makeCommandSlot(
            "Clear Override Rules",
            "Remove every manual rule for this detected game and fall back to auto-detection.",
            () => clearStoreSyncTitleOverrides(titleId),
            {
              disabled: isStoreSyncBusy() || !detectedTitle,
            },
          ),
          makeCommandSlot(
            "Refresh Detection",
            "Rescan all stores and update this title.",
            () => loadStoreSyncState(),
            {
              disabled: isStoreSyncBusy(),
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
        note: "Artwork is built in, and the background watcher keeps Steam synced automatically without a manual sync step.",
        cards: [buildSteamProfileCard(storeSyncSnapshot?.steamProfile)],
        dividerAfterIndices: [1, 4],
        slots: [
          makeSettingToggleSlot(
            "store-sync",
            "download-artwork",
            "Download Artwork",
            "Download SteamGridDB artwork during sync.",
            Boolean(settings?.downloadArtwork),
            () => toggleStoreSyncSetting("download-artwork"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeSettingToggleSlot(
            "store-sync",
            "prefer-animated-artwork",
            "Prefer Animated Artwork",
            "Prefer animated artwork when compatible assets exist.",
            Boolean(settings?.preferAnimatedArtwork),
            () => toggleStoreSyncSetting("prefer-animated-artwork"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeSettingToggleSlot(
            "store-sync",
            "backup-shortcuts",
            "Back Up shortcuts.vdf",
            "Create a timestamped backup before each sync.",
            Boolean(settings?.backupShortcuts),
            () => toggleStoreSyncSetting("backup-shortcuts"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeSettingToggleSlot(
            "store-sync",
            "take-over-existing-shortcuts",
            "Take Over Existing Shortcuts",
            "Reuse matching Steam shortcuts instead of skipping them or creating duplicates.",
            Boolean(settings?.takeOverExistingShortcuts),
            () => toggleStoreSyncSetting("take-over-existing-shortcuts"),
            {
              disabled: isStoreSyncBusy(),
            },
          ),
          makeSettingToggleSlot(
            "store-sync",
            "cleanup-missing-titles",
            "Clean Up Missing Managed Titles",
            "Remove old Tools for Steam shortcuts when a launcher no longer reports that title.",
            Boolean(settings?.cleanupMissingTitles),
            () => toggleStoreSyncSetting("cleanup-missing-titles"),
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
      const pluginSettings = getGeneralPluginSettings();
      const startupMode = settings?.startupMode || "shell";

      return {
        ...defaultModel,
        title: "Settings",
        subtitle: "General",
        status: resolveGeneralSettingsStatusText(),
        error: state.generalSettings.error,
        note: "Choose between Shell and Tray startup, then manage the global behavior and plugin list below. Developer debug stays hidden unless you turn it on here.",
        dividerAfterIndex: 3,
        slots: [
          makeChoiceSlot(
            "Shell Takeover",
            "Tools for Steam starts before Explorer, syncs launchers, opens Steam Big Picture, then brings Windows back behind Steam.",
            () => setStartupMode("shell"),
            {
              disabled: isGeneralSettingsBusy() || startupMode === "shell",
              selected: startupMode === "shell",
              badge: startupMode === "shell" ? "Current" : "",
              trailing: startupMode === "shell" ? "none" : "chevron",
            },
          ),
          makeChoiceSlot(
            "Tray App",
            "Windows starts normally. Tools for Steam runs from the tray, syncs launchers, and starts Steam without taking over the shell.",
            () => setStartupMode("tray"),
            {
              disabled: isGeneralSettingsBusy() || startupMode === "tray",
              selected: startupMode === "tray",
              badge: startupMode === "tray" ? "Current" : "",
              trailing: startupMode === "tray" ? "none" : "chevron",
            },
          ),
          makeSettingToggleSlot(
            "tfs",
            "hide-windows-shell",
            "Hide Windows Shell in Console Mode",
            "Hide the taskbar and desktop icons while Steam Big Picture is active. This only applies in Shell Takeover mode and never in Tray App mode.",
            settings?.hideWindowsShellInConsoleMode !== false,
            () => toggleHideWindowsShellInConsoleMode(),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
          makeSettingToggleSlot(
            "tfs",
            "developer-debug",
            "Show Developer Debug Info",
            "Show live debug notes inside the UI while we build and test controller flows and plugin behavior. Hidden by default.",
            Boolean(settings?.developerDebugEnabled),
            () => toggleDeveloperDebugEnabled(),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
          ...pluginSettings.map((plugin) =>
            makeSettingToggleSlot(
              "tfs-plugin",
              plugin.id,
              plugin.title,
              plugin.description || "Show or hide this plugin and disable its background routes.",
              Boolean(plugin.enabled),
              () => togglePluginEnabled(plugin.id, !Boolean(plugin.enabled)),
              {
                disabled: isGeneralSettingsBusy() || plugin.canDisable === false,
              },
            ),
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "settings" &&
      state.route.pageId === "splashscreen-themes"
    ) {
      const settings = getGeneralSettingsSnapshot();
      const splash = getSplashScreenSettings();
      const wallpaperPath = splash?.wallpaperPath || "";
      const iconPath = splash?.iconPath || "";
      const extraDelay = Number(splash?.extraCloseDelaySeconds || 0);

      return {
        ...defaultModel,
        title: "Settings",
        subtitle: "Splashscreen Themes",
        status: resolveGeneralSettingsStatusText(),
        error: state.generalSettings.error,
        note: "Use full local image paths. Missing files are kept in settings, but the splash falls back safely until the path exists.",
        cards: [
          {
            title: "Current Splash",
            lines: [
              `Splashscreen: ${splash?.enabled === false ? "Hidden" : "Shown"}`,
              `Text: ${splash?.showText === false ? "Hidden" : "Shown"}`,
              wallpaperPath
                ? `Wallpaper: ${splash?.wallpaperExists ? wallpaperPath : `Missing - ${wallpaperPath}`}`
                : "Wallpaper: default background",
              iconPath
                ? `Icon: ${splash?.iconExists ? iconPath : `Missing - ${iconPath}`}`
                : "Icon: default Tools for Steam icon",
              `Extra close delay: ${extraDelay}s`,
            ],
          },
        ],
        editors: [
          {
            label: "Wallpaper Path",
            help: "PNG, JPG, JPEG, or WebP image shown behind the startup splash.",
            value: state.generalSettings.splashWallpaperDraft,
            placeholder: "C:\\Path\\To\\splash-wallpaper.png",
            rows: 2,
            inputKey: `splash-wallpaper-${state.generalSettings.splashWallpaperInputVersion}`,
            onInput: (value) => {
              state.generalSettings.splashWallpaperDraft = value;
            },
          },
          {
            label: "Icon Path",
            help: "PNG, JPG, JPEG, or WebP image used instead of the default splash icon.",
            value: state.generalSettings.splashIconDraft,
            placeholder: "C:\\Path\\To\\splash-icon.png",
            rows: 2,
            inputKey: `splash-icon-${state.generalSettings.splashIconInputVersion}`,
            onInput: (value) => {
              state.generalSettings.splashIconDraft = value;
            },
          },
        ],
        slots: [
          makeSettingToggleSlot(
            "tfs-splash",
            "enabled",
            "Show Splashscreen",
            "Show the full-screen Tools for Steam startup splash before Steam takes over.",
            splash?.enabled !== false,
            () => toggleSplashScreenSetting("enabled"),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
          makeSettingToggleSlot(
            "tfs-splash",
            "show-text",
            "Show Splash Text",
            "Show startup status text on top of the splash artwork.",
            splash?.showText !== false,
            () => toggleSplashScreenSetting("show-text"),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
          makeCommandSlot(
            "Show Splashscreen for 5 Seconds",
            "Open a preview-only splash window without starting Steam or running setup actions.",
            () => showSplashPreview(),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
          makeCommandSlot(
            "Save Wallpaper",
            "Use the wallpaper path above for future startup splashes.",
            () => saveSplashWallpaperPath(),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
          makeCommandSlot(
            "Clear Wallpaper",
            "Return to the default splash background.",
            () => clearSplashWallpaperPath(),
            {
              disabled: isGeneralSettingsBusy() || !wallpaperPath,
            },
          ),
          makeCommandSlot(
            "Save Icon",
            "Use the icon path above for future startup splashes.",
            () => saveSplashIconPath(),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
          makeCommandSlot(
            "Clear Icon",
            "Return to the default Tools for Steam splash icon.",
            () => clearSplashIconPath(),
            {
              disabled: isGeneralSettingsBusy() || !iconPath,
            },
          ),
          makeCommandSlot(
            "Shorter Delay",
            "Close the splash one second sooner after Steam is ready.",
            () => adjustSplashExtraDelay(-1),
            {
              disabled: isGeneralSettingsBusy() || extraDelay <= 0,
            },
          ),
          makeCommandSlot(
            "Longer Delay",
            "Keep the splash visible one extra second after Steam is ready.",
            () => adjustSplashExtraDelay(1),
            {
              disabled: isGeneralSettingsBusy() || extraDelay >= 30,
            },
          ),
          makeCommandSlot(
            "Reset Delay",
            "Close the splash as soon as the normal handoff is complete.",
            () => resetSplashExtraDelay(),
            {
              disabled: isGeneralSettingsBusy() || extraDelay <= 0,
            },
          ),
          makeCommandSlot(
            "Refresh Settings",
            "Reload the current splashscreen settings from Tools for Steam.",
            () => loadGeneralSettingsState(),
            {
              disabled: isGeneralSettingsBusy(),
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "settings" &&
      state.route.pageId === "updates"
    ) {
      const settings = getGeneralSettingsSnapshot();
      const updateSnapshot = getUpdateSnapshot();
      const currentVersion = updateSnapshot?.currentVersion || settings?.productVersion || "Unknown";
      const latestVersion = updateSnapshot?.latestVersion || currentVersion;
      const publishedAtText = updateSnapshot?.publishedAtUtc
        ? new Date(updateSnapshot.publishedAtUtc).toLocaleString()
        : "Not published yet";
      const channel = getUpdateChannel();
      const channelTitle = getUpdateChannelTitle(channel);
      const installReady = Boolean(updateSnapshot?.updateAvailable) && Boolean(updateSnapshot?.canInstall);

      return {
        ...defaultModel,
        title: "Settings",
        subtitle: "Updates",
        status: resolveUpdatesStatusText(),
        error: state.updates.error,
        note: "Stable follows the latest full GitHub release. Beta follows the newest GitHub prerelease preview so you can test the newest TFS builds first.",
        dividerAfterIndex: 1,
        cards: [
          {
            title: "Tools for Steam",
            lines: [
              `Current: ${currentVersion}`,
              `${updateSnapshot?.isPrerelease ? "Latest preview" : "Latest release"}: ${latestVersion}`,
              `Channel: ${channelTitle}`,
              updateSnapshot?.installInProgress
                ? `Status: ${formatUpdateInstallStatus(updateSnapshot)}`
                : null,
              updateSnapshot?.releaseName
                ? `${updateSnapshot.releaseName} - ${publishedAtText}`
                : publishedAtText,
            ].filter(Boolean),
          },
        ],
        slots: [
          makeChoiceSlot(
            "Stable Channel",
            "Track the newest full public release from GitHub releases.",
            () => setUpdateChannel("stable"),
            {
              disabled: isUpdatesBusy() || channel === "stable",
              selected: channel === "stable",
              badge: channel === "stable" ? "Current" : "",
              trailing: channel === "stable" ? "none" : "chevron",
            },
          ),
          makeChoiceSlot(
            "Beta Channel",
            "Track the newest GitHub prerelease preview automatically.",
            () => setUpdateChannel("beta"),
            {
              disabled: isUpdatesBusy() || channel === "beta",
              selected: channel === "beta",
              badge: channel === "beta" ? "Current" : "Preview",
              trailing: channel === "beta" ? "none" : "chevron",
            },
          ),
          makeCommandSlot(
            "Check for Updates",
            "Query GitHub again right now and refresh the release details.",
            () => checkForUpdates(),
            {
              disabled: isUpdatesBusy(),
            },
          ),
          makeCommandSlot(
            updateSnapshot?.installInProgress
              ? "Installing Update..."
              : installReady
                ? `Install ${updateSnapshot?.latestVersion || "Update"}`
                : "No Update Available",
            updateSnapshot?.installInProgress
              ? "Please wait while Tools for Steam downloads the package, hands it off, and restarts."
              : installReady
              ? "Download the selected channel in the background, close TFS, then relaunch on the new build."
              : "You are already on the newest build for this channel.",
            () => installUpdate(),
            {
              disabled: isUpdatesBusy() || !installReady,
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
            makeCommandSlot("Refresh Themes", "Reload the current theme catalog and state.", () => loadThemesState(), {
              disabled: state.themes.loading || state.themes.saving,
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
            makeChoiceSlot(
              choice.title,
              choice.id === option.selectedChoiceId ? "Current selection" : "Apply this value",
              () => setThemeChoice(theme.id, option.id, choice.id),
              {
                disabled: state.themes.loading || state.themes.saving || !theme.installed,
                badge: choice.id === option.selectedChoiceId ? "Selected" : "",
                selected: choice.id === option.selectedChoiceId,
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
            makeCommandSlot(
              `Decrease by ${stepLabel}`,
              "Move the setting down by one step.",
              () => adjustThemeRange(theme.id, option.id, -1),
              {
                disabled:
                  state.themes.loading ||
                  state.themes.saving ||
                  !theme.installed ||
                  option.numberValue <= option.min,
              },
            ),
            makeCommandSlot(
              `Increase by ${stepLabel}`,
              "Move the setting up by one step.",
              () => adjustThemeRange(theme.id, option.id, 1),
              {
                disabled:
                  state.themes.loading ||
                  state.themes.saving ||
                  !theme.installed ||
                  option.numberValue >= option.max,
              },
            ),
            makeCommandSlot(
              "Reset to Default",
              "Restore the original value from the theme manifest.",
              () => resetThemeRange(theme.id, option.id),
              {
                disabled: state.themes.loading || state.themes.saving || !theme.installed,
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
            makeCommandSlot("Refresh Catalog", "Reload theme and profile entries.", () => refreshThemesCatalog(), {
              disabled: state.themes.loading || state.themes.saving,
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
              makeCommandSlot(
                "Apply Profile",
                "Install any missing themes from this profile and switch the current setup to match it.",
                () => applyThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                  badge: profile.selected ? "Selected" : "",
                },
              ),
              makeCommandSlot(
                "Update From Current Setup",
                "Overwrite this installed profile with the themes and values you are using right now.",
                () => updateThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                },
              ),
              makeCommandSlot(
                "Remove Profile",
                "Remove this profile from your local installed list.",
                () => removeThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                },
              ),
            ]
          : [
              makeCommandSlot(
                "Download Profile",
                "Add this profile to your installed profile library.",
                () => installThemeProfile(profile.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
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
            makeCommandSlot("Refresh Catalog", "Reload built-in and community theme entries.", () => refreshThemesCatalog(), {
              disabled: state.themes.loading || state.themes.saving,
            }),
          ],
        };
      }

      const optionSlots = theme.installed
        ? theme.options.map((option, optionIndex) => {
            if (option.type === "toggle") {
              return makeSettingToggleSlot(
                "themes.theme-option",
                `${theme.id}:${option.id}`,
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
            return makeNavigationSlot(
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
              makeSettingToggleSlot(
                "themes.theme",
                theme.id,
                "Enabled",
                "Turn this theme on or off and reapply the current theme stack.",
                Boolean(theme.enabled),
                () => toggleThemeEnabled(theme.id, !Boolean(theme.enabled)),
                {
                  disabled: state.themes.loading || state.themes.saving,
                },
              ),
              ...optionSlots,
              makeCommandSlot(
                "Uninstall Theme",
                "Remove this theme from the installed list but keep it available in the store.",
                () => uninstallTheme(theme.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
                },
              ),
            ]
          : [
              makeCommandSlot(
                "Install Theme",
                "Add this theme to the installed list so you can enable and tune it.",
                () => installTheme(theme.id),
                {
                  disabled: state.themes.loading || state.themes.saving,
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
        note: "Browse built-in and imported themes that can be installed into Tools for Steam.",
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
            makeNavigationSlot(
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
          makeCommandSlot(
            "Refresh Catalog",
            "Reload the current theme catalog and installation state.",
            () => refreshThemesCatalog(),
            {
              disabled: state.themes.loading || state.themes.saving,
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
          makeNavigationSlot(
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
          makeCommandSlot(
            "Save Current Setup As Profile",
            "Capture the themes you have installed right now into a reusable profile.",
            () => createThemeProfileFromCurrentSetup(),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          ...installedProfiles.map((profile, profileIndex) =>
            makeNavigationSlot(
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
            makeNavigationSlot(
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
          makeCommandSlot(
            "Refresh Catalog",
            "Reload local themes, theme profiles, and built-in catalog entries.",
            () => refreshThemesCatalog(),
            {
              disabled: state.themes.loading || state.themes.saving,
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
        note: `These settings control how the theme framework behaves across the whole Tools for Steam shell. Local themes are loaded from ${themesSnapshot?.localThemesFolder || "the local themes folder"}.`,
        slots: [
          makeSettingToggleSlot(
            "themes",
            "theme-engine-enabled",
            "Theme Engine Enabled",
            "Apply active theme CSS into the current Tools for Steam surfaces.",
            Boolean(settings?.themeEngineEnabled),
            () => toggleThemesSetting("theme-engine-enabled"),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          makeSettingToggleSlot(
            "themes",
            "show-community-themes",
            "Show Community Themes",
            "Include community-made catalog entries in the theme store.",
            Boolean(settings?.showCommunityThemes),
            () => toggleThemesSetting("show-community-themes"),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          makeSettingToggleSlot(
            "themes",
            "single-theme-mode",
            "Single Theme Mode",
            "Keep only one theme active at a time when you enable a new one.",
            Boolean(settings?.singleThemeMode),
            () => toggleThemesSetting("single-theme-mode"),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          makeSettingToggleSlot(
            "themes",
            "auto-enable-on-install",
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
        note: "",
        cards: [buildStoreSyncCompactCard(storeSyncSnapshot, "Stores Overview")],
        slots: stores.map((store, storeIndex) =>
          makeNavigationSlot(
            store.title,
            buildStoreSyncStoreListCopy(store),
            () => {
              rememberCurrentRouteIndex(storeIndex);
              const targetRoute = {
                screen: "page",
                pluginId: "store-sync",
                pageId: `store-${store.id}`,
              };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              disabled: isStoreSyncBusy(),
              badge: buildStoreSyncStoreBadge(store),
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
      if (store?.supportsCustomPath && storeId === "custom-locations") {
        syncCustomPathDraftFromSnapshot();
      }
      const customPathDraft = state.storeSync.customPathDraft || "";
      syncStoreSyncAdditionalPathsDraftFromSnapshot(storeId);
      const additionalPathsDraft = getStoreSyncAdditionalPathsDraft(storeId);
      const additionalPathsInputVersion = state.storeSync.additionalPathsInputVersionByStoreId[storeId] || 0;
      const detectedTitles = Array.isArray(store?.detectedTitles)
        ? [...store.detectedTitles].sort((left, right) => {
            const leftPinned = isStoreSyncPinnedTitle(left.id);
            const rightPinned = isStoreSyncPinnedTitle(right.id);
            if (leftPinned !== rightPinned) {
              return leftPinned ? -1 : 1;
            }

            return compareStoreSyncTitles(left, right);
          })
        : [];
      const storeEditors = [
        ...(store?.supportsCustomPath
          ? [
              {
                label: "Primary Folder",
                help: "Set the main folder for this store. This is required for Custom Locations and optional for other stores that support a primary path.",
                value: customPathDraft,
                placeholder: "D:\\Games\\Custom Library",
                isCustomPath: true,
                inputKey: `custom-path-${state.storeSync.customPathInputVersion}`,
                onInput: (value) => {
                  state.storeSync.customPathDraft = value;
                },
              },
            ]
          : []),
        ...(store?.supportsAdditionalPaths
          ? [
              {
                label: "Extra Scan Folders",
                help: "Enter one full folder path per line for installs that live outside the launcher's normal library detection.",
                value: additionalPathsDraft,
                placeholder: "E:\\Games\\Portable\nF:\\Launchers\\Epic Alt Library",
                rows: 4,
                inputKey: `store-sync-additional-paths-${storeId}-${additionalPathsInputVersion}`,
                onInput: (value) => {
                  state.storeSync.additionalPathsDraftByStoreId[storeId] = value;
                },
              },
            ]
          : []),
      ];
      const managementSlots = [
        makeSettingToggleSlot(
          "store-sync.store",
          storeId,
          "Enabled",
          "Turn this source on or off for future sync runs.",
          Boolean(store?.enabled),
          () => toggleStoreSyncStoreEnabled(storeId, !Boolean(store?.enabled)),
          {
            disabled: isStoreSyncBusy() || !store,
          },
        ),
        ...(store?.supportsCustomPath
          ? [
              makeCommandSlot(
                "Save Primary Folder",
                "Store the main folder for this launcher source.",
                () => setStoreSyncPrimaryPath(storeId),
                {
                  disabled: isStoreSyncBusy(),
                },
              ),
              makeCommandSlot(
                "Clear Primary Folder",
                "Remove the saved main folder for this launcher source.",
                () => clearStoreSyncPrimaryPath(storeId),
                {
                  disabled: isStoreSyncBusy(),
                },
              ),
            ]
          : []),
        ...(store?.supportsAdditionalPaths
          ? [
              makeCommandSlot(
                "Save Extra Folders",
                "Validate and save the extra scan folders listed above.",
                () => saveStoreSyncAdditionalPaths(storeId),
                {
                  disabled: isStoreSyncBusy(),
                },
              ),
              makeCommandSlot(
                "Clear Extra Folders",
                "Remove every extra scan folder for this store.",
                () => clearStoreSyncAdditionalPaths(storeId),
                {
                  disabled: isStoreSyncBusy(),
                },
              ),
            ]
          : []),
        makeCommandSlot(
          "Refresh Store State",
          "Reload the store and validate the current detection status.",
          () => loadStoreSyncState(),
          {
            disabled: isStoreSyncBusy(),
          },
        ),
      ];
      const detectedTitleSlots = detectedTitles.length
        ? [
            createStoreSyncSectionSlot(
              "Detected Titles",
              "Open a title to inspect launch details, overrides, artwork naming, or pin it for Preview.",
              `store-sync-detected-titles-${storeId}`,
            ),
            ...detectedTitles.map((title, titleIndex) => {
              const previewEntry = {
                ...title,
                detectedTitle: title,
                attentionFlags: buildStoreSyncAttentionFlags(title, title),
                groupKey: resolveStoreSyncPreviewGroupKey(title, title),
                pinned: isStoreSyncPinnedTitle(title.id),
              };

              return makeNavigationSlot(
                previewEntry.pinned ? `[Pinned] ${title.title}` : title.title,
                `${title.storeTitle} - ${title.syncDetail} - ${getPathFileName(title.executablePath)}`,
                () => {
                  rememberCurrentRouteIndex(managementSlots.length + 1 + titleIndex);
                  syncStoreSyncTitleDraftsFromSnapshot(title.id, true);
                  const targetRoute = {
                    screen: "page",
                    pluginId: "store-sync",
                    pageId: `detected-title-${title.id}`,
                  };
                  requestFreshEntryForRoute(targetRoute, 0, 0);
                  setRoute(targetRoute);
                },
                {
                  disabled: isStoreSyncBusy(),
                  badge: buildStoreSyncPreviewBadge(previewEntry),
                },
              );
            }),
          ]
        : [];

      return {
        ...defaultModel,
        title: "Store Sync",
        subtitle: store?.title || "Store",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "",
        cards: store ? [buildStoreSyncStoreCard(store)] : [],
        editors: storeEditors.length ? storeEditors : null,
        dividerAfterIndex: detectedTitleSlots.length ? managementSlots.length - 1 : null,
        slots: [...managementSlots, ...detectedTitleSlots],
      };
    }

    if (state.route.screen === "plugin") {
      const plugin = plugins.find((entry) => entry.id === state.route.pluginId);
      if (plugin) {
        return {
          ...defaultModel,
          title: plugin.title,
          subtitle: plugin.description,
          status:
            plugin.id === "store-sync"
              ? storeSyncStatus
              : plugin.id === "performance"
                ? resolvePerformanceStatusText()
                : "",
          error:
            plugin.id === "store-sync"
              ? state.storeSync.error
              : plugin.id === "performance"
                ? state.performance.error
                : "",
          note:
            plugin.id === "store-sync"
              ? "Use Sync Now, Preview, Settings, and Stores to bring other launchers into Steam."
              : plugin.id === "auto-sisr"
                ? "Configure SISR marker mode and choose which non-Steam games should trigger it."
              : plugin.id === "artwork"
                ? "Use Settings to control the SteamGridDB context menu and artwork search behavior."
              : plugin.id === "performance"
                ? "Open overlay controls."
              : plugin.id === "hltb"
                ? "Use Settings to choose which HowLongToBeat values appear on the open game page."
              : plugin.id === "themes"
                ? "Use Store, Installed, Profiles, and Settings to build up a reusable Tools for Steam theme library."
                : plugin.id === "settings"
                  ? "General Tools for Steam options live here, separate from plugin-specific settings."
                  : "",
          cards:
            plugin.id === "performance"
              ? [buildPerformanceOverlayCard()]
              : [],
          autoFocusIndex: resolveAutoFocusIndex(state.route),
          slots: [
            ...plugin.pages.map((page, pageIndex) =>
              makeNavigationSlot(page.title, page.description, () => {
                rememberCurrentRouteIndex(pageIndex);
                const targetRoute = { screen: "page", pluginId: plugin.id, pageId: page.id };
                requestFreshEntryForRoute(targetRoute, 0, 0);
                setRoute(targetRoute);
              }),
            ),
          ],
        };
      }
    }

    const homePlugins = getHomePlugins();
    const movingPluginId = state.homeReorder.movingPluginId;
    const movingPlugin = homePlugins.find((plugin) => plugin.id === movingPluginId) || null;
    const updateSnapshot = getUpdateSnapshot();
    const updateReady =
      Boolean(updateSnapshot?.updateAvailable) &&
      Boolean(updateSnapshot?.canInstall) &&
      !state.homeReorder.active;
    const homeStatus = state.generalSettings.saving
      ? "Saving home order..."
      : updateSnapshot?.installInProgress
        ? formatUpdateInstallStatus(updateSnapshot)
      : updateReady
        ? getUpdateHeadline(updateSnapshot)
        : "";
    const homeDebugNote = getDeveloperDebugNote("home-reorder", "waiting for Y input...");
    const homeNote = state.homeReorder.active
      ? [`Moving ${movingPlugin?.title || "plugin"}. Use Up / Down to reposition it. Press A to drop or B to cancel.`, homeDebugNote]
          .filter(Boolean)
          .join(" ")
      : ["Press Y to move a plugin.", homeDebugNote]
          .filter(Boolean)
          .join(" ");
    const homeFooterLegend = state.homeReorder.active
      ? [
          { button: "Y", label: "Move", active: true },
          { button: "A", label: "Drop" },
          { button: "B", label: "Cancel" },
        ]
      : [
          { button: "Y", label: "Move" },
          { button: "A", label: "Open" },
          { button: "B", label: "Back" },
        ];

    return {
      ...defaultModel,
      headerIcon: null,
      panelClassName: "steamloader-panel-home",
      status: homeStatus,
      note: homeNote,
      footerLegend: homeFooterLegend,
      headerActions: [
        ...(updateReady
          ? [
              {
                title: `Install ${updateSnapshot?.latestVersion || "Update"}`,
                icon: HeaderUpdateIcon,
                disabled: state.homeReorder.active || state.generalSettings.saving || isUpdatesBusy(),
                buttonStyle: {
                  width: "30px",
                  height: "30px",
                  minWidth: "30px",
                  minHeight: "30px",
                  padding: "0",
                },
                onClick: () => {
                  void installUpdate();
                },
              },
            ]
          : []),
        {
          title: "Settings",
          icon: HeaderSettingsIcon,
          disabled: state.homeReorder.active || state.generalSettings.saving,
          buttonStyle: {
            width: "30px",
            height: "30px",
            minWidth: "30px",
            minHeight: "30px",
            padding: "0",
          },
          onClick: () => {
            const targetRoute = { screen: "plugin", pluginId: "settings", pageId: null };
            requestFreshEntryForRoute(targetRoute, 0, 0);
            setRoute(targetRoute);
          },
        },
      ],
      dividerAfterIndex: null,
      slots: homePlugins.map((plugin, pluginIndex) => {
        const isMoving = state.homeReorder.active && plugin.id === movingPluginId;
        return makeNavigationSlot(plugin.title, "", () => {
          if (state.homeReorder.active) {
            if (!state.homeReorder.activationLocked) {
              void commitHomeReorder();
            }
            return;
          }

          rememberCurrentRouteIndex(pluginIndex);
          const targetRoute =
            plugin.id === "artwork"
              ? { screen: "page", pluginId: "artwork", pageId: "settings" }
              : plugin.id === "hltb"
                ? { screen: "page", pluginId: "hltb", pageId: "settings" }
                : plugin.id === "performance"
                  ? { screen: "page", pluginId: "performance", pageId: "overlay" }
                : { screen: "plugin", pluginId: plugin.id, pageId: null };
          requestFreshEntryForRoute(targetRoute, 0, 0);
          setRoute(targetRoute);
        }, {
          slotKey: `home-plugin-${plugin.id}`,
          leadingIcon: getPluginIconComponent(plugin.id),
          disabled: state.generalSettings.saving,
          trailing: "none",
          rowClassName: `steamloader-row-shell-home${isMoving ? " is-reordering" : ""}`,
          buttonClassName: `steamloader-dialog-button steamloader-dialog-button-home${isMoving ? " is-reordering" : ""}`,
          buttonStyle: {
            minHeight: "44px",
            padding: "7px 10px",
          },
          buttonProps: state.homeReorder.active
            ? {
                onMoveUp: () => {
                  moveHomeReorderSelection(-1);
                  return true;
                },
                onMoveDown: () => {
                  moveHomeReorderSelection(1);
                  return true;
                },
                onCancelButton: () => {
                  cancelHomeReorder();
                  return true;
                },
              }
            : {
                onSecondaryButton: () => {
                  return startHomeReorderFromFocusedPlugin("onSecondaryButton", plugin.id);
                },
                onOptionsButton: () => {
                  return startHomeReorderFromFocusedPlugin("onOptionsButton", plugin.id);
                },
                onMenuButton: () => {
                  return startHomeReorderFromFocusedPlugin("onMenuButton", plugin.id);
                },
              },
        });
      }),
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
    const previousRoute = state.route;
    const previousRouteKey = getRouteKey(previousRoute);
    const currentScrollTop = getPanelScrollContainer()?.scrollTop;
    if (Number.isFinite(currentScrollTop)) {
      state.lastScrollTopByRoute[previousRouteKey] = Math.max(0, currentScrollTop);
    }

    if (route?.pluginId && !isPluginEnabled(route.pluginId)) {
      route = parseRoute("root");
    }

    const nextRouteKey = getRouteKey(route);
    const hasExplicitScrollRestore =
      state.pendingScrollRouteKey === nextRouteKey &&
      Number.isFinite(state.pendingScrollTop);
    if (!hasExplicitScrollRestore) {
      requestScrollRestoreForRoute(
        route,
        previousRouteKey === nextRouteKey && Number.isFinite(currentScrollTop)
          ? currentScrollTop
          : null,
      );
    }

    if (state.homeReorder.active && route.screen !== "root") {
      clearHomeReorderState({ restoreOriginalOrder: true });
    } else if (route.screen !== "root") {
      resetHomeReorderArmState();
    }

    const enteringSettingsPage =
      route.screen === "page" &&
      route.pluginId === "settings" &&
      !(
        previousRoute?.screen === "page" &&
        previousRoute?.pluginId === "settings" &&
        previousRoute?.pageId === route.pageId
      );

    const isAudioVolumePage =
      route.screen === "page" &&
      route.pluginId === "audio" &&
      route.pageId === "system-volume";
    const isAudioMixerPage =
      route.screen === "page" &&
      route.pluginId === "audio" &&
      route.pageId === "audio-mixer";
    const isAudioDashboardPage =
      route.screen === "plugin" &&
      route.pluginId === "audio";
    const isPerformanceOverlayPage =
      route.screen === "page" &&
      route.pluginId === "performance" &&
      (route.pageId === "overlay" || route.pageId === "tfs-overlay");

    state.audio.pendingVolumeActionAutoFocus = isAudioVolumePage;
    if (state.audio.pendingVolumeActionAutoFocus) {
      state.audio.activeVolumeActionIndex = 0;
    }

    state.performance.pendingSliderAutoFocus = isPerformanceOverlayPage;

    if (!isAudioVolumePage && state.audio.sliderEditActive) {
      finishVolumeSliderEditing(false);
    }

    if (!isPerformanceOverlayPage && state.performance.sliderEditActive) {
      finishPerformanceSliderEditing(false);
    }
    state.route = route;
    state.renderRevision += 1;

    if (
      route.screen === "page" &&
      route.pluginId === "audio" &&
      route.pageId === "system-volume" &&
      !state.audio.volumeLoading &&
      !state.audio.volumeInfo &&
      !state.audio.volumeError
    ) {
      void loadAudioVolume();
    }

    if (
      isAudioDashboardPage &&
      !state.audio.dashboardLoading &&
      (!state.audio.volumeInfo || !state.audio.captureVolumeInfo || !state.audio.devices.length)
    ) {
      void loadAudioDashboardState();
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
      isAudioMixerPage &&
      !state.audio.mixerLoading &&
      !state.audio.mixerSessions.length &&
      !state.audio.mixerError
    ) {
      void loadAudioMixerSessions();
    }

    if (
      route.pluginId === "performance" &&
      !state.performance.loading &&
      !state.performance.snapshot &&
      !state.performance.error
    ) {
      void loadPerformanceState();
    }

    if (
      route.pluginId === "display" &&
      !state.display.modesLoading &&
      !state.display.modesSnapshot &&
      !state.display.error
    ) {
      void loadDisplayModes();
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
      route.pluginId === "artwork" &&
      !state.artwork.loading &&
      !state.artwork.snapshot &&
      !state.artwork.error
    ) {
      void loadArtworkState();
    }

    if (
      route.pluginId === "auto-sisr" &&
      !state.autoSisir.loading &&
      !state.autoSisir.snapshot &&
      !state.autoSisir.error
    ) {
      void loadAutoSisirState();
    }

    if (
      route.pluginId === "processes" &&
      !state.processes.loading &&
      !state.processes.snapshot &&
      !state.processes.error
    ) {
      void loadProcessesState();
    }

    if (
      route.pluginId === "app-start" &&
      !state.appStart.loading &&
      !state.appStart.snapshot &&
      !state.appStart.error
    ) {
      void loadAppStartState();
    }

    if (
      route.screen === "page" &&
      route.pluginId === "app-start" &&
      route.pageId === "add-app" &&
      !state.appStart.catalogLoading &&
      !state.appStart.catalog &&
      !state.appStart.error
    ) {
      void loadAppStartCatalog();
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
      (route.pluginId === "settings" || route.screen === "root") &&
      !state.generalSettings.loading &&
      !state.generalSettings.snapshot &&
      (route.screen === "root" || enteringSettingsPage || !state.generalSettings.error)
    ) {
      void loadGeneralSettingsState();
    }

    if (
      (route.pluginId === "settings" || route.screen === "root") &&
      !state.updates.loading &&
      !state.updates.snapshot &&
      (route.screen === "root" || enteringSettingsPage || !state.updates.error)
    ) {
      void loadUpdateState();
    }

    updateProcessesPolling();
    updateAudioMixerPolling();
    updateStoreSyncPolling();
    updateUpdatesPolling();
    updateHomeReorderInputCapture();

    refreshQuickAccessPanel();
    queuePendingScrollRestore();
  }

  async function loadAudioVolume() {
    state.audio.volumeLoading = true;
    state.audio.volumeError = "";
    refreshAudioVolumePanel();

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
      refreshAudioVolumePanel();
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
    if (isAudioDashboardRoute()) {
      rerenderAudioDashboard();
    } else {
      state.renderRevision += 1;
      refreshQuickAccessPanel();
    }

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
      if (isAudioDashboardRoute()) {
        rerenderAudioDashboard();
      } else {
        state.renderRevision += 1;
        refreshQuickAccessPanel();
      }
    }
  }

  async function setDefaultCaptureDevice(deviceId) {
    state.audio.loading = true;
    state.audio.error = "";
    if (isAudioDashboardRoute()) {
      rerenderAudioDashboard();
    } else {
      state.renderRevision += 1;
      refreshQuickAccessPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/audio/default-capture`, {
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

      state.audio.captureDevices = Array.isArray(payload) ? payload : [];
    } catch (error) {
      state.audio.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.audio.loading = false;
      if (isAudioDashboardRoute()) {
        rerenderAudioDashboard();
      } else {
        state.renderRevision += 1;
        refreshQuickAccessPanel();
      }
    }
  }

  async function performVolumeAction(path, bodyPayload = null) {
    const requestId = state.audio.volumeMutationSequence + 1;
    state.audio.volumeMutationSequence = requestId;
    state.audio.volumeLoading = true;
    state.audio.volumeError = "";
    refreshAudioVolumePanel();

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

      if (requestId === state.audio.volumeMutationSequence) {
        state.audio.volumeInfo =
          responsePayload && typeof responsePayload === "object" ? responsePayload : null;
      }
    } catch (error) {
      if (requestId === state.audio.volumeMutationSequence) {
        state.audio.volumeError = error instanceof Error ? error.message : String(error);
      }
    } finally {
      if (requestId === state.audio.volumeMutationSequence) {
        state.audio.volumeLoading = false;
        refreshAudioVolumePanel();
        if (isAudioDashboardRoute()) {
          rerenderAudioDashboard();
        }
      }
    }
  }

  async function setVolume(volume) {
    const nextValue = snapVolumeToStep(volume);
    const info = state.audio.volumeInfo;
    if (info) {
      state.audio.volumeInfo = {
        ...info,
        volume: nextValue,
        isMuted: nextValue <= 0 ? true : false,
      };
      if (isAudioDashboardRoute()) {
        rerenderAudioDashboard();
      } else {
        refreshAudioVolumePanel();
      }
    }

    await performVolumeAction("api/audio/volume", { volume: nextValue });
  }

  async function toggleMute() {
    const info = state.audio.volumeInfo;
    if (info) {
      state.audio.volumeInfo = {
        ...info,
        isMuted: !info.isMuted,
      };
      if (isAudioDashboardRoute()) {
        rerenderAudioDashboard();
      } else {
        refreshAudioVolumePanel({ fullRender: true });
      }
    }

    await performVolumeAction("api/audio/volume/toggle-mute");
  }

  async function performCaptureVolumeAction(path, bodyPayload = null) {
    const requestId = state.audio.captureVolumeMutationSequence + 1;
    state.audio.captureVolumeMutationSequence = requestId;
    state.audio.captureVolumeLoading = true;
    state.audio.captureVolumeError = "";
    if (isAudioDashboardRoute()) {
      rerenderAudioDashboard();
    }

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

      if (requestId === state.audio.captureVolumeMutationSequence) {
        state.audio.captureVolumeInfo =
          responsePayload && typeof responsePayload === "object" ? responsePayload : null;
      }
    } catch (error) {
      if (requestId === state.audio.captureVolumeMutationSequence) {
        state.audio.captureVolumeError = error instanceof Error ? error.message : String(error);
      }
    } finally {
      if (requestId === state.audio.captureVolumeMutationSequence) {
        state.audio.captureVolumeLoading = false;
        if (isAudioDashboardRoute()) {
          rerenderAudioDashboard();
        }
      }
    }
  }

  async function setCaptureVolume(volume) {
    const nextValue = snapVolumeToStep(volume);
    const info = state.audio.captureVolumeInfo;
    if (info) {
      state.audio.captureVolumeInfo = {
        ...info,
        volume: nextValue,
        isMuted: nextValue <= 0 ? true : false,
      };
      if (isAudioDashboardRoute()) {
        rerenderAudioDashboard();
      }
    }

    await performCaptureVolumeAction("api/audio/capture/volume", { volume: nextValue });
  }

  async function toggleCaptureMute() {
    const info = state.audio.captureVolumeInfo;
    if (info) {
      state.audio.captureVolumeInfo = {
        ...info,
        isMuted: !info.isMuted,
      };
      if (isAudioDashboardRoute()) {
        rerenderAudioDashboard();
      }
    }

    await performCaptureVolumeAction("api/audio/capture/volume/toggle-mute");
  }

  function rerenderAudioDashboard() {
    if (isAudioDashboardRoute()) {
      const currentRoute = { ...state.route };
      const focusedIndex = getFocusedSlotIndex();
      requestFocusForRoute(currentRoute, focusedIndex);
      setRoute(currentRoute);
      return;
    }

    state.renderRevision += 1;
    renderPanelState();
  }

  async function loadAudioDashboardState(options = {}) {
    const showLoading = options.showLoading !== false;
    state.audio.dashboardLoading = true;
    state.audio.dashboardError = "";

    if (showLoading) {
      rerenderAudioDashboard();
    }

    try {
      const response = await fetch(`${apiBase}api/audio/state`, { cache: "no-store" });
      if (!response.ok) {
        throw new Error(`Audio state could not be loaded (${response.status}).`);
      }

      const payload = await response.json();
      state.audio.volumeInfo = payload?.playbackVolume || null;
      state.audio.captureVolumeInfo = payload?.captureVolume || null;
      state.audio.devices = Array.isArray(payload?.playbackDevices) ? payload.playbackDevices : [];
      state.audio.captureDevices = Array.isArray(payload?.captureDevices) ? payload.captureDevices : [];
      state.audio.mixerSessions = sortAudioMixerSessions(Array.isArray(payload?.mixerSessions) ? payload.mixerSessions : []);
      state.audio.volumeError = "";
      state.audio.captureVolumeError = "";
      state.audio.mixerError = "";
      state.audio.error = "";
    } catch (error) {
      state.audio.dashboardError = error instanceof Error ? error.message : String(error);
    } finally {
      state.audio.dashboardLoading = false;
      rerenderAudioDashboard();
    }
  }

  function findCurrentAudioDeviceIndex(devices, currentId) {
    const list = Array.isArray(devices) ? devices : [];
    if (!list.length) {
      return -1;
    }

    const byId = list.findIndex((device) => device?.id === currentId);
    if (byId >= 0) {
      return byId;
    }

    const defaultIndex = list.findIndex((device) => device?.isDefault);
    return defaultIndex >= 0 ? defaultIndex : 0;
  }

  async function cyclePlaybackDevice(direction = 1) {
    const devices = getAudioPlaybackDevices();
    if (!devices.length || state.audio.dashboardLoading) {
      return;
    }

    const currentIndex = findCurrentAudioDeviceIndex(devices, state.audio.volumeInfo?.deviceId);
    const nextIndex = (currentIndex + direction + devices.length) % devices.length;
    const nextDevice = devices[nextIndex];
    if (!nextDevice?.id || nextDevice.id === state.audio.volumeInfo?.deviceId) {
      return;
    }

    await setDefaultDevice(nextDevice.id);
    await loadAudioDashboardState({ showLoading: false });
  }

  async function cycleCaptureDevice(direction = 1) {
    const devices = getAudioCaptureDevices();
    if (!devices.length || state.audio.dashboardLoading) {
      return;
    }

    const currentIndex = findCurrentAudioDeviceIndex(devices, state.audio.captureVolumeInfo?.deviceId);
    const nextIndex = (currentIndex + direction + devices.length) % devices.length;
    const nextDevice = devices[nextIndex];
    if (!nextDevice?.id || nextDevice.id === state.audio.captureVolumeInfo?.deviceId) {
      return;
    }

    await setDefaultCaptureDevice(nextDevice.id);
    await loadAudioDashboardState({ showLoading: false });
  }

  async function loadAudioMixerSessions(options = {}) {
    const showLoading = options.showLoading !== false;
    state.audio.mixerLoading = true;
    state.audio.mixerError = "";

    if (showLoading) {
      rerenderAudioMixerPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/audio/mixer`, { cache: "no-store" });
      if (!response.ok) {
        throw new Error(`Audio mixer sessions could not be loaded (${response.status}).`);
      }

      const payload = await response.json();
      state.audio.mixerSessions = sortAudioMixerSessions(Array.isArray(payload) ? payload : []);
    } catch (error) {
      state.audio.mixerError = error instanceof Error ? error.message : String(error);
      if (!state.audio.mixerSessions.length) {
        state.audio.mixerSessions = [];
      }
    } finally {
      state.audio.mixerLoading = false;
      rerenderAudioMixerPanel();
    }
  }

  async function setAudioMixerSessionVolume(sessionId, volume, options = {}) {
    if (!sessionId) {
      return;
    }

    const nextValue = snapAudioMixerVolumeToStep(volume);
    const requestId = (state.audio.mixerMutationSequenceById[sessionId] || 0) + 1;
    state.audio.mixerMutationSequenceById[sessionId] = requestId;
    state.audio.mixerError = "";

    if (options.optimistic !== false) {
      previewAudioMixerSessionVolume(sessionId, nextValue);
    } else {
      rerenderAudioMixerPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/audio/mixer/session/volume`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ sessionId, volume: nextValue }),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        upsertAudioMixerSession(payload && typeof payload === "object" ? payload : null);
      }
    } catch (error) {
      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        state.audio.mixerError = error instanceof Error ? error.message : String(error);
      }

      void loadAudioMixerSessions({ showLoading: false });
    } finally {
      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        rerenderAudioMixerPanel();
      }
    }
  }

  async function toggleAudioMixerSessionMute(sessionId) {
    if (!sessionId) {
      return;
    }

    clearAudioMixerVolumeCommitTimer(sessionId);
    const session = findAudioMixerSession(sessionId);
    const requestId = (state.audio.mixerMutationSequenceById[sessionId] || 0) + 1;
    state.audio.mixerMutationSequenceById[sessionId] = requestId;
    state.audio.mixerError = "";

    if (session) {
      upsertAudioMixerSession({
        ...session,
        isMuted: !session.isMuted,
      });
      rerenderAudioMixerPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/audio/mixer/session/toggle-mute`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ sessionId }),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        upsertAudioMixerSession(payload && typeof payload === "object" ? payload : null);
      }
    } catch (error) {
      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        state.audio.mixerError = error instanceof Error ? error.message : String(error);
      }

      void loadAudioMixerSessions({ showLoading: false });
    } finally {
      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        rerenderAudioMixerPanel();
      }
    }
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

  function resolveAudioDashboardStatusText() {
    const mixerCount = getAudioMixerSessions().length;

    if (state.audio.dashboardLoading && !state.audio.volumeInfo && !state.audio.captureVolumeInfo) {
      return "Loading audio controls...";
    }

    if (state.audio.dashboardLoading) {
      return `Refreshing ${mixerCount} mixer ${mixerCount === 1 ? "app" : "apps"}...`;
    }

    if (!state.audio.volumeInfo && !state.audio.captureVolumeInfo) {
      return "Audio devices are not available yet.";
    }

    const systemValue = state.audio.volumeInfo?.isMuted ? "Muted" : `${getVolumeValue()}%`;
    const micValue = state.audio.captureVolumeInfo?.isMuted ? "Muted" : `${getCaptureVolumeValue()}%`;
    return `System ${systemValue} - Mic ${micValue} - ${mixerCount} ${mixerCount === 1 ? "app" : "apps"} in mixer.`;
  }

  function resolveAudioMixerStatusText() {
    const sessionCount = getAudioMixerSessions().length;

    if (state.audio.mixerLoading && !sessionCount) {
      return "Scanning active audio sessions...";
    }

    if (state.audio.mixerLoading) {
      return `Refreshing ${sessionCount} audio ${sessionCount === 1 ? "process" : "processes"}...`;
    }

    if (!sessionCount) {
      return "No active app sessions found.";
    }

    return `${sessionCount} active audio ${sessionCount === 1 ? "process" : "processes"} ready to mix.`;
  }

  function isDisplayBusy() {
    return state.display.switching || state.display.modesLoading || state.display.modesSaving;
  }

  function resolveDisplayStatusText() {
    if (state.display.switching) {
      return state.display.status || "Switching display mode...";
    }

    if (state.display.modesSaving) {
      return state.display.status || "Applying display mode...";
    }

    if (state.display.modesLoading) {
      return "Loading display modes...";
    }

    return (
      state.display.status ||
      getDisplayModesSnapshot()?.statusText ||
      "Use the Windows display switch or select a supported resolution and refresh rate."
    );
  }

  function isPowerBusy() {
    return state.power.actioning;
  }

  function resolvePowerStatusText() {
    if (state.power.actioning) {
      return state.power.status || "Running power action...";
    }

    return state.power.status || "Recovery and power actions are ready.";
  }

  function getProcessesSnapshot() {
    return state.processes.snapshot;
  }

  function isProcessesBusy() {
    return state.processes.loading || state.processes.activating;
  }

  function resolveProcessesStatusText() {
    if (state.processes.activating) {
      return "Opening the selected app window...";
    }

    if (state.processes.loading) {
      return "Loading open app windows...";
    }

    return getProcessesSnapshot()?.statusText || "Live app windows will appear here.";
  }

  function resolveAppStartStatusText() {
    if (state.appStart.saving) {
      return "Updating App Start shortcuts...";
    }

    if (state.appStart.catalogLoading) {
      return "Scanning installed Start Menu apps...";
    }

    if (state.appStart.loading) {
      return "Loading App Start shortcuts...";
    }

    return getAppStartSnapshot()?.statusText || "Add apps to launch them from Steam.";
  }

  function updateProcessesPolling() {
    if (window.__steamToolsProcessesPollTimer) {
      window.clearInterval(window.__steamToolsProcessesPollTimer);
      window.__steamToolsProcessesPollTimer = null;
    }

    if (state.route.pluginId !== "processes") {
      return;
    }

    window.__steamToolsProcessesPollTimer = window.setInterval(() => {
      if (!state.processes.loading && !state.processes.activating) {
        void loadProcessesState();
      }
    }, 2500);
  }

  function updateAudioMixerPolling() {
    if (window.__steamToolsAudioMixerPollTimer) {
      window.clearInterval(window.__steamToolsAudioMixerPollTimer);
      window.__steamToolsAudioMixerPollTimer = null;
    }

    if (state.route?.pluginId !== "audio") {
      return;
    }

    window.__steamToolsAudioMixerPollTimer = window.setInterval(() => {
      if (isAudioDashboardRoute()) {
        if (
          !state.audio.dashboardLoading &&
          !state.audio.volumeLoading &&
          !state.audio.captureVolumeLoading &&
          !hasPendingAudioMixerCommits() &&
          !state.audio.volumeCommitTimer &&
          !state.audio.captureVolumeCommitTimer
        ) {
          void loadAudioDashboardState({ showLoading: false });
        }

        return;
      }

      if (isAudioMixerRoute() && !state.audio.mixerLoading && !hasPendingAudioMixerCommits()) {
        void loadAudioMixerSessions({ showLoading: false });
      }
    }, 2500);
  }

  function updateStoreSyncPolling() {
    if (window.__steamToolsStoreSyncPollTimer) {
      window.clearInterval(window.__steamToolsStoreSyncPollTimer);
      window.__steamToolsStoreSyncPollTimer = null;
    }

    if (state.route?.pluginId !== "store-sync") {
      return;
    }

    const pageId = state.route?.pageId || "";
    if (/^(detected-title-|store-)/.test(pageId)) {
      return;
    }

    window.__steamToolsStoreSyncPollTimer = window.setInterval(() => {
      if (!isStoreSyncBusy()) {
        void loadStoreSyncState({ showLoading: false, preserveDrafts: true });
      }
    }, 10000);
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
      state.display.modesSnapshot = null;
    } catch (error) {
      state.display.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.display.switching = false;
      rerenderDisplayPanel();
      if (!state.display.error) {
        void loadDisplayModes();
      }
    }
  }

  async function sendDisplayModeRequest(path, bodyPayload, statusText) {
    state.display.modesSaving = true;
    state.display.error = "";
    state.display.status = statusText;
    rerenderDisplayPanel();

    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify(bodyPayload),
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.display.modesSnapshot = payload && typeof payload === "object" ? payload : null;
      state.display.status = state.display.modesSnapshot?.statusText || statusText;
      return true;
    } catch (error) {
      state.display.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.display.modesSaving = false;
      rerenderDisplayPanel();
    }
  }

  async function setDisplayResolutionPreset(presetId, title) {
    await sendDisplayModeRequest(
      "api/display/resolution",
      { value: presetId },
      `Setting ${title} resolution...`,
    );
  }

  async function setDisplayRefreshRatePreset(refreshRate) {
    await sendDisplayModeRequest(
      "api/display/refresh-rate",
      { value: Number(refreshRate) },
      `Setting ${refreshRate}Hz...`,
    );
  }

  async function sendPowerRequest(path, statusText, options = {}) {
    if (options.confirmText && state.power.confirmingPath !== path) {
      state.power.confirmingPath = path;
      state.power.error = "";
      state.power.status = options.confirmText;
      rerenderPowerPanel();
      return;
    }

    state.power.confirmingPath = "";
    state.power.actioning = true;
    state.power.error = "";
    state.power.status = statusText;
    rerenderPowerPanel();

    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
      });

      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The request failed (${response.status}).`);
      }

      state.power.status = payload?.message || statusText;
    } catch (error) {
      state.power.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.power.actioning = false;
      rerenderPowerPanel();
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

    tab.strTitle = "Tools for Steam";
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
    ensureVolumeSliderHotkeys();
    ensureAudioDashboardHotkeys();
    ensurePerformanceSliderHotkeys();
    ensureHomeReorderHotkeys();
    updateHomeReorderInputCapture();

    if (shouldLoadFrontendComponentRegistry()) {
      void loadFrontendComponentRegistry();
    }

    if (!state.themes.loading && !state.themes.snapshot && !state.themes.error) {
      void loadThemesState();
    }

    if (!state.generalSettings.loading && !state.generalSettings.snapshot && !state.generalSettings.error) {
      void loadGeneralSettingsState();
    }

    if (!state.updates.loading && !state.updates.snapshot && !state.updates.error) {
      void loadUpdateState();
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
