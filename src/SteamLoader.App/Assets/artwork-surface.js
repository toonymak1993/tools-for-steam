(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const version = 64;
  const openRequestStorageKey = "ToolsForSteamArtworkOpenRequest";
  const inputStorageKey = "ToolsForSteamArtworkInput";
  const steamInputRelayStorageKey = "ToolsForSteamArtworkSteamInputRelay";
  const overlayStateStorageKey = "ToolsForSteamArtworkOverlayState";
  const artworkChannelName = "ToolsForSteamArtworkChannel";
  const omniLibraryStoreChannelName = "ToolsForSteamOmniLibraryStores";
  const omniLibraryUninstallNoticeId = "steamtools-omnilibrary-uninstall-notice";
  const localizedText = (...codes) => String.fromCharCode(...codes);
  const localizedCommands = Object.freeze({
    play: localizedText(115, 112, 105, 101, 108, 101, 110),
    addToFavorites: localizedText(122, 117, 32, 102, 97, 118, 111, 114, 105, 116, 101, 110, 32, 104, 105, 110, 122, 117, 102, 117, 103, 101, 110),
    addTo: localizedText(104, 105, 110, 122, 117, 102, 117, 103, 101, 110, 32, 122, 117),
    manage: localizedText(118, 101, 114, 119, 97, 108, 116, 101, 110),
    properties: localizedText(101, 105, 103, 101, 110, 115, 99, 104, 97, 102, 116, 101, 110),
    cancel: localizedText(97, 98, 98, 114, 101, 99, 104, 101, 110),
  });
  // Menu labels in the active Steam language come from Steam's own
  // localization tokens; the English and German literals above stay only as a
  // fallback while Steam's LocalizationManager is not reachable.
  const steamCommandTokens = Object.freeze({
    play: ["#GameAction_Play"],
    addToFavorites: ["#GameAction_AddToFavorites"],
    addTo: ["#GameAction_AddToCollection"],
    manage: ["#GameAction_Manage"],
    properties: ["#GameAction_GameProperties"],
    cancel: ["#GameAction_Cancel", "#Button_Cancel"],
  });
  const fallbackCommandLabels = Object.freeze({
    play: ["play", localizedCommands.play],
    addToFavorites: ["add to favorites", localizedCommands.addToFavorites],
    addTo: ["add to", localizedCommands.addTo],
    manage: ["manage", localizedCommands.manage],
    properties: ["properties", localizedCommands.properties],
    cancel: ["cancel", localizedCommands.cancel],
  });
  let commandLabelCache = null;

  const existingArtworkRuntime = window.ToolsForSteamArtwork;
  if (existingArtworkRuntime?.version >= version) {
    existingArtworkRuntime.refresh?.();
    return "injected";
  }
  existingArtworkRuntime?.destroy?.();

  const assetTypes = [
    {
      id: "grid_p",
      label: "Cover",
      hint: "Library portrait",
      recommended: "600 x 900",
      targetWidth: 600,
      targetHeight: 900,
    },
    {
      id: "hero",
      label: "Hero",
      hint: "Game detail header",
      recommended: "1920 x 620",
      targetWidth: 1920,
      targetHeight: 620,
    },
    {
      id: "logo",
      label: "Logo",
      hint: "Transparent title logo",
      recommended: "1000 px wide",
      targetLongEdge: 1000,
    },
    {
      id: "icon",
      label: "Icon",
      hint: "Shortcut icon",
      recommended: "512 x 512",
      targetWidth: 512,
      targetHeight: 512,
    },
    {
      id: "grid_l",
      label: "Wide",
      hint: "Recent and library wide art",
      recommended: "920 x 430",
      targetWidth: 920,
      targetHeight: 430,
    },
  ];

  const state = {
    appId: 0,
    title: "",
    query: "",
    activeType: "grid_p",
    selectedGameId: 0,
    games: [],
    assets: [],
    loadingGames: false,
    gameSearchRequestId: 0,
    loadingAssets: false,
    assetRequestId: 0,
    animatedPreviewObserver: null,
    applying: false,
    lastAppliedAssetKey: "",
    status: "",
    error: "",
    overlay: null,
    currentOpenKey: "",
    lastClosedKey: "",
    lastClosedAt: 0,
    lastPanelRequestKey: "",
    lastPanelRequestAt: 0,
    observer: null,
    refreshTimer: null,
    openRequestTimer: null,
    localOpenRequestTimer: null,
    openRequestStorageHandler: null,
    artworkSettingsTimer: null,
    lastOpenRequestNonce: 0,
    lastLocalOpenRequestNonce: "",
    reactPatchInstalled: false,
    reactPatchRetryTimer: null,
    reactPatchRetryDelay: 0,
    focusItems: [],
    focusIndex: 0,
    gamepadFrame: 0,
    lastGamepadInput: "",
    lastGamepadInputAt: 0,
    lastSteamGamepadInput: "",
    lastSteamGamepadInputAt: 0,
    pressedGamepadButtons: new Set(),
    ignoreOverlayInputUntil: 0,
    artworkChannel: null,
    artworkChannelHandler: null,
    recentArtworkInputNonces: [],
    inputPollTimer: null,
    inputStorageHandler: null,
    overlayAnnounceTimer: null,
    remoteOverlayActive: false,
    catchAllInstalled: false,
    catchAllRegistration: null,
    catchAllCallback: null,
    previousCatchAllGamepadInput: null,
    catchAllButtonState: {},
    heldSteamButtons: new Set(),
    steamInputRelayAt: 0,
    steamInputRelayAnnouncedAt: 0,
    lastBrowserGamepadInput: "",
    lastBrowserGamepadInputAt: 0,
    returnFocusElement: null,
    rawSteamButtons: [],
    catchAllReleaseTimer: null,
    catchAllSuppressUntil: 0,
    focusZone: "side",
    pendingInitialAssetsFocus: false,
    contextMenuEnabled: false,
    artworkSettingsLoaded: false,
    lastContextMenuContext: null,
    lastContextMenuContextAt: 0,
    contextActivationCaptureInstalled: false,
    contextTrackingInstalled: false,
    contextTrackingHandler: null,
    contextActivationHandler: null,
    uninstallRequests: new Set(),
    repairRequests: new Set(),
    overlayMode: "artwork",
    metadataSnapshot: null,
    metadataMatches: [],
    selectedMetadataSteamAppId: 0,
    metadataLoading: false,
    metadataApplying: false,
    metadataStoreId: "",
    omniLibraryStateUnsubscribe: null,
    omniLibraryUninstallNoticeTimer: null,
  };

  function getArtworkChannel() {
    if (state.artworkChannel || typeof BroadcastChannel !== "function") {
      return state.artworkChannel;
    }

    try {
      state.artworkChannel = new BroadcastChannel(artworkChannelName);
      state.artworkChannelHandler = (event) => {
        handleArtworkChannelMessage(event.data);
      };
      state.artworkChannel.addEventListener("message", state.artworkChannelHandler);
    } catch {
      state.artworkChannel = null;
    }

    return state.artworkChannel;
  }

  function postArtworkMessage(message) {
    const payload = {
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      ...message,
    };

    try {
      getArtworkChannel()?.postMessage(payload);
    } catch {
    }

    try {
      const key = payload.type === "input" ? inputStorageKey : overlayStateStorageKey;
      localStorage.setItem(key, JSON.stringify(payload));
    } catch {
    }
  }

  function normalizeSteamAppId(value) {
    const numeric = Number(value || 0);
    if (!Number.isFinite(numeric)) {
      return 0;
    }

    const appId = Math.trunc(numeric);
    if (appId < 0) {
      return appId >>> 0;
    }

    return appId;
  }

  function getArtworkActionFromSteamButton(button) {
    const namedButton = String(button || "").toUpperCase();
    if (/(LEFT|L).*(BUMPER|SHOULDER|TRIGGER)|\b(LB|L1)\b/.test(namedButton)) {
      return "previous-type";
    }
    if (/(RIGHT|R).*(BUMPER|SHOULDER|TRIGGER)|\b(RB|R1)\b/.test(namedButton)) {
      return "next-type";
    }

    switch (Number(button)) {
      case 1:
        return "a";
      case 2:
        return "b";
      case 5:
      case 7:
        return "previous-type";
      case 6:
      case 8:
        return "next-type";
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

  function rememberRawSteamButton(button, action) {
    state.rawSteamButtons.push({
      button: typeof button === "number" || typeof button === "string" ? button : String(button),
      action,
      at: Date.now(),
    });
    if (state.rawSteamButtons.length > 24) {
      state.rawSteamButtons.shift();
    }
  }

  function isDirectionalArtworkAction(action) {
    return action === "up" || action === "down" || action === "left" || action === "right";
  }

  function shouldForwardSteamButton(button, action, isRepeat) {
    // Fresh presses always count; only Steam's held-button repeats are paced.
    const now = Date.now();
    const minimumGapMs = isRepeat ? 120 : 40;
    const lastMs = state.catchAllButtonState[button] || 0;
    if (now - lastMs < minimumGapMs) {
      return false;
    }

    state.catchAllButtonState[button] = now;
    return true;
  }

  function removeStaleArtworkCatchAllCallbacks(focusNav) {
    // Steam keeps catch-all handlers in a list now. Handlers left behind by an
    // earlier TFS runtime would otherwise stay registered until Steam restarts.
    const handlers = focusNav?.m_rgCatchAllGamepadInput;
    if (!Array.isArray(handlers)) {
      return;
    }

    for (let index = handlers.length - 1; index >= 0; index -= 1) {
      if (handlers[index]?.__steamToolsArtworkCatchAll) {
        handlers.splice(index, 1);
      }
    }
  }

  function installArtworkCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || state.catchAllInstalled) {
      return;
    }

    removeStaleArtworkCatchAllCallbacks(focusNav);
    const previous = focusNav.m_fnCatchAllGamepadInput;
    // Steam calls catch-all handlers for button down and button up, with a
    // repeat flag for held buttons: (button, isButtonDown, isRepeat).
    const callback = (button, isButtonDown = true, isRepeat = false) => {
      const action = getArtworkActionFromSteamButton(button);
      const overlayInputActive = state.remoteOverlayActive || Boolean(state.overlay);
      const isRelease = isButtonDown === false;

      if (!overlayInputActive) {
        // The press that closed the overlay is still held: its release and
        // repeats must not reach the Steam page underneath.
        if (isRelease && state.heldSteamButtons.delete(button)) {
          return true;
        }
        if (state.heldSteamButtons.has(button) || Date.now() < state.catchAllSuppressUntil) {
          return true;
        }

        return typeof previous === "function" ? previous(button, isButtonDown, isRepeat) : false;
      }

      if (isRelease) {
        state.heldSteamButtons.delete(button);
        return true;
      }

      state.heldSteamButtons.add(button);
      rememberRawSteamButton(button, action);
      if (!action || (isRepeat && !isDirectionalArtworkAction(action))) {
        return true;
      }

      if (shouldForwardSteamButton(button, action, Boolean(isRepeat))) {
        postArtworkMessage({ type: "input", action, repeat: Boolean(isRepeat), source: "steam-catch-all" });
      }

      return true;
    };

    callback.__steamToolsArtworkCatchAll = true;
    state.previousCatchAllGamepadInput = previous?.__steamToolsArtworkCatchAll ? null : previous;
    state.catchAllRegistration = focusNav.SetCatchAllGamepadInput(callback) || null;
    state.catchAllCallback = callback;
    state.catchAllInstalled = true;
  }

  function uninstallArtworkCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || !state.catchAllInstalled) {
      return;
    }

    if (typeof state.catchAllRegistration?.Unregister === "function") {
      state.catchAllRegistration.Unregister();
    } else if (focusNav.m_fnCatchAllGamepadInput?.__steamToolsArtworkCatchAll) {
      focusNav.SetCatchAllGamepadInput(state.previousCatchAllGamepadInput || undefined);
    }
    if (Array.isArray(focusNav.m_rgCatchAllGamepadInput) && state.catchAllCallback) {
      const index = focusNav.m_rgCatchAllGamepadInput.indexOf(state.catchAllCallback);
      if (index >= 0) {
        focusNav.m_rgCatchAllGamepadInput.splice(index, 1);
      }
    }

    state.catchAllInstalled = false;
    state.catchAllRegistration = null;
    state.catchAllCallback = null;
    state.previousCatchAllGamepadInput = null;
    state.catchAllButtonState = {};
    state.catchAllSuppressUntil = 0;
    state.heldSteamButtons.clear();
  }

  function releaseArtworkInputCapture() {
    const wasActive = state.remoteOverlayActive;
    state.remoteOverlayActive = false;
    if (!state.catchAllInstalled || (!wasActive && state.catchAllReleaseTimer)) {
      return;
    }

    if (state.catchAllReleaseTimer) {
      window.clearTimeout(state.catchAllReleaseTimer);
    }

    // Keep catching briefly after the overlay closes so the closing B (or a
    // late repeat) cannot trigger Steam's own Back on the page underneath.
    const now = Date.now();
    const releaseDeadline = now + 1500;
    state.catchAllSuppressUntil = now + 220;
    const finishRelease = () => {
      if (state.remoteOverlayActive || state.overlay) {
        state.catchAllReleaseTimer = null;
        return;
      }

      const current = Date.now();
      if (
        current < releaseDeadline &&
        (state.heldSteamButtons.size || current < state.catchAllSuppressUntil)
      ) {
        state.catchAllReleaseTimer = window.setTimeout(finishRelease, 60);
        return;
      }

      state.catchAllReleaseTimer = null;
      uninstallArtworkCatchAllInput();
    };
    state.catchAllReleaseTimer = window.setTimeout(finishRelease, 60);
  }

  function setRemoteOverlayActive(active) {
    if (active) {
      state.remoteOverlayActive = true;
      if (state.catchAllReleaseTimer) {
        window.clearTimeout(state.catchAllReleaseTimer);
        state.catchAllReleaseTimer = null;
      }
      state.catchAllSuppressUntil = 0;
      installArtworkCatchAllInput();
      announceSteamInputRelay();
    } else {
      releaseArtworkInputCapture();
    }
  }

  function announceSteamInputRelay() {
    // Tell the overlay window that Steam's own controller input is relayed,
    // so it stops reading the same pad a second time via getGamepads().
    if (!state.catchAllInstalled || state.overlay) {
      return;
    }

    const now = Date.now();
    if (now - state.steamInputRelayAnnouncedAt < 400) {
      return;
    }

    state.steamInputRelayAnnouncedAt = now;
    try {
      getArtworkChannel()?.postMessage({ type: "steam-input-relay", at: now });
    } catch {
    }
    try {
      localStorage.setItem(steamInputRelayStorageKey, String(now));
    } catch {
    }
  }

  function noteSteamInputRelay(at = Date.now()) {
    const value = Number(at) || 0;
    if (value > state.steamInputRelayAt && value <= Date.now() + 1000) {
      state.steamInputRelayAt = value;
    }
  }

  function isSteamInputRelayActive() {
    return Date.now() - state.steamInputRelayAt < 2500;
  }

  function rememberArtworkInputNonce(nonce) {
    if (state.recentArtworkInputNonces.includes(nonce)) {
      return false;
    }

    state.recentArtworkInputNonces.push(nonce);
    if (state.recentArtworkInputNonces.length > 48) {
      state.recentArtworkInputNonces.shift();
    }
    return true;
  }

  function consumeArtworkInput(raw) {
    if (!raw || !state.overlay) {
      return;
    }

    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type !== "input" || !payload.action || !payload.nonce) {
        return;
      }

      // The same input arrives over BroadcastChannel, the storage event and
      // the storage poll; each nonce may act once, and old leftovers never.
      const sentAt = Number.parseInt(String(payload.nonce), 10);
      if (!rememberArtworkInputNonce(String(payload.nonce)) || (sentAt && Date.now() - sentAt > 1500)) {
        return;
      }

      if (payload.source === "steam-catch-all") {
        noteSteamInputRelay();
      }
      maybeRepeatGamepadAction(
        payload.action,
        String(payload.source || "remote"),
        payload.repeat !== false,
      );
    } catch {
    }
  }

  function consumeArtworkOverlayState(raw) {
    if (!raw) {
      return;
    }

    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type === "overlay-state") {
        const stillFresh = !payload.expiresAt || Number(payload.expiresAt) > Date.now();
        setRemoteOverlayActive(Boolean(payload.active) && stillFresh);
      }
    } catch {
    }
  }

  function handleArtworkChannelMessage(payload) {
    if (payload?.type === "input") {
      consumeArtworkInput(payload);
    } else if (payload?.type === "steam-input-relay") {
      noteSteamInputRelay(payload.at);
    } else if (payload?.type === "overlay-state") {
      consumeArtworkOverlayState(payload);
    }
  }

  function announceArtworkOverlayState(active) {
    postArtworkMessage({
      type: "overlay-state",
      active: Boolean(active),
      expiresAt: active ? Date.now() + 1800 : 0,
    });
  }

  function startArtworkOverlayAnnouncements() {
    stopArtworkOverlayAnnouncements();
    announceArtworkOverlayState(true);
    state.overlayAnnounceTimer = window.setInterval(() => {
      if (state.overlay) {
        announceArtworkOverlayState(true);
      }
    }, 700);
  }

  function stopArtworkOverlayAnnouncements() {
    if (state.overlayAnnounceTimer) {
      window.clearInterval(state.overlayAnnounceTimer);
      state.overlayAnnounceTimer = null;
    }
    announceArtworkOverlayState(false);
  }

  function setupArtworkInputBridge() {
    getArtworkChannel();

    if (!state.inputStorageHandler) {
      state.inputStorageHandler = (event) => {
        if (event.key === inputStorageKey) {
          consumeArtworkInput(event.newValue);
        } else if (event.key === overlayStateStorageKey) {
          consumeArtworkOverlayState(event.newValue);
        } else if (event.key === steamInputRelayStorageKey) {
          noteSteamInputRelay(event.newValue);
        }
      };
      window.addEventListener("storage", state.inputStorageHandler);
    }

    if (!state.inputPollTimer) {
      state.inputPollTimer = window.setInterval(() => {
        try {
          consumeArtworkInput(localStorage.getItem(inputStorageKey));
          consumeArtworkOverlayState(localStorage.getItem(overlayStateStorageKey));
          if (state.overlay) {
            noteSteamInputRelay(localStorage.getItem(steamInputRelayStorageKey));
          }
        } catch {
        }
      }, 100);
    }
  }

  function injectStyles() {
    const existingStyle = document.getElementById("steamtools-artwork-style");
    if (existingStyle?.dataset.steamtoolsArtworkVersion === String(version)) {
      return;
    }
    existingStyle?.remove();

    const style = document.createElement("style");
    style.id = "steamtools-artwork-style";
    style.dataset.steamtoolsArtworkVersion = String(version);
    style.textContent = `
      .steamtools-artwork-context-row {
        cursor: pointer;
      }

      .steamtools-artwork-context-row:hover,
      .steamtools-artwork-context-row:focus,
      .steamtools-artwork-context-row.gpfocus {
        outline: none;
      }

      .steamtools-artwork-overlay {
        position: fixed;
        inset: 0;
        z-index: 2147483600;
        --tfs-art-accent: #63d6ff;
        --tfs-art-panel: rgba(16, 24, 34, 0.88);
        --tfs-art-panel-soft: rgba(24, 35, 47, 0.74);
        --tfs-art-line: rgba(172, 207, 230, 0.13);
        background:
          radial-gradient(circle at 14% -4%, rgba(42, 159, 213, 0.28), transparent 31%),
          radial-gradient(circle at 88% 2%, rgba(78, 101, 220, 0.16), transparent 27%),
          linear-gradient(145deg, #071019 0%, #0b121b 46%, #080c12 100%);
        color: #f4f7fb;
        font-family: "Motiva Sans", Arial, sans-serif;
        overflow: hidden;
      }

      .steamtools-artwork-shell {
        height: 100%;
        box-sizing: border-box;
        padding: clamp(16px, 2.4vw, 34px) clamp(18px, 3.2vw, 52px) clamp(72px, 7vh, 86px);
        display: grid;
        grid-template-rows: auto auto auto minmax(0, 1fr);
        gap: 11px;
      }

      .steamtools-artwork-head {
        min-height: 62px;
        display: flex;
        align-items: flex-end;
        justify-content: space-between;
        gap: 24px;
      }

      .steamtools-artwork-brand-row {
        display: flex;
        align-items: center;
        gap: 12px;
      }

      .steamtools-artwork-brand-mark {
        display: grid;
        width: 34px;
        height: 34px;
        place-items: center;
        border-radius: 10px;
        color: #04121b;
        background: linear-gradient(145deg, #8fe8ff, #4fc2f2);
        box-shadow: 0 8px 28px rgba(56, 184, 235, 0.24);
        font-size: 13px;
        font-weight: 950;
      }

      .steamtools-artwork-kicker {
        color: var(--tfs-art-accent);
        letter-spacing: 0.14em;
        text-transform: uppercase;
        font-size: 13px;
        font-weight: 900;
      }

      .steamtools-artwork-title {
        margin-top: 5px;
        max-width: min(70vw, 980px);
        overflow: hidden;
        color: #ffffff;
        font-size: clamp(27px, 3.1vw, 44px);
        font-weight: 950;
        line-height: 1;
        letter-spacing: -0.035em;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .steamtools-artwork-subtitle {
        max-width: 500px;
        color: #9db0c1;
        font-size: clamp(13px, 1.1vw, 16px);
        font-weight: 650;
        line-height: 1.35;
        text-align: right;
      }

      .steamtools-artwork-close {
        border: 0;
        border-radius: 18px;
        min-width: 112px;
        min-height: 58px;
        padding: 0 24px;
        background: #303742;
        color: #edf3f8;
        font-size: 24px;
        font-weight: 800;
      }

      .steamtools-artwork-close:focus,
      .steamtools-artwork-close.is-controller-focus,
      .steamtools-artwork-close:hover,
      .steamtools-artwork-search button:focus,
      .steamtools-artwork-search button.is-controller-focus,
      .steamtools-artwork-search button:hover,
      .steamtools-artwork-game:focus,
      .steamtools-artwork-game.is-controller-focus,
      .steamtools-artwork-game:hover,
      .steamtools-artwork-tab:focus,
      .steamtools-artwork-tab.is-controller-focus,
      .steamtools-artwork-tab:hover,
      .steamtools-artwork-type-chip:focus,
      .steamtools-artwork-type-chip.is-controller-focus,
      .steamtools-artwork-type-chip:hover,
      .steamtools-artwork-type-shoulder:focus,
      .steamtools-artwork-type-shoulder.is-controller-focus,
      .steamtools-artwork-type-shoulder:hover {
        outline: none;
        background: #485261;
        color: #ffffff;
        box-shadow: inset 0 0 0 2px rgba(244, 247, 251, 0.78);
      }

      .steamtools-artwork-asset.is-applied {
        background: rgba(47, 84, 58, 0.68);
        box-shadow: inset 0 0 0 2px rgba(101, 226, 132, 0.85);
      }

      .steamtools-artwork-asset.is-applied .steamtools-artwork-asset-meta {
        color: #9ce8ad;
      }

      /* Artwork cards follow Steam's Big Picture capsules: the focused card
         lifts slightly and a bright ring is painted above the image, so it
         stays visible on full-bleed artwork and on already applied cards. */
      .steamtools-artwork-asset {
        transition: transform 140ms ease-out, box-shadow 140ms ease-out;
      }

      .steamtools-artwork-asset::after {
        content: "";
        position: absolute;
        inset: 0;
        z-index: 3;
        border-radius: inherit;
        pointer-events: none;
        opacity: 0;
        background: linear-gradient(135deg, rgba(255, 255, 255, 0.14), transparent 38%);
        box-shadow:
          inset 0 0 0 3px #ffffff,
          inset 0 0 0 5px rgba(6, 11, 17, 0.5);
        transition: opacity 140ms ease-out;
      }

      .steamtools-artwork-asset:hover::after {
        opacity: 0.4;
      }

      .steamtools-artwork-asset:focus,
      .steamtools-artwork-asset.is-controller-focus {
        outline: none;
        z-index: 1;
        background: #3a4553;
        color: #ffffff;
        transform: scale(1.045);
        box-shadow:
          0 14px 30px rgba(0, 0, 0, 0.55),
          0 0 24px rgba(99, 214, 255, 0.32);
      }

      .steamtools-artwork-asset.is-applied:focus,
      .steamtools-artwork-asset.is-applied.is-controller-focus {
        box-shadow:
          0 14px 30px rgba(0, 0, 0, 0.55),
          0 0 24px rgba(101, 226, 132, 0.4);
      }

      .steamtools-artwork-asset:focus::after,
      .steamtools-artwork-asset.is-controller-focus::after {
        opacity: 1;
      }

      .steamtools-artwork-asset.is-controller-focus .steamtools-artwork-asset-resolution {
        color: #ffffff;
      }

      @media (prefers-reduced-motion: reduce) {
        .steamtools-artwork-asset,
        .steamtools-artwork-asset::after {
          transition: none;
        }

        .steamtools-artwork-asset:focus,
        .steamtools-artwork-asset.is-controller-focus {
          transform: none;
        }
      }

      .steamtools-artwork-search {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 12px;
      }

      .steamtools-artwork-search input {
        min-height: 48px;
        border: 1px solid rgba(146, 182, 207, 0.24);
        border-radius: 14px;
        background: rgba(5, 11, 17, 0.76);
        color: #f3f7fb;
        font-size: 18px;
        padding: 0 18px;
      }

      .steamtools-artwork-search input:focus {
        outline: none;
        border-color: var(--tfs-art-accent);
        box-shadow: 0 0 0 2px rgba(99, 214, 255, 0.14);
      }

      .steamtools-artwork-search button,
      .steamtools-artwork-game,
      .steamtools-artwork-tab,
      .steamtools-artwork-type-chip,
      .steamtools-artwork-type-shoulder,
      .steamtools-artwork-asset {
        border: 0;
        color: #dce4ed;
        background: #303742;
        cursor: pointer;
        font: inherit;
      }

      .steamtools-artwork-search button {
        border-radius: 14px;
        min-width: 132px;
        font-size: 17px;
        font-weight: 900;
        background: linear-gradient(145deg, #277eaa, #17658f);
      }

      .steamtools-artwork-body {
        min-height: 0;
        display: grid;
        grid-template-columns: minmax(205px, 268px) minmax(0, 1fr);
        gap: 14px;
      }

      .steamtools-artwork-side,
      .steamtools-artwork-results {
        min-height: 0;
        border-radius: 20px;
        background: var(--tfs-art-panel);
        box-shadow:
          inset 0 0 0 1px var(--tfs-art-line),
          0 18px 50px rgba(0, 0, 0, 0.16);
      }

      .steamtools-artwork-side {
        padding: 12px;
        overflow: auto;
      }

      .steamtools-artwork-side-label {
        padding: 6px 6px 10px;
        color: #7f94aa;
        font-size: 13px;
        font-weight: 900;
        letter-spacing: 0.12em;
        text-transform: uppercase;
      }

      .steamtools-artwork-game,
      .steamtools-artwork-tab {
        width: 100%;
        min-height: 50px;
        margin-bottom: 7px;
        padding: 10px 12px;
        border-radius: 14px;
        text-align: left;
        font-size: 16px;
        font-weight: 800;
      }

      .steamtools-artwork-game.is-active,
      .steamtools-artwork-tab.is-active {
        background: #3b4654;
        color: #ffffff;
      }

      .steamtools-artwork-game span,
      .steamtools-artwork-tab span {
        display: block;
        margin-top: 4px;
        color: #9fb2c5;
        font-size: 12px;
        font-weight: 600;
      }

      .steamtools-artwork-results {
        padding: 15px;
        overflow: auto;
      }

      .steamtools-artwork-results-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 16px;
        margin: 0 2px 13px;
      }

      .steamtools-artwork-results-title {
        min-width: 0;
      }

      .steamtools-artwork-results-title strong {
        display: block;
        color: #f7fbff;
        font-size: clamp(17px, 1.5vw, 22px);
        font-weight: 950;
      }

      .steamtools-artwork-results-title span {
        display: block;
        margin-top: 3px;
        color: #91a6b9;
        font-size: 12px;
        font-weight: 700;
      }

      .steamtools-artwork-quality-legend {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: 7px;
      }

      .steamtools-artwork-legend-item {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        color: #9fb0c0;
        font-size: 11px;
        font-weight: 800;
      }

      .steamtools-artwork-legend-dot {
        width: 8px;
        height: 8px;
        border-radius: 999px;
        background: #718092;
      }

      .steamtools-artwork-legend-dot.is-high { background: #57df8c; }
      .steamtools-artwork-legend-dot.is-good { background: #63d6ff; }
      .steamtools-artwork-legend-dot.is-usable { background: #ffc85c; }
      .steamtools-artwork-legend-dot.is-low { background: #ff7182; }

      .steamtools-artwork-type-rail {
        width: 100%;
        display: grid;
        grid-template-columns: auto minmax(0, 1fr) auto;
        align-items: center;
        gap: 8px;
        margin: -2px 0 0;
      }

      .steamtools-artwork-type-stack {
        min-width: 0;
        display: grid;
        grid-template-columns: repeat(5, minmax(0, 1fr));
        gap: 6px;
        margin-top: 6px;
      }

      .steamtools-artwork-type-chip,
      .steamtools-artwork-type-shoulder {
        min-height: 38px;
        border-radius: 12px;
        padding: 0 10px;
        font-size: 14px;
        font-weight: 900;
      }

      .steamtools-artwork-type-chip {
        color: #a9bacb;
        background: rgba(48, 55, 66, 0.72);
      }

      .steamtools-artwork-type-chip.is-active {
        color: #ffffff;
        background: #4a5665;
        box-shadow:
          inset 0 0 0 2px rgba(112, 200, 255, 0.62),
          0 0 18px rgba(112, 200, 255, 0.12);
      }

      .steamtools-artwork-type-chip span {
        display: block;
        margin-top: 1px;
        color: #9fb2c5;
        font-size: 10px;
        font-weight: 700;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }

      .steamtools-artwork-type-shoulder {
        min-width: 58px;
        color: #f4f7fb;
        background: #303742;
      }

      .steamtools-artwork-type-center {
        min-width: 0;
      }

      .steamtools-artwork-type-current {
        min-height: 46px;
        border-radius: 14px;
        padding: 8px 14px;
        display: grid;
        grid-template-columns: auto auto minmax(0, 1fr);
        align-items: center;
        column-gap: 12px;
        background: linear-gradient(90deg, rgba(33, 73, 96, 0.92), rgba(35, 45, 58, 0.86));
        box-shadow: inset 0 0 0 1px rgba(99, 214, 255, 0.2);
      }

      .steamtools-artwork-type-current span {
        display: block;
        color: #8fa6bd;
        font-size: 11px;
        font-weight: 900;
        letter-spacing: 0.12em;
        text-transform: uppercase;
      }

      .steamtools-artwork-type-current strong {
        display: block;
        margin-top: 0;
        color: #ffffff;
        font-size: clamp(18px, 1.6vw, 24px);
        font-weight: 950;
        line-height: 1;
      }

      .steamtools-artwork-type-current em {
        display: block;
        margin-top: 0;
        color: #a9bacb;
        font-size: 13px;
        font-style: normal;
        font-weight: 700;
      }

      .steamtools-artwork-message {
        margin-bottom: 16px;
        border-radius: 18px;
        padding: 16px 20px;
        color: #b4c6d8;
        background: rgba(255,255,255,0.045);
        font-size: 21px;
        line-height: 1.28;
      }

      .steamtools-artwork-message.is-error {
        color: #ffd08d;
        background: rgba(112, 55, 23, 0.72);
      }

      .steamtools-artwork-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(154px, 1fr));
        gap: 13px;
      }

      .steamtools-artwork-grid.is-wide,
      .steamtools-artwork-grid.is-hero {
        grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
      }

      .steamtools-artwork-asset {
        min-width: 0;
        position: relative;
        overflow: hidden;
        border-radius: 16px;
        padding: 0;
        text-align: left;
        background: rgba(30, 41, 53, 0.92);
        box-shadow: inset 0 0 0 1px rgba(171, 204, 226, 0.1);
      }

      .steamtools-artwork-asset img,
      .steamtools-artwork-asset video {
        display: block;
        width: 100%;
        aspect-ratio: 2 / 3;
        object-fit: cover;
        border-radius: 15px 15px 0 0;
        background: #0c1118;
      }

      .steamtools-artwork-asset video {
        pointer-events: none;
      }

      .steamtools-artwork-asset-image-wrap {
        position: relative;
        overflow: hidden;
        border-radius: 15px 15px 0 0;
        background:
          linear-gradient(45deg, rgba(255,255,255,.035) 25%, transparent 25%),
          linear-gradient(-45deg, rgba(255,255,255,.035) 25%, transparent 25%),
          #0c1118;
        background-size: 16px 16px;
      }

      .steamtools-artwork-quality-badge {
        position: absolute;
        top: 9px;
        left: 9px;
        display: inline-flex;
        align-items: center;
        gap: 6px;
        min-height: 25px;
        padding: 0 9px;
        border-radius: 999px;
        color: #f6fbff;
        background: rgba(17, 24, 32, 0.9);
        box-shadow: 0 5px 16px rgba(0, 0, 0, 0.28);
        font-size: 10px;
        font-weight: 950;
        letter-spacing: 0.035em;
        text-transform: uppercase;
      }

      .steamtools-artwork-quality-badge::before {
        content: "";
        width: 7px;
        height: 7px;
        border-radius: 999px;
        background: #718092;
      }

      .steamtools-artwork-quality-badge.is-high::before { background: #57df8c; }
      .steamtools-artwork-quality-badge.is-good::before { background: #63d6ff; }
      .steamtools-artwork-quality-badge.is-usable::before { background: #ffc85c; }
      .steamtools-artwork-quality-badge.is-low::before { background: #ff7182; }

      .steamtools-artwork-applied-badge {
        position: absolute;
        top: 9px;
        right: 9px;
        min-height: 25px;
        padding: 0 9px;
        display: grid;
        place-items: center;
        border-radius: 999px;
        color: #062013;
        background: #68e39a;
        box-shadow: 0 5px 16px rgba(0, 0, 0, 0.28);
        font-size: 10px;
        font-weight: 950;
        text-transform: uppercase;
      }

      .steamtools-artwork-motion-badge {
        position: absolute;
        top: 9px;
        right: 9px;
        z-index: 2;
        min-height: 25px;
        padding: 0 9px;
        display: grid;
        place-items: center;
        border-radius: 999px;
        color: #211403;
        background: #ffbd59;
        box-shadow: 0 5px 16px rgba(0, 0, 0, 0.28);
        font-size: 10px;
        font-weight: 950;
        letter-spacing: 0.035em;
        text-transform: uppercase;
      }

      .steamtools-artwork-asset.is-animated .steamtools-artwork-applied-badge {
        top: 42px;
      }

      .steamtools-artwork-grid.is-wide .steamtools-artwork-asset img,
      .steamtools-artwork-grid.is-wide .steamtools-artwork-asset video {
        aspect-ratio: 92 / 43;
      }

      .steamtools-artwork-grid.is-hero .steamtools-artwork-asset img,
      .steamtools-artwork-grid.is-hero .steamtools-artwork-asset video {
        aspect-ratio: 192 / 62;
      }

      .steamtools-artwork-grid.is-logo .steamtools-artwork-asset img,
      .steamtools-artwork-grid.is-logo .steamtools-artwork-asset video,
      .steamtools-artwork-grid.is-icon .steamtools-artwork-asset img,
      .steamtools-artwork-grid.is-icon .steamtools-artwork-asset video {
        aspect-ratio: 1 / 1;
        object-fit: contain;
        padding: 16px;
        box-sizing: border-box;
      }

      .steamtools-artwork-asset-meta {
        padding: 10px 11px 11px;
      }

      .steamtools-artwork-asset-resolution {
        color: #eef6fc;
        font-size: 13px;
        font-weight: 900;
      }

      .steamtools-artwork-asset-fit {
        margin-top: 3px;
        color: #8fa5b8;
        font-size: 11px;
        font-weight: 700;
      }

      .steamtools-artwork-asset.is-quality-low .steamtools-artwork-asset-fit {
        color: #ff9ba7;
      }

      .steamtools-artwork-asset.is-quality-usable .steamtools-artwork-asset-fit {
        color: #ffd57d;
      }

      .steamtools-metadata-preview {
        display: grid;
        gap: 16px;
      }

      .steamtools-metadata-card {
        border-radius: 18px;
        padding: clamp(18px, 2vw, 28px);
        background: rgba(255,255,255,0.045);
        box-shadow: inset 0 0 0 1px rgba(180, 201, 220, 0.12);
      }

      .steamtools-metadata-card h2 {
        margin: 0 0 8px;
        color: #ffffff;
        font-size: clamp(22px, 2vw, 30px);
      }

      .steamtools-metadata-card p {
        margin: 0;
        color: #b4c6d8;
        font-size: clamp(16px, 1.45vw, 20px);
        line-height: 1.45;
      }

      .steamtools-metadata-source {
        margin-bottom: 12px;
        color: #67c1f5;
        font-size: 13px;
        font-weight: 900;
        letter-spacing: 0.1em;
        text-transform: uppercase;
      }

      .steamtools-metadata-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
      }

      .steamtools-metadata-action {
        min-width: 180px;
        border-radius: 14px;
        padding: 13px 18px;
        text-align: center;
      }

      .steamtools-artwork-controller-hint {
        position: fixed;
        right: clamp(24px, 3.5vw, 54px);
        bottom: clamp(18px, 2.8vw, 42px);
        display: flex;
        align-items: center;
        gap: 12px;
        border-radius: 999px;
        padding: 11px 18px;
        color: #d8e0e8;
        background: rgba(11, 16, 23, 0.78);
        box-shadow: inset 0 0 0 1px rgba(180, 201, 220, 0.14);
        font-size: clamp(14px, 1.45vw, 18px);
        font-weight: 800;
        letter-spacing: 0.01em;
        pointer-events: none;
      }

      .steamtools-artwork-floating-status {
        position: fixed;
        left: clamp(24px, 3.5vw, 54px);
        bottom: clamp(18px, 2.8vw, 42px);
        max-width: min(640px, calc(100vw - 420px));
        border-radius: 20px;
        padding: 14px 20px;
        color: #dbe8f4;
        background: rgba(16, 24, 34, 0.88);
        box-shadow: inset 0 0 0 1px rgba(180, 201, 220, 0.14);
        font-size: clamp(15px, 1.6vw, 20px);
        font-weight: 800;
        line-height: 1.28;
        pointer-events: none;
      }

      .steamtools-artwork-floating-status.is-error {
        color: #ffd08d;
        background: rgba(112, 55, 23, 0.88);
      }

      .steamtools-artwork-floating-status.is-success {
        color: #dff8e7;
        background: rgba(23, 83, 52, 0.88);
      }

      .steamtools-artwork-controller-key {
        display: inline-grid;
        min-width: 30px;
        height: 30px;
        padding: 0 8px;
        place-items: center;
        border-radius: 999px;
        background: #f4f7fb;
        color: #0b1017;
        font-size: 19px;
        font-weight: 950;
      }

      /* TFS Artworks is deliberately image-first: two compact control rows,
         then the gallery owns every remaining pixel. Metadata correction keeps
         the roomier shared layout above. */
      .steamtools-artwork-shell.is-artwork-mode {
        grid-template-rows: auto auto minmax(0, 1fr);
        gap: 9px;
        padding: 14px 22px 64px;
      }

      .is-artwork-mode .steamtools-artwork-head {
        min-height: 44px;
        align-items: center;
      }

      .steamtools-artwork-lockup {
        min-width: 0;
        display: flex;
        align-items: center;
        gap: 16px;
      }

      .is-artwork-mode .steamtools-artwork-brand-row {
        flex: 0 0 auto;
        gap: 9px;
      }

      .is-artwork-mode .steamtools-artwork-brand-mark {
        width: 30px;
        height: 30px;
        border-radius: 9px;
        font-size: 11px;
      }

      .is-artwork-mode .steamtools-artwork-kicker {
        font-size: 12px;
      }

      .is-artwork-mode .steamtools-artwork-title {
        margin: 0;
        max-width: min(52vw, 760px);
        padding-left: 16px;
        border-left: 1px solid rgba(168, 205, 228, 0.2);
        font-size: clamp(22px, 2.2vw, 32px);
        letter-spacing: -0.025em;
      }

      .steamtools-artwork-head-summary {
        flex: 0 0 auto;
        color: #7f96a9;
        font-size: 12px;
        font-weight: 800;
        letter-spacing: 0.06em;
        text-transform: uppercase;
      }

      .steamtools-artwork-command-bar {
        display: grid;
        grid-template-columns: minmax(280px, 0.72fr) minmax(560px, 1.28fr);
        gap: 10px;
        min-height: 46px;
      }

      .is-artwork-mode .steamtools-artwork-search {
        gap: 7px;
      }

      .is-artwork-mode .steamtools-artwork-search input {
        min-height: 44px;
        border-radius: 12px;
        font-size: 15px;
      }

      .is-artwork-mode .steamtools-artwork-search button {
        min-width: 92px;
        border-radius: 12px;
        font-size: 14px;
      }

      .is-artwork-mode .steamtools-artwork-type-rail {
        gap: 6px;
        margin: 0;
      }

      .is-artwork-mode .steamtools-artwork-type-stack {
        grid-template-columns: repeat(5, minmax(0, 1fr));
        gap: 5px;
        margin: 0;
      }

      .is-artwork-mode .steamtools-artwork-type-chip,
      .is-artwork-mode .steamtools-artwork-type-shoulder {
        min-height: 44px;
        border-radius: 12px;
        padding: 0 8px;
        font-size: 13px;
      }

      .is-artwork-mode .steamtools-artwork-type-chip span {
        display: none;
      }

      .is-artwork-mode .steamtools-artwork-type-shoulder {
        min-width: 46px;
        padding: 0 8px;
      }

      .is-artwork-mode .steamtools-artwork-body {
        grid-template-columns: minmax(176px, 218px) minmax(0, 1fr);
        gap: 10px;
      }

      .is-artwork-mode .steamtools-artwork-side,
      .is-artwork-mode .steamtools-artwork-results {
        border-radius: 16px;
      }

      .is-artwork-mode .steamtools-artwork-side {
        padding: 9px;
      }

      .is-artwork-mode .steamtools-artwork-side-label {
        padding: 4px 5px 8px;
        font-size: 11px;
      }

      .is-artwork-mode .steamtools-artwork-game {
        min-height: 43px;
        margin-bottom: 5px;
        padding: 8px 10px;
        border-radius: 11px;
        font-size: 13px;
      }

      .is-artwork-mode .steamtools-artwork-game span {
        margin-top: 2px;
        font-size: 10px;
      }

      .is-artwork-mode .steamtools-artwork-results {
        padding: 10px;
      }

      .is-artwork-mode .steamtools-artwork-results-head {
        min-height: 32px;
        margin: 0 2px 8px;
      }

      .is-artwork-mode .steamtools-artwork-results-title {
        display: flex;
        align-items: baseline;
        gap: 10px;
      }

      .is-artwork-mode .steamtools-artwork-results-title strong {
        font-size: 17px;
      }

      .is-artwork-mode .steamtools-artwork-results-title span {
        margin: 0;
        font-size: 10px;
      }

      .is-artwork-mode .steamtools-artwork-quality-legend {
        gap: 9px;
      }

      .is-artwork-mode .steamtools-artwork-legend-item {
        font-size: 10px;
      }

      .is-artwork-mode .steamtools-artwork-grid {
        grid-template-columns: repeat(auto-fill, minmax(132px, 1fr));
        gap: 9px;
      }

      .is-artwork-mode .steamtools-artwork-grid.is-wide,
      .is-artwork-mode .steamtools-artwork-grid.is-hero {
        grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
      }

      .is-artwork-mode .steamtools-artwork-asset {
        border-radius: 12px;
      }

      .is-artwork-mode .steamtools-artwork-asset-image-wrap,
      .is-artwork-mode .steamtools-artwork-asset img,
      .is-artwork-mode .steamtools-artwork-asset video {
        border-radius: 11px;
      }

      .is-artwork-mode .steamtools-artwork-asset-meta {
        position: absolute;
        right: 0;
        bottom: 0;
        left: 0;
        z-index: 2;
        padding: 28px 9px 8px;
        border-radius: 0 0 11px 11px;
        background: linear-gradient(180deg, transparent, rgba(4, 9, 14, 0.93));
        pointer-events: none;
      }

      .is-artwork-mode .steamtools-artwork-asset-resolution {
        font-size: 12px;
      }

      .is-artwork-mode .steamtools-artwork-asset-fit {
        margin-top: 1px;
        color: rgba(222, 234, 243, 0.72);
        font-size: 9px;
      }

      .is-artwork-mode .steamtools-artwork-quality-badge,
      .is-artwork-mode .steamtools-artwork-applied-badge,
      .is-artwork-mode .steamtools-artwork-motion-badge {
        top: 7px;
        min-height: 22px;
        padding: 0 8px;
        font-size: 9px;
      }

      .is-artwork-mode .steamtools-artwork-quality-badge { left: 7px; }
      .is-artwork-mode .steamtools-artwork-applied-badge,
      .is-artwork-mode .steamtools-artwork-motion-badge { right: 7px; }
      .is-artwork-mode .steamtools-artwork-asset.is-animated .steamtools-artwork-applied-badge { top: 36px; }

      .is-artwork-mode + .steamtools-artwork-controller-hint {
        right: 22px;
        bottom: 12px;
        gap: 8px;
        padding: 7px 12px;
        font-size: 12px;
      }

      .is-artwork-mode + .steamtools-artwork-controller-hint .steamtools-artwork-controller-key {
        min-width: 24px;
        height: 24px;
        padding: 0 6px;
        font-size: 13px;
      }

      @media (max-height: 760px) {
        .steamtools-artwork-shell.is-artwork-mode {
          padding-top: 10px;
          padding-bottom: 56px;
        }

        .is-artwork-mode .steamtools-artwork-head {
          min-height: 38px;
        }

        .is-artwork-mode .steamtools-artwork-command-bar,
        .is-artwork-mode .steamtools-artwork-search input,
        .is-artwork-mode .steamtools-artwork-type-chip,
        .is-artwork-mode .steamtools-artwork-type-shoulder {
          min-height: 40px;
        }
      }

      @media (max-width: 900px) {
        .steamtools-artwork-shell {
          padding: 14px 16px 72px;
        }

        .steamtools-artwork-head {
          min-height: 52px;
        }

        .steamtools-artwork-subtitle {
          display: none;
        }

        .steamtools-artwork-body {
          grid-template-columns: 1fr;
        }

        .steamtools-artwork-side {
          max-height: 220px;
        }

        .steamtools-artwork-type-rail {
          grid-template-columns: 1fr;
        }

        .steamtools-artwork-type-stack {
          grid-template-columns: repeat(2, minmax(0, 1fr));
        }

        .steamtools-artwork-type-shoulder {
          display: none;
        }

        .steamtools-artwork-floating-status {
          max-width: calc(100vw - 48px);
          bottom: 74px;
        }

        .steamtools-artwork-results-head {
          align-items: flex-start;
          flex-direction: column;
        }

        .steamtools-artwork-quality-legend {
          justify-content: flex-start;
        }
      }
    `;
    document.head.append(style);
  }

  function getWebpackRequire() {
    if (window.__steamToolsArtworkWebpackRequire) {
      return window.__steamToolsArtworkWebpackRequire;
    }

    const chunk = window.webpackChunksteamui;
    if (!Array.isArray(chunk) || typeof chunk.push !== "function") {
      return null;
    }

    let runtimeRequire = null;
    try {
      chunk.push([[`steam-tools-artwork-${Date.now()}`], {}, (require) => {
        runtimeRequire = require;
        window.__steamToolsArtworkWebpackRequire = require;
      }]);
    } catch (error) {
      console.warn("[Tools for Steam] Unable to capture Steam webpack runtime.", error);
    }

    return runtimeRequire;
  }

  function getFunctionSource(value) {
    try {
      if (typeof value === "function") {
        return value.toString();
      }

      if (typeof value?.render === "function") {
        return value.render.toString();
      }
    } catch {
    }

    return "";
  }

  function getSteamReact(runtimeRequire) {
    if (window.__steamToolsArtworkReact) {
      return window.__steamToolsArtworkReact;
    }

    if (!runtimeRequire?.m) {
      return null;
    }

    for (const moduleId of Object.keys(runtimeRequire.m)) {
      let exportsObject;
      try {
        exportsObject = runtimeRequire(moduleId);
      } catch {
        continue;
      }

      if (
        exportsObject &&
        typeof exportsObject === "object" &&
        typeof exportsObject.useContext === "function" &&
        typeof exportsObject.useState === "function" &&
        typeof exportsObject.cloneElement === "function"
      ) {
        window.__steamToolsArtworkReact = exportsObject;
        return exportsObject;
      }
    }

    return null;
  }

  function createFakeHookDispatcher() {
    const fakeNavigator = {
      AppProperties() {},
      Navigate() {},
      SteamWeb() {},
      location: { href: "" },
    };

    return {
      readContext: () => fakeNavigator,
      use: () => undefined,
      useCallback: (callback) => callback,
      useContext: () => fakeNavigator,
      useEffect: () => undefined,
      useImperativeHandle: () => undefined,
      useLayoutEffect: () => undefined,
      useInsertionEffect: () => undefined,
      useMemo: (factory) => factory(),
      useReducer: (_, initialValue) => [initialValue, () => undefined],
      useRef: (value) => ({ current: value }),
      useState: (value) => [typeof value === "function" ? value() : value, () => undefined],
      useDebugValue: () => undefined,
      useDeferredValue: (value) => value,
      useTransition: () => [false, (callback) => callback()],
      useSyncExternalStore: (_, getSnapshot) => getSnapshot?.(),
      useId: () => `steamtools-artwork-${Date.now()}`,
      useHostTransitionStatus: () => null,
      useFormState: (_, initialValue) => [initialValue, () => undefined],
      useActionState: (_, initialValue) => [initialValue, () => undefined, false],
      useOptimistic: (value) => [value, () => undefined],
    };
  }

  function withFakeReactDispatcher(runtimeRequire, callback) {
    const react = getSteamReact(runtimeRequire);
    const internals = react?.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;
    if (!internals || !Object.prototype.hasOwnProperty.call(internals, "H")) {
      return callback();
    }

    const originalDispatcher = internals.H;
    try {
      internals.H = createFakeHookDispatcher();
      return callback();
    } finally {
      internals.H = originalDispatcher;
    }
  }

  function getModuleExports(runtimeRequire, moduleId) {
    try {
      const exportsObject = runtimeRequire(moduleId);
      return exportsObject && typeof exportsObject === "object"
        ? Object.entries(exportsObject)
        : [["default", exportsObject]];
    } catch {
      return [];
    }
  }

  function findReactTree(root, predicate, maxDepth = 8) {
    const seen = new Set();
    const stack = [{ value: root, depth: 0 }];

    while (stack.length) {
      const { value, depth } = stack.pop();
      if (!value || depth > maxDepth || seen.has(value)) {
        continue;
      }

      if (typeof value === "object" || typeof value === "function") {
        seen.add(value);
      }

      try {
        if (predicate(value)) {
          return value;
        }
      } catch {
      }

      if (Array.isArray(value)) {
        for (const item of value) {
          stack.push({ value: item, depth: depth + 1 });
        }
        continue;
      }

      if (typeof value !== "object" && typeof value !== "function") {
        continue;
      }

      const props = value.props;
      if (props) {
        stack.push({ value: props.app, depth: depth + 1 });
        stack.push({ value: props.overview, depth: depth + 1 });
        stack.push({ value: props.children, depth: depth + 1 });
      }

      const pendingProps = value.pendingProps || value.memoizedProps || value._owner?.pendingProps;
      if (pendingProps) {
        stack.push({ value: pendingProps, depth: depth + 1 });
        stack.push({ value: pendingProps.app, depth: depth + 1 });
        stack.push({ value: pendingProps.overview, depth: depth + 1 });
        stack.push({ value: pendingProps.children, depth: depth + 1 });
      }

      if (value.app) {
        stack.push({ value: value.app, depth: depth + 1 });
      }

      if (value.overview) {
        stack.push({ value: value.overview, depth: depth + 1 });
      }
    }

    return null;
  }

  function getTitleFromValue(value) {
    return (
      value?.display_name ||
      value?.displayName ||
      value?.strDisplayName ||
      value?.name ||
      value?.title ||
      ""
    );
  }

  function getRawAppIdFromValue(value) {
    return value?.appid ?? value?.appId ?? value?.unAppID ?? value?.app_id ?? value?.app_id64;
  }

  function hasFiniteAppId(value) {
    return Number.isFinite(Number(getRawAppIdFromValue(value)));
  }

  function isUsableGameTitle(value) {
    if (typeof value !== "string") {
      return false;
    }

    const title = value.trim();
    if (title.length < 3) {
      return false;
    }

    return /[a-z0-9]/i.test(title) && !/^[a-z]{1,3}$/i.test(title);
  }

  function getBestTitleFromValue(value) {
    const preferred =
      value?.display_name ||
      value?.displayName ||
      value?.strDisplayName ||
      value?.title ||
      "";
    if (isUsableGameTitle(preferred)) {
      return preferred.trim();
    }

    const fallback = value?.name || "";
    return isUsableGameTitle(fallback) ? fallback.trim() : "";
  }

  function getArtworkContextFromReact(instance, root) {
    const props = instance?.props || {};
    const ownerProps = root?._owner?.pendingProps || {};
    const overview =
      props.overview ||
      ownerProps.overview ||
      findReactTree(root, (value) => value?.overview?.appid)?.overview ||
      findReactTree(root, (value) => value?.app?.appid)?.app ||
      findReactTree(root, (value) => hasFiniteAppId(value) && getTitleFromValue(value)) ||
      findReactTree(root, hasFiniteAppId, 10);

    const rawAppId =
      getRawAppIdFromValue(overview) ??
      getRawAppIdFromValue(props) ??
      getRawAppIdFromValue(ownerProps);

    const appId = normalizeSteamAppId(rawAppId);
    const titleObject =
      findReactTree(root, (value) => isUsableGameTitle(getBestTitleFromValue(value)), 10) ||
      overview ||
      props ||
      ownerProps;
    const title =
      getBestTitleFromValue(overview) ||
      getBestTitleFromValue(props) ||
      getBestTitleFromValue(ownerProps) ||
      getBestTitleFromValue(titleObject) ||
      "Selected Game";

    return { appId, title };
  }

  function isOpeningAppContextMenu(items) {
    if (!Array.isArray(items) || items.length === 0) {
      return false;
    }

    return Boolean(findReactTree(
      items,
      (value) => {
        const source = getFunctionSource(value?.props?.onSelected || value?.props?.onClick || value?.onSelected);
        return source.includes("launchSource");
      },
      9,
    ));
  }

  function normalizeMenuText(value) {
    return String(value || "")
      .replace(/\u00a0/g, " ")
      .replace(/\u2026/g, "...")
      .replace(/\s+/g, " ")
      .trim()
      .replace(/[.\s]+$/g, "")
      .trim()
      .toLowerCase();
  }

  function getSteamLocalization() {
    try {
      return window.LocalizationManager || window.opener?.LocalizationManager || null;
    } catch {
      return window.LocalizationManager || null;
    }
  }

  function localizeSteamToken(token) {
    try {
      const value = getSteamLocalization()?.LocalizeString?.(token);
      return typeof value === "string" && value && !value.startsWith("#") ? value : "";
    } catch {
      return "";
    }
  }

  // Normalized labels of one Steam menu command in every language TFS can
  // see right now: Steam's current UI language plus the English/German fallback.
  function getCommandLabels(name) {
    const manager = getSteamLocalization();
    let locale = "";
    try {
      locale = String(manager?.GetPreferredLocales?.()?.[0] || "");
    } catch {
    }
    if (!commandLabelCache || commandLabelCache.manager !== manager || commandLabelCache.locale !== locale) {
      commandLabelCache = { manager, locale, labels: new Map() };
    }

    let labels = commandLabelCache.labels.get(name);
    if (!labels) {
      labels = new Set((fallbackCommandLabels[name] || []).map(normalizeMenuText).filter(Boolean));
      for (const token of steamCommandTokens[name] || []) {
        const label = normalizeMenuText(localizeSteamToken(token));
        if (label) {
          labels.add(label);
        }
      }
      commandLabelCache.labels.set(name, labels);
    }
    return labels;
  }

  function textIncludesCommand(text, name) {
    for (const label of getCommandLabels(name)) {
      if (text.includes(label)) {
        return true;
      }
    }
    return false;
  }

  function isPropertiesText(value) {
    return getCommandLabels("properties").has(normalizeMenuText(value));
  }

  function isCancelText(value) {
    return getCommandLabels("cancel").has(normalizeMenuText(value));
  }

  // Steam's "Properties" entry calls navigator.AppProperties(appid). Matching
  // that handler (not the label) finds it in every Steam UI language.
  function isAppPropertiesMenuItem(item) {
    const source = getFunctionSource(item?.props?.onSelected || item?.props?.onClick || item?.onSelected);
    return source.includes("AppProperties");
  }

  function hasNestedAppPropertiesMenuItem(items) {
    return items.some((item) => {
      const children = item?.props?.children;
      return Array.isArray(children) && children.some(isAppPropertiesMenuItem);
    });
  }

  function findPropertiesMenuIndex(items) {
    return items.findIndex((item) => {
      const source = getFunctionSource(item?.props?.onSelected || item?.props?.onClick || item?.onSelected);
      const key = String(item?.key || "").toLowerCase();
      const label = getMenuItemText(item);

      return (
        source.includes("AppProperties") ||
        key === "properties" ||
        isPropertiesText(label)
      );
    });
  }

  function getMenuItemText(item) {
    const children = item?.props?.children ?? item?.children;
    if (typeof children === "string") {
      return children.trim();
    }

    if (Array.isArray(children)) {
      return children
        .map((child) => (typeof child === "string" ? child : getMenuItemText(child)))
        .join(" ")
        .replace(/\s+/g, " ")
        .trim();
    }

    if (children && typeof children === "object") {
      return getMenuItemText(children);
    }

    return "";
  }

  function removeArtworkContextRows(root = document) {
    for (const row of root.querySelectorAll(".steamtools-artwork-context-row")) {
      row.remove();
    }
  }

  function removeOmniLibraryUninstallContextRows(root = document) {
    for (const row of root.querySelectorAll(
      ".steamtools-omnilibrary-uninstall-context-row, .steamtools-omnilibrary-repair-context-row, .steamtools-omnilibrary-data-context-row",
    )) {
      row.remove();
    }
  }

  function removeOmniLibraryUninstallNotice() {
    if (state.omniLibraryUninstallNoticeTimer) {
      window.clearTimeout(state.omniLibraryUninstallNoticeTimer);
      state.omniLibraryUninstallNoticeTimer = null;
    }
    document.getElementById(omniLibraryUninstallNoticeId)?.remove();
  }

  function showOmniLibraryUninstallNotice(storeId, errorMessage = "", override = null) {
    removeOmniLibraryUninstallNotice();

    const payload = {
      type: "uninstall-notice",
      storeId,
      errorMessage,
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
    };
    let deliveredOutsideMenu = false;
    if (typeof window.BroadcastChannel === "function") {
      try {
        const channel = new window.BroadcastChannel(
          omniLibraryStoreChannelName,
        );
        channel.postMessage(payload);
        window.setTimeout(() => channel.close(), 100);
        deliveredOutsideMenu = true;
      } catch {
      }
    }

    // A context menu can be hosted by a separate Steam surface which becomes
    // hidden immediately after selection. Never render into the current
    // window first: doing so produces a real notice that the user cannot see.
    if (!deliveredOutsideMenu) {
      const candidateWindows = [];
      try {
        if (window.opener) {
          candidateWindows.push(window.opener);
        }
        if (window.parent && window.parent !== window) {
          candidateWindows.push(window.parent);
        }
        if (window.top && window.top !== window) {
          candidateWindows.push(window.top);
        }
      } catch {
      }

      for (const candidateWindow of candidateWindows) {
        try {
          const showInLibrary =
            candidateWindow?.__steamLoaderLibraryTabsState?.showUninstallNotice;
          if (typeof showInLibrary === "function") {
            showInLibrary(payload);
            deliveredOutsideMenu = true;
            break;
          }
        } catch {
        }
      }
    }

    if (deliveredOutsideMenu) {
      return;
    }

    // Last-resort fallback for Steam builds that isolate the context menu from
    // both its opener and BroadcastChannel.
    const failed = Boolean(errorMessage);
    const isRefreshNotice = override?.kind === "refresh";
    const notice = document.createElement("div");
    notice.id = omniLibraryUninstallNoticeId;
    notice.setAttribute("role", failed ? "alert" : "status");
    notice.setAttribute("aria-live", failed ? "assertive" : "polite");
    notice.style.cssText = [
      "position:fixed",
      "z-index:2147483646",
      "right:40px",
      "bottom:116px",
      "width:min(420px,calc(100vw - 80px))",
      "box-sizing:border-box",
      "display:grid",
      "grid-template-columns:58px minmax(0,1fr)",
      "align-items:center",
      "overflow:hidden",
      "border-radius:7px",
      "border:1px solid rgba(119,151,178,.28)",
      "background:linear-gradient(135deg,rgba(35,46,59,.985),rgba(20,27,36,.985))",
      "box-shadow:0 16px 42px rgba(0,0,0,.52),0 2px 8px rgba(0,0,0,.34)",
      "color:#f3f6fa",
      "font-family:Motiva Sans,Arial,sans-serif",
      "pointer-events:none",
    ].join(";");

    const accent = document.createElement("div");
    accent.style.cssText = [
      "position:absolute",
      "inset:0 0 auto 0",
      "height:2px",
      `background:linear-gradient(90deg,${failed ? "#ff6675,#d83f51" : "#66c0f4,#2a78b8"})`,
    ].join(";");

    const icon = document.createElement("div");
    icon.setAttribute("aria-hidden", "true");
    icon.style.cssText = [
      "width:34px",
      "height:34px",
      "margin-left:14px",
      "display:flex",
      "align-items:center",
      "justify-content:center",
      "border-radius:50%",
      `background:${failed ? "rgba(255,102,117,.16)" : isRefreshNotice ? "rgba(102,192,244,.16)" : storeId === "xbox-game-pass" ? "rgba(74,181,65,.18)" : "rgba(102,192,244,.16)"}`,
      `border:1px solid ${failed ? "rgba(255,102,117,.52)" : isRefreshNotice ? "rgba(102,192,244,.5)" : storeId === "xbox-game-pass" ? "rgba(107,206,95,.48)" : "rgba(102,192,244,.5)"}`,
      `color:${failed ? "#ff8994" : isRefreshNotice ? "#76c9f6" : storeId === "xbox-game-pass" ? "#7bd56f" : "#76c9f6"}`,
      "font-size:17px",
      "font-weight:800",
      "line-height:1",
    ].join(";");
    icon.textContent = failed
      ? "!"
      : isRefreshNotice
        ? "\u21bb"
        : storeId === "xbox-game-pass"
        ? "X"
        : "\u2193";

    const content = document.createElement("div");
    content.style.cssText = "min-width:0;padding:15px 18px 16px 7px";

    const title = document.createElement("div");
    title.style.cssText =
      "font-size:14px;font-weight:800;line-height:1.25;letter-spacing:.35px";
    title.textContent = override?.title || (failed
      ? "Uninstall failed"
      : storeId === "xbox-game-pass"
        ? "Continue in Xbox"
        : storeId === "gog-galaxy"
          ? "Uninstalling GOG game"
          : "Uninstalling Epic game");

    const message = document.createElement("div");
    message.style.cssText =
      "margin-top:4px;color:rgba(215,226,237,.76);font-size:13px;font-weight:500;line-height:1.4";
    message.textContent = override?.message || (failed
      ? errorMessage
      : storeId === "xbox-game-pass"
        ? "Please finish uninstalling this game in the Xbox window."
        : storeId === "gog-galaxy"
          ? "Please wait. Managed installs are removed automatically; GOG Galaxy opens only when it owns the installation."
          : "Please wait while OmniLibrary uninstalls this game automatically.");

    content.append(title, message);
    notice.append(accent, icon, content);
    // Keep the notice outside Steam's React-owned root. Closing the context
    // menu reconciles that root immediately and would otherwise delete it.
    document.body.appendChild(notice);
    notice.animate?.(
      [
        { opacity: 0, transform: "translateY(14px) scale(.985)" },
        { opacity: 1, transform: "translateY(0) scale(1)" },
      ],
      { duration: 180, easing: "cubic-bezier(.2,.8,.2,1)" },
    );
    state.omniLibraryUninstallNoticeTimer = window.setTimeout(
      removeOmniLibraryUninstallNotice,
      failed ? 7000 : 6000,
    );
  }

  async function loadArtworkSettings() {
    installOmniLibraryContextMenuLifecycle();
    try {
      const response = await fetch(`${apiBase}api/artwork/state`, { cache: "no-store" });
      const payload = await response.json().catch(() => null);
      const enabled = response.ok && payload?.settings?.contextMenuEnabled !== false;
      const changed = state.contextMenuEnabled !== enabled || !state.artworkSettingsLoaded;

      state.contextMenuEnabled = enabled;
      state.artworkSettingsLoaded = true;

      if (!enabled) {
        removeArtworkContextRows();
      } else if (changed) {
        patchMenus();
      }
    } catch {
      state.contextMenuEnabled = false;
      state.artworkSettingsLoaded = true;
      removeArtworkContextRows();
    }
  }

  function startArtworkSettingsPolling() {
    if (state.artworkSettingsTimer) {
      return;
    }

    void loadArtworkSettings();
    state.artworkSettingsTimer = window.setInterval(() => {
      void loadArtworkSettings();
    }, 2500);
  }

  function requestArtworkPanel(context) {
    const normalizedContext = normalizeArtworkContext(context) || getRememberedArtworkContext();
    const appId = normalizeSteamAppId(normalizedContext?.appId);
    if (!appId || !state.contextMenuEnabled) {
      if (!appId) {
        console.warn("[Tools for Steam] Artwork panel was requested without a Steam app id.", context, getRememberedArtworkContext());
      }
      return;
    }

    const title = normalizedContext?.title || "Selected Game";
    rememberArtworkContext({ appId, title });
    const requestKey = getOpenRequestKey(appId, title);
    const now = Date.now();
    if (
      (state.overlay && state.currentOpenKey === requestKey) ||
      (state.lastPanelRequestKey === requestKey && now - state.lastPanelRequestAt < 1600)
    ) {
      return;
    }

    state.lastPanelRequestKey = requestKey;
    state.lastPanelRequestAt = now;

    const request = {
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      appId,
      title,
    };

    try {
      localStorage.setItem(openRequestStorageKey, JSON.stringify(request));
    } catch {
    }

    const payload = {
      appId,
      title: request.title,
    };

    fetch(`${apiBase}api/artwork/open-request`, {
      method: "POST",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    }).catch((error) => {
      console.warn("[Tools for Steam] Unable to request artwork panel.", error);
    });
  }

  function getOmniLibraryStateStore() {
    try {
      return window.__steamLoaderOmniLibraryStateStore ||
        window.opener?.__steamLoaderOmniLibraryStateStore ||
        null;
    } catch {
      return window.__steamLoaderOmniLibraryStateStore || null;
    }
  }

  function publishOmniLibraryLifecycleStatus(appId, status) {
    if (typeof window.BroadcastChannel !== "function") {
      return;
    }

    try {
      const channel = new window.BroadcastChannel(
        omniLibraryStoreChannelName,
      );
      channel.postMessage({
        type: "download-status-changed",
        appId,
        status,
      });
      window.setTimeout(() => channel.close(), 100);
    } catch {
    }
  }

  function publishOmniLibraryMetadataSnapshot(snapshot) {
    if (!snapshot || typeof window.BroadcastChannel !== "function") {
      return;
    }
    try {
      const channel = new window.BroadcastChannel(omniLibraryStoreChannelName);
      channel.postMessage({ type: "native-metadata", snapshot });
      window.setTimeout(() => channel.close(), 100);
    } catch {
    }
  }

  function installOmniLibraryContextMenuLifecycle() {
    if (state.omniLibraryStateUnsubscribe) {
      return;
    }

    const shared = getOmniLibraryStateStore();
    if (typeof shared?.subscribe !== "function") {
      return;
    }

    state.omniLibraryStateUnsubscribe = shared.subscribe((snapshot) => {
      if (snapshot?.pluginEnabled !== true) {
        removeOmniLibraryUninstallContextRows();
        removeOmniLibraryUninstallNotice();
      }
      window.clearTimeout(state.refreshTimer);
      state.refreshTimer = window.setTimeout(patchMenus, 0);
    });
  }

  function getInstalledOmniLibraryStore(appId) {
    const normalizedAppId = normalizeSteamAppId(appId);
    if (!normalizedAppId) {
      return null;
    }

    const snapshot = getOmniLibraryStateStore()?.snapshot;
    if (snapshot?.pluginEnabled !== true) {
      return null;
    }
    return (snapshot?.stores || []).find((store) =>
      store?.enabled === true &&
      store?.supportsUninstall === true &&
      (store?.installedAppIds || []).some((candidate) =>
        normalizeSteamAppId(candidate) === normalizedAppId)) || null;
  }

  function getManagedOmniLibraryStore(appId) {
    const normalizedAppId = normalizeSteamAppId(appId);
    if (!normalizedAppId) {
      return null;
    }
    const snapshot = getOmniLibraryStateStore()?.snapshot;
    if (snapshot?.pluginEnabled !== true) {
      return null;
    }
    return (snapshot?.stores || []).find((store) =>
      store?.enabled === true &&
      (store?.appIds || []).some((candidate) =>
        normalizeSteamAppId(candidate) === normalizedAppId)) || null;
  }

  function isRepairableOmniLibraryGame(store, appId) {
    const normalizedAppId = normalizeSteamAppId(appId);
    return (
      store?.id === "gog-galaxy" &&
      normalizedAppId > 0 &&
      (store?.repairableAppIds || []).some((candidate) =>
        normalizeSteamAppId(candidate) === normalizedAppId)
    );
  }

  async function requestOmniLibraryUninstall(context, storeId) {
    const appId = normalizeSteamAppId(context?.appId);
    const requestKey = `${storeId}:${appId}`;
    if (!appId || !storeId || state.uninstallRequests.has(requestKey)) {
      return;
    }

    state.uninstallRequests.add(requestKey);
    showOmniLibraryUninstallNotice(storeId);
    try {
      const detailResponse = await fetch(
        `${apiBase}api/unifystore/games/${encodeURIComponent(appId)}`,
        { cache: "no-store" },
      );
      const detail = await detailResponse.json().catch(() => null);
      const game = detail?.game || null;
      if (
        !detailResponse.ok ||
        !game?.id ||
        game?.installed !== true ||
        String(detail?.storeId || "") !== storeId
      ) {
        throw new Error("This OmniLibrary game is no longer installed.");
      }

      const response = await fetch(`${apiBase}api/unifystore/games/uninstall`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          storeId,
          gameId: game.id,
        }),
      });
      const payload = await response.json().catch(() => null);
      if (!response.ok) {
        throw new Error(payload?.message || `Uninstall request failed (${response.status}).`);
      }

      publishOmniLibraryLifecycleStatus(appId, "uninstalling");
      await getOmniLibraryStateStore()?.refresh?.(true);
    } catch (error) {
      console.warn("[Tools for Steam] Unable to uninstall the OmniLibrary game.", error);
      showOmniLibraryUninstallNotice(
        storeId,
        error instanceof Error ? error.message : String(error),
      );
    } finally {
      state.uninstallRequests.delete(requestKey);
    }
  }

  async function requestOmniLibraryRepair(context, storeId) {
    const appId = normalizeSteamAppId(context?.appId);
    const requestKey = `${storeId}:${appId}`;
    if (
      !appId ||
      storeId !== "gog-galaxy" ||
      state.repairRequests.has(requestKey)
    ) {
      return;
    }

    state.repairRequests.add(requestKey);
    try {
      const detailResponse = await fetch(
        `${apiBase}api/unifystore/games/${encodeURIComponent(appId)}`,
        { cache: "no-store" },
      );
      const detail = await detailResponse.json().catch(() => null);
      const game = detail?.game || null;
      if (
        !detailResponse.ok ||
        !game?.id ||
        game?.installed !== true ||
        String(detail?.storeId || "") !== storeId
      ) {
        throw new Error("This GOG game is no longer installed.");
      }

      const response = await fetch(`${apiBase}api/unifystore/games/repair`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          storeId,
          gameId: game.id,
        }),
      });
      const payload = await response.json().catch(() => null);
      if (!response.ok) {
        throw new Error(payload?.message || `Repair request failed (${response.status}).`);
      }

      publishOmniLibraryLifecycleStatus(appId, "preparing");
      await getOmniLibraryStateStore()?.refresh?.(true);
    } catch (error) {
      console.warn("[Tools for Steam] Unable to repair the GOG game.", error);
      publishOmniLibraryLifecycleStatus(appId, "failed");
    } finally {
      state.repairRequests.delete(requestKey);
    }
  }

  function createReactRepairMenuItem(template, context, storeId) {
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) {
        return;
      }
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      void requestOmniLibraryRepair(context, storeId);
    };
    const templateProps = template?.props || {};
    const className = [
      templateProps.className,
      "steamtools-omnilibrary-repair-context-row",
    ].filter(Boolean).join(" ");

    return {
      ...template,
      key: "tfs-omnilibrary-repair",
      props: {
        ...templateProps,
        disabled: false,
        className,
        "data-steamtools-omnilibrary-repair-row": "true",
        "data-steamtools-omnilibrary-app-id": String(
          normalizeSteamAppId(context?.appId) || "",
        ),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: "Verify & Repair...",
      },
    };
  }

  function createReactOmniLibraryDataMenuItem(template, context, storeId) {
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) {
        return;
      }
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      openMetadataOverlay(context, storeId);
    };
    const templateProps = template?.props || {};
    return {
      ...template,
      key: "tfs-omnilibrary-data-refresh",
      props: {
        ...templateProps,
        disabled: false,
        className: [
          templateProps.className,
          "steamtools-omnilibrary-data-context-row",
        ].filter(Boolean).join(" "),
        "data-steamtools-omnilibrary-data-row": "true",
        "data-steamtools-omnilibrary-app-id": String(
          normalizeSteamAppId(context?.appId) || "",
        ),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: "Fix Game Data...",
      },
    };
  }


  function createReactUninstallMenuItem(template, context, storeId) {
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) {
        return;
      }
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      void requestOmniLibraryUninstall(context, storeId);
    };
    const templateProps = template?.props || {};
    const className = [
      templateProps.className,
      "steamtools-omnilibrary-uninstall-context-row",
    ].filter(Boolean).join(" ");

    return {
      ...template,
      key: "tfs-omnilibrary-uninstall",
      props: {
        ...templateProps,
        disabled: false,
        className,
        "data-steamtools-omnilibrary-uninstall-row": "true",
        "data-steamtools-omnilibrary-app-id": String(
          normalizeSteamAppId(context?.appId) || "",
        ),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: "Uninstall...",
      },
    };
  }

  function createReactArtworkMenuItem(template, context) {
    const normalizedContext = rememberArtworkContext(context) || context;
    const onSelected = (event) => {
      if (event?.preventDefault) {
        event.preventDefault();
      }
      if (event?.stopPropagation) {
        event.stopPropagation();
      }
      // The TFS Artworks page (a real Steam route from artwork-picker.js) is
      // preferred; the overlay stays as fallback while it is not loaded.
      const page = getArtworkPageRuntime();
      const pageAppId = normalizeSteamAppId(normalizedContext?.appId);
      if (pageAppId && page?.hasEditor?.() === true && page.openEditor(pageAppId)) return;
      requestArtworkPanel(normalizedContext);
    };
    const templateProps = template?.props || {};
    const className = [templateProps.className, "steamtools-artwork-context-row"]
      .filter(Boolean)
      .join(" ");

    return {
      ...template,
      key: "tfs-change-artwork",
      props: {
        ...templateProps,
        disabled: false,
        className,
        "data-steamtools-artwork-row": "true",
        "data-steamtools-artwork-app-id": String(normalizeSteamAppId(normalizedContext?.appId) || ""),
        "data-steamtools-artwork-title": normalizedContext?.title || "Selected Game",
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: "TFS Artworks...",
      },
    };
  }

  function createReactCurtainMenuItem(template, context) {
    const normalizedContext = rememberArtworkContext(context) || context;
    const appId = normalizeSteamAppId(normalizedContext?.appId);
    const curtain = window.ToolsForSteamCurtain;
    const enabled = curtain?.isGameEnabled?.(appId) === true;
    // With the shared runtime the entry opens the per-game TFS Curtain page
    // (a Steam route); without it, it falls back to the old on/off toggle.
    const editor = curtain?.hasEditor?.() === true;
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) return;
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      if (editor && curtain?.openEditor?.(appId)) return;
      void curtain?.toggleGame?.(
        appId,
        !enabled,
        normalizedContext?.title || "Selected game",
      );
    };
    const templateProps = template?.props || {};
    return {
      ...template,
      key: "tfs-curtain-toggle",
      props: {
        ...templateProps,
        disabled: false,
        className: [templateProps.className, "steamtools-curtain-context-row"]
          .filter(Boolean)
          .join(" "),
        "data-steamtools-curtain-row": "true",
        "data-steamtools-curtain-app-id": String(appId || ""),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: editor ? `TFS Curtain... (${enabled ? "On" : "Off"})` : `TFS Curtain: ${enabled ? "On" : "Off"}`,
      },
    };
  }

  // TFS Trailers lives in Steam's shared context (trailer-core.js); Big
  // Picture reaches it through window.opener.
  function getTrailerRuntime() {
    try {
      return window.__tfsTrailers || window.opener?.__tfsTrailers || null;
    } catch {
      return null;
    }
  }

  function getArtworkPageRuntime() {
    try {
      return window.__tfsArtworks || window.opener?.__tfsArtworks || null;
    } catch {
      return null;
    }
  }

  function createReactTrailerMenuItem(template, context) {
    const normalizedContext = rememberArtworkContext(context) || context;
    const appId = normalizeSteamAppId(normalizedContext?.appId);
    const trailers = getTrailerRuntime();
    const enabled = trailers?.isGameEnabled?.(appId) === true;
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) return;
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      getTrailerRuntime()?.openEditor?.(appId);
    };
    const templateProps = template?.props || {};
    return {
      ...template,
      key: "tfs-trailer-settings",
      props: {
        ...templateProps,
        disabled: false,
        className: [templateProps.className, "steamtools-trailer-context-row"]
          .filter(Boolean)
          .join(" "),
        "data-steamtools-trailer-row": "true",
        "data-steamtools-trailer-app-id": String(appId || ""),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: `TFS Trailer... (${enabled ? "On" : "Off"})`,
      },
    };
  }

  // TFS Overture (game theme music) lives in the shared context as well.
  function getOvertureRuntime() {
    try {
      return window.__tfsOverture || window.opener?.__tfsOverture || null;
    } catch {
      return null;
    }
  }

  function createReactOvertureMenuItem(template, context) {
    const normalizedContext = rememberArtworkContext(context) || context;
    const appId = normalizeSteamAppId(normalizedContext?.appId);
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) return;
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      getOvertureRuntime()?.openEditor?.(appId);
    };
    const templateProps = template?.props || {};
    return {
      ...template,
      key: "tfs-overture-settings",
      props: {
        ...templateProps,
        disabled: false,
        className: [templateProps.className, "steamtools-overture-context-row"]
          .filter(Boolean)
          .join(" "),
        "data-steamtools-overture-row": "true",
        "data-steamtools-overture-app-id": String(appId || ""),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: "TFS Overture...",
      },
    };
  }

  // TFS Metadata (game details for non-Steam games), shared context.
  function getMetadataRuntime() {
    try {
      return window.__tfsMetadata || window.opener?.__tfsMetadata || null;
    } catch {
      return null;
    }
  }

  function createReactMetadataMenuItem(template, context) {
    const normalizedContext = rememberArtworkContext(context) || context;
    const appId = normalizeSteamAppId(normalizedContext?.appId);
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) return;
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      getMetadataRuntime()?.openEditor?.(appId);
    };
    const templateProps = template?.props || {};
    return {
      ...template,
      key: "tfs-metadata-editor",
      props: {
        ...templateProps,
        disabled: false,
        className: [templateProps.className, "steamtools-metadata-context-row"]
          .filter(Boolean)
          .join(" "),
        "data-steamtools-metadata-row": "true",
        "data-steamtools-metadata-app-id": String(appId || ""),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: "TFS Metadata...",
      },
    };
  }

  // TFS game profiles (TDP, frame rate, RTSS ... per game) from perf-tab.js, shared context.
  // Offered only when this device has TDP control (handheld with a known AMD TDP range).
  function getPerfTabRuntime() {
    try {
      return window.__tfsPerfTab || window.opener?.__tfsPerfTab || null;
    } catch {
      return null;
    }
  }

  function createReactGameProfileMenuItem(template, context) {
    const normalizedContext = rememberArtworkContext(context) || context;
    const appId = normalizeSteamAppId(normalizedContext?.appId);
    const title = String(normalizedContext?.title || "");
    const perfTab = getPerfTabRuntime();
    const exists = perfTab?.hasGameProfileForApp?.(appId) === true;
    let lastActivationAt = 0;
    const onSelected = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) return;
      lastActivationAt = now;
      event?.preventDefault?.();
      event?.stopPropagation?.();
      getPerfTabRuntime()?.openGameProfileForApp?.(appId, title);
    };
    const templateProps = template?.props || {};
    return {
      ...template,
      key: "tfs-game-profile",
      props: {
        ...templateProps,
        disabled: false,
        className: [templateProps.className, "steamtools-game-profile-context-row"]
          .filter(Boolean)
          .join(" "),
        "data-steamtools-game-profile-row": "true",
        "data-steamtools-game-profile-app-id": String(appId || ""),
        onClick: onSelected,
        onMouseUp: onSelected,
        onPointerUp: onSelected,
        onSelected,
        children: perfTab?.gameProfileMenuLabel?.(exists) || (exists ? "TFS Game Profile..." : "TFS Game Profile (new)..."),
      },
    };
  }

  function isFocusableReactMenuTemplate(item) {
    return Boolean(
      item?.props &&
      typeof item.props.onSelected === "function" &&
      typeof item.type !== "symbol",
    );
  }

  function getReactMenuItemTemplate(items, beforeIndex) {
    for (let index = beforeIndex - 1; index >= 0; index -= 1) {
      if (isFocusableReactMenuTemplate(items[index]) && getMenuItemText(items[index])) {
        return items[index];
      }
    }

    return items.find(isFocusableReactMenuTemplate) || items[beforeIndex];
  }

  function patchReactMenuItems(items, context) {
    // The game context menu is recognized by Steam's own handlers, never by
    // its labels: the Properties entry (also in the game page's options menu,
    // which has no Play entry) or the launch entry.
    const hasAppPropertiesItem = Array.isArray(items) && items.some(isAppPropertiesMenuItem);
    if (!hasAppPropertiesItem && !isOpeningAppContextMenu(items)) {
      return false;
    }

    for (let index = items.length - 1; index >= 0; index -= 1) {
      if (
        items[index]?.key === "tfs-change-artwork" ||
        items[index]?.key === "tfs-curtain-toggle" ||
        items[index]?.key === "tfs-trailer-settings" ||
        items[index]?.key === "tfs-overture-settings" ||
        items[index]?.key === "tfs-metadata-editor" ||
        items[index]?.key === "tfs-game-profile" ||
        items[index]?.key === "tfs-omnilibrary-data-refresh" ||
        items[index]?.key === "tfs-omnilibrary-repair" ||
        items[index]?.key === "tfs-omnilibrary-uninstall"
      ) {
        items.splice(index, 1);
      }
    }

    // Current Steam builds keep Properties in a Fragment together with its
    // separator. That nested array is patched on its own, so the entries are
    // not added a second time at this level.
    if (!hasAppPropertiesItem && hasNestedAppPropertiesMenuItem(items)) {
      return false;
    }

    const propertiesIndex = hasAppPropertiesItem
      ? items.findIndex(isAppPropertiesMenuItem)
      : findPropertiesMenuIndex(items);
    if (propertiesIndex < 0) {
      return false;
    }

    const template = getReactMenuItemTemplate(items, propertiesIndex);
    if (!template) {
      return false;
    }

    const itemContext = getArtworkContextFromReact(null, items);
    const resolvedContext = {
      appId: itemContext.appId || context?.appId || 0,
      title: itemContext.title && itemContext.title !== "Selected Game" ? itemContext.title : context?.title || "Selected Game",
    };

    if (!resolvedContext.appId) {
      return false;
    }

    rememberArtworkContext(resolvedContext);
    const installedStore = getInstalledOmniLibraryStore(resolvedContext.appId);
    const additions = [];
    if (window.ToolsForSteamCurtain?.snapshot?.enabled === true) {
      additions.push(createReactCurtainMenuItem(template, resolvedContext));
    }
    if (getTrailerRuntime()?.hasEditor?.() === true) {
      additions.push(createReactTrailerMenuItem(template, resolvedContext));
    }
    if (getOvertureRuntime()?.hasEditor?.() === true) {
      additions.push(createReactOvertureMenuItem(template, resolvedContext));
    }
    if (getMetadataRuntime()?.hasEditor?.(resolvedContext.appId) === true) {
      additions.push(createReactMetadataMenuItem(template, resolvedContext));
    }
    if (getPerfTabRuntime()?.hasGameProfiles?.() === true) {
      additions.push(createReactGameProfileMenuItem(template, resolvedContext));
    }
    if (state.contextMenuEnabled) {
      additions.push(createReactArtworkMenuItem(template, resolvedContext));
    }
    // "Fix Game Data..." is no longer offered: "TFS Metadata..." covers the
    // Steam match for OmniLibrary games (Luis, 2026-11-09).
    if (installedStore) {
      if (isRepairableOmniLibraryGame(installedStore, resolvedContext.appId)) {
        additions.push(
          createReactRepairMenuItem(template, resolvedContext, installedStore.id),
        );
      }
      additions.push(
        createReactUninstallMenuItem(template, resolvedContext, installedStore.id),
      );
    }
    if (!additions.length) {
      return false;
    }

    items.splice(propertiesIndex, 0, ...additions.map(withMenuAutoClose));
    return true;
  }

  /**
   * Steam's menu item hides its menu only when its own click path reaches the
   * end; our entries act earlier (pointer up, or they navigate to a TFS page),
   * which left the context menu open over the new page. Close it explicitly
   * through the menu instance found in the item's React fiber.
   */
  function closeSteamContextMenu(node) {
    window.setTimeout(() => {
      try {
        const fiberKey = node && Object.keys(node).find((key) => key.startsWith("__reactFiber$"));
        let fiber = fiberKey ? node[fiberKey] : null;
        for (let depth = 0; fiber && depth < 60; depth += 1, fiber = fiber.return) {
          const instance = fiber.stateNode?.instance;
          if (instance && typeof instance.ForceHide === "function") {
            instance.ForceHide();
            return;
          }
        }
      } catch {}
    }, 0);
  }

  function withMenuAutoClose(item) {
    const props = item?.props;
    if (!props) return item;
    const wrap = (handler) => (typeof handler === "function"
      ? (event, ...rest) => {
        const node = event?.currentTarget || event?.target || null;
        try {
          return handler(event, ...rest);
        } finally {
          closeSteamContextMenu(node);
        }
      }
      : handler);
    return {
      ...item,
      props: {
        ...props,
        onSelected: wrap(props.onSelected),
        onClick: wrap(props.onClick),
        onMouseUp: wrap(props.onMouseUp),
        onPointerUp: wrap(props.onPointerUp),
      },
    };
  }

  function patchMenuRenderOutput(rendered, context) {
    const resolvedContext = getArtworkContextFromReact(null, rendered);
    const mergedContext = {
      appId: resolvedContext.appId || context?.appId || 0,
      title:
        resolvedContext.title && resolvedContext.title !== "Selected Game"
          ? resolvedContext.title
          : context?.title || "Selected Game",
    };

    return patchReactElementTree(rendered, mergedContext);
  }

  function patchReactElementTree(root, context) {
    const seen = new Set();
    let patched = false;

    const visit = (value, depth = 0) => {
      if (!value || depth > 10) {
        return;
      }

      if (Array.isArray(value)) {
        patched = patchReactMenuItems(value, context) || patched;
        for (const item of value) {
          visit(item, depth + 1);
        }
        return;
      }

      if (typeof value !== "object" && typeof value !== "function") {
        return;
      }

      if (seen.has(value)) {
        return;
      }
      seen.add(value);

      const children = value.props?.children;
      if (Array.isArray(children)) {
        patched = patchReactMenuItems(children, context) || patched;
      }
      visit(children, depth + 1);
    };

    visit(root);
    return patched;
  }

  function patchInnerMenuClass(element, context) {
    const prototype = element?.type?.prototype;
    if (!prototype?.render) {
      return false;
    }

    if ((prototype.render.__steamToolsArtworkInnerPatchVersion || 0) < version) {
      const originalRender = prototype.render;
      prototype.render = function patchedArtworkInnerMenuRender(...args) {
        const rendered = originalRender.apply(this, args);
        try {
          patchMenuRenderOutput(rendered, getArtworkContextFromReact(this, rendered) || context);
        } catch (error) {
          console.warn("[Tools for Steam] Unable to patch inner artwork menu render.", error);
        }
        return rendered;
      };
      prototype.render.__steamToolsArtworkInnerPatchVersion = version;
    }

    if (
      typeof prototype.shouldComponentUpdate === "function" &&
      (prototype.shouldComponentUpdate.__steamToolsArtworkInnerPatchVersion || 0) < version
    ) {
      const originalShouldComponentUpdate = prototype.shouldComponentUpdate;
      prototype.shouldComponentUpdate = function patchedArtworkInnerMenuUpdate(nextProps, ...args) {
        const shouldUpdate = originalShouldComponentUpdate.apply(this, [nextProps, ...args]);
        try {
          const nextChildren = nextProps?.children;
          if (Array.isArray(nextChildren)) {
            patchReactMenuItems(nextChildren, context);
          }
        } catch {
        }
        return shouldUpdate;
      };
      prototype.shouldComponentUpdate.__steamToolsArtworkInnerPatchVersion = version;
    }

    return true;
  }

  function patchReturnedMenuComponent(component, context) {
    if (!component || typeof component !== "object" || typeof component.type !== "function") {
      return false;
    }

    patchInnerMenuClass(component, context);

    if (component.type.prototype?.render) {
      return true;
    }

    if ((component.type.__steamToolsArtworkPatchVersion || 0) >= version) {
      return true;
    }

    const originalType = component.type;
    const patchedType = function patchedArtworkMenuType(...args) {
      const rendered = originalType.apply(this, args);
      try {
        patchInnerMenuClass(rendered, context);
        patchMenuRenderOutput(rendered, context);
      } catch (error) {
        console.warn("[Tools for Steam] Unable to patch returned artwork menu component.", error);
      }
      return rendered;
    };

    try {
      Object.defineProperty(patchedType, "name", { value: originalType.name, configurable: true });
    } catch {
    }
    patchedType.prototype = originalType.prototype;
    patchedType.displayName = originalType.displayName;
    patchedType.__steamToolsArtworkPatchVersion = version;
    component.type = patchedType;
    return true;
  }

  function findLibraryContextMenuType(runtimeRequire) {
    if (window.__steamToolsArtworkLibraryContextMenuType) {
      return window.__steamToolsArtworkLibraryContextMenuType;
    }

    if (!runtimeRequire?.m) {
      return null;
    }

    for (const moduleId of Object.keys(runtimeRequire.m)) {
      const exportsList = getModuleExports(runtimeRequire, moduleId);
      if (!exportsList.some(([, value]) => getFunctionSource(value).includes("().LibraryContextMenu"))) {
        continue;
      }

      const wrapper = exportsList
        .map(([, value]) => value)
        .find((value) => getFunctionSource(value).includes("navigator:"));

      if (typeof wrapper !== "function") {
        continue;
      }

      try {
        const element = withFakeReactDispatcher(runtimeRequire, () => wrapper({}));
        if (typeof element?.type === "function" && element.type.prototype?.render) {
          window.__steamToolsArtworkLibraryContextMenuType = element.type;
          return element.type;
        }
      } catch (error) {
        console.warn("[Tools for Steam] Unable to fake-render LibraryContextMenu.", error);
      }
    }

    return null;
  }

  function installReactContextMenuPatch() {
    if (state.reactPatchInstalled) {
      return true;
    }

    const runtimeRequire = getWebpackRequire();
    const LibraryContextMenu = findLibraryContextMenuType(runtimeRequire);
    const prototype = LibraryContextMenu?.prototype;
    if (!prototype?.render) {
      state.reactPatchInstalled = false;
      return state.reactPatchInstalled;
    }

    if ((prototype.render.__steamToolsArtworkPatchVersion || 0) >= version) {
      state.reactPatchInstalled = true;
      return true;
    }

    const originalRender = prototype.render;
    prototype.render = function patchedArtworkContextMenuRender(...args) {
      const rendered = originalRender.apply(this, args);
      try {
        const context = getArtworkContextFromReact(this, rendered);
        patchReturnedMenuComponent(rendered, context);
        patchMenuRenderOutput(rendered, context);
      } catch (error) {
        console.warn("[Tools for Steam] Unable to patch the Steam game context menu.", error);
      }
      return rendered;
    };
    prototype.render.__steamToolsArtworkPatched = true;
    prototype.render.__steamToolsArtworkPatchVersion = version;
    state.reactPatchInstalled = true;
    return true;
  }

  /**
   * The React patch is what adds Curtain, Trailer, Overture, Metadata and
   * Game Profile to the game context menu; the DOM fallback only adds the
   * artwork and OmniLibrary rows. At injection time Steam may not have
   * registered React or the LibraryContextMenu module yet (Steam start,
   * library chunk loaded later), and a single failed attempt used to leave
   * the menu with "TFS Artworks..." only until the next reinjection.
   * Retry with backoff (each attempt scans the webpack modules, ~50 ms).
   */
  function scheduleReactContextMenuPatchRetry() {
    if (state.reactPatchInstalled || state.reactPatchRetryTimer) {
      return;
    }

    state.reactPatchRetryDelay = Math.min(
      30000,
      state.reactPatchRetryDelay ? state.reactPatchRetryDelay * 2 : 1000,
    );
    state.reactPatchRetryTimer = window.setTimeout(() => {
      state.reactPatchRetryTimer = null;
      if (!installReactContextMenuPatch()) {
        scheduleReactContextMenuPatchRetry();
      }
    }, state.reactPatchRetryDelay);
  }

  function ensureReactContextMenuPatch() {
    if (!installReactContextMenuPatch()) {
      scheduleReactContextMenuPatchRetry();
    }
  }

  function getReactPropertyKey(element, prefix) {
    return element
      ? Object.getOwnPropertyNames(element).find((name) => name.startsWith(prefix))
      : null;
  }

  function getReactFiber(element) {
    const key = getReactPropertyKey(element, "__reactFiber");
    return key ? element[key] : null;
  }

  function findInObject(root, predicate, maxDepth = 7) {
    const seen = new Set();
    const stack = [{ value: root, depth: 0 }];
    while (stack.length) {
      const { value, depth } = stack.pop();
      if (!value || depth > maxDepth || seen.has(value)) {
        continue;
      }
      seen.add(value);

      try {
        if (predicate(value)) {
          return value;
        }
      } catch {
      }

      if (typeof value !== "object" && typeof value !== "function") {
        continue;
      }

      for (const key of Object.keys(value)) {
        if (key === "_owner" || key === "return" || key === "child" || key === "sibling") {
          continue;
        }

        try {
          const next = value[key];
          if (next && (typeof next === "object" || typeof next === "function")) {
            stack.push({ value: next, depth: depth + 1 });
          }
        } catch {
        }
      }
    }

    return null;
  }

  function extractAppIdFromText(value) {
    const text = String(value || "");
    const patterns = [
      /(?:appdetails|app|rungameid)\/(-?\d+)/i,
      /(?:appid|app_id|unAppID)[=:/"]+(-?\d+)/i,
      /steam:\/\/rungameid\/(-?\d+)/i,
    ];

    for (const pattern of patterns) {
      const match = text.match(pattern);
      if (match) {
        return normalizeSteamAppId(match[1]);
      }
    }

    return 0;
  }

  function extractAppIdFromElement(element) {
    if (!(element instanceof HTMLElement)) {
      return 0;
    }

    const attributeNames = [
      "href",
      "src",
      "data-appid",
      "data-app-id",
      "data-ds-appid",
      "data-steam-appid",
      "data-steam-app-id",
    ];

    for (const name of attributeNames) {
      const value = element.getAttribute(name);
      const appId = /^\s*-?\d+\s*$/.test(value || "")
        ? normalizeSteamAppId(value)
        : extractAppIdFromText(value);
      if (appId) {
        return appId;
      }
    }

    const styleAppId = extractAppIdFromText(element.getAttribute("style") || element.style?.backgroundImage || "");
    if (styleAppId) {
      return styleAppId;
    }

    return 0;
  }

  function findAppIdNearElement(node) {
    let current = node instanceof HTMLElement ? node : node?.parentElement;
    for (let depth = 0; current && depth < 8; depth += 1, current = current.parentElement) {
      const ownAppId = extractAppIdFromElement(current);
      if (ownAppId) {
        return ownAppId;
      }

      const linkedElement = current.querySelector?.("[href*='/app/'], [href*='appdetails'], [href*='rungameid'], [data-appid], [data-app-id], [data-steam-appid]");
      const linkedAppId = extractAppIdFromElement(linkedElement);
      if (linkedAppId) {
        return linkedAppId;
      }
    }

    return extractAppIdFromText(location.href);
  }

  function getContextMenuTitle(menu) {
    if (!(menu instanceof HTMLElement)) {
      return "";
    }

    const commandLabels = new Set(["change artwork", "tfs artworks"]);
    for (const name of Object.keys(steamCommandTokens)) {
      for (const label of getCommandLabels(name)) {
        commandLabels.add(label);
      }
    }

    const candidates = [...menu.querySelectorAll("h1, h2, [class*='title'], [class*='Title'], div, span")]
      .filter((item) => item instanceof HTMLElement)
      .map((item) => ({
        text: textOf(item),
        rect: item.getBoundingClientRect(),
      }))
      .filter((item) => {
        const normalized = normalizeMenuText(item.text);
        return (
          item.text.length >= 3 &&
          item.text.length <= 140 &&
          item.rect.width > 80 &&
          item.rect.height > 10 &&
          !commandLabels.has(normalized)
        );
      })
      .sort((left, right) => left.rect.top - right.rect.top || left.text.length - right.text.length);

    return candidates[0]?.text || "";
  }

  function getContextDataFromNode(node, allowRemembered = true) {
    let current = node;
    let appId = 0;
    let title = "";

    for (let depth = 0; current && depth < 8; depth += 1, current = current.parentElement) {
      const fiber = getReactFiber(current);
      if (!fiber) {
        continue;
      }

      const appObject = findInObject(
        fiber,
        hasFiniteAppId,
        9,
      );
      const rawAppId = getRawAppIdFromValue(appObject);
      if (Number.isFinite(Number(rawAppId))) {
        appId = normalizeSteamAppId(rawAppId);
        title = getBestTitleFromValue(appObject) || title;
      }

      if (!title) {
        const titleObject = findInObject(fiber, (value) => isUsableGameTitle(getBestTitleFromValue(value)));
        title = getBestTitleFromValue(titleObject) || title;
      }

      if (appId && title) {
        break;
      }
    }

    if (!appId) {
      appId = findAppIdNearElement(node);
    }

    if (!appId && allowRemembered) {
      const remembered = getRememberedArtworkContext();
      if (remembered?.appId) {
        appId = remembered.appId;
        title = remembered.title || title;
      }
    }

    if (!appId) {
      const match = location.href.match(/\/appdetails\/(\d+)/i);
      if (match) {
        appId = normalizeSteamAppId(match[1]);
      }
    }

    if (!title) {
      title = getContextMenuTitle(node);
    }

    if (!title) {
      const textBlocks = [...document.querySelectorAll("h1, h2, [class*='AppTitle'], [class*='Title']")]
        .map((item) => item.textContent?.trim())
        .filter(Boolean);
      title = textBlocks[0] || document.title.replace(/\s*-\s*Steam.*$/i, "").trim();
    }

    const result = { appId, title: title || "Selected Game" };
    rememberArtworkContext(result);
    return result;
  }

  function textOf(node) {
    return (node?.textContent || "").replace(/\s+/g, " ").trim();
  }

  function isPropertiesRow(node) {
    return isPropertiesText(textOf(node));
  }

  function getMenuRows(menu) {
    const rows = [];
    const seen = new Set();

    for (const node of [...menu.querySelectorAll("div, button, [role='menuitem']")]) {
      if (!(node instanceof HTMLElement)) {
        continue;
      }

      const rect = node.getBoundingClientRect();
      if (rect.width < 120 || rect.height < 24 || rect.height > 180) {
        continue;
      }

      const row = resolveMenuItemRow(node, menu);
      if (!row || seen.has(row) || row.classList.contains("steamtools-artwork-context-row")) {
        continue;
      }

      const rowText = textOf(row);
      if (!rowText || rowText.length > 180) {
        continue;
      }

      seen.add(row);
      rows.push(row);
    }

    return rows.sort((left, right) => {
      const leftRect = left.getBoundingClientRect();
      const rightRect = right.getBoundingClientRect();
      return leftRect.top - rightRect.top || leftRect.left - rightRect.left;
    });
  }

  function isGameContextMenu(menu) {
    const text = normalizeMenuText(textOf(menu));
    return (
      textIncludesCommand(text, "properties") &&
      (textIncludesCommand(text, "play") || textIncludesCommand(text, "manage"))
    );
  }

  // Text checks first, layout last: measuring every div of the Library on each
  // DOM change was the main Tools for Steam cost while browsing (forced layout).
  function isMenuCandidate(node) {
    if (!(node instanceof HTMLElement)) {
      return false;
    }

    const rawLength = (node.textContent || "").length;
    if (rawLength <= 30 || rawLength > 4000) {
      return false;
    }

    const textLength = textOf(node).length;
    if (textLength <= 30 || textLength >= 1200 || !isGameContextMenu(node)) {
      return false;
    }

    if (node.offsetParent === null) {
      return false;
    }

    const rect = node.getBoundingClientRect();
    return !(rect.width < 240 || rect.width > Math.min(window.innerWidth, 1100) || rect.height < 180);
  }

  function findMenuCandidates(root = document) {
    const nodes = [];
    if (root !== document && root instanceof HTMLElement) {
      nodes.push(root);
    }
    if (typeof root?.querySelectorAll === "function") {
      nodes.push(...root.querySelectorAll("[role='menu'], [class*='contextmenu'], [class*='ContextMenu'], div"));
    }

    const result = [];
    for (const node of nodes) {
      if (result.includes(node) || !isMenuCandidate(node)) {
        continue;
      }

      result.push(node);
      if (result.length >= 6) {
        break;
      }
    }

    return result;
  }

  // Only nodes Steam just inserted can be (or belong to) a new context menu.
  function findMenusForAddedNodes(addedNodes) {
    const menus = new Set();
    // Sibling insertions share ancestors (a Library grid adds dozens of cells
    // at once); test each ancestor once per batch, not once per added node.
    const checkedAncestors = new Set();
    for (const node of addedNodes) {
      if (!(node instanceof HTMLElement) || !node.isConnected) {
        continue;
      }

      // A game context menu always carries a Properties row. Library cells,
      // artwork and focus rings never do, so they cost one string check.
      const addedText = (node.textContent || "").toLowerCase();
      if (!textIncludesCommand(addedText, "properties")) {
        continue;
      }

      let ancestor = node.parentElement;
      for (let depth = 0; ancestor && depth < 6; depth += 1, ancestor = ancestor.parentElement) {
        if (checkedAncestors.has(ancestor)) {
          break;
        }
        checkedAncestors.add(ancestor);
        if (isMenuCandidate(ancestor)) {
          menus.add(ancestor);
          break;
        }
      }

      for (const menu of findMenuCandidates(node)) {
        menus.add(menu);
      }
    }

    return [...menus].slice(0, 6);
  }

  function findPropertiesRow(menu) {
    for (const node of [...menu.querySelectorAll("div, button, [role='menuitem']")]) {
      if (!(node instanceof HTMLElement) || !isPropertiesRow(node)) {
        continue;
      }

      return resolveMenuItemRow(node, menu);
    }

    return null;
  }

  function resolveMenuItemRow(node, menu) {
    let row = node;
    let current = node;

    while (current.parentElement && current.parentElement !== menu) {
      const parent = current.parentElement;
      const parentText = textOf(parent).toLowerCase();
      const currentText = textOf(current).toLowerCase();
      const parentLooksLikeSameRow =
        parentText === currentText ||
        parent.getAttribute("role") === "menuitem" ||
        parent.tabIndex >= 0 ||
        parent.matches("button");

      if (!parentLooksLikeSameRow) {
        break;
      }

      row = parent;
      current = parent;
    }

    return row;
  }

  function replaceRowText(row, text) {
    const walker = document.createTreeWalker(row, NodeFilter.SHOW_TEXT);
    let replaced = false;
    let node = walker.nextNode();

    while (node) {
      const value = node.nodeValue || "";
      if (isPropertiesText(value)) {
        node.nodeValue = text;
        replaced = true;
      }

      node = walker.nextNode();
    }

    if (!replaced) {
      row.textContent = text;
    }
  }

  function sanitizeClonedRow(node) {
    if (!(node instanceof HTMLElement)) {
      return;
    }

    node.removeAttribute("id");
    node.classList.remove("gpfocus");
    node.classList.remove("focus");
    node.classList.remove("Focused");
    node.removeAttribute("aria-current");
    node.removeAttribute("aria-selected");
    node.removeAttribute("data-focusable-child");
    node.removeAttribute("data-focusable-id");

    for (const child of node.children) {
      sanitizeClonedRow(child);
    }
  }

  function getArtworkContextFromRow(row, menu) {
    return (
      readArtworkContextFromElement(row) ||
      getContextDataFromNode(menu) ||
      getRememberedArtworkContext()
    );
  }

  function bindContextRowAction(row, menu, context) {
    writeArtworkContextToElement(row, context);
    const open = (event) => {
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      requestArtworkPanel(getArtworkContextFromRow(row, menu));
    };

    const handleKeys = (event) => {
      if (
        event.key === "Enter" ||
        event.key === " " ||
        event.key === "GamepadA" ||
        event.code === "Enter" ||
        event.code === "Space"
      ) {
        open(event);
      }
    };

    row.addEventListener("click", open, true);
    row.addEventListener("mousedown", (event) => {
      event.stopPropagation();
      event.stopImmediatePropagation?.();
    }, true);
    row.addEventListener("keydown", handleKeys, true);
  }

  function createArtworkContextRow(propertiesRow, menu, context) {
    const row = propertiesRow.cloneNode(true);
    sanitizeClonedRow(row);
    row.classList.add("steamtools-artwork-context-row");
    row.setAttribute("role", propertiesRow.getAttribute("role") || "menuitem");
    row.setAttribute("tabindex", propertiesRow.getAttribute("tabindex") || "0");
    row.setAttribute("data-steamtools-artwork-row", "true");
    replaceRowText(row, "TFS Artworks...");
    bindContextRowAction(row, menu, context);
    return row;
  }

  function createOmniLibraryUninstallContextRow(
    propertiesRow,
    menu,
    context,
    storeId,
  ) {
    const row = propertiesRow.cloneNode(true);
    sanitizeClonedRow(row);
    row.classList.add("steamtools-omnilibrary-uninstall-context-row");
    row.setAttribute("role", propertiesRow.getAttribute("role") || "menuitem");
    row.setAttribute("tabindex", propertiesRow.getAttribute("tabindex") || "0");
    row.setAttribute("data-steamtools-omnilibrary-uninstall-row", "true");
    replaceRowText(row, "Uninstall...");

    let lastActivationAt = 0;
    const uninstall = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) {
        return;
      }
      lastActivationAt = now;
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      void requestOmniLibraryUninstall(
        getArtworkContextFromRow(row, menu) || context,
        storeId,
      );
    };
    row.addEventListener("click", uninstall, true);
    row.addEventListener("keydown", (event) => {
      if (
        event.key === "Enter" ||
        event.key === " " ||
        event.key === "GamepadA" ||
        event.code === "Enter" ||
        event.code === "Space"
      ) {
        uninstall(event);
      }
    }, true);
    return row;
  }

  function createOmniLibraryRepairContextRow(
    propertiesRow,
    menu,
    context,
    storeId,
  ) {
    const row = propertiesRow.cloneNode(true);
    sanitizeClonedRow(row);
    row.classList.add("steamtools-omnilibrary-repair-context-row");
    row.setAttribute("role", propertiesRow.getAttribute("role") || "menuitem");
    row.setAttribute("tabindex", propertiesRow.getAttribute("tabindex") || "0");
    row.setAttribute("data-steamtools-omnilibrary-repair-row", "true");
    replaceRowText(row, "Verify & Repair...");

    let lastActivationAt = 0;
    const repair = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) {
        return;
      }
      lastActivationAt = now;
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      void requestOmniLibraryRepair(
        getArtworkContextFromRow(row, menu) || context,
        storeId,
      );
    };
    row.addEventListener("click", repair, true);
    row.addEventListener("keydown", (event) => {
      if (
        event.key === "Enter" ||
        event.key === " " ||
        event.key === "GamepadA" ||
        event.code === "Enter" ||
        event.code === "Space"
      ) {
        repair(event);
      }
    }, true);
    return row;
  }

  function createOmniLibraryDataContextRow(
    propertiesRow,
    menu,
    context,
    storeId,
  ) {
    const row = propertiesRow.cloneNode(true);
    sanitizeClonedRow(row);
    row.classList.add("steamtools-omnilibrary-data-context-row");
    row.setAttribute("role", propertiesRow.getAttribute("role") || "menuitem");
    row.setAttribute("tabindex", propertiesRow.getAttribute("tabindex") || "0");
    row.setAttribute("data-steamtools-omnilibrary-data-row", "true");
    replaceRowText(row, "Fix Game Data...");
    let lastActivationAt = 0;
    const refresh = (event) => {
      const now = Date.now();
      if (now - lastActivationAt < 750) {
        return;
      }
      lastActivationAt = now;
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      openMetadataOverlay(
        getArtworkContextFromRow(row, menu) || context,
        storeId,
      );
    };
    row.addEventListener("click", refresh, true);
    row.addEventListener("keydown", (event) => {
      if (
        event.key === "Enter" ||
        event.key === " " ||
        event.key === "GamepadA" ||
        event.code === "Enter" ||
        event.code === "Space"
      ) {
        refresh(event);
      }
    }, true);
    return row;
  }

  function findArtworkContextRow(target) {
    if (!(target instanceof Element)) {
      return null;
    }

    const markedRow = target.closest(".steamtools-artwork-context-row, [data-steamtools-artwork-row='true']");
    if (markedRow) {
      return markedRow;
    }

    let current = target;
    for (let depth = 0; current instanceof HTMLElement && depth < 6; depth += 1, current = current.parentElement) {
      const text = normalizeMenuText(textOf(current));
      if (text === "change artwork" || text === "tfs artworks") {
        return current;
      }
    }

    return null;
  }

  function findContextMenuForRow(row) {
    if (!(row instanceof HTMLElement)) {
      return null;
    }

    return row.closest("[role='menu'], [class*='contextmenu'], [class*='ContextMenu']") || row.parentElement;
  }

  function installContextActivationCapture() {
    if (state.contextActivationCaptureInstalled) {
      return;
    }

    state.contextActivationHandler = (event) => {
      const row = findArtworkContextRow(event.target);
      if (!row) {
        return;
      }

      if (event.type === "keydown") {
        const keyboardEvent = event;
        if (
          keyboardEvent.key !== "Enter" &&
          keyboardEvent.key !== " " &&
          keyboardEvent.code !== "Enter" &&
          keyboardEvent.code !== "Space"
        ) {
          return;
        }
      }

      event.preventDefault?.();
      event.stopPropagation?.();
      event.stopImmediatePropagation?.();
      requestArtworkPanel(getArtworkContextFromRow(row, findContextMenuForRow(row)));
    };

    document.addEventListener("click", state.contextActivationHandler, true);
    document.addEventListener("mouseup", state.contextActivationHandler, true);
    document.addEventListener("keydown", state.contextActivationHandler, true);
    state.contextActivationCaptureInstalled = true;
  }

  function uninstallContextActivationCapture() {
    if (!state.contextActivationCaptureInstalled || !state.contextActivationHandler) {
      return;
    }

    document.removeEventListener("click", state.contextActivationHandler, true);
    document.removeEventListener("mouseup", state.contextActivationHandler, true);
    document.removeEventListener("keydown", state.contextActivationHandler, true);
    state.contextActivationHandler = null;
    state.contextActivationCaptureInstalled = false;
  }

  function installContextTracking() {
    if (state.contextTrackingInstalled) {
      return;
    }

    state.contextTrackingHandler = (event) => {
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }

      const context = getContextDataFromNode(target, false);
      if (context?.appId) {
        rememberArtworkContext(context);
      }
    };

    document.addEventListener("pointerdown", state.contextTrackingHandler, true);
    document.addEventListener("contextmenu", state.contextTrackingHandler, true);
    document.addEventListener("focusin", state.contextTrackingHandler, true);
    state.contextTrackingInstalled = true;
  }

  function uninstallContextTracking() {
    if (!state.contextTrackingInstalled || !state.contextTrackingHandler) {
      return;
    }

    document.removeEventListener("pointerdown", state.contextTrackingHandler, true);
    document.removeEventListener("contextmenu", state.contextTrackingHandler, true);
    document.removeEventListener("focusin", state.contextTrackingHandler, true);
    state.contextTrackingHandler = null;
    state.contextTrackingInstalled = false;
  }

  function patchMenus(menus) {
    if (!state.contextMenuEnabled) {
      removeArtworkContextRows();
    }

    for (const menu of Array.isArray(menus) ? menus : findMenuCandidates()) {
      if (!isGameContextMenu(menu)) {
        removeArtworkContextRows(menu);
        removeOmniLibraryUninstallContextRows(menu);
        continue;
      }

      // A game menu reached the DOM fallback: make sure the React patch is in
      // place so the next opening carries all TFS entries.
      if (!state.reactPatchInstalled) {
        installReactContextMenuPatch();
      }

      const menuContext = getContextDataFromNode(menu);
      const propertiesRow = findPropertiesRow(menu);
      const parent = propertiesRow?.parentElement;
      if (!propertiesRow || !parent) {
        continue;
      }

      const hasArtworkRow =
        menu.querySelector(".steamtools-artwork-context-row") ||
        textOf(menu).toLowerCase().includes("change artwork") ||
        textOf(menu).toLowerCase().includes("tfs artworks");
      if (state.contextMenuEnabled && !hasArtworkRow) {
        const row = createArtworkContextRow(propertiesRow, menu, menuContext);
        parent.insertBefore(row, propertiesRow);
      }

      const installedStore = getInstalledOmniLibraryStore(menuContext?.appId);
      // "Fix Game Data..." is retired (see the React path); drop rows an
      // older surface version may have left in an open menu.
      menu.querySelector(".steamtools-omnilibrary-data-context-row")?.remove();
      const repairRow = menu.querySelector(
        ".steamtools-omnilibrary-repair-context-row",
      );
      const uninstallRow = menu.querySelector(
        ".steamtools-omnilibrary-uninstall-context-row",
      );
      if (!installedStore) {
        repairRow?.remove();
        uninstallRow?.remove();
      } else {
        if (!isRepairableOmniLibraryGame(installedStore, menuContext?.appId)) {
          repairRow?.remove();
        } else if (!repairRow) {
          const row = createOmniLibraryRepairContextRow(
            propertiesRow,
            menu,
            menuContext,
            installedStore.id,
          );
          parent.insertBefore(row, propertiesRow);
        }
        if (uninstallRow) {
          continue;
        }
        const row = createOmniLibraryUninstallContextRow(
          propertiesRow,
          menu,
          menuContext,
          installedStore.id,
        );
        parent.insertBefore(row, propertiesRow);
      }
    }
  }

  function canHostArtworkOverlay() {
    const hasUsableViewport = Boolean(
      document.body &&
      window.innerWidth >= 500 &&
      window.innerHeight >= 300
    );
    if (!hasUsableViewport) {
      return false;
    }

    // Current Steam Big Picture builds navigate the primary window to /routes/*.
    // Popup surfaces use the same route family, so the route alone can no longer
    // distinguish the full-screen host from Quick Access and context menus.
    const windowName = String(window.name || "");
    const isPrimaryBigPictureWindow =
      /^SP BPM(?:_|$)/i.test(windowName) ||
      document.title === "Steam Big Picture Mode";
    if (isPrimaryBigPictureWindow) {
      return true;
    }

    // Keep supporting older Steam clients whose primary surface stayed on an
    // about:blank document, while refusing modern routed popup surfaces.
    return !location.href.includes("/routes/");
  }

  function getOpenRequestKey(appId, title) {
    return `${normalizeSteamAppId(appId)}:${String(title || "").trim().toLowerCase()}`;
  }

  function normalizeArtworkContext(context) {
    const appId = normalizeSteamAppId(context?.appId);
    if (!appId) {
      return null;
    }

    return {
      appId,
      title: context?.title || "Selected Game",
    };
  }

  function rememberArtworkContext(context) {
    const normalized = normalizeArtworkContext(context);
    if (!normalized) {
      return null;
    }

    state.lastContextMenuContext = normalized;
    state.lastContextMenuContextAt = Date.now();
    return normalized;
  }

  function getRememberedArtworkContext() {
    if (!state.lastContextMenuContext || Date.now() - state.lastContextMenuContextAt > 15000) {
      return null;
    }

    return state.lastContextMenuContext;
  }

  function writeArtworkContextToElement(element, context) {
    const normalized = normalizeArtworkContext(context);
    if (!(element instanceof HTMLElement) || !normalized) {
      return;
    }

    element.dataset.steamtoolsArtworkAppId = String(normalized.appId);
    element.dataset.steamtoolsArtworkTitle = normalized.title;
  }

  function readArtworkContextFromElement(element) {
    if (!(element instanceof HTMLElement)) {
      return null;
    }

    const appId = normalizeSteamAppId(element.dataset.steamtoolsArtworkAppId);
    if (!appId) {
      return null;
    }

    return {
      appId,
      title: element.dataset.steamtoolsArtworkTitle || "Selected Game",
    };
  }

  function shouldIgnoreDuplicateOpen(appId, title) {
    const key = getOpenRequestKey(appId, title);
    if (state.overlay && state.currentOpenKey === key) {
      return true;
    }

    return state.lastClosedKey === key && Date.now() - state.lastClosedAt < 1400;
  }

  async function pollArtworkOpenRequest() {
    if (!canHostArtworkOverlay()) {
      return;
    }

    try {
      const request = await fetchJson(`api/artwork/open-request?after=${state.lastOpenRequestNonce}`);
      if (!request?.nonce || Number(request.nonce) <= state.lastOpenRequestNonce) {
        return;
      }

      state.lastOpenRequestNonce = Number(request.nonce);
      if (shouldIgnoreDuplicateOpen(request.appId, request.title)) {
        return;
      }
      openOverlay({
        appId: normalizeSteamAppId(request.appId),
        title: request.title || "Selected Game",
      });
    } catch {
      // Steam recreates surfaces often; the next poll will recover quietly.
    }
  }

  function consumeLocalArtworkOpenRequest(raw) {
    if (!raw || !canHostArtworkOverlay()) {
      return;
    }

    try {
      const request = JSON.parse(raw);
      const nonce = String(request?.nonce || "");
      const appId = normalizeSteamAppId(request?.appId);
      if (!nonce || !appId || nonce === state.lastLocalOpenRequestNonce) {
        return;
      }

      state.lastLocalOpenRequestNonce = nonce;
      try {
        localStorage.removeItem(openRequestStorageKey);
      } catch {
      }
      if (shouldIgnoreDuplicateOpen(appId, request.title)) {
        return;
      }
      openOverlay({
        appId,
        title: request.title || "Selected Game",
      });
    } catch {
    }
  }

  function pollLocalArtworkOpenRequest() {
    try {
      consumeLocalArtworkOpenRequest(localStorage.getItem(openRequestStorageKey));
    } catch {
    }
  }

  function startOpenRequestPolling() {
    if (state.openRequestTimer) {
      return;
    }

    state.openRequestTimer = window.setInterval(() => {
      void pollArtworkOpenRequest();
    }, 350);
    state.localOpenRequestTimer = window.setInterval(pollLocalArtworkOpenRequest, 250);
    if (!state.openRequestStorageHandler) {
      state.openRequestStorageHandler = (event) => {
        if (event.key === openRequestStorageKey) {
          consumeLocalArtworkOpenRequest(event.newValue);
        }
      };
      window.addEventListener("storage", state.openRequestStorageHandler);
    }
    void pollArtworkOpenRequest();
    pollLocalArtworkOpenRequest();
  }

  function setMessage(status, error = "") {
    state.status = status || "";
    state.error = error || "";
    renderOverlay();
  }

  async function fetchJson(path, options = {}) {
    const response = await fetch(`${apiBase}${path}`, {
      cache: "no-store",
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {}),
      },
    });

    const payload = await response.json().catch(() => null);
    if (!response.ok) {
      throw new Error(payload?.message || `Request failed with ${response.status}`);
    }

    return payload;
  }

  async function loadMetadataSnapshot(forceRefresh = false) {
    if (!state.appId) {
      return;
    }
    state.metadataLoading = true;
    state.error = "";
    renderOverlay();
    try {
      state.metadataSnapshot = await fetchJson(
        `api/unifystore/metadata/games/${encodeURIComponent(state.appId)}`,
        { method: forceRefresh ? "POST" : "GET" },
      );
      state.status = forceRefresh
        ? "Game data refreshed."
        : "Choose a Steam match only when the automatic result is missing or wrong.";
      publishOmniLibraryMetadataSnapshot(state.metadataSnapshot);
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.metadataLoading = false;
      renderOverlay();
    }
  }

  async function searchMetadataMatches(term) {
    if (!state.appId) {
      return;
    }
    state.metadataLoading = true;
    state.error = "";
    state.metadataMatches = [];
    renderOverlay();
    try {
      const query = new URLSearchParams({
        appId: String(state.appId),
        term: term || state.title || "",
      });
      state.metadataMatches = await fetchJson(
        `api/unifystore/metadata/search?${query.toString()}`,
      );
      const currentSteamAppId = Number(state.metadataSnapshot?.sourceSteamAppId || 0);
      state.selectedMetadataSteamAppId = Number(
        state.metadataMatches.find(
          (match) => Number(match?.steamAppId) === currentSteamAppId,
        )?.steamAppId || state.metadataMatches?.[0]?.steamAppId || 0,
      );
      state.status = state.metadataMatches.length
        ? `Found ${state.metadataMatches.length} possible Steam match${state.metadataMatches.length === 1 ? "" : "es"}.`
        : "No Steam matches found. Try the base title without an edition or launcher suffix.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.metadataLoading = false;
      renderOverlay();
    }
  }

  async function applyMetadataMatch(steamAppId) {
    if (!state.appId || !steamAppId || state.metadataApplying) {
      return;
    }
    state.metadataApplying = true;
    state.error = "";
    state.status = "Applying the selected Steam match...";
    renderOverlay();
    try {
      state.metadataSnapshot = await fetchJson(
        `api/unifystore/metadata/games/${encodeURIComponent(state.appId)}/match`,
        {
          method: "POST",
          body: JSON.stringify({ steamAppId: Number(steamAppId) }),
        },
      );
      state.selectedMetadataSteamAppId = Number(steamAppId);
      state.status = "Selected match saved. Steam's game page is updating.";
      publishOmniLibraryMetadataSnapshot(state.metadataSnapshot);
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.metadataApplying = false;
      renderOverlay();
    }
  }

  async function clearMetadataMatch() {
    if (!state.appId || state.metadataApplying) {
      return;
    }
    state.metadataApplying = true;
    state.error = "";
    state.status = "Restoring automatic matching...";
    renderOverlay();
    try {
      state.metadataSnapshot = await fetchJson(
        `api/unifystore/metadata/games/${encodeURIComponent(state.appId)}/match`,
        { method: "DELETE" },
      );
      state.selectedMetadataSteamAppId = 0;
      state.status = "Automatic matching restored. Steam's game page is updating.";
      publishOmniLibraryMetadataSnapshot(state.metadataSnapshot);
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.metadataApplying = false;
      renderOverlay();
    }
  }

  async function searchGames(term) {
    const requestId = ++state.gameSearchRequestId;
    state.loadingGames = true;
    state.error = "";
    state.pendingInitialAssetsFocus = true;
    renderOverlay();

    try {
      const query = encodeURIComponent(term || state.title || "");
      const games = await fetchJson(`api/artwork/search?term=${query}`);
      if (requestId !== state.gameSearchRequestId || state.overlayMode !== "artwork") {
        return;
      }
      state.games = games;
      state.selectedGameId = Number(state.games?.[0]?.id || 0);
      state.status = state.games.length
        ? `Found ${state.games.length} SteamGridDB match${state.games.length === 1 ? "" : "es"}.`
        : "No SteamGridDB matches found. Try a shorter title.";
      await loadAssets();
    } catch (error) {
      if (requestId === state.gameSearchRequestId) {
        state.error = error instanceof Error ? error.message : String(error);
      }
    } finally {
      if (requestId === state.gameSearchRequestId) {
        state.loadingGames = false;
        renderOverlay();
      }
    }
  }

  async function loadAssets() {
    if (!state.selectedGameId) {
      state.assets = [];
      renderOverlay();
      return;
    }

    const requestId = ++state.assetRequestId;
    const requestedGameId = state.selectedGameId;
    const requestedType = state.activeType;
    state.loadingAssets = true;
    state.error = "";
    state.assets = [];
    renderOverlay();

    try {
      const query = new URLSearchParams({
        gameId: String(requestedGameId),
        type: requestedType,
        page: "0",
      });
      const assets = await fetchJson(`api/artwork/assets?${query.toString()}`);
      if (
        requestId !== state.assetRequestId ||
        requestedGameId !== state.selectedGameId ||
        requestedType !== state.activeType
      ) {
        return;
      }
      state.assets = [...assets].sort((left, right) => {
        const qualityDifference = getArtworkQuality(right, requestedType).rank -
          getArtworkQuality(left, requestedType).rank;
        if (qualityDifference) {
          return qualityDifference;
        }

        return getArtworkPixelCount(right) - getArtworkPixelCount(left);
      });
      const animatedCount = state.assets.filter((asset) => asset?.isAnimated).length;
      state.status = state.assets.length
        ? `${state.assets.length} ${getActiveType().label.toLowerCase()} option${state.assets.length === 1 ? "" : "s"}, best quality first${animatedCount ? `, including ${animatedCount} animated` : ""}.`
        : "No artwork found for this tab.";
    } catch (error) {
      if (requestId === state.assetRequestId) {
        state.error = error instanceof Error ? error.message : String(error);
      }
    } finally {
      if (requestId === state.assetRequestId) {
        state.loadingAssets = false;
        renderOverlay();
      }
    }
  }

  async function applyAsset(asset) {
    if (!state.appId || !asset?.url) {
      return;
    }

    state.applying = true;
    setMessage("Applying artwork...");

    try {
      const result = await fetchJson("api/artwork/apply", {
        method: "POST",
        body: JSON.stringify({
          appId: state.appId,
          assetType: state.activeType,
          url: asset.url,
        }),
      });

      let liveApplied = false;
      const steamApps = window.SteamClient?.Apps;
      if (result?.success && steamApps?.SetCustomArtworkForApp && result.base64Data) {
        try {
          await steamApps.SetCustomArtworkForApp(
            Number(result.appId),
            result.base64Data,
            result.extension || "png",
            Number(result.steamAssetType),
          );
          liveApplied = true;
        } catch (error) {
          console.warn("[Tools for Steam] Steam live artwork apply failed; file fallback was still written.", error);
        }
      }

      state.status = liveApplied
        ? "Artwork applied through Steam."
        : result.message || "Artwork written. Steam may need to refresh the game page.";
      state.error = "";
      state.lastAppliedAssetKey = `${state.activeType}:${asset.id || asset.url}`;
      refreshSteamArtworkAfterApply(result);
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.applying = false;
      renderOverlay();
    }
  }

  function getActiveType() {
    return assetTypes.find((type) => type.id === state.activeType) || assetTypes[0];
  }

  function getArtworkPixelCount(asset) {
    const width = Number(asset?.width || 0);
    const height = Number(asset?.height || 0);
    return width > 0 && height > 0 ? width * height : 0;
  }

  function disconnectAnimatedPreviewObserver() {
    state.animatedPreviewObserver?.disconnect();
    state.animatedPreviewObserver = null;
  }

  function createStillArtworkPreview(asset, activeType) {
    const image = document.createElement("img");
    image.loading = "lazy";
    image.decoding = "async";
    image.src = asset.thumbnailUrl || asset.url;
    image.alt = `${activeType.label} artwork`;
    return image;
  }

  function createAnimatedArtworkPreview(asset, activeType) {
    const video = document.createElement("video");
    video.autoplay = true;
    video.loop = true;
    video.muted = true;
    video.playsInline = true;
    video.preload = "none";
    video.setAttribute("aria-label", `${activeType.label} animated artwork preview`);
    video.dataset.previewSource = asset.thumbnailUrl || asset.url;
    video.addEventListener("error", () => {
      state.animatedPreviewObserver?.unobserve(video);
      const fallback = createStillArtworkPreview(
        { ...asset, thumbnailUrl: asset.url },
        activeType,
      );
      video.replaceWith(fallback);
    }, { once: true });

    if (typeof window.IntersectionObserver !== "function") {
      video.src = video.dataset.previewSource;
      void video.play().catch(() => {});
      return video;
    }

    if (!state.animatedPreviewObserver) {
      state.animatedPreviewObserver = new window.IntersectionObserver((entries) => {
        for (const entry of entries) {
          const preview = entry.target;
          if (entry.isIntersecting) {
            if (!preview.src) {
              preview.src = preview.dataset.previewSource;
            }
            void preview.play().catch(() => {});
          } else {
            preview.pause();
          }
        }
      }, { rootMargin: "120px" });
    }
    state.animatedPreviewObserver.observe(video);
    return video;
  }

  function getArtworkQuality(asset, typeId = state.activeType) {
    const type = assetTypes.find((candidate) => candidate.id === typeId) || assetTypes[0];
    const width = Number(asset?.width || 0);
    const height = Number(asset?.height || 0);
    if (width <= 0 || height <= 0) {
      return {
        level: "unknown",
        label: "Unknown",
        rank: 0,
        fit: "Resolution unavailable",
        detail: "The source did not report an image size.",
      };
    }

    if (type.targetLongEdge) {
      const scale = Math.max(width, height) / type.targetLongEdge;
      if (scale >= 1) {
        return {
          level: "high",
          label: "High quality",
          rank: 4,
          fit: "Large source",
          detail: `At or above the recommended ${type.recommended}.`,
        };
      }
      if (scale >= 0.6) {
        return {
          level: "good",
          label: "Good",
          rank: 3,
          fit: "Good source size",
          detail: `Below the recommended ${type.recommended}, but suitable for Steam.`,
        };
      }
      if (scale >= 0.3) {
        return {
          level: "usable",
          label: "Usable",
          rank: 2,
          fit: "Small source",
          detail: `May look soft on large displays; recommended is ${type.recommended}.`,
        };
      }
      return {
        level: "low",
        label: "Low quality",
        rank: 1,
        fit: "Very small source",
        detail: `Likely to look blurry; recommended is ${type.recommended}.`,
      };
    }

    const targetWidth = Number(type.targetWidth || 1);
    const targetHeight = Number(type.targetHeight || 1);
    const resolutionScale = Math.min(width / targetWidth, height / targetHeight);
    const targetAspect = targetWidth / targetHeight;
    const aspectDifference = Math.abs(width / height - targetAspect) / targetAspect;
    const fit = aspectDifference <= 0.025
      ? "Exact fit"
      : aspectDifference <= 0.09
        ? "Good fit"
        : aspectDifference <= 0.24
          ? "Some cropping"
          : "Wrong shape";

    if (resolutionScale >= 1 && aspectDifference <= 0.04) {
      return {
        level: "high",
        label: "High quality",
        rank: 4,
        fit,
        detail: `Meets the recommended ${type.recommended} target.`,
      };
    }
    if (resolutionScale >= 0.7 && aspectDifference <= 0.1) {
      return {
        level: "good",
        label: "Good",
        rank: 3,
        fit,
        detail: `Close to the recommended ${type.recommended} target.`,
      };
    }
    if (resolutionScale >= 0.45 && aspectDifference <= 0.24) {
      return {
        level: "usable",
        label: "Usable",
        rank: 2,
        fit,
        detail: aspectDifference > 0.1
          ? `Large enough, but Steam will crop it toward ${type.recommended}.`
          : `Below the recommended ${type.recommended}; it may look softer.`,
      };
    }
    return {
      level: "low",
      label: "Low quality",
      rank: 1,
      fit,
      detail: aspectDifference > 0.24
        ? `The shape does not match the recommended ${type.recommended} target.`
        : `Below the recommended ${type.recommended}; likely to look blurry.`,
    };
  }

  function getSteamGridStem(appId, assetType) {
    switch (assetType) {
      case "grid_p":
        return `${appId}p`;
      case "hero":
        return `${appId}_hero`;
      case "logo":
        return `${appId}_logo`;
      case "icon":
        return `${appId}-icon`;
      case "grid_l":
      default:
        return String(appId);
    }
  }

  function nudgeSteamArtworkStores(appId) {
    const numericAppId = normalizeSteamAppId(appId);
    if (!numericAppId) {
      return;
    }

    const overview = window.appStore?.GetAppOverviewByAppID?.(numericAppId);
    const timestamp = Math.floor(Date.now() / 1000);
    if (overview) {
      try {
        overview.rt_custom_image_mtime = Math.max(Number(overview.rt_custom_image_mtime || 0), timestamp);
      } catch {
      }

      try {
        overview.rt_last_time_locally_modified = Math.max(Number(overview.rt_last_time_locally_modified || 0), timestamp);
      } catch {
      }
    }

    try {
      const appData = window.appDetailsStore?.GetAppData?.(numericAppId);
      if (appData && "customImageInfo" in appData) {
        appData.customImageInfo = null;
      }
      if (appData && "customImageInfoPromise" in appData) {
        appData.customImageInfoPromise = null;
      }
    } catch {
    }

    try {
      if (overview) {
        void window.appDetailsStore?.RequestCustomImageInfo?.(overview);
      }
    } catch {
    }

    try {
      void window.appDetailsStore?.RequestAppDetails?.(numericAppId);
    } catch {
    }

    try {
      void window.SteamClient?.Apps?.GetCachedAppDetails?.(numericAppId);
    } catch {
    }

    try {
      void window.SteamClient?.Apps?.RequestIconDataForApp?.(numericAppId);
    } catch {
    }
  }

  function cacheBustUrl(value, token) {
    if (!value || /^data:|^blob:/i.test(value)) {
      return value;
    }

    try {
      const url = new URL(value, window.location.href);
      url.searchParams.set("tfs_artwork", token);
      return url.toString();
    } catch {
      const separator = value.includes("?") ? "&" : "?";
      return `${value}${separator}tfs_artwork=${encodeURIComponent(token)}`;
    }
  }

  function extractStyleUrls(value) {
    const urls = [];
    const regex = /url\((['"]?)(.*?)\1\)/gi;
    let match;
    while ((match = regex.exec(value || "")) !== null) {
      if (match[2]) {
        urls.push(match[2]);
      }
    }
    return urls;
  }

  function imageUrlMatchesAppliedAsset(value, appId, assetType) {
    if (!value || /^data:|^blob:/i.test(value)) {
      return false;
    }

    const stem = getSteamGridStem(appId, assetType).toLowerCase();
    const normalized = String(value).toLowerCase();
    if (!normalized.includes(String(appId).toLowerCase())) {
      return false;
    }

    try {
      const url = new URL(value, window.location.href);
      const fileName = decodeURIComponent(url.pathname.split("/").pop() || "").toLowerCase();
      return fileName === stem || fileName.startsWith(`${stem}.`);
    } catch {
      return normalized.includes(`/${stem}.`) || normalized.includes(`\\${stem}.`);
    }
  }

  function refreshVisibleSteamArtworkUrls(appId, assetType) {
    const token = String(Date.now());

    for (const image of document.querySelectorAll("img, source")) {
      const source = image.currentSrc || image.src || image.srcset || "";
      if (!imageUrlMatchesAppliedAsset(source, appId, assetType)) {
        continue;
      }

      if (image.srcset) {
        image.srcset = image.srcset
          .split(",")
          .map((entry) => {
            const parts = entry.trim().split(/\s+/);
            return parts.length ? [cacheBustUrl(parts[0], token), ...parts.slice(1)].join(" ") : entry;
          })
          .join(", ");
      } else if (image.src) {
        image.src = cacheBustUrl(image.src, token);
      }
    }

    for (const element of document.querySelectorAll("[style*='url(']")) {
      const background = element.style.backgroundImage;
      if (!background || !extractStyleUrls(background).some((url) => imageUrlMatchesAppliedAsset(url, appId, assetType))) {
        continue;
      }

      element.style.backgroundImage = background.replace(/url\((['"]?)(.*?)\1\)/gi, (_match, quote, url) => {
        return `url(${quote || "\""}${cacheBustUrl(url, token)}${quote || "\""})`;
      });
    }
  }

  function refreshSteamArtworkAfterApply(result) {
    const appId = normalizeSteamAppId(result?.appId);
    const assetType = result?.assetType || state.activeType;
    if (!appId || !assetType) {
      return;
    }

    nudgeSteamArtworkStores(appId);
    refreshVisibleSteamArtworkUrls(appId, assetType);

    window.setTimeout(() => {
      nudgeSteamArtworkStores(appId);
      refreshVisibleSteamArtworkUrls(appId, assetType);
    }, 150);

    window.setTimeout(() => {
      nudgeSteamArtworkStores(appId);
      refreshVisibleSteamArtworkUrls(appId, assetType);
    }, 550);

    window.setTimeout(() => {
      nudgeSteamArtworkStores(appId);
      refreshVisibleSteamArtworkUrls(appId, assetType);
    }, 1400);
  }

  function getActiveTypeIndex() {
    return Math.max(0, assetTypes.findIndex((type) => type.id === state.activeType));
  }

  function setArtworkType(typeId) {
    if (!assetTypes.some((type) => type.id === typeId) || typeId === state.activeType) {
      return;
    }

    state.activeType = typeId;
    state.pendingInitialAssetsFocus = true;
    state.status = `Loading ${getActiveType().label.toLowerCase()} artwork...`;
    state.error = "";
    void loadAssets();
  }

  function cycleArtworkType(direction) {
    const index = getActiveTypeIndex();
    const next = assetTypes[(index + direction + assetTypes.length) % assetTypes.length];
    if (next) {
      setArtworkType(next.id);
    }
  }

  function createElement(tag, className, text) {
    const element = document.createElement(tag);
    if (className) {
      element.className = className;
    }
    if (text !== undefined && text !== null) {
      element.textContent = text;
    }
    return element;
  }

  function button(className, text, onClick) {
    const element = createElement("button", className, text);
    element.type = "button";
    element.setAttribute("data-steamtools-artwork-focusable", "true");
    element.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      onClick(event);
    });
    element.addEventListener("focus", () => {
      const index = state.focusItems.indexOf(element);
      if (index >= 0) {
        state.focusIndex = index;
        applyArtworkFocus(false);
      }
    });
    element.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        closeOverlay();
      }
    });
    return element;
  }

  function getVisibleArtworkFocusItems() {
    if (!state.overlay) {
      return [];
    }

    return [...state.overlay.querySelectorAll("[data-steamtools-artwork-focusable='true']")]
      .filter((element) => element instanceof HTMLElement)
      .filter((element) => !element.disabled && element.offsetParent !== null);
  }

  function getArtworkItemZone(item) {
    return item?.classList?.contains("steamtools-artwork-asset") ||
      item?.classList?.contains("steamtools-metadata-action")
      ? "assets"
      : "side";
  }

  function getArtworkZoneItems(zone = state.focusZone) {
    return state.focusItems.filter((item) => getArtworkItemZone(item) === zone);
  }

  function getFirstArtworkZoneWithItems(preferredZone = state.focusZone) {
    if (getArtworkZoneItems(preferredZone).length) {
      return preferredZone;
    }

    return getArtworkZoneItems("side").length ? "side" : "assets";
  }

  function applyArtworkFocus(shouldFocus = true) {
    for (const item of state.focusItems) {
      item.classList.remove("is-controller-focus");
      item.removeAttribute("aria-selected");
    }

    const item = state.focusItems[state.focusIndex];
    if (!item) {
      return;
    }

    item.classList.add("is-controller-focus");
    item.setAttribute("aria-selected", "true");
    if (shouldFocus) {
      item.focus({ preventScroll: true });
    }
    item.scrollIntoView({ block: "nearest", inline: "nearest" });
  }

  function refreshArtworkFocus(preferredElement = null) {
    const previous = preferredElement || state.focusItems[state.focusIndex] || document.activeElement;
    state.focusItems = getVisibleArtworkFocusItems();

    if (!state.focusItems.length) {
      state.focusIndex = 0;
      return;
    }

    state.focusZone = getFirstArtworkZoneWithItems(getArtworkItemZone(previous) || state.focusZone);
    const zoneItems = getArtworkZoneItems();
    const previousIndex = zoneItems.indexOf(previous);
    const selected = previousIndex >= 0
      ? previous
      : zoneItems[Math.min(Math.max(0, state.focusIndex), zoneItems.length - 1)] || zoneItems[0];
    state.focusIndex = Math.max(0, state.focusItems.indexOf(selected));
    applyArtworkFocus();
  }

  function focusFirstArtworkAsset() {
    state.focusItems = getVisibleArtworkFocusItems();
    const firstAsset = state.focusItems.find((item) => getArtworkItemZone(item) === "assets");
    if (!firstAsset) {
      refreshArtworkFocus();
      return;
    }

    state.focusZone = "assets";
    state.focusIndex = state.focusItems.indexOf(firstAsset);
    state.pendingInitialAssetsFocus = false;
    const results = state.overlay?.querySelector(".steamtools-artwork-results");
    if (results) {
      results.scrollTop = 0;
    }
    applyArtworkFocus();
    firstAsset.scrollIntoView({ block: "start", inline: "nearest" });
  }

  function getArtworkFocusGeometry(element) {
    // The focused card is scaled around its centre, so take the centre from
    // the rendered box and the size from the untransformed layout box.
    const rect = element.getBoundingClientRect();
    const width = element.offsetWidth || rect.width;
    const height = element.offsetHeight || rect.height;
    const x = rect.left + rect.width / 2;
    const y = rect.top + rect.height / 2;
    return {
      x,
      y,
      left: x - width / 2,
      right: x + width / 2,
      top: y - height / 2,
      bottom: y + height / 2,
    };
  }

  const artworkFocusGroupSelector = [
    ".steamtools-artwork-grid",
    ".steamtools-artwork-side",
    ".steamtools-artwork-type-rail",
    ".steamtools-artwork-search",
    ".steamtools-metadata-actions",
  ].join(", ");

  function findArtworkFocusNeighbor(current, direction, items) {
    // Spatial navigation like Steam: pick the nearest control beyond the
    // current one's edge, preferring the same column or row. No wrap-around,
    // and a move only reaches controls lying alongside the current section,
    // so Right at the end of a grid row stays instead of jumping to the bar.
    const from = getArtworkFocusGeometry(current);
    const vertical = direction === "up" || direction === "down";
    const group = current.closest?.(artworkFocusGroupSelector)?.getBoundingClientRect?.();
    let best = null;
    let bestScore = Number.POSITIVE_INFINITY;

    for (const item of items) {
      if (item === current) {
        continue;
      }

      const to = getArtworkFocusGeometry(item);
      const gap =
        direction === "down" ? to.top - from.bottom
          : direction === "up" ? from.top - to.bottom
            : direction === "right" ? to.left - from.right
              : from.left - to.right;
      if (gap < -2) {
        continue;
      }

      if (
        group &&
        (vertical
          ? to.right < group.left - 1 || to.left > group.left + group.width + 1
          : to.bottom < group.top - 1 || to.top > group.top + group.height + 1)
      ) {
        continue;
      }

      const crossGap = vertical
        ? Math.max(0, Math.max(from.left, to.left) - Math.min(from.right, to.right))
        : Math.max(0, Math.max(from.top, to.top) - Math.min(from.bottom, to.bottom));
      const centreOffset = vertical ? Math.abs(to.x - from.x) : Math.abs(to.y - from.y);
      const score = Math.max(0, gap) + crossGap * 3 + centreOffset * 0.25;
      if (score < bestScore) {
        bestScore = score;
        best = item;
      }
    }

    return best;
  }

  function moveArtworkFocus(direction) {
    refreshArtworkFocus();
    if (!state.focusItems.length) {
      return;
    }

    const current = state.focusItems[state.focusIndex];
    const next = current
      ? findArtworkFocusNeighbor(current, direction, state.focusItems)
      : state.focusItems[0];
    if (!next) {
      return;
    }

    state.focusIndex = state.focusItems.indexOf(next);
    state.focusZone = getArtworkItemZone(next);
    applyArtworkFocus();
  }

  function activateArtworkFocus() {
    refreshArtworkFocus();
    const item = state.focusItems[state.focusIndex];
    if (item) {
      item.click();
    }
  }

  function handleOverlayKeyDown(event) {
    if (!state.overlay) {
      return;
    }

    const key = event.key || event.code || "";
    const editingText = document.activeElement?.tagName === "INPUT";
    if (editingText && key !== "Escape" && key !== "GamepadB") {
      return;
    }
    const lowerKey = key.toLowerCase?.() || "";
    const isPreviousTypeKey =
      key === "PageUp" ||
      key === "GamepadLB" ||
      key === "GamepadL1" ||
      key === "GamepadLeftShoulder" ||
      lowerKey === "[";
    const isNextTypeKey =
      key === "PageDown" ||
      key === "GamepadRB" ||
      key === "GamepadR1" ||
      key === "GamepadRightShoulder" ||
      lowerKey === "]";
    const handled =
      key === "ArrowUp" ||
      key === "ArrowDown" ||
      key === "ArrowLeft" ||
      key === "ArrowRight" ||
      key === "Enter" ||
      key === " " ||
      key === "Space" ||
      key === "Escape" ||
      key === "GamepadA" ||
      key === "GamepadB" ||
      isPreviousTypeKey ||
      isNextTypeKey;

    // Nothing typed while the overlay is open may reach the Steam page below.
    event.stopPropagation();
    event.stopImmediatePropagation?.();
    if (!handled) {
      return;
    }

    event.preventDefault();

    if (Date.now() < state.ignoreOverlayInputUntil) {
      return;
    }

    const source = key.startsWith("Gamepad") ? "steam-key" : "keyboard";
    const isRepeat = Boolean(event.repeat);
    if (key === "ArrowUp") {
      maybeRepeatGamepadAction("up", source, isRepeat);
    } else if (key === "ArrowDown") {
      maybeRepeatGamepadAction("down", source, isRepeat);
    } else if (key === "ArrowLeft") {
      maybeRepeatGamepadAction("left", source, isRepeat);
    } else if (key === "ArrowRight") {
      maybeRepeatGamepadAction("right", source, isRepeat);
    } else if (key === "Escape" || key === "GamepadB") {
      maybeRepeatGamepadAction("b", source, isRepeat);
    } else if (isPreviousTypeKey) {
      maybeRepeatGamepadAction("previous-type", source, isRepeat);
    } else if (isNextTypeKey) {
      maybeRepeatGamepadAction("next-type", source, isRepeat);
    } else {
      maybeRepeatGamepadAction("a", source, isRepeat);
    }
  }

  function swallowOverlayInput(event) {
    if (!state.overlay) {
      return;
    }

    event.stopPropagation();
    event.stopImmediatePropagation?.();
  }

  function handleGamepadAction(action) {
    if (Date.now() < state.ignoreOverlayInputUntil) {
      return;
    }

    if (action === "a") {
      activateArtworkFocus();
    } else if (action === "b") {
      closeOverlay();
    } else if (action === "previous-type" && state.overlayMode === "artwork") {
      cycleArtworkType(-1);
    } else if (action === "next-type" && state.overlayMode === "artwork") {
      cycleArtworkType(1);
    } else {
      moveArtworkFocus(action);
    }
  }

  function maybeRepeatGamepadAction(action, source = "browser-gamepad", isRepeat = true) {
    const now = Date.now();
    if (now < state.ignoreOverlayInputUntil) {
      return;
    }

    const isSteamSource = source.includes("steam") || source === "remote";
    if (isSteamSource) {
      state.lastSteamGamepadInput = action;
      state.lastSteamGamepadInputAt = now;
      // The browser pad may have reported this very press a moment earlier.
      if (
        action === state.lastBrowserGamepadInput &&
        now - state.lastBrowserGamepadInputAt < 220
      ) {
        state.lastBrowserGamepadInput = "";
        return;
      }
    } else if (
      action === state.lastSteamGamepadInput &&
      now - state.lastSteamGamepadInputAt < 220
    ) {
      return;
    }

    // A fresh press always counts, so quick taps are never swallowed; held
    // buttons and the per-frame browser gamepad poll repeat at a steady pace.
    const freshPress = !isRepeat && source !== "browser-gamepad";
    if (
      !freshPress &&
      state.lastGamepadInput === action &&
      now - state.lastGamepadInputAt < 150
    ) {
      return;
    }

    state.lastGamepadInput = action;
    state.lastGamepadInputAt = now;
    if (source === "browser-gamepad") {
      state.lastBrowserGamepadInput = action;
      state.lastBrowserGamepadInputAt = now;
    }
    handleGamepadAction(action);
  }

  function getPressedArtworkGamepadActions() {
    const gamepads = typeof navigator.getGamepads === "function" ? navigator.getGamepads() : [];
    const gamepad = [...gamepads].find(Boolean);
    const pressed = new Set();
    if (!gamepad) {
      return pressed;
    }

    const buttonMap = [
      [0, "a"],
      [1, "b"],
      [4, "previous-type"],
      [5, "next-type"],
      [12, "up"],
      [13, "down"],
      [14, "left"],
      [15, "right"],
    ];

    for (const [index, action] of buttonMap) {
      if (gamepad.buttons[index]?.pressed) {
        pressed.add(action);
      }
    }

    return pressed;
  }

  function pollArtworkGamepad() {
    if (!state.overlay) {
      stopArtworkGamepadLoop();
      return;
    }

    const gamepads = typeof navigator.getGamepads === "function" ? navigator.getGamepads() : [];
    const gamepad = [...gamepads].find(Boolean);
    // Steam's relayed input is authoritative; reading the same pad here as
    // well made every press move twice. The browser pad is only a fallback.
    if (gamepad && isSteamInputRelayActive()) {
      state.pressedGamepadButtons = getPressedArtworkGamepadActions();
    } else if (gamepad) {
      const pressed = new Set();
      const buttonMap = [
        [0, "a"],
        [1, "b"],
        [4, "previous-type"],
        [5, "next-type"],
        [12, "up"],
        [13, "down"],
        [14, "left"],
        [15, "right"],
      ];

      for (const [index, action] of buttonMap) {
        if (gamepad.buttons[index]?.pressed) {
          pressed.add(action);
          if (!state.pressedGamepadButtons.has(action) || action === "up" || action === "down" || action === "left" || action === "right") {
            maybeRepeatGamepadAction(action, "browser-gamepad");
          }
        }
      }

      const axisX = Math.abs(gamepad.axes[0] || 0) > 0.55 ? gamepad.axes[0] : 0;
      const axisY = Math.abs(gamepad.axes[1] || 0) > 0.55 ? gamepad.axes[1] : 0;
      if (Math.abs(axisY) >= Math.abs(axisX) && axisY) {
        maybeRepeatGamepadAction(axisY > 0 ? "down" : "up", "browser-gamepad");
      } else if (axisX) {
        maybeRepeatGamepadAction(axisX > 0 ? "right" : "left", "browser-gamepad");
      } else if (!pressed.size) {
        state.lastGamepadInput = "";
      }

      state.pressedGamepadButtons = pressed;
    }

    state.gamepadFrame = window.requestAnimationFrame(pollArtworkGamepad);
  }

  function startArtworkGamepadLoop() {
    stopArtworkGamepadLoop();
    state.lastGamepadInput = "";
    state.pressedGamepadButtons = getPressedArtworkGamepadActions();
    state.gamepadFrame = window.requestAnimationFrame(pollArtworkGamepad);
  }

  function stopArtworkGamepadLoop() {
    if (state.gamepadFrame) {
      window.cancelAnimationFrame(state.gamepadFrame);
      state.gamepadFrame = 0;
    }
  }

  function attachOverlayInputTrap() {
    window.addEventListener("keydown", handleOverlayKeyDown, true);
    window.addEventListener("keyup", swallowOverlayInput, true);
    window.addEventListener("keypress", swallowOverlayInput, true);
  }

  function detachOverlayInputTrap() {
    window.removeEventListener("keydown", handleOverlayKeyDown, true);
    window.removeEventListener("keyup", swallowOverlayInput, true);
    window.removeEventListener("keypress", swallowOverlayInput, true);
  }

  function renderMetadataOverlay() {
    disconnectAnimatedPreviewObserver();
    state.overlay.textContent = "";
    const snapshot = state.metadataSnapshot;
    const selectedMatch = state.metadataMatches.find(
      (match) => Number(match?.steamAppId) === Number(state.selectedMetadataSteamAppId),
    );

    const shell = createElement("div", "steamtools-artwork-shell");
    const head = createElement("div", "steamtools-artwork-head");
    const titleWrap = createElement("div");
    titleWrap.append(createElement("div", "steamtools-artwork-kicker", "OmniLibrary · Game Data"));
    titleWrap.append(createElement("div", "steamtools-artwork-title", state.title));
    head.append(titleWrap);

    const search = createElement("div", "steamtools-artwork-search");
    const input = createElement("input");
    input.value = state.query;
    input.placeholder = "Search Steam for the correct game";
    input.addEventListener("input", () => {
      state.query = input.value;
    });
    input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        void searchMetadataMatches(state.query);
      } else if (event.key === "Escape") {
        closeOverlay();
      }
    });
    search.append(input);
    search.append(button("steamtools-artwork-search-button", "Search", () => {
      void searchMetadataMatches(state.query);
    }));

    const body = createElement("div", "steamtools-artwork-body");
    const side = createElement("div", "steamtools-artwork-side");
    side.append(createElement("div", "steamtools-artwork-side-label", "Steam Match"));
    for (const match of state.metadataMatches) {
      const matchButton = button(
        `steamtools-artwork-game${Number(match.steamAppId) === Number(state.selectedMetadataSteamAppId) ? " is-active" : ""}`,
        match.title,
        () => {
          state.selectedMetadataSteamAppId = Number(match.steamAppId);
          state.status = `Selected ${match.title}. Press Apply Match to save it.`;
          renderOverlay();
        },
      );
      const detail = [
        `Steam ${match.steamAppId}`,
        match.price || "",
        Number(match.matchScore) <= 1 ? "Close title match" : "Search result",
      ].filter(Boolean).join(" · ");
      matchButton.append(createElement("span", null, detail));
      side.append(matchButton);
    }

    const results = createElement("div", "steamtools-artwork-results");
    const preview = createElement("div", "steamtools-metadata-preview");
    const card = createElement("div", "steamtools-metadata-card");
    const sourceSteamAppId = Number(snapshot?.sourceSteamAppId || 0);
    const sourceLabel = snapshot?.sourceLabel || state.metadataStoreId || "No source";
    const matchMode = snapshot?.matchMode === "manual" ? "Selected match" : "Automatic match";
    card.append(createElement(
      "div",
      "steamtools-metadata-source",
      `${matchMode} · ${sourceLabel}${sourceSteamAppId ? ` · Steam ${sourceSteamAppId}` : ""}`,
    ));
    card.append(createElement("h2", null, selectedMatch?.title || snapshot?.title || state.title));
    card.append(createElement(
      "p",
      null,
      snapshot?.gameInfo?.description ||
        snapshot?.gameInfo?.shortDescription ||
        "No description is available yet. Search for the correct Steam title or refresh the automatic match.",
    ));
    preview.append(card);

    const actions = createElement("div", "steamtools-metadata-actions");
    const applyButton = button("steamtools-metadata-action", "Apply Match", () => {
      void applyMetadataMatch(state.selectedMetadataSteamAppId);
    });
    applyButton.disabled = !state.selectedMetadataSteamAppId || state.metadataApplying;
    actions.append(applyButton);
    const automaticButton = button("steamtools-metadata-action", "Use Automatic Match", () => {
      void clearMetadataMatch();
    });
    automaticButton.disabled = snapshot?.matchMode !== "manual" || state.metadataApplying;
    actions.append(automaticButton);
    const refreshButton = button("steamtools-metadata-action", "Refresh Now", () => {
      void loadMetadataSnapshot(true);
    });
    refreshButton.disabled = state.metadataLoading || state.metadataApplying;
    actions.append(refreshButton);
    preview.append(actions);

    if (state.error || state.metadataLoading || state.metadataApplying || state.status) {
      preview.append(createElement(
        "div",
        `steamtools-artwork-message${state.error ? " is-error" : ""}`,
        state.error ||
          (state.metadataApplying ? "Saving the selected match..." : "") ||
          (state.metadataLoading ? "Loading game data..." : "") ||
          state.status,
      ));
    }

    results.append(preview);
    body.append(side);
    body.append(results);
    shell.append(head);
    shell.append(search);
    shell.append(body);
    state.overlay.append(shell);

    const hint = createElement("div", "steamtools-artwork-controller-hint");
    hint.append(createElement("span", "steamtools-artwork-controller-key", "D-pad"));
    hint.append(createElement("span", null, "Navigate"));
    hint.append(createElement("span", "steamtools-artwork-controller-key", "A"));
    hint.append(createElement("span", null, "Select"));
    hint.append(createElement("span", "steamtools-artwork-controller-key", "B"));
    hint.append(createElement("span", null, "Back"));
    state.overlay.append(hint);

    window.requestAnimationFrame(() => refreshArtworkFocus());
  }

  function renderOverlay() {
    if (!state.overlay) {
      return;
    }

    if (state.overlayMode === "metadata") {
      renderMetadataOverlay();
      return;
    }

    const activeType = getActiveType();
    disconnectAnimatedPreviewObserver();
    state.overlay.textContent = "";

    const shell = createElement("div", "steamtools-artwork-shell is-artwork-mode");
    const head = createElement("div", "steamtools-artwork-head");
    const titleWrap = createElement("div", "steamtools-artwork-lockup");
    const brandRow = createElement("div", "steamtools-artwork-brand-row");
    brandRow.append(createElement("div", "steamtools-artwork-brand-mark", "TFS"));
    brandRow.append(createElement("div", "steamtools-artwork-kicker", "TFS Artworks"));
    titleWrap.append(brandRow);
    titleWrap.append(createElement("div", "steamtools-artwork-title", state.title));
    head.append(titleWrap);
    head.append(createElement(
      "div",
      "steamtools-artwork-head-summary",
      "SteamGridDB - quality ranked",
    ));

    const search = createElement("div", "steamtools-artwork-search");
    const input = createElement("input");
    input.value = state.query;
    input.placeholder = "Search games on SteamGridDB";
    input.addEventListener("input", () => {
      state.query = input.value;
    });
    input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        void searchGames(state.query);
      } else if (event.key === "Escape") {
        closeOverlay();
      }
    });
    search.append(input);
    search.append(button("steamtools-artwork-search-button", "Search", () => {
      void searchGames(state.query);
    }));

    const commandBar = createElement("div", "steamtools-artwork-command-bar");
    commandBar.append(search);

    const body = createElement("div", "steamtools-artwork-body");
    const side = createElement("div", "steamtools-artwork-side");
    side.append(createElement("div", "steamtools-artwork-side-label", "Game Match"));
    for (const game of state.games) {
      const gameButton = button(
        `steamtools-artwork-game${Number(game.id) === Number(state.selectedGameId) ? " is-active" : ""}`,
        game.name,
        () => {
          state.selectedGameId = Number(game.id);
          state.pendingInitialAssetsFocus = true;
          void loadAssets();
        },
      );
      const detail = createElement("span", null, game.verified ? "Verified" : "Community match");
      gameButton.append(detail);
      side.append(gameButton);
    }

    const results = createElement("div", "steamtools-artwork-results");
    const typeRail = createElement("div", "steamtools-artwork-type-rail");
    typeRail.append(button("steamtools-artwork-type-shoulder", "LB", () => {
      cycleArtworkType(-1);
    }));

    const typeCenter = createElement("div", "steamtools-artwork-type-center");
    const typeStack = createElement("div", "steamtools-artwork-type-stack");
    for (const type of assetTypes) {
      const chip = button(
        `steamtools-artwork-type-chip${type.id === state.activeType ? " is-active" : ""}`,
        type.label,
        () => {
          setArtworkType(type.id);
        },
      );
      chip.append(createElement("span", null, type.hint));
      typeStack.append(chip);
    }
    typeCenter.append(typeStack);
    typeRail.append(typeCenter);
    typeRail.append(button("steamtools-artwork-type-shoulder", "RB", () => {
      cycleArtworkType(1);
    }));
    commandBar.append(typeRail);

    const resultHead = createElement("div", "steamtools-artwork-results-head");
    const resultTitle = createElement("div", "steamtools-artwork-results-title");
    const animatedCount = state.assets.filter((asset) => asset?.isAnimated).length;
    resultTitle.append(createElement("strong", null, `${activeType.label} artwork`));
    resultTitle.append(createElement(
      "span",
      null,
      state.assets.length
        ? `${state.assets.length} options${animatedCount ? ` - ${animatedCount} animated` : ""} - recommended ${activeType.recommended} - best quality first`
        : `Recommended size ${activeType.recommended}`,
    ));
    resultHead.append(resultTitle);

    const legend = createElement("div", "steamtools-artwork-quality-legend");
    for (const item of [
      ["high", "High"],
      ["good", "Good"],
      ["usable", "Usable"],
      ["low", "Low"],
    ]) {
      const legendItem = createElement("span", "steamtools-artwork-legend-item");
      legendItem.append(createElement("span", `steamtools-artwork-legend-dot is-${item[0]}`));
      legendItem.append(createElement("span", null, item[1]));
      legend.append(legendItem);
    }
    resultHead.append(legend);
    results.append(resultHead);

    const showInlineMessage =
      state.error ||
      state.loadingGames ||
      state.loadingAssets ||
      state.applying ||
      (!state.assets.length && state.status);
    if (showInlineMessage) {
      results.append(createElement(
        "div",
        `steamtools-artwork-message${state.error ? " is-error" : ""}`,
        state.error ||
          (state.applying ? "Applying artwork..." : "") ||
          (state.loadingGames ? "Searching SteamGridDB..." : "") ||
          (state.loadingAssets ? "Loading artwork..." : "") ||
          state.status,
      ));
    }

    const gridClass =
      state.activeType === "grid_l"
        ? "steamtools-artwork-grid is-wide"
        : state.activeType === "hero"
          ? "steamtools-artwork-grid is-hero"
          : state.activeType === "logo"
            ? "steamtools-artwork-grid is-logo"
            : state.activeType === "icon"
              ? "steamtools-artwork-grid is-icon"
              : "steamtools-artwork-grid";
    const grid = createElement("div", gridClass);
    for (const asset of state.assets) {
      const assetKey = `${state.activeType}:${asset.id || asset.url}`;
      const isApplied = assetKey === state.lastAppliedAssetKey;
      const quality = getArtworkQuality(asset);
      const assetButton = button(
        `steamtools-artwork-asset is-quality-${quality.level}${asset.isAnimated ? " is-animated" : ""}${isApplied ? " is-applied" : ""}`,
        "",
        () => {
        void applyAsset(asset);
        },
      );
      assetButton.title = `${quality.label}: ${quality.detail}`;
      assetButton.setAttribute(
        "aria-label",
        `${activeType.label} ${asset.width || "unknown"} by ${asset.height || "unknown"}, ${quality.label}, ${quality.fit}${asset.isAnimated ? ", animated" : ""}. Apply artwork.`,
      );
      const imageWrap = createElement("div", "steamtools-artwork-asset-image-wrap");
      imageWrap.append(asset.isAnimated
        ? createAnimatedArtworkPreview(asset, activeType)
        : createStillArtworkPreview(asset, activeType));
      imageWrap.append(createElement(
        "span",
        `steamtools-artwork-quality-badge is-${quality.level}`,
        quality.label,
      ));
      if (asset.isAnimated) {
        imageWrap.append(createElement("span", "steamtools-artwork-motion-badge", "Animated"));
      }
      if (isApplied) {
        imageWrap.append(createElement("span", "steamtools-artwork-applied-badge", "Applied"));
      }
      assetButton.append(imageWrap);
      const dimensions = asset.width && asset.height ? `${asset.width} x ${asset.height}` : "Unknown size";
      const meta = createElement("div", "steamtools-artwork-asset-meta");
      meta.append(createElement("div", "steamtools-artwork-asset-resolution", dimensions));
      meta.append(createElement(
        "div",
        "steamtools-artwork-asset-fit",
        `${quality.fit}${asset.isAnimated ? " - Animated" : ""} - ${isApplied ? "Applied" : "Press A to apply"}`,
      ));
      assetButton.append(meta);
      grid.append(assetButton);
    }
    results.append(grid);

    body.append(side);
    body.append(results);
    shell.append(head);
    shell.append(commandBar);
    shell.append(body);
    state.overlay.append(shell);

    const hint = createElement("div", "steamtools-artwork-controller-hint");
    hint.append(createElement("span", "steamtools-artwork-controller-key", "LB/RB"));
    hint.append(createElement("span", null, "Artwork type"));
    hint.append(createElement("span", "steamtools-artwork-controller-key", "A"));
    hint.append(createElement("span", null, "Apply"));
    hint.append(createElement("span", "steamtools-artwork-controller-key", "B"));
    hint.append(createElement("span", null, "Back"));
    state.overlay.append(hint);

    if (state.error || state.applying) {
      const statusText =
        state.error ||
        (state.applying ? "Applying artwork..." : "");
      const isSuccess = /applied|written/i.test(statusText || "") && !state.error && !state.applying;
      state.overlay.append(createElement(
        "div",
        `steamtools-artwork-floating-status${state.error ? " is-error" : ""}${isSuccess ? " is-success" : ""}`,
        statusText,
      ));
    }

    window.requestAnimationFrame(() => {
      if (state.pendingInitialAssetsFocus && state.assets.length) {
        focusFirstArtworkAsset();
      } else {
        refreshArtworkFocus();
      }
    });
  }

  // The old artwork overlay is retired: every way that used to open it (game
  // context menu, Quick Access, open requests) now opens the TFS Artworks page.
  function openArtworkPage(appId) {
    const id = normalizeSteamAppId(appId);
    if (!id) return false;
    try {
      const page = getArtworkPageRuntime();
      if (page?.openEditor?.(id)) return true;
    } catch {}
    try {
      const store = window.SteamUIStore || window.opener?.SteamUIStore;
      const main = store?.m_WindowStore?.GamepadUIMainWindowInstance;
      if (typeof main?.Navigate === "function") {
        main.Navigate(`/tfs/artworks/${id}`);
        return true;
      }
    } catch {}
    return false;
  }

  function openOverlay(context) {
    if (!openArtworkPage(context?.appId)) {
      console.warn("[Tools for Steam] The TFS Artworks page is not available yet.", context);
    }
    return;
    // eslint-disable-next-line no-unreachable
    injectStyles();

    state.overlayMode = "artwork";
    state.appId = normalizeSteamAppId(context?.appId);
    state.title = context?.title || "Selected Game";
    state.query = state.title;
    state.activeType = "grid_p";
    state.selectedGameId = 0;
    state.games = [];
    state.assets = [];
    state.status = "";
    state.error = "";
    state.focusZone = "side";
    state.pendingInitialAssetsFocus = true;

    const returnFocusElement = getOverlayReturnFocusElement();
    closeOverlay({ restoreFocus: false });
    state.returnFocusElement = returnFocusElement;
    state.ignoreOverlayInputUntil = Date.now() + 700;
    state.currentOpenKey = getOpenRequestKey(state.appId, state.title);
    state.overlay = createElement("div", "steamtools-artwork-overlay");
    state.overlay.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        closeOverlay();
      }
    });
    document.body.append(state.overlay);
    setRemoteOverlayActive(true);
    attachOverlayInputTrap();
    renderOverlay();
    startArtworkGamepadLoop();
    startArtworkOverlayAnnouncements();
    void searchGames(state.query);
  }

  function openMetadataOverlay(context, storeId) {
    injectStyles();

    state.overlayMode = "metadata";
    state.appId = normalizeSteamAppId(context?.appId);
    state.title = context?.title || "Selected Game";
    state.query = state.title;
    state.metadataStoreId = storeId || "";
    state.metadataSnapshot = null;
    state.metadataMatches = [];
    state.selectedMetadataSteamAppId = 0;
    state.metadataLoading = false;
    state.metadataApplying = false;
    state.status = "";
    state.error = "";
    state.focusZone = "side";
    state.pendingInitialAssetsFocus = false;

    const returnFocusElement = getOverlayReturnFocusElement();
    closeOverlay({ restoreFocus: false });
    state.returnFocusElement = returnFocusElement;
    state.overlayMode = "metadata";
    state.ignoreOverlayInputUntil = Date.now() + 700;
    state.currentOpenKey = `metadata:${getOpenRequestKey(state.appId, state.title)}`;
    state.overlay = createElement("div", "steamtools-artwork-overlay");
    state.overlay.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        closeOverlay();
      }
    });
    document.body.append(state.overlay);
    setRemoteOverlayActive(true);
    attachOverlayInputTrap();
    renderOverlay();
    startArtworkGamepadLoop();
    startArtworkOverlayAnnouncements();
    void loadMetadataSnapshot(false).then(() => searchMetadataMatches(state.query));
  }

  function getOverlayReturnFocusElement() {
    // Reopening keeps the original Steam target instead of an overlay button.
    if (state.overlay && state.returnFocusElement) {
      return state.returnFocusElement;
    }

    const active = document.activeElement;
    return active instanceof HTMLElement &&
      active !== document.body &&
      !active.closest(".steamtools-artwork-overlay")
      ? active
      : null;
  }

  function restoreOverlayReturnFocus(element) {
    // Hand focus back to the Steam control that was focused before the
    // overlay opened, so B lands where the user came from.
    if (!(element instanceof HTMLElement) || !element.isConnected) {
      return;
    }

    try {
      element.focus({ preventScroll: true });
    } catch {
    }
  }

  function closeOverlay(options = {}) {
    const returnFocusElement = state.returnFocusElement;
    const hadOverlay = Boolean(state.overlay);
    state.returnFocusElement = null;
    state.gameSearchRequestId += 1;
    state.assetRequestId += 1;
    disconnectAnimatedPreviewObserver();
    if (state.currentOpenKey) {
      state.lastClosedKey = state.currentOpenKey;
      state.lastClosedAt = Date.now();
    }
    stopArtworkGamepadLoop();
    stopArtworkOverlayAnnouncements();
    setRemoteOverlayActive(false);
    detachOverlayInputTrap();
    for (const overlay of document.querySelectorAll(".steamtools-artwork-overlay")) {
      overlay.remove();
    }
    state.overlay = null;
    state.currentOpenKey = "";
    state.focusItems = [];
    state.focusIndex = 0;
    if (hadOverlay && options.restoreFocus !== false) {
      restoreOverlayReturnFocus(returnFocusElement);
    }
  }

  function destroy() {
    closeOverlay();
    removeArtworkContextRows();
    removeOmniLibraryUninstallContextRows();
    removeOmniLibraryUninstallNotice();
    uninstallArtworkCatchAllInput();

    if (state.catchAllReleaseTimer) {
      window.clearTimeout(state.catchAllReleaseTimer);
      state.catchAllReleaseTimer = null;
    }

    if (state.openRequestTimer) {
      window.clearInterval(state.openRequestTimer);
      state.openRequestTimer = null;
    }

    if (state.localOpenRequestTimer) {
      window.clearInterval(state.localOpenRequestTimer);
      state.localOpenRequestTimer = null;
    }

    if (state.artworkSettingsTimer) {
      window.clearInterval(state.artworkSettingsTimer);
      state.artworkSettingsTimer = null;
    }

    if (state.inputPollTimer) {
      window.clearInterval(state.inputPollTimer);
      state.inputPollTimer = null;
    }

    if (state.refreshTimer) {
      window.clearTimeout(state.refreshTimer);
      state.refreshTimer = null;
    }

    if (state.reactPatchRetryTimer) {
      window.clearTimeout(state.reactPatchRetryTimer);
      state.reactPatchRetryTimer = null;
    }

    state.observer?.disconnect();
    state.observer = null;
    try {
      state.omniLibraryStateUnsubscribe?.();
    } catch {
    }
    state.omniLibraryStateUnsubscribe = null;

    if (state.openRequestStorageHandler) {
      window.removeEventListener("storage", state.openRequestStorageHandler);
      state.openRequestStorageHandler = null;
    }

    if (state.inputStorageHandler) {
      window.removeEventListener("storage", state.inputStorageHandler);
      state.inputStorageHandler = null;
    }

    if (state.artworkChannel && state.artworkChannelHandler) {
      state.artworkChannel.removeEventListener("message", state.artworkChannelHandler);
    }
    uninstallContextActivationCapture();
    uninstallContextTracking();
    try {
      state.artworkChannel?.close();
    } catch {
    }
    state.artworkChannel = null;
    state.artworkChannelHandler = null;
  }

  function refresh() {
    setupArtworkInputBridge();
    injectStyles();
    installContextActivationCapture();
    installContextTracking();
    ensureReactContextMenuPatch();
    installOmniLibraryContextMenuLifecycle();
    void loadArtworkSettings();
  }

  function start() {
    setupArtworkInputBridge();
    injectStyles();
    installContextActivationCapture();
    installContextTracking();
    ensureReactContextMenuPatch();
    installOmniLibraryContextMenuLifecycle();
    startArtworkSettingsPolling();
    startOpenRequestPolling();
    state.pendingMenuNodes = new Set();
    state.observer = new MutationObserver((records) => {
      for (const record of records) {
        for (const node of record.addedNodes) {
          if (node.nodeType === 1 && !node.classList?.contains("steamtools-artwork-context-row")) {
            state.pendingMenuNodes.add(node);
          }
        }
      }
      if (!state.pendingMenuNodes.size) {
        return;
      }

      window.clearTimeout(state.refreshTimer);
      state.refreshTimer = window.setTimeout(() => {
        const addedNodes = [...state.pendingMenuNodes];
        state.pendingMenuNodes.clear();
        const menus = findMenusForAddedNodes(addedNodes);
        if (menus.length) {
          patchMenus(menus);
        }
      }, 80);
    });
    state.observer.observe(document.documentElement, { childList: true, subtree: true });
  }

  window.ToolsForSteamArtwork = {
    version,
    refresh,
    open: openOverlay,
    close: closeOverlay,
    destroy,
    debug: () => ({
      overlayCount: document.querySelectorAll(".steamtools-artwork-overlay").length,
      remoteOverlayActive: state.remoteOverlayActive,
      focusZone: state.focusZone,
      focusIndex: state.focusIndex,
      activeType: state.activeType,
      activeTypeLabel: getActiveType().label,
      rawSteamButtons: state.rawSteamButtons,
    }),
  };

  start();
  return "injected";
})();
