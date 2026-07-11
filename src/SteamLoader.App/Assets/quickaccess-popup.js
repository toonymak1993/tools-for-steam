(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const stateVersion = 104;
  const globalBackSlotKey = "global-back";
  const sliderCommitSettleDelayMs = 180;
  const smartHomeSliderCommitSettleDelayMs = 1000;
  const smartHomeSliderCommitRetryDelayMs = 140;
  const audioVolumeCommitSettleDelayMs = 260;
  const soundtrackTabKey = 7;
  const storeSyncPinnedTitlesStorageKey = "steamloader.storeSyncPinnedTitles.v1";
  const pluginStoreInputStorageKey = "ToolsForSteamPluginStoreInput";
  const pluginStoreOverlayStateStorageKey = "ToolsForSteamPluginStoreOverlayState";
  const pluginStoreChannelName = "ToolsForSteamPluginStoreChannel";
  const storefrontEnabled = false;

  window.__steamLoaderApiBase = apiBase;

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

  if (window.__steamToolsUpdatesPollTimer) {
    window.clearInterval(window.__steamToolsUpdatesPollTimer);
    window.__steamToolsUpdatesPollTimer = null;
  }

  if (window.__steamToolsSmartHomePollTimer) {
    window.clearInterval(window.__steamToolsSmartHomePollTimer);
    window.__steamToolsSmartHomePollTimer = null;
  }

  if (window.__steamLoaderFocusRepairTimer) {
    window.clearInterval(window.__steamLoaderFocusRepairTimer);
    window.__steamLoaderFocusRepairTimer = null;
  }

  if (window.__steamLoaderFocusRepairHandler) {
    document.removeEventListener("focusout", window.__steamLoaderFocusRepairHandler, true);
    window.__steamLoaderFocusRepairHandler = null;
  }

  const previousState = window.__steamLoaderPopupReactState;
  if (previousState?.version !== stateVersion) {
    try {
      previousState?.liveUpdates?.source?.close?.();
    } catch {
    }

    if (previousState?.liveUpdates?.retryTimer) {
      window.clearTimeout(previousState.liveUpdates.retryTimer);
    }

    if (previousState?.pluginStoreBridge?.activationFallbackTimer) {
      window.clearTimeout(previousState.pluginStoreBridge.activationFallbackTimer);
    }

    if (previousState?.pluginStoreBridge?.quickAccessRestoreTimer) {
      window.clearTimeout(previousState.pluginStoreBridge.quickAccessRestoreTimer);
    }

    if (previousState?.pluginStoreBridge?.overlayStatePollTimer) {
      window.clearInterval(previousState.pluginStoreBridge.overlayStatePollTimer);
    }

    if (typeof previousState?.pluginStoreBridge?.overlayStateStorageHandler === "function") {
      window.removeEventListener("storage", previousState.pluginStoreBridge.overlayStateStorageHandler);
    }

    if (typeof previousState?.pluginStoreBridge?.keyHandler === "function") {
      window.removeEventListener("keydown", previousState.pluginStoreBridge.keyHandler, true);
      window.removeEventListener("keyup", previousState.pluginStoreBridge.keyHandler, true);
      window.removeEventListener("keypress", previousState.pluginStoreBridge.keyHandler, true);
    }

    const focusNav = window.FocusNavController;
    if (
      focusNav?.SetCatchAllGamepadInput &&
      previousState?.pluginStoreBridge?.catchAllInstalled &&
      focusNav.m_fnCatchAllGamepadInput?.__steamLoaderPluginStoreQuickAccessCatchAll
    ) {
      focusNav.SetCatchAllGamepadInput(
        previousState.pluginStoreBridge.previousCatchAllGamepadInput || undefined,
      );
    }

    if (
      previousState?.pluginStoreBridge?.channel &&
      previousState?.pluginStoreBridge?.channelHandler
    ) {
      previousState.pluginStoreBridge.channel.removeEventListener(
        "message",
        previousState.pluginStoreBridge.channelHandler,
      );
    }

    try {
      previousState?.pluginStoreBridge?.channel?.close?.();
    } catch {
    }

    document.getElementById("steamloader-plugin-store-quickaccess-bridge-style")?.remove();
    document.body?.classList?.remove("steamloader-plugin-store-remote-active");
  }

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
            pendingOverlayLevelCommit: null,
            suppressNextLivePanelRerender: false,
            settingCommitTimersByKey: {},
            pendingSliderAutoFocus: false,
          },
          handheldPerformance: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
            tdpCommitTimer: 0,
            tdpMutationSequence: 0,
            globalTdpCommitTimers: {},
            globalTdpMutationSequences: {},
            profileTdpCommitTimers: {},
            profileTdpMutationSequences: {},
            editingProfileKey: "",
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
            unifySteamAuthDraftByStoreId: {},
            unifySteamAuthInputVersionByStoreId: {},
            artworkPreviewByTitleId: {},
            artworkPreviewLoadingByTitleId: {},
            pinnedTitleIds: readStoreSyncPinnedTitleIds(),
          },
          themes: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
            profileDraft: "",
            profileDraftInputVersion: 0,
            storeLoading: false,
            storeCatalog: null,
            storeCatalogRequestSequence: 0,
            storeSearchDraft: "",
            storeSearchInputVersion: 0,
            storeDetailById: {},
            storeDetailLoadingId: "",
            installedPreviewByThemeId: {},
            installedPreviewLoadingByThemeId: {},
            operationText: "",
            sliderCommitTimersByKey: {},
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
          communityPlugins: {
            loading: false,
            error: "",
            snapshot: null,
            scriptVersionsById: {},
            scriptPromisesById: {},
            scriptErrorsById: {},
            sdkById: {},
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
          smartHome: {
            loading: false,
            saving: false,
            error: "",
            snapshot: null,
            baseUrlDraft: "",
            baseUrlInputVersion: 0,
            homeyIdDraft: "",
            homeyIdInputVersion: 0,
            sessionTokenDraft: "",
            sessionTokenInputVersion: 0,
            sliderCommitTimersByKey: {},
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
          renderedSlots: [],
          renderRevision: 1,
          panelObserver: null,
          panelObserverHost: null,
          trackedPanel: null,
          trackedPanelScrollHandler: null,
          trackedPanelFocusHandler: null,
          trackedPanelFocusOutHandler: null,
          panelVisible: false,
          pendingEntryAutoFocus: true,
          lastSelectedIndexByRoute: {},
          lastSelectedSlotKeyByRoute: {},
          lastScrollTopByRoute: {},
          expandedSectionsByRoute: {},
          pendingFocusRouteKey: null,
          pendingFocusIndex: null,
          pendingFocusSlotKey: null,
          pendingFocusRestoreAnimationFrame: 0,
          pendingFocusRestoreRouteKey: null,
          pendingScrollRouteKey: null,
          pendingScrollTop: null,
          pendingScrollAnimationFrame: 0,
          installedPanelKey: null,
          installedPanelRevision: -1,
          installedPanelElement: null,
          editorFocusActive: false,
          editorFocusCardKey: null,
          editorFocusRouteKey: null,
          editorSelectionByKey: {},
          liveUpdates: {
            source: null,
            connected: false,
            retryTimer: 0,
            lastMessageAt: 0,
          },
          optimistic: {
            desiredValuesByKey: {},
          },
          pluginStoreBridge: {
            remoteActive: false,
            remoteActiveExpiresAt: 0,
            lastOverlayStateNonce: "",
            lastOverlayStateAt: 0,
            activationFallbackTimer: 0,
            overlayStatePollTimer: 0,
            overlayStateStorageHandler: null,
            channel: null,
            channelHandler: null,
            catchAllInstalled: false,
            previousCatchAllGamepadInput: null,
            catchAllButtonState: {},
            keyHandler: null,
            quickAccessClosedForStore: false,
            quickAccessRestoreTimer: 0,
            quickAccessRestoreAttempts: 0,
          },
          steamKeyboardActiveUntil: 0,
        });

  function playSoundFile(path) {
    try {
      const audio = new Audio(path);
      audio.volume = 0.72;
      const promise = audio.play();
      if (promise && typeof promise.catch === "function") {
        promise.catch(() => {});
      }
      return true;
    } catch {
      return false;
    }
  }

  function playSliderMoveSound(direction) {
    if (!direction) {
      return false;
    }

    return (
      playSoundFile(
        direction < 0
          ? "/sounds/deck_ui_slider_down.wav"
          : "/sounds/deck_ui_slider_up.wav",
      ) ||
      Boolean(window.STFrontendLib?.playUiSound?.())
    );
  }

  function ensurePluginStoreBridgeStyle() {
    if (document.getElementById("steamloader-plugin-store-quickaccess-bridge-style")) {
      return;
    }

    const style = document.createElement("style");
    style.id = "steamloader-plugin-store-quickaccess-bridge-style";
    style.textContent = `
      body.steamloader-plugin-store-remote-active {
        background: transparent !important;
      }

      body.steamloader-plugin-store-remote-active #QuickAccess-NA {
        display: none !important;
        opacity: 0 !important;
        visibility: hidden !important;
        pointer-events: none !important;
        transition: none !important;
      }
    `;
    document.head.append(style);
  }

  function getPluginStoreBridgeChannel() {
    const bridge = state.pluginStoreBridge;
    if (bridge.channel || typeof BroadcastChannel !== "function") {
      return bridge.channel;
    }

    try {
      bridge.channel = new BroadcastChannel(pluginStoreChannelName);
      bridge.channelHandler = (event) => {
        handlePluginStoreBridgeMessage(event.data);
      };
      bridge.channel.addEventListener("message", bridge.channelHandler);
    } catch {
      bridge.channel = null;
    }

    return bridge.channel;
  }

  function postPluginStoreBridgeMessage(message) {
    const payload = {
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      ...message,
    };

    try {
      getPluginStoreBridgeChannel()?.postMessage(payload);
    } catch {
    }

    try {
      const key = payload.type === "input"
        ? pluginStoreInputStorageKey
        : pluginStoreOverlayStateStorageKey;
      localStorage.setItem(key, JSON.stringify(payload));
    } catch {
    }
  }

  function getPluginStoreActionFromSteamButton(button) {
    const namedButton = String(button || "").toUpperCase();
    if (/(DPAD|GAMEPAD|ARROW).*UP|\bUP\b/.test(namedButton)) {
      return "up";
    }
    if (/(DPAD|GAMEPAD|ARROW).*DOWN|\bDOWN\b/.test(namedButton)) {
      return "down";
    }
    if (/(DPAD|GAMEPAD|ARROW).*LEFT|\bLEFT\b/.test(namedButton)) {
      return "left";
    }
    if (/(DPAD|GAMEPAD|ARROW).*RIGHT|\bRIGHT\b/.test(namedButton)) {
      return "right";
    }
    if (/\b(A|ACCEPT|SELECT)\b/.test(namedButton)) {
      return "a";
    }
    if (/\b(B|BACK|CANCEL)\b/.test(namedButton)) {
      return "b";
    }
    if (/(LEFT|L).*(BUMPER|SHOULDER|TRIGGER)|\b(LB|L1)\b/.test(namedButton)) {
      return "previous-section";
    }
    if (/(RIGHT|R).*(BUMPER|SHOULDER|TRIGGER)|\b(RB|R1)\b/.test(namedButton)) {
      return "next-section";
    }

    switch (Number(button)) {
      case 1:
        return "a";
      case 2:
        return "b";
      case 5:
      case 7:
        return "previous-section";
      case 6:
      case 8:
        return "next-section";
      case 9:
        return "up";
      case 10:
        return "down";
      case 11:
        return "left";
      case 12:
        return "right";
      default:
        return "";
    }
  }

  function getPluginStoreActionFromKeyEvent(event) {
    const key = event.key || event.code || "";
    const lowerKey = key.toLowerCase?.() || "";
    if (key === "ArrowUp" || key === "GamepadUp" || key === "GamepadDPadUp") {
      return "up";
    }
    if (key === "ArrowDown" || key === "GamepadDown" || key === "GamepadDPadDown") {
      return "down";
    }
    if (key === "ArrowLeft" || key === "GamepadLeft" || key === "GamepadDPadLeft") {
      return "left";
    }
    if (key === "ArrowRight" || key === "GamepadRight" || key === "GamepadDPadRight") {
      return "right";
    }
    if (key === "Enter" || key === " " || key === "Space" || key === "GamepadA") {
      return "a";
    }
    if (key === "Escape" || key === "Backspace" || key === "GamepadB") {
      return "b";
    }
    if (
      key === "PageUp" ||
      key === "GamepadLB" ||
      key === "GamepadL1" ||
      key === "GamepadLeftShoulder" ||
      lowerKey === "["
    ) {
      return "previous-section";
    }
    if (
      key === "PageDown" ||
      key === "GamepadRB" ||
      key === "GamepadR1" ||
      key === "GamepadRightShoulder" ||
      lowerKey === "]"
    ) {
      return "next-section";
    }

    return "";
  }

  function shouldForwardPluginStoreSteamButton(button, action) {
    const bridge = state.pluginStoreBridge;
    const now = Date.now();
    const repeatMs = action === "up" || action === "down" || action === "left" || action === "right"
      ? 230
      : 340;
    const lastMs = bridge.catchAllButtonState[button] || 0;
    if (now - lastMs < repeatMs) {
      return false;
    }

    bridge.catchAllButtonState[button] = now;
    return true;
  }

  function sendPluginStoreInput(action, source) {
    if (!action) {
      return;
    }

    postPluginStoreBridgeMessage({
      type: "input",
      action,
      source,
    });

    fetch(`${apiBase}api/plugin-store/overlay/input`, {
      method: "POST",
      cache: "no-store",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        action,
        source,
      }),
    }).catch(() => {
    });
  }

  function installPluginStoreCatchAllInput() {
    const bridge = state.pluginStoreBridge;
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || bridge.catchAllInstalled) {
      return;
    }

    const previous = focusNav.m_fnCatchAllGamepadInput;
    const callback = (button) => {
      if (!bridge.remoteActive) {
        return typeof previous === "function" ? previous(button) : false;
      }

      const action = getPluginStoreActionFromSteamButton(button);
      if (!action) {
        return true;
      }

      if (shouldForwardPluginStoreSteamButton(button, action)) {
        sendPluginStoreInput(action, "quickaccess-catch-all");
      }

      return true;
    };

    callback.__steamLoaderPluginStoreQuickAccessCatchAll = true;
    bridge.previousCatchAllGamepadInput =
      previous?.__steamLoaderPluginStoreQuickAccessCatchAll ? null : previous;
    focusNav.SetCatchAllGamepadInput(callback);
    bridge.catchAllInstalled = true;
  }

  function uninstallPluginStoreCatchAllInput() {
    const bridge = state.pluginStoreBridge;
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || !bridge.catchAllInstalled) {
      return;
    }

    if (focusNav.m_fnCatchAllGamepadInput?.__steamLoaderPluginStoreQuickAccessCatchAll) {
      focusNav.SetCatchAllGamepadInput(bridge.previousCatchAllGamepadInput || undefined);
    }

    bridge.catchAllInstalled = false;
    bridge.previousCatchAllGamepadInput = null;
    bridge.catchAllButtonState = {};
  }

  function handlePluginStoreBridgeKeyEvent(event) {
    const bridge = state.pluginStoreBridge;
    if (!bridge.remoteActive) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation?.();

    if (event.type !== "keydown") {
      return;
    }

    sendPluginStoreInput(getPluginStoreActionFromKeyEvent(event), "quickaccess-key");
  }

  function installPluginStoreKeyTrap() {
    const bridge = state.pluginStoreBridge;
    if (!bridge.keyHandler) {
      bridge.keyHandler = handlePluginStoreBridgeKeyEvent;
      window.addEventListener("keydown", bridge.keyHandler, true);
      window.addEventListener("keyup", bridge.keyHandler, true);
      window.addEventListener("keypress", bridge.keyHandler, true);
    }
  }

  function uninstallPluginStoreKeyTrap() {
    const bridge = state.pluginStoreBridge;
    if (!bridge.keyHandler) {
      return;
    }

    window.removeEventListener("keydown", bridge.keyHandler, true);
    window.removeEventListener("keyup", bridge.keyHandler, true);
    window.removeEventListener("keypress", bridge.keyHandler, true);
    bridge.keyHandler = null;
  }

  function updatePluginStoreRemoteUi() {
    ensurePluginStoreBridgeStyle();
    document.body?.classList?.toggle(
      "steamloader-plugin-store-remote-active",
      Boolean(state.pluginStoreBridge.remoteActive),
    );
  }

  function schedulePluginStoreBridgeFallbackRelease() {
    const bridge = state.pluginStoreBridge;
    if (bridge.activationFallbackTimer) {
      window.clearTimeout(bridge.activationFallbackTimer);
    }

    bridge.activationFallbackTimer = window.setTimeout(() => {
      bridge.activationFallbackTimer = 0;
      if (bridge.remoteActive && Date.now() > bridge.remoteActiveExpiresAt) {
        setPluginStoreRemoteActive(false);
      }
    }, 3800);
  }

  function clearPluginStoreQuickAccessRestoreTimer() {
    const bridge = state.pluginStoreBridge;
    if (bridge.quickAccessRestoreTimer) {
      window.clearTimeout(bridge.quickAccessRestoreTimer);
      bridge.quickAccessRestoreTimer = 0;
    }
  }

  function schedulePluginStoreQuickAccessRestore(delayMs = 140) {
    const bridge = state.pluginStoreBridge;
    if (!bridge.quickAccessClosedForStore) {
      return;
    }

    clearPluginStoreQuickAccessRestoreTimer();
    bridge.quickAccessRestoreTimer = window.setTimeout(() => {
      bridge.quickAccessRestoreTimer = 0;
      if (bridge.remoteActive) {
        return;
      }

      bridge.quickAccessRestoreAttempts += 1;
      const restored = tryOpenQuickAccessMenuForPluginStore();
      if (!restored && bridge.quickAccessRestoreAttempts < 6) {
        schedulePluginStoreQuickAccessRestore(180);
        return;
      }

      bridge.quickAccessClosedForStore = false;
      bridge.quickAccessRestoreAttempts = 0;
    }, delayMs);
  }

  function setPluginStoreRemoteActive(active, options = {}) {
    const bridge = state.pluginStoreBridge;
    const wasActive = Boolean(bridge.remoteActive);
    const nextActive = Boolean(active);
    bridge.remoteActive = nextActive;
    bridge.remoteActiveExpiresAt = nextActive
      ? Date.now() + (options.fromOverlay ? 2200 : 3600)
      : 0;

    if (nextActive) {
      clearPluginStoreQuickAccessRestoreTimer();
      bridge.quickAccessRestoreAttempts = 0;
      installPluginStoreCatchAllInput();
      installPluginStoreKeyTrap();
      schedulePluginStoreBridgeFallbackRelease();
    } else {
      uninstallPluginStoreKeyTrap();
      uninstallPluginStoreCatchAllInput();
      if (bridge.activationFallbackTimer) {
        window.clearTimeout(bridge.activationFallbackTimer);
        bridge.activationFallbackTimer = 0;
      }

      if (wasActive) {
        schedulePluginStoreQuickAccessRestore();
      }
    }

    updatePluginStoreRemoteUi();
  }

  function consumePluginStoreOverlayState(raw) {
    if (!raw) {
      return;
    }

    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type !== "overlay-state" || payload.nonce === state.pluginStoreBridge.lastOverlayStateNonce) {
        return;
      }

      state.pluginStoreBridge.lastOverlayStateNonce = payload.nonce;
      state.pluginStoreBridge.lastOverlayStateAt = Date.now();
      const stillFresh = !payload.expiresAt || Number(payload.expiresAt) > Date.now();
      setPluginStoreRemoteActive(Boolean(payload.active) && stillFresh, { fromOverlay: true });
    } catch {
    }
  }

  function handlePluginStoreBridgeMessage(payload) {
    if (payload?.type === "overlay-state") {
      consumePluginStoreOverlayState(payload);
    }
  }

  function setupPluginStoreBridge() {
    getPluginStoreBridgeChannel();
    ensurePluginStoreBridgeStyle();

    const bridge = state.pluginStoreBridge;
    if (!bridge.overlayStateStorageHandler) {
      bridge.overlayStateStorageHandler = (event) => {
        if (event.key === pluginStoreOverlayStateStorageKey) {
          consumePluginStoreOverlayState(event.newValue);
        }
      };
      window.addEventListener("storage", bridge.overlayStateStorageHandler);
    }

    if (!bridge.overlayStatePollTimer) {
      bridge.overlayStatePollTimer = window.setInterval(() => {
        try {
          consumePluginStoreOverlayState(localStorage.getItem(pluginStoreOverlayStateStorageKey));
        } catch {
        }
      }, 250);
    }
  }

  function tryInvokePluginStoreCloseCandidate(target, methodNames, argsList = [[]]) {
    if (!target) {
      return false;
    }

    for (const methodName of methodNames) {
      const method = target?.[methodName];
      if (typeof method !== "function") {
        continue;
      }

      for (const args of argsList) {
        try {
          method.apply(target, args);
          return true;
        } catch {
        }
      }
    }

    return false;
  }

  function tryCloseQuickAccessMenuForPluginStore() {
    const closeMethodNames = [
      "CloseSideMenus",
      "CloseSideMenu",
      "HideSideMenus",
      "HideSideMenu",
      "DismissSideMenus",
      "DismissSideMenu",
      "CloseQuickAccessMenu",
      "HideQuickAccessMenu",
      "DismissQuickAccessMenu",
    ];
    const setVisibleMethodNames = [
      "SetQuickAccessMenuVisible",
      "SetQuickAccessVisible",
      "SetSideMenuVisible",
      "SetSideMenuOpen",
    ];
    const candidates = [
      window.GamepadUI,
      window.GamepadUI?.Router,
      window.GamepadUI?.NavigationManager,
      window.SteamUIStore,
      window.SteamUIStore?.MenuStore,
      window.SteamUIStore?.SideMenuStore,
      window.SteamClient?.UI,
      window.SteamClient?.Overlay,
      window.SteamClient?.Input,
      window.SteamClient?.System,
      window.SteamClient,
    ];

    for (const candidate of candidates) {
      if (
        tryInvokePluginStoreCloseCandidate(candidate, closeMethodNames, [[]]) ||
        tryInvokePluginStoreCloseCandidate(candidate, setVisibleMethodNames, [
          [false],
          ["quickaccess", false],
          ["QuickAccess", false],
          ["quick-access", false],
          ["quickAccess", false],
        ])
      ) {
        return true;
      }
    }

    const runtime = findRuntime();
    const propsList = [
      runtime?.qamNode?.memoizedProps,
      runtime?.qamNode?.pendingProps,
      runtime?.qamNode?.return?.memoizedProps,
      runtime?.qamNode?.return?.pendingProps,
    ].filter(Boolean);

    for (const props of propsList) {
      for (const [key, value] of Object.entries(props)) {
        if (typeof value !== "function" || !/(close|dismiss|hide|cancel)/i.test(key)) {
          continue;
        }

        try {
          value(false);
          return true;
        } catch {
          try {
            value();
            return true;
          } catch {
          }
        }
      }
    }

    return false;
  }

  function tryOpenQuickAccessMenuForPluginStore() {
    const openMethodNames = [
      "OpenQuickAccessMenu",
      "ShowQuickAccessMenu",
      "OpenSideMenus",
      "OpenSideMenu",
      "ShowSideMenus",
      "ShowSideMenu",
    ];
    const setVisibleMethodNames = [
      "SetQuickAccessMenuVisible",
      "SetQuickAccessVisible",
      "SetSideMenuVisible",
      "SetSideMenuOpen",
    ];
    const menuArgs = [
      [],
      ["quickaccess"],
      ["QuickAccess"],
      ["quick-access"],
      ["quickAccess"],
    ];
    const candidates = [
      window.GamepadUI,
      window.GamepadUI?.Router,
      window.GamepadUI?.NavigationManager,
      window.SteamUIStore,
      window.SteamUIStore?.MenuStore,
      window.SteamUIStore?.SideMenuStore,
      window.SteamClient?.UI,
      window.SteamClient?.Overlay,
      window.SteamClient?.Input,
      window.SteamClient?.System,
      window.SteamClient,
    ];

    for (const candidate of candidates) {
      if (
        tryInvokePluginStoreCloseCandidate(candidate, openMethodNames, menuArgs) ||
        tryInvokePluginStoreCloseCandidate(candidate, setVisibleMethodNames, [
          [true],
          ["quickaccess", true],
          ["QuickAccess", true],
          ["quick-access", true],
          ["quickAccess", true],
        ])
      ) {
        return true;
      }
    }

    const runtime = findRuntime();
    const propsList = [
      runtime?.qamNode?.memoizedProps,
      runtime?.qamNode?.pendingProps,
      runtime?.qamNode?.return?.memoizedProps,
      runtime?.qamNode?.return?.pendingProps,
    ].filter(Boolean);

    for (const props of propsList) {
      for (const [key, value] of Object.entries(props)) {
        if (typeof value !== "function" || !/(open|show|display|visible)/i.test(key)) {
          continue;
        }

        try {
          value(true);
          return true;
        } catch {
          try {
            value();
            return true;
          } catch {
          }
        }
      }
    }

    return false;
  }

  function closeQuickAccessMenuForPluginStoreSession() {
    const bridge = state.pluginStoreBridge;
    clearPluginStoreQuickAccessRestoreTimer();
    bridge.quickAccessRestoreAttempts = 0;
    const closed = tryCloseQuickAccessMenuForPluginStore();
    if (closed) {
      bridge.quickAccessClosedForStore = true;
    }

    return closed;
  }

  function normalizeOptimisticValueKey(key) {
    if (typeof key !== "string") {
      return "";
    }

    return key.trim();
  }

  function setOptimisticDesiredValue(key, value) {
    const resolvedKey = normalizeOptimisticValueKey(key);
    if (!resolvedKey) {
      return "";
    }

    state.optimistic.desiredValuesByKey[resolvedKey] = value;
    return resolvedKey;
  }

  function getOptimisticDesiredValue(key) {
    const resolvedKey = normalizeOptimisticValueKey(key);
    if (!resolvedKey) {
      return undefined;
    }

    return state.optimistic.desiredValuesByKey[resolvedKey];
  }

  function hasOptimisticDesiredValue(key) {
    const resolvedKey = normalizeOptimisticValueKey(key);
    return Boolean(
      resolvedKey &&
      Object.prototype.hasOwnProperty.call(state.optimistic.desiredValuesByKey, resolvedKey)
    );
  }

  function canApplyOptimisticResponse(key, value) {
    if (!hasOptimisticDesiredValue(key)) {
      return true;
    }

    return Object.is(getOptimisticDesiredValue(key), value);
  }

  function clearOptimisticDesiredValue(key, expectedValue) {
    const resolvedKey = normalizeOptimisticValueKey(key);
    if (!resolvedKey || !hasOptimisticDesiredValue(resolvedKey)) {
      return false;
    }

    if (arguments.length > 1 && !Object.is(getOptimisticDesiredValue(resolvedKey), expectedValue)) {
      return false;
    }

    delete state.optimistic.desiredValuesByKey[resolvedKey];
    return true;
  }

  function getOptimisticDesiredEntries(prefix = "") {
    const normalizedPrefix = normalizeOptimisticValueKey(prefix);
    return Object.entries(state.optimistic.desiredValuesByKey).filter(([key]) =>
      normalizedPrefix ? key.startsWith(normalizedPrefix) : true,
    );
  }

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
      id: "unifystore",
      title: "Storefront",
      description: "Fullscreen launcher for Epic and GOG libraries",
      pages: [],
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
      title: "FPS Overlay",
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
      title: "CSSLoader",
      description: "Manage installed themes, store installs, presets, and backend tools",
      pages: [
        {
          id: "installed",
          title: "Installed Themes",
          description: "Enable installed CSSLoader themes and change controller-friendly options",
        },
        {
          id: "store",
          title: "Store",
          description: "Browse Big Picture themes from DeckThemes and install them into CSSLoader",
        },
        {
          id: "profiles",
          title: "Presets",
          description: "Save and reapply full CSSLoader theme stacks",
        },
        {
          id: "settings",
          title: "Settings",
          description: "Backend status, theme files, and CSSLoader shortcuts",
        },
      ],
    },
    {
      id: "smart-home",
      title: "Homey",
      description: "Rooms, lights, moods, colors, and Homey flows",
      defaultEnabled: false,
      pages: [
        {
          id: "rooms",
          title: "Rooms",
          description: "Browse zones and expand controllable devices room by room",
        },
        {
          id: "moods",
          title: "Moods",
          description: "Apply Homey moods and room scenes directly from Quick Access",
        },
        {
          id: "flows",
          title: "Flows",
          description: "Trigger Homey flows and scenes directly from Quick Access",
        },
        {
          id: "settings",
          title: "Settings",
          description: "Save the Homey address, session token, and provider foundation",
        },
      ],
    },
  ].filter((plugin) => storefrontEnabled || plugin.id !== "unifystore");

  function getPluginSettings() {
    const entries = state.generalSettings.snapshot?.plugins;
    return Array.isArray(entries) ? entries : [];
  }

  function getPluginSettingsEntry(pluginId) {
    return getPluginSettings().find((entry) => entry.id === pluginId) || null;
  }

  function isPluginAvailable(pluginId) {
    if (pluginId === "handheld-performance") {
      return Boolean(state.generalSettings.snapshot?.handheldPerformanceAvailable);
    }

    return true;
  }

  function getCommunityRuntimePlugins() {
    const pluginsSnapshot = state.communityPlugins.snapshot?.plugins;
    return Array.isArray(pluginsSnapshot) ? pluginsSnapshot : [];
  }

  function getCommunityRegistry() {
    return window.ToolsForSteamCommunityPlugins && typeof window.ToolsForSteamCommunityPlugins === "object"
      ? window.ToolsForSteamCommunityPlugins
      : {};
  }

  function normalizeCommunityPluginDefinition(runtimePlugin) {
    const pluginId = String(runtimePlugin?.id || "").trim();
    if (!pluginId) {
      return null;
    }

    const registryEntry = getCommunityRegistry()[pluginId] || null;
    const manifest = registryEntry?.manifest || {};
    const title = String(manifest.name || runtimePlugin.title || pluginId).trim();
    const description = String(
      manifest.description ||
      runtimePlugin.description ||
      "Community plugin.",
    ).trim();

    return {
      id: pluginId,
      title,
      description,
      pages: Array.isArray(registryEntry?.pages) ? registryEntry.pages : [],
      defaultEnabled: true,
      isCommunity: true,
      registry: registryEntry,
      runtime: runtimePlugin,
      loadError: state.communityPlugins.scriptErrorsById?.[pluginId] || "",
    };
  }

  function getCommunityPluginDefinitions() {
    return getCommunityRuntimePlugins()
      .map((plugin) => normalizeCommunityPluginDefinition(plugin))
      .filter(Boolean);
  }

  function getCommunityPluginDefinition(pluginId) {
    return getCommunityPluginDefinitions().find((plugin) => plugin.id === pluginId) || null;
  }

  function getPluginDefinition(pluginId) {
    if (pluginId === "handheld-performance" && isPluginAvailable(pluginId)) {
      return {
        id: "handheld-performance",
        title: state.generalSettings.snapshot?.handheldPerformanceTitle || "Handheld Performance",
        description: "Automatic per-game TDP profiles and device power controls",
        pages: [],
        isSystemCategory: true,
      };
    }

    return plugins.find((plugin) => plugin.id === pluginId) || getCommunityPluginDefinition(pluginId);
  }

  function isPluginEnabled(pluginId) {
    if (!pluginId || pluginId === "settings") {
      return true;
    }

    if (getCommunityPluginDefinition(pluginId)) {
      return true;
    }

    const entry = getPluginSettingsEntry(pluginId);
    if (entry) {
      return entry.enabled !== false || entry.canDisable === false;
    }

    const definition = getPluginDefinition(pluginId);
    return definition ? definition.defaultEnabled !== false : true;
  }

  function getDefaultPluginOrderIds() {
    return plugins
      .filter((plugin) => plugin.id !== "settings" && isPluginAvailable(plugin.id))
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
    const builtInEntries = sortPluginsBySavedOrder(
      plugins.filter((plugin) => isPluginAvailable(plugin.id) && isPluginEnabled(plugin.id)),
    );
    const communityEntries = getCommunityPluginDefinitions()
      .filter((plugin) => isPluginEnabled(plugin.id))
      .sort((left, right) => left.title.localeCompare(right.title));
    return [...builtInEntries, ...communityEntries];
  }

  function getHomePlugins() {
    const entries = getVisiblePlugins().filter((plugin) => plugin.id !== "settings");
    const handheldPerformance = getPluginDefinition("handheld-performance");
    return handheldPerformance ? [handheldPerformance, ...entries] : entries;
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

      .steamloader-top-stack {
        margin-bottom: 8px;
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

      .steamloader-inline-section {
        display: flex;
        gap: 10px;
        align-items: flex-start;
        margin-top: 12px;
        padding: 2px 4px 0;
      }

      .steamloader-inline-section-mark {
        width: 28px;
        height: 28px;
        border-radius: 9px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        flex-shrink: 0;
        background: rgba(255, 255, 255, 0.06);
        color: rgba(226, 233, 240, 0.92);
      }

      .steamloader-inline-section-copy-wrap {
        min-width: 0;
      }

      .steamloader-inline-section-title {
        color: rgba(226, 233, 240, 0.94);
        font-size: clamp(12px, 1.36vw, 14px);
        line-height: 1.2;
        font-weight: 800;
        letter-spacing: 0.05em;
        text-transform: uppercase;
      }

      .steamloader-inline-section-copy {
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

      .steamloader-row-icon img {
        width: 100%;
        height: 100%;
        display: block;
        border-radius: inherit;
        object-fit: cover;
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

      .steamloader-row-swatch {
        margin-top: 7px;
        display: inline-flex;
        align-items: center;
        gap: 8px;
        color: rgba(205, 214, 224, 0.92);
        font-size: clamp(10px, 1.2vw, 13px);
        line-height: 1.2;
      }

      .steamloader-row-swatch-dot {
        width: 10px;
        height: 10px;
        border-radius: 999px;
        box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.12), 0 0 10px rgba(255, 255, 255, 0.08);
        flex: 0 0 auto;
      }

      .steamloader-row-swatch-label {
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
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

      .steamloader-dialog-button-accordion {
        min-height: 0 !important;
        padding: 8px 10px !important;
        border-radius: 12px !important;
      }

      .steamloader-accordion-toggle {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        width: 100%;
      }

      .steamloader-accordion-toggle-copy-wrap {
        min-width: 0;
        flex: 1 1 auto;
      }

      .steamloader-accordion-toggle-title {
        color: rgba(226, 233, 240, 0.92);
        font-size: clamp(12px, 1.36vw, 14px);
        line-height: 1.2;
        font-weight: 700;
        letter-spacing: 0.04em;
        text-transform: uppercase;
      }

      .steamloader-accordion-toggle-copy {
        margin-top: 3px;
        color: rgba(145, 157, 169, 0.86);
        font-size: clamp(10px, 1.08vw, 12px);
        line-height: 1.32;
      }

      .steamloader-accordion-toggle-arrow {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 16px;
        color: rgba(187, 197, 208, 0.78);
        font-size: 12px;
        line-height: 1;
        text-transform: lowercase;
        transform: rotate(0deg);
        transition: transform 140ms ease;
      }

      .steamloader-accordion-toggle.is-expanded .steamloader-accordion-toggle-arrow {
        transform: rotate(180deg);
      }

      .steamloader-dialog-button-feature {
        min-height: 0 !important;
        padding: 0 !important;
        border-radius: 18px !important;
        overflow: hidden;
        background: transparent !important;
      }

      .steamloader-dialog-button-feature:hover,
      .steamloader-dialog-button-feature:focus-visible,
      .steamloader-dialog-button-feature.gpfocus {
        background: transparent !important;
        box-shadow: none !important;
      }

      .steamloader-feature-card {
        overflow: hidden;
        border-radius: 18px;
        border: 1px solid rgba(120, 153, 191, 0.14);
        background:
          linear-gradient(180deg, rgba(25, 35, 49, 0.96) 0%, rgba(18, 25, 34, 0.98) 100%);
      }

      .steamloader-dialog-button-feature:hover .steamloader-feature-card,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-card,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-card {
        border-color: rgba(131, 188, 255, 0.82);
        box-shadow:
          0 0 0 2px rgba(131, 188, 255, 0.78),
          0 14px 30px rgba(6, 12, 19, 0.34);
      }

      .steamloader-feature-media-shell {
        position: relative;
        aspect-ratio: 2.12 / 1;
        overflow: hidden;
        background:
          radial-gradient(circle at top left, rgba(102, 166, 255, 0.24) 0%, rgba(12, 17, 23, 0) 38%),
          rgba(8, 13, 19, 0.84);
      }

      .steamloader-feature-media {
        width: 100%;
        height: 100%;
        display: block;
        object-fit: cover;
      }

      .steamloader-feature-media-placeholder {
        width: 100%;
        height: 100%;
        display: flex;
        align-items: center;
        justify-content: center;
        color: rgba(222, 230, 237, 0.78);
      }

      .steamloader-feature-media-placeholder svg {
        width: 34px;
        height: 34px;
      }

      .steamloader-feature-eyebrow {
        position: absolute;
        top: 10px;
        left: 10px;
        display: inline-flex;
        align-items: center;
        padding: 4px 8px;
        border-radius: 999px;
        background: rgba(10, 15, 21, 0.72);
        color: rgba(228, 234, 240, 0.86);
        font-size: 10px;
        line-height: 1;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        backdrop-filter: blur(8px);
      }

      .steamloader-feature-status {
        position: absolute;
        top: 10px;
        right: 10px;
        backdrop-filter: blur(8px);
      }

      .steamloader-feature-body {
        display: flex;
        flex-direction: column;
        gap: 8px;
        padding: 12px 13px 13px;
      }

      .steamloader-feature-title {
        color: rgba(236, 242, 247, 0.96);
        font-size: clamp(16px, 2vw, 21px);
        line-height: 1.15;
        font-weight: 700;
        letter-spacing: -0.02em;
      }

      .steamloader-feature-copy {
        color: rgba(162, 173, 184, 0.92);
        font-size: clamp(11px, 1.28vw, 14px);
        line-height: 1.42;
        display: -webkit-box;
        -webkit-box-orient: vertical;
        -webkit-line-clamp: 2;
        overflow: hidden;
      }

      .steamloader-feature-meta {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }

      .steamloader-feature-meta-item {
        display: inline-flex;
        align-items: center;
        padding: 4px 8px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.06);
        color: rgba(204, 214, 224, 0.86);
        font-size: 10px;
        line-height: 1;
      }

      .steamloader-feature-footer {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        padding-top: 2px;
      }

      .steamloader-feature-footer-copy {
        color: rgba(196, 208, 220, 0.84);
        font-size: 11px;
        line-height: 1.2;
        font-weight: 600;
        letter-spacing: 0.02em;
      }

      .steamloader-feature-footer-chevron {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        color: rgba(196, 208, 220, 0.84);
      }

      .steamloader-feature-footer-chevron svg {
        width: 18px;
        height: 18px;
      }

      .steamloader-dialog-button-inline-stepper {
        width: min(100%, 232px) !important;
        min-height: 0 !important;
        margin: 2px auto 0 !important;
        padding: 9px 14px !important;
        border-radius: 14px !important;
        background: rgba(255, 255, 255, 0.042) !important;
      }

      .steamloader-inline-stepper {
        display: grid;
        grid-template-columns: 18px minmax(0, 1fr) 18px;
        align-items: center;
        gap: 12px;
      }

      .steamloader-inline-stepper-main {
        min-width: 0;
        text-align: center;
      }

      .steamloader-inline-stepper-title {
        color: rgba(233, 239, 245, 0.94);
        font-size: 15px;
        line-height: 1.15;
        font-weight: 700;
        letter-spacing: 0.02em;
      }

      .steamloader-inline-stepper-copy {
        margin-top: 2px;
        color: rgba(173, 184, 195, 0.86);
        font-size: 11px;
        line-height: 1.2;
        font-weight: 600;
      }

      .steamloader-inline-stepper.is-compact .steamloader-inline-stepper-title {
        font-size: 14px;
      }

      .steamloader-inline-stepper-arrow {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        color: rgba(196, 208, 220, 0.82);
      }

      .steamloader-inline-stepper-arrow svg {
        width: 18px;
        height: 18px;
      }

      .steamloader-inline-stepper-arrow.is-disabled {
        opacity: 0.28;
      }

      .steamloader-progress-row {
        width: 100%;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 7px;
        text-align: left;
      }

      .steamloader-progress-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
      }

      .steamloader-progress-title,
      .steamloader-progress-label {
        color: rgba(233, 239, 245, 0.94);
        font-size: 14px;
        line-height: 1.2;
        font-weight: 800;
      }

      .steamloader-progress-copy {
        color: rgba(173, 184, 195, 0.82);
        font-size: 11px;
        line-height: 1.3;
        font-weight: 600;
      }

      .steamloader-progress-track {
        position: relative;
        height: 8px;
        overflow: hidden;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.09);
      }

      .steamloader-progress-fill {
        display: block;
        height: 100%;
        border-radius: inherit;
        background: linear-gradient(90deg, #4aa4dc, #78c8f4);
      }

      .steamloader-row-shell-global-back {
        padding: 0;
      }

      .steamloader-dialog-button-global-back {
        min-height: 40px !important;
        padding: 7px 10px !important;
        border-radius: 13px !important;
        background: rgba(255, 255, 255, 0.045) !important;
      }

      .steamloader-dialog-button-global-back:hover,
      .steamloader-dialog-button-global-back:focus-visible,
      .steamloader-dialog-button-global-back.gpfocus {
        background: #edf3f8 !important;
        box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.22), 0 8px 24px rgba(0, 0, 0, 0.18) !important;
      }

      .steamloader-row-shell-global-back .steamloader-row-icon {
        width: 28px;
        height: 28px;
        border-radius: 9px;
        background: rgba(255, 255, 255, 0.07);
      }

      .steamloader-row-shell-global-back .steamloader-row-title {
        font-size: clamp(14px, 1.62vw, 17px);
        line-height: 1.12;
        font-weight: 700;
      }

      .steamloader-row-shell-global-back .steamloader-row-copy {
        margin-top: 1px;
        font-size: clamp(10px, 1.08vw, 12px);
      }

      .steamloader-dialog-button-global-back:hover .steamloader-row-title,
      .steamloader-dialog-button-global-back:hover .steamloader-row-copy,
      .steamloader-dialog-button-global-back:hover .steamloader-row-trailing,
      .steamloader-dialog-button-global-back:focus-visible .steamloader-row-title,
      .steamloader-dialog-button-global-back:focus-visible .steamloader-row-copy,
      .steamloader-dialog-button-global-back:focus-visible .steamloader-row-trailing,
      .steamloader-dialog-button-global-back.gpfocus .steamloader-row-title,
      .steamloader-dialog-button-global-back.gpfocus .steamloader-row-copy,
      .steamloader-dialog-button-global-back.gpfocus .steamloader-row-trailing {
        color: #121923 !important;
      }

      .steamloader-dialog-button-global-back:hover .steamloader-row-icon,
      .steamloader-dialog-button-global-back:focus-visible .steamloader-row-icon,
      .steamloader-dialog-button-global-back.gpfocus .steamloader-row-icon {
        background: rgba(18, 25, 35, 0.1) !important;
        color: #121923 !important;
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
        border-radius: 16px;
        background: rgba(255, 255, 255, 0.05);
        transition: box-shadow 0.15s ease;
      }

      .steamloader-editor-card:has(.steamloader-editor-trigger:focus),
      .steamloader-editor-card:has(.steamloader-editor-trigger.gpfocus) {
        box-shadow: 0 0 0 2px rgba(106, 169, 255, 0.72);
      }

      .steamloader-editor-trigger {
        display: block;
        width: 100%;
        padding: 12px 13px 0;
        min-height: 0 !important;
        background: transparent !important;
        border: none !important;
        border-radius: 16px 16px 0 0 !important;
        font: inherit;
        text-align: left;
        cursor: default;
        outline: none;
      }

      .steamloader-editor-trigger.gpfocus,
      .steamloader-editor-trigger:focus-visible {
        background: rgba(237, 243, 248, 0.94) !important;
      }

      .steamloader-editor-trigger.gpfocus .steamloader-editor-label,
      .steamloader-editor-trigger.gpfocus .steamloader-editor-help,
      .steamloader-editor-trigger:focus-visible .steamloader-editor-label,
      .steamloader-editor-trigger:focus-visible .steamloader-editor-help {
        color: #121923 !important;
      }

      .steamloader-editor-card .steamloader-editor-textarea {
        margin: 9px 13px 12px;
        width: calc(100% - 26px);
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

      .steamloader-editor-input-secret {
        min-height: 44px;
        resize: none;
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

      .steamloader-card-swatch {
        margin-top: 10px;
        display: inline-flex;
        align-items: center;
        gap: 9px;
        color: rgba(214, 222, 231, 0.92);
        font-size: clamp(11px, 1.25vw, 14px);
      }

      .steamloader-card-swatch-dot {
        width: 12px;
        height: 12px;
        border-radius: 999px;
        box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.16), 0 0 12px rgba(255, 255, 255, 0.1);
        flex: 0 0 auto;
      }

      .steamloader-card-swatch-label {
        color: rgba(205, 214, 224, 0.94);
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
        gap: 8px;
        min-height: 24px;
        text-align: center;
      }

      .steamloader-volume-action-icon {
        width: 18px;
        height: 18px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        color: rgba(214, 222, 231, 0.9);
        flex-shrink: 0;
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
        outline: none !important;
        transition:
          background 0.12s ease,
          box-shadow 0.12s ease,
          transform 0.12s ease;
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

      .steamloader-audio-quick-button.gpfocus,
      .steamloader-audio-quick-button:focus-visible {
        transform: translateY(-1px);
        box-shadow:
          0 0 0 3px rgba(86, 188, 255, 0.98),
          0 0 0 7px rgba(86, 188, 255, 0.22),
          inset 0 0 0 3px rgba(10, 17, 26, 0.72) !important;
      }

      .steamloader-audio-quick-button.gpfocus:not(.is-active),
      .steamloader-audio-quick-button:focus-visible:not(.is-active) {
        background: linear-gradient(180deg, rgba(242, 247, 252, 0.94) 0%, rgba(198, 212, 225, 0.9) 100%) !important;
      }

      .steamloader-audio-quick-button.gpfocus:not(.is-active) .steamloader-audio-quick-title,
      .steamloader-audio-quick-button.gpfocus:not(.is-active) .steamloader-audio-quick-icon,
      .steamloader-audio-quick-button:focus-visible:not(.is-active) .steamloader-audio-quick-title,
      .steamloader-audio-quick-button:focus-visible:not(.is-active) .steamloader-audio-quick-icon {
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

      .steamloader-dialog-button.gpfocus .steamloader-accordion-toggle-title,
      .steamloader-dialog-button.gpfocus .steamloader-accordion-toggle-copy,
      .steamloader-dialog-button.gpfocus .steamloader-accordion-toggle-arrow {
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

      .steamloader-dialog-button-inline-stepper.gpfocus,
      .steamloader-dialog-button-inline-stepper:focus-visible {
        box-shadow: 0 0 0 2px rgba(131, 188, 255, 0.68) !important;
      }

      .steamloader-dialog-button-inline-stepper.gpfocus .steamloader-inline-stepper-title,
      .steamloader-dialog-button-inline-stepper:focus-visible .steamloader-inline-stepper-title,
      .steamloader-dialog-button-inline-stepper.gpfocus .steamloader-inline-stepper-copy,
      .steamloader-dialog-button-inline-stepper:focus-visible .steamloader-inline-stepper-copy,
      .steamloader-dialog-button-inline-stepper.gpfocus .steamloader-inline-stepper-arrow,
      .steamloader-dialog-button-inline-stepper:focus-visible .steamloader-inline-stepper-arrow {
        color: rgba(243, 248, 252, 0.98) !important;
      }

      .steamloader-dialog-button-feature:hover .steamloader-feature-title,
      .steamloader-dialog-button-feature:hover .steamloader-feature-copy,
      .steamloader-dialog-button-feature:hover .steamloader-feature-footer-copy,
      .steamloader-dialog-button-feature:hover .steamloader-feature-footer-chevron,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-title,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-copy,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-footer-copy,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-footer-chevron,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-title,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-copy,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-footer-copy,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-footer-chevron {
        color: rgba(236, 242, 247, 0.96) !important;
      }

      .steamloader-dialog-button-feature:hover .steamloader-feature-meta-item,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-meta-item,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-meta-item {
        background: rgba(255, 255, 255, 0.06) !important;
        color: rgba(204, 214, 224, 0.86) !important;
      }

      .steamloader-dialog-button-feature:hover .steamloader-feature-eyebrow,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-eyebrow,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-eyebrow {
        background: rgba(10, 15, 21, 0.72) !important;
        color: rgba(228, 234, 240, 0.86) !important;
      }

      .steamloader-dialog-button-feature:hover .steamloader-feature-status,
      .steamloader-dialog-button-feature:focus-visible .steamloader-feature-status,
      .steamloader-dialog-button-feature.gpfocus .steamloader-feature-status {
        background: rgba(10, 15, 21, 0.72) !important;
        color: rgba(214, 222, 231, 0.9) !important;
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

      .steamloader-dialog-button.gpfocus .steamloader-volume-action-title,
      .steamloader-dialog-button.gpfocus .steamloader-volume-action-icon {
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

      .steamloader-panel-themes-store {
        padding: 14px 10px 24px;
        background:
          radial-gradient(circle at top left, rgba(62, 127, 212, 0.14) 0%, rgba(15, 21, 29, 0) 30%),
          linear-gradient(180deg, rgba(12, 17, 24, 0.98) 0%, rgba(15, 21, 29, 1) 100%);
      }

      .steamloader-panel-themes-store .steamloader-header {
        margin-bottom: 12px;
      }

      .steamloader-panel-themes-store .steamloader-stack {
        gap: 12px;
      }

      .steamloader-panel-themes-store .steamloader-card,
      .steamloader-panel-themes-store .steamloader-editor-card {
        border: 1px solid rgba(120, 153, 191, 0.12);
        background:
          linear-gradient(180deg, rgba(28, 37, 48, 0.9) 0%, rgba(18, 25, 34, 0.94) 100%);
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

  function getExpandedSectionRouteKey(sectionKey, route = state.route) {
    const normalizedSectionKey = typeof sectionKey === "string" ? sectionKey.trim() : "";
    if (!normalizedSectionKey) {
      return "";
    }

    return `${getRouteKey(route)}::${normalizedSectionKey}`;
  }

  function isExpandedSection(sectionKey, defaultExpanded = false, route = state.route) {
    const routeSectionKey = getExpandedSectionRouteKey(sectionKey, route);
    if (!routeSectionKey) {
      return Boolean(defaultExpanded);
    }

    if (!Object.prototype.hasOwnProperty.call(state.expandedSectionsByRoute, routeSectionKey)) {
      state.expandedSectionsByRoute[routeSectionKey] = Boolean(defaultExpanded);
    }

    return Boolean(state.expandedSectionsByRoute[routeSectionKey]);
  }

  function setExpandedSection(sectionKey, expanded, route = state.route) {
    const routeSectionKey = getExpandedSectionRouteKey(sectionKey, route);
    if (!routeSectionKey) {
      return Boolean(expanded);
    }

    state.expandedSectionsByRoute[routeSectionKey] = Boolean(expanded);
    return state.expandedSectionsByRoute[routeSectionKey];
  }

  function toggleExpandedSection(sectionKey, defaultExpanded = false, route = state.route) {
    return setExpandedSection(sectionKey, !isExpandedSection(sectionKey, defaultExpanded, route), route);
  }

  function normalizeFocusSlotKey(value) {
    if (typeof value !== "string") {
      return null;
    }

    const normalized = value.trim();
    return normalized ? normalized : null;
  }

  function resolveSlotFocusKey(slot, index = null) {
    const explicitKey = normalizeFocusSlotKey(slot?.slotKey || slot?.key);
    if (explicitKey) {
      return explicitKey;
    }

    const settingScope = normalizeFocusSlotKey(slot?.settingScope);
    const settingKey = normalizeFocusSlotKey(slot?.settingKey);
    if (settingScope && settingKey) {
      return `setting:${settingScope}:${settingKey}`;
    }

    const value = slot?.value;
    if (typeof value === "string" && value.trim()) {
      return `value:${value.trim()}`;
    }

    if (typeof value === "number" && Number.isFinite(value)) {
      return `value:${value}`;
    }

    const title = normalizeFocusSlotKey(slot?.title);
    const copy = typeof slot?.copy === "string" ? normalizeFocusSlotKey(slot.copy) : null;
    if (title && copy) {
      return `label:${title}::${copy}`;
    }

    if (title) {
      return `label:${title}`;
    }

    return Number.isInteger(index) ? `index:${index}` : null;
  }

  function rememberCurrentRouteSelection(index, slotOrKey = null) {
    const routeKey = getRouteKey(state.route);
    if (Number.isInteger(index)) {
      state.lastSelectedIndexByRoute[routeKey] = index;
    }

    const resolvedFocusKey =
      typeof slotOrKey === "string"
        ? normalizeFocusSlotKey(slotOrKey)
        : resolveSlotFocusKey(slotOrKey, index);

    if (resolvedFocusKey) {
      state.lastSelectedSlotKeyByRoute[routeKey] = resolvedFocusKey;
    } else {
      delete state.lastSelectedSlotKeyByRoute[routeKey];
    }
  }

  function rememberCurrentRouteIndex(index) {
    const slot = Array.isArray(state.renderedSlots) && Number.isInteger(index)
      ? state.renderedSlots[index] || null
      : null;
    rememberCurrentRouteSelection(index, slot);
  }

  function rememberCurrentRouteSlot(index, slot = null) {
    rememberCurrentRouteSelection(index, slot);
  }

  function requestFocusForRoute(route, fallbackIndex = null, fallbackSlotKey = null) {
    const routeKey = getRouteKey(route);
    const rememberedIndex = state.lastSelectedIndexByRoute[routeKey];
    const rememberedSlotKey = normalizeFocusSlotKey(state.lastSelectedSlotKeyByRoute[routeKey]);
    const explicitSlotKey = normalizeFocusSlotKey(fallbackSlotKey);

    state.pendingFocusRouteKey = routeKey;
    state.pendingFocusSlotKey = explicitSlotKey || rememberedSlotKey;
    state.pendingFocusIndex = Number.isInteger(rememberedIndex)
      ? Number.isInteger(fallbackIndex)
        ? fallbackIndex
        : rememberedIndex
      : Number.isInteger(fallbackIndex)
        ? fallbackIndex
        : null;
  }

  function requestRouteEntryFocus(route) {
    const routeKey = getRouteKey(route);
    if (state.pendingFocusRouteKey === routeKey) {
      return;
    }

    requestFocusForRoute(route, null);
  }

  function requestFreshEntryForRoute(route, focusIndex = 0, scrollTop = 0, focusSlotKey = null) {
    const routeKey = getRouteKey(route);
    state.pendingFocusRouteKey = routeKey;
    state.pendingFocusSlotKey = normalizeFocusSlotKey(focusSlotKey);
    state.pendingFocusIndex = Number.isInteger(focusIndex) ? focusIndex : 0;
    state.pendingScrollRouteKey = routeKey;
    state.pendingScrollTop = Number.isFinite(scrollTop) ? Math.max(0, scrollTop) : 0;
  }

  function resolveAutoFocusTarget(route, slots = [], fallbackIndex = null) {
    const routeKey = getRouteKey(route);
    const hasPendingFocus = state.pendingFocusRouteKey === routeKey;
    const pendingSlotKey = hasPendingFocus ? normalizeFocusSlotKey(state.pendingFocusSlotKey) : null;
    if (pendingSlotKey && Array.isArray(slots)) {
      const matchedIndex = slots.findIndex((slot, index) => resolveSlotFocusKey(slot, index) === pendingSlotKey);
      if (matchedIndex >= 0) {
        return matchedIndex;
      }
    }

    if (hasPendingFocus && Number.isInteger(state.pendingFocusIndex)) {
      return state.pendingFocusIndex;
    }

    if (hasPendingFocus && Number.isInteger(fallbackIndex)) {
      return fallbackIndex;
    }

    if (route.screen === "root" && state.pendingEntryAutoFocus) {
      return 0;
    }

    return null;
  }

  function getPanelScrollContainer() {
    return document.querySelector("#quickaccess_content_7 .steamloader-panel");
  }

  function getPanelRouteKey(panel = getPanelScrollContainer()) {
    return normalizeFocusSlotKey(panel?.getAttribute?.("data-route-key"));
  }

  function hasPanelLayout(panel = getPanelScrollContainer()) {
    return panel instanceof HTMLElement && panel.clientHeight > 0 && panel.scrollHeight > 0;
  }

  function rememberRouteScroll(route = state.route, scrollTop = null, options = {}) {
    const routeKey = getRouteKey(route);
    const panel = getPanelScrollContainer();
    const resolvedTop = Number.isFinite(scrollTop)
      ? scrollTop
      : panel?.scrollTop;

    if (!Number.isFinite(resolvedTop)) {
      return;
    }

    const nextScrollTop = Math.max(0, resolvedTop);
    const rememberedTop = state.lastScrollTopByRoute[routeKey];
    if (
      !hasPanelLayout(panel) &&
      nextScrollTop <= 1 &&
      Number.isFinite(rememberedTop) &&
      rememberedTop > 1
    ) {
      return;
    }

    state.lastScrollTopByRoute[routeKey] = nextScrollTop;
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

    const maxAttempts = 2;

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
        if (!(panel instanceof HTMLElement) || !hasPanelLayout(panel)) {
          if (attempt < maxAttempts) {
            applyRestore(attempt + 1);
          } else {
            clearPendingScrollRestore();
          }
          return;
        }

        ensurePanelInteractionTracker();
        const maxScrollTop = Math.max(0, panel.scrollHeight - panel.clientHeight);
        const nextScrollTop = Math.max(0, Math.min(targetTop, maxScrollTop));
        if (Math.abs(panel.scrollTop - nextScrollTop) > 1) {
          panel.scrollTop = nextScrollTop;
        }

        if (attempt < maxAttempts) {
          applyRestore(attempt + 1);
          return;
        }

        rememberRouteScroll(state.route, panel.scrollTop, { force: true });
        clearPendingScrollRestore();
      });
    };

    applyRestore(0);
  }

  function rememberSlotElementFocus(element) {
    const focusedNode = element?.closest?.(".steamloader-panel [data-slot-button]") || null;
    if (!(focusedNode instanceof HTMLElement)) {
      return false;
    }

    const panel = focusedNode.closest(".steamloader-panel");
    const panelRouteKey = getPanelRouteKey(panel);
    const routeKey = getRouteKey(state.route);
    if (panelRouteKey && panelRouteKey !== routeKey) {
      return false;
    }

    const rawValue = focusedNode.getAttribute("data-slot-button");
    const parsedValue = Number.parseInt(rawValue || "", 10);
    const index = Number.isInteger(parsedValue) ? parsedValue : null;
    const slotKey =
      normalizeFocusSlotKey(focusedNode.getAttribute("data-slot-key")) ||
      (Number.isInteger(index) && Array.isArray(state.renderedSlots)
        ? resolveSlotFocusKey(state.renderedSlots[index], index)
        : null);

    if (Number.isInteger(index)) {
      rememberCurrentRouteSelection(index, slotKey || state.renderedSlots?.[index] || null);
      return true;
    }

    if (slotKey) {
      state.lastSelectedSlotKeyByRoute[routeKey] = slotKey;
      return true;
    }

    return false;
  }

  function rememberFocusedSlotFromDom() {
    const focusedNode =
      document.querySelector(".steamloader-panel [data-slot-button].gpfocus") ||
      document.activeElement?.closest?.(".steamloader-panel [data-slot-button]") ||
      null;
    return rememberSlotElementFocus(focusedNode);
  }

  function detachPanelInteractionTracker() {
    if (state.trackedPanel && state.trackedPanelScrollHandler) {
      state.trackedPanel.removeEventListener("scroll", state.trackedPanelScrollHandler);
    }

    if (state.trackedPanel && state.trackedPanelFocusHandler) {
      state.trackedPanel.removeEventListener("focusin", state.trackedPanelFocusHandler, true);
    }

    if (state.trackedPanel && state.trackedPanelFocusOutHandler) {
      state.trackedPanel.removeEventListener("focusout", state.trackedPanelFocusOutHandler, true);
    }

    state.trackedPanel = null;
    state.trackedPanelScrollHandler = null;
    state.trackedPanelFocusHandler = null;
    state.trackedPanelFocusOutHandler = null;
  }

  function ensurePanelInteractionTracker() {
    const panel = getPanelScrollContainer();
    if (!(panel instanceof HTMLElement)) {
      detachPanelInteractionTracker();
      return;
    }

    if (state.trackedPanel === panel) {
      return;
    }

    detachPanelInteractionTracker();
    state.trackedPanel = panel;

    const scrollHandler = () => {
      const panelRouteKey = getPanelRouteKey(panel);
      if (panelRouteKey && panelRouteKey !== getRouteKey(state.route)) {
        return;
      }

      rememberRouteScroll(state.route, panel.scrollTop);
    };
    const focusHandler = (event) => {
      rememberSlotElementFocus(event.target);
    };
    const focusOutHandler = () => {
      window.requestAnimationFrame(() => {
        if (!state.panelVisible || !hasPanelLayout(panel)) {
          return;
        }

        const panelRouteKey = getPanelRouteKey(panel);
        if (panelRouteKey && panelRouteKey !== getRouteKey(state.route)) {
          return;
        }

        if (document.activeElement === document.body) {
          queuePendingFocusRestore(state.route);
        }
      });
    };

    state.trackedPanelScrollHandler = scrollHandler;
    state.trackedPanelFocusHandler = focusHandler;
    state.trackedPanelFocusOutHandler = focusOutHandler;
    panel.addEventListener("scroll", scrollHandler, { passive: true });
    panel.addEventListener("focusin", focusHandler, true);
    panel.addEventListener("focusout", focusOutHandler, true);

    rememberFocusedSlotFromDom();
  }

  function isEditableFocusTarget(element) {
    if (!(element instanceof HTMLElement)) {
      return false;
    }

    const tagName = element.tagName.toLowerCase();
    return (
      tagName === "input" ||
      tagName === "textarea" ||
      tagName === "select" ||
      element.isContentEditable
    );
  }

  function getEditorDataKey(element) {
    return normalizeFocusSlotKey(element?.getAttribute?.("data-editor-key"));
  }

  function ensureEditorSelectionStore() {
    if (!state.editorSelectionByKey || typeof state.editorSelectionByKey !== "object") {
      state.editorSelectionByKey = {};
    }

    return state.editorSelectionByKey;
  }

  function rememberEditorSelection(element) {
    if (!(element instanceof HTMLElement) || !isEditableFocusTarget(element)) {
      return null;
    }

    const editorKey = getEditorDataKey(element);
    if (!editorKey || typeof element.selectionStart !== "number" || typeof element.selectionEnd !== "number") {
      return null;
    }

    const value = typeof element.value === "string" ? element.value : "";
    const selection = {
      start: Math.max(0, Math.min(value.length, element.selectionStart)),
      end: Math.max(0, Math.min(value.length, element.selectionEnd)),
      direction: typeof element.selectionDirection === "string" ? element.selectionDirection : "none",
      value,
    };

    ensureEditorSelectionStore()[editorKey] = selection;
    return selection;
  }

  function restoreEditorSelection(element, options = {}) {
    if (
      !(element instanceof HTMLElement) ||
      typeof element.setSelectionRange !== "function" ||
      typeof element.value !== "string"
    ) {
      return false;
    }

    const editorKey = getEditorDataKey(element);
    const saved = editorKey ? ensureEditorSelectionStore()[editorKey] : null;
    const valueLength = element.value.length;
    const fallback = options.preferEnd ? valueLength : null;
    const startValue = Number.isFinite(saved?.start) ? saved.start : fallback;
    const endValue = Number.isFinite(saved?.end) ? saved.end : startValue;
    if (!Number.isFinite(startValue) || !Number.isFinite(endValue)) {
      return false;
    }

    const start = Math.max(0, Math.min(valueLength, startValue));
    const end = Math.max(0, Math.min(valueLength, endValue));
    const direction = typeof saved?.direction === "string" ? saved.direction : "none";

    try {
      element.setSelectionRange(start, end, direction);
      rememberEditorSelection(element);
      return true;
    } catch {
      return false;
    }
  }

  function markEditorFocused(editorKey, element = null) {
    if (!editorKey) {
      return;
    }

    state.editorFocusActive = true;
    state.editorFocusCardKey = editorKey;
    state.editorFocusRouteKey = getRouteKey(state.route);
  }

  function clearEditorFocus(editorKey = null) {
    if (editorKey && state.editorFocusCardKey && state.editorFocusCardKey !== editorKey) {
      return;
    }

    state.editorFocusActive = false;
    state.editorFocusCardKey = null;
    state.editorFocusRouteKey = null;
  }

  function isEditorFocusForRoute(route = state.route) {
    return Boolean(
      state.editorFocusActive &&
        state.editorFocusCardKey &&
        (!state.editorFocusRouteKey || state.editorFocusRouteKey === getRouteKey(route)),
    );
  }

  function getElementFromNode(node) {
    if (node instanceof Element) {
      return node;
    }

    return node?.parentElement instanceof Element ? node.parentElement : null;
  }

  function isTextInputContextElement(node) {
    const element = getElementFromNode(node);
    if (!element) {
      return false;
    }

    if (element instanceof HTMLElement && isEditableFocusTarget(element)) {
      return true;
    }

    return Boolean(
      element.closest?.(
        "[data-editor-key], input, textarea, select, [contenteditable='true'], [role='textbox']",
      ),
    );
  }

  function shouldSuppressGlobalHotkeysForTextInput(event = null) {
    return Boolean(
      Date.now() < (Number(state.steamKeyboardActiveUntil) || 0) ||
        state.editorFocusActive ||
        state.editorFocusCardKey ||
        isTextInputContextElement(event?.target) ||
        isTextInputContextElement(document.activeElement),
    );
  }

  function isCurrentRouteSlotElement(element, route = state.route) {
    const slotElement = element?.closest?.(".steamloader-panel [data-slot-button]") || null;
    if (!(slotElement instanceof HTMLElement)) {
      return false;
    }

    const panel = slotElement.closest(".steamloader-panel");
    const panelRouteKey = getPanelRouteKey(panel);
    const routeKey = getRouteKey(route);
    return !panelRouteKey || panelRouteKey === routeKey;
  }

  function hasRouteTextInputFocus(route = state.route) {
    if (isEditorFocusForRoute(route)) {
      return true;
    }

    return (
      isCurrentRouteSlotElement(document.activeElement, route) &&
      isTextInputContextElement(document.activeElement)
    );
  }

  function findSlotElementByKey(panel, slotKey) {
    const normalizedKey = normalizeFocusSlotKey(slotKey);
    if (!normalizedKey || !(panel instanceof HTMLElement)) {
      return null;
    }

    for (const element of panel.querySelectorAll("[data-slot-button]")) {
      if (element.getAttribute("data-slot-key") === normalizedKey) {
        return element;
      }
    }

    return null;
  }

  function findSlotElementByIndex(panel, index) {
    if (!(panel instanceof HTMLElement) || !Number.isInteger(index)) {
      return null;
    }

    return panel.querySelector(`[data-slot-button="${index}"]`);
  }

  function isFocusableSlotElement(element) {
    return Boolean(
      element instanceof HTMLElement &&
        !element.hasAttribute("disabled") &&
        element.getAttribute("aria-disabled") !== "true",
    );
  }

  function findFirstFocusableSlotElement(panel) {
    if (!(panel instanceof HTMLElement)) {
      return null;
    }

    for (const element of panel.querySelectorAll("[data-slot-button]")) {
      if (isFocusableSlotElement(element)) {
        return element;
      }
    }

    return null;
  }

  function getFocusRestoreTarget(route = state.route) {
    const panel = getPanelScrollContainer();
    if (!(panel instanceof HTMLElement) || !hasPanelLayout(panel)) {
      return null;
    }

    const routeKey = getRouteKey(route);
    const panelRouteKey = getPanelRouteKey(panel);
    if (panelRouteKey && panelRouteKey !== routeKey) {
      return null;
    }

    const hasPendingFocus = state.pendingFocusRouteKey === routeKey;
    const pendingSlotKey = hasPendingFocus ? normalizeFocusSlotKey(state.pendingFocusSlotKey) : null;
    const rememberedSlotKey = normalizeFocusSlotKey(state.lastSelectedSlotKeyByRoute[routeKey]);
    const pendingIndex = hasPendingFocus && Number.isInteger(state.pendingFocusIndex)
      ? state.pendingFocusIndex
      : null;
    const rememberedIndex = state.lastSelectedIndexByRoute[routeKey];
    const index = Number.isInteger(pendingIndex)
      ? pendingIndex
      : Number.isInteger(rememberedIndex)
        ? rememberedIndex
        : route.screen === "root"
          ? 0
          : null;

    const byKey =
      findSlotElementByKey(panel, pendingSlotKey) ||
      findSlotElementByKey(panel, rememberedSlotKey);
    const byIndex = findSlotElementByIndex(panel, index);
    const target = [byKey, byIndex, findFirstFocusableSlotElement(panel)]
      .find((element) => isFocusableSlotElement(element));

    return target || null;
  }

  function restoreRouteFocus(route = state.route) {
    const panel = getPanelScrollContainer();
    if (!(panel instanceof HTMLElement) || !hasPanelLayout(panel)) {
      return false;
    }

    if (!state.panelVisible && !isVisible(getPanelHost())) {
      return false;
    }

    const activeElement = document.activeElement;
    if (panel.contains(activeElement) && isEditableFocusTarget(activeElement)) {
      const editorKey = getEditorDataKey(activeElement);
      if (editorKey) {
        markEditorFocused(editorKey);
      }
      rememberEditorSelection(activeElement);
      return true;
    }

    if (state.editorFocusActive && state.editorFocusCardKey && !isEditorFocusForRoute(route)) {
      clearEditorFocus();
    }

    // If the user was typing in an editor before a re-render, restore to it.
    if (state.editorFocusActive && state.editorFocusCardKey) {
      let textarea = null;
      for (const el of panel.querySelectorAll("[data-editor-key]")) {
        if (el.getAttribute("data-editor-key") === state.editorFocusCardKey) {
          textarea = el;
          break;
        }
      }
      if (textarea instanceof HTMLElement) {
        textarea.focus({ preventScroll: true });
        restoreEditorSelection(textarea, { preferEnd: true });
        return document.activeElement === textarea;
      }
      // Textarea not yet in DOM — report not-done so the caller retries
      return false;
    }

    if (isCurrentRouteSlotElement(activeElement, route)) {
      rememberFocusedSlotFromDom();
      return true;
    }

    // On Steam Deck, gamepad navigation uses a .gpfocus class instead of browser
    // focus — document.activeElement stays as document.body. If a slot element
    // already has gamepad focus, calling target.focus() would trigger Steam's
    // gamepad-focus re-evaluation, causing the "A SELECT" label and the selected
    // element's highlight to flicker visually. Skip the focus restore in that case.
    const gpFocusedSlot = panel.querySelector("[data-slot-button].gpfocus");
    if (gpFocusedSlot instanceof HTMLElement) {
      rememberSlotElementFocus(gpFocusedSlot);
      return true;
    }

    const target = getFocusRestoreTarget(route);
    if (!target) {
      return false;
    }

    const scrollTop = panel.scrollTop;
    try {
      target.focus({ preventScroll: true });
    } catch {
      try {
        target.focus();
      } catch {
        return false;
      }
    }

    if (Math.abs(panel.scrollTop - scrollTop) > 1) {
      panel.scrollTop = scrollTop;
    }

    rememberSlotElementFocus(target);
    return document.activeElement === target || target.contains(document.activeElement);
  }

  function clearPendingFocusRestore() {
    if (state.pendingFocusRestoreAnimationFrame) {
      window.cancelAnimationFrame(state.pendingFocusRestoreAnimationFrame);
      state.pendingFocusRestoreAnimationFrame = 0;
    }

    state.pendingFocusRestoreRouteKey = null;
  }

  function queuePendingFocusRestore(route = state.route) {
    const routeKey = getRouteKey(route);
    state.pendingFocusRestoreRouteKey = routeKey;

    if (state.pendingFocusRestoreAnimationFrame) {
      window.cancelAnimationFrame(state.pendingFocusRestoreAnimationFrame);
      state.pendingFocusRestoreAnimationFrame = 0;
    }

    const applyRestore = (attempt = 0) => {
      state.pendingFocusRestoreAnimationFrame = window.requestAnimationFrame(() => {
        if (state.pendingFocusRestoreRouteKey !== routeKey) {
          return;
        }

        if (getRouteKey(state.route) !== routeKey) {
          clearPendingFocusRestore();
          return;
        }

        if (restoreRouteFocus(state.route) || attempt >= 2) {
          clearPendingFocusRestore();
          return;
        }

        applyRestore(attempt + 1);
      });
    };

    applyRestore();
  }

  function repairPanelFocusIfNeeded() {
    if (!state.panelVisible || document.activeElement !== document.body) {
      return;
    }

    const panel = getPanelScrollContainer();
    if (!(panel instanceof HTMLElement) || !hasPanelLayout(panel)) {
      return;
    }

    const panelRouteKey = getPanelRouteKey(panel);
    if (panelRouteKey && panelRouteKey !== getRouteKey(state.route)) {
      return;
    }

    queuePendingFocusRestore(state.route);
  }

  function ensureFocusRepairTimer() {
    if (window.__steamLoaderFocusRepairTimer) {
      return;
    }

    window.__steamLoaderFocusRepairTimer = window.setInterval(repairPanelFocusIfNeeded, 500);
  }

  function ensureFocusRepairHandler() {
    if (window.__steamLoaderFocusRepairHandler) {
      return;
    }

    window.__steamLoaderFocusRepairHandler = () => {
      window.requestAnimationFrame(repairPanelFocusIfNeeded);
    };
    document.addEventListener("focusout", window.__steamLoaderFocusRepairHandler, true);
  }

  function preparePanelReplacement() {
    const panel = getPanelScrollContainer();
    const panelRouteKey = getPanelRouteKey(panel);
    const routeKey = getRouteKey(state.route);

    if (panel instanceof HTMLElement && hasPanelLayout(panel) && (!panelRouteKey || panelRouteKey === routeKey)) {
      rememberRouteScroll(state.route, panel.scrollTop, { force: true });
    }

    // Save editor focus state before the panel DOM is replaced
    const activeElement = document.activeElement;
    if (
      activeElement instanceof HTMLElement &&
      isEditableFocusTarget(activeElement) &&
      panel instanceof HTMLElement &&
      panel.contains(activeElement)
    ) {
      const editorKey = activeElement.getAttribute("data-editor-key");
      if (editorKey) {
        markEditorFocused(editorKey, activeElement);
        rememberEditorSelection(activeElement);
      }
    }

    rememberFocusedSlotFromDom();
    requestScrollRestoreForRoute(state.route);
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

  function isThemesStoreThemeRoute(route = state.route) {
    return Boolean(
      route &&
        route.screen === "page" &&
        route.pluginId === "themes" &&
        typeof route.pageId === "string" &&
        route.pageId.startsWith("store-theme-"),
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

  function getThemeStoreIdFromRoute(route = state.route) {
    if (!route || route.pluginId !== "themes" || typeof route.pageId !== "string") {
      return null;
    }

    if (!route.pageId.startsWith("store-theme-")) {
      return null;
    }

    return route.pageId.replace(/^store-theme-/, "") || null;
  }

  function getInstalledThemeGroups() {
    const themes = Array.isArray(state.themes.snapshot?.installedThemes)
      ? state.themes.snapshot.installedThemes
      : [];

    return {
      activeThemes: themes.filter((theme) => Boolean(theme?.enabled)),
      readyThemes: themes.filter((theme) => !theme?.enabled),
    };
  }

  function getThemesInstalledIndex(themeId) {
    const { activeThemes, readyThemes } = getInstalledThemeGroups();
    const activeIndex = activeThemes.findIndex((theme) => theme.id === themeId);
    if (activeIndex >= 0) {
      return activeIndex + 1;
    }

    const readyIndex = readyThemes.findIndex((theme) => theme.id === themeId);
    if (readyIndex >= 0) {
      return activeThemes.length + readyIndex + 3;
    }

    return null;
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

  function getThemeStoreResultSlotKey(storeId) {
    return storeId ? `theme-store-result-${storeId}` : "";
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

      if (route.pluginId === "smart-home" && route.pageId?.startsWith("room-")) {
        const roomId = route.pageId.replace(/^room-/, "");
        return {
          route: parseRoute("page:smart-home:rooms"),
          fallbackIndex: getSmartHomeRoomIndex(roomId),
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
        const fallbackIndex = getThemesInstalledIndex(themeId);

        return {
          route: parseRoute("page:themes:installed"),
          fallbackIndex,
        };
      }

      if (route.pluginId === "themes" && isThemesStoreThemeRoute(route)) {
        const storeThemeId = getThemeStoreIdFromRoute(route);

        return {
          route: parseRoute("page:themes:store"),
          fallbackSlotKey: getThemeStoreResultSlotKey(storeThemeId),
        };
      }

      if (route.pluginId === "themes" && isThemesProfileRoute(route)) {
        const profileId = getThemeProfileIdFromRoute(route);

        return {
          route: parseRoute("page:themes:profiles"),
          fallbackIndex: getThemesInstalledProfileIndex(profileId),
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

    requestFocusForRoute(
      backNavigation.route,
      backNavigation.fallbackIndex,
      backNavigation.fallbackSlotKey,
    );
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
      rememberCurrentRouteSelection(autoFocusIndex, state.renderedSlots?.[autoFocusIndex] || null);
      state.pendingFocusRouteKey = null;
      state.pendingFocusIndex = null;
      state.pendingFocusSlotKey = null;
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
      ensurePanelInteractionTracker();

      if (state.route.screen === "root") {
        state.pendingEntryAutoFocus = true;
        state.renderRevision += 1;
        refreshQuickAccessPanel();
        queuePendingFocusRestore(state.route);
        refreshCurrentLiveRouteState();
        return;
      }

      if (getBackNavigation(state.route)) {
        requestFreshEntryForRoute(state.route, 0, 0, globalBackSlotKey);
        queuePendingScrollRestore();
      }

      queuePendingFocusRestore(state.route);
      refreshCurrentLiveRouteState();
      return;
    }

    if (!visible) {
      if (state.homeReorder.active) {
        clearHomeReorderState({ restoreOriginalOrder: true });
      }
      state.panelVisible = false;
      clearPendingFocusRestore();
      clearEditorFocus();
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
      ensurePanelInteractionTracker();
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
    if (
      !window.STFrontendLib?.refreshComponentRegistry ||
      state.nativeUi.registryLoading ||
      usesCustomShellRoute()
    ) {
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
      if (!state.installed || !state.panelVisible) {
        state.renderRevision += 1;
        refreshQuickAccessPanel();
      }
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

  function HeaderStoreIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 24 24",
          fill: "none",
        },
        createElement("path", {
          d: "M4.6 7.25H19.4L18.45 18.2C18.37 19.05 17.66 19.7 16.81 19.7H7.19C6.34 19.7 5.63 19.05 5.55 18.2L4.6 7.25Z",
          stroke: "currentColor",
          strokeWidth: "1.85",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M8.15 9.35V6.85C8.15 4.97 9.67 3.45 11.55 3.45H12.45C14.33 3.45 15.85 4.97 15.85 6.85V9.35",
          stroke: "currentColor",
          strokeWidth: "1.85",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M9.15 12.2H14.85",
          stroke: "currentColor",
          strokeWidth: "1.85",
          strokeLinecap: "round",
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

  function SmartHomePluginIcon() {
    return createElement(
      "svg",
      withChildren(
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 36 36",
          fill: "none",
        },
        createElement("path", {
          d: "M7.5 17.25L18 8.5L28.5 17.25V28.5H20.75V22.75H15.25V28.5H7.5V17.25Z",
          stroke: "currentColor",
          strokeWidth: "2.2",
          strokeLinejoin: "round",
        }),
        createElement("circle", {
          cx: "24.5",
          cy: "12",
          r: "3",
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
      case "handheld-performance":
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
      case "unifystore":
        return StoreSyncPluginIcon;
      case "auto-sisr":
        return AutoSisirPluginIcon;
      case "artwork":
        return ArtworkPluginIcon;
      case "themes":
        return ThemesPluginIcon;
      case "smart-home":
        return SmartHomePluginIcon;
      case "settings":
        return SettingsPluginIcon;
      default:
        if (getCommunityPluginDefinition(pluginId)) {
          return pluginId === "home-assistant" ? SmartHomePluginIcon : HeaderStoreIcon;
        }

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

  function RefreshActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M27 12.75V7.5M27 7.5H21.75M27 7.5L22.25 12.25",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M28 17.5C28 11.7 23.3 7 17.5 7C13.7 7 10.35 9.05 8.55 12.1",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M9 23.25V28.5M9 28.5H14.25M9 28.5L13.75 23.75",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M8 18.5C8 24.3 12.7 29 18.5 29C22.3 29 25.65 26.95 27.45 23.9",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function SaveActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M10 8.5H23.5L28 13V27.5H8V10.5C8 9.4 8.9 8.5 10 8.5Z",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M12.5 8.5V16H22V8.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinejoin: "round",
        }),
        createElement("rect", {
          x: "12",
          y: "21",
          width: "12",
          height: "6",
          rx: "1.8",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
      ),
    );
  }

  function ResetActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M12.25 10.5H7V15.75",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M7 15.75C8.85 10.6 13.75 7 19.5 7C26.85 7 32 12.15 32 19.5C32 26.85 26.85 32 19.5 32C13.95 32 9.25 28.6 7.25 23.75",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function LaunchActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M14 10.5L25.5 18L14 25.5V10.5Z",
          fill: "currentColor",
        }),
      ),
    );
  }

  function StopActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("rect", {
          x: "11",
          y: "11",
          width: "14",
          height: "14",
          rx: "2.5",
          fill: "currentColor",
        }),
      ),
    );
  }

  function RestartActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M22.5 8H28V13.5",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M28 13.5C25.9 9.55 21.75 7 17 7C10.1 7 4.5 12.6 4.5 19.5C4.5 26.4 10.1 32 17 32C22.2 32 26.65 28.8 28.5 24.25",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function SleepActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M23.5 8C20.45 9.1 18.25 12.05 18.25 15.5C18.25 19.9 21.85 23.5 26.25 23.5C27.3 23.5 28.3 23.3 29.25 22.95C27.65 27.55 23.25 30.85 18.1 30.85C11.6 30.85 6.35 25.6 6.35 19.1C6.35 13 10.95 7.95 16.85 7.35",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function ShutdownActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M18 6.5V16",
          stroke: "currentColor",
          strokeWidth: "3",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M11.2 10.25C8.4 12.3 6.6 15.6 6.6 19.35C6.6 25.55 11.65 30.6 17.85 30.6C24.05 30.6 29.1 25.55 29.1 19.35C29.1 15.6 27.3 12.3 24.5 10.25",
          stroke: "currentColor",
          strokeWidth: "2.8",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function DeleteActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M10 11.5H26",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M14 11.5V9.5C14 8.4 14.9 7.5 16 7.5H20C21.1 7.5 22 8.4 22 9.5V11.5",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M12 11.5L13 27C13.1 28.45 14.3 29.5 15.75 29.5H20.25C21.7 29.5 22.9 28.45 23 27L24 11.5",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function FolderActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M7.5 11.5H14L16.25 14H28.5V25.5C28.5 26.6 27.6 27.5 26.5 27.5H9.5C8.4 27.5 7.5 26.6 7.5 25.5V11.5Z",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M7.5 15H28.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function InstallActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M18 7.5V21.5",
          stroke: "currentColor",
          strokeWidth: "2.8",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M12.5 16.75L18 22.25L23.5 16.75",
          stroke: "currentColor",
          strokeWidth: "2.8",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
        createElement("path", {
          d: "M9.5 27.5H26.5",
          stroke: "currentColor",
          strokeWidth: "2.8",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function EyeActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M4.75 18C7.25 13.15 12.1 10 18 10C23.9 10 28.75 13.15 31.25 18C28.75 22.85 23.9 26 18 26C12.1 26 7.25 22.85 4.75 18Z",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinejoin: "round",
        }),
        createElement("circle", {
          cx: "18",
          cy: "18",
          r: "4",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
      ),
    );
  }

  function LogActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("rect", {
          x: "8",
          y: "8",
          width: "20",
          height: "20",
          rx: "3",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
        createElement("path", {
          d: "M13 14H23M13 18H23M13 22H19",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function AddActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("circle", {
          cx: "18",
          cy: "18",
          r: "10.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
        createElement("path", {
          d: "M18 12.5V23.5M12.5 18H23.5",
          stroke: "currentColor",
          strokeWidth: "2.8",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function ResolutionActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("rect", {
          x: "6.5",
          y: "8.5",
          width: "23",
          height: "15",
          rx: "2.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
        createElement("path", {
          d: "M13 27.5H23M18 23.5V27.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function RefreshRateActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("path", {
          d: "M11.5 24.5C13.2 26.65 15.75 28 18.6 28C23.75 28 27.9 23.85 27.9 18.7C27.9 13.55 23.75 9.4 18.6 9.4C15.3 9.4 12.35 11.15 10.7 13.85",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M10.25 18.7H18.75L23 14.45",
          stroke: "currentColor",
          strokeWidth: "2.6",
          strokeLinecap: "round",
          strokeLinejoin: "round",
        }),
      ),
    );
  }

  function DesktopActionIcon() {
    return createElement(
      "svg",
      withChildren(
        { xmlns: "http://www.w3.org/2000/svg", viewBox: "0 0 36 36", fill: "none" },
        createElement("rect", {
          x: "6.5",
          y: "8.5",
          width: "23",
          height: "15",
          rx: "2.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
        }),
        createElement("path", {
          d: "M10 27.5H26",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
        createElement("path", {
          d: "M15 23.5V27.5M21 23.5V27.5",
          stroke: "currentColor",
          strokeWidth: "2.4",
          strokeLinecap: "round",
        }),
      ),
    );
  }

  function normalizeIconLookupText(value) {
    return typeof value === "string" ? value.trim().toLowerCase() : "";
  }

  function findPluginIconByTitle(title) {
    const normalizedTitle = normalizeIconLookupText(title);
    if (!normalizedTitle) {
      return null;
    }

    const pluginId = getVisiblePlugins().find((plugin) => normalizeIconLookupText(plugin.title) === normalizedTitle)?.id;
    return pluginId ? getPluginIconComponent(pluginId) : null;
  }

  function resolveDefaultSlotLeadingIcon(slot, route = state.route) {
    if (slot?.leadingIcon) {
      return slot.leadingIcon;
    }

    const title = normalizeIconLookupText(slot?.title);
    const copy = normalizeIconLookupText(slot?.copy);
    const text = `${title} ${copy}`.trim();
    const role = normalizeIconLookupText(slot?.role);
    const routePluginId = normalizeIconLookupText(route?.pluginId);
    const matchedPluginIcon = findPluginIconByTitle(slot?.title);

    if (matchedPluginIcon) {
      return matchedPluginIcon;
    }

    if (text.includes("refresh rate")) {
      return RefreshRateActionIcon;
    }

    if (text.includes("resolution")) {
      return ResolutionActionIcon;
    }

    if (text.includes("output mode") || text.includes("windows desktop")) {
      return DesktopActionIcon;
    }

    if (text.includes("steamgriddb") || text.includes("artwork")) {
      return ArtworkPluginIcon;
    }

    if (text.includes("cssloader") || text.includes("preset") || text.includes("theme")) {
      return ThemesPluginIcon;
    }

    if (text.includes("auto sisr") || text.includes("sisr")) {
      return AutoSisirPluginIcon;
    }

    if (text.includes("howlongtobeat") || text.includes("game page")) {
      return HltbPluginIcon;
    }

    if (title.startsWith("preview") || text.includes("preview")) {
      return EyeActionIcon;
    }

    if (text.includes("journal") || text.includes("log")) {
      return LogActionIcon;
    }

    if (title.startsWith("refresh") || text.includes("refresh ")) {
      return RefreshActionIcon;
    }

    if (title.startsWith("save") || text.includes("back up")) {
      return SaveActionIcon;
    }

    if (text.includes("reset")) {
      return ResetActionIcon;
    }

    if (text.includes("remove") || text.includes("exclude") || text.includes("clean up")) {
      return DeleteActionIcon;
    }

    if (text.includes("clear")) {
      return text.includes("cache") ? DeleteActionIcon : ResetActionIcon;
    }

    if (text.includes("install")) {
      return InstallActionIcon;
    }

    if (title.startsWith("add")) {
      return AddActionIcon;
    }

    if (text.includes("folder")) {
      return FolderActionIcon;
    }

    if (
      title.startsWith("launch") ||
      title.startsWith("start") ||
      title.startsWith("show") ||
      text.includes("apply preset") ||
      text.includes("apply this")
    ) {
      return LaunchActionIcon;
    }

    if (title.startsWith("stop")) {
      return StopActionIcon;
    }

    if (text.includes("restart")) {
      return RestartActionIcon;
    }

    if (text.includes("sleep")) {
      return SleepActionIcon;
    }

    if (text.includes("shut down") || text.includes("shutdown")) {
      return ShutdownActionIcon;
    }

    if (text.includes("settings")) {
      return SettingsPluginIcon;
    }

    if (text.includes("stores")) {
      return StoreSyncPluginIcon;
    }

    if (text.includes("windows shell")) {
      return DesktopActionIcon;
    }

    if (text.includes("delay")) {
      return RefreshRateActionIcon;
    }

    if (text.includes("microphone")) {
      return MicrophoneIcon;
    }

    if (text.includes("speaker") || text.includes("playback")) {
      return AudioPluginIcon;
    }

    if (text.includes("display")) {
      return DisplayPluginIcon;
    }

    if (text.includes("developer") || text.includes("debug")) {
      return SettingsPluginIcon;
    }

    if (text.includes("show ") && role === "toggle") {
      return EyeActionIcon;
    }

    if (text.includes("download artwork")) {
      return ArtworkPluginIcon;
    }

    if (role === "navigation") {
      switch (routePluginId) {
        case "display":
          return DisplayPluginIcon;
        case "store-sync":
          return StoreSyncPluginIcon;
        case "settings":
          return SettingsPluginIcon;
        case "themes":
          return ThemesPluginIcon;
        case "app-start":
          return AppStartPluginIcon;
        case "auto-sisr":
          return AutoSisirPluginIcon;
        case "artwork":
          return ArtworkPluginIcon;
        case "hltb":
          return HltbPluginIcon;
        default:
          break;
      }
    }

    if (role === "command" || role === "action") {
      switch (routePluginId) {
        case "processes":
          return ProcessesPluginIcon;
        case "store-sync":
          return StoreSyncPluginIcon;
        default:
          break;
      }
    }

    return null;
  }

  function findPluginDefinition(pluginId) {
    return plugins.find((plugin) => plugin.id === pluginId) || null;
  }

  function findPluginPageDefinition(pluginId, pageId) {
    const plugin = findPluginDefinition(pluginId);
    return plugin?.pages?.find((page) => page.id === pageId) || null;
  }

  function getRouteTitle(route) {
    if (!route || route.screen === "root") {
      return "Tools for Steam";
    }

    if (route.screen === "plugin") {
      return findPluginDefinition(route.pluginId)?.title || "Tools for Steam";
    }

    if (route.screen === "page") {
      return (
        findPluginPageDefinition(route.pluginId, route.pageId)?.title ||
        findPluginDefinition(route.pluginId)?.title ||
        "Tools for Steam"
      );
    }

    return "Tools for Steam";
  }

  function createGlobalBackSlot(route = state.route) {
    const backNavigation = getBackNavigation(route);
    if (!backNavigation) {
      return null;
    }

    return {
      kind: "button",
      role: "back",
      title: "Back",
      copy: `Return to ${getRouteTitle(backNavigation.route)}.`,
      onClick: () => navigateBackFromRoute(route),
      disabled: false,
      badge: "",
      trailing: "none",
      switchValue: undefined,
      switchLabel: "",
      leadingIcon: BackIcon,
      buttonClassName: "steamloader-dialog-button steamloader-dialog-button-global-back",
      buttonStyle: null,
      buttonProps: null,
      rowClassName: "steamloader-row-shell-global-back",
      slotKey: globalBackSlotKey,
      selected: false,
      value: globalBackSlotKey,
      nativeComponentId: "dialogButton",
    };
  }

  function withGlobalBackSlot(model, route = state.route) {
    const backSlot = createGlobalBackSlot(route);
    if (!backSlot) {
      return {
        ...model,
        topSlots: Array.isArray(model.topSlots) ? model.topSlots : [],
      };
    }

    const topSlots = Array.isArray(model.topSlots) ? model.topSlots : [];
    if (topSlots.some((slot) => resolveSlotFocusKey(slot) === globalBackSlotKey)) {
      return model;
    }

    return {
      ...model,
      topSlots: [backSlot, ...topSlots],
    };
  }

  function getRenderableSlots(model) {
    return [
      ...(Array.isArray(model?.topSlots) ? model.topSlots : []),
      ...(Array.isArray(model?.slots) ? model.slots : []),
    ];
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

    if (slot.badge && slot.layout !== "feature") {
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

  function buildFallbackRowClassName(slot) {
    const roleClassName = slot.role ? ` steamtools-row-${slot.role}` : "";
    const layoutClassName = slot.layout ? ` steamtools-row-layout-${slot.layout}` : "";

    if (slot.leadingIcon) {
      return slot.rowClassName
        ? `steamloader-row-shell steamloader-row-shell-with-icon${roleClassName}${layoutClassName} ${slot.rowClassName}`
        : `steamloader-row-shell steamloader-row-shell-with-icon${roleClassName}${layoutClassName}`;
    }

    return slot.rowClassName
      ? `steamloader-row-shell${roleClassName}${layoutClassName} ${slot.rowClassName}`
      : `steamloader-row-shell${roleClassName}${layoutClassName}`;
  }

  function createAccordionRowContent(slot) {
    return createElement(
      "div",
      withChildren(
        {
          className: `steamloader-accordion-toggle${slot.expanded ? " is-expanded" : ""}`,
        },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-accordion-toggle-copy-wrap" },
            createElement("div", {
              className: "steamloader-accordion-toggle-title",
              children: slot.title,
            }),
            slot.copy
              ? createElement("div", {
                  className: "steamloader-accordion-toggle-copy",
                  children: slot.copy,
                })
              : null,
          ),
        ),
        createElement("span", {
          className: "steamloader-accordion-toggle-arrow",
          children: "v",
        }),
      ),
    );
  }

  function createFeatureRowContent(slot, trailingContent) {
    const metaItems = Array.isArray(slot.meta) ? slot.meta.filter(Boolean) : [];
    const FeatureIcon = slot.leadingIcon;

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-feature-card" },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-feature-media-shell" },
            slot.mediaImageSrc
              ? createElement("img", {
                  className: "steamloader-feature-media",
                  src: slot.mediaImageSrc,
                  alt: slot.mediaImageAlt || slot.title || "",
                })
              : createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-feature-media-placeholder" },
                    FeatureIcon ? createElement(FeatureIcon, {}) : null,
                  ),
                ),
            slot.eyebrow
              ? createElement("span", {
                  className: "steamloader-feature-eyebrow",
                  children: slot.eyebrow,
                })
              : null,
            slot.badge
              ? createElement("span", {
                  className: "steamloader-badge steamloader-feature-status",
                  children: slot.badge,
                })
              : null,
          ),
        ),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-feature-body" },
            createElement("div", {
              className: "steamloader-feature-title",
              children: slot.title,
            }),
            slot.copy
              ? createElement("div", {
                  className: "steamloader-feature-copy",
                  children: slot.copy,
                })
              : null,
            metaItems.length
              ? createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-feature-meta" },
                    ...metaItems.map((item, metaIndex) =>
                      createElement("span", {
                        className: "steamloader-feature-meta-item",
                        key: `feature-meta-${metaIndex}`,
                        children: item,
                      }),
                    ),
                  ),
                )
              : null,
            createElement(
              "div",
              withChildren(
                { className: "steamloader-feature-footer" },
                createElement("span", {
                  className: "steamloader-feature-footer-copy",
                  children: slot.footerLabel || "Open",
                }),
                trailingContent
                  ? createElement(
                      "span",
                      withChildren(
                        { className: "steamloader-feature-footer-chevron" },
                        trailingContent,
                      ),
                    )
                  : null,
              ),
            ),
          ),
        ),
      ),
    );
  }

  function createInlineStepperRowContent(slot) {
    const primaryText = slot.title || slot.copy || "";
    const secondaryText = slot.title && slot.copy ? slot.copy : "";

    return createElement(
      "div",
      withChildren(
        {
          className: `steamloader-inline-stepper${secondaryText ? "" : " is-compact"}`,
        },
        createElement(
          "span",
          withChildren(
            {
              className: `steamloader-inline-stepper-arrow${slot.stepperLeftDisabled ? " is-disabled" : ""}`,
              "aria-hidden": "true",
            },
            createElement(BackIcon, {}),
          ),
        ),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-inline-stepper-main" },
            primaryText
              ? createElement("div", {
                  className: "steamloader-inline-stepper-title",
                  children: primaryText,
                })
              : null,
            secondaryText
              ? createElement("div", {
                  className: "steamloader-inline-stepper-copy",
                  children: secondaryText,
                })
              : null,
          ),
        ),
        createElement(
          "span",
          withChildren(
            {
              className: `steamloader-inline-stepper-arrow${slot.stepperRightDisabled ? " is-disabled" : ""}`,
              "aria-hidden": "true",
            },
            createElement(ChevronIcon, {}),
          ),
        ),
      ),
    );
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
            ...(line && typeof line === "object" && line.liveKey
              ? { "data-live-value": line.liveKey }
              : {}),
            children: line && typeof line === "object" ? line.text : line,
          }),
        ),
        card.swatchHex
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-card-swatch" },
                createElement("span", {
                  className: "steamloader-card-swatch-dot",
                  style: {
                    background: card.swatchHex,
                  },
                }),
                createElement("span", {
                  className: "steamloader-card-swatch-label",
                  children: card.swatchLabel || card.swatchHex,
                }),
              ),
            )
          : null,
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

  function tryInvokeSteamKeyboardOpener(opener, argSets) {
    for (const args of argSets) {
      try {
        opener(...args);
        return true;
      } catch {}
    }

    return false;
  }

  function tryPostSteamVirtualKeyboardMessage(message) {
    const payload = {
      type: "VirtualKeyboardMessage",
      message,
    };
    const payloadText = JSON.stringify(payload);
    let posted = false;

    try {
      if (typeof window.SteamClient?.BrowserView?.PostMessageToParent === "function") {
        window.SteamClient.BrowserView.PostMessageToParent(payload.type, payloadText);
        posted = true;
      }
    } catch {}

    try {
      if (window.parent && window.parent !== window && typeof window.parent.postMessage === "function") {
        window.parent.postMessage(payload, "*");
        posted = true;
      }
    } catch {}

    try {
      if (window.opener && typeof window.opener.postMessage === "function") {
        window.opener.postMessage(payload, "*");
        posted = true;
      }
    } catch {}

    return posted;
  }

  let lastTfsSteamKeyboardRequestAt = 0;
  let lastTfsSteamKeyboardRequestKey = "";

  function markSteamKeyboardLikelyActive() {
    state.steamKeyboardActiveUntil = Date.now() + 60000;
  }

  function requestTfsSteamKeyboard(element, description) {
    if (!apiBase || !(element instanceof HTMLElement)) {
      return false;
    }

    markSteamKeyboardLikelyActive();

    const rect = element.getBoundingClientRect();
    const currentValue = element.value || "";
    const payload = {
      label: description || "Text",
      value: currentValue,
      x: rect.left,
      y: rect.top,
      width: rect.width,
      height: rect.height,
    };
    const requestKey = JSON.stringify({
      label: payload.label,
      value: payload.value,
      x: Math.round(payload.x),
      y: Math.round(payload.y),
      width: Math.round(payload.width),
      height: Math.round(payload.height),
    });
    const now = Date.now();

    if (requestKey === lastTfsSteamKeyboardRequestKey && now - lastTfsSteamKeyboardRequestAt < 650) {
      markSteamKeyboardLikelyActive();
      return true;
    }

    lastTfsSteamKeyboardRequestKey = requestKey;
    lastTfsSteamKeyboardRequestAt = now;

    try {
      void fetch(`${apiBase}api/steam/keyboard/show`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        cache: "no-store",
        body: JSON.stringify(payload),
      }).catch(() => {});
      return true;
    } catch {
      return false;
    }
  }

  function tryOpenSteamKeyboard(element, description) {
    if (element instanceof HTMLElement) {
      markSteamKeyboardLikelyActive();
    }

    if (requestTfsSteamKeyboard(element, description)) {
      return true;
    }

    const label = description || "Text";
    const currentValue = element instanceof HTMLElement ? element.value || "" : "";
    const rect = element instanceof HTMLElement ? element.getBoundingClientRect() : null;
    let opened = false;

    try {
      if (typeof window.navigator?.virtualKeyboard?.show === "function") {
        window.navigator.virtualKeyboard.show();
        opened = true;
      }
    } catch {}

    opened = tryPostSteamVirtualKeyboardMessage("ShowVirtualKeyboard") || opened;

    const steamInput = window.SteamClient?.Input;
    if (typeof steamInput?.ShowFloatingGamepadTextInput === "function" && rect) {
      opened =
        tryInvokeSteamKeyboardOpener(steamInput.ShowFloatingGamepadTextInput.bind(steamInput), [
          [0, Math.round(rect.left), Math.round(rect.top), Math.round(rect.width), Math.round(rect.height)],
          [0, Math.round(rect.left), Math.round(rect.top), Math.round(rect.right), Math.round(rect.bottom)],
        ]) || opened;
    }

    if (typeof steamInput?.ShowGamepadTextInput === "function") {
      opened =
        tryInvokeSteamKeyboardOpener(steamInput.ShowGamepadTextInput.bind(steamInput), [
          [0, 0, label, 256, currentValue],
          [0, 0, label, 1024, currentValue],
        ]) || opened;
    }

    const openVrKeyboard = window.SteamClient?.OpenVR?.Keyboard;
    if (typeof openVrKeyboard?.Show === "function") {
      opened =
        tryInvokeSteamKeyboardOpener(openVrKeyboard.Show.bind(openVrKeyboard), [
          [],
          [0, 0, 0, label, 256, currentValue, false, 0],
          [0, 0, label, 256, currentValue],
          [label, currentValue],
        ]) || opened;
    }

    return opened;
  }

  function queuePendingEditorFocusRestore() {
    if (!isEditorFocusForRoute() || !state.editorFocusCardKey) {
      return;
    }

    clearPendingFocusRestore();

    const editorKey = state.editorFocusCardKey;
    window.requestAnimationFrame(() => {
      if (!isEditorFocusForRoute() || state.editorFocusCardKey !== editorKey) {
        return;
      }
      const panel = getPanelScrollContainer();
      if (!(panel instanceof HTMLElement)) {
        return;
      }
      let textarea = null;
      for (const el of panel.querySelectorAll("[data-editor-key]")) {
        if (el.getAttribute("data-editor-key") === editorKey) {
          textarea = el;
          break;
        }
      }
      if (textarea instanceof HTMLElement) {
        textarea.focus({ preventScroll: true });
        restoreEditorSelection(textarea, { preferEnd: true });
      }
    });
  }

  function createEditorCard(editor) {
    const cardKey = editor.inputKey || editor.cardKey || "steamloader-editor";
    const editorDataKey = `editor-${cardKey}`;
    const isSecretEditor = editor.inputType === "password" || editor.secret === true;
    const editorElementType = isSecretEditor ? "input" : "textarea";

    function focusEditorTextarea() {
      const panel = getPanelScrollContainer();
      if (!(panel instanceof HTMLElement)) {
        return;
      }
      let textarea = null;
      for (const el of panel.querySelectorAll("[data-editor-key]")) {
        if (el.getAttribute("data-editor-key") === editorDataKey) {
          textarea = el;
          break;
        }
      }
      if (textarea instanceof HTMLElement) {
        markEditorFocused(editorDataKey);
        textarea.focus({ preventScroll: true });
        restoreEditorSelection(textarea, { preferEnd: true });
        tryOpenSteamKeyboard(textarea, editor.label);
        window.requestAnimationFrame(() => tryOpenSteamKeyboard(textarea, editor.label));
        window.setTimeout(() => tryOpenSteamKeyboard(textarea, editor.label), 120);
      }
    }

    const triggerButton = NativeDialogButton(
      createElement(
        "div",
        withChildren(
          { className: "steamloader-editor-trigger-content" },
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
        ),
      ),
      focusEditorTextarea,
      {
        slotKey: editorDataKey,
        className: "steamloader-dialog-button steamloader-editor-trigger",
        extraProps: {
          "data-slot-button": editorDataKey,
          "data-slot-key": editorDataKey,
          onOKButton: focusEditorTextarea,
          onActivate: focusEditorTextarea,
          onGamepadFocus: () => {
            state.lastSelectedSlotKeyByRoute[getRouteKey(state.route)] = editorDataKey;
          },
          onCancelButton: getBackNavigation()
            ? () => {
                navigateBackFromRoute();
              }
            : undefined,
          style: {
            width: "100%",
            minWidth: 0,
          },
        },
      },
      `${cardKey}-trigger`,
    );

    const textareaElement = createElement(editorElementType, {
      key: editor.inputKey,
      className: `steamloader-editor-textarea${isSecretEditor ? " steamloader-editor-input-secret" : ""}`,
      "data-editor-key": editorDataKey,
      "data-custom-path-input": editor.isCustomPath ? "true" : undefined,
      type: isSecretEditor ? "password" : undefined,
      defaultValue: editor.value || "",
      placeholder: editor.placeholder || "",
      rows: isSecretEditor ? undefined : editor.rows || 3,
      spellCheck: false,
      autoCapitalize: "off",
      autoCorrect: "off",
      autoComplete: isSecretEditor ? "new-password" : "off",
      onClick: (event) => {
        event.stopPropagation();
        markEditorFocused(editorDataKey, event.target);
        rememberEditorSelection(event.target);
        tryOpenSteamKeyboard(event.target, editor.label);
      },
      onFocus: (event) => {
        markEditorFocused(editorDataKey, event.target);
      },
      onBlur: (event) => {
        rememberEditorSelection(event.target);
        window.setTimeout(() => {
          const panel = getPanelScrollContainer();
          const activeElement = document.activeElement;
          if (
            state.editorFocusCardKey !== editorDataKey ||
            activeElement === document.body ||
            activeElement?.getAttribute?.("data-editor-key") === editorDataKey
          ) {
            return;
          }

          if (panel instanceof HTMLElement && activeElement instanceof HTMLElement && panel.contains(activeElement)) {
            clearEditorFocus(editorDataKey);
          }
        }, 120);
      },
      onInput: (event) => {
        editor.onInput?.(event.target.value);
        rememberEditorSelection(event.target);
      },
      onSelect: (event) => {
        rememberEditorSelection(event.target);
      },
      onKeyUp: (event) => {
        rememberEditorSelection(event.target);
      },
    });

    return createElement(
      "div",
      withChildren({ className: "steamloader-editor-card" }, triggerButton, textareaElement),
      cardKey,
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
          rememberCurrentRouteIndex,
          rememberCurrentRouteSlot,
          resolveSlotFocusKey,
          navigateBackFromRoute,
        },
      );
    }

    const backNavigation = getBackNavigation();
    const trailingContent = renderTrailingContent(slot);
    const buttonContent =
      slot.layout === "accordion"
        ? createAccordionRowContent(slot)
        : slot.layout === "feature"
          ? createFeatureRowContent(slot, trailingContent)
          : slot.layout === "stepper"
            ? createInlineStepperRowContent(slot)
          : createElement(
              "div",
              withChildren(
                { className: buildFallbackRowClassName(slot) },
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
                    slot.swatchHex
                      ? createElement(
                          "div",
                          withChildren(
                            { className: "steamloader-row-swatch" },
                            createElement("span", {
                              className: "steamloader-row-swatch-dot",
                              style: {
                                background: slot.swatchHex,
                              },
                            }),
                            createElement("span", {
                              className: "steamloader-row-swatch-label",
                              children: slot.swatchLabel || slot.swatchHex,
                            }),
                          ),
                        )
                      : null,
                  ),
                ),
                createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-row-trailing" },
                    trailingContent,
                  ),
                ),
              ),
            );

    return NativeDialogButton(
      buttonContent,
      () => handleSlotClick(index),
      {
        disabled: slot.disabled,
        slotKey: slot.slotKey || null,
        className: slot.buttonClassName || "steamloader-dialog-button",
        extraProps: {
          ...(slot.buttonProps || {}),
          "data-slot-button": String(index),
          "data-slot-key": resolveSlotFocusKey(slot, index) || undefined,
          autoFocus: Number.isInteger(autoFocusIndex) && index === autoFocusIndex,
          style: slot.buttonStyle || undefined,
          onGamepadFocus: () => {
            rememberCurrentRouteSlot(index, slot);
            slot.buttonProps?.onGamepadFocus?.();
          },
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

  function createSectionHeader(index, title, copy = "", options = {}) {
    return {
      index: Number.isInteger(index) ? index : 0,
      title,
      copy,
      icon: options.icon || null,
      sectionKey: options.sectionKey || `${title}-${index}`,
    };
  }

  function getInlineSectionHeaders(model, index) {
    return (Array.isArray(model?.sectionHeaders) ? model.sectionHeaders : []).filter((section) =>
      Number.isInteger(section?.index) && section.index === index,
    );
  }

  function createInlineSectionHeader(section, key) {
    const SectionIcon = section?.icon;
    return createElement(
      "div",
      withChildren(
        {
          className: "steamloader-inline-section",
          key,
        },
        SectionIcon
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-inline-section-mark" },
                createElement(SectionIcon, {}),
              ),
            )
          : null,
        createElement(
          "div",
          withChildren(
            { className: "steamloader-inline-section-copy-wrap" },
            createElement("div", {
              className: "steamloader-inline-section-title",
              children: section?.title || "",
            }),
            section?.copy
              ? createElement("div", {
                  className: "steamloader-inline-section-copy",
                  children: section.copy,
                })
              : null,
          ),
        ),
      ),
    );
  }

  function hasDividerAfter(model, index) {
    if (Number.isInteger(model?.dividerAfterIndex) && index === model.dividerAfterIndex) {
      return true;
    }

    return Array.isArray(model?.dividerAfterIndices) && model.dividerAfterIndices.includes(index);
  }

  function shouldSeparateAfterSlot(slot) {
    return slot?.role === "back" || slot?.trailing === "back";
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

  function usesCustomShellRoute(route = state.route) {
    return isAudioDashboardRoute(route) || isPerformanceOverlayRoute(route);
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

  function reconcilePlaybackVolumeInfoWithOptimisticValue(volumeInfo) {
    const desiredVolume = getOptimisticDesiredValue("audio.playback.volume");
    if (!Number.isFinite(desiredVolume)) {
      return volumeInfo;
    }

    const nextValue = snapVolumeToStep(desiredVolume);
    const currentSnapshotValue = snapVolumeToStep(volumeInfo?.volume);
    if (Object.is(currentSnapshotValue, nextValue)) {
      clearOptimisticDesiredValue("audio.playback.volume", desiredVolume);
      return volumeInfo;
    }

    const fallbackInfo = state.audio.volumeInfo;
    const baseInfo =
      volumeInfo && typeof volumeInfo === "object"
        ? volumeInfo
        : fallbackInfo && typeof fallbackInfo === "object"
          ? fallbackInfo
          : null;

    if (!baseInfo) {
      return volumeInfo;
    }

    return {
      ...baseInfo,
      volume: nextValue,
      isMuted: nextValue <= 0 ? true : false,
    };
  }

  function reconcileCaptureVolumeInfoWithOptimisticValue(volumeInfo) {
    const desiredVolume = getOptimisticDesiredValue("audio.capture.volume");
    if (!Number.isFinite(desiredVolume)) {
      return volumeInfo;
    }

    const nextValue = snapVolumeToStep(desiredVolume);
    const currentSnapshotValue = snapVolumeToStep(volumeInfo?.volume);
    if (Object.is(currentSnapshotValue, nextValue)) {
      clearOptimisticDesiredValue("audio.capture.volume", desiredVolume);
      return volumeInfo;
    }

    const fallbackInfo = state.audio.captureVolumeInfo;
    const baseInfo =
      volumeInfo && typeof volumeInfo === "object"
        ? volumeInfo
        : fallbackInfo && typeof fallbackInfo === "object"
          ? fallbackInfo
          : null;

    if (!baseInfo) {
      return volumeInfo;
    }

    return {
      ...baseInfo,
      volume: nextValue,
      isMuted: nextValue <= 0 ? true : Boolean(baseInfo.isMuted),
    };
  }

  function applyAudioDashboardSnapshotIfCurrent(snapshot, options = {}) {
    if (!isSnapshotObject(snapshot)) {
      setAudioDashboardSnapshot(snapshot, options);
      return true;
    }

    setAudioDashboardSnapshot(
      {
        ...snapshot,
        playbackVolume: reconcilePlaybackVolumeInfoWithOptimisticValue(snapshot.playbackVolume),
        captureVolume: reconcileCaptureVolumeInfoWithOptimisticValue(snapshot.captureVolume),
      },
      options,
    );
    return true;
  }

  function previewSliderVolume(value) {
    const info = state.audio.volumeInfo;
    if (!info) {
      return;
    }

    const nextValue = snapVolumeToStep(value);
    state.audio.volumeInfo = {
      ...info,
      volume: nextValue,
      isMuted: nextValue <= 0 ? true : false,
    };

    if (isAudioDashboardRoute()) {
      refreshAudioDashboardUi();
    } else {
      refreshAudioVolumePanel();
    }
  }

  function queueSliderVolumeCommit(value) {
    const nextValue = snapVolumeToStep(value);
    setOptimisticDesiredValue("audio.playback.volume", nextValue);
    clearVolumeCommitTimer();
    state.audio.volumeCommitTimer = window.setTimeout(() => {
      state.audio.volumeCommitTimer = 0;
      void setVolume(nextValue);
    }, audioVolumeCommitSettleDelayMs);
  }

  function stepVolumeSlider(direction, step = 10) {
    if (!direction) {
      return false;
    }

    const currentValue = getVolumeValue();
    const nextValue = snapVolumeToStep(currentValue + direction * step);
    if (nextValue === currentValue) {
      return false;
    }

    playSliderMoveSound(direction);
    previewSliderVolume(nextValue);
    queueSliderVolumeCommit(nextValue);
    return true;
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
    refreshAudioDashboardUi();
  }

  function queueCaptureSliderVolumeCommit(value) {
    const nextValue = snapVolumeToStep(value);
    setOptimisticDesiredValue("audio.capture.volume", nextValue);
    clearCaptureVolumeCommitTimer();
    state.audio.captureVolumeCommitTimer = window.setTimeout(() => {
      state.audio.captureVolumeCommitTimer = 0;
      void setCaptureVolume(nextValue);
    }, audioVolumeCommitSettleDelayMs);
  }

  function stepCaptureVolumeSlider(direction, step = 10) {
    if (!direction) {
      return false;
    }

    const currentValue = getCaptureVolumeValue();
    const nextValue = snapVolumeToStep(currentValue + direction * step);
    if (nextValue === currentValue) {
      return false;
    }

    playSliderMoveSound(direction);
    previewCaptureSliderVolume(nextValue);
    queueCaptureSliderVolumeCommit(nextValue);
    return true;
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
      stepVolumeSlider(isLeft ? -1 : 1);
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
      renderPanelDataRefresh();
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
    refreshAudioMixerUi();
  }

  function queueAudioMixerVolumeCommit(sessionId, value) {
    const nextValue = snapAudioMixerVolumeToStep(value);
    setOptimisticDesiredValue(`audio.mixer.${sessionId}.volume`, nextValue);
    clearAudioMixerVolumeCommitTimer(sessionId);
    state.audio.mixerVolumeCommitTimersById[sessionId] = window.setTimeout(() => {
      delete state.audio.mixerVolumeCommitTimersById[sessionId];
      void setAudioMixerSessionVolume(sessionId, nextValue, { optimistic: false });
    }, sliderCommitSettleDelayMs);
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
    if (nextValue === currentValue) {
      return;
    }

    playSliderMoveSound(direction);
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

  function getPerformanceSnapshotSettingValue(snapshot, settingKey) {
    const settings = snapshot?.settings;
    switch (settingKey) {
      case "overlay-level":
        return settings?.overlayLevel;
      case "overlay-position":
        return settings?.overlayPosition;
      case "overlay-width":
        return settings?.overlayWidth;
      case "overlay-scale":
        return settings?.overlayScale;
      case "graph-mode":
        return settings?.graphMode;
      case "background-theme":
        return settings?.backgroundTheme;
      case "background-opacity":
        return settings?.backgroundOpacity;
      case "metric-poll-rate":
        return settings?.metricPollRate;
      case "telemetry-period":
        return settings?.telemetrySamplingPeriodMs;
      case "metrics-window":
        return settings?.metricsWindow;
      case "overlay-draw-rate":
        return settings?.overlayDrawRate;
      default:
        return undefined;
    }
  }

  function applyPerformanceSnapshotIfCurrent(snapshot) {
    const optimisticEntries = getOptimisticDesiredEntries("performance.setting.");
    if (!optimisticEntries.length) {
      setPerformanceSnapshot(snapshot);
      return true;
    }

    const matchesAllDesiredValues = optimisticEntries.every(([key, desiredValue]) =>
      Object.is(
        getPerformanceSnapshotSettingValue(snapshot, key.slice("performance.setting.".length)),
        desiredValue,
      ),
    );

    if (!matchesAllDesiredValues) {
      return false;
    }

    setPerformanceSnapshot(snapshot);
    optimisticEntries.forEach(([key, desiredValue]) => {
      clearOptimisticDesiredValue(key, desiredValue);
    });
    return true;
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
    if (Number.isInteger(state.performance.draftOverlayLevel)) {
      return state.performance.draftOverlayLevel;
    }

    if (Number.isInteger(state.performance.pendingOverlayLevelCommit)) {
      return state.performance.pendingOverlayLevelCommit;
    }

    return getPerformanceOverlayLevel();
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

  function clearPerformanceSettingCommitTimer(key) {
    const timerHandle = state.performance.settingCommitTimersByKey[key];
    if (!timerHandle) {
      return;
    }

    window.clearTimeout(timerHandle);
    delete state.performance.settingCommitTimersByKey[key];
  }

  function getPerformanceOptionSettingTitle(key, value) {
    switch (key) {
      case "overlay-position":
        return performancePositionOptions.find((option) => option.value === value)?.title || "Top Left";
      case "graph-mode":
        return performanceGraphModeOptions.find((option) => option.value === value)?.title || "Off";
      case "background-theme":
        return performanceBackgroundThemeOptions.find((option) => option.value === value)?.title || "Steam Blue";
      default:
        return "";
    }
  }

  function previewPerformanceSettingValue(key, value) {
    const snapshot = getPerformanceSnapshot();
    const settings = getPerformanceSettings();
    if (!snapshot || !settings) {
      return false;
    }

    const nextSettings = {
      ...settings,
    };

    switch (key) {
      case "overlay-position":
        nextSettings.overlayPosition = value;
        nextSettings.overlayPositionTitle = getPerformanceOptionSettingTitle(key, value);
        break;
      case "overlay-width":
        nextSettings.overlayWidth = value;
        break;
      case "overlay-scale":
        nextSettings.overlayScale = value;
        break;
      case "graph-mode":
        nextSettings.graphMode = value;
        nextSettings.graphModeTitle = getPerformanceOptionSettingTitle(key, value);
        break;
      case "background-theme":
        nextSettings.backgroundTheme = value;
        nextSettings.backgroundThemeTitle = getPerformanceOptionSettingTitle(key, value);
        break;
      case "background-opacity":
        nextSettings.backgroundOpacity = value;
        break;
      case "metric-poll-rate":
        nextSettings.metricPollRate = value;
        break;
      case "telemetry-period":
        nextSettings.telemetrySamplingPeriodMs = value;
        break;
      case "metrics-window":
        nextSettings.metricsWindow = value;
        break;
      case "overlay-draw-rate":
        nextSettings.overlayDrawRate = value;
        break;
      default:
        return false;
    }

    state.performance.snapshot = {
      ...snapshot,
      settings: nextSettings,
    };
    return true;
  }

  function queuePerformanceSettingCommit(key, value) {
    setOptimisticDesiredValue(`performance.setting.${key}`, value);
    clearPerformanceSettingCommitTimer(key);
    state.performance.settingCommitTimersByKey[key] = window.setTimeout(() => {
      delete state.performance.settingCommitTimersByKey[key];

      if (state.performance.saving) {
        queuePerformanceSettingCommit(key, value);
        return;
      }

      void setPerformanceSettingValue(key, value, {
        rerenderOnStart: false,
        rerenderOnComplete: false,
        syncVisibleSliders: true,
        reloadOnError: true,
        optimisticKey: `performance.setting.${key}`,
        optimisticValue: value,
      });
    }, sliderCommitSettleDelayMs);
  }

  function cyclePerformanceOptionSetting(key, currentValue, options, direction = 1) {
    if (isPerformanceBusy() || !Array.isArray(options) || !options.length || !direction) {
      return;
    }

    const currentIndex = Math.max(0, options.findIndex((option) => option.value === currentValue));
    const nextIndex = Math.max(0, Math.min(options.length - 1, currentIndex + direction));
    const nextValue = options[nextIndex]?.value ?? options[0].value;
    if (nextValue === currentValue) {
      return;
    }

    playSliderMoveSound(direction);
    if (previewPerformanceSettingValue(key, nextValue)) {
      refreshPerformancePanel();
    }
    queuePerformanceSettingCommit(key, nextValue);
  }

  function adjustPerformanceNumberSetting(key, currentValue, direction, step, min, max) {
    if (isPerformanceBusy() || !direction) {
      return;
    }

    const nextValue = clampPerformanceSettingValue(currentValue + direction * step, min, max);
    if (nextValue === currentValue) {
      return;
    }

    playSliderMoveSound(direction);
    if (previewPerformanceSettingValue(key, nextValue)) {
      refreshPerformancePanel();
    }
    queuePerformanceSettingCommit(key, nextValue);
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
      return "Press Prepare Elevated Helper once. Windows will ask for admin permission, then future starts stay silent even after a restart.";
    }

    return Number.isInteger(state.performance.pendingOverlayLevelCommit) || state.performance.saving
      ? "Use Left / Right to choose a preset. TFS applies it automatically after a short pause."
      : "Use Left / Right to switch presets. No A confirmation is needed.";
  }

  function shouldSyncLivePerformancePanel() {
    return (
      isPerformanceOverlayRoute() &&
      (
        state.performance.saving ||
        Number.isInteger(state.performance.pendingOverlayLevelCommit)
      )
    );
  }

  function previewPerformanceOverlayLevel(level) {
    const snapshot = getPerformanceSnapshot();
    const settings = getPerformanceSettings();
    const levelDefinitions = getPerformanceLevelDefinitions();
    const levelDefinition =
      levelDefinitions.find((entry) => entry.value === level)
      || levelDefinitions[0]
      || null;

    if (!snapshot || !settings || !levelDefinition) {
      return false;
    }

    const sourceLevels = Array.isArray(settings.overlayLevels) && settings.overlayLevels.length
      ? settings.overlayLevels
      : levelDefinitions;

    state.performance.snapshot = {
      ...snapshot,
      settings: {
        ...settings,
        overlayLevel: levelDefinition.value,
        overlayLevelTitle: levelDefinition.title,
        overlayLevelDescription: levelDefinition.description,
        overlayLevels: sourceLevels.map((entry) => ({
          ...entry,
          selected: entry.value === levelDefinition.value,
        })),
      },
    };
    return true;
  }

  function queuePerformanceOverlayLevelCommit(level) {
    setOptimisticDesiredValue("performance.setting.overlay-level", level);
    clearPerformanceSettingCommitTimer("overlay-level");
    state.performance.settingCommitTimersByKey["overlay-level"] = window.setTimeout(() => {
      delete state.performance.settingCommitTimersByKey["overlay-level"];

      if (state.performance.saving) {
        queuePerformanceOverlayLevelCommit(level);
        return;
      }

      void setPerformanceOverlayLevel(level, {
        rerenderOnStart: false,
        rerenderOnComplete: false,
        clearPendingOverlayCommit: true,
        optimisticKey: "performance.setting.overlay-level",
        optimisticValue: level,
      });
    }, sliderCommitSettleDelayMs);
  }

  async function flushPerformanceOverlayLevelCommit() {
    clearPerformanceSettingCommitTimer("overlay-level");

    const deadline = Date.now() + 3000;
    while (state.performance.saving && Date.now() < deadline) {
      await new Promise((resolve) => window.setTimeout(resolve, 40));
    }

    if (state.performance.saving) {
      return false;
    }

    const nextLevel = state.performance.pendingOverlayLevelCommit;
    if (!Number.isInteger(nextLevel)) {
      return true;
    }

    return setPerformanceOverlayLevel(nextLevel, {
      rerenderOnStart: false,
      rerenderOnComplete: false,
      clearPendingOverlayCommit: true,
      optimisticKey: "performance.setting.overlay-level",
      optimisticValue: nextLevel,
    });
  }

  function startPerformanceSliderEditing() {
    state.performance.sliderEditActive = false;
    state.performance.draftOverlayLevel = null;
  }

  function finishPerformanceSliderEditing(commit = true) {
    state.performance.sliderEditActive = false;
    state.performance.draftOverlayLevel = null;
    if (commit) {
      void flushPerformanceOverlayLevelCommit();
    }
  }

  function movePerformanceSlider(direction) {
    const levels = getPerformanceLevelDefinitions();
    if (!levels.length || !direction || isPerformanceBusy()) {
      return;
    }

    const currentIndex = Math.max(
      0,
      levels.findIndex((level) => level.value === getPerformanceDraftLevel()),
    );
    const nextIndex = Math.max(0, Math.min(levels.length - 1, currentIndex + direction));
    const nextLevel = levels[nextIndex]?.value ?? levels[0].value;
    if (nextLevel === levels[currentIndex]?.value) {
      return;
    }

    playSliderMoveSound(direction);
    state.performance.sliderEditActive = false;
    state.performance.draftOverlayLevel = null;
    state.performance.pendingOverlayLevelCommit = nextLevel;
    if (previewPerformanceOverlayLevel(nextLevel)) {
      refreshPerformancePanel();
    }
    queuePerformanceOverlayLevelCommit(nextLevel);
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
    return syncVisibleSlotSliderUi();
  }

  function refreshPerformancePanel(options = {}) {
    if (options.cueSlider) {
      state.performance.pendingSliderAutoFocus = true;
    }

    if (options.fullRender !== true && syncLivePerformancePanelUi()) {
      if (options.cueSlider) {
        state.performance.pendingSliderAutoFocus = false;
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
    if (document.activeElement instanceof HTMLElement && isEditableFocusTarget(document.activeElement)) {
      const editorKey = getEditorDataKey(document.activeElement);
      if (editorKey) {
        markEditorFocused(editorKey, document.activeElement);
        rememberEditorSelection(document.activeElement);
      }
    }

    if (options.preserveFocus === false && state.pendingFocusRouteKey === getRouteKey(state.route)) {
      state.pendingFocusRouteKey = null;
      state.pendingFocusIndex = null;
      state.pendingFocusSlotKey = null;
      if (state.route.screen === "root") {
        state.pendingEntryAutoFocus = false;
      }
    } else if (options.preserveFocus !== false && !state.pendingFocusRouteKey) {
      const focusedSelection = getFocusedSlotState();
      if (Number.isInteger(focusedSelection.index)) {
        requestFocusForRoute(state.route, focusedSelection.index, focusedSelection.slotKey);
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

    if (options.preserveFocus !== false) {
      queuePendingFocusRestore(state.route);
      queuePendingEditorFocusRestore();
    }
  }

  function getFocusedSlotState() {
    const focusedNode =
      document.querySelector(".steamloader-panel [data-slot-button].gpfocus") ||
      document.activeElement?.closest?.(".steamloader-panel [data-slot-button]") ||
      null;
    const slotKeyFromDom = normalizeFocusSlotKey(focusedNode?.getAttribute?.("data-slot-key"));
    const rawValue = focusedNode?.getAttribute?.("data-slot-button");
    const parsedValue = Number.parseInt(rawValue || "", 10);
    const index = Number.isInteger(parsedValue) ? parsedValue : null;
    const slotKey =
      slotKeyFromDom ||
      (Number.isInteger(index) && Array.isArray(state.renderedSlots)
        ? resolveSlotFocusKey(state.renderedSlots[index], index)
        : null);
    return { index, slotKey };
  }

  function getFocusedSlotIndex() {
    return getFocusedSlotState().index;
  }

  function isCurrentPluginRoute(pluginId) {
    return state.route?.pluginId === pluginId;
  }

  function renderPanelDataRefresh() {
    const focusedSelection = getFocusedSlotState();
    requestFocusForRoute(state.route, focusedSelection.index, focusedSelection.slotKey);
    state.renderRevision += 1;
    renderPanelState();
  }

  function rerenderStoreSyncPanel() {
    if (isCurrentPluginRoute("store-sync")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderDisplayPanel() {
    if (isCurrentPluginRoute("display")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderPerformancePanel() {
    if (isPerformanceOverlayRoute()) {
      state.renderRevision += 1;
      renderPanelState();
      return;
    }

    if (isCurrentPluginRoute("performance")) {
      state.renderRevision += 1;
      renderPanelState();
      return;
    }
  }

  function rerenderPowerPanel() {
    if (isCurrentPluginRoute("power")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderProcessesPanel() {
    if (isCurrentPluginRoute("processes")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderAppStartPanel() {
    if (isCurrentPluginRoute("app-start")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderHltbPanel() {
    if (isCurrentPluginRoute("hltb")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderArtworkPanel() {
    if (isCurrentPluginRoute("artwork")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderAutoSisirPanel() {
    if (isCurrentPluginRoute("auto-sisr")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderSmartHomePanel() {
    if (isCurrentPluginRoute("smart-home")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderThemesPanel() {
    applyActiveThemeCss();

    if (isCurrentPluginRoute("themes")) {
      renderPanelDataRefresh();
      return;
    }
  }

  function rerenderGeneralSettingsPanel() {
    if (state.route.pluginId === "settings" || state.route.screen === "root") {
      renderPanelDataRefresh();
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
    }, { rerenderOnStart: false });

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

      if (shouldSuppressGlobalHotkeysForTextInput(event)) {
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

      if (shouldSuppressGlobalHotkeysForTextInput()) {
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
    const ActionIcon = action.icon || null;

    return NativeDialogButton(
      createElement(
        "div",
        withChildren(
          { className: "steamloader-volume-action-shell" },
          ActionIcon
            ? createElement(
                "div",
                withChildren(
                  { className: "steamloader-volume-action-icon" },
                  createElement(ActionIcon, {}),
                ),
              )
            : null,
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
              style: slider.trackStyle || undefined,
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
                ...(slider.fillStyle || {}),
              },
            }),
            createElement("div", {
              className: "steamloader-volume-slider-thumb",
              style: {
                left: `${percent}%`,
                ...(slider.thumbStyle || {}),
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
        if (slider.isEditing) {
          slider.onDeactivate?.();
          return;
        }

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
        rememberCurrentRouteSlot(index, slot);
        rememberVolumeActionFocus(0);
        if (slider.isEditing) {
          slider.onDeactivate?.();
          return;
        }

        slider.onActivate?.();
      },
      {
        disabled: slider.disabled,
        slotKey: slot.slotKey || `performance-slider-${index}`,
        className: `steamloader-dialog-button steamloader-volume-slider-fallback-button steamloader-performance-slider-button${slider.isEditing ? " is-editing" : ""}`,
        extraProps: {
          "data-slot-button": String(index),
          "data-slot-key": resolveSlotFocusKey(slot, index) || undefined,
          "data-volume-slider": "true",
          "data-performance-slider": "true",
          autoFocus: shouldAutoFocus,
          onGamepadFocus: () => {
            rememberCurrentRouteSlot(index, slot);
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
            rememberCurrentRouteSlot(index, slot);
            rememberVolumeActionFocus(0);
            slider.onMoveLeft?.(event);
            return true;
          },
          onMoveRight: (event) => {
            rememberCurrentRouteSlot(index, slot);
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
        rememberCurrentRouteSlot(index, slot);
        panel.onClick?.();
      },
      {
        disabled: slider.disabled,
        slotKey: slot.slotKey || `performance-value-slider-${index}`,
        className: "steamloader-dialog-button steamloader-performance-slider-button",
        extraProps: {
          "data-slot-button": String(index),
          "data-slot-key": resolveSlotFocusKey(slot, index) || undefined,
          "data-performance-slider": "true",
          autoFocus: shouldAutoFocus,
          onGamepadFocus: () => {
            rememberCurrentRouteSlot(index, slot);
          },
          onMoveLeft: (event) => {
            rememberCurrentRouteSlot(index, slot);
            slider.onMoveLeft?.(event);
            return true;
          },
          onMoveRight: (event) => {
            rememberCurrentRouteSlot(index, slot);
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

  function createRichValueSliderSlotButton(slot, index, autoFocusIndex) {
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
            children: panel.error || panel.hint || "Use Left / Right to adjust this value.",
          }),
        ),
      ),
      () => {
        rememberCurrentRouteSlot(index, slot);
        panel.onClick?.();
      },
      {
        disabled: slider.disabled,
        slotKey: slot.slotKey || `rich-value-slider-${index}`,
        className: `steamloader-dialog-button steamloader-volume-slider-fallback-button steamloader-performance-slider-button${slider.isEditing ? " is-editing" : ""}`,
        extraProps: {
          "data-slot-button": String(index),
          "data-slot-key": resolveSlotFocusKey(slot, index) || undefined,
          "data-volume-slider": "true",
          "data-rich-slider": "true",
          autoFocus: shouldAutoFocus,
          onGamepadFocus: () => {
            rememberCurrentRouteSlot(index, slot);
          },
          onMoveLeft: (event) => {
            rememberCurrentRouteSlot(index, slot);
            slider.onMoveLeft?.(event);
            return true;
          },
          onMoveRight: (event) => {
            rememberCurrentRouteSlot(index, slot);
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

  function createRichValueSliderSlot(options) {
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
      customRenderer: createRichValueSliderSlotButton,
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
          trackStyle: options.trackStyle || null,
          fillStyle: options.fillStyle || null,
          thumbStyle: options.thumbStyle || null,
          disabled: Boolean(options.disabled),
          isEditing: Boolean(options.isEditing),
          onCancel: () => {
            if (typeof options.onCancel === "function") {
              options.onCancel();
              return;
            }

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

  function createAudioDashboardButton(control, content, className, autoFocusIndex, indexOffset = 0) {
    const controlIndex = control.index + indexOffset;
    const shouldAutoFocus = Number.isInteger(autoFocusIndex) && autoFocusIndex === controlIndex;

    return NativeDialogButton(
      content,
      () => {
        rememberCurrentRouteIndex(controlIndex);
        control.onClick?.();
      },
      {
        disabled: control.disabled,
        slotKey: control.slotKey || `audio-dashboard-${control.index}`,
        className,
        extraProps: {
          "data-slot-button": String(controlIndex),
          "data-audio-dashboard-control": getAudioDashboardControlSyncKey(control),
          autoFocus: shouldAutoFocus,
          onGamepadFocus: () => {
            rememberCurrentRouteIndex(controlIndex);
          },
          onMoveLeft: control.onMoveLeft
            ? (event) => {
                rememberCurrentRouteIndex(controlIndex);
                control.onMoveLeft?.(event);
                return true;
              }
            : undefined,
          onMoveRight: control.onMoveRight
            ? (event) => {
                rememberCurrentRouteIndex(controlIndex);
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

  function createAudioDashboardQuickButton(control, autoFocusIndex, indexOffset = 0) {
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
      indexOffset,
    );
  }

  function createAudioDashboardSliderButton(
    control,
    autoFocusIndex,
    className = "steamloader-dialog-button steamloader-audio-slider-button",
    indexOffset = 0,
  ) {
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
      indexOffset,
    );
  }

  function createAudioDashboardSelectorButton(control, autoFocusIndex, indexOffset = 0) {
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
      indexOffset,
    );
  }

  function createAudioDashboardCommandButton(control, autoFocusIndex, indexOffset = 0) {
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
      indexOffset,
    );
  }

  function createAudioDashboard(dashboard, indexOffset = 0) {
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
                createAudioDashboardQuickButton(dashboard.playbackToggle, autoFocusIndex, indexOffset),
                createAudioDashboardQuickButton(dashboard.captureToggle, autoFocusIndex, indexOffset),
              ),
            ),
            createDivider("audio-dashboard-quick-divider"),
            createElement(
              "div",
              withChildren(
                { className: "steamloader-audio-slider-stack" },
                createAudioDashboardSliderButton(dashboard.playbackSlider, autoFocusIndex, undefined, indexOffset),
                createAudioDashboardSliderButton(dashboard.captureSlider, autoFocusIndex, undefined, indexOffset),
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
                createAudioDashboardSelectorButton(dashboard.playbackSelector, autoFocusIndex, indexOffset),
                createAudioDashboardSelectorButton(dashboard.captureSelector, autoFocusIndex, indexOffset),
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
                        indexOffset,
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
        createAudioDashboardCommandButton(dashboard.refreshControl, autoFocusIndex, indexOffset),
      ),
    );
  }

  function getFallbackSliderDisplayValue(slider) {
    const min = Number.isFinite(slider?.min) ? slider.min : 0;
    const max = Number.isFinite(slider?.max) ? slider.max : 100;
    const value = Math.max(min, Math.min(max, Math.round(Number(slider?.value) || 0)));
    if (typeof slider?.displayValue === "string" && slider.displayValue.length > 0) {
      return slider.displayValue;
    }

    return `${value}${slider?.valueSuffix || ""}`;
  }

  function getFallbackSliderPercent(slider) {
    const min = Number.isFinite(slider?.min) ? slider.min : 0;
    const max = Number.isFinite(slider?.max) ? slider.max : 100;
    const value = Math.max(min, Math.min(max, Math.round(Number(slider?.value) || 0)));
    const range = Math.max(1, max - min);
    return ((value - min) / range) * 100;
  }

  function syncFallbackSliderVisual(shell, slider) {
    if (!(shell instanceof HTMLElement) || !slider) {
      return false;
    }

    const valueText = getFallbackSliderDisplayValue(slider);
    const percent = getFallbackSliderPercent(slider);
    const labelNode = shell.querySelector(".steamloader-volume-slider-label");
    const valueNode = shell.querySelector(".steamloader-volume-slider-value");
    const trackNode = shell.querySelector(".steamloader-volume-slider-track");
    const fillNode = shell.querySelector(".steamloader-volume-slider-fill");
    const thumbNode = shell.querySelector(".steamloader-volume-slider-thumb");

    if (labelNode instanceof HTMLElement && typeof slider.title === "string") {
      labelNode.textContent = slider.title;
    }

    if (valueNode instanceof HTMLElement) {
      valueNode.textContent = valueText;
    }

    if (trackNode instanceof HTMLElement) {
      trackNode.style.cssText = "";
      Object.assign(trackNode.style, slider.trackStyle || {});
    }

    if (fillNode instanceof HTMLElement) {
      fillNode.style.width = `${percent}%`;
      Object.assign(fillNode.style, slider.fillStyle || {});
    }

    if (thumbNode instanceof HTMLElement) {
      thumbNode.style.left = `${percent}%`;
      Object.assign(thumbNode.style, slider.thumbStyle || {});
    }

    return true;
  }

  function buildCurrentSyncModel() {
    return {
      ...withGlobalBackSlot(buildScreenModel()),
      routeKey: getRouteKey(state.route),
    };
  }

  function getSliderSlotSyncKey(slot, index = null) {
    return resolveSlotFocusKey(slot, index) || slot?.slotKey || "";
  }

  function syncSlotSliderButtonUi(button, slot) {
    const panel = slot?.panel;
    const slider = panel?.slider;
    if (!(button instanceof HTMLElement) || !panel || !slider) {
      return false;
    }

    const titleNode = button.querySelector(".steamloader-volume-title");
    const copyNode = button.querySelector(".steamloader-volume-copy");
    const hintNode = button.querySelector(".steamloader-volume-hint, .steamloader-volume-hint-error");
    const sliderShell = button.querySelector(".steamloader-volume-slider-fallback-shell");
    const hintText = panel.error || panel.hint || "Use Left / Right to adjust this value.";

    button.classList.toggle("is-editing", Boolean(slider.isEditing));

    if (titleNode instanceof HTMLElement) {
      titleNode.textContent = panel.title || "";
    }

    if (copyNode instanceof HTMLElement) {
      copyNode.textContent = panel.copy || "";
    }

    if (hintNode instanceof HTMLElement) {
      const hasError = Boolean(panel.error);
      hintNode.classList.toggle("steamloader-volume-hint-error", hasError);
      hintNode.classList.toggle("steamloader-volume-hint", !hasError);
      hintNode.textContent = hintText;
    }

    return syncFallbackSliderVisual(sliderShell, slider);
  }

  function syncVisibleSlotSliderUi() {
    const sliderButtons = Array.from(document.querySelectorAll(".steamloader-performance-slider-button[data-slot-key]"));
    if (!sliderButtons.length) {
      return false;
    }

    const model = buildCurrentSyncModel();
    const slots = getRenderableSlots(model);
    const sliderSlotMap = new Map();

    slots.forEach((slot, index) => {
      if (slot?.panel?.slider) {
        const syncKey = getSliderSlotSyncKey(slot, index);
        if (syncKey) {
          sliderSlotMap.set(syncKey, slot);
        }
      }
    });

    if (!sliderSlotMap.size) {
      return false;
    }

    let updated = 0;
    for (const button of sliderButtons) {
      const syncKey = button.getAttribute("data-slot-key") || "";
      const slot = sliderSlotMap.get(syncKey);
      if (!slot) {
        return false;
      }

      if (syncSlotSliderButtonUi(button, slot)) {
        updated += 1;
      }
    }

    return updated > 0;
  }

  function getAudioDashboardControlSyncKey(control) {
    if (!control) {
      return "";
    }

    return control.slotKey || `audio-dashboard-${control.index}`;
  }

  function getAudioDashboardControls(dashboard) {
    return [
      dashboard?.playbackToggle,
      dashboard?.captureToggle,
      dashboard?.playbackSlider,
      dashboard?.captureSlider,
      dashboard?.playbackSelector,
      dashboard?.captureSelector,
      ...(Array.isArray(dashboard?.mixerControls) ? dashboard.mixerControls : []),
      dashboard?.refreshControl,
    ].filter(Boolean);
  }

  function syncAudioDashboardButtonUi(button, control) {
    if (!(button instanceof HTMLElement) || !control) {
      return false;
    }

    if (button.querySelector(".steamloader-audio-quick-shell")) {
      const titleNode = button.querySelector(".steamloader-audio-quick-title");
      if (titleNode instanceof HTMLElement) {
        titleNode.textContent = control.title || "";
      }

      button.classList.toggle("is-active", Boolean(control.active));
      return true;
    }

    if (button.querySelector(".steamloader-audio-slider-card")) {
      syncFallbackSliderVisual(
        button.querySelector(".steamloader-volume-slider-fallback-shell"),
        {
          title: control.title,
          value: control.value,
          min: control.min ?? 0,
          max: control.max ?? 100,
          step: control.step ?? 5,
          notchCount: control.notchCount ?? 21,
          displayValue: control.displayValue,
          valueSuffix: control.valueSuffix || "%",
        },
      );

      const copyNode = button.querySelector(".steamloader-audio-slider-copy");
      if (copyNode instanceof HTMLElement) {
        copyNode.textContent = control.copy || "";
      }

      return true;
    }

    if (button.querySelector(".steamloader-audio-selector-card")) {
      const labelNode = button.querySelector(".steamloader-audio-selector-label");
      const valueNode = button.querySelector(".steamloader-audio-selector-value");
      const copyNode = button.querySelector(".steamloader-audio-selector-copy");

      if (labelNode instanceof HTMLElement && typeof control.label === "string") {
        labelNode.textContent = control.label;
      }

      if (valueNode instanceof HTMLElement) {
        valueNode.textContent = control.value || control.title || "";
      }

      if (copyNode instanceof HTMLElement) {
        copyNode.textContent = control.copy || "";
      }

      return true;
    }

    return false;
  }

  function syncVisibleAudioDashboardUi() {
    if (!isAudioDashboardRoute()) {
      return false;
    }

    const dashboardRoot = document.querySelector(".steamloader-audio-dashboard");
    if (!(dashboardRoot instanceof HTMLElement)) {
      return false;
    }

    const dashboard = buildAudioDashboardModel();
    const controls = getAudioDashboardControls(dashboard);
    const buttons = Array.from(document.querySelectorAll("[data-audio-dashboard-control]"));
    const mixerControls = Array.isArray(dashboard?.mixerControls) ? dashboard.mixerControls : [];
    const mixerButtons = buttons.filter((button) => button.classList.contains("steamloader-audio-mixer-button"));
    const emptyState = dashboardRoot.querySelector(".steamloader-audio-empty-state");

    if (mixerButtons.length !== mixerControls.length) {
      return false;
    }

    if (Boolean(emptyState) !== (mixerControls.length === 0)) {
      return false;
    }

    const controlMap = new Map(
      controls.map((control) => [getAudioDashboardControlSyncKey(control), control]),
    );

    let updated = 0;
    for (const button of buttons) {
      const syncKey = button.getAttribute("data-audio-dashboard-control") || "";
      const control = controlMap.get(syncKey);
      if (!control) {
        return false;
      }

      if (syncAudioDashboardButtonUi(button, control)) {
        updated += 1;
      }
    }

    const mixerSummaryNode = dashboardRoot.querySelector(".steamloader-audio-mixer-header .steamloader-audio-card-copy");
    if (mixerSummaryNode instanceof HTMLElement) {
      mixerSummaryNode.textContent = dashboard.mixerSummary || "";
    }

    if (emptyState instanceof HTMLElement) {
      emptyState.textContent = dashboard.emptyMixerText || "";
    }

    return updated > 0;
  }

  function refreshAudioDashboardUi() {
    if (syncVisibleAudioDashboardUi()) {
      return;
    }

    rerenderAudioDashboard();
  }

  function refreshAudioMixerUi() {
    if (isAudioDashboardRoute()) {
      refreshAudioDashboardUi();
      return;
    }

    if (syncVisibleSlotSliderUi()) {
      return;
    }

    rerenderAudioMixerPanel();
  }

  function refreshVisibleSliderSurfaces() {
    let updated = false;

    if (syncLiveVolumePanelUi()) {
      updated = true;
    }

    if (syncVisibleAudioDashboardUi()) {
      updated = true;
    }

    if (syncVisibleSlotSliderUi()) {
      updated = true;
    }

    return updated;
  }

  function createFrontendRenderHelpers() {
    return {
      apiBase,
      DefaultIcon: SteamLoaderIcon,
      BackIcon,
      ChevronIcon,
      getBackNavigation,
      handleSlotClick,
      navigateBackFromRoute,
      getRouteKey: () => getRouteKey(state.route),
      consumeResolvedFocus,
      rememberCurrentRouteIndex,
      rememberCurrentRouteSlot,
      resolveSlotFocusKey,
      consumeVolumeActionAutoFocus,
      rememberVolumeActionFocus,
      getActiveVolumeActionIndex: () => state.audio.activeVolumeActionIndex,
    };
  }

  function SteamLoaderPanelShell() {
    let model = withGlobalBackSlot(buildScreenModel());
    const forceCustomShell = isPerformanceOverlayRoute() || isAudioDashboardRoute();
    const focusSlots = getRenderableSlots(model);
    const resolvedAutoFocusIndex = resolveAutoFocusTarget(
      state.route,
      focusSlots,
      Number.isInteger(model.autoFocusIndex)
        ? model.autoFocusIndex
        : Number.isInteger(model.audioDashboard?.autoFocusIndex)
          ? model.audioDashboard.autoFocusIndex
          : null,
    );
    let renderedModel = {
      ...model,
      autoFocusIndex: resolvedAutoFocusIndex,
      routeKey: getRouteKey(state.route),
    };
    state.renderedSlots = getRenderableSlots(renderedModel);
    state.slotActions = state.renderedSlots.map((slot) => slot.onClick);

    if (!forceCustomShell && window.STFrontendLib?.createPanelShell) {
      try {
        return window.STFrontendLib.createPanelShell(
          state,
          createElement,
          withChildren,
          renderedModel,
          createFrontendRenderHelpers(),
        );
      } catch (error) {
        state.nativeUi.renderError = error instanceof Error ? error.message : String(error);
        console.warn("[Tools for Steam] Recovered from st-frontend-lib render error.", error);
        model = {
          ...withGlobalBackSlot(model),
          error: model.error || "Tools for Steam recovered from an internal UI renderer error.",
        };
        renderedModel = {
          ...model,
          autoFocusIndex: resolvedAutoFocusIndex,
          routeKey: getRouteKey(state.route),
        };
        state.renderedSlots = getRenderableSlots(renderedModel);
        state.slotActions = state.renderedSlots.map((slot) => slot.onClick);
      }
    }

    const HeaderIcon = model.headerIcon === null ? null : model.headerIcon || SteamLoaderIcon;
    const headerActions = Array.isArray(model.headerActions) ? model.headerActions : [];
    consumeResolvedFocus(state.route, resolvedAutoFocusIndex);
    const topSlots = Array.isArray(renderedModel.topSlots) ? renderedModel.topSlots : [];
    const topSlotChildren = topSlots.flatMap((slot, index) => {
      const children = [createButtonSlot(slot, index, renderedModel.autoFocusIndex)];
      if (shouldSeparateAfterSlot(slot)) {
        children.push(createDivider(`top-back-divider-${index}`));
      }

      return children;
    });
    const slotIndexOffset = topSlots.length;
    const slotChildren = (Array.isArray(renderedModel.slots) ? renderedModel.slots : []).flatMap((slot, index) => {
      const slotIndex = slotIndexOffset + index;
      const sectionHeaders = getInlineSectionHeaders(renderedModel, index).map((section, sectionIndex) =>
        createInlineSectionHeader(section, `section-${index}-${section.sectionKey || sectionIndex}`),
      );
      const children = [...sectionHeaders, createButtonSlot(slot, slotIndex, renderedModel.autoFocusIndex)];
      if (hasDividerAfter(model, index) || shouldSeparateAfterSlot(slot)) {
        children.push(createDivider(`divider-${slotIndex}`));
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
          "data-route-key": renderedModel.routeKey,
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
        topSlotChildren.length
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-stack steamloader-top-stack" },
                ...topSlotChildren,
              ),
            )
          : null,
        model.error
          ? createElement("div", {
              className: "steamloader-error",
              children: model.error,
            })
          : null,
        ...(Array.isArray(model.cards)
          ? model.cards.map((card, index) => createInfoCard(card, index))
          : []),
        model.audioDashboard ? createAudioDashboard(model.audioDashboard, topSlots.length) : null,
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
    if (Number.isInteger(index) && Array.isArray(state.renderedSlots)) {
      rememberCurrentRouteSlot(index, state.renderedSlots[index] || null);
    }

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

          stepVolumeSlider(-1);
        },
        onMoveRight: () => {
          if (!state.audio.sliderEditActive) {
            startVolumeSliderEditing();
          }

          stepVolumeSlider(1);
        },
      },
      actions: [
        {
          title: info?.isMuted ? "Unmute" : "Mute",
          icon: info?.isMuted ? AudioPluginIcon : AudioMuteIcon,
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
          icon: !elevatedHelperReady
            ? SettingsPluginIcon
            : installation?.running
              ? RestartActionIcon
              : LaunchActionIcon,
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
          stepVolumeSlider(-1);
        },
        onMoveRight: () => {
          stepVolumeSlider(1);
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
          stepCaptureVolumeSlider(-1);
        },
        onMoveRight: () => {
          stepCaptureVolumeSlider(1);
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
          slotKey: "performance-overlay-start-stop-primary",
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
          slotKey: "performance-overlay-start-stop-secondary",
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
          slotKey: "performance-auto-target-toggle",
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
          slotKey: "performance-refresh-state",
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

  function isSnapshotObject(snapshot) {
    return Boolean(snapshot && typeof snapshot === "object");
  }

  function setPerformanceSnapshot(snapshot, options = {}) {
    state.performance.snapshot = isSnapshotObject(snapshot) ? snapshot : null;
    if (options.clearError !== false) {
      state.performance.error = "";
    }
  }

  function setProcessesSnapshot(snapshot, options = {}) {
    state.processes.snapshot = isSnapshotObject(snapshot) ? snapshot : null;
    if (options.clearError !== false) {
      state.processes.error = "";
    }
  }

  function setAppStartSnapshot(snapshot, options = {}) {
    state.appStart.snapshot = isSnapshotObject(snapshot) ? snapshot : null;
    if (options.clearError !== false) {
      state.appStart.error = "";
    }
  }

  function setGeneralSettingsSnapshot(snapshot, options = {}) {
    state.generalSettings.snapshot = isSnapshotObject(snapshot) ? snapshot : null;
    if (options.clearError !== false) {
      state.generalSettings.error = "";
    }

    if (state.generalSettings.snapshot && options.syncDrafts !== false) {
      syncSplashDraftsFromSnapshot(options.forceDraftSync === true);
    }

    if (
      state.generalSettings.snapshot &&
      state.route.pluginId === "handheld-performance" &&
      !state.generalSettings.snapshot.handheldPerformanceAvailable
    ) {
      state.handheldPerformance.snapshot = null;
      requestFocusForRoute(parseRoute("root"), 0);
      state.route = parseRoute("root");
    }
  }

  function setUpdateSnapshot(snapshot, options = {}) {
    state.updates.snapshot = isSnapshotObject(snapshot) ? snapshot : null;
    if (options.clearError !== false) {
      state.updates.error = "";
    }
  }

  function setStoreSyncSnapshot(snapshot, options = {}) {
    state.storeSync.snapshot = isSnapshotObject(snapshot) ? snapshot : null;
    if (options.clearError !== false) {
      state.storeSync.error = "";
    }

    if (!state.storeSync.snapshot) {
      return;
    }

    const preserveDrafts = options.preserveDrafts === true;
    const forceDraftSync = options.forceDraftSync === true;

    if (
      isCustomLocationsRoute(state.route) &&
      (forceDraftSync || (!preserveDrafts && !hasRouteTextInputFocus()) || !state.storeSync.customPathDraft)
    ) {
      syncCustomPathDraftFromSnapshot(forceDraftSync || !preserveDrafts);
    }

    const activeTitleId = getStoreSyncTitleRouteId();
    if (activeTitleId && !preserveDrafts) {
      clearStoreSyncArtworkPreview(activeTitleId);
      syncStoreSyncTitleDraftsFromSnapshot(activeTitleId, true);
    }

  }

  function setAudioDashboardSnapshot(snapshot, options = {}) {
    const nextSnapshot = isSnapshotObject(snapshot) ? snapshot : null;
    state.audio.volumeInfo = nextSnapshot?.playbackVolume || null;
    state.audio.captureVolumeInfo = nextSnapshot?.captureVolume || null;
    state.audio.devices = Array.isArray(nextSnapshot?.playbackDevices) ? nextSnapshot.playbackDevices : [];
    state.audio.captureDevices = Array.isArray(nextSnapshot?.captureDevices) ? nextSnapshot.captureDevices : [];
    state.audio.mixerSessions = sortAudioMixerSessions(
      Array.isArray(nextSnapshot?.mixerSessions) ? nextSnapshot.mixerSessions : [],
    );

    if (options.clearErrors !== false) {
      state.audio.volumeError = "";
      state.audio.captureVolumeError = "";
      state.audio.mixerError = "";
      state.audio.error = "";
      state.audio.dashboardError = "";
    }
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

  function getSmartHomeSnapshot() {
    return state.smartHome.snapshot;
  }

  function getSmartHomeSettings() {
    return getSmartHomeSnapshot()?.settings || null;
  }

  function getSmartHomeOverview() {
    return getSmartHomeSnapshot()?.overview || null;
  }

  function getSmartHomeZones() {
    const zones = getSmartHomeSnapshot()?.zones;
    return Array.isArray(zones) ? zones : [];
  }

  function getSmartHomeFlows() {
    const flows = getSmartHomeSnapshot()?.flows;
    return Array.isArray(flows) ? flows : [];
  }

  function getSmartHomeMoods() {
    const moods = getSmartHomeSnapshot()?.moods;
    return Array.isArray(moods) ? moods : [];
  }

  function getSmartHomeUnassignedDevices() {
    const devices = getSmartHomeSnapshot()?.unassignedDevices;
    return Array.isArray(devices) ? devices : [];
  }

  function getSmartHomeZone(zoneId) {
    if (!zoneId) {
      return null;
    }

    return getSmartHomeZones().find((zone) => zone.id === zoneId) || null;
  }

  function getSmartHomeZoneMoods(zoneId) {
    if (!zoneId) {
      return [];
    }

    return getSmartHomeMoods().filter((mood) => mood.zoneId === zoneId);
  }

  function getSmartHomeRoomIndex(roomId) {
    if (!roomId) {
      return null;
    }

    if (roomId === "unassigned") {
      return getSmartHomeZones().length;
    }

    const index = getSmartHomeZones().findIndex((zone) => zone.id === roomId);
    return index >= 0 ? index : null;
  }

  function getSmartHomeDevice(deviceId) {
    if (!deviceId) {
      return null;
    }

    for (const zone of getSmartHomeZones()) {
      const device = Array.isArray(zone.devices)
        ? zone.devices.find((entry) => entry.id === deviceId)
        : null;
      if (device) {
        return device;
      }
    }

    return getSmartHomeUnassignedDevices().find((device) => device.id === deviceId) || null;
  }

  function getSmartHomeRoomRouteId(route = state.route) {
    return route?.pluginId === "smart-home" &&
      typeof route?.pageId === "string" &&
      route.pageId.startsWith("room-")
      ? route.pageId.replace(/^room-/, "")
      : "";
  }

  function isSmartHomeBusy() {
    return state.smartHome.loading || state.smartHome.saving;
  }

  function getSmartHomeErrorText() {
    return state.smartHome.error || getSmartHomeSnapshot()?.errorText || "";
  }

  function syncSmartHomeDraftsFromSnapshot(force = false) {
    const homey = getSmartHomeSettings()?.homey;
    if (!homey) {
      return;
    }

    const nextBaseUrl = homey.baseUrl || "";
    if (force || !state.smartHome.baseUrlDraft) {
      if (state.smartHome.baseUrlDraft !== nextBaseUrl) {
        state.smartHome.baseUrlDraft = nextBaseUrl;
        state.smartHome.baseUrlInputVersion += 1;
      }
    }

    const nextHomeyId = homey.homeyId || "";
    if (force || !state.smartHome.homeyIdDraft) {
      if (state.smartHome.homeyIdDraft !== nextHomeyId) {
        state.smartHome.homeyIdDraft = nextHomeyId;
        state.smartHome.homeyIdInputVersion += 1;
      }
    }

    if ((force || !state.smartHome.sessionTokenDraft) && state.smartHome.sessionTokenDraft) {
      state.smartHome.sessionTokenDraft = "";
      state.smartHome.sessionTokenInputVersion += 1;
    }
  }

  function setSmartHomeSnapshot(snapshot, options = {}) {
    state.smartHome.snapshot = isSnapshotObject(snapshot) ? snapshot : null;
    if (options.clearError !== false) {
      state.smartHome.error = "";
    }

    if (state.smartHome.snapshot && options.syncDrafts !== false) {
      syncSmartHomeDraftsFromSnapshot(options.forceDraftSync === true);
    }
  }

  function getSmartHomeSliderCommitKey(deviceId, capabilityId) {
    return `${deviceId}::${capabilityId}`;
  }

  function clearSmartHomeSliderCommitTimer(commitKey) {
    const timerHandle = state.smartHome.sliderCommitTimersByKey[commitKey];
    if (!timerHandle) {
      return;
    }

    window.clearTimeout(timerHandle);
    delete state.smartHome.sliderCommitTimersByKey[commitKey];
  }

  function clearAllSmartHomeSliderCommitTimers() {
    Object.keys(state.smartHome.sliderCommitTimersByKey).forEach((commitKey) =>
      clearSmartHomeSliderCommitTimer(commitKey),
    );
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

  function getUnifySteamSnapshot() {
    return getStoreSyncSnapshot()?.unifySteam || null;
  }

  function getUnifySteamStores() {
    const stores = getUnifySteamSnapshot()?.stores;
    return Array.isArray(stores) ? stores : [];
  }

  function getUnifySteamStore(storeId) {
    return getUnifySteamStores().find((store) => store.id === storeId) || null;
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

  function buildUnifySteamOverviewCard(snapshot = getUnifySteamSnapshot()) {
    const stores = Array.isArray(snapshot?.stores) ? snapshot.stores : [];
    const installedCount = stores.reduce((total, store) => total + (Number(store.installedCount) || 0), 0);
    const libraryCount = stores.reduce((total, store) => total + (Number(store.availableCount) || 0), 0);

    return {
      title: "Storefront",
      lines: [
        `${installedCount} installed / ${libraryCount} in library`,
        snapshot?.statusText || "Waiting for setup.",
        snapshot?.detailText || "Sign in and refresh Epic or GOG to build the cached library.",
      ].filter(Boolean),
    };
  }

  function buildUnifySteamStoreCopy(store) {
    if (!store) {
      return "Storefront store unavailable.";
    }

    return [
      `${store.installedCount || 0} installed / ${store.availableCount || 0} total`,
      store.accountName ? `Signed in as ${store.accountName}` : store.statusText || "",
      store.detailText || "",
    ]
      .filter(Boolean)
      .join(" - ");
  }

  function buildUnifySteamGameCopy(game) {
    if (!game) {
      return "Library item unavailable.";
    }

    return [
      game.statusText || "",
      game.version ? `Version ${game.version}` : "",
      game.detailText || "",
    ]
      .filter(Boolean)
      .join(" - ");
  }

  function buildUnifySteamGameBadge(game) {
    if (!game) {
      return "";
    }

    if (game.syncedToSteam) {
      return "Synced";
    }

    if (game.installed) {
      return "Installed";
    }

    return "Available";
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

  function createSectionSlot(title, copy, slotKey, showDivider = false) {
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

  function createStoreSyncSectionSlot(title, copy, slotKey, showDivider = false) {
    return createSectionSlot(title, copy, slotKey, showDivider);
  }

  function createThemeStorePagerSlots({
    currentPage = 1,
    totalPages = 1,
    disabled = false,
    onPrevious = null,
    onNext = null,
  } = {}) {
    if (totalPages <= 1) {
      return [];
    }

    const slotKey = "theme-store-pager-top";
    const canGoPrevious = !disabled && currentPage > 1 && typeof onPrevious === "function";
    const canGoNext = !disabled && currentPage < totalPages && typeof onNext === "function";
    const createInlineStepperSlot = window.STFrontendLib?.createInlineStepperSlot
      || ((title, copy, onMoveLeft, onMoveRight, options = {}) => {
        const leftDisabled = Boolean(options.leftDisabled);
        const rightDisabled = Boolean(options.rightDisabled);
        const externalButtonProps = options.buttonProps || {};

        return {
          kind: "button",
          role: "command",
          title,
          copy: copy || "",
          onClick: options.onClick || onMoveRight || onMoveLeft || (() => {}),
          disabled: Boolean(options.disabled),
          badge: "",
          trailing: "none",
          switchValue: undefined,
          switchLabel: "",
          leadingIcon: null,
          buttonClassName:
            options.buttonClassName || "steamloader-dialog-button steamloader-dialog-button-inline-stepper",
          buttonStyle: options.buttonStyle || null,
          buttonProps: {
            ...externalButtonProps,
            onMoveLeft: (event) => {
              externalButtonProps.onMoveLeft?.(event);
              if (!leftDisabled) {
                onMoveLeft?.(event);
              }
              return true;
            },
            onMoveRight: (event) => {
              externalButtonProps.onMoveRight?.(event);
              if (!rightDisabled) {
                onMoveRight?.(event);
              }
              return true;
            },
          },
          rowClassName: options.rowClassName || "",
          slotKey: options.slotKey || options.key || "",
          selected: Boolean(options.selected),
          value: options.value,
          layout: "stepper",
          expanded: Boolean(options.expanded),
          eyebrow: options.eyebrow || "",
          meta: Array.isArray(options.meta) ? options.meta.filter(Boolean) : [],
          mediaImageSrc: options.mediaImageSrc || "",
          mediaImageAlt: options.mediaImageAlt || "",
          footerLabel: options.footerLabel || "",
          stepperLeftDisabled: leftDisabled,
          stepperRightDisabled: rightDisabled,
        };
      });

    return [
      createInlineStepperSlot(
        `${currentPage} / ${totalPages}`,
        "",
        () => {
          onPrevious?.();
        },
        () => {
          onNext?.();
        },
        {
          slotKey,
          disabled: Boolean(disabled),
          leftDisabled: !canGoPrevious,
          rightDisabled: !canGoNext,
          onClick: canGoNext ? onNext : canGoPrevious ? onPrevious : (() => {}),
          buttonProps: {
            "aria-label": `Theme Store page ${currentPage} of ${totalPages}. Use left and right to change pages.`,
            title: `Theme Store page ${currentPage} of ${totalPages}`,
          },
        },
      ),
    ];
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

  function getThemeIntegration() {
    return getThemesSnapshot()?.integration || null;
  }

  function getThemeById(themeId) {
    if (!themeId) {
      return null;
    }

    return getThemesSnapshot()?.installedThemes?.find((theme) => theme.id === themeId) || null;
  }

  function getThemeSliderChoiceFromSnapshot(snapshot, themeId, optionId) {
    const theme = snapshot?.installedThemes?.find((entry) => entry.id === themeId);
    const option = theme?.options?.find((entry) => entry.id === optionId);
    return option?.selectedChoiceId;
  }

  function applyThemesSnapshotIfCurrent(snapshot) {
    const optimisticEntries = getOptimisticDesiredEntries("themes.slider.");
    if (!optimisticEntries.length) {
      state.themes.snapshot = snapshot && typeof snapshot === "object" ? snapshot : null;
      applyActiveThemeCss();
      return true;
    }

    const matchesAllDesiredValues = optimisticEntries.every(([key, desiredValue]) => {
      const routeKey = key.slice("themes.slider.".length);
      const separatorIndex = routeKey.indexOf("::");
      if (separatorIndex < 0) {
        return true;
      }

      const themeId = routeKey.slice(0, separatorIndex);
      const optionId = routeKey.slice(separatorIndex + 2);
      return Object.is(getThemeSliderChoiceFromSnapshot(snapshot, themeId, optionId), desiredValue);
    });

    if (!matchesAllDesiredValues) {
      return false;
    }

    state.themes.snapshot = snapshot && typeof snapshot === "object" ? snapshot : null;
    applyActiveThemeCss();
    optimisticEntries.forEach(([key, desiredValue]) => {
      clearOptimisticDesiredValue(key, desiredValue);
    });
    return true;
  }

  function getThemeProfilesState() {
    return getThemesSnapshot()?.profiles || null;
  }

  function getThemeStoreCatalog() {
    return state.themes.storeCatalog || null;
  }

  function getThemeStoreById(storeThemeId) {
    if (!storeThemeId) {
      return null;
    }

    return (
      state.themes.storeDetailById?.[storeThemeId] ||
      getThemeStoreCatalog()?.items?.find((theme) => theme.storeId === storeThemeId) ||
      null
    );
  }

  function getInstalledThemePreview(themeId) {
    if (!themeId) {
      return null;
    }

    return state.themes.installedPreviewByThemeId?.[themeId] || null;
  }

  function hasInstalledThemePreviewRecord(themeId) {
    if (!themeId) {
      return false;
    }

    return Object.prototype.hasOwnProperty.call(state.themes.installedPreviewByThemeId || {}, themeId);
  }

  function findInstalledThemePreview(theme) {
    if (!theme?.id) {
      return null;
    }

    const cachedPreview = getInstalledThemePreview(theme.id);
    if (cachedPreview?.imageSrc) {
      return cachedPreview;
    }

    const detailMatch = Object.values(state.themes.storeDetailById || {}).find(
      (entry) => entry?.themeId === theme.id,
    );
    if (detailMatch?.previewImageUrl || detailMatch?.previewThumbnailUrl) {
      return {
        imageSrc: detailMatch.previewImageUrl || detailMatch.previewThumbnailUrl || "",
        imageAlt: `${theme.title} preview`,
      };
    }

    const catalogMatch = getThemeStoreCatalog()?.items?.find((entry) => entry?.themeId === theme.id);
    if (catalogMatch?.previewImageUrl || catalogMatch?.previewThumbnailUrl) {
      return {
        imageSrc: catalogMatch.previewImageUrl || catalogMatch.previewThumbnailUrl || "",
        imageAlt: `${theme.title} preview`,
      };
    }

    return cachedPreview;
  }

  async function ensureInstalledThemePreview(theme) {
    if (!theme?.id) {
      return null;
    }

    const existingPreview = findInstalledThemePreview(theme);
    if (existingPreview?.imageSrc || hasInstalledThemePreviewRecord(theme.id)) {
      if (existingPreview) {
        state.themes.installedPreviewByThemeId[theme.id] = existingPreview;
      }
      return existingPreview;
    }

    state.themes.installedPreviewByThemeId ||= {};
    state.themes.installedPreviewLoadingByThemeId ||= {};
    if (state.themes.installedPreviewLoadingByThemeId[theme.id]) {
      return null;
    }

    state.themes.installedPreviewLoadingByThemeId[theme.id] = true;

    try {
      const query = new URLSearchParams({
        search: theme.id || theme.title || "",
        filter: "All",
        order: "Most Downloaded",
        page: "1",
        perPage: "8",
      });
      const response = await fetch(`${apiBase}api/themes/store?${query.toString()}`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Theme preview could not be loaded (${response.status}).`);
      }

      const items = Array.isArray(payload?.items) ? payload.items : [];
      const normalizedThemeId = String(theme.id || "").trim().toLowerCase();
      const normalizedThemeTitle = String(theme.title || "").trim().toLowerCase();
      const normalizedThemeAuthor = String(theme.author || "").trim().toLowerCase();
      const matchedTheme =
        items.find((entry) => String(entry?.themeId || "").trim().toLowerCase() === normalizedThemeId) ||
        items.find(
          (entry) =>
            String(entry?.title || "").trim().toLowerCase() === normalizedThemeTitle &&
            String(entry?.author || "").trim().toLowerCase() === normalizedThemeAuthor,
        ) ||
        items.find((entry) => String(entry?.title || "").trim().toLowerCase() === normalizedThemeTitle) ||
        null;

      state.themes.installedPreviewByThemeId[theme.id] = matchedTheme
        ? {
            imageSrc: matchedTheme.previewImageUrl || matchedTheme.previewThumbnailUrl || "",
            imageAlt: `${theme.title} preview`,
          }
        : {
            imageSrc: "",
            imageAlt: `${theme.title} preview`,
          };
    } catch {
      state.themes.installedPreviewByThemeId[theme.id] = {
        imageSrc: "",
        imageAlt: `${theme.title} preview`,
      };
    } finally {
      state.themes.installedPreviewLoadingByThemeId[theme.id] = false;
      rerenderThemesPanel();
    }

    return state.themes.installedPreviewByThemeId[theme.id];
  }

  function getThemeProfileById(profileId) {
    if (!profileId) {
      return null;
    }

    return getThemeProfilesState()?.installedProfiles?.find((profile) => profile.id === profileId) || null;
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

    if (option.type === "choice" || option.type === "slider") {
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
      `${theme.author} - ${theme.version}`,
      theme.storeDescription || theme.description,
      `${theme.sourceLabel} - ${theme.dependencyCount || 0} dependenc${theme.dependencyCount === 1 ? "y" : "ies"} - ${theme.advancedControlCount || 0} advanced control${theme.advancedControlCount === 1 ? "" : "s"}`,
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
      `${profile.author} - ${profile.version}`,
      profile.description,
      `${profile.sourceLabel} - ${profile.themes.length} theme${profile.themes.length === 1 ? "" : "s"}`,
      profile.statusText,
      `${profile.themes.length} theme${profile.themes.length === 1 ? "" : "s"} in this profile`,
    ];

    return {
      title: profile.title,
      lines,
    };
  }

  function buildThemeStoreSummaryCard(theme) {
    if (!theme) {
      return null;
    }

    const lines = [
      `${theme.author} - ${theme.version}`,
      theme.description,
      `${theme.target} - ${theme.downloadCount.toLocaleString()} download${theme.downloadCount === 1 ? "" : "s"} - ${theme.starCount.toLocaleString()} star${theme.starCount === 1 ? "" : "s"}`,
      `${theme.dependencyCount} dependenc${theme.dependencyCount === 1 ? "y" : "ies"} - ${theme.statusText}`,
    ];

    return {
      title: theme.title,
      imageSrc: theme.previewImageUrl || theme.previewThumbnailUrl || "",
      imageAlt: `${theme.title} preview`,
      lines,
    };
  }

  function buildThemeStoreIcon(imageSrc, imageAlt) {
    if (!imageSrc) {
      return ThemesPluginIcon;
    }

    return function ThemeStoreIcon() {
      return createElement("img", {
        className: "steamloader-theme-store-icon",
        src: imageSrc,
        alt: imageAlt || "",
      });
    };
  }

  function formatCompactThemeStoreCount(value) {
    const safeValue = Math.max(0, Number(value) || 0);
    if (safeValue >= 1000000) {
      return `${(safeValue / 1000000).toFixed(safeValue >= 10000000 ? 0 : 1)}M`;
    }

    if (safeValue >= 1000) {
      return `${(safeValue / 1000).toFixed(safeValue >= 100000 ? 0 : 1)}K`;
    }

    return safeValue.toLocaleString();
  }

  function getThemeStoreTargetSummary(theme) {
    const targets = Array.isArray(theme?.targets) ? theme.targets.filter(Boolean) : [];
    if (targets.length > 0) {
      return targets.slice(0, 2).join(" + ");
    }

    return theme?.target || "Big Picture";
  }

  function buildThemeStoreMetaItems(theme) {
    if (!theme) {
      return [];
    }

    return [
      theme.author,
      getThemeStoreTargetSummary(theme),
      `${formatCompactThemeStoreCount(theme.downloadCount)} downloads`,
      `${formatCompactThemeStoreCount(theme.starCount)} stars`,
    ].filter(Boolean);
  }

  function buildThemeStoreFilterSummary(currentFilter, currentOrder, searchDraft) {
    const summaryParts = [currentFilter || "All", currentOrder || "Most Downloaded"];
    const normalizedSearch = typeof searchDraft === "string" ? searchDraft.trim() : "";
    summaryParts.push(normalizedSearch ? `Search: ${normalizedSearch}` : "No search");
    return summaryParts.join(" • ");
  }

  function createThemeSliderSlot(theme, option) {
    const choices = Array.isArray(option?.choices) ? option.choices : [];
    const currentChoiceId = option?.selectedChoiceId || choices[0]?.id || "";
    const currentIndex = Math.max(0, choices.findIndex((choice) => choice.id === currentChoiceId));
    const advancedHint =
      option?.advancedControlCount > 0
        ? ` ${option.advancedControlCount} advanced control${option.advancedControlCount === 1 ? "" : "s"} are not exposed in Quick Access yet.`
        : "";

    return createPerformanceValueSliderSlot({
      title: option.title,
      copy: option.description,
      hint: `Use Left / Right to adjust this patch. Press A to reset it to the default value.${advancedHint}`,
      slotKey: `theme-slider-${theme.id}-${option.id}`,
      min: 0,
      max: Math.max(0, choices.length - 1),
      step: 1,
      disabled: state.themes.loading || state.themes.saving || !theme.installed || choices.length <= 1,
      getValue: () => {
        const liveOption = getThemeOptionById(theme.id, option.id) || option;
        const liveChoiceId = liveOption?.selectedChoiceId || choices[0]?.id || "";
        const liveIndex = choices.findIndex((choice) => choice.id === liveChoiceId);
        return liveIndex >= 0 ? liveIndex : currentIndex;
      },
      displayValue: (index) => {
        const safeIndex = Math.max(0, Math.min(choices.length - 1, index));
        return choices[safeIndex]?.title || currentChoiceId;
      },
      onAdjust: (direction) => {
        adjustThemeRange(theme.id, option.id, direction);
      },
      onClick: () => {
        resetThemeRange(theme.id, option.id);
      },
    });
  }

  function resolveThemesStatusText() {
    if (state.themes.saving) {
      return state.themes.operationText || "Saving CSSLoader changes...";
    }

    if (state.themes.storeLoading) {
      return "Loading CSSLoader Store...";
    }

    if (state.themes.loading) {
      return "Loading CSSLoader state...";
    }

    return getThemesSnapshot()?.statusText || "";
  }

  function buildOptimisticThemesStatusText(snapshot) {
    const installedThemes = Array.isArray(snapshot?.installedThemes) ? snapshot.installedThemes : [];
    const activeCount = installedThemes.filter((theme) => theme.enabled).length;
    return activeCount > 0
      ? `${installedThemes.length} installed theme${installedThemes.length === 1 ? "" : "s"} - ${activeCount} active.`
      : `${installedThemes.length} installed theme${installedThemes.length === 1 ? "" : "s"} - none active.`;
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

      setStoreSyncSnapshot(payload, {
        preserveDrafts,
        forceDraftSync: !preserveDrafts,
      });
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

  async function loadGeneralSettingsState(options = {}) {
    const showLoading = options.showLoading !== false;
    state.generalSettings.loading = true;
    state.generalSettings.error = "";
    if (showLoading) {
      rerenderGeneralSettingsPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/settings/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Tools for Steam settings could not be loaded (${response.status}).`);
      }

      setGeneralSettingsSnapshot(payload, { forceDraftSync: true });
    } catch (error) {
      state.generalSettings.error = error instanceof Error ? error.message : String(error);
      state.generalSettings.snapshot = null;
    } finally {
      state.generalSettings.loading = false;
      rerenderGeneralSettingsPanel();
    }
  }

  function buildCommunityScriptUrl(plugin) {
    const scriptUrl = String(plugin?.scriptUrl || "").replace(/^\/+/, "");
    if (!scriptUrl) {
      return "";
    }

    const separator = scriptUrl.includes("?") ? "&" : "?";
    return `${apiBase}${scriptUrl}${separator}v=${encodeURIComponent(plugin.version || plugin.installedVersion || Date.now())}`;
  }

  function loadCommunityPluginScript(plugin) {
    const pluginId = String(plugin?.id || "").trim();
    const version = String(plugin?.version || "").trim();
    const scriptUrl = buildCommunityScriptUrl(plugin);
    if (!pluginId || !scriptUrl) {
      return Promise.resolve(false);
    }

    window.ToolsForSteamCommunityPlugins ??= {};

    if (
      state.communityPlugins.scriptVersionsById[pluginId] === version &&
      window.ToolsForSteamCommunityPlugins[pluginId]
    ) {
      return Promise.resolve(true);
    }

    if (state.communityPlugins.scriptPromisesById[pluginId]) {
      return state.communityPlugins.scriptPromisesById[pluginId];
    }

    const scriptId = `steamloader-community-plugin-script-${pluginId}`;
    document.getElementById(scriptId)?.remove();
    state.communityPlugins.sdkById ??= {};
    window.ToolsForSteamCommunityPlugins?.[pluginId]?.dispose?.();
    state.communityPlugins.sdkById[pluginId]?.dispose?.();
    delete state.communityPlugins.sdkById[pluginId];
    delete window.ToolsForSteamCommunityPlugins[pluginId];

    const promise = new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.id = scriptId;
      script.src = typeof window.__steamLoaderApiUrl === "function"
        ? window.__steamLoaderApiUrl(scriptUrl)
        : scriptUrl;
      script.async = false;
      script.onload = () => {
        state.communityPlugins.scriptVersionsById[pluginId] = version;
        delete state.communityPlugins.scriptErrorsById[pluginId];
        delete state.communityPlugins.scriptPromisesById[pluginId];
        resolve(true);
      };
      script.onerror = () => {
        const message = `${plugin?.title || pluginId} could not be loaded.`;
        state.communityPlugins.scriptErrorsById[pluginId] = message;
        delete state.communityPlugins.scriptPromisesById[pluginId];
        reject(new Error(message));
      };
      document.head.append(script);
    });

    state.communityPlugins.scriptPromisesById[pluginId] = promise;
    return promise;
  }

  async function loadCommunityPluginScripts(pluginsSnapshot) {
    const loadResults = await Promise.allSettled(
      (Array.isArray(pluginsSnapshot) ? pluginsSnapshot : []).map((plugin) =>
        loadCommunityPluginScript(plugin),
      ),
    );

    const failed = loadResults.filter((result) => result.status === "rejected");
    if (failed.length) {
      state.communityPlugins.error = `${failed.length} community plugin${failed.length === 1 ? "" : "s"} could not be loaded.`;
    }
  }

  async function loadCommunityPluginsState(options = {}) {
    if (state.communityPlugins.loading) {
      return;
    }

    const showLoading = options.showLoading !== false;
    state.communityPlugins.loading = true;
    state.communityPlugins.error = "";
    if (showLoading) {
      rerenderHomePanel();
    }

    try {
      const response = await fetch(`${apiBase}api/plugin-store/community/installed`, {
        cache: "no-store",
      });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Community plugins could not be loaded (${response.status}).`);
      }

      state.communityPlugins.snapshot = payload && typeof payload === "object"
        ? payload
        : { plugins: [] };
      state.communityPlugins.sdkById ??= {};
      const installedPluginIds = new Set(
        state.communityPlugins.snapshot.plugins.map((plugin) => String(plugin?.id || "")).filter(Boolean),
      );
      for (const pluginId of Object.keys(state.communityPlugins.sdkById)) {
        if (!installedPluginIds.has(pluginId)) {
          window.ToolsForSteamCommunityPlugins?.[pluginId]?.dispose?.();
          state.communityPlugins.sdkById[pluginId]?.dispose?.();
          delete state.communityPlugins.sdkById[pluginId];
          document.getElementById(`steamloader-community-plugin-script-${pluginId}`)?.remove();
          delete window.ToolsForSteamCommunityPlugins?.[pluginId];
        }
      }
      await loadCommunityPluginScripts(state.communityPlugins.snapshot.plugins);
    } catch (error) {
      state.communityPlugins.error = error instanceof Error ? error.message : String(error);
      state.communityPlugins.snapshot = { plugins: [] };
    } finally {
      state.communityPlugins.loading = false;
      rerenderHomePanel();
    }
  }

  function getCommunityPluginSdk(plugin, registryEntry, runtime) {
    const pluginId = String(plugin?.id || "").trim();
    if (!pluginId) {
      return null;
    }

    state.communityPlugins.sdkById ??= {};
    const registeredSdk = registryEntry?.sdk;
    if (registeredSdk && !registeredSdk.lifecycle?.disposed) {
      state.communityPlugins.sdkById[pluginId] = registeredSdk;
      return registeredSdk;
    }

    const existingSdk = state.communityPlugins.sdkById[pluginId];
    if (existingSdk && !existingSdk.lifecycle?.disposed) {
      return existingSdk;
    }

    const sdk = window.TfsPluginSdk?.create?.(registryEntry?.manifest || runtime || {}, { pluginId }) || null;
    if (sdk) {
      state.communityPlugins.sdkById[pluginId] = sdk;
    }
    return sdk;
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

      setUpdateSnapshot(payload);
    } catch (error) {
      state.updates.error = error instanceof Error ? error.message : String(error);
      if (force) {
        state.updates.snapshot = null;
      }
    } finally {
      state.updates.loading = false;
      updateUpdatesPolling();
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

  async function loadSmartHomeState(options = {}) {
    const showLoading = options.showLoading !== false;
    state.smartHome.loading = true;
    state.smartHome.error = "";
    if (showLoading) {
      rerenderSmartHomePanel();
    }

    try {
      const path = options.force === true ? "api/smart-home/refresh" : "api/smart-home/state";
      const response = await fetch(`${apiBase}${path}`, {
        method: options.force === true ? "POST" : "GET",
        headers: options.force === true
          ? {
              "Content-Type": "application/json",
            }
          : undefined,
        body: options.force === true ? "{}" : undefined,
        cache: "no-store",
      });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Homey could not be loaded (${response.status}).`);
      }

      setSmartHomeSnapshot(payload, {
        forceDraftSync: !hasRouteTextInputFocus(),
      });
    } catch (error) {
      state.smartHome.error = error instanceof Error ? error.message : String(error);
      if (showLoading) {
        state.smartHome.snapshot = null;
      }
    } finally {
      state.smartHome.loading = false;
      rerenderSmartHomePanel();
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

  async function loadPerformanceState(options = {}) {
    const showLoading = options.showLoading !== false;
    state.performance.loading = true;
    state.performance.error = "";
    if (showLoading) {
      rerenderPerformancePanel();
    }

    try {
      const response = await fetch(`${apiBase}api/performance/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Performance could not be loaded (${response.status}).`);
      }

      applyPerformanceSnapshotIfCurrent(payload);
    } catch (error) {
      state.performance.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.performance.loading = false;
      if (showLoading) {
        rerenderPerformancePanel();
      } else {
        refreshPerformancePanel();
      }
    }
  }

  async function loadHandheldPerformanceState() {
    state.handheldPerformance.loading = true;
    state.handheldPerformance.error = "";
    if (isCurrentPluginRoute("handheld-performance")) {
      renderPanelDataRefresh();
    }

    try {
      const response = await fetch(`${apiBase}api/handheld-performance/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Handheld performance could not be loaded (${response.status}).`);
      }
      state.handheldPerformance.snapshot = payload && typeof payload === "object" ? payload : null;
    } catch (error) {
      state.handheldPerformance.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.handheldPerformance.loading = false;
      if (isCurrentPluginRoute("handheld-performance")) {
        renderPanelDataRefresh();
      }
    }
  }

  async function sendHandheldPerformanceRequest(path, body, options = {}) {
    if (state.handheldPerformance.saving) {
      return;
    }
    state.handheldPerformance.saving = true;
    state.handheldPerformance.error = "";
    if (options.silent !== true && isCurrentPluginRoute("handheld-performance")) {
      renderPanelDataRefresh();
    }
    try {
      const response = await fetch(`${apiBase}${path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The TDP request failed (${response.status}).`);
      }
      state.handheldPerformance.snapshot = payload;
    } catch (error) {
      state.handheldPerformance.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.handheldPerformance.saving = false;
      if (options.silent === true && isCurrentPluginRoute("handheld-performance")) {
        syncVisibleSlotSliderUi();
      } else if (isCurrentPluginRoute("handheld-performance")) {
        renderPanelDataRefresh();
      }
    }
  }

  function previewHandheldTdp(watts) {
    const snapshot = state.handheldPerformance.snapshot;
    if (!snapshot) {
      return false;
    }

    const modes = Array.isArray(snapshot.modes) ? snapshot.modes : [];
    const matchingMode = modes.find((mode) => Number(mode.watts) === watts);
    state.handheldPerformance.snapshot = {
      ...snapshot,
      selectedTdpWatts: watts,
      selectedModeId: matchingMode?.id || "custom",
    };
    return true;
  }

  function queueHandheldTdpCommit(watts) {
    if (state.handheldPerformance.tdpCommitTimer) {
      window.clearTimeout(state.handheldPerformance.tdpCommitTimer);
    }

    const sequence = ++state.handheldPerformance.tdpMutationSequence;
    const commit = async () => {
      state.handheldPerformance.tdpCommitTimer = 0;
      if (state.handheldPerformance.saving) {
        state.handheldPerformance.tdpCommitTimer = window.setTimeout(commit, 100);
        return;
      }
      await sendHandheldPerformanceRequest(
        "api/handheld-performance/tdp",
        { watts },
        { silent: true },
      );
      if (sequence !== state.handheldPerformance.tdpMutationSequence) {
        return;
      }
      syncVisibleSlotSliderUi();
    };
    state.handheldPerformance.tdpCommitTimer = window.setTimeout(commit, 250);
  }

  function stepHandheldTdp(direction) {
    const snapshot = state.handheldPerformance.snapshot;
    if (!snapshot || !direction) {
      return;
    }

    const minimumWatts = Number(snapshot.minimumTdpWatts || 0);
    const maximumWatts = Number(snapshot.maximumTdpWatts || 0);
    const currentWatts = Number(snapshot.selectedTdpWatts || minimumWatts);
    const nextWatts = Math.max(minimumWatts, Math.min(maximumWatts, currentWatts + direction));
    if (nextWatts === currentWatts) {
      return;
    }

    playSliderMoveSound(direction);
    if (previewHandheldTdp(nextWatts)) {
      syncVisibleSlotSliderUi();
    }
    queueHandheldTdpCommit(nextWatts);
  }

  function previewHandheldGlobalTdp(powerSource, watts) {
    const snapshot = state.handheldPerformance.snapshot;
    if (!snapshot) {
      return false;
    }

    const sourceField = powerSource === "battery" ? "globalBatteryTdpWatts" : "globalAcTdpWatts";
    const isCurrentSource = snapshot.powerSource === powerSource;
    state.handheldPerformance.snapshot = {
      ...snapshot,
      [sourceField]: watts,
      ...(isCurrentSource ? { globalTdpWatts: watts } : {}),
      ...(isCurrentSource && !snapshot.currentGame ? { selectedTdpWatts: watts } : {}),
    };
    return true;
  }

  function queueHandheldGlobalTdpCommit(powerSource, watts) {
    const timers = state.handheldPerformance.globalTdpCommitTimers;
    const sequences = state.handheldPerformance.globalTdpMutationSequences;
    if (timers[powerSource]) {
      window.clearTimeout(timers[powerSource]);
    }

    const sequence = (sequences[powerSource] || 0) + 1;
    sequences[powerSource] = sequence;
    const commit = async () => {
      timers[powerSource] = 0;
      if (state.handheldPerformance.saving) {
        timers[powerSource] = window.setTimeout(commit, 100);
        return;
      }
      await sendHandheldPerformanceRequest(
        "api/handheld-performance/profiles/global",
        { watts, powerSource },
        { silent: true },
      );
      if (sequence !== sequences[powerSource]) {
        return;
      }
      syncVisibleSlotSliderUi();
    };
    timers[powerSource] = window.setTimeout(commit, 250);
  }

  function stepHandheldGlobalTdp(powerSource, direction) {
    const snapshot = state.handheldPerformance.snapshot;
    if (!snapshot || !direction) {
      return;
    }

    const minimumWatts = Number(snapshot.minimumTdpWatts || 0);
    const maximumWatts = Number(snapshot.maximumTdpWatts || 0);
    const sourceField = powerSource === "battery" ? "globalBatteryTdpWatts" : "globalAcTdpWatts";
    const currentWatts = Number(snapshot[sourceField] || snapshot.globalTdpWatts || minimumWatts);
    const nextWatts = Math.max(minimumWatts, Math.min(maximumWatts, currentWatts + direction));
    if (nextWatts === currentWatts) {
      return;
    }

    playSliderMoveSound(direction);
    if (previewHandheldGlobalTdp(powerSource, nextWatts)) {
      syncVisibleSlotSliderUi();
    }
    queueHandheldGlobalTdpCommit(powerSource, nextWatts);
  }

  function getHandheldProfileTdp(profile, powerSource) {
    if (!profile) {
      return 0;
    }
    const sourceValue = powerSource === "battery" ? profile.batteryTdpWatts : profile.acTdpWatts;
    return Number(sourceValue ?? profile.tdpWatts ?? 0);
  }

  function previewHandheldGameProfileTdp(key, powerSource, watts) {
    const snapshot = state.handheldPerformance.snapshot;
    if (!snapshot) {
      return false;
    }

    const updateProfile = (profile) => ({
      ...profile,
      tdpWatts: watts,
      acTdpWatts: powerSource === "ac" ? watts : getHandheldProfileTdp(profile, "ac"),
      batteryTdpWatts: powerSource === "battery" ? watts : getHandheldProfileTdp(profile, "battery"),
    });
    const profiles = (Array.isArray(snapshot.profiles) ? snapshot.profiles : []).map((profile) =>
      profile.key === key ? updateProfile(profile) : profile,
    );
    const activeProfile = snapshot.activeProfile?.key === key
      ? updateProfile(snapshot.activeProfile)
      : snapshot.activeProfile;
    const isActiveSource = snapshot.currentGame?.key === key && snapshot.powerSource === powerSource;
    state.handheldPerformance.snapshot = {
      ...snapshot,
      profiles,
      activeProfile,
      ...(isActiveSource ? { selectedTdpWatts: watts } : {}),
    };
    return true;
  }

  function queueHandheldGameProfileTdpCommit(key, powerSource, watts) {
    const timerKey = `${key}:${powerSource}`;
    const timers = state.handheldPerformance.profileTdpCommitTimers;
    const sequences = state.handheldPerformance.profileTdpMutationSequences;
    if (timers[timerKey]) {
      window.clearTimeout(timers[timerKey]);
    }

    const sequence = (sequences[timerKey] || 0) + 1;
    sequences[timerKey] = sequence;
    const commit = async () => {
      timers[timerKey] = 0;
      if (state.handheldPerformance.saving) {
        timers[timerKey] = window.setTimeout(commit, 100);
        return;
      }
      await sendHandheldPerformanceRequest(
        "api/handheld-performance/profiles/game",
        { key, watts, powerSource },
        { silent: true },
      );
      if (sequence === sequences[timerKey]) {
        syncVisibleSlotSliderUi();
      }
    };
    timers[timerKey] = window.setTimeout(commit, 250);
  }

  function stepHandheldGameProfileTdp(key, powerSource, direction) {
    const snapshot = state.handheldPerformance.snapshot;
    const profile = snapshot?.profiles?.find((candidate) => candidate.key === key);
    if (!snapshot || !profile || !direction) {
      return;
    }

    const minimumWatts = Number(snapshot.minimumTdpWatts || 0);
    const maximumWatts = Number(snapshot.maximumTdpWatts || 0);
    const currentWatts = getHandheldProfileTdp(profile, powerSource);
    const nextWatts = Math.max(minimumWatts, Math.min(maximumWatts, currentWatts + direction));
    if (nextWatts === currentWatts) {
      return;
    }

    playSliderMoveSound(direction);
    if (previewHandheldGameProfileTdp(key, powerSource, nextWatts)) {
      syncVisibleSlotSliderUi();
    }
    queueHandheldGameProfileTdpCommit(key, powerSource, nextWatts);
  }

  function refreshHandheldPerformanceLiveUi() {
    const snapshot = state.handheldPerformance.snapshot;
    if (!snapshot || !isCurrentPluginRoute("handheld-performance")) {
      return false;
    }

    const telemetry = snapshot.telemetry || {};
    const liveValues = {
      "handheld-power-source": snapshot.powerSource === "battery" ? "Battery power" : "Plugged in",
      "handheld-battery-level": Number(telemetry.batteryPercent) >= 0
        ? `${telemetry.batteryPercent}% battery${Number(telemetry.estimatedMinutesRemaining) > 0
          ? ` - about ${Math.floor(telemetry.estimatedMinutesRemaining / 60)}h ${telemetry.estimatedMinutesRemaining % 60}m left`
          : ""}`
        : "Battery level unavailable",
      "handheld-applied-tdp": telemetry.appliedTdpConfirmed
        ? `${telemetry.appliedTdpWatts} W applied`
        : `${snapshot.selectedTdpWatts} W requested`,
    };

    Object.entries(liveValues).forEach(([key, value]) => {
      const node = document.querySelector(`[data-live-value="${key}"]`);
      if (node && node.textContent !== value) {
        node.textContent = value;
      }
    });
    syncVisibleSlotSliderUi();
    return true;
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

  async function loadProcessesState(options = {}) {
    if (state.processes.loading) {
      return;
    }

    const showLoading = options.showLoading !== false;
    state.processes.loading = true;
    state.processes.error = "";
    if (showLoading) {
      rerenderProcessesPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/processes/windows`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Processes could not be loaded (${response.status}).`);
      }

      setProcessesSnapshot(payload);
    } catch (error) {
      state.processes.error = error instanceof Error ? error.message : String(error);
      state.processes.snapshot = null;
    } finally {
      state.processes.loading = false;
      rerenderProcessesPanel();
    }
  }

  async function loadAppStartState(options = {}) {
    const showLoading = options.showLoading !== false;
    state.appStart.loading = true;
    state.appStart.error = "";
    if (showLoading) {
      rerenderAppStartPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/app-start/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `App Start could not be loaded (${response.status}).`);
      }

      setAppStartSnapshot(payload);
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

  async function loadThemesState(options = {}) {
    const showLoading = options.showLoading !== false;
    state.themes.loading = true;
    state.themes.error = "";
    if (showLoading) {
      rerenderThemesPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/themes/state`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `CSSLoader state could not be loaded (${response.status}).`);
      }

      applyThemesSnapshotIfCurrent(payload);
    } catch (error) {
      state.themes.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.themes.loading = false;
      if (showLoading) {
        rerenderThemesPanel();
      } else if (state.panelVisible && state.route?.pluginId === "themes") {
        if (state.themes.error || !syncVisibleSlotSliderUi()) {
          rerenderThemesPanel();
        }
      } else {
        rerenderThemesPanel();
      }
    }
  }

  async function loadThemesStoreCatalog(options = {}) {
    const showLoading = options.showLoading !== false;
    const requestId = state.themes.storeCatalogRequestSequence + 1;
    state.themes.storeCatalogRequestSequence = requestId;
    const currentCatalog = getThemeStoreCatalog();
    const search =
      options.search !== undefined
        ? String(options.search || "").trim()
        : currentCatalog?.search || "";
    const filter = options.filter || currentCatalog?.filter || "All";
    const order = options.order || currentCatalog?.order || "Most Downloaded";
    const page = Number.isFinite(options.page) ? Math.max(1, options.page) : currentCatalog?.page || 1;
    const perPage = Number.isFinite(options.perPage)
      ? Math.max(1, options.perPage)
      : currentCatalog?.perPage || 12;
    const query = new URLSearchParams({
      search,
      filter,
      order,
      page: String(page),
      perPage: String(perPage),
    });

    state.themes.storeLoading = true;
    state.themes.error = "";
    if (showLoading) {
      rerenderThemesPanel();
    }

    try {
      const response = await fetch(`${apiBase}api/themes/store?${query.toString()}`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `CSSLoader Store could not be loaded (${response.status}).`);
      }

      if (requestId !== state.themes.storeCatalogRequestSequence) {
        return;
      }

      state.themes.storeCatalog = payload && typeof payload === "object" ? payload : null;
      const storeSearchEditorActive =
        isEditorFocusForRoute() &&
        typeof state.editorFocusCardKey === "string" &&
        state.editorFocusCardKey.startsWith("editor-theme-store-search-");
      if (options.search !== undefined || !storeSearchEditorActive) {
        state.themes.storeSearchDraft = state.themes.storeCatalog?.search || "";
      }
    } catch (error) {
      if (requestId !== state.themes.storeCatalogRequestSequence) {
        return;
      }

      state.themes.error = error instanceof Error ? error.message : String(error);
      if (!state.themes.storeCatalog) {
        state.themes.storeCatalog = null;
      }
    } finally {
      if (requestId === state.themes.storeCatalogRequestSequence) {
        state.themes.storeLoading = false;
        rerenderThemesPanel();
      }
    }
  }

  async function loadThemesStoreTheme(storeThemeId) {
    if (!storeThemeId) {
      return;
    }

    state.themes.storeLoading = true;
    state.themes.storeDetailLoadingId = storeThemeId;
    state.themes.error = "";
    rerenderThemesPanel();

    try {
      const response = await fetch(
        `${apiBase}api/themes/store/theme?storeThemeId=${encodeURIComponent(storeThemeId)}`,
        { cache: "no-store" },
      );
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The CSSLoader Store entry could not be loaded (${response.status}).`);
      }

      if (payload && typeof payload === "object") {
        state.themes.storeDetailById[storeThemeId] = payload;
      }
    } catch (error) {
      state.themes.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.themes.storeLoading = false;
      state.themes.storeDetailLoadingId = "";
      rerenderThemesPanel();
    }
  }

  async function sendPerformanceRequest(path, bodyPayload = null, options = {}) {
    if (state.performance.saving) {
      return false;
    }

    const rerenderOnStart = options.rerenderOnStart !== false;
    const rerenderOnComplete = options.rerenderOnComplete !== false;
    state.performance.saving = true;
    state.performance.error = "";
    if (rerenderOnStart) {
      rerenderPerformancePanel();
    }

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

      if (
        (!options.optimisticKey || canApplyOptimisticResponse(options.optimisticKey, options.optimisticValue)) &&
        applyPerformanceSnapshotIfCurrent(payload)
      ) {
        if (options.optimisticKey) {
          clearOptimisticDesiredValue(options.optimisticKey, options.optimisticValue);
        }
      }
      return true;
    } catch (error) {
      state.performance.error = error instanceof Error ? error.message : String(error);
      if (
        options.reloadOnError === true &&
        (!options.optimisticKey || canApplyOptimisticResponse(options.optimisticKey, options.optimisticValue))
      ) {
        clearOptimisticDesiredValue(options.optimisticKey);
        void loadPerformanceState({ showLoading: false });
      }
      return false;
    } finally {
      state.performance.saving = false;
      if (options.clearPendingOverlayCommit === true) {
        state.performance.pendingOverlayLevelCommit = null;
        state.performance.suppressNextLivePanelRerender = Boolean(
          !state.performance.error &&
          state.liveUpdates.connected &&
          isPerformanceOverlayRoute(),
        );
      }

      if (rerenderOnComplete) {
        rerenderPerformancePanel();
      } else if (options.syncVisibleSliders === true && state.panelVisible && state.route?.pluginId === "performance") {
        refreshPerformancePanel();
      } else if (state.panelVisible && isPerformanceOverlayRoute()) {
        refreshPerformancePanel();
      } else if (state.panelVisible && state.route?.pluginId === "performance" && state.performance.error) {
        rerenderPerformancePanel();
      }
    }
  }

  async function startPerformanceOverlay() {
    await flushPerformanceOverlayLevelCommit();
    return sendPerformanceRequest("api/performance/overlay/start");
  }

  async function preparePerformanceElevatedHelper() {
    await flushPerformanceOverlayLevelCommit();
    return sendPerformanceRequest("api/performance/elevated-helper/prepare");
  }

  async function stopPerformanceOverlay() {
    await flushPerformanceOverlayLevelCommit();
    return sendPerformanceRequest("api/performance/overlay/stop");
  }

  async function setPerformanceOverlayLevel(level, options = {}) {
    return sendPerformanceRequest("api/performance/settings/overlay-level", { level }, options);
  }

  async function setPerformanceSettingValue(key, value, options = {}) {
    return sendPerformanceRequest("api/performance/settings/value", { key, value }, options);
  }

  async function togglePerformanceAutoTarget() {
    await flushPerformanceOverlayLevelCommit();
    return sendPerformanceRequest("api/performance/settings/auto-target");
  }

  async function sendSmartHomeRequest(path, bodyPayload = null, options = {}) {
    let succeeded = false;
    const rerenderOnStart = options.rerenderOnStart !== false;
    const rerenderOnComplete = options.rerenderOnComplete !== false;
    const hadError = Boolean(state.smartHome.error);
    state.smartHome.saving = true;
    state.smartHome.error = "";
    if (rerenderOnStart) {
      rerenderSmartHomePanel();
    }

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

      setSmartHomeSnapshot(payload, {
        forceDraftSync: options.forceDraftSync === true,
      });
      succeeded = true;
    } catch (error) {
      state.smartHome.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.smartHome.saving = false;
      if (rerenderOnComplete) {
        rerenderSmartHomePanel();
      } else if (options.syncVisibleSliders === true && state.panelVisible && state.route?.pluginId === "smart-home") {
        if (state.smartHome.error || hadError || !syncVisibleSlotSliderUi()) {
          rerenderSmartHomePanel();
        }
      } else if (state.panelVisible && state.route?.pluginId === "smart-home" && state.smartHome.error) {
        rerenderSmartHomePanel();
      }
    }

    return succeeded;
  }

  async function saveSmartHomeBaseUrl() {
    const value = (state.smartHome.baseUrlDraft || "").trim();
    await sendSmartHomeRequest("api/smart-home/settings/homey/base-url", { value }, { forceDraftSync: true });
  }

  async function saveSmartHomeHomeyId() {
    const value = (state.smartHome.homeyIdDraft || "").trim();
    await sendSmartHomeRequest("api/smart-home/settings/homey/homey-id", { value }, { forceDraftSync: true });
  }

  async function saveSmartHomeSessionToken() {
    const value = (state.smartHome.sessionTokenDraft || "").trim();
    await sendSmartHomeRequest("api/smart-home/settings/homey/session-token", { value }, { forceDraftSync: true });
  }

  async function clearSmartHomeSessionToken() {
    state.smartHome.sessionTokenDraft = "";
    state.smartHome.sessionTokenInputVersion += 1;
    await sendSmartHomeRequest("api/smart-home/settings/homey/session-token/clear", {}, { forceDraftSync: true });
  }

  async function refreshSmartHome(force = true) {
    await loadSmartHomeState({ force, showLoading: true });
  }

  async function runSmartHomeFlow(flowId, isAdvanced) {
    if (!flowId) {
      return;
    }

    await sendSmartHomeRequest("api/smart-home/flows/run", { flowId, isAdvanced });
  }

  async function runSmartHomeMood(moodId) {
    if (!moodId) {
      return;
    }

    await sendSmartHomeRequest("api/smart-home/moods/apply", { moodId });
  }

  async function toggleSmartHomeDevicePower(deviceId, currentValue) {
    if (!deviceId) {
      return;
    }

    previewSmartHomeCapabilityValue(deviceId, "onoff", !Boolean(currentValue));
    await sendSmartHomeRequest(
      "api/smart-home/devices/capability",
      { deviceId, capabilityId: "onoff", value: !Boolean(currentValue) },
      { rerenderOnComplete: false, syncVisibleSliders: true },
    );
  }

  async function commitSmartHomeSliderCapability(deviceId, capabilityId, nextUiValue) {
    if (!deviceId || !capabilityId) {
      return;
    }

    await sendSmartHomeRequest(
      "api/smart-home/devices/capability",
      {
        deviceId,
        capabilityId,
        value: convertSmartHomeUiValueToPayload(capabilityId, nextUiValue),
      },
      { rerenderOnStart: false, rerenderOnComplete: false, syncVisibleSliders: true },
    );
  }

  function queueSmartHomeSliderCommit(deviceId, capabilityId, nextUiValue, delayMs = smartHomeSliderCommitSettleDelayMs) {
    const commitKey = getSmartHomeSliderCommitKey(deviceId, capabilityId);
    clearSmartHomeSliderCommitTimer(commitKey);
    state.smartHome.sliderCommitTimersByKey[commitKey] = window.setTimeout(() => {
      delete state.smartHome.sliderCommitTimersByKey[commitKey];

      if (state.smartHome.saving) {
        queueSmartHomeSliderCommit(deviceId, capabilityId, nextUiValue, smartHomeSliderCommitRetryDelayMs);
        return;
      }

      void commitSmartHomeSliderCapability(deviceId, capabilityId, nextUiValue);
    }, delayMs);
  }

  function setSmartHomeSliderCapability(deviceId, capabilityId, nextUiValue) {
    if (!deviceId || !capabilityId) {
      return;
    }

    previewSmartHomeCapabilityValue(deviceId, capabilityId, nextUiValue, { syncVisibleSliders: true });
    queueSmartHomeSliderCommit(deviceId, capabilityId, nextUiValue);
  }

  function stepSmartHomeCapability(deviceId, capabilityId, direction) {
    const control = getSmartHomeControl(deviceId, capabilityId);
    if (!control || control.kind !== "slider" || isSmartHomeBusy()) {
      return;
    }

    const min = Number.isFinite(control.min) ? control.min : 0;
    const max = Number.isFinite(control.max) ? control.max : 100;
    const step = Number.isFinite(control.step) && control.step > 0 ? control.step : 5;
    const currentValue = Number.isFinite(control.numericValue) ? control.numericValue : min;
    const nextValue = Math.max(min, Math.min(max, currentValue + (step * (direction < 0 ? -1 : 1))));

    if (Object.is(nextValue, currentValue)) {
      return;
    }

    playSliderMoveSound(direction);
    setSmartHomeSliderCapability(deviceId, capabilityId, nextValue);
  }

  async function sendStoreSyncRequest(path, bodyPayload = null, options = {}) {
    const requestStateKey = options.syncing ? "syncing" : "saving";
    const rerenderOnStart = options.rerenderOnStart !== false;
    let succeeded = false;
    state.storeSync[requestStateKey] = true;
    state.storeSync.error = "";
    if (rerenderOnStart) {
      rerenderStoreSyncPanel();
    }

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

      setStoreSyncSnapshot(payload, { forceDraftSync: true });
      succeeded = true;
    } catch (error) {
      state.storeSync.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.storeSync[requestStateKey] = false;
      rerenderStoreSyncPanel();
    }

    return succeeded;
  }

  async function sendGeneralSettingsRequest(path, bodyPayload = null, options = {}) {
    let succeeded = false;
    const rerenderOnStart = options.rerenderOnStart !== false;
    state.generalSettings.saving = true;
    state.generalSettings.error = "";
    if (rerenderOnStart) {
      rerenderGeneralSettingsPanel();
    }

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

      setGeneralSettingsSnapshot(payload, { forceDraftSync: true });
      succeeded = true;
    } catch (error) {
      state.generalSettings.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.generalSettings.saving = false;
      rerenderGeneralSettingsPanel();
    }

    return succeeded;
  }

  async function openPluginStoreOverlay() {
    setupPluginStoreBridge();
    setPluginStoreRemoteActive(true);
    closeQuickAccessMenuForPluginStoreSession();

    try {
      const response = await fetch(`${apiBase}api/plugin-store/overlay/open`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: "{}",
      });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `The store could not be opened (${response.status}).`);
      }
    } catch (error) {
      setPluginStoreRemoteActive(false);
      state.generalSettings.error = error instanceof Error ? error.message : String(error);
      rerenderHomePanel();
    }
  }

  async function openUnifyStoreOverlay() {
    setupPluginStoreBridge();
    setPluginStoreRemoteActive(true);
    closeQuickAccessMenuForPluginStoreSession();

    try {
      const response = await fetch(`${apiBase}api/unifystore/overlay/open`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: "{}",
      });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Storefront could not be opened (${response.status}).`);
      }
    } catch (error) {
      setPluginStoreRemoteActive(false);
      state.storeSync.error = error instanceof Error ? error.message : String(error);
      rerenderHomePanel();
    }
  }

  async function sendUpdateRequest(path, bodyPayload = null, options = {}) {
    let succeeded = false;
    const rerenderOnStart = options.rerenderOnStart !== false;
    state.updates.saving = true;
    state.updates.error = "";
    if (rerenderOnStart) {
      rerenderGeneralSettingsPanel();
    }

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

      setUpdateSnapshot(payload);
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
      setUpdateSnapshot({
        ...snapshot,
        channel: normalizedChannel,
      });
      rerenderGeneralSettingsPanel();
    }

    await sendUpdateRequest("api/updates/channel", { channel: normalizedChannel }, { rerenderOnStart: false });
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
    const updatesVisible = isUpdatesVisibleRoute();
    if (!updatesVisible || !snapshot?.installInProgress || !shouldUseLiveUpdatePollingFallback()) {
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

      setProcessesSnapshot(payload);
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

      setAppStartSnapshot(payload);
      return true;
    } catch (error) {
      state.appStart.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.appStart.saving = false;
      rerenderAppStartPanel();
    }
  }

  async function sendThemesRequest(path, bodyPayload = null, operationText = "", options = {}) {
    let succeeded = false;
    const rerenderOnStart = options.rerenderOnStart !== false;
    const rerenderOnComplete = options.rerenderOnComplete !== false;
    state.themes.saving = true;
    state.themes.operationText = operationText || "Saving CSSLoader changes...";
    state.themes.error = "";
    if (rerenderOnStart) {
      rerenderThemesPanel();
    }

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

      if (
        (!options.optimisticKey || canApplyOptimisticResponse(options.optimisticKey, options.optimisticValue)) &&
        applyThemesSnapshotIfCurrent(payload)
      ) {
        if (options.optimisticKey) {
          clearOptimisticDesiredValue(options.optimisticKey, options.optimisticValue);
        }
      }
      succeeded = true;
    } catch (error) {
      state.themes.error = error instanceof Error ? error.message : String(error);
      if (
        options.reloadOnError === true &&
        (!options.optimisticKey || canApplyOptimisticResponse(options.optimisticKey, options.optimisticValue))
      ) {
        clearOptimisticDesiredValue(options.optimisticKey);
        void loadThemesState({ showLoading: false });
      }
    } finally {
      state.themes.saving = false;
      state.themes.operationText = "";
      if (rerenderOnComplete) {
        rerenderThemesPanel();
      } else if (options.syncVisibleSliders === true && state.panelVisible && state.route?.pluginId === "themes") {
        if (state.themes.error || !syncVisibleSlotSliderUi()) {
          rerenderThemesPanel();
        }
      } else if (state.panelVisible && state.route?.pluginId === "themes" && state.themes.error) {
        rerenderThemesPanel();
      }
    }

    return succeeded;
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

    await sendStoreSyncRequest("api/store-sync/settings/toggle", { key }, { rerenderOnStart: false });
  }

  async function setStartupMode(mode) {
    const normalizedMode = ["shell", "tray", "xbox"].includes(mode) ? mode : "shell";
    const snapshot = getGeneralSettingsSnapshot();
    if (snapshot) {
      state.generalSettings.snapshot = {
        ...snapshot,
        startupMode: normalizedMode,
        hideWindowsShellInConsoleMode:
          normalizedMode === "shell"
            ? snapshot.hideWindowsShellInConsoleMode !== false
            : false,
        runOnWindowsSignIn: true,
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/startup-mode", { mode: normalizedMode }, { rerenderOnStart: false });
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

    await sendGeneralSettingsRequest("api/settings/hide-windows-shell", { value: enabled }, { rerenderOnStart: false });
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

    await sendGeneralSettingsRequest("api/settings/developer-debug", { value: enabled }, { rerenderOnStart: false });
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
    await sendGeneralSettingsRequest(path, { value: enabled }, { rerenderOnStart: false });
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

  async function adjustWindowsShellStartDelay(delta) {
    const snapshot = getGeneralSettingsSnapshot();
    const nextValue = Math.max(0, Math.min(30, Number(snapshot?.windowsShellStartDelaySeconds || 0) + delta));
    if (snapshot) {
      state.generalSettings.snapshot = {
        ...snapshot,
        windowsShellStartDelaySeconds: nextValue,
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/windows-shell-start-delay", { value: nextValue }, { rerenderOnStart: false });
  }

  async function resetWindowsShellStartDelay() {
    const snapshot = getGeneralSettingsSnapshot();
    if (snapshot) {
      state.generalSettings.snapshot = {
        ...snapshot,
        windowsShellStartDelaySeconds: 0,
      };
      rerenderGeneralSettingsPanel();
    }

    await sendGeneralSettingsRequest("api/settings/windows-shell-start-delay", { value: 0 }, { rerenderOnStart: false });
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

      if (!enabled && pluginId === "smart-home") {
        clearAllSmartHomeSliderCommitTimers();
        state.smartHome.snapshot = null;
        state.smartHome.baseUrlDraft = "";
        state.smartHome.homeyIdDraft = "";
        state.smartHome.sessionTokenDraft = "";
      }

      rerenderGeneralSettingsPanel();
    }

    const saved = await sendGeneralSettingsRequest(
      "api/settings/plugins/enabled",
      { pluginId, enabled },
      { rerenderOnStart: false },
    );
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
    if (saved && pluginId === "smart-home") {
      clearAllSmartHomeSliderCommitTimers();
      state.smartHome.snapshot = null;
      state.smartHome.error = "";
      state.smartHome.baseUrlDraft = "";
      state.smartHome.homeyIdDraft = "";
      state.smartHome.sessionTokenDraft = "";
      state.smartHome.baseUrlInputVersion += 1;
      state.smartHome.homeyIdInputVersion += 1;
      state.smartHome.sessionTokenInputVersion += 1;
      if (enabled) {
        void loadSmartHomeState();
      } else {
        renderPanelState();
      }
    }

    if (saved) {
      if (state.route.screen === "root") {
        rerenderHomePanel();
      }

      window.SteamLoaderPluginStoreOverlay?.refresh?.({
        preserveSelection: true,
        showLoading: false,
      });
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

  async function refreshThemesCatalog() {
    await sendThemesRequest("api/themes/catalog/refresh");
  }

  async function openThemesFolder() {
    await sendThemesRequest("api/themes/folder/open");
  }

  async function installThemesBackend() {
    await sendThemesRequest(
      "api/themes/backend/install",
      null,
      "Installing CSSLoader standalone backend...",
    );
  }

  async function startThemesBackend() {
    await sendThemesRequest("api/themes/backend/start");
  }

  async function setThemesWatchEnabled(enabled) {
    await sendThemesRequest("api/themes/watch/enabled", { value: enabled });
  }

  async function installThemesStoreTheme(storeThemeId) {
    const succeeded = await sendThemesRequest("api/themes/store/install", {
      storeThemeId,
    });

    if (succeeded) {
      const catalog = getThemeStoreCatalog();
      await loadThemesStoreCatalog({
        search: catalog?.search || "",
        filter: catalog?.filter || "All",
        order: catalog?.order || "Most Downloaded",
        page: catalog?.page || 1,
        perPage: catalog?.perPage || 12,
      });
      await loadThemesStoreTheme(storeThemeId);
    }
  }

  async function searchThemesStore() {
    await loadThemesStoreCatalog({
      search: (state.themes.storeSearchDraft || "").trim(),
      page: 1,
    });
  }

  async function clearThemesStoreSearch() {
    state.themes.storeSearchDraft = "";
    state.themes.storeSearchInputVersion += 1;
    rerenderThemesPanel();
    await loadThemesStoreCatalog({
      search: "",
      page: 1,
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
                statusText: enabled ? "Active in CSSLoader" : "Ready in CSSLoader",
              }
            : snapshot.settings?.singleThemeMode && enabled
              ? {
                  ...theme,
                  enabled: false,
                  statusText: theme.installed ? "Ready in CSSLoader" : theme.statusText,
                }
              : theme,
        ),
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

  function getThemeSliderCommitKey(themeId, optionId) {
    return `${themeId}::${optionId}`;
  }

  function getThemeSliderOptimisticKey(themeId, optionId) {
    return `themes.slider.${getThemeSliderCommitKey(themeId, optionId)}`;
  }

  function clearThemeSliderCommitTimer(commitKey) {
    const timerHandle = state.themes.sliderCommitTimersByKey[commitKey];
    if (!timerHandle) {
      return;
    }

    window.clearTimeout(timerHandle);
    delete state.themes.sliderCommitTimersByKey[commitKey];
  }

  function previewThemeSliderChoice(themeId, optionId, choiceId) {
    const snapshot = getThemesSnapshot();
    if (!snapshot || !choiceId) {
      return false;
    }

    let didUpdate = false;
    state.themes.snapshot = {
      ...snapshot,
      installedThemes: snapshot.installedThemes.map((theme) =>
        theme.id === themeId
          ? {
              ...theme,
              options: theme.options.map((entry) =>
                entry.id === optionId
                  ? {
                      ...entry,
                      selectedChoiceId: choiceId,
                    }
                  : entry,
              ),
            }
          : theme,
      ),
    };

    const updatedOption = getThemeOptionById(themeId, optionId);
    didUpdate = updatedOption?.selectedChoiceId === choiceId;
    if (didUpdate) {
      setOptimisticDesiredValue(getThemeSliderOptimisticKey(themeId, optionId), choiceId);
    }
    return didUpdate;
  }

  function queueThemeSliderCommit(path, bodyPayload) {
    const commitKey = getThemeSliderCommitKey(bodyPayload.themeId, bodyPayload.optionId);
    const optimisticKey = getThemeSliderOptimisticKey(bodyPayload.themeId, bodyPayload.optionId);
    const optimisticValue = bodyPayload.choiceId;
    clearThemeSliderCommitTimer(commitKey);
    state.themes.sliderCommitTimersByKey[commitKey] = window.setTimeout(() => {
      delete state.themes.sliderCommitTimersByKey[commitKey];

      if (state.themes.saving) {
        queueThemeSliderCommit(path, bodyPayload);
        return;
      }

      void sendThemesRequest(path, bodyPayload, "", {
        rerenderOnStart: false,
        rerenderOnComplete: false,
        syncVisibleSliders: true,
        reloadOnError: true,
        optimisticKey,
        optimisticValue,
      });
    }, sliderCommitSettleDelayMs);
  }

  function adjustThemeRange(themeId, optionId, delta) {
    if (!delta) {
      return;
    }

    const option = getThemeOptionById(themeId, optionId);
    const choices = Array.isArray(option?.choices) ? option.choices : [];
    if (!choices.length) {
      return;
    }

    const currentChoiceId = option?.selectedChoiceId || choices[0]?.id || "";
    const currentIndex = Math.max(0, choices.findIndex((choice) => choice.id === currentChoiceId));
    const nextIndex = Math.max(0, Math.min(choices.length - 1, currentIndex + delta));
    const nextChoiceId = choices[nextIndex]?.id || currentChoiceId;
    if (!nextChoiceId || nextChoiceId === currentChoiceId) {
      return;
    }

    playSliderMoveSound(delta);
    if (previewThemeSliderChoice(themeId, optionId, nextChoiceId)) {
      syncVisibleSlotSliderUi();
    }

    queueThemeSliderCommit("api/themes/themes/option/choice", {
      themeId,
      optionId,
      choiceId: nextChoiceId,
    });
  }

  async function resetThemeRange(themeId, optionId) {
    const commitKey = getThemeSliderCommitKey(themeId, optionId);
    clearThemeSliderCommitTimer(commitKey);
    clearOptimisticDesiredValue(getThemeSliderOptimisticKey(themeId, optionId));
    await sendThemesRequest(
      "api/themes/themes/option/range/reset",
      {
        themeId,
        optionId,
      },
      "",
      {
        rerenderOnStart: false,
        rerenderOnComplete: false,
        syncVisibleSliders: true,
        reloadOnError: true,
      },
    );
  }

  async function createThemeProfileFromCurrentSetup() {
    const title = (state.themes.profileDraft || "").trim();
    if (title.length < 3) {
      state.themes.error = "Enter a preset name with at least 3 characters before saving.";
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

    await sendStoreSyncRequest(
      "api/store-sync/stores/enabled",
      {
        storeId,
        enabled,
      },
      { rerenderOnStart: false },
    );
  }

  async function toggleUnifySteamStoreEnabled(storeId, enabled) {
    const snapshot = getStoreSyncSnapshot();
    if (snapshot?.unifySteam?.stores) {
      state.storeSync.snapshot = {
        ...snapshot,
        unifySteam: {
          ...snapshot.unifySteam,
          stores: snapshot.unifySteam.stores.map((store) =>
            store.id === storeId
              ? {
                  ...store,
                  enabled,
                }
              : store,
          ),
        },
      };
      rerenderStoreSyncPanel();
    }

    await sendStoreSyncRequest(
      "api/store-sync/unifysteam/stores/enabled",
      {
        storeId,
        enabled,
      },
      { rerenderOnStart: false },
    );
  }

  async function refreshUnifySteam(storeId = "") {
    await sendStoreSyncRequest("api/store-sync/unifysteam/refresh", {
      value: storeId,
    });
  }

  async function startUnifySteamLogin(storeId) {
    await sendStoreSyncRequest("api/store-sync/unifysteam/stores/login", {
      value: storeId,
    });
  }

  async function refreshUnifyStore(storeId = "") {
    await sendStoreSyncRequest("api/unifystore/stores/refresh", {
      value: storeId,
    });
  }

  async function startUnifyStoreLogin(storeId) {
    await sendStoreSyncRequest("api/unifystore/stores/login", {
      value: storeId,
    });
  }

  async function submitUnifySteamAuthCode(storeId) {
    const draft = (state.storeSync.unifySteamAuthDraftByStoreId[storeId] || "").trim();
    if (!draft) {
      state.storeSync.error = "Paste the login code or page URL first.";
      rerenderStoreSyncPanel();
      return;
    }

    const succeeded = await sendStoreSyncRequest("api/store-sync/unifysteam/stores/auth-code", {
      storeId,
      value: draft,
    });

    if (succeeded) {
      state.storeSync.unifySteamAuthDraftByStoreId[storeId] = "";
      state.storeSync.unifySteamAuthInputVersionByStoreId[storeId] =
        (state.storeSync.unifySteamAuthInputVersionByStoreId[storeId] || 0) + 1;
    }
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

    const succeeded = await sendStoreSyncRequest(
      "api/store-sync/stores/additional-paths",
      {
        storeId,
        values: [],
      },
      { rerenderOnStart: false },
    );
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

  function formatSmartHomeCount(count, singular, plural = `${singular}s`) {
    const safeCount = Number.isFinite(count) ? count : 0;
    return `${safeCount} ${safeCount === 1 ? singular : plural}`;
  }

  function resolveSmartHomeStatusText() {
    if (state.smartHome.saving) {
      return "Applying Homey change...";
    }

    if (state.smartHome.loading) {
      return "Loading Homey rooms and devices...";
    }

    return getSmartHomeSnapshot()?.statusText || "Homey is ready when it is configured.";
  }

  function getSmartHomeControl(deviceId, capabilityId) {
    const device = typeof deviceId === "string" ? getSmartHomeDevice(deviceId) : deviceId;
    const controls = Array.isArray(device?.controls) ? device.controls : [];
    return controls.find((control) => control.capabilityId === capabilityId) || null;
  }

  function buildSmartHomeRoomSummary(zone) {
    if (!zone) {
      return "";
    }

    return [
      formatSmartHomeCount(zone.deviceCount || 0, "device"),
      formatSmartHomeCount(zone.lightCount || 0, "light"),
    ].join(" - ");
  }

  function buildSmartHomeFlowSummary(flow) {
    if (!flow) {
      return "";
    }

    const badge = flow.badgeText || (flow.isAdvanced ? "Advanced" : "Flow");
    const status = flow.triggerable ? "Ready" : flow.broken ? "Broken" : flow.enabled === false ? "Disabled" : "Unavailable";
    return `${badge} - ${status}`;
  }

  function buildSmartHomeMoodSummary(mood) {
    if (!mood) {
      return "";
    }

    const roomText = mood.zoneName || mood.zonePath || "Homey";
    const deviceText = formatSmartHomeCount(mood.deviceCount || 0, "device");
    return mood.preset
      ? `${roomText} - ${deviceText} - Preset: ${mood.preset}`
      : `${roomText} - ${deviceText}`;
  }

  function buildSmartHomeDeviceCopy(device) {
    if (!device) {
      return "";
    }

    const tags = Array.isArray(device.tags) ? device.tags.filter(Boolean).slice(0, 2) : [];
    const tagText = tags.length ? ` - ${tags.join(" - ")}` : "";
    return `${device.statusText || "Ready"}${tagText}`;
  }

  function buildSmartHomeDeviceSwatchLabel(device) {
    if (!device?.swatchHex) {
      return "";
    }

    return device.swatchLabel
      ? `${device.swatchLabel} - ${device.swatchHex}`
      : device.swatchHex;
  }

  function formatSmartHomeSliderValue(accent, value) {
    const safeValue = Math.round(Number(value) || 0);
    return accent === "hue" ? `${safeValue} deg` : `${safeValue}%`;
  }

  function clampSmartHomeValue(value, min, max) {
    const safeValue = Number(value) || 0;
    return Math.max(min, Math.min(max, safeValue));
  }

  function convertSmartHomeUiValueToPayload(capabilityId, nextUiValue) {
    const safeValue = Number(nextUiValue) || 0;
    switch (capabilityId) {
      case "light_hue":
        return Math.max(0, Math.min(1, safeValue / 360));
      case "dim":
      case "light_saturation":
      case "light_temperature":
        return Math.max(0, Math.min(1, safeValue / 100));
      default:
        return safeValue;
    }
  }

  function buildSmartHomeSliderStyle(device, control) {
    const previewHex = control.previewHex || device?.swatchHex || "#7CB6FF";
    switch (control.accent) {
      case "hue":
        return {
          trackStyle: {
            background:
              "linear-gradient(90deg, #ff4d4d 0%, #ffb84d 17%, #f6ff4d 33%, #5cff6b 50%, #4dd3ff 67%, #7e6fff 83%, #ff4db8 100%)",
          },
          fillStyle: {
            background:
              "linear-gradient(90deg, rgba(255,255,255,0.1) 0%, rgba(255,255,255,0.42) 100%)",
          },
          thumbStyle: {
            background: previewHex,
          },
        };
      case "saturation":
        return {
          trackStyle: {
            background: `linear-gradient(90deg, rgba(255,255,255,0.18) 0%, ${previewHex} 100%)`,
          },
          fillStyle: {
            background: `linear-gradient(90deg, rgba(255,255,255,0.3) 0%, ${previewHex} 100%)`,
          },
          thumbStyle: {
            background: previewHex,
          },
        };
      case "temperature":
        return {
          trackStyle: {
            background: "linear-gradient(90deg, #ffb56d 0%, #ffd7a6 40%, #d2ebff 68%, #8fc7ff 100%)",
          },
          fillStyle: {
            background: "linear-gradient(90deg, rgba(255,186,124,0.65) 0%, rgba(143,199,255,0.75) 100%)",
          },
          thumbStyle: {
            background: previewHex || "#ffd7a6",
          },
        };
      case "brightness":
        return {
          trackStyle: {
            background: "linear-gradient(90deg, rgba(255,255,255,0.1) 0%, rgba(255,255,255,0.75) 100%)",
          },
          fillStyle: {
            background: `linear-gradient(90deg, rgba(255,255,255,0.45) 0%, ${previewHex} 100%)`,
          },
          thumbStyle: {
            background: previewHex,
          },
        };
      default:
        return {
          thumbStyle: previewHex
            ? {
                background: previewHex,
              }
            : null,
        };
    }
  }

  function buildSmartHomeSwatchFromControls(device) {
    const hue = Number(getSmartHomeControl(device, "light_hue")?.numericValue);
    const saturation = Number(getSmartHomeControl(device, "light_saturation")?.numericValue);
    const brightness = Number(getSmartHomeControl(device, "dim")?.numericValue);
    const temperature = Number(getSmartHomeControl(device, "light_temperature")?.numericValue);
    const isOn = Boolean(getSmartHomeControl(device, "onoff")?.booleanValue);
    const lightness = Math.max(isOn ? 0.2 : 0.12, (Number.isFinite(brightness) ? brightness : 100) / 100);

    if (Number.isFinite(hue)) {
      const saturationRatio = Math.max(0, Math.min(1, (Number.isFinite(saturation) ? saturation : 100) / 100));
      return {
        swatchHex: hsvToHex((hue % 360) / 360, saturationRatio, lightness),
        swatchLabel: saturationRatio < 0.08 ? "White tone" : `Hue ${Math.round(hue)} deg`,
      };
    }

    if (Number.isFinite(temperature)) {
      const temperatureRatio = Math.max(0, Math.min(1, temperature / 100));
      return {
        swatchHex: smartHomeTemperatureToHex(temperatureRatio, lightness),
        swatchLabel: temperatureRatio < 0.34 ? "Warm white" : temperatureRatio > 0.66 ? "Cool white" : "Neutral white",
      };
    }

    return {
      swatchHex: "",
      swatchLabel: "",
    };
  }

  function buildSmartHomeDeviceStatus(device) {
    if (!device?.available) {
      return device?.statusText || "Unavailable";
    }

    const parts = [];
    const power = getSmartHomeControl(device, "onoff");
    const dim = getSmartHomeControl(device, "dim");
    if (power) {
      parts.push(power.booleanValue ? "On" : "Off");
    }

    if (dim && Number.isFinite(dim.numericValue)) {
      parts.push(`${Math.round(dim.numericValue)}%`);
    }

    if (device?.supportsColor) {
      parts.push("Color ready");
    }

    return parts.length ? parts.join(" - ") : device?.statusText || "Ready";
  }

  function patchSmartHomeDevice(device, capabilityId, nextUiValue) {
    if (!device) {
      return device;
    }

    const controls = (Array.isArray(device.controls) ? device.controls : []).map((control) => {
      if (control.capabilityId !== capabilityId) {
        return control;
      }

      if (control.kind === "switch") {
        const boolValue = Boolean(nextUiValue);
        return {
          ...control,
          booleanValue: boolValue,
          numericValue: boolValue ? 1 : 0,
          valueLabel: boolValue ? "On" : "Off",
        };
      }

      const numericValue = clampSmartHomeValue(nextUiValue, control.min ?? 0, control.max ?? 100);
      return {
        ...control,
        numericValue,
        valueLabel: formatSmartHomeSliderValue(control.accent, numericValue),
        previewHex:
          control.accent === "hue"
            ? hsvToHex((numericValue % 360) / 360, 1, 1)
            : control.previewHex,
      };
    });

    const nextDevice = {
      ...device,
      controls,
      isOn: Boolean(getSmartHomeControl({ ...device, controls }, "onoff")?.booleanValue),
    };
    const swatch = buildSmartHomeSwatchFromControls(nextDevice);
    nextDevice.swatchHex = swatch.swatchHex;
    nextDevice.swatchLabel = swatch.swatchLabel;
    nextDevice.statusText = buildSmartHomeDeviceStatus(nextDevice);
    return nextDevice;
  }

  function previewSmartHomeCapabilityValue(deviceId, capabilityId, nextValue, options = {}) {
    const snapshot = getSmartHomeSnapshot();
    if (!snapshot) {
      return;
    }

    const zones = getSmartHomeZones().map((zone) => ({
      ...zone,
      devices: (Array.isArray(zone.devices) ? zone.devices : []).map((device) =>
        device.id === deviceId ? patchSmartHomeDevice(device, capabilityId, nextValue) : device,
      ),
    }));
    const unassignedDevices = getSmartHomeUnassignedDevices().map((device) =>
      device.id === deviceId ? patchSmartHomeDevice(device, capabilityId, nextValue) : device,
    );

    setSmartHomeSnapshot(
      {
        ...snapshot,
        zones,
        unassignedDevices,
      },
      {
        clearError: false,
        syncDrafts: false,
      },
    );

    if (state.panelVisible && state.route?.pluginId === "smart-home") {
      if (options.syncVisibleSliders === true && syncVisibleSlotSliderUi()) {
        return;
      }

      rerenderSmartHomePanel();
    }
  }

  function smartHomeTemperatureToHex(ratio, brightness = 1) {
    const clampedRatio = Math.max(0, Math.min(1, Number(ratio) || 0));
    const value = Math.max(0, Math.min(1, Number(brightness) || 0));
    const warm = { r: 255, g: 171, b: 93 };
    const cool = { r: 164, g: 214, b: 255 };
    const red = Math.round((warm.r + ((cool.r - warm.r) * clampedRatio)) * value);
    const green = Math.round((warm.g + ((cool.g - warm.g) * clampedRatio)) * value);
    const blue = Math.round((warm.b + ((cool.b - warm.b) * clampedRatio)) * value);
    return rgbToHex(red, green, blue);
  }

  function hsvToHex(h, s, v) {
    const hue = ((Number(h) || 0) % 1 + 1) % 1;
    const saturation = Math.max(0, Math.min(1, Number(s) || 0));
    const brightness = Math.max(0, Math.min(1, Number(v) || 0));
    const sector = Math.floor(hue * 6);
    const fraction = hue * 6 - sector;
    const p = brightness * (1 - saturation);
    const q = brightness * (1 - (fraction * saturation));
    const t = brightness * (1 - ((1 - fraction) * saturation));

    const palette = [
      [brightness, t, p],
      [q, brightness, p],
      [p, brightness, t],
      [p, q, brightness],
      [t, p, brightness],
      [brightness, p, q],
    ];

    const [r, g, b] = palette[((sector % 6) + 6) % 6];
    return rgbToHex(Math.round(r * 255), Math.round(g * 255), Math.round(b * 255));
  }

  function rgbToHex(r, g, b) {
    const values = [r, g, b].map((value) => {
      const safeValue = Math.max(0, Math.min(255, Number(value) || 0));
      return safeValue.toString(16).padStart(2, "0");
    });
    return `#${values.join("").toUpperCase()}`;
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
      leadingIcon: options.leadingIcon || resolveDefaultSlotLeadingIcon({
        title,
        copy,
        role: options.role || "action",
      }),
      buttonClassName: options.buttonClassName || "",
      buttonStyle: options.buttonStyle || null,
      buttonProps: options.buttonProps || null,
      rowClassName: options.rowClassName || "",
      slotKey: options.slotKey || options.key || "",
      selected: Boolean(options.selected),
      value: options.value,
      layout: options.layout || "",
      expanded: Boolean(options.expanded),
      eyebrow: options.eyebrow || "",
      meta: Array.isArray(options.meta) ? options.meta.filter(Boolean) : [],
      mediaImageSrc: options.mediaImageSrc || "",
      mediaImageAlt: options.mediaImageAlt || "",
      footerLabel: options.footerLabel || "",
      swatchHex: options.swatchHex || "",
      swatchLabel: options.swatchLabel || "",
      stepperLeftDisabled: Boolean(options.stepperLeftDisabled),
      stepperRightDisabled: Boolean(options.stepperRightDisabled),
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

    const makeAccordionSlot = ui.createAccordionSlot || ((title, copy, expanded, onClick, options = {}) =>
      makeCommandSlot(title, copy, onClick, {
        ...options,
        layout: "accordion",
        expanded,
        buttonClassName:
          options.buttonClassName || "steamloader-dialog-button steamloader-dialog-button-accordion",
      }));

    const makeNavigationSlot = ui.createNavigationSlot || ((title, copy, onClick, options = {}) =>
      makeSlot(title, copy, onClick, {
        ...options,
        role: "navigation",
        trailing: options.trailing || "chevron",
      }));

    const makeFeatureNavigationSlot =
      ui.createFeatureNavigationSlot || ((title, copy, onClick, options = {}) =>
        makeNavigationSlot(title, copy, onClick, {
          ...options,
          layout: "feature",
          eyebrow: options.eyebrow || "",
          meta: Array.isArray(options.meta) ? options.meta : [],
          mediaImageSrc: options.mediaImageSrc || "",
          mediaImageAlt: options.mediaImageAlt || title || "",
          footerLabel: options.footerLabel || "Open",
          buttonClassName:
            options.buttonClassName || "steamloader-dialog-button steamloader-dialog-button-feature",
        }));

    const makeInlineStepperSlot =
      ui.createInlineStepperSlot || ((title, copy, onMoveLeft, onMoveRight, options = {}) => {
        const leftDisabled = Boolean(options.leftDisabled);
        const rightDisabled = Boolean(options.rightDisabled);
        const externalButtonProps = options.buttonProps || {};

        return makeCommandSlot(title, copy, options.onClick || onMoveRight || onMoveLeft || (() => {}), {
          ...options,
          layout: "stepper",
          trailing: "none",
          stepperLeftDisabled: leftDisabled,
          stepperRightDisabled: rightDisabled,
          buttonClassName:
            options.buttonClassName || "steamloader-dialog-button steamloader-dialog-button-inline-stepper",
          buttonProps: {
            ...externalButtonProps,
            onMoveLeft: (event) => {
              externalButtonProps.onMoveLeft?.(event);
              if (!leftDisabled) {
                onMoveLeft?.(event);
              }
              return true;
            },
            onMoveRight: (event) => {
              externalButtonProps.onMoveRight?.(event);
              if (!rightDisabled) {
                onMoveRight?.(event);
              }
              return true;
            },
          },
        });
      });

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
      sectionHeaders: [],
      dividerAfterIndex: null,
      dividerAfterIndices: null,
      audioDashboard: null,
      volumePanel: null,
      cards: [],
      editor: null,
      slots: [],
    };

    function buildCommunityPluginModel(plugin) {
      const registryEntry = plugin.registry || getCommunityRegistry()[plugin.id] || null;
      const routeContext = { ...state.route };
      const runtime = plugin.runtime || {};
      if (plugin.loadError || !registryEntry) {
        return {
          ...defaultModel,
          title: plugin.title,
          subtitle: "Community Plugin",
          error: plugin.loadError || "This community plugin has not registered a screen yet.",
          note: "Reload community plugins from the store after installing or updating a plugin.",
          slots: [
            makeCommandSlot(
              "Reload Community Plugins",
              "Load installed plugin entry points again.",
              () => loadCommunityPluginsState({ showLoading: true }),
              { leadingIcon: HeaderStoreIcon },
            ),
          ],
        };
      }

      if (typeof registryEntry.createScreen !== "function") {
        return {
          ...defaultModel,
          title: plugin.title,
          subtitle: "Community Plugin",
          note: "The plugin is installed, but it does not expose a screen yet.",
          slots: [
            makeCommandSlot(
              "Reload Community Plugins",
              "Load installed plugin entry points again.",
              () => loadCommunityPluginsState({ showLoading: true }),
              { leadingIcon: HeaderStoreIcon },
            ),
          ],
        };
      }

      try {
        const screenModel = registryEntry.createScreen({
          route: routeContext,
          plugin,
          runtime,
          refresh: () => renderPanelDataRefresh(),
          sdk: getCommunityPluginSdk(plugin, registryEntry, runtime),
        });

        if (!screenModel || typeof screenModel !== "object" || typeof screenModel.then === "function") {
          return {
            ...defaultModel,
            title: plugin.title,
            subtitle: "Community Plugin",
            note: "This plugin returned an unsupported screen model. Community screens must be synchronous for now.",
            slots: [
              makeCommandSlot(
                "Reload Community Plugins",
                "Load installed plugin entry points again.",
                () => loadCommunityPluginsState({ showLoading: true }),
                { leadingIcon: HeaderStoreIcon },
              ),
            ],
          };
        }

        return {
          ...defaultModel,
          ...screenModel,
          title: screenModel.title || plugin.title,
          subtitle: screenModel.subtitle || "Community Plugin",
          headerIcon: screenModel.headerIcon === undefined ? getPluginIconComponent(plugin.id) : screenModel.headerIcon,
          autoFocusIndex: Number.isInteger(screenModel.autoFocusIndex)
            ? screenModel.autoFocusIndex
            : resolveAutoFocusIndex(state.route),
        };
      } catch (error) {
        return {
          ...defaultModel,
          title: plugin.title,
          subtitle: "Community Plugin",
          error: error instanceof Error ? error.message : String(error),
          slots: [
            makeCommandSlot(
              "Reload Community Plugins",
              "Load installed plugin entry points again.",
              () => loadCommunityPluginsState({ showLoading: true }),
              { leadingIcon: HeaderStoreIcon },
            ),
          ],
        };
      }
    }

    if (
      (state.route.screen === "plugin" || state.route.screen === "page") &&
      state.route.pluginId &&
      getCommunityPluginDefinition(state.route.pluginId)
    ) {
      return buildCommunityPluginModel(getCommunityPluginDefinition(state.route.pluginId));
    }

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
        sectionHeaders: [
          createSectionHeader(0, "Display Controls", "Open the exact display area you want to change.", {
            icon: DisplayPluginIcon,
          }),
          createSectionHeader(3, "Maintenance", "Refresh Windows mode data when a TV or monitor changed.", {
            icon: RefreshActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Switch Output", "Choose which screen stays active right now.", {
            icon: DesktopActionIcon,
          }),
          createSectionHeader(2, "Maintenance", "Reload current Windows display data if the target changed.", {
            icon: RefreshActionIcon,
          }),
        ],
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

    if (state.route.screen === "plugin" && state.route.pluginId === "handheld-performance") {
      const snapshot = state.handheldPerformance.snapshot;
      const busy = state.handheldPerformance.loading || state.handheldPerformance.saving;
      const supported = Boolean(snapshot?.supported);
      const pawnIoInstalled = Boolean(snapshot?.pawnIoInstalled);
      const modes = Array.isArray(snapshot?.modes) ? snapshot.modes : [];
      const selectedWatts = Number(snapshot?.selectedTdpWatts || 0);
      const globalWatts = Number(snapshot?.globalTdpWatts || selectedWatts);
      const globalAcWatts = Number(snapshot?.globalAcTdpWatts || globalWatts);
      const globalBatteryWatts = Number(snapshot?.globalBatteryTdpWatts || globalWatts);
      const minimumWatts = Number(snapshot?.minimumTdpWatts || 0);
      const maximumWatts = Number(snapshot?.maximumTdpWatts || 0);
      const powerSource = snapshot?.powerSource === "battery" ? "battery" : "ac";
      const telemetry = snapshot?.telemetry || null;
      const currentGame = snapshot?.currentGame || null;
      const activeProfile = snapshot?.activeProfile || null;
      const profiles = Array.isArray(snapshot?.profiles) ? snapshot.profiles : [];
      const editingProfile = profiles.find((profile) => profile.key === state.handheldPerformance.editingProfileKey) || null;
      const modeSlots = modes.map((mode) =>
        makeCommandSlot(
          mode.title,
          `${mode.watts} W${snapshot?.selectedModeId === mode.id ? " - selected" : ""}`,
          () => void sendHandheldPerformanceRequest("api/handheld-performance/mode", { modeId: mode.id }),
          {
            slotKey: `handheld-mode-${mode.id}`,
            disabled: busy || !supported,
            leadingIcon: PerformancePluginIcon,
          },
        ),
      );
      const tdpSlider = createPerformanceValueSliderSlot({
        title: `${currentGame?.title || "Game"} TDP`,
        copy: supported ? "Saved automatically for the active game" : "No supported device detected",
        hint: "Left / Right changes TDP by 1 watt.",
        slotKey: "handheld-tdp-slider",
        min: minimumWatts,
        max: maximumWatts,
        step: 1,
        disabled: busy || !supported,
        getValue: () => selectedWatts,
        displayValue: (value) => `${value} W`,
        onAdjust: (direction) => stepHandheldTdp(direction),
      });
      const createGlobalTdpSlider = (source, title, watts) => createPerformanceValueSliderSlot({
        title,
        copy: supported
          ? `${minimumWatts}-${maximumWatts} W fallback for games without their own ${source} profile`
          : "No supported device detected",
        hint: "Left / Right changes this persistent profile by 1 watt.",
        slotKey: `handheld-global-${source}-tdp-slider`,
        min: minimumWatts,
        max: maximumWatts,
        step: 1,
        disabled: busy || !supported,
        getValue: () => watts,
        displayValue: (value) => `${value} W`,
        onAdjust: (direction) => stepHandheldGlobalTdp(source, direction),
      });
      const globalTdpSliders = [
        createGlobalTdpSlider("ac", "Plugged In Profile", globalAcWatts),
        createGlobalTdpSlider("battery", "Battery Profile", globalBatteryWatts),
      ];
      const profileSlots = profiles.map((profile) =>
        makeCommandSlot(
          profile.title || profile.appId || "Saved game",
          `${getHandheldProfileTdp(profile, "ac")} W plugged in / ${getHandheldProfileTdp(profile, "battery")} W battery`,
          () => {
            state.handheldPerformance.editingProfileKey =
              state.handheldPerformance.editingProfileKey === profile.key ? "" : profile.key;
            renderPanelDataRefresh();
          },
          {
            slotKey: `handheld-profile-${profile.key}`,
            disabled: busy,
            leadingIcon: PerformancePluginIcon,
            selected: editingProfile?.key === profile.key,
            badge: editingProfile?.key === profile.key ? "Editing" : "",
          },
        ),
      );
      const profileEditorSlots = editingProfile
        ? [
            createPerformanceValueSliderSlot({
              title: `${editingProfile.title} - Plugged In`,
              copy: "Saved TDP while the charger is connected.",
              hint: "Left / Right changes the game profile by 1 watt.",
              slotKey: `handheld-profile-editor-${editingProfile.key}-ac`,
              min: minimumWatts,
              max: maximumWatts,
              step: 1,
              disabled: busy || !supported,
              getValue: () => getHandheldProfileTdp(editingProfile, "ac"),
              displayValue: (value) => `${value} W`,
              onAdjust: (direction) => stepHandheldGameProfileTdp(editingProfile.key, "ac", direction),
            }),
            createPerformanceValueSliderSlot({
              title: `${editingProfile.title} - Battery`,
              copy: "Saved TDP while running from the internal battery.",
              hint: "Left / Right changes the game profile by 1 watt.",
              slotKey: `handheld-profile-editor-${editingProfile.key}-battery`,
              min: minimumWatts,
              max: maximumWatts,
              step: 1,
              disabled: busy || !supported,
              getValue: () => getHandheldProfileTdp(editingProfile, "battery"),
              displayValue: (value) => `${value} W`,
              onAdjust: (direction) => stepHandheldGameProfileTdp(editingProfile.key, "battery", direction),
            }),
            makeCommandSlot(
              "Delete Game Profile",
              `Remove the automatic TDP values saved for ${editingProfile.title}.`,
              () => {
                const key = editingProfile.key;
                state.handheldPerformance.editingProfileKey = "";
                void sendHandheldPerformanceRequest(
                  "api/handheld-performance/profiles/delete",
                  { key },
                );
              },
              {
                slotKey: `handheld-profile-delete-${editingProfile.key}`,
                disabled: busy,
                leadingIcon: RefreshActionIcon,
              },
            ),
          ]
        : [];
      const activeGameSlots = currentGame ? [tdpSlider] : [];
      const globalProfileStartIndex = 2;
      const activeGameStartIndex = globalProfileStartIndex + globalTdpSliders.length;
      const modeStartIndex = activeGameStartIndex + activeGameSlots.length;
      const profileStartIndex = modeStartIndex + modeSlots.length;
      const profileEditorStartIndex = profileStartIndex + profileSlots.length;
      const maintenanceStartIndex = profileEditorStartIndex + profileEditorSlots.length;
      return {
        ...defaultModel,
        title: snapshot?.pluginTitle || "Handheld Performance",
        subtitle: snapshot?.productCode || "Device detection",
        status: snapshot?.statusText || "Loading handheld state...",
        error: state.handheldPerformance.error || snapshot?.errorText || "",
        note: supported
          ? currentGame
            ? `Changes are saved automatically for ${currentGame.title}. The saved TDP will be restored on its next launch.`
            : "No game is running. TDP changes now update the global default profile."
          : "TDP writes remain disabled until a supported handheld is detected.",
        cards: [
          {
            title: "Live Power",
            lines: [
              {
                liveKey: "handheld-power-source",
                text: powerSource === "battery" ? "Battery power" : "Plugged in",
              },
              {
                liveKey: "handheld-battery-level",
                text: Number(telemetry?.batteryPercent) >= 0
                  ? `${telemetry.batteryPercent}% battery`
                  : "Battery level unavailable",
              },
              {
                liveKey: "handheld-applied-tdp",
                text: telemetry?.appliedTdpConfirmed
                  ? `${telemetry.appliedTdpWatts} W applied`
                  : `${selectedWatts} W requested`,
              },
            ],
          },
          {
            title: currentGame ? "Active Game" : "Automatic Profiles",
            lines: currentGame
              ? [
                  currentGame.title,
                  activeProfile
                    ? `${getHandheldProfileTdp(activeProfile, powerSource)} W ${powerSource} profile`
                    : `${globalWatts} W global fallback; moving the slider creates a game profile`,
                ]
              : [
                  `${globalAcWatts} W plugged in / ${globalBatteryWatts} W battery`,
                  `${profiles.length} saved game profile${profiles.length === 1 ? "" : "s"}`,
                ],
          },
        ],
        autoFocusIndex: resolveAutoFocusIndex(state.route) ?? 0,
        sectionHeaders: [
          createSectionHeader(0, "Automatic Profiles", "Detect Steam games and restore their saved TDP.", {
            icon: PerformancePluginIcon,
          }),
          createSectionHeader(globalProfileStartIndex, "Global Profiles", "Separate persistent defaults for plugged-in and battery use.", {
            icon: PerformancePluginIcon,
          }),
          ...(currentGame
            ? [createSectionHeader(activeGameStartIndex, "Active Game Profile", `Saved automatically for ${currentGame.title}.`, {
                icon: PerformancePluginIcon,
              })]
            : []),
          createSectionHeader(modeStartIndex, "TDP Modes", "Apply device-specific presets.", {
            icon: PerformancePluginIcon,
          }),
          ...(profileSlots.length
            ? [createSectionHeader(profileStartIndex, "Saved Game Profiles", "Open a profile to edit both power states.", {
                icon: PerformancePluginIcon,
              })]
            : []),
          ...(profileEditorSlots.length
            ? [createSectionHeader(profileEditorStartIndex, `Edit ${editingProfile.title}`, "Fine-tune and maintain this game profile.", {
                icon: PerformancePluginIcon,
              })]
            : []),
          createSectionHeader(maintenanceStartIndex, "Maintenance", "Install PawnIO or reload helper status.", {
            icon: RefreshActionIcon,
          }),
        ],
        dividerAfterIndices: [
          1,
          activeGameStartIndex - 1,
          ...(currentGame ? [modeStartIndex - 1] : []),
          profileStartIndex - 1,
          ...(profileSlots.length ? [profileEditorStartIndex - 1] : []),
          ...(profileEditorSlots.length ? [maintenanceStartIndex - 1] : []),
        ],
        slots: [
          makeSettingToggleSlot(
            "handheld-performance",
            "automatic-profiles",
            "Automatic Game Profiles",
            "Apply a saved TDP when a Steam game starts and return to the global profile when it closes.",
            snapshot?.autoProfilesEnabled !== false,
            () => void sendHandheldPerformanceRequest(
              "api/handheld-performance/profiles/auto-enabled",
              { value: snapshot?.autoProfilesEnabled === false },
            ),
            { disabled: busy || !supported },
          ),
          makeSettingToggleSlot(
            "handheld-performance",
            "profile-notifications",
            "Windows Profile Notifications",
            "Show one Windows notification when TFS automatically applies a profile.",
            snapshot?.profileNotificationsEnabled !== false,
            () => void sendHandheldPerformanceRequest(
              "api/handheld-performance/profiles/notifications-enabled",
              { value: snapshot?.profileNotificationsEnabled === false },
            ),
            { disabled: busy || !supported },
          ),
          ...globalTdpSliders,
          ...activeGameSlots,
          ...modeSlots,
          ...profileSlots,
          ...profileEditorSlots,
          makeCommandSlot(
            "Test Profile Notification",
            "Show the same TFS profile banner used for automatic game and global profile changes.",
            () => void sendHandheldPerformanceRequest(
              "api/handheld-performance/profiles/notifications/test",
              {},
            ),
            {
              slotKey: "handheld-notification-test",
              disabled: busy || !supported,
              leadingIcon: PerformancePluginIcon,
            },
          ),
          makeCommandSlot(
            pawnIoInstalled ? "Repair PawnIO" : "Install PawnIO",
            pawnIoInstalled
              ? "Run the bundled verified PawnIO 2.2.0 setup again."
              : "Install the verified PawnIO 2.2.0 driver required for TDP control.",
            () => void sendHandheldPerformanceRequest("api/handheld-performance/pawnio/install", {}),
            {
              slotKey: "handheld-pawnio-install",
              disabled: busy || !supported,
              leadingIcon: SaveActionIcon,
            },
          ),
          makeCommandSlot("Refresh Status", "Read the latest result from the elevated helper.", () => {
            void loadHandheldPerformanceState();
          }, {
            slotKey: "handheld-refresh",
            disabled: busy,
            leadingIcon: RefreshActionIcon,
          }),
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
        sectionHeaders: [
          createSectionHeader(0, "Available Resolutions", "Only presets supported by the active display are shown.", {
            icon: ResolutionActionIcon,
          }),
          ...(resolutionPresets.length
            ? [createSectionHeader(resolutionPresets.length, "Maintenance", "Reload the preset list if Windows changed outputs.", {
                icon: RefreshActionIcon,
              })]
            : []),
        ],
        dividerAfterIndex: resolutionPresets.length ? resolutionPresets.length - 1 : null,
        slots: [
          ...resolutionPresets.map((preset) =>
            makeChoiceSlot(
              preset.title,
              preset.available ? preset.description : "Not available on the current display.",
              () => setDisplayResolutionPreset(preset.id, preset.title),
              {
                slotKey: `display-resolution-${preset.id}`,
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
        sectionHeaders: [
          createSectionHeader(0, "Available Refresh Rates", "Rates are filtered to match the current resolution.", {
            icon: RefreshRateActionIcon,
          }),
          ...(refreshRatePresets.length
            ? [createSectionHeader(refreshRatePresets.length, "Maintenance", "Reload the preset list if Windows changed outputs.", {
                icon: RefreshActionIcon,
              })]
            : []),
        ],
        dividerAfterIndex: refreshRatePresets.length ? refreshRatePresets.length - 1 : null,
        slots: [
          ...refreshRatePresets.map((preset) =>
            makeChoiceSlot(
              preset.title,
              preset.available ? preset.description : "Not available at the current resolution.",
              () => setDisplayRefreshRatePreset(preset.id),
              {
                slotKey: `display-refresh-rate-${preset.id}`,
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
        sectionHeaders: [
          createSectionHeader(0, "Recovery & Steam", "Use these first when Big Picture or TFS needs a safe reset.", {
            icon: PowerPluginIcon,
          }),
          createSectionHeader(3, "System Power", "These affect the whole PC, so they stay grouped at the end.", {
            icon: ShutdownActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Open Windows", "Pick a window to bring it to the foreground.", {
            icon: ProcessesPluginIcon,
          }),
          ...(windows.length
            ? [createSectionHeader(windows.length, "Maintenance", "Refresh the list after apps open, close, or minimize.", {
                icon: RefreshActionIcon,
              })]
            : []),
        ],
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
                slotKey: `process-window-${windowInfo.handle}`,
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
        sectionHeaders: [
          createSectionHeader(0, "Launcher", "Add new Windows apps or jump into your saved shortcuts.", {
            icon: AppStartPluginIcon,
          }),
          ...(shortcuts.length
            ? [createSectionHeader(1, "Saved Shortcuts", "These apps are ready to launch from the controller.", {
                icon: LaunchActionIcon,
              })]
            : []),
          createSectionHeader(shortcuts.length + 1, "Maintenance", "Reload the saved launcher list and the Start Menu catalog.", {
            icon: RefreshActionIcon,
          }),
        ],
        slots: [
          makeNavigationSlot(
            "Add App",
            "Choose an installed Start Menu app and add it to this launcher.",
            () => {
              rememberCurrentRouteIndex(0);
              setRoute({ screen: "page", pluginId: "app-start", pageId: "add-app" });
            },
            {
              slotKey: "app-start-add-app",
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
                slotKey: `app-start-shortcut-${shortcut.id}`,
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
        sectionHeaders: [
          createSectionHeader(0, "Detected Start Menu Apps", "Pick any app here to add it into App Start.", {
            icon: AddActionIcon,
          }),
          ...(apps.length
            ? [createSectionHeader(apps.length, "Maintenance", "Rescan the Start Menu after new installs.", {
                icon: RefreshActionIcon,
              })]
            : []),
        ],
        slots: [
          ...apps.map((app) =>
            makeCommandSlot(
              app.name,
              app.added ? "Already added to App Start." : "Add this app to the launcher.",
              () => addAppStartShortcut(app.id),
              {
                slotKey: `app-start-catalog-${app.id}`,
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
        sectionHeaders: [
          createSectionHeader(0, "Launch", "Start the app or remove it from the launcher library.", {
            icon: LaunchActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Overlay", "Turn the game-page module on or off and keep the detail link visible.", {
            icon: HltbPluginIcon,
          }),
          createSectionHeader(1, "Visible Time Blocks", "Choose which estimate categories appear on game pages.", {
            icon: EyeActionIcon,
          }),
          createSectionHeader(6, "Cache", "Clear cached matches when you want a fresh lookup.", {
            icon: DeleteActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Automation", "Control when TFS should start and stop the SISR marker.", {
            icon: AutoSisirPluginIcon,
          }),
          createSectionHeader(2, "Executable Path", "Save or reset the SISR location used for launches.", {
            icon: FolderActionIcon,
          }),
          createSectionHeader(4, "Maintenance", "Reload watched titles and marker state from the backend.", {
            icon: RefreshActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Detected Titles", "Toggle which detected games should trigger the marker automatically.", {
            icon: EyeActionIcon,
          }),
          ...(titles.length
            ? [createSectionHeader(titles.length, "Maintenance", "Refresh the detected game list after Store Sync changes.", {
                icon: RefreshActionIcon,
              })]
            : []),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Maintenance", "Reload the latest trace lines from the log file.", {
            icon: LogActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Artwork Picker", "Control how the Big Picture Change Artwork action behaves.", {
            icon: ArtworkPluginIcon,
          }),
          createSectionHeader(2, "SteamGridDB Access", "Save or clear the API key used for manual artwork browsing.", {
            icon: SaveActionIcon,
          }),
          createSectionHeader(4, "Steam Path", "Choose whether artwork writes use a manual or detected Steam path.", {
            icon: FolderActionIcon,
          }),
          createSectionHeader(6, "Result Count", "Increase or reduce the number of results shown per tab.", {
            icon: EyeActionIcon,
          }),
          createSectionHeader(8, "Maintenance", "Reload artwork settings from the background host.", {
            icon: RefreshActionIcon,
          }),
        ],
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
      const slots = [];

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
              leadingIcon: AudioPluginIcon,
            },
          ),
        );
      }

      slots.push(
        makeCommandSlot("Refresh", resolveAudioStatusText(), () => loadAudioDevices(), {
          disabled: state.audio.loading,
        }),
      );

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
        sectionHeaders: [
          createSectionHeader(0, "Playback Devices", "Pick the Windows default output device directly from Quick Access.", {
            icon: AudioPluginIcon,
          }),
          ...(state.audio.devices.length
            ? [createSectionHeader(state.audio.devices.length, "Maintenance", "Refresh the device list after speakers or headsets change.", {
                icon: RefreshActionIcon,
              })]
            : []),
        ],
        dividerAfterIndex: state.audio.devices.length ? state.audio.devices.length - 1 : null,
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
        sectionHeaders: [
          createSectionHeader(0, "Per-App Mixer", "Adjust each active session without leaving Quick Access.", {
            icon: AudioPluginIcon,
          }),
          ...(mixerSessions.length
            ? [createSectionHeader(mixerSlots.length - 1, "Maintenance", "Refresh the session list after apps start or close.", {
                icon: RefreshActionIcon,
              })]
            : []),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Overlay Controls", "Adjust the live overlay and its built-in TFS options first.", {
            icon: PerformancePluginIcon,
          }),
          createSectionHeader(performancePrimarySlots.length, "Readouts & Maintenance", "Review helper status and one-shot actions below.", {
            icon: RefreshActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Main Areas", "Jump into preview, logs, stores, or detailed settings.", {
            icon: StoreSyncPluginIcon,
          }),
          createSectionHeader(4, "Maintenance", "Reload store state and rebuild the current sync plan.", {
            icon: RefreshActionIcon,
          }),
        ],
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
              slotKey: "store-sync-preview-page",
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
              slotKey: "store-sync-journal-page",
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
              slotKey: "store-sync-stores-page",
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
              slotKey: "store-sync-settings-page",
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
        sectionHeaders: [
          createSectionHeader(0, "Recent Events", "Newest sync and watcher events appear at the top.", {
            icon: LogActionIcon,
          }),
        ],
        dividerAfterIndex: journalEntries.length ? 0 : null,
        slots: journalEntries.map((entry, index) =>
          makeCommandSlot(
            `${(entry.level || "info").toUpperCase()} - ${entry.message || "Store Sync Event"}`,
            [entry.trigger ? `Source: ${entry.trigger}` : "", entry.detail || "", entry.timestampUtc ? new Date(entry.timestampUtc).toLocaleString() : ""]
              .filter(Boolean)
              .join(" - "),
            () => {},
            {
              slotKey: `store-sync-journal-${entry.timestampUtc || index}-${entry.trigger || "entry"}-${entry.message || index}`,
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
                  slotKey: `store-sync-preview-title-${planEntry.entry.id}`,
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
                  slotKey: `store-sync-preview-item-${planEntry.entry?.id || planIndex}`,
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
                  slotKey: `store-sync-preview-action-${planEntry.entry.id}-${planEntry.action}`,
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
        sectionHeaders: [
          createSectionHeader(0, "Preview Behavior", "Control where this title appears and whether it stays excluded.", {
            icon: EyeActionIcon,
          }),
          createSectionHeader(2, "Override Rules", "Save or clear custom title and artwork matching rules.", {
            icon: SaveActionIcon,
          }),
          createSectionHeader(4, "Maintenance", "Refresh the source scan when this game changed outside TFS.", {
            icon: RefreshActionIcon,
          }),
        ],
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
        sectionHeaders: [
          createSectionHeader(0, "Artwork", "Control downloaded artwork style and animation preference.", {
            icon: ArtworkPluginIcon,
          }),
          createSectionHeader(2, "Shortcut Strategy", "Choose how Store Sync backs up and reuses Steam shortcuts.", {
            icon: SaveActionIcon,
          }),
          createSectionHeader(4, "Cleanup", "Allow TFS to remove managed shortcuts when launchers stop reporting them.", {
            icon: DeleteActionIcon,
          }),
        ],
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
      const shellHideAvailable = startupMode === "shell";
      const xboxModeSupported = settings?.xboxModeSupported === true;
      const startupModeSlotCount = xboxModeSupported ? 3 : 2;
      const xboxModeSupportNote = xboxModeSupported
        ? ""
        : ` Xbox Mode is hidden: ${settings?.xboxModeSupportReason || "Windows Gaming FSE support was not detected."}`;

      return {
        ...defaultModel,
        title: "Settings",
        subtitle: "General",
        status: resolveGeneralSettingsStatusText(),
        error: state.generalSettings.error,
        note: `Choose a supported startup mode, then manage global behavior and plugins below. Startup modes always replace each other.${xboxModeSupportNote}`,
        sectionHeaders: [
          createSectionHeader(0, "Startup Mode", "Choose how TFS enters Windows and Steam on sign-in.", {
            icon: SettingsPluginIcon,
          }),
          createSectionHeader(startupModeSlotCount, "Behavior", "Fine-tune shell hiding and debug visibility.", {
            icon: DesktopActionIcon,
          }),
          createSectionHeader(startupModeSlotCount + 2, "Built-In Plugins", "Show or hide modules and block their background routes.", {
            icon: SteamLoaderIcon,
          }),
        ],
        dividerAfterIndex: startupModeSlotCount + 1,
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
              leadingIcon: DesktopActionIcon,
            },
          ),
          makeChoiceSlot(
            "eTray",
            "Windows starts normally. Tools for Steam runs from the tray, syncs launchers, and starts Steam without taking over the shell.",
            () => setStartupMode("tray"),
            {
              disabled: isGeneralSettingsBusy() || startupMode === "tray",
              selected: startupMode === "tray",
              badge: startupMode === "tray" ? "Current" : "",
              trailing: startupMode === "tray" ? "none" : "chevron",
              leadingIcon: SteamLoaderIcon,
            },
          ),
          ...(xboxModeSupported
            ? [
                makeChoiceSlot(
                  "Xbox Mode",
                  "Windows launches TFS as the Xbox Mode Home app. Shell takeover and eTray startup are disabled.",
                  () => setStartupMode("xbox"),
                  {
                    disabled: isGeneralSettingsBusy() || startupMode === "xbox",
                    selected: startupMode === "xbox",
                    badge: startupMode === "xbox" ? "Current" : "",
                    trailing: startupMode === "xbox" ? "none" : "chevron",
                    leadingIcon: SettingsPluginIcon,
                  },
                ),
              ]
            : []),
          makeSettingToggleSlot(
            "tfs",
            "hide-windows-shell",
            "Hide Windows Shell in Console Mode",
            "Hide the taskbar and desktop icons while Steam Big Picture is active. This only applies in Shell Takeover mode and never in Xbox Mode or eTray.",
            shellHideAvailable && settings?.hideWindowsShellInConsoleMode !== false,
            () => toggleHideWindowsShellInConsoleMode(),
            {
              disabled: isGeneralSettingsBusy() || !shellHideAvailable,
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
      const shellTakeoverMode = settings?.startupMode === "shell";
      const wallpaperPath = splash?.wallpaperPath || "";
      const iconPath = splash?.iconPath || "";
      const windowsShellStartDelaySeconds = Number(settings?.windowsShellStartDelaySeconds || 0);

      return {
        ...defaultModel,
        title: "Settings",
        subtitle: "Splashscreen Themes",
        status: resolveGeneralSettingsStatusText(),
        error: state.generalSettings.error,
        note: "Use full local image paths. Missing files fall back safely. The enabled startup splash appears for 10 seconds in Shell Takeover, Xbox Mode, and eTray.",
        sectionHeaders: [
          createSectionHeader(0, "Preview", "Open the splash briefly without running the full startup flow.", {
            icon: EyeActionIcon,
          }),
          createSectionHeader(2, "Artwork Paths", "Save or clear the wallpaper and icon used during startup.", {
            icon: FolderActionIcon,
          }),
          createSectionHeader(6, "Windows Hand-Off Delay", "Tune how long Windows waits before restoring behind Big Picture.", {
            icon: RefreshRateActionIcon,
          }),
          createSectionHeader(9, "Maintenance", "Reload splash settings from the current TFS configuration.", {
            icon: RefreshActionIcon,
          }),
        ],
        cards: [
          {
            title: "Current Splash",
            lines: [
              "Splashscreen: Shown for 10 seconds in every startup mode",
              `Text: ${splash?.showText === false ? "Hidden" : "Shown"}`,
              wallpaperPath
                ? `Wallpaper: ${splash?.wallpaperExists ? wallpaperPath : `Missing - ${wallpaperPath}`}`
                : "Wallpaper: default background",
              iconPath
                ? `Icon: ${splash?.iconExists ? iconPath : `Missing - ${iconPath}`}`
                : "Icon: default Tools for Steam icon",
              `Additional Windows hand-off delay: ${windowsShellStartDelaySeconds}s`,
              `Total Windows hand-off delay: ${5 + windowsShellStartDelaySeconds}s after Big Picture is visible`,
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
            "show-text",
            "Show Splash Text",
            "Show startup status text on top of the splash artwork in every startup mode.",
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
            "Start Windows one second sooner after Big Picture is visible.",
            () => adjustWindowsShellStartDelay(-1),
            {
              disabled: isGeneralSettingsBusy() || windowsShellStartDelaySeconds <= 0,
              leadingIcon: RefreshRateActionIcon,
            },
          ),
          makeCommandSlot(
            "Longer Delay",
            "Wait one extra second before Windows starts in the background.",
            () => adjustWindowsShellStartDelay(1),
            {
              disabled: isGeneralSettingsBusy() || windowsShellStartDelaySeconds >= 30,
              leadingIcon: RefreshRateActionIcon,
            },
          ),
          makeCommandSlot(
            "Reset Delay",
            "Use only the default 5 second Windows hand-off delay.",
            () => resetWindowsShellStartDelay(),
            {
              disabled: isGeneralSettingsBusy() || windowsShellStartDelaySeconds <= 0,
              leadingIcon: RefreshRateActionIcon,
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
        sectionHeaders: [
          createSectionHeader(0, "Release Channel", "Choose whether TFS follows stable releases or beta previews.", {
            icon: HeaderUpdateIcon,
          }),
          createSectionHeader(2, "Update Actions", "Refresh release metadata or install the newest compatible build.", {
            icon: RefreshActionIcon,
          }),
        ],
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
              leadingIcon: HeaderUpdateIcon,
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
              leadingIcon: HeaderUpdateIcon,
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
          title: "CSSLoader",
          subtitle: "Theme Option",
          status: themesStatus,
          error: state.themes.error,
          note: "The requested CSSLoader patch could not be found.",
          slots: [
            makeCommandSlot("Refresh CSSLoader State", "Reload the current CSSLoader theme state.", () => loadThemesState(), {
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
              lines: [
                formatThemeOptionValue(option),
                ...(option.advancedControlCount > 0
                  ? [`${option.advancedControlCount} advanced control${option.advancedControlCount === 1 ? "" : "s"} for this patch are not exposed in Quick Access yet.`]
                  : []),
              ],
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
          title: "CSSLoader",
          subtitle: "Preset",
          status: themesStatus,
          error: state.themes.error,
          note: "The requested CSSLoader preset could not be found in the installed theme folder.",
          slots: [
            makeCommandSlot("Refresh CSSLoader State", "Reload presets from the live CSSLoader backend.", () => refreshThemesCatalog(), {
              disabled: state.themes.loading || state.themes.saving,
            }),
          ],
        };
      }

      return {
        ...defaultModel,
        title: "CSSLoader",
        subtitle: profile.title,
        status: themesStatus,
        error: state.themes.error,
        note: profile.description,
        cards: [buildThemeProfileSummaryCard(profile)],
        slots: [
          makeCommandSlot(
            "Apply Preset",
            "Switch CSSLoader to the theme stack saved in this preset.",
            () => applyThemeProfile(profile.id),
            {
              disabled: state.themes.loading || state.themes.saving,
              badge: profile.selected ? "Selected" : "",
            },
          ),
          makeCommandSlot(
            "Update From Current Setup",
            "Overwrite this saved preset with the currently enabled CSSLoader theme stack.",
            () => updateThemeProfile(profile.id),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          makeCommandSlot(
            "Remove Preset",
            "Delete this saved preset from the installed CSSLoader theme folder.",
            () => removeThemeProfile(profile.id),
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
          title: "CSSLoader",
          subtitle: "Theme",
          status: themesStatus,
          error: state.themes.error,
          note: "The requested CSSLoader theme could not be found in the current installed library.",
          slots: [
            makeCommandSlot("Refresh CSSLoader State", "Ask CSSLoader to rescan the installed theme library.", () => refreshThemesCatalog(), {
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

            if (option.type === "slider" || option.type === "choice") {
              return createThemeSliderSlot(theme, option);
            }

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
        title: "CSSLoader",
        subtitle: theme.title,
        status: themesStatus,
        error: state.themes.error,
        note: theme.description,
        cards: [buildThemeSummaryCard(theme)],
        slots: [
          makeSettingToggleSlot(
            "themes.theme",
            theme.id,
            "Enabled",
            "Turn this CSSLoader theme on or off without leaving Quick Access.",
            Boolean(theme.enabled),
            () => toggleThemeEnabled(theme.id, !Boolean(theme.enabled)),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          ...optionSlots,
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      state.route.pageId === "store"
    ) {
      const integration = getThemeIntegration();
      const storeCatalog = getThemeStoreCatalog();
      const storeItems = Array.isArray(storeCatalog?.items) ? storeCatalog.items : [];
      const availableFilters =
        Array.isArray(storeCatalog?.availableFilters) && storeCatalog.availableFilters.length > 0
          ? storeCatalog.availableFilters
          : ["All"];
      const availableOrders =
        Array.isArray(storeCatalog?.availableOrders) && storeCatalog.availableOrders.length > 0
          ? storeCatalog.availableOrders
          : ["Most Downloaded"];
      const currentFilter = storeCatalog?.filter || "All";
      const currentOrder = storeCatalog?.order || availableOrders[0] || "Most Downloaded";
      const currentPage = Math.max(1, storeCatalog?.page || 1);
      const perPage = Math.max(1, storeCatalog?.perPage || 12);
      const total = Math.max(0, storeCatalog?.total || 0);
      const totalPages = Math.max(1, Math.ceil(total / perPage));
      const currentFilterIndex = Math.max(0, availableFilters.findIndex((value) => value === currentFilter));
      const currentOrderIndex = Math.max(0, availableOrders.findIndex((value) => value === currentOrder));
      const filtersExpanded = isExpandedSection("themes-store-filters", false);
      const filterSummary = buildThemeStoreFilterSummary(
        currentFilter,
        currentOrder,
        state.themes.storeSearchDraft,
      );

      if (!storeCatalog && !state.themes.storeLoading) {
        void loadThemesStoreCatalog();
      }

      return {
        ...defaultModel,
        title: "CSSLoader",
        subtitle: "Big Picture Store",
        panelClassName: "steamloader-panel-themes-store",
        status: themesStatus,
        error: state.themes.error,
        note: "",
        cards: [
          {
            title: "DeckThemes Store",
            lines: [
              `${storeItems.length} themes shown right now`,
              `Page ${currentPage} of ${totalPages}`,
              `${total.toLocaleString()} total Big Picture themes${integration?.backendReachable ? " - installs ready" : " - browse only until CSSLoader is online"}`,
            ],
          },
        ],
        editor: filtersExpanded
          ? {
              label: "Search Catalog",
              help: "Search Big Picture themes by title, author, or theme name, then press Search Store below to apply it.",
              value: state.themes.storeSearchDraft,
              placeholder: "Round",
              inputKey: `theme-store-search-${state.themes.storeSearchInputVersion}`,
              rows: 2,
              onInput: (value) => {
                state.themes.storeSearchDraft = value;
              },
            }
          : null,
        slots: [
          makeAccordionSlot(
            "Filters",
            filterSummary,
            filtersExpanded,
            () => {
              toggleExpandedSection("themes-store-filters", false);
              rerenderThemesPanel();
            },
            {
              slotKey: "theme-store-filters-toggle",
              disabled: state.themes.storeLoading && !storeCatalog,
            },
          ),
          ...(filtersExpanded
            ? [
                createPerformanceValueSliderSlot({
                  title: "Filter",
                  copy: "Switch between Big Picture theme categories.",
                  hint: "Use Left / Right to change the active filter. Press A to reset to All.",
                  slotKey: "theme-store-filter",
                  min: 0,
                  max: Math.max(0, availableFilters.length - 1),
                  step: 1,
                  disabled: state.themes.storeLoading || availableFilters.length <= 1,
                  getValue: () => {
                    const liveFilter = getThemeStoreCatalog()?.filter || "All";
                    const liveIndex = availableFilters.findIndex((value) => value === liveFilter);
                    return liveIndex >= 0 ? liveIndex : currentFilterIndex;
                  },
                  displayValue: (index) => {
                    const safeIndex = Math.max(0, Math.min(availableFilters.length - 1, index));
                    return availableFilters[safeIndex] || "All";
                  },
                  onAdjust: (direction) => {
                    const currentCatalog = getThemeStoreCatalog();
                    const liveFilter = getThemeStoreCatalog()?.filter || "All";
                    const liveIndex = Math.max(0, availableFilters.findIndex((value) => value === liveFilter));
                    const nextIndex = Math.max(0, Math.min(availableFilters.length - 1, liveIndex + direction));
                    const nextFilter = availableFilters[nextIndex] || "All";
                    if (currentCatalog) {
                      state.themes.storeCatalog = {
                        ...currentCatalog,
                        filter: nextFilter,
                        page: 1,
                      };
                      syncVisibleSlotSliderUi();
                    }
                    void loadThemesStoreCatalog({
                      filter: nextFilter,
                      page: 1,
                      showLoading: false,
                    });
                  },
                  onClick: () => {
                    const currentCatalog = getThemeStoreCatalog();
                    if (currentCatalog) {
                      state.themes.storeCatalog = {
                        ...currentCatalog,
                        filter: "All",
                        page: 1,
                      };
                      syncVisibleSlotSliderUi();
                    }
                    void loadThemesStoreCatalog({
                      filter: "All",
                      page: 1,
                      showLoading: false,
                    });
                  },
                }),
                createPerformanceValueSliderSlot({
                  title: "Order",
                  copy: "Change how DeckThemes results are sorted.",
                  hint: "Use Left / Right to change sorting. Press A to reset to Most Downloaded.",
                  slotKey: "theme-store-order",
                  min: 0,
                  max: Math.max(0, availableOrders.length - 1),
                  step: 1,
                  disabled: state.themes.storeLoading || availableOrders.length <= 1,
                  getValue: () => {
                    const liveOrder = getThemeStoreCatalog()?.order || availableOrders[0] || "Most Downloaded";
                    const liveIndex = availableOrders.findIndex((value) => value === liveOrder);
                    return liveIndex >= 0 ? liveIndex : currentOrderIndex;
                  },
                  displayValue: (index) => {
                    const safeIndex = Math.max(0, Math.min(availableOrders.length - 1, index));
                    return availableOrders[safeIndex] || "Most Downloaded";
                  },
                  onAdjust: (direction) => {
                    const currentCatalog = getThemeStoreCatalog();
                    const liveOrder = getThemeStoreCatalog()?.order || availableOrders[0] || "Most Downloaded";
                    const liveIndex = Math.max(0, availableOrders.findIndex((value) => value === liveOrder));
                    const nextIndex = Math.max(0, Math.min(availableOrders.length - 1, liveIndex + direction));
                    const nextOrder = availableOrders[nextIndex] || "Most Downloaded";
                    if (currentCatalog) {
                      state.themes.storeCatalog = {
                        ...currentCatalog,
                        order: nextOrder,
                        page: 1,
                      };
                      syncVisibleSlotSliderUi();
                    }
                    void loadThemesStoreCatalog({
                      order: nextOrder,
                      page: 1,
                      showLoading: false,
                    });
                  },
                  onClick: () => {
                    const currentCatalog = getThemeStoreCatalog();
                    if (currentCatalog) {
                      state.themes.storeCatalog = {
                        ...currentCatalog,
                        order: "Most Downloaded",
                        page: 1,
                      };
                      syncVisibleSlotSliderUi();
                    }
                    void loadThemesStoreCatalog({
                      order: "Most Downloaded",
                      page: 1,
                      showLoading: false,
                    });
                  },
                }),
                makeCommandSlot(
                  "Search Store",
                  "Run the current DeckThemes search query.",
                  () => searchThemesStore(),
                  {
                    disabled: state.themes.storeLoading,
                  },
                ),
                makeCommandSlot(
                  "Clear Search",
                  "Remove the current query and show the full DeckThemes list again.",
                  () => clearThemesStoreSearch(),
                  {
                    disabled: state.themes.storeLoading || !state.themes.storeSearchDraft,
                  },
                ),
                makeCommandSlot(
                  "Refresh Store",
                  "Reload the current DeckThemes search, filters, and install status.",
                  () =>
                    loadThemesStoreCatalog({
                      search: storeCatalog?.search || "",
                      filter: currentFilter,
                      order: currentOrder,
                      page: currentPage,
                      perPage,
                    }),
                  {
                    disabled: state.themes.storeLoading,
                  },
                ),
              ]
            : []),
          createSectionSlot(
            "Themes",
            storeItems.length
              ? "Open any result to see a full preview and install it into CSSLoader."
              : "No Big Picture themes match the current filter yet.",
            "theme-store-section-results",
            true,
          ),
          ...createThemeStorePagerSlots({
            currentPage,
            totalPages,
            disabled: state.themes.storeLoading,
            onPrevious: () => loadThemesStoreCatalog({ page: currentPage - 1 }),
            onNext: () => loadThemesStoreCatalog({ page: currentPage + 1 }),
          }),
          ...storeItems.map((theme) =>
            makeFeatureNavigationSlot(
              theme.title,
              theme.description,
              () => {
                setRoute({
                  screen: "page",
                  pluginId: "themes",
                  pageId: `store-theme-${theme.storeId}`,
                });
                void loadThemesStoreTheme(theme.storeId);
              },
              {
                slotKey: getThemeStoreResultSlotKey(theme.storeId),
                leadingIcon: ThemesPluginIcon,
                mediaImageSrc: theme.previewImageUrl || theme.previewThumbnailUrl || "",
                mediaImageAlt: `${theme.title} preview`,
                eyebrow: `${theme.source || "DeckThemes"} - ${theme.version}`,
                meta: buildThemeStoreMetaItems(theme),
                footerLabel: theme.installed
                  ? theme.installedVersionMatches
                    ? "Open installed store entry"
                    : "Open update preview"
                  : "Open store entry",
                disabled: state.themes.storeLoading,
                badge: theme.installed
                  ? theme.installedVersionMatches
                    ? "Installed"
                    : "Update"
                  : "Install",
              },
            ),
          ),
          createSectionSlot(
            "Pages",
            "Move through the current results page-by-page once you reach the end of the current store view.",
            "theme-store-section-pages-bottom",
            true,
          ),
          makeCommandSlot(
            "Previous Page",
            "Go back to the previous DeckThemes results page.",
            () => loadThemesStoreCatalog({ page: currentPage - 1 }),
            {
              leadingIcon: BackIcon,
              disabled: state.themes.storeLoading || currentPage <= 1,
            },
          ),
          makeCommandSlot(
            "Next Page",
            "Open the next DeckThemes results page.",
            () => loadThemesStoreCatalog({ page: currentPage + 1 }),
            {
              leadingIcon: ChevronIcon,
              disabled: state.themes.storeLoading || currentPage >= totalPages,
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      isThemesStoreThemeRoute(state.route)
    ) {
      const integration = getThemeIntegration();
      const storeThemeId = getThemeStoreIdFromRoute(state.route);
      const storeTheme = getThemeStoreById(storeThemeId);
      const installedTheme = storeTheme ? getThemeById(storeTheme.themeId) : null;
      const installedPreset = storeTheme ? getThemeProfileById(storeTheme.themeId) : null;

      if (
        storeThemeId &&
        (!storeTheme || !storeTheme.description) &&
        !state.themes.storeLoading &&
        state.themes.storeDetailLoadingId !== storeThemeId
      ) {
        void loadThemesStoreTheme(storeThemeId);
      }

      if (!storeTheme) {
        return {
          ...defaultModel,
          title: "CSSLoader",
          subtitle: "Store Entry",
          status: themesStatus,
          error: state.themes.error,
          note: "The requested DeckThemes entry is loading or could not be found.",
          slots: [
            makeCommandSlot(
              "Refresh Store Entry",
              "Try loading this DeckThemes entry again.",
              () => loadThemesStoreTheme(storeThemeId),
              {
                disabled: state.themes.storeLoading || !storeThemeId,
              },
            ),
          ],
        };
      }

      return {
        ...defaultModel,
        title: "CSSLoader",
        subtitle: storeTheme.title,
        status: themesStatus,
        error: state.themes.error,
        note:
          storeTheme.target === "Profile"
            ? "This DeckThemes entry installs into CSSLoader as a preset and will appear under Presets after installation."
            : "Install this Big Picture theme into CSSLoader, then return to Installed Themes to turn it on or adjust its controller-ready patches.",
        cards: [buildThemeStoreSummaryCard(storeTheme)],
        slots: [
          createSectionSlot(
            "Install",
            "Use the preview above, then install or update the selected Big Picture theme.",
            "theme-store-detail-install",
          ),
          makeCommandSlot(
            storeTheme.installed
              ? storeTheme.installedVersionMatches
                ? "Reinstall from Store"
                : "Update from Store"
              : "Install from Store",
            integration?.backendReachable
              ? "Download this DeckThemes Big Picture theme and let CSSLoader install it into the CSSLoader themes folder."
              : "Start the CSSLoader backend first, then install this DeckThemes Big Picture theme from TFS.",
            () => installThemesStoreTheme(storeTheme.storeId),
            {
              disabled: state.themes.loading || state.themes.saving || state.themes.storeLoading || !integration?.backendReachable,
              badge: storeTheme.installed
                ? storeTheme.installedVersionMatches
                  ? "Installed"
                  : "Update"
                : "",
            },
          ),
          ...((installedTheme || installedPreset)
            ? [
                createSectionSlot(
                  "Local Entry",
                  "Jump from the store preview straight into the installed CSSLoader item.",
                  "theme-store-detail-local",
                  true,
                ),
              ]
            : []),
          ...(installedTheme
            ? [
                makeNavigationSlot(
                  "Open Installed Theme",
                  "Jump straight to the installed CSSLoader theme entry after reviewing the store version.",
                  () => {
                    setRoute({
                      screen: "page",
                      pluginId: "themes",
                      pageId: `theme-${installedTheme.id}`,
                    });
                  },
                  {
                    disabled: state.themes.loading || state.themes.saving,
                  },
                ),
              ]
            : []),
          ...(installedPreset
            ? [
                makeNavigationSlot(
                  "Open Installed Preset",
                  "Jump straight to the installed CSSLoader preset entry.",
                  () => {
                    setRoute({
                      screen: "page",
                      pluginId: "themes",
                      pageId: `profile-${installedPreset.id}`,
                    });
                  },
                  {
                    disabled: state.themes.loading || state.themes.saving,
                  },
                ),
              ]
            : []),
          createSectionSlot(
            "Store Actions",
            "Refresh the metadata here whenever you want the latest preview or install state.",
            "theme-store-detail-actions",
            Boolean(installedTheme || installedPreset),
          ),
          makeCommandSlot(
            "Refresh Store Entry",
            "Reload the latest DeckThemes metadata and install status for this entry.",
            () => loadThemesStoreTheme(storeTheme.storeId),
            {
              disabled: state.themes.storeLoading,
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
      const integration = getThemeIntegration();
      const installedThemes = Array.isArray(themesSnapshot?.installedThemes)
        ? themesSnapshot.installedThemes
        : [];

      if (!integration?.backendReachable) {
        return {
          ...defaultModel,
          title: "CSSLoader",
          subtitle: "Installed Themes",
          status: themesStatus,
          error: state.themes.error,
          note: integration?.backendInstalled
            ? "CSSLoader standalone backend is installed, but offline right now. Start it to bring your installed themes back online."
            : "CSSLoader standalone backend is not installed yet. Install it before trying to manage themes from TFS.",
          cards: [
            {
              title: "CSSLoader Status",
              lines: [
                integration?.backendInstalled ? "Standalone backend is installed." : "Standalone backend is missing.",
                `Backend Path: ${integration?.backendPath || "Unavailable"}`,
              ],
            },
          ],
          slots: [
            ...(!integration?.backendInstalled
              ? [
                  makeCommandSlot(
                    "Install CSSLoader Backend",
                    "Download the official standalone headless backend and let TFS manage it.",
                    () => installThemesBackend(),
                    {
                      disabled: state.themes.loading || state.themes.saving,
                    },
                  ),
                ]
              : []),
            ...(integration?.backendInstalled && !integration?.backendReachable
              ? [
                  makeCommandSlot(
                    "Start CSSLoader Backend",
                    "Launch the standalone CSSLoader backend in the background.",
                    () => startThemesBackend(),
                    {
                      disabled: state.themes.loading || state.themes.saving,
                    },
                  ),
                ]
              : []),
            makeCommandSlot(
              "Open Theme Folder",
              "Open the CSSLoader themes folder on disk.",
              () => openThemesFolder(),
              {
                disabled: state.themes.loading || state.themes.saving,
              },
            ),
            makeCommandSlot(
              "Refresh CSSLoader State",
              "Ask TFS to refresh its live view of the CSSLoader backend.",
              () => loadThemesState(),
              {
                disabled: state.themes.loading || state.themes.saving,
              },
            ),
          ],
        };
      }

      return {
        ...defaultModel,
        title: "CSSLoader",
        subtitle: "Installed Themes",
        status: themesStatus,
        error: state.themes.error,
        note:
          installedThemes.length > 0
            ? "Active themes stay grouped at the top. Disable one and it drops back into the Ready section below."
            : "No installed CSSLoader themes were found. Use the Store or drop a theme into the themes folder.",
        cards: installedThemes.length > 0
          ? [
              {
                title: "Theme Library",
                lines: [
                  `${getInstalledThemeGroups().activeThemes.length} active - ${getInstalledThemeGroups().readyThemes.length} ready`,
                  "Active themes are currently enabled in CSSLoader. Ready themes are installed locally and can be turned on anytime.",
                ],
              },
            ]
          : [],
        slots: (() => {
          if (!installedThemes.length) {
            return [];
          }

          const { activeThemes, readyThemes } = getInstalledThemeGroups();
          const buildThemeSlot = (theme, slotIndex) => {
            const preview = findInstalledThemePreview(theme);
            if (
              !preview?.imageSrc &&
              !hasInstalledThemePreviewRecord(theme.id) &&
              !state.themes.loading &&
              !state.themes.saving
            ) {
              void ensureInstalledThemePreview(theme);
            }

            return makeFeatureNavigationSlot(
              theme.title,
              theme.storeDescription || theme.description,
              () => {
                rememberCurrentRouteIndex(slotIndex);
                setRoute({
                  screen: "page",
                  pluginId: "themes",
                  pageId: `theme-${theme.id}`,
                });
              },
              {
                slotKey: `theme-installed-${theme.id}`,
                leadingIcon: ThemesPluginIcon,
                mediaImageSrc: preview?.imageSrc || "",
                mediaImageAlt: preview?.imageAlt || `${theme.title} preview`,
                eyebrow: `${theme.author} - ${theme.version}`,
                meta: [
                  theme.enabled ? "Active in CSSLoader" : "Ready in CSSLoader",
                  `${theme.options.length} basic option${theme.options.length === 1 ? "" : "s"}`,
                  theme.advancedControlCount
                    ? `${theme.advancedControlCount} advanced`
                    : `${theme.dependencyCount || 0} dependenc${theme.dependencyCount === 1 ? "y" : "ies"}`,
                ],
                footerLabel: "Open installed theme",
                disabled: state.themes.loading || state.themes.saving,
                badge: theme.enabled ? "Active" : "Ready",
              },
            );
          };

          return [
            createSectionSlot(
              "Active",
              activeThemes.length
                ? `${activeThemes.length} theme${activeThemes.length === 1 ? "" : "s"} currently enabled in CSSLoader.`
                : "No themes are active right now.",
              "themes-installed-active",
            ),
            ...activeThemes.map((theme, index) => buildThemeSlot(theme, index + 1)),
            createSectionSlot(
              "Ready",
              readyThemes.length
                ? "Installed locally and ready to enable. Disable an active theme and it lands back here."
                : "Everything local is already active.",
              "themes-installed-ready",
              true,
            ),
            ...readyThemes.map((theme, index) =>
              buildThemeSlot(theme, activeThemes.length + index + 3),
            ),
          ];
        })(),
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "themes" &&
      state.route.pageId === "profiles"
    ) {
      const integration = getThemeIntegration();
      const profiles = getThemeProfilesState();
      const installedProfiles = Array.isArray(profiles?.installedProfiles) ? profiles.installedProfiles : [];
      const selectedProfile = getThemeProfileById(profiles?.selectedProfileId);

      return {
        ...defaultModel,
        title: "CSSLoader",
        subtitle: "Presets",
        status: themesStatus,
        error: state.themes.error,
        note:
          integration?.backendReachable
            ? "Presets capture the current CSSLoader theme stack so you can save it, switch to another setup later, or refresh an existing preset after tweaking your active themes."
            : "CSSLoader needs to be running before TFS can read or update presets.",
        cards: selectedProfile
          ? [
              {
                title: "Selected Preset",
                lines: [
                  selectedProfile.title,
                  profiles?.currentSetupMatchesSelectedProfile
                    ? "Current setup matches this preset."
                    : "Current setup differs from the selected preset.",
                ],
              },
            ]
          : [
              {
                title: "No Active Preset",
                lines: ["Create or apply a CSSLoader preset to keep reusable theme setups ready."],
              },
            ],
        editor: {
          label: "New Preset Name",
          help: `Save the current CSSLoader theme stack as a reusable preset. Installed themes are read from ${themesSnapshot?.localThemesFolder || "the CSSLoader themes folder"}.`,
          value: state.themes.profileDraft,
          placeholder: "Living Room Default",
          inputKey: `theme-profile-name-${state.themes.profileDraftInputVersion}`,
          rows: 2,
          onInput: (value) => {
            state.themes.profileDraft = value;
          },
        },
        slots: [
          makeCommandSlot(
            "Save Current Setup As Preset",
            "Capture the currently enabled CSSLoader themes into a reusable preset.",
            () => createThemeProfileFromCurrentSetup(),
            {
              disabled: state.themes.loading || state.themes.saving || !integration?.backendReachable,
            },
          ),
          ...installedProfiles.map((profile, profileIndex) =>
            makeNavigationSlot(
              profile.title,
              `${profile.statusText} - ${profile.themes.length} theme${profile.themes.length === 1 ? "" : "s"}`,
              () => {
                rememberCurrentRouteIndex(profileIndex + 1);
                setRoute({
                  screen: "page",
                  pluginId: "themes",
                  pageId: `profile-${profile.id}`,
                });
              },
            {
              slotKey: `theme-profile-${profile.id}`,
              disabled: state.themes.loading || state.themes.saving,
              badge: profile.selected ? "Selected" : profile.matchesCurrentSetup ? "Current" : "Installed",
            },
          ),
          ),
          makeCommandSlot(
            "Refresh CSSLoader State",
            "Reload installed CSSLoader themes, presets, and load errors from the backend.",
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
      const integration = getThemeIntegration();
      const loadErrors = Array.isArray(integration?.loadErrors) ? integration.loadErrors : [];
      const loadErrorLines = loadErrors
        .slice(0, 3)
        .map((entry) => `${entry.title}: ${entry.message}`);

      if (loadErrors.length > 3) {
        loadErrorLines.push(`${loadErrors.length - 3} more CSSLoader load error(s) are not shown here.`);
      }

      return {
        ...defaultModel,
        title: "CSSLoader",
        subtitle: "Settings",
        status: themesStatus,
        error: state.themes.error,
        note: "TFS defers Steam theming to the standalone CSSLoader backend. Use the actions below to install, start, and manage that backend.",
        cards: [
          {
            title: "Backend Status",
            lines: [
              `Backend: ${integration?.backendReachable ? "Connected" : "Offline"}`,
              `Standalone: ${integration?.backendInstalled ? "Installed" : "Missing"}`,
              `Watch Folder: ${integration?.watchEnabled ? "Enabled" : "Disabled"}`,
              `Theme Path: ${integration?.themePath || themesSnapshot?.localThemesFolder || "Unavailable"}`,
              `Backend Path: ${integration?.backendPath || "Unavailable"}`,
              `Backend Version: ${integration?.backendVersion ?? "Unknown"}`,
            ],
          },
          ...(loadErrorLines.length
            ? [
                {
                  title: "Load Errors",
                  lines: loadErrorLines,
                },
              ]
            : []),
        ],
        slots: [
          ...(!integration?.backendInstalled
            ? [
                makeCommandSlot(
                  "Install CSSLoader Backend",
                  "Download the official standalone headless backend from DeckThemes and let TFS manage it.",
                  () => installThemesBackend(),
                  {
                    disabled: state.themes.loading || state.themes.saving,
                  },
                ),
              ]
            : []),
          ...(integration?.backendInstalled && !integration?.backendReachable
            ? [
                makeCommandSlot(
                  "Start CSSLoader Backend",
                  "Launch the standalone CSSLoader backend so TFS can read live theme state again.",
                  () => startThemesBackend(),
                  {
                    disabled: state.themes.loading || state.themes.saving,
                  },
                ),
              ]
            : []),
          makeCommandSlot(
            "Open Theme Folder",
            "Open the CSSLoader themes folder on disk.",
            () => openThemesFolder(),
            {
              disabled: state.themes.loading || state.themes.saving,
            },
          ),
          ...(integration?.backendReachable
            ? [
                makeSettingToggleSlot(
                  "themes",
                  "watch-enabled",
                  "Watch Theme Folder",
                  "Let CSSLoader watch the CSSLoader themes folder and reload when files change.",
                  Boolean(integration?.watchEnabled),
                  () => setThemesWatchEnabled(!Boolean(integration?.watchEnabled)),
                  {
                    disabled: state.themes.loading || state.themes.saving,
                  },
                ),
              ]
            : []),
          makeCommandSlot(
            "Refresh CSSLoader State",
            "Reload the current backend state, themes, presets, and load errors.",
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
              slotKey: `store-sync-store-${store.id}`,
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
                  slotKey: `store-sync-title-${title.id}`,
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

    if (
      state.route.screen === "plugin" &&
      state.route.pluginId === "smart-home"
    ) {
      const overview = getSmartHomeOverview();
      const settings = getSmartHomeSettings();
      const homey = settings?.homey;

      return {
        ...defaultModel,
        title: "Homey",
        subtitle: "Homey-first room and flow control",
        status: resolveSmartHomeStatusText(),
        error: getSmartHomeErrorText(),
        note: homey?.isConfigured
          ? "Open Rooms to expand devices with power, brightness, hue, saturation, and white temperature controls. Moods and flows are ready as quick scenes."
          : "Phase 1 uses a stored Homey Web API session token. The provider-neutral room and device UI is being shaped so Home Assistant can plug into the same foundation later.",
        sectionHeaders: [
          createSectionHeader(0, "Main Areas", "Jump into rooms, moods, flows, or the saved Homey connection.", {
            icon: SmartHomePluginIcon,
          }),
          createSectionHeader(4, "Maintenance", "Reload the Homey snapshot after changes outside TFS.", {
            icon: RefreshActionIcon,
          }),
        ],
        cards: [
          {
            title: "Discovery",
            lines: [
              formatSmartHomeCount(overview?.zoneCount || 0, "room"),
              formatSmartHomeCount(overview?.deviceCount || 0, "device"),
              formatSmartHomeCount(overview?.lightCount || 0, "light"),
              formatSmartHomeCount(overview?.flowCount || 0, "flow"),
              formatSmartHomeCount(overview?.moodCount || 0, "mood"),
            ],
          },
          {
            title: "Connection",
            lines: [
              `Provider: ${settings?.activeProviderId === "homey" ? "Homey" : settings?.activeProviderId || "Homey"}`,
              `Address: ${homey?.baseUrl || "Not saved yet"}`,
              homey?.sessionTokenConfigured ? "Session token is stored locally." : "Session token not saved yet.",
              homey?.statusText || "Waiting for connection details.",
            ],
          },
        ],
        slots: [
          makeNavigationSlot(
            "Rooms",
            `${formatSmartHomeCount(overview?.zoneCount || 0, "room")} with discoverable devices.`,
            () => {
              const targetRoute = { screen: "page", pluginId: "smart-home", pageId: "rooms" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              badge: `${overview?.zoneCount || 0}`,
              leadingIcon: SmartHomePluginIcon,
            },
          ),
          makeNavigationSlot(
            "Moods",
            `${formatSmartHomeCount(overview?.moodCount || 0, "mood")} saved as Homey room themes.`,
            () => {
              const targetRoute = { screen: "page", pluginId: "smart-home", pageId: "moods" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              badge: `${overview?.moodCount || 0}`,
              leadingIcon: SmartHomePluginIcon,
            },
          ),
          makeNavigationSlot(
            "Flows",
            `${formatSmartHomeCount(overview?.flowCount || 0, "flow")} and quick scene trigger.`,
            () => {
              const targetRoute = { screen: "page", pluginId: "smart-home", pageId: "flows" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              badge: `${overview?.flowCount || 0}`,
              leadingIcon: SmartHomePluginIcon,
            },
          ),
          makeNavigationSlot(
            "Settings",
            "Save the Homey address, optional Homey id, and current session token.",
            () => {
              const targetRoute = { screen: "page", pluginId: "smart-home", pageId: "settings" };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              leadingIcon: SettingsPluginIcon,
            },
          ),
          makeCommandSlot(
            "Refresh Homey",
            "Reload rooms, moods, devices, and flows from the active Homey connection.",
            () => refreshSmartHome(true),
            {
              disabled: isSmartHomeBusy(),
              leadingIcon: RefreshActionIcon,
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "smart-home" &&
      state.route.pageId === "rooms"
    ) {
      const zones = getSmartHomeZones();
      const unassignedDevices = getSmartHomeUnassignedDevices();
      const roomSlots = [
        ...zones.map((zone, zoneIndex) =>
          makeNavigationSlot(
            zone.name,
            buildSmartHomeRoomSummary(zone),
            () => {
              rememberCurrentRouteIndex(zoneIndex);
              const targetRoute = {
                screen: "page",
                pluginId: "smart-home",
                pageId: `room-${zone.id}`,
              };
              requestFreshEntryForRoute(targetRoute, 0, 0);
              setRoute(targetRoute);
            },
            {
              slotKey: `smart-home-room-${zone.id}`,
              badge: `${zone.deviceCount || 0}`,
              leadingIcon: SmartHomePluginIcon,
            },
          ),
        ),
        ...(unassignedDevices.length
          ? [
              makeNavigationSlot(
                "Unassigned Devices",
                `${formatSmartHomeCount(unassignedDevices.length, "device")} without a Homey room.`,
                () => {
                  const targetRoute = {
                    screen: "page",
                    pluginId: "smart-home",
                    pageId: "room-unassigned",
                  };
                  requestFreshEntryForRoute(targetRoute, 0, 0);
                  setRoute(targetRoute);
                },
                {
                  slotKey: "smart-home-room-unassigned",
                  badge: `${unassignedDevices.length}`,
                  leadingIcon: SmartHomePluginIcon,
                },
              ),
            ]
          : []),
      ];

      return {
        ...defaultModel,
        title: "Homey",
        subtitle: "Rooms",
        status: resolveSmartHomeStatusText(),
        error: getSmartHomeErrorText(),
        note: roomSlots.length
          ? "Each room opens its own expandable device list so we can keep moods, sliders, and color controls focused and controller-friendly."
          : "No Homey rooms with devices are available yet. Save your connection first, then refresh Homey.",
        sectionHeaders: [
          createSectionHeader(0, "Discovered Rooms", "Open a room to expand lights, switches, and controllable devices.", {
            icon: SmartHomePluginIcon,
          }),
        ],
        cards: [
          {
            title: "Room Summary",
            lines: [
              formatSmartHomeCount(zones.length, "room"),
              formatSmartHomeCount(getSmartHomeOverview()?.deviceCount || 0, "device"),
              formatSmartHomeCount(getSmartHomeOverview()?.moodCount || 0, "mood"),
              unassignedDevices.length ? `${unassignedDevices.length} device${unassignedDevices.length === 1 ? "" : "s"} outside a room.` : "All discovered devices are assigned to a room.",
            ],
          },
        ],
        slots: roomSlots.length
          ? roomSlots
          : [
              makeCommandSlot(
                "Refresh Homey",
                "Try another Homey refresh right now.",
                () => refreshSmartHome(true),
                {
                  disabled: isSmartHomeBusy(),
                },
              ),
            ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "smart-home" &&
      state.route.pageId === "flows"
    ) {
      const flows = getSmartHomeFlows();
      const standardFlows = flows.filter((flow) => !flow.isAdvanced);
      const advancedFlows = flows.filter((flow) => flow.isAdvanced);

      return {
        ...defaultModel,
        title: "Homey",
        subtitle: "Flows",
        status: resolveSmartHomeStatusText(),
        error: getSmartHomeErrorText(),
        note: flows.length
          ? "Use flows as scenes: movie mode, lights off, bedtime, or whole-room changes from the controller."
          : "No Homey flows were discovered yet.",
        sectionHeaders: [
          createSectionHeader(0, "Flows", "Standard Homey flows appear first.", {
            icon: SmartHomePluginIcon,
          }),
          ...(advancedFlows.length
            ? [createSectionHeader(standardFlows.length, "Advanced Flows", "Advanced flows are kept together below.", {
                icon: SmartHomePluginIcon,
              })]
            : []),
        ],
        cards: [
          {
            title: "Scene Library",
            lines: [
              formatSmartHomeCount(standardFlows.length, "flow"),
              formatSmartHomeCount(advancedFlows.length, "advanced flow", "advanced flows"),
              flows.some((flow) => flow.triggerable)
                ? "Triggerable scenes are ready."
                : "Flows are listed, but Homey did not mark any as triggerable yet.",
            ],
          },
        ],
        slots: flows.length
          ? flows.map((flow) =>
              makeCommandSlot(
                flow.name,
                flow.description || buildSmartHomeFlowSummary(flow),
                () => runSmartHomeFlow(flow.id, flow.isAdvanced),
                {
                  slotKey: `smart-home-flow-${flow.id}`,
                  disabled: isSmartHomeBusy() || !flow.triggerable,
                  badge: flow.badgeText || "",
                  leadingIcon: SmartHomePluginIcon,
                },
              ),
            )
          : [
              makeCommandSlot(
                "Refresh Homey",
                "Reload the flow list from Homey.",
                () => refreshSmartHome(true),
                {
                  disabled: isSmartHomeBusy(),
                },
              ),
            ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "smart-home" &&
      state.route.pageId === "moods"
    ) {
      const moods = getSmartHomeMoods();

      return {
        ...defaultModel,
        title: "Homey",
        subtitle: "Moods",
        status: resolveSmartHomeStatusText(),
        error: getSmartHomeErrorText(),
        note: moods.length
          ? "Homey moods behave like saved room themes. Trigger one to restage multiple lamp states at once."
          : "No Homey moods were discovered yet.",
        sectionHeaders: [
          createSectionHeader(0, "Moods", "Apply saved Homey room moods and lighting themes.", {
            icon: SmartHomePluginIcon,
          }),
        ],
        cards: [
          {
            title: "Mood Library",
            lines: [
              formatSmartHomeCount(moods.length, "mood"),
              moods.some((mood) => mood.zoneName)
                ? "Room-linked moods are ready."
                : "Moods are listed even when Homey does not attach a room name.",
            ],
          },
        ],
        slots: moods.length
          ? moods.map((mood) =>
              makeCommandSlot(
                mood.name,
                mood.description || buildSmartHomeMoodSummary(mood),
                () => runSmartHomeMood(mood.id),
                {
                  slotKey: `smart-home-mood-${mood.id}`,
                  disabled: isSmartHomeBusy(),
                  badge: mood.badgeText || "Mood",
                  leadingIcon: SmartHomePluginIcon,
                },
              ),
            )
          : [
              makeCommandSlot(
                "Refresh Homey",
                "Reload the mood list from Homey.",
                () => refreshSmartHome(true),
                {
                  disabled: isSmartHomeBusy(),
                },
              ),
            ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "smart-home" &&
      state.route.pageId === "settings"
    ) {
      const settings = getSmartHomeSettings();
      const homey = settings?.homey;

      return {
        ...defaultModel,
        title: "Homey",
        subtitle: "Settings",
        status: resolveSmartHomeStatusText(),
        error: getSmartHomeErrorText(),
        note: "Phase 1 is a practical Homey connection screen. The provider-neutral room, device, and slider models are already being shaped so we can bring Home Assistant and later a store-backed integration model into the same surface.",
        sectionHeaders: [
          createSectionHeader(0, "Connection", "Save the Homey address and a valid session token for the Web API.", {
            icon: SmartHomePluginIcon,
          }),
          createSectionHeader(5, "Provider Foundation", "Homey is live first. Home Assistant is the next target on the same UI contract.", {
            icon: SettingsPluginIcon,
          }),
        ],
        cards: [
          {
            title: "Current Homey Link",
            lines: [
              `Address: ${homey?.baseUrl || "Not saved yet"}`,
              `Homey id: ${homey?.homeyId || "Optional / empty"}`,
              homey?.sessionTokenConfigured ? "Session token is stored." : "Session token is not stored.",
              homey?.statusText || "Waiting for connection details.",
            ],
          },
          {
            title: "Provider Roadmap",
            lines: (Array.isArray(settings?.providers) ? settings.providers : []).map((provider) =>
              `${provider.title}: ${provider.supported ? "Supported now" : "Planned next"}${provider.selected ? " - selected" : ""}`,
            ),
          },
        ],
        editors: [
          {
            label: "Homey Address or IP",
            help: "Examples: homey.local, 192.168.1.42, or a full Homey Web API URL. TFS normalizes this before saving.",
            value: state.smartHome.baseUrlDraft,
            placeholder: "http://homey.local",
            rows: 2,
            inputKey: `smart-home-base-url-${state.smartHome.baseUrlInputVersion}`,
            onInput: (value) => {
              state.smartHome.baseUrlDraft = value;
            },
          },
          {
            label: "Homey Id (Optional)",
            help: "Reserved for future pairing and provider expansion. Safe to leave blank for the first Homey phase.",
            value: state.smartHome.homeyIdDraft,
            placeholder: "Optional Homey id",
            rows: 2,
            inputKey: `smart-home-homey-id-${state.smartHome.homeyIdInputVersion}`,
            onInput: (value) => {
              state.smartHome.homeyIdDraft = value;
            },
          },
          {
            label: "Session Token",
            help: "Paste the active Homey Web API session token we use for phase 1. This stays local in TFS settings.",
            value: state.smartHome.sessionTokenDraft,
            placeholder: "Paste Homey session token",
            rows: 3,
            inputKey: `smart-home-session-token-${state.smartHome.sessionTokenInputVersion}`,
            onInput: (value) => {
              state.smartHome.sessionTokenDraft = value;
            },
          },
        ],
        slots: [
          makeCommandSlot(
            "Save Address",
            "Store the normalized Homey address used for device and flow discovery.",
            () => saveSmartHomeBaseUrl(),
            {
              disabled: isSmartHomeBusy(),
              leadingIcon: SaveActionIcon,
            },
          ),
          makeCommandSlot(
            "Save Homey Id",
            "Store the optional Homey id for later pairing work and provider expansion.",
            () => saveSmartHomeHomeyId(),
            {
              disabled: isSmartHomeBusy(),
              leadingIcon: SaveActionIcon,
            },
          ),
          makeCommandSlot(
            "Save Session Token",
            "Use this token for Homey Web API discovery and control requests.",
            () => saveSmartHomeSessionToken(),
            {
              disabled: isSmartHomeBusy(),
              leadingIcon: SaveActionIcon,
            },
          ),
          makeCommandSlot(
            "Clear Session Token",
            "Forget the saved token while keeping the Homey address and provider foundation.",
            () => clearSmartHomeSessionToken(),
            {
              disabled: isSmartHomeBusy() || !homey?.sessionTokenConfigured,
              leadingIcon: DeleteActionIcon,
            },
          ),
          makeCommandSlot(
            "Refresh Connection",
            "Test the current Homey settings and reload rooms, devices, and flows.",
            () => refreshSmartHome(true),
            {
              disabled: isSmartHomeBusy(),
              leadingIcon: RefreshActionIcon,
            },
          ),
        ],
      };
    }

    if (
      state.route.screen === "page" &&
      state.route.pluginId === "smart-home" &&
      (state.route.pageId?.startsWith("room-") || state.route.pageId === "room-unassigned")
    ) {
      const roomId = getSmartHomeRoomRouteId(state.route);
      const room = roomId === "unassigned"
        ? null
        : getSmartHomeZone(roomId);
      const roomMoods = room ? getSmartHomeZoneMoods(room.id) : [];
      const devices = roomId === "unassigned"
        ? getSmartHomeUnassignedDevices()
        : Array.isArray(room?.devices) ? room.devices : [];
      const sectionPrefix = roomId || "unassigned";
      const moodSlots = roomMoods.map((mood) =>
        makeCommandSlot(
          mood.name,
          mood.description || buildSmartHomeMoodSummary(mood),
          () => runSmartHomeMood(mood.id),
          {
            slotKey: `smart-home-room-mood-${mood.id}`,
            disabled: isSmartHomeBusy(),
            badge: mood.badgeText || "Mood",
            leadingIcon: SmartHomePluginIcon,
          },
        ),
      );
      const deviceSlots = devices.flatMap((device) => {
        const expandedSectionKey = `smart-home-device-${sectionPrefix}-${device.id}`;
        const expanded = isExpandedSection(expandedSectionKey, false);
        const headerSlot = makeAccordionSlot(
          device.name,
          buildSmartHomeDeviceCopy(device),
          expanded,
          () => {
            toggleExpandedSection(expandedSectionKey, false);
            rerenderSmartHomePanel();
          },
          {
            slotKey: `smart-home-device-${device.id}`,
            badge: !device.available ? "Offline" : device.isOn ? "On" : "Off",
            leadingIcon: SmartHomePluginIcon,
            swatchHex: device.swatchHex || "",
            swatchLabel: buildSmartHomeDeviceSwatchLabel(device),
          },
        );

        if (!expanded) {
          return [headerSlot];
        }

        const controls = Array.isArray(device.controls) ? device.controls : [];
        const controlSlots = controls.length
          ? controls.map((control) => {
              if (control.kind === "switch") {
                return makeSettingToggleSlot(
                  "smart-home",
                  `${device.id}:${control.capabilityId}`,
                  control.title,
                  control.copy,
                  Boolean(control.booleanValue),
                  () => toggleSmartHomeDevicePower(device.id, control.booleanValue),
                  {
                    disabled: isSmartHomeBusy() || !control.enabled,
                    badge: control.booleanValue ? "On" : "Off",
                    leadingIcon: SmartHomePluginIcon,
                  },
                );
              }

              const sliderStyle = buildSmartHomeSliderStyle(device, control);
              return createRichValueSliderSlot({
                title: control.title,
                copy: control.copy,
                hint: "Use Left / Right to adjust this value. Homey sends it after a short pause.",
                slotKey: `smart-home-control-${device.id}-${control.capabilityId}`,
                min: control.min ?? 0,
                max: control.max ?? 100,
                step: control.step ?? 5,
                disabled: isSmartHomeBusy() || !control.enabled,
                trackStyle: sliderStyle.trackStyle,
                fillStyle: sliderStyle.fillStyle,
                thumbStyle: sliderStyle.thumbStyle,
                getValue: () => getSmartHomeControl(device.id, control.capabilityId)?.numericValue ?? control.numericValue,
                displayValue: (value) => formatSmartHomeSliderValue(control.accent, value),
                onAdjust: (direction) => {
                  stepSmartHomeCapability(device.id, control.capabilityId, direction);
                },
              });
            })
          : [
              makeCommandSlot(
                "No Quick Controls Yet",
                "This Homey device was discovered, but phase 1 does not expose one of its capabilities yet.",
                () => {},
                {
                  disabled: true,
                  leadingIcon: SmartHomePluginIcon,
                },
              ),
            ];

        return [headerSlot, ...controlSlots];
      });

      return {
        ...defaultModel,
        title: "Homey",
        subtitle: roomId === "unassigned" ? "Unassigned Devices" : room?.name || "Room",
        status: resolveSmartHomeStatusText(),
        error: getSmartHomeErrorText(),
        note: roomId === "unassigned"
          ? "These devices are available from Homey but are not mapped to a room right now."
          : `${room?.path || room?.name || "Room"} - expand a device to reach power, color, and brightness controls.`,
        sectionHeaders: [
          ...(moodSlots.length
            ? [createSectionHeader(0, "Moods", "Apply the saved Homey room themes before fine-tuning devices.", {
                icon: SmartHomePluginIcon,
              })]
            : []),
          createSectionHeader(moodSlots.length, "Devices", "Expand a device to reveal the available quick controls.", {
            icon: SmartHomePluginIcon,
          }),
        ],
        cards: [
          {
            title: roomId === "unassigned" ? "Unassigned Devices" : room?.name || "Room",
            lines: [
              room ? buildSmartHomeRoomSummary(room) : formatSmartHomeCount(devices.length, "device"),
              roomMoods.length ? formatSmartHomeCount(roomMoods.length, "mood") : null,
              room?.path && room?.path !== room?.name ? `Path: ${room.path}` : null,
              devices.some((device) => device.supportsColor)
                ? "Color-capable lamps show their current swatch directly in the list."
                : "Expand a device to reach its supported quick controls.",
            ].filter(Boolean),
          },
        ],
        slots: moodSlots.length || deviceSlots.length
          ? [...moodSlots, ...deviceSlots]
          : [
              makeCommandSlot(
                "Refresh Homey",
                "Reload this room from Homey.",
                () => refreshSmartHome(true),
                {
                  disabled: isSmartHomeBusy(),
                },
              ),
            ],
      };
    }

    if (storefrontEnabled && state.route.screen === "plugin" && state.route.pluginId === "unifystore") {
      const stores = getUnifySteamStores();
      const installedCount = stores.reduce((total, store) => total + (Number(store.installedCount) || 0), 0);
      const libraryCount = stores.reduce((total, store) => total + (Number(store.availableCount) || 0), 0);
      const authEditors = stores
        .filter((store) => store.supportsManualCodeAuth)
        .map((store) => {
          const inputVersion = state.storeSync.unifySteamAuthInputVersionByStoreId[store.id] || 0;
          return {
            label: `${store.title || "Store"} Login Code`,
            help: store.id === "epic-games"
              ? "After Epic login, paste the authorizationCode JSON, the code, or the final page URL here."
              : "After GOG login, paste the final URL or code here.",
            value: state.storeSync.unifySteamAuthDraftByStoreId[store.id] || "",
            placeholder: store.id === "epic-games"
              ? '{"authorizationCode":"..."}'
              : "https://embed.gog.com/on_login_success?code=...",
            inputKey: `unifystore-login-code-${store.id}-${inputVersion}`,
            rows: 2,
            onInput: (value) => {
              state.storeSync.unifySteamAuthDraftByStoreId[store.id] = value;
            },
          };
        });
      const storeSlots = stores.flatMap((store) => [
        makeCommandSlot(
          `${store.title || "Store"} Login`,
          store.authReady
            ? `Signed in${store.accountName ? ` as ${store.accountName}` : ""}.`
            : store.detailText || "Open the store login flow.",
          () => startUnifyStoreLogin(store.id),
          {
            slotKey: `unifystore-login-${store.id}`,
            disabled: isStoreSyncBusy(),
            leadingIcon: StoreSyncPluginIcon,
          },
        ),
        makeCommandSlot(
          `Save ${store.title || "Store"} Login Code`,
          "Paste the browser login result above, then save it locally for Storefront.",
          () => submitUnifySteamAuthCode(store.id),
          {
            slotKey: `unifystore-save-login-${store.id}`,
            disabled: isStoreSyncBusy() || !store.supportsManualCodeAuth,
            leadingIcon: SaveActionIcon,
          },
        ),
        makeCommandSlot(
          `Refresh ${store.title || "Store"}`,
          buildUnifySteamStoreCopy(store),
          () => refreshUnifyStore(store.id),
          {
            slotKey: `unifystore-refresh-${store.id}`,
            disabled: isStoreSyncBusy() || !store.enabled,
            leadingIcon: RefreshActionIcon,
          },
        ),
      ]);

      return {
        ...defaultModel,
        title: "Storefront",
        subtitle: "Epic and GOG in one fullscreen launcher",
        status: storeSyncStatus,
        error: state.storeSync.error,
        note: "Open the fullscreen surface to browse stores with LB/RB, mark installed games, and install or launch directly.",
        editors: authEditors.length ? authEditors : null,
        cards: [
          {
            title: "Storefront",
            lines: [
              `${installedCount} installed / ${libraryCount} in account libraries`,
              getUnifySteamSnapshot()?.statusText || "Waiting for Epic or GOG setup.",
              getUnifySteamSnapshot()?.detailText || "Login and refresh to populate the fullscreen launcher.",
            ].filter(Boolean),
          },
        ],
        autoFocusIndex: resolveAutoFocusIndex(state.route) ?? 0,
        sectionHeaders: [
          createSectionHeader(0, "Launcher", "Jump into the dedicated fullscreen library.", {
            icon: StoreSyncPluginIcon,
          }),
          createSectionHeader(2, "Accounts", "Login and refresh the store libraries used by Storefront.", {
            icon: RefreshActionIcon,
          }),
        ],
        dividerAfterIndices: [1],
        slots: [
          makeCommandSlot(
            "Open Fullscreen",
            "Launch Storefront with controller navigation, Epic/GOG tabs, and install-ready game cards.",
            () => openUnifyStoreOverlay(),
            {
              slotKey: "unifystore-open-fullscreen",
              disabled: isStoreSyncBusy(),
              leadingIcon: StoreSyncPluginIcon,
            },
          ),
          makeCommandSlot(
            "Refresh Libraries",
            "Reload Epic and GOG account libraries before opening the fullscreen launcher.",
            () => refreshUnifyStore(),
            {
              slotKey: "unifystore-refresh-all",
              disabled: isStoreSyncBusy(),
              leadingIcon: RefreshActionIcon,
            },
          ),
          ...(storeSlots.length
            ? storeSlots
            : [
                makeCommandSlot(
                  "Load Store State",
                  "Reload Store Sync so Epic and GOG account status can appear here.",
                  () => loadStoreSyncState(),
                  {
                    slotKey: "unifystore-load-state",
                    disabled: isStoreSyncBusy(),
                    leadingIcon: RefreshActionIcon,
                  },
                ),
              ]),
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
                ? "Use Installed Themes, Store, Presets, and Settings to control CSSLoader from Quick Access."
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
      : state.communityPlugins.loading
        ? "Loading community plugins..."
      : state.communityPlugins.error
        ? state.communityPlugins.error
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
          title: "Store",
          icon: HeaderStoreIcon,
          disabled: state.homeReorder.active || state.generalSettings.saving,
          buttonStyle: {
            width: "30px",
            height: "30px",
            minWidth: "30px",
            minHeight: "30px",
            padding: "0",
          },
          onClick: () => {
            void openPluginStoreOverlay();
          },
        },
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
          if (plugin.id === "unifystore") {
            void openUnifyStoreOverlay();
            return;
          }

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
    const currentPanel = getPanelScrollContainer();
    const currentScrollTop = currentPanel?.scrollTop;
    if (Number.isFinite(currentScrollTop) && hasPanelLayout(currentPanel)) {
      state.lastScrollTopByRoute[previousRouteKey] = Math.max(0, currentScrollTop);
    }

    if (route?.pluginId && !isPluginEnabled(route.pluginId)) {
      route = parseRoute("root");
    }

    const nextRouteKey = getRouteKey(route);
    const shouldFocusGlobalBackOnEntry =
      previousRouteKey !== nextRouteKey &&
      route?.screen !== "root" &&
      Boolean(getBackNavigation(route));
    const hasExplicitScrollRestore =
      state.pendingScrollRouteKey === nextRouteKey &&
      Number.isFinite(state.pendingScrollTop);
    if (!hasExplicitScrollRestore && !shouldFocusGlobalBackOnEntry) {
      requestScrollRestoreForRoute(
        route,
        previousRouteKey === nextRouteKey && Number.isFinite(currentScrollTop)
          ? currentScrollTop
          : null,
      );
    }

    if (shouldFocusGlobalBackOnEntry) {
      requestFreshEntryForRoute(route, 0, 0, globalBackSlotKey);
    } else {
      requestRouteEntryFocus(route);
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

    if (previousRouteKey !== nextRouteKey) {
      state.audio.pendingVolumeActionAutoFocus = isAudioVolumePage;
    } else if (!isAudioVolumePage) {
      state.audio.pendingVolumeActionAutoFocus = false;
    }
    if (state.audio.pendingVolumeActionAutoFocus) {
      state.audio.activeVolumeActionIndex = 0;
    }

    if (previousRouteKey !== nextRouteKey) {
      state.performance.pendingSliderAutoFocus = isPerformanceOverlayPage;
    } else if (!isPerformanceOverlayPage) {
      state.performance.pendingSliderAutoFocus = false;
    }

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
      route.pluginId === "handheld-performance" &&
      state.generalSettings.snapshot?.handheldPerformanceAvailable === true &&
      !state.handheldPerformance.loading &&
      !state.handheldPerformance.snapshot &&
      !state.handheldPerformance.error
    ) {
      void loadHandheldPerformanceState();
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
      route.pluginId === "smart-home" &&
      !state.smartHome.loading &&
      !state.smartHome.snapshot &&
      !state.smartHome.error
    ) {
      void loadSmartHomeState();
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
    updateSmartHomePolling();
    updateUpdatesPolling();
    updateHomeReorderInputCapture();

    refreshQuickAccessPanel();
    queuePendingScrollRestore();
    queuePendingFocusRestore(state.route);
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
      if (!hasConnectedLiveUpdates()) {
        await loadAudioDashboardState({ showLoading: false });
      }
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
      if (!hasConnectedLiveUpdates()) {
        await loadAudioDashboardState({ showLoading: false });
      }
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
    if (isAudioDashboardRoute()) {
      refreshAudioDashboardUi();
    } else {
      refreshAudioVolumePanel();
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

      if (requestId === state.audio.volumeMutationSequence) {
        const responseVolume = snapVolumeToStep(responsePayload?.volume);
        if (canApplyOptimisticResponse("audio.playback.volume", responseVolume)) {
          state.audio.volumeInfo =
            responsePayload && typeof responsePayload === "object" ? responsePayload : null;
          clearOptimisticDesiredValue("audio.playback.volume", responseVolume);
        }
      }
    } catch (error) {
      if (requestId === state.audio.volumeMutationSequence) {
        state.audio.volumeError = error instanceof Error ? error.message : String(error);
        clearOptimisticDesiredValue("audio.playback.volume");
      }
    } finally {
      if (requestId === state.audio.volumeMutationSequence) {
        state.audio.volumeLoading = false;
        if (isAudioDashboardRoute()) {
          refreshAudioDashboardUi();
        } else {
          refreshAudioVolumePanel();
        }
      }
    }
  }

  async function setVolume(volume) {
    const nextValue = snapVolumeToStep(volume);
    setOptimisticDesiredValue("audio.playback.volume", nextValue);
    const info = state.audio.volumeInfo;
    if (info) {
      state.audio.volumeInfo = {
        ...info,
        volume: nextValue,
        isMuted: nextValue <= 0 ? true : false,
      };
      if (isAudioDashboardRoute()) {
        refreshAudioDashboardUi();
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
        refreshAudioDashboardUi();
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
      refreshAudioDashboardUi();
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
        const responseVolume = snapVolumeToStep(responsePayload?.volume);
        if (canApplyOptimisticResponse("audio.capture.volume", responseVolume)) {
          state.audio.captureVolumeInfo =
            responsePayload && typeof responsePayload === "object" ? responsePayload : null;
          clearOptimisticDesiredValue("audio.capture.volume", responseVolume);
        }
      }
    } catch (error) {
      if (requestId === state.audio.captureVolumeMutationSequence) {
        state.audio.captureVolumeError = error instanceof Error ? error.message : String(error);
        clearOptimisticDesiredValue("audio.capture.volume");
      }
    } finally {
      if (requestId === state.audio.captureVolumeMutationSequence) {
        state.audio.captureVolumeLoading = false;
        if (isAudioDashboardRoute()) {
          refreshAudioDashboardUi();
        }
      }
    }
  }

  async function setCaptureVolume(volume) {
    const nextValue = snapVolumeToStep(volume);
    setOptimisticDesiredValue("audio.capture.volume", nextValue);
    const info = state.audio.captureVolumeInfo;
    if (info) {
      state.audio.captureVolumeInfo = {
        ...info,
        volume: nextValue,
        isMuted: nextValue <= 0 ? true : false,
      };
      if (isAudioDashboardRoute()) {
        refreshAudioDashboardUi();
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
        refreshAudioDashboardUi();
      }
    }

    await performCaptureVolumeAction("api/audio/capture/volume/toggle-mute");
  }

  function rerenderAudioDashboard() {
    if (isAudioDashboardRoute()) {
      renderPanelDataRefresh();
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
      applyAudioDashboardSnapshotIfCurrent(payload);
    } catch (error) {
      state.audio.dashboardError = error instanceof Error ? error.message : String(error);
    } finally {
      state.audio.dashboardLoading = false;
      if (showLoading) {
        rerenderAudioDashboard();
      } else {
        refreshAudioDashboardUi();
      }
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
      if (showLoading) {
        rerenderAudioMixerPanel();
      } else {
        refreshAudioMixerUi();
      }
    }
  }

  async function setAudioMixerSessionVolume(sessionId, volume, options = {}) {
    if (!sessionId) {
      return;
    }

    const nextValue = snapAudioMixerVolumeToStep(volume);
    setOptimisticDesiredValue(`audio.mixer.${sessionId}.volume`, nextValue);
    const requestId = (state.audio.mixerMutationSequenceById[sessionId] || 0) + 1;
    state.audio.mixerMutationSequenceById[sessionId] = requestId;
    state.audio.mixerError = "";

    if (options.optimistic !== false) {
      previewAudioMixerSessionVolume(sessionId, nextValue);
    } else {
      refreshAudioMixerUi();
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
        const responseVolume = snapAudioMixerVolumeToStep(payload?.volume ?? nextValue);
        if (canApplyOptimisticResponse(`audio.mixer.${sessionId}.volume`, responseVolume)) {
          upsertAudioMixerSession(payload && typeof payload === "object" ? payload : null);
          clearOptimisticDesiredValue(`audio.mixer.${sessionId}.volume`, responseVolume);
        }
      }
    } catch (error) {
      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        state.audio.mixerError = error instanceof Error ? error.message : String(error);
        clearOptimisticDesiredValue(`audio.mixer.${sessionId}.volume`);
      }

      void loadAudioMixerSessions({ showLoading: false });
    } finally {
      if (requestId === state.audio.mixerMutationSequenceById[sessionId]) {
        refreshAudioMixerUi();
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
      refreshAudioMixerUi();
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
        refreshAudioMixerUi();
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

  function supportsLiveUpdates() {
    return typeof EventSource === "function";
  }

  function hasConnectedLiveUpdates() {
    return supportsLiveUpdates() && state.liveUpdates.connected;
  }

  function shouldUseLiveUpdatePollingFallback() {
    return !supportsLiveUpdates() || !hasConnectedLiveUpdates();
  }

  function clearLiveUpdateRetryTimer() {
    if (state.liveUpdates.retryTimer) {
      window.clearTimeout(state.liveUpdates.retryTimer);
      state.liveUpdates.retryTimer = 0;
    }
  }

  function closeLiveUpdateSource() {
    const source = state.liveUpdates.source;
    if (source) {
      source.onopen = null;
      source.onmessage = null;
      source.onerror = null;

      try {
        source.close();
      } catch {
      }
    }

    state.liveUpdates.source = null;
    state.liveUpdates.connected = false;
  }

  function scheduleLiveUpdateReconnect(delayMs = 3000) {
    if (!supportsLiveUpdates() || state.liveUpdates.retryTimer || state.liveUpdates.source) {
      return;
    }

    state.liveUpdates.retryTimer = window.setTimeout(() => {
      state.liveUpdates.retryTimer = 0;
      ensureLiveUpdateConnection();
    }, delayMs);
  }

  function refreshLiveUpdatePollingFallbacks() {
    updateProcessesPolling();
    updateAudioMixerPolling();
    updateStoreSyncPolling();
    updateSmartHomePolling();
    updateUpdatesPolling();
  }

  function isUpdatesVisibleRoute() {
    return (
      state.route?.screen === "root" ||
      (state.route?.screen === "page" &&
        state.route?.pluginId === "settings" &&
        state.route?.pageId === "updates")
    );
  }

  function isGeneralSettingsVisibleRoute(route = state.route) {
    return route?.pluginId === "settings" || route?.screen === "root";
  }

  function refreshCurrentLiveRouteState() {
    if (!state.panelVisible) {
      return;
    }

    if (state.route?.pluginId === "processes") {
      if (!state.processes.loading && !state.processes.activating) {
        void loadProcessesState({ showLoading: false });
      }
      return;
    }

    if (state.route?.pluginId === "audio") {
      if (
        isAudioDashboardRoute() &&
        !state.audio.dashboardLoading &&
        !state.audio.volumeLoading &&
        !state.audio.captureVolumeLoading &&
        !hasPendingAudioMixerCommits() &&
        !state.audio.volumeCommitTimer &&
        !state.audio.captureVolumeCommitTimer
      ) {
        void loadAudioDashboardState({ showLoading: false });
      } else if (isAudioMixerRoute() && !state.audio.mixerLoading && !hasPendingAudioMixerCommits()) {
        void loadAudioMixerSessions({ showLoading: false });
      }
      return;
    }

    if (state.route?.pluginId === "store-sync") {
      if (!isStoreSyncBusy() && !hasRouteTextInputFocus()) {
        void loadStoreSyncState({ showLoading: false, preserveDrafts: true });
      }
      return;
    }

    if (state.route?.pluginId === "performance") {
      if (!isPerformanceBusy()) {
        void loadPerformanceState({ showLoading: false });
      }
      return;
    }

    if (state.route?.pluginId === "app-start") {
      if (!isAppStartBusy()) {
        void loadAppStartState({ showLoading: false });
      }
      return;
    }

    if (state.route?.pluginId === "smart-home") {
      if (!isSmartHomeBusy()) {
        void loadSmartHomeState({ force: false, showLoading: false });
      }
      return;
    }

    if (isGeneralSettingsVisibleRoute()) {
      if (!state.generalSettings.loading && !state.generalSettings.saving) {
        void loadGeneralSettingsState({ showLoading: false });
      }

      if (isUpdatesVisibleRoute() && !state.updates.loading && !state.updates.saving) {
        void loadUpdateState({ force: false, showLoading: false });
      }
      return;
    }

    if (isUpdatesVisibleRoute() && getUpdateSnapshot()?.installInProgress) {
      if (!state.updates.loading && !state.updates.saving) {
        void loadUpdateState({ force: false, showLoading: false });
      }
    }
  }

  function applyLiveUpdatePayload(topic, payload) {
    if (!isSnapshotObject(payload)) {
      return false;
    }

    switch (topic) {
      case "handheld-performance.state": {
        const previous = state.handheldPerformance.snapshot;
        const hasPendingSliderCommit = Boolean(
          state.handheldPerformance.tdpCommitTimer ||
          Object.values(state.handheldPerformance.globalTdpCommitTimers).some(Boolean) ||
          Object.values(state.handheldPerformance.profileTdpCommitTimers).some(Boolean),
        );
        const gameChanged = previous?.currentGame?.key !== payload?.currentGame?.key;
        const powerSourceChanged = previous?.powerSource !== payload?.powerSource;
        const profileKeysChanged = (previous?.profiles || []).map((profile) => profile.key).join("|") !==
          (payload?.profiles || []).map((profile) => profile.key).join("|");

        state.handheldPerformance.snapshot = hasPendingSliderCommit && previous
          ? {
              ...previous,
              telemetry: payload.telemetry,
              powerSource: payload.powerSource,
              statusText: payload.statusText,
              errorText: payload.errorText,
            }
          : payload;

        if (state.panelVisible && state.route?.pluginId === "handheld-performance") {
          if (gameChanged || powerSourceChanged || profileKeysChanged) {
            renderPanelDataRefresh();
          } else {
            refreshHandheldPerformanceLiveUi();
          }
        }
        return true;
      }
      case "audio.dashboard":
      case "audio.mixer":
      applyAudioDashboardSnapshotIfCurrent(payload);
        if (state.panelVisible && state.route?.pluginId === "audio") {
          if (isSystemVolumeRoute()) {
            refreshAudioVolumePanel();
          } else if (isAudioDashboardRoute()) {
            refreshAudioDashboardUi();
          } else if (isAudioMixerRoute()) {
            refreshAudioMixerUi();
          } else {
            rerenderAudioDashboard();
          }
        }
        return true;
      case "processes.state":
        setProcessesSnapshot(payload);
        if (state.panelVisible && state.route?.pluginId === "processes") {
          rerenderProcessesPanel();
        }
        return true;
      case "store-sync.state": {
        const preserveDrafts = hasRouteTextInputFocus() || isStoreSyncBusy();
        setStoreSyncSnapshot(payload, {
          preserveDrafts,
          forceDraftSync: !preserveDrafts,
        });
        if (state.panelVisible && state.route?.pluginId === "store-sync" && !hasRouteTextInputFocus()) {
          rerenderStoreSyncPanel();
        }
        return true;
      }
      case "settings.state": {
        setGeneralSettingsSnapshot(payload, {
          forceDraftSync: !hasRouteTextInputFocus(),
        });
        if (state.panelVisible && isGeneralSettingsVisibleRoute()) {
          rerenderGeneralSettingsPanel();
        }
        return true;
      }
      case "plugin-store.state":
        void loadCommunityPluginsState({ showLoading: false });
        if (state.panelVisible && state.route?.screen === "root") {
          rerenderHomePanel();
        }

        return true;
      case "updates.state":
        setUpdateSnapshot(payload);
        updateUpdatesPolling();
        if (state.panelVisible && isUpdatesVisibleRoute()) {
          rerenderGeneralSettingsPanel();
        }
        return true;
      case "performance.state": {
        const shouldUseLiveRefresh =
          shouldSyncLivePerformancePanel() ||
          (isPerformanceOverlayRoute() && state.performance.suppressNextLivePanelRerender);
        if (!applyPerformanceSnapshotIfCurrent(payload)) {
          return true;
        }

        state.performance.suppressNextLivePanelRerender = false;
        if (state.panelVisible && state.route?.pluginId === "performance") {
          if (shouldUseLiveRefresh) {
            refreshPerformancePanel();
          } else {
            rerenderPerformancePanel();
          }
        }
        return true;
      }
      case "app-start.state":
        setAppStartSnapshot(payload);
        if (state.panelVisible && state.route?.pluginId === "app-start") {
          rerenderAppStartPanel();
        }
        return true;
      case "smart-home.state":
        setSmartHomeSnapshot(payload, {
          forceDraftSync: !hasRouteTextInputFocus(),
        });
        if (state.panelVisible && state.route?.pluginId === "smart-home") {
          rerenderSmartHomePanel();
        }
        return true;
      default:
        return false;
    }
  }

  function handleLiveUpdateMessage(message) {
    const topic = typeof message?.topic === "string" ? message.topic : "";
    if (!topic) {
      return;
    }

    if (applyLiveUpdatePayload(topic, message?.payload)) {
      return;
    }

    switch (topic) {
      case "audio.dashboard":
      case "audio.mixer":
      case "processes.state":
      case "store-sync.state":
      case "updates.state":
      case "settings.state":
      case "performance.state":
      case "app-start.state":
      case "smart-home.state":
      case "plugin-store.state":
      case "handheld-performance.state":
        refreshCurrentLiveRouteState();
        return;
      default:
        return;
    }
  }

  function ensureLiveUpdateConnection() {
    if (!supportsLiveUpdates() || state.liveUpdates.source) {
      return;
    }

    clearLiveUpdateRetryTimer();

    try {
      const eventUrl = typeof window.__steamLoaderApiUrl === "function"
        ? window.__steamLoaderApiUrl("api/events")
        : `${apiBase}api/events`;
      const source = new EventSource(eventUrl);
      state.liveUpdates.source = source;
      state.liveUpdates.connected = false;

      source.onopen = () => {
        if (state.liveUpdates.source !== source) {
          return;
        }

        state.liveUpdates.connected = true;
        refreshLiveUpdatePollingFallbacks();
        refreshCurrentLiveRouteState();
      };

      source.onmessage = (event) => {
        if (state.liveUpdates.source !== source) {
          return;
        }

        state.liveUpdates.connected = true;
        state.liveUpdates.lastMessageAt = Date.now();

        try {
          handleLiveUpdateMessage(JSON.parse(event.data));
        } catch {
        }
      };

      source.onerror = () => {
        if (state.liveUpdates.source !== source) {
          return;
        }

        state.liveUpdates.connected = false;
        refreshLiveUpdatePollingFallbacks();

        if (source.readyState === EventSource.CLOSED) {
          closeLiveUpdateSource();
          scheduleLiveUpdateReconnect();
        }
      };
    } catch {
      state.liveUpdates.connected = false;
      refreshLiveUpdatePollingFallbacks();
      scheduleLiveUpdateReconnect(5000);
    }
  }

  function updateProcessesPolling() {
    if (window.__steamToolsProcessesPollTimer) {
      window.clearInterval(window.__steamToolsProcessesPollTimer);
      window.__steamToolsProcessesPollTimer = null;
    }

    if (state.route.pluginId !== "processes" || !shouldUseLiveUpdatePollingFallback()) {
      return;
    }

    window.__steamToolsProcessesPollTimer = window.setInterval(() => {
      if (!state.processes.loading && !state.processes.activating) {
        void loadProcessesState({ showLoading: false });
      }
    }, 2500);
  }

  function updateAudioMixerPolling() {
    if (window.__steamToolsAudioMixerPollTimer) {
      window.clearInterval(window.__steamToolsAudioMixerPollTimer);
      window.__steamToolsAudioMixerPollTimer = null;
    }

    if (state.route?.pluginId !== "audio" || !shouldUseLiveUpdatePollingFallback()) {
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

    if (state.route?.pluginId !== "store-sync" || !shouldUseLiveUpdatePollingFallback()) {
      return;
    }

    const pageId = state.route?.pageId || "";
    if (/^(detected-title-|store-)/.test(pageId)) {
      return;
    }

    window.__steamToolsStoreSyncPollTimer = window.setInterval(() => {
      if (!isStoreSyncBusy() && !hasRouteTextInputFocus()) {
        void loadStoreSyncState({ showLoading: false, preserveDrafts: true });
      }
    }, 10000);
  }

  function updateSmartHomePolling() {
    if (window.__steamToolsSmartHomePollTimer) {
      window.clearInterval(window.__steamToolsSmartHomePollTimer);
      window.__steamToolsSmartHomePollTimer = null;
    }

    if (state.route?.pluginId !== "smart-home" || !shouldUseLiveUpdatePollingFallback()) {
      return;
    }

    window.__steamToolsSmartHomePollTimer = window.setInterval(() => {
      if (!isSmartHomeBusy() && !hasRouteTextInputFocus()) {
        void loadSmartHomeState({ force: false, showLoading: false });
      }
    }, 6000);
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

    if (state.pendingScrollRouteKey === getRouteKey(state.route)) {
      queuePendingScrollRestore();
    }

    queuePendingFocusRestore(state.route);
  }

  function refreshQuickAccessPanel() {
    install();
  }

  function createInstalledPanelElement(panelKey, revision = state.renderRevision) {
    return createElement(SteamLoaderPanelShell, {
      key: panelKey,
      __steamLoaderRevision: revision,
    });
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

    // Use persistent state to determine what's actually installed in the React tree.
    // We cannot rely on tab.panel.key because soundtrackTab is a fresh object on every
    // Steam re-render — tab.panel is always undefined from Steam's perspective.
    const currentRouteKey = getRouteKey(state.route);
    const expectedPanelKey = `steamloader-panel-${currentRouteKey}`;
    const isRouteChange = state.installedPanelKey !== expectedPanelKey;
    const isRevisionChange = state.installedPanelRevision !== state.renderRevision;

    if (isRouteChange) {
      // Route changed or first injection: prepare scroll/focus state, then mount fresh panel.
      preparePanelReplacement();
      state.installedPanelKey = expectedPanelKey;
      state.installedPanelRevision = state.renderRevision;
      state.installedPanelElement = createInstalledPanelElement(expectedPanelKey, state.renderRevision);
      tab.panel = state.installedPanelElement;
      changed = true;
      queuePendingScrollRestore();
      queuePendingFocusRestore(state.route);
      queuePendingEditorFocusRestore();
    } else if (isRevisionChange) {
      // Same route, SteamLoader state changed: update props, keep same key.
      // React reconciles (DOM-diff) — no unmount, no flicker, focus preserved.
      state.installedPanelRevision = state.renderRevision;
      state.installedPanelElement = createInstalledPanelElement(expectedPanelKey, state.renderRevision);
      tab.panel = state.installedPanelElement;
      changed = true;
    } else {
      // Steam's own re-render (clock tick, notification, etc.) with no SteamLoader state
      // change. Reuse the existing panel element so React can keep the subtree stable.
      if (!state.installedPanelElement) {
        state.installedPanelElement = createInstalledPanelElement(expectedPanelKey, state.renderRevision);
      }
      tab.panel = state.installedPanelElement;
      // changed stays false — prevents a redundant invalidate() call.
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
    ensureLiveUpdateConnection();
    setupPluginStoreBridge();
    ensureVolumeSliderHotkeys();
    ensureAudioDashboardHotkeys();
    ensurePerformanceSliderHotkeys();
    ensureHomeReorderHotkeys();
    ensureFocusRepairTimer();
    ensureFocusRepairHandler();
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

    if (!state.communityPlugins.loading && !state.communityPlugins.snapshot && !state.communityPlugins.error) {
      void loadCommunityPluginsState({ showLoading: false });
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

  window.__steamLoaderPluginStoreOverlayBridge = {
    togglePluginEnabled,
    refreshSettings: () => loadGeneralSettingsState({ showLoading: false }),
    refreshCommunityPlugins: () => loadCommunityPluginsState({ showLoading: false }),
    refreshVisiblePanel: () => {
      state.renderRevision += 1;
      renderPanelState();
    },
  };

  return install() ? "injected" : "waiting";
})();
