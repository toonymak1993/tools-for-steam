(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const version = 42;
  const openRequestStorageKey = "ToolsForSteamArtworkOpenRequest";
  const inputStorageKey = "ToolsForSteamArtworkInput";
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

  const existingArtworkRuntime = window.ToolsForSteamArtwork;
  if (existingArtworkRuntime?.version >= version) {
    existingArtworkRuntime.refresh?.();
    return "injected";
  }
  existingArtworkRuntime?.destroy?.();

  const assetTypes = [
    { id: "grid_p", label: "Cover", hint: "Library portrait" },
    { id: "hero", label: "Hero", hint: "Game detail header" },
    { id: "logo", label: "Logo", hint: "Transparent title logo" },
    { id: "icon", label: "Icon", hint: "Shortcut icon" },
    { id: "grid_l", label: "Wide", hint: "Recent and library wide art" },
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
    loadingAssets: false,
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
    lastArtworkInputNonce: "",
    inputPollTimer: null,
    inputStorageHandler: null,
    overlayAnnounceTimer: null,
    remoteOverlayActive: false,
    catchAllInstalled: false,
    previousCatchAllGamepadInput: null,
    catchAllButtonState: {},
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

  function shouldForwardSteamButton(button, action) {
    const now = Date.now();
    const repeatMs = action === "up" || action === "down" || action === "left" || action === "right"
      ? 230
      : 340;
    const lastMs = state.catchAllButtonState[button] || 0;
    if (now - lastMs < repeatMs) {
      return false;
    }

    state.catchAllButtonState[button] = now;
    return true;
  }

  function installArtworkCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || state.catchAllInstalled) {
      return;
    }

    const previous = focusNav.m_fnCatchAllGamepadInput;
    const callback = (button) => {
      const action = getArtworkActionFromSteamButton(button);
      const overlayInputActive = state.remoteOverlayActive || Boolean(state.overlay);
      rememberRawSteamButton(button, action);

      if (!overlayInputActive) {
        if (action && Date.now() < state.catchAllSuppressUntil) {
          return true;
        }

        return typeof previous === "function" ? previous(button) : false;
      }

      if (!action) {
        return true;
      }

      if (shouldForwardSteamButton(button, action)) {
        postArtworkMessage({ type: "input", action, source: "steam-catch-all" });
      }

      return true;
    };

    callback.__steamToolsArtworkCatchAll = true;
    state.previousCatchAllGamepadInput = previous?.__steamToolsArtworkCatchAll ? null : previous;
    focusNav.SetCatchAllGamepadInput(callback);
    state.catchAllInstalled = true;
  }

  function uninstallArtworkCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || !state.catchAllInstalled) {
      return;
    }

    if (focusNav.m_fnCatchAllGamepadInput?.__steamToolsArtworkCatchAll) {
      focusNav.SetCatchAllGamepadInput(state.previousCatchAllGamepadInput || undefined);
    }

    state.catchAllInstalled = false;
    state.previousCatchAllGamepadInput = null;
    state.catchAllButtonState = {};
    state.catchAllSuppressUntil = 0;
  }

  function releaseArtworkInputCapture() {
    state.remoteOverlayActive = false;
    if (state.catchAllReleaseTimer) {
      window.clearTimeout(state.catchAllReleaseTimer);
      state.catchAllReleaseTimer = null;
    }
    state.catchAllSuppressUntil = 0;
    uninstallArtworkCatchAllInput();
  }

  function setRemoteOverlayActive(active) {
    state.remoteOverlayActive = Boolean(active);
    if (state.remoteOverlayActive) {
      if (state.catchAllReleaseTimer) {
        window.clearTimeout(state.catchAllReleaseTimer);
        state.catchAllReleaseTimer = null;
      }
      state.catchAllSuppressUntil = 0;
      installArtworkCatchAllInput();
    } else {
      releaseArtworkInputCapture();
    }
  }

  function consumeArtworkInput(raw) {
    if (!raw || !state.overlay) {
      return;
    }

    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type !== "input" || !payload.action || payload.nonce === state.lastArtworkInputNonce) {
        return;
      }

      state.lastArtworkInputNonce = payload.nonce;
      maybeRepeatGamepadAction(payload.action, String(payload.source || "remote"));
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
        }
      };
      window.addEventListener("storage", state.inputStorageHandler);
    }

    if (!state.inputPollTimer) {
      state.inputPollTimer = window.setInterval(() => {
        try {
          consumeArtworkInput(localStorage.getItem(inputStorageKey));
          consumeArtworkOverlayState(localStorage.getItem(overlayStateStorageKey));
        } catch {
        }
      }, 100);
    }
  }

  function injectStyles() {
    if (document.getElementById("steamtools-artwork-style")) {
      return;
    }

    const style = document.createElement("style");
    style.id = "steamtools-artwork-style";
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
        background:
          radial-gradient(circle at 18% 8%, rgba(71, 108, 140, 0.24), transparent 34%),
          linear-gradient(180deg, rgba(9, 14, 20, 0.98), rgba(6, 10, 15, 0.98));
        color: #f4f7fb;
        font-family: "Motiva Sans", Arial, sans-serif;
        overflow: hidden;
      }

      .steamtools-artwork-shell {
        height: 100%;
        box-sizing: border-box;
        padding: clamp(18px, 2.8vw, 38px);
        display: grid;
        grid-template-rows: auto auto auto minmax(0, 1fr);
        gap: 12px;
      }

      .steamtools-artwork-head {
        min-height: 22px;
      }

      .steamtools-artwork-kicker {
        color: #70c8ff;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        font-size: 14px;
        font-weight: 800;
      }

      .steamtools-artwork-title {
        margin-top: 8px;
        font-size: clamp(34px, 5vw, 62px);
        font-weight: 900;
        line-height: 0.98;
      }

      .steamtools-artwork-subtitle {
        margin-top: 12px;
        max-width: 920px;
        color: #a9bacb;
        font-size: clamp(18px, 2.3vw, 27px);
        line-height: 1.28;
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
      .steamtools-artwork-type-shoulder:hover,
      .steamtools-artwork-asset:focus,
      .steamtools-artwork-asset.is-controller-focus,
      .steamtools-artwork-asset:hover {
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

      .steamtools-artwork-search {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 12px;
      }

      .steamtools-artwork-search input {
        min-height: 52px;
        border: 2px solid rgba(146, 165, 185, 0.26);
        border-radius: 16px;
        background: #0c1219;
        color: #f3f7fb;
        font-size: 21px;
        padding: 0 18px;
      }

      .steamtools-artwork-search input:focus {
        outline: none;
        border-color: rgba(112, 200, 255, 0.72);
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
        border-radius: 16px;
        min-width: 132px;
        font-size: 20px;
        font-weight: 800;
      }

      .steamtools-artwork-body {
        min-height: 0;
        display: grid;
        grid-template-columns: minmax(190px, 260px) minmax(0, 1fr);
        gap: 14px;
      }

      .steamtools-artwork-side,
      .steamtools-artwork-results {
        min-height: 0;
        border-radius: 24px;
        background: rgba(22, 29, 38, 0.82);
        box-shadow: inset 0 0 0 1px rgba(140, 166, 190, 0.1);
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
        padding: 14px;
        overflow: auto;
      }

      .steamtools-artwork-type-rail {
        width: min(960px, 100%);
        justify-self: end;
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
        background:
          linear-gradient(90deg, rgba(72, 82, 97, 0.92), rgba(48, 55, 66, 0.86));
        box-shadow: inset 0 0 0 1px rgba(180, 201, 220, 0.14);
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
        grid-template-columns: repeat(auto-fill, minmax(138px, 1fr));
        gap: 11px;
      }

      .steamtools-artwork-grid.is-wide,
      .steamtools-artwork-grid.is-hero {
        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      }

      .steamtools-artwork-asset {
        min-width: 0;
        border-radius: 15px;
        padding: 8px;
        text-align: left;
      }

      .steamtools-artwork-asset img {
        display: block;
        width: 100%;
        aspect-ratio: 2 / 3;
        object-fit: cover;
        border-radius: 10px;
        background: #0c1118;
      }

      .steamtools-artwork-grid.is-wide .steamtools-artwork-asset img {
        aspect-ratio: 92 / 43;
      }

      .steamtools-artwork-grid.is-hero .steamtools-artwork-asset img {
        aspect-ratio: 192 / 62;
      }

      .steamtools-artwork-grid.is-logo .steamtools-artwork-asset img,
      .steamtools-artwork-grid.is-icon .steamtools-artwork-asset img {
        aspect-ratio: 1 / 1;
        object-fit: contain;
        padding: 16px;
        box-sizing: border-box;
      }

      .steamtools-artwork-asset-meta {
        margin-top: 7px;
        color: #aebdcb;
        font-size: 13px;
        font-weight: 700;
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

      @media (max-width: 900px) {
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

  function isPropertiesText(value) {
    const text = normalizeMenuText(value);
    return text === "properties" || text === localizedCommands.properties;
  }

  function isCancelText(value) {
    const text = normalizeMenuText(value);
    return text === "cancel" || text === localizedCommands.cancel;
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
      ".steamtools-omnilibrary-uninstall-context-row, .steamtools-omnilibrary-repair-context-row",
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

  function showOmniLibraryUninstallNotice(storeId, errorMessage = "") {
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
      `background:${failed ? "rgba(255,102,117,.16)" : storeId === "xbox-game-pass" ? "rgba(74,181,65,.18)" : "rgba(102,192,244,.16)"}`,
      `border:1px solid ${failed ? "rgba(255,102,117,.52)" : storeId === "xbox-game-pass" ? "rgba(107,206,95,.48)" : "rgba(102,192,244,.5)"}`,
      `color:${failed ? "#ff8994" : storeId === "xbox-game-pass" ? "#7bd56f" : "#76c9f6"}`,
      "font-size:17px",
      "font-weight:800",
      "line-height:1",
    ].join(";");
    icon.textContent = failed
      ? "!"
      : storeId === "xbox-game-pass"
        ? "X"
        : "\u2193";

    const content = document.createElement("div");
    content.style.cssText = "min-width:0;padding:15px 18px 16px 7px";

    const title = document.createElement("div");
    title.style.cssText =
      "font-size:14px;font-weight:800;line-height:1.25;letter-spacing:.35px";
    title.textContent = failed
      ? "Uninstall failed"
      : storeId === "xbox-game-pass"
        ? "Continue in Xbox"
        : storeId === "gog-galaxy"
          ? "Uninstalling GOG game"
          : "Uninstalling Epic game";

    const message = document.createElement("div");
    message.style.cssText =
      "margin-top:4px;color:rgba(215,226,237,.76);font-size:13px;font-weight:500;line-height:1.4";
    message.textContent = failed
      ? errorMessage
      : storeId === "xbox-game-pass"
        ? "Please finish uninstalling this game in the Xbox window."
        : storeId === "gog-galaxy"
          ? "Please wait. Managed installs are removed automatically; GOG Galaxy opens only when it owns the installation."
          : "Please wait while OmniLibrary uninstalls this game automatically.";

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
        children: "Change Artwork...",
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
    if (!isOpeningAppContextMenu(items)) {
      return false;
    }

    for (let index = items.length - 1; index >= 0; index -= 1) {
      if (
        items[index]?.key === "tfs-change-artwork" ||
        items[index]?.key === "tfs-omnilibrary-repair" ||
        items[index]?.key === "tfs-omnilibrary-uninstall"
      ) {
        items.splice(index, 1);
      }
    }

    const propertiesIndex = findPropertiesMenuIndex(items);
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
    if (state.contextMenuEnabled) {
      additions.push(createReactArtworkMenuItem(template, resolvedContext));
    }
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

    items.splice(propertiesIndex, 0, ...additions);
    return true;
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

    const commandLabels = new Set([
      "play",
      localizedCommands.play,
      "add to favorites",
      localizedCommands.addToFavorites,
      "add to",
      localizedCommands.addTo,
      "manage",
      localizedCommands.manage,
      "properties",
      localizedCommands.properties,
      "cancel",
      localizedCommands.cancel,
      "change artwork",
    ]);

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
      (text.includes("properties") || text.includes(localizedCommands.properties)) &&
      (
        text.includes("play") ||
        text.includes(localizedCommands.play) ||
        text.includes("manage") ||
        text.includes(localizedCommands.manage)
      )
    );
  }

  function findMenuCandidates() {
    return [...document.querySelectorAll("[role='menu'], [class*='contextmenu'], [class*='ContextMenu'], div")]
      .filter((node) => node instanceof HTMLElement)
      .filter((node) => node.offsetParent !== null)
      .filter((node) => {
        const rect = node.getBoundingClientRect();
        if (rect.width < 240 || rect.width > Math.min(window.innerWidth, 1100) || rect.height < 180) {
          return false;
        }

        const textLength = textOf(node).length;
        return textLength > 30 && textLength < 1200;
      })
      .filter(isGameContextMenu)
      .slice(0, 6);
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
    replaceRowText(row, "Change Artwork...");
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
      if (text === "change artwork") {
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

  function patchMenus() {
    if (!state.contextMenuEnabled) {
      removeArtworkContextRows();
    }

    for (const menu of findMenuCandidates()) {
      if (!isGameContextMenu(menu)) {
        removeArtworkContextRows(menu);
        removeOmniLibraryUninstallContextRows(menu);
        continue;
      }

      const menuContext = getContextDataFromNode(menu);
      const propertiesRow = findPropertiesRow(menu);
      const parent = propertiesRow?.parentElement;
      if (!propertiesRow || !parent) {
        continue;
      }

      const hasArtworkRow =
        menu.querySelector(".steamtools-artwork-context-row") ||
        textOf(menu).toLowerCase().includes("change artwork");
      if (state.contextMenuEnabled && !hasArtworkRow) {
        const row = createArtworkContextRow(propertiesRow, menu, menuContext);
        parent.insertBefore(row, propertiesRow);
      }

      const installedStore = getInstalledOmniLibraryStore(menuContext?.appId);
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
    if (location.href.includes("/routes/")) {
      return false;
    }

    return Boolean(
      document.body &&
      window.innerWidth >= 500 &&
      window.innerHeight >= 300,
    );
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

  async function searchGames(term) {
    state.loadingGames = true;
    state.error = "";
    state.pendingInitialAssetsFocus = true;
    renderOverlay();

    try {
      const query = encodeURIComponent(term || state.title || "");
      state.games = await fetchJson(`api/artwork/search?term=${query}`);
      state.selectedGameId = Number(state.games?.[0]?.id || 0);
      state.status = state.games.length
        ? `Found ${state.games.length} SteamGridDB match${state.games.length === 1 ? "" : "es"}.`
        : "No SteamGridDB matches found. Try a shorter title.";
      await loadAssets();
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.loadingGames = false;
      renderOverlay();
    }
  }

  async function loadAssets() {
    if (!state.selectedGameId) {
      state.assets = [];
      renderOverlay();
      return;
    }

    state.loadingAssets = true;
    state.error = "";
    state.assets = [];
    renderOverlay();

    try {
      const query = new URLSearchParams({
        gameId: String(state.selectedGameId),
        type: state.activeType,
        page: "0",
      });
      state.assets = await fetchJson(`api/artwork/assets?${query.toString()}`);
      state.status = state.assets.length
        ? `Showing ${state.assets.length} ${getActiveType().label.toLowerCase()} result${state.assets.length === 1 ? "" : "s"}.`
        : "No artwork found for this tab.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.loadingAssets = false;
      renderOverlay();
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
    return item?.classList?.contains("steamtools-artwork-asset") ? "assets" : "side";
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

  function getArtworkGridStep() {
    const current = state.focusItems[state.focusIndex];
    if (!current?.classList.contains("steamtools-artwork-asset")) {
      return 1;
    }

    const grid = current.closest(".steamtools-artwork-grid");
    const firstAsset = grid?.querySelector(".steamtools-artwork-asset");
    const gridWidth = grid?.getBoundingClientRect().width || 0;
    const itemWidth = firstAsset?.getBoundingClientRect().width || 0;
    return Math.max(1, Math.floor(gridWidth / Math.max(1, itemWidth + 8)));
  }

  function moveArtworkFocus(direction) {
    refreshArtworkFocus();
    if (!state.focusItems.length) {
      return;
    }

    const zoneItems = getArtworkZoneItems();
    if (!zoneItems.length) {
      return;
    }

    const current = state.focusItems[state.focusIndex];
    const zoneIndex = Math.max(0, zoneItems.indexOf(current));
    const step =
      state.focusZone === "side"
        ? direction === "up" || direction === "left"
          ? -1
          : 1
        : direction === "up"
        ? -getArtworkGridStep()
        : direction === "down"
          ? getArtworkGridStep()
          : direction === "left"
            ? -1
            : 1;
    const nextZoneIndex = (zoneIndex + step + zoneItems.length) % zoneItems.length;
    state.focusIndex = state.focusItems.indexOf(zoneItems[nextZoneIndex]);
    applyArtworkFocus();
  }

  function toggleArtworkFocusZone() {
    refreshArtworkFocus();
    const nextZone = state.focusZone === "assets" ? "side" : "assets";
    const nextItems = getArtworkZoneItems(nextZone);
    if (!nextItems.length) {
      return;
    }

    state.focusZone = nextZone;
    state.focusIndex = state.focusItems.indexOf(nextItems[0]);
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

    if (!handled) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation?.();

    if (Date.now() < state.ignoreOverlayInputUntil) {
      return;
    }

    const source = key.startsWith("Gamepad") ? "steam-key" : "keyboard";
    if (key === "ArrowUp") {
      maybeRepeatGamepadAction("up", source);
    } else if (key === "ArrowDown") {
      maybeRepeatGamepadAction("down", source);
    } else if (key === "ArrowLeft") {
      maybeRepeatGamepadAction("left", source);
    } else if (key === "ArrowRight") {
      maybeRepeatGamepadAction("right", source);
    } else if (key === "Escape" || key === "GamepadB") {
      maybeRepeatGamepadAction("b", source);
    } else if (isPreviousTypeKey) {
      maybeRepeatGamepadAction("previous-type", source);
    } else if (isNextTypeKey) {
      maybeRepeatGamepadAction("next-type", source);
    } else {
      maybeRepeatGamepadAction("a", source);
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
    } else if (action === "previous-type") {
      cycleArtworkType(-1);
    } else if (action === "next-type") {
      cycleArtworkType(1);
    } else {
      moveArtworkFocus(action);
    }
  }

  function maybeRepeatGamepadAction(action, source = "browser-gamepad") {
    const now = Date.now();
    if (now < state.ignoreOverlayInputUntil) {
      return;
    }

    const isSteamSource = source.includes("steam") || source === "remote";
    if (isSteamSource) {
      state.lastSteamGamepadInput = action;
      state.lastSteamGamepadInputAt = now;
    } else if (
      action === state.lastSteamGamepadInput &&
      now - state.lastSteamGamepadInputAt < 220
    ) {
      return;
    }

    const repeatDelay = state.lastGamepadInput === action ? 170 : 260;
    if (state.lastGamepadInput === action && now - state.lastGamepadInputAt < repeatDelay) {
      return;
    }

    state.lastGamepadInput = action;
    state.lastGamepadInputAt = now;
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
    if (gamepad) {
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

  function renderOverlay() {
    if (!state.overlay) {
      return;
    }

    const activeType = getActiveType();
    state.overlay.textContent = "";

    const shell = createElement("div", "steamtools-artwork-shell");
    const head = createElement("div", "steamtools-artwork-head");
    const titleWrap = createElement("div");
    titleWrap.append(createElement("div", "steamtools-artwork-kicker", "SteamGridDB"));
    head.append(titleWrap);

    const search = createElement("div", "steamtools-artwork-search");
    const input = createElement("input");
    input.value = state.query;
    input.placeholder = "Search SteamGridDB";
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
    const currentType = createElement("div", "steamtools-artwork-type-current");
    currentType.append(createElement("span", null, "Artwork Type"));
    currentType.append(createElement("strong", null, activeType.label));
    currentType.append(createElement("em", null, `${getActiveTypeIndex() + 1} of ${assetTypes.length} - ${activeType.hint}`));
    typeCenter.append(currentType);

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
      const assetButton = button(`steamtools-artwork-asset${isApplied ? " is-applied" : ""}`, "", () => {
        void applyAsset(asset);
      });
      const image = document.createElement("img");
      image.loading = "lazy";
      image.decoding = "async";
      image.src = asset.thumbnailUrl || asset.url;
      image.alt = `${activeType.label} artwork`;
      assetButton.append(image);
      const dimensions = asset.width && asset.height ? `${asset.width} x ${asset.height}` : "Unknown size";
      assetButton.append(createElement("div", "steamtools-artwork-asset-meta", `${dimensions} · ${isApplied ? "Applied" : "Apply"}`));
      grid.append(assetButton);
    }
    results.append(grid);

    body.append(side);
    body.append(results);
    shell.append(head);
    shell.append(search);
    shell.append(typeRail);
    shell.append(body);
    state.overlay.append(shell);

    const hint = createElement("div", "steamtools-artwork-controller-hint");
    hint.append(createElement("span", "steamtools-artwork-controller-key", "LB/RB"));
    hint.append(createElement("span", null, "Artwork type"));
    hint.append(createElement("span", "steamtools-artwork-controller-key", "A"));
    hint.append(createElement("span", null, "Apply"));
    hint.append(createElement("span", "steamtools-artwork-controller-key", "B"));
    hint.append(createElement("span", null, "Close"));
    state.overlay.append(hint);

    if (state.error || state.status || state.applying) {
      const statusText =
        state.error ||
        (state.applying ? "Applying artwork..." : "") ||
        state.status;
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

  function openOverlay(context) {
    injectStyles();

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

    closeOverlay();
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

  function closeOverlay() {
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
    installReactContextMenuPatch();
    installOmniLibraryContextMenuLifecycle();
    void loadArtworkSettings();
  }

  function start() {
    setupArtworkInputBridge();
    injectStyles();
    installContextActivationCapture();
    installContextTracking();
    installReactContextMenuPatch();
    installOmniLibraryContextMenuLifecycle();
    startArtworkSettingsPolling();
    startOpenRequestPolling();
    state.observer = new MutationObserver(() => {
      window.clearTimeout(state.refreshTimer);
      state.refreshTimer = window.setTimeout(patchMenus, 80);
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
