(() => {
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";
  const stateVersion = 18;
  const closedPollMs = 700;
  const openPollMs = 2200;
  const inputStorageKey = "ToolsForSteamPluginStoreInput";
  const overlayStateStorageKey = "ToolsForSteamPluginStoreOverlayState";
  const storeChannelName = "ToolsForSteamPluginStoreChannel";

  const previousState = window.__steamLoaderPluginStoreSurfaceState;
  if (previousState?.version !== stateVersion) {
    if (previousState?.pollTimer) {
      window.clearTimeout(previousState.pollTimer);
    }

    if (previousState?.gamepadFrame) {
      window.cancelAnimationFrame(previousState.gamepadFrame);
    }

    if (previousState?.inputPollTimer) {
      window.clearInterval(previousState.inputPollTimer);
    }

    if (previousState?.apiInputPollTimer) {
      window.clearInterval(previousState.apiInputPollTimer);
    }

    if (previousState?.searchRenderTimer) {
      window.clearTimeout(previousState.searchRenderTimer);
    }

    if (previousState?.storeKeyboardLayerTimer) {
      window.clearTimeout(previousState.storeKeyboardLayerTimer);
    }

    if (previousState?.overlayAnnounceTimer) {
      window.clearInterval(previousState.overlayAnnounceTimer);
    }

    if (typeof previousState?.keyHandler === "function") {
      document.removeEventListener("keydown", previousState.keyHandler, true);
      window.removeEventListener("keydown", previousState.keyHandler, true);
    }

    if (typeof previousState?.keyUpHandler === "function") {
      document.removeEventListener("keyup", previousState.keyUpHandler, true);
      window.removeEventListener("keyup", previousState.keyUpHandler, true);
    }

    if (typeof previousState?.keyPressHandler === "function") {
      document.removeEventListener("keypress", previousState.keyPressHandler, true);
      window.removeEventListener("keypress", previousState.keyPressHandler, true);
    }

    const focusNav = window.FocusNavController;
    if (
      focusNav?.SetCatchAllGamepadInput &&
      previousState?.catchAllInstalled &&
      focusNav.m_fnCatchAllGamepadInput?.__steamLoaderPluginStoreCatchAll
    ) {
      focusNav.SetCatchAllGamepadInput(previousState.previousCatchAllGamepadInput || undefined);
    }

    if (typeof previousState?.inputStorageHandler === "function") {
      window.removeEventListener("storage", previousState.inputStorageHandler);
    }

    if (previousState?.storeChannel && previousState?.storeChannelHandler) {
      previousState.storeChannel.removeEventListener("message", previousState.storeChannelHandler);
    }

    try {
      previousState?.storeChannel?.close?.();
    } catch {
    }

    document.getElementById("steamloader-plugin-store-root")?.remove();
    document.getElementById("steamloader-plugin-store-style")?.remove();
  }

  const state =
    previousState?.version === stateVersion
      ? previousState
      : (window.__steamLoaderPluginStoreSurfaceState = {
          version: stateVersion,
          pollTimer: 0,
          open: false,
          loading: false,
          busy: false,
          error: "",
          snapshot: null,
          selectedPluginId: "",
          contextMenuPluginId: "",
          permissionReviewPluginId: "",
          activeSection: "discover",
          storePageIndex: 0,
          searchQuery: "",
          searchPadOpen: false,
          searchRenderTimer: 0,
          searchKeyboardActiveUntil: 0,
          storeKeyboardLayerTimer: 0,
          imageReadyUrls: new Set(),
          root: null,
          focusPending: false,
          focusItems: [],
          focusIndex: 0,
          focusKey: "",
          gamepadFrame: 0,
          lastGamepadInput: "",
          lastGamepadInputAt: 0,
          lastSteamGamepadInput: "",
          lastSteamGamepadInputAt: 0,
          pressedGamepadButtons: new Set(),
          remoteOverlayActive: false,
          catchAllInstalled: false,
          previousCatchAllGamepadInput: null,
          catchAllButtonState: {},
          catchAllReleaseTimer: 0,
          catchAllSuppressUntil: 0,
          ignoreOverlayInputUntil: 0,
          lastOverlayOpenValue: false,
          storeChannel: null,
          storeChannelHandler: null,
          lastStoreInputNonce: "",
          lastApiInputNonce: 0,
          inputPollTimer: 0,
          apiInputPollTimer: 0,
          inputStorageHandler: null,
          overlayAnnounceTimer: 0,
          keyHandler: null,
          keyUpHandler: null,
          keyPressHandler: null,
        });

  const storeSections = [
    ["discover", "Discover", "All plugins in one full-screen view."],
    ["built-in", "Built-In", "Core TFS plugins. They stay installed and can only be hidden."],
    ["community", "Community", "Downloadable plugins from your future registry."],
    ["installed", "Installed", "Everything already present on this machine."],
    ["updates", "Updates", "Entries that currently publish a newer version."],
  ];
  const storePageSize = 6;
  const storePermissionDescriptions = {
    frontend: "Renders a controller-friendly screen inside Tools for Steam.",
    storage: "Stores public JSON settings for this plugin.",
    secrets: "Stores write-only credentials protected by Windows.",
    network: "Sends HTTP or HTTPS requests through the TFS network proxy.",
    files: "Manages files inside this plugin's private sandbox.",
    notifications: "Shows non-blocking notices inside the Steam interface.",
    logging: "Writes bounded diagnostic logs for troubleshooting.",
    "native.audio": "Reads and controls Windows audio devices, volumes, mute state, and mixer sessions.",
    "native.processes": "Lists visible windows and can bring a selected window to the foreground.",
    "native.display": "Reads and changes supported Windows display, resolution, and refresh-rate modes.",
    "native.themes": "Browses, installs, configures, and applies CSSLoader themes and profiles.",
    "native.artwork": "Searches SteamGridDB and writes selected artwork into Steam's grid folder.",
    "native.app-start": "Manages and launches applications from the curated TFS App Start catalog.",
    "native.store-sync": "Reads launcher libraries and manages TFS Steam shortcut synchronization.",
    "native.automation": "Controls reviewed TFS automation integrations such as Auto SISR.",
    "native.performance": "Reads FPS, frame-time, CPU, memory, overlay, and target-process telemetry.",
    "native.power": "Can invoke confirmed Steam, sleep, restart, and shutdown actions.",
    "native.full-trust": "Full-trust plugin: can run native backends and programs, access arbitrary files, open shell targets, and inject code into Steam surfaces.",
  };
  const searchKeyboardRows = [
    ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"],
    ["A", "S", "D", "F", "G", "H", "J", "K", "L"],
    ["Z", "X", "C", "V", "B", "N", "M", "0", "1", "2"],
    ["3", "4", "5", "6", "7", "8", "9", "-", "Space", "Back"],
    ["Clear", "Done"],
  ];

  const builtInStoreOrder = new Map([
    ["performance", 10],
    ["themes", 20],
    ["audio", 30],
    ["smart-home", 40],
    ["app-start", 50],
    ["store-sync", 60],
    ["power", 70],
    ["processes", 80],
    ["artwork", 90],
    ["hltb", 100],
    ["display", 110],
    ["auto-sisr", 120],
  ]);

  let lastStoreKeyboardRequestAt = 0;
  let lastStoreKeyboardRequestKey = "";

  function setStoreKeyboardLayer(active) {
    const root = state.root || document.getElementById("steamloader-plugin-store-root");
    root?.classList.toggle("is-keyboard-open", Boolean(active));

    if (state.storeKeyboardLayerTimer) {
      window.clearTimeout(state.storeKeyboardLayerTimer);
      state.storeKeyboardLayerTimer = 0;
    }

    if (active) {
      state.storeKeyboardLayerTimer = window.setTimeout(() => {
        state.storeKeyboardLayerTimer = 0;
        const currentRoot = state.root || document.getElementById("steamloader-plugin-store-root");
        currentRoot?.classList.remove("is-keyboard-open");
      }, 60000);
    }
  }

  function markStoreKeyboardActive() {
    state.searchKeyboardActiveUntil = Date.now() + 60000;
    setStoreKeyboardLayer(true);
  }

  function tryInvokeStoreKeyboardOpener(opener, argSets) {
    for (const args of argSets) {
      try {
        opener(...args);
        return true;
      } catch {
      }
    }

    return false;
  }

  function requestStoreSteamKeyboard(element, description = "Search") {
    if (!apiBase || !(element instanceof HTMLElement)) {
      return false;
    }

    const rect = element.getBoundingClientRect();
    const payload = {
      label: description,
      value: element.value || "",
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

    if (requestKey === lastStoreKeyboardRequestKey && now - lastStoreKeyboardRequestAt < 650) {
      return true;
    }

    lastStoreKeyboardRequestKey = requestKey;
    lastStoreKeyboardRequestAt = now;

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

  function tryOpenStoreSteamKeyboard(element, description = "Search", force = false) {
    if (!(element instanceof HTMLElement)) {
      return false;
    }

    element.focus({ preventScroll: true });
    if (!force && Date.now() < (state.searchKeyboardActiveUntil || 0)) {
      setStoreKeyboardLayer(true);
      return true;
    }

    markStoreKeyboardActive();

    if (requestStoreSteamKeyboard(element, description)) {
      return true;
    }

    const label = description;
    const currentValue = element.value || "";
    const rect = element.getBoundingClientRect();
    let opened = false;

    try {
      if (typeof window.navigator?.virtualKeyboard?.show === "function") {
        window.navigator.virtualKeyboard.show();
        opened = true;
      }
    } catch {
    }

    const steamInput = window.SteamClient?.Input;
    if (typeof steamInput?.ShowFloatingGamepadTextInput === "function") {
      opened =
        tryInvokeStoreKeyboardOpener(steamInput.ShowFloatingGamepadTextInput.bind(steamInput), [
          [0, Math.round(rect.left), Math.round(rect.top), Math.round(rect.width), Math.round(rect.height)],
          [0, Math.round(rect.left), Math.round(rect.top), Math.round(rect.right), Math.round(rect.bottom)],
        ]) || opened;
    }

    if (typeof steamInput?.ShowGamepadTextInput === "function") {
      opened =
        tryInvokeStoreKeyboardOpener(steamInput.ShowGamepadTextInput.bind(steamInput), [
          [0, 0, label, 256, currentValue],
          [0, 0, label, 1024, currentValue],
        ]) || opened;
    }

    return opened;
  }

  function canHostPluginStoreOverlay() {
    return Boolean(
      document.body &&
      !document.getElementById("QuickAccess-NA") &&
      window.innerWidth >= 900 &&
      window.innerHeight >= 500,
    );
  }

  function ensureStyleElement() {
    let style = document.getElementById("steamloader-plugin-store-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "steamloader-plugin-store-style";
      document.head.append(style);
    }

    style.textContent = `
      .steamloader-plugin-store-root {
        position: fixed;
        inset: 0;
        z-index: 2147483640;
        display: none;
      }

      .steamloader-plugin-store-root.is-open {
        display: block;
      }

      .steamloader-plugin-store-root.is-keyboard-open {
        z-index: 10;
      }

      .steamloader-plugin-store-surface {
        --store-bg: #0e141b;
        --store-panel: rgba(22, 32, 43, 0.92);
        --store-panel-strong: rgba(29, 47, 64, 0.96);
        --store-line: rgba(102, 192, 244, 0.18);
        --store-blue: #66c0f4;
        --store-blue-strong: #8ecdf8;
        --store-text: #f3f7fb;
        --store-muted: rgba(199, 213, 224, 0.74);
        position: absolute;
        inset: 0;
        display: grid;
        grid-template-rows: auto auto auto minmax(0, 1fr);
        color: var(--store-text);
        overflow: hidden;
        background:
          radial-gradient(circle at 12% 0%, rgba(102, 192, 244, 0.18), transparent 28%),
          radial-gradient(circle at 92% 10%, rgba(42, 71, 94, 0.5), transparent 34%),
          linear-gradient(180deg, #0e141b, #101923 44%, #0b1118);
        font-family: "Motiva Sans", "Segoe UI", sans-serif;
      }

      .steamloader-plugin-store-surface::before {
        content: "";
        position: absolute;
        inset: 0;
        background:
          linear-gradient(90deg, rgba(102, 192, 244, 0.045), transparent 18%, transparent 82%, rgba(102, 192, 244, 0.025)),
          repeating-linear-gradient(90deg, rgba(255, 255, 255, 0.014) 0 1px, transparent 1px 96px);
        pointer-events: none;
      }

      .steamloader-plugin-store-topbar,
      .steamloader-plugin-store-tabs-row,
      .steamloader-plugin-store-status-row,
      .steamloader-plugin-store-content {
        position: relative;
        z-index: 1;
      }

      .steamloader-plugin-store-topbar {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 18px;
        align-items: start;
        padding: clamp(18px, 2vw, 34px) clamp(28px, 3.4vw, 58px) 10px;
      }

      .steamloader-plugin-store-brand {
        display: flex;
        flex-direction: column;
        gap: 8px;
        min-width: 0;
      }

      .steamloader-plugin-store-kicker {
        color: var(--store-blue);
        font-size: clamp(11px, 1vw, 15px);
        font-weight: 900;
        letter-spacing: 0.22em;
        text-transform: uppercase;
      }

      .steamloader-plugin-store-title {
        margin: 0;
        color: #f8ffff;
        font-size: clamp(38px, 4.4vw, 76px);
        line-height: 0.92;
        font-weight: 950;
        letter-spacing: -0.07em;
        text-shadow: 0 18px 44px rgba(102, 192, 244, 0.12);
      }

      .steamloader-plugin-store-subtitle {
        max-width: min(860px, 62vw);
        color: var(--store-muted);
        font-size: clamp(14px, 1.35vw, 20px);
        line-height: 1.36;
      }

      .steamloader-plugin-store-topbar-actions {
        display: inline-flex;
        align-items: center;
        gap: 12px;
        justify-content: flex-end;
      }

      .steamloader-plugin-store-chip {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-height: 38px;
        padding: 0 14px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.06);
        border: 1px solid rgba(255, 255, 255, 0.08);
        color: rgba(232, 238, 244, 0.94);
        font-size: 12px;
        font-weight: 900;
        letter-spacing: 0.04em;
        white-space: nowrap;
      }

      .steamloader-plugin-store-chip.is-accent {
        color: var(--store-blue-strong);
        background: rgba(102, 192, 244, 0.12);
        border-color: rgba(102, 192, 244, 0.24);
      }

      .steamloader-plugin-store-button {
        appearance: none;
        border: 0;
        min-height: 42px;
        padding: 0 16px;
        border-radius: 11px;
        background: rgba(255, 255, 255, 0.12);
        color: #f6ffff;
        font: inherit;
        font-size: 14px;
        font-weight: 900;
        letter-spacing: 0.02em;
        cursor: pointer;
        transition: transform 120ms ease, background 120ms ease, opacity 120ms ease, box-shadow 120ms ease;
      }

      .steamloader-plugin-store-button:hover,
      .steamloader-plugin-store-button:focus-visible,
      .steamloader-plugin-store-button.is-controller-focus {
        background: rgba(102, 192, 244, 0.18);
        box-shadow:
          inset 0 0 0 2px rgba(102, 192, 244, 0.8),
          0 0 0 3px rgba(102, 192, 244, 0.16),
          0 14px 30px rgba(0, 0, 0, 0.28);
        transform: translateY(-1px);
        outline: none;
      }

      .steamloader-plugin-store-button.is-primary {
        background: linear-gradient(135deg, #66c0f4, #417a9b);
        color: #07131d;
      }

      .steamloader-plugin-store-button.is-danger {
        background: rgba(102, 192, 244, 0.1);
        color: rgba(199, 213, 224, 0.86);
      }

      .steamloader-plugin-store-button:disabled,
      .steamloader-plugin-store-button.is-disabled {
        opacity: 0.48;
        cursor: default;
        transform: none;
      }

      .steamloader-plugin-store-tabs-row {
        display: grid;
        grid-template-columns: auto minmax(0, auto) auto;
        align-items: center;
        justify-content: center;
        gap: clamp(12px, 2vw, 30px);
        padding: 2px clamp(28px, 3.4vw, 58px) 18px;
      }

      .steamloader-plugin-store-bumper {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 54px;
        height: 44px;
        border-radius: 9px;
        background: linear-gradient(180deg, #66c0f4, #417a9b);
        color: #07131d;
        font-size: 17px;
        font-weight: 950;
        letter-spacing: -0.03em;
        box-shadow: 0 0 28px rgba(102, 192, 244, 0.22);
      }

      .steamloader-plugin-store-nav {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 10px;
        min-width: 0;
      }

      .steamloader-plugin-store-nav-button {
        appearance: none;
        border: 0;
        min-height: 48px;
        padding: 0 18px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.08);
        color: rgba(232, 246, 246, 0.82);
        text-align: center;
        font: inherit;
        cursor: pointer;
        white-space: nowrap;
        transition: background 120ms ease, transform 120ms ease, box-shadow 120ms ease;
      }

      .steamloader-plugin-store-nav-button.is-active {
        background: rgba(255, 255, 255, 0.16);
        color: var(--store-blue-strong);
        box-shadow:
          inset 0 0 0 1px rgba(102, 192, 244, 0.28),
          0 0 26px rgba(102, 192, 244, 0.12);
      }

      .steamloader-plugin-store-nav-button:focus-visible,
      .steamloader-plugin-store-nav-button.is-controller-focus {
        outline: none;
        transform: translateY(-1px);
        box-shadow:
          inset 0 0 0 2px rgba(102, 192, 244, 0.8),
          0 0 0 3px rgba(102, 192, 244, 0.16);
      }

      .steamloader-plugin-store-tab-title {
        display: inline-flex;
        align-items: baseline;
        gap: 9px;
        font-size: 15px;
        font-weight: 950;
        letter-spacing: 0.05em;
        text-transform: uppercase;
      }

      .steamloader-plugin-store-tab-count {
        color: rgba(102, 192, 244, 0.76);
        font-size: 13px;
      }

      .steamloader-plugin-store-nav-copy,
      .steamloader-plugin-store-rail,
      .steamloader-plugin-store-rail-copy,
      .steamloader-plugin-store-preview,
      .steamloader-plugin-store-detail,
      .steamloader-plugin-store-metrics,
      .steamloader-plugin-store-metric {
        display: none;
      }

      .steamloader-plugin-store-status-row {
        display: flex;
        flex-wrap: nowrap;
        gap: 10px;
        overflow: hidden;
        padding: 0 clamp(28px, 3.4vw, 58px) 16px;
      }

      .steamloader-plugin-store-status,
      .steamloader-plugin-store-error {
        display: inline-flex;
        align-items: center;
        min-height: 34px;
        max-width: 100%;
        padding: 0 13px;
        border-radius: 10px;
        font-size: 12px;
        line-height: 1.25;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .steamloader-plugin-store-status {
        background: rgba(255, 255, 255, 0.055);
        color: rgba(205, 218, 219, 0.82);
      }

      .steamloader-plugin-store-status.is-developer-catalog {
        background: rgba(148, 96, 35, 0.5);
        color: #ffe0a6;
      }

      .steamloader-plugin-store-error {
        background: rgba(148, 69, 35, 0.62);
        color: #ffd7b0;
      }

      .steamloader-plugin-store-content {
        min-height: 0;
        display: block;
        padding: 0 clamp(28px, 3.4vw, 58px) clamp(22px, 3vw, 42px);
        overflow: hidden;
      }

      .steamloader-plugin-store-browser {
        min-height: 0;
        height: 100%;
        display: block;
      }

      .steamloader-plugin-store-section-heading {
        display: flex;
        align-items: flex-end;
        justify-content: space-between;
        gap: 16px;
        margin: 0 0 14px;
      }

      .steamloader-plugin-store-section-title {
        color: var(--store-blue-strong);
        font-size: clamp(18px, 1.8vw, 30px);
        font-weight: 950;
        letter-spacing: -0.04em;
      }

      .steamloader-plugin-store-section-copy {
        color: rgba(205, 218, 219, 0.66);
        font-size: 13px;
        line-height: 1.35;
        text-align: right;
      }

      .steamloader-plugin-store-gallery {
        height: calc(100% - 48px);
        min-height: 0;
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(clamp(248px, 22vw, 360px), 1fr));
        gap: clamp(12px, 1.4vw, 20px);
        overflow: auto;
        padding: 2px 8px 18px 2px;
        align-content: start;
        scrollbar-color: rgba(102, 192, 244, 0.42) rgba(255, 255, 255, 0.05);
      }

      .steamloader-plugin-store-card {
        appearance: none;
        border: 0;
        position: relative;
        display: flex;
        flex-direction: column;
        gap: 10px;
        min-height: clamp(236px, 27vh, 330px);
        padding: 12px;
        border-radius: 17px;
        background:
          linear-gradient(180deg, rgba(27, 40, 56, 0.94), rgba(13, 20, 28, 0.98)),
          radial-gradient(circle at top right, rgba(102, 192, 244, 0.12), transparent 34%);
        box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.07);
        color: inherit;
        text-align: left;
        cursor: pointer;
        transition: transform 130ms ease, box-shadow 130ms ease, background 130ms ease;
      }

      .steamloader-plugin-store-card:hover,
      .steamloader-plugin-store-card:focus-visible,
      .steamloader-plugin-store-card.is-controller-focus {
        transform: translateY(-2px);
        box-shadow:
          inset 0 0 0 2px rgba(102, 192, 244, 0.8),
          0 18px 34px rgba(0, 0, 0, 0.28),
          0 0 0 3px rgba(102, 192, 244, 0.16);
        outline: none;
      }

      .steamloader-plugin-store-card.is-selected {
        background:
          linear-gradient(180deg, rgba(42, 71, 94, 0.98), rgba(16, 28, 40, 0.98)),
          radial-gradient(circle at top right, rgba(102, 192, 244, 0.18), transparent 34%);
        box-shadow:
          inset 0 0 0 1px rgba(102, 192, 244, 0.36),
          0 18px 34px rgba(0, 0, 0, 0.22);
      }

      .steamloader-plugin-store-card-preview {
        position: relative;
        height: clamp(82px, 11vh, 126px);
        min-height: 0;
        overflow: hidden;
        border-radius: 12px;
        background:
          radial-gradient(circle at top right, rgba(102, 192, 244, 0.2), transparent 32%),
          radial-gradient(circle at bottom left, rgba(42, 71, 94, 0.46), transparent 34%),
          linear-gradient(160deg, rgba(24, 37, 50, 0.98), rgba(9, 15, 22, 0.98));
        box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.055);
        flex: 0 0 auto;
      }

      .steamloader-plugin-store-card-preview img {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: cover;
      }

      .steamloader-plugin-store-card-placeholder {
        position: absolute;
        inset: 0;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: 4px;
        padding: 10px;
        text-align: center;
      }

      .steamloader-plugin-store-card-placeholder-title {
        color: rgba(248, 255, 255, 0.92);
        font-size: 10px;
        font-weight: 950;
        letter-spacing: 0.14em;
        text-transform: uppercase;
      }

      .steamloader-plugin-store-card-placeholder-copy {
        color: rgba(205, 218, 219, 0.72);
        font-size: 11px;
        line-height: 1.28;
      }

      .steamloader-plugin-store-card-main {
        display: flex;
        flex-direction: column;
        gap: 6px;
        min-height: 0;
      }

      .steamloader-plugin-store-card-title {
        color: #f8ffff;
        font-size: clamp(17px, 1.35vw, 24px);
        line-height: 1.05;
        font-weight: 950;
        letter-spacing: -0.045em;
      }

      .steamloader-plugin-store-card-author {
        color: var(--store-blue-strong);
        font-size: 13px;
        line-height: 1.2;
        font-weight: 800;
      }

      .steamloader-plugin-store-card-description {
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
        color: rgba(205, 218, 219, 0.72);
        font-size: 12px;
        line-height: 1.35;
      }

      .steamloader-plugin-store-badges {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-top: auto;
      }

      .steamloader-plugin-store-badge {
        display: inline-flex;
        align-items: center;
        min-height: 23px;
        padding: 0 8px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.08);
        color: rgba(236, 248, 248, 0.9);
        font-size: 10px;
        font-weight: 950;
        letter-spacing: 0.08em;
        text-transform: uppercase;
      }

      .steamloader-plugin-store-badge.is-built-in {
        background: rgba(102, 192, 244, 0.16);
        color: var(--store-blue-strong);
      }

      .steamloader-plugin-store-badge.is-update {
        background: rgba(102, 192, 244, 0.12);
        color: rgba(199, 213, 224, 0.92);
      }

      .steamloader-plugin-store-card-footer {
        display: flex;
        justify-content: flex-start;
        gap: 10px;
        align-items: center;
        margin-top: 4px;
      }

      .steamloader-plugin-store-card-status {
        min-width: 0;
        color: rgba(205, 218, 219, 0.62);
        font-size: 11px;
        line-height: 1.28;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .steamloader-plugin-store-empty {
        grid-column: 1 / -1;
        display: flex;
        align-items: center;
        justify-content: center;
        min-height: 220px;
        padding: 24px;
        border-radius: 18px;
        background: rgba(255, 255, 255, 0.045);
        box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.08);
        color: rgba(205, 218, 219, 0.78);
        text-align: center;
        font-size: 15px;
        line-height: 1.5;
      }

      @media (max-width: 1320px) {
        .steamloader-plugin-store-topbar {
          padding-top: 18px;
        }

        .steamloader-plugin-store-title {
          font-size: clamp(34px, 4vw, 54px);
        }

        .steamloader-plugin-store-subtitle {
          max-width: 720px;
          font-size: 14px;
        }

        .steamloader-plugin-store-topbar-actions .steamloader-plugin-store-chip {
          display: none;
        }

        .steamloader-plugin-store-nav {
          gap: 7px;
        }

        .steamloader-plugin-store-nav-button {
          min-height: 42px;
          padding: 0 13px;
        }

        .steamloader-plugin-store-tab-title {
          font-size: 12px;
        }

        .steamloader-plugin-store-gallery {
          grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));
        }
      }

      @media (max-width: 980px) {
        .steamloader-plugin-store-surface {
          grid-template-rows: auto auto auto minmax(0, 1fr);
        }

        .steamloader-plugin-store-topbar {
          grid-template-columns: 1fr;
          gap: 12px;
        }

        .steamloader-plugin-store-subtitle,
        .steamloader-plugin-store-status-row {
          display: none;
        }

        .steamloader-plugin-store-tabs-row {
          grid-template-columns: minmax(0, 1fr);
        }

        .steamloader-plugin-store-bumper {
          display: none;
        }

        .steamloader-plugin-store-nav {
          justify-content: flex-start;
          overflow: auto;
          padding-bottom: 3px;
        }

        .steamloader-plugin-store-content {
          padding-top: 2px;
        }

        .steamloader-plugin-store-gallery {
          height: calc(100% - 40px);
          grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
        }
      }

      .steamloader-plugin-store-surface {
        --store-bg: #0b1118;
        --store-main: #101720;
        --store-card: #343a42;
        --store-card-focus: #48515d;
        --store-icon: #3d444e;
        --store-text: #eef3f8;
        --store-muted: rgba(190, 201, 213, 0.82);
        --store-blue: #66c0f4;
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: minmax(0, 1fr);
        background: linear-gradient(180deg, #0b1118 0%, #0e151e 100%);
        color: var(--store-text);
      }

      .steamloader-plugin-store-surface::before {
        display: none;
      }

      .steamloader-plugin-store-main {
        position: relative;
        grid-column: 1;
        min-width: 0;
        min-height: 0;
        display: grid;
        grid-template-rows: auto auto auto minmax(0, 1fr) auto;
        padding: clamp(28px, 4.6vh, 52px) clamp(24px, 4vw, 72px) 18px;
        background: #101720;
      }

      .steamloader-plugin-store-topbar {
        display: grid;
        grid-template-columns: minmax(0, 1fr) minmax(260px, 460px) auto;
        gap: 22px;
        align-items: center;
        padding: 0 0 18px;
      }

      .steamloader-plugin-store-title {
        font-size: clamp(34px, 3vw, 48px);
        line-height: 1.02;
        letter-spacing: -0.055em;
        text-shadow: none;
      }

      .steamloader-plugin-store-kicker,
      .steamloader-plugin-store-subtitle,
      .steamloader-plugin-store-chip,
      .steamloader-plugin-store-section-copy {
        display: none;
      }

      .steamloader-plugin-store-topbar-actions {
        display: flex;
        gap: 12px;
      }

      .steamloader-plugin-store-search {
        min-width: 0;
      }

      .steamloader-plugin-store-search-input {
        appearance: none;
        width: 100%;
        min-height: 54px;
        border: 0;
        border-radius: 22px;
        padding: 0 18px;
        background: #252c35;
        color: #eef3f8;
        font: inherit;
        font-size: 15px;
        font-weight: 850;
        outline: none;
        box-shadow: inset 0 0 0 2px rgba(238, 243, 248, 0.04);
      }

      .steamloader-plugin-store-search-input::placeholder {
        color: rgba(190, 201, 213, 0.62);
      }

      .steamloader-plugin-store-search-input:focus-visible,
      .steamloader-plugin-store-search-input.is-controller-focus {
        background: #343a42;
        box-shadow:
          inset 0 0 0 3px rgba(238, 243, 248, 0.2),
          0 0 0 4px rgba(238, 243, 248, 0.08);
      }

      .steamloader-plugin-store-button {
        min-height: 54px;
        padding: 0 18px;
        border-radius: 22px;
        background: #343a42;
        color: #eef3f8;
        font-size: 15px;
        font-weight: 900;
      }

      .steamloader-plugin-store-button:hover,
      .steamloader-plugin-store-button:focus-visible,
      .steamloader-plugin-store-button.is-controller-focus {
        background: #48515d;
        box-shadow: inset 0 0 0 3px rgba(238, 243, 248, 0.18);
        transform: none;
      }

      .steamloader-plugin-store-button.is-primary {
        background: rgba(238, 243, 248, 0.9);
        color: #17212c;
      }

      .steamloader-plugin-store-button.is-danger {
        background: rgba(255, 255, 255, 0.1);
        color: rgba(238, 243, 248, 0.78);
      }

      .steamloader-plugin-store-tabs-row {
        display: grid;
        grid-template-columns: auto minmax(0, max-content) auto;
        align-items: center;
        justify-content: center;
        gap: 12px;
        padding: 0 0 16px;
      }

      .steamloader-plugin-store-bumper {
        min-width: 64px;
        height: 38px;
        border-radius: 999px;
        background: #eef3f8;
        color: #17212c;
        font-size: 15px;
        box-shadow: none;
      }

      .steamloader-plugin-store-nav {
        justify-content: center;
        gap: 8px;
        overflow: auto;
        padding: 2px;
      }

      .steamloader-plugin-store-nav-button {
        min-height: 42px;
        padding: 0 15px;
        border-radius: 16px;
        background: #252c35;
        color: rgba(238, 243, 248, 0.78);
      }

      .steamloader-plugin-store-nav-button.is-active,
      .steamloader-plugin-store-nav-button:focus-visible,
      .steamloader-plugin-store-nav-button.is-controller-focus {
        background: #343a42;
        color: #eef3f8;
        box-shadow: inset 0 0 0 2px rgba(238, 243, 248, 0.16);
        transform: none;
      }

      .steamloader-plugin-store-tab-title {
        font-size: 13px;
      }

      .steamloader-plugin-store-tab-count {
        color: rgba(190, 201, 213, 0.8);
      }

      .steamloader-plugin-store-status-row {
        display: flex;
        align-items: center;
        gap: 10px;
        min-height: 34px;
        padding: 0 0 14px;
        overflow: hidden;
      }

      .steamloader-plugin-store-status,
      .steamloader-plugin-store-error {
        min-height: 32px;
        border-radius: 12px;
        padding: 0 12px;
        display: inline-flex;
        align-items: center;
        max-width: 100%;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        background: rgba(255, 255, 255, 0.055);
        color: rgba(190, 201, 213, 0.76);
        font-size: 12px;
        font-weight: 800;
      }

      .steamloader-plugin-store-error {
        background: rgba(148, 69, 35, 0.62);
        color: #ffd7b0;
      }

      .steamloader-plugin-store-content {
        min-height: 0;
        min-width: 0;
        padding: 0;
        overflow: hidden;
      }

      .steamloader-plugin-store-browser {
        min-height: 0;
        height: 100%;
        display: grid;
        grid-template-rows: auto minmax(0, 1fr);
      }

      .steamloader-plugin-store-section-heading {
        margin: 0 0 14px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 16px;
      }

      .steamloader-plugin-store-section-title {
        color: rgba(238, 243, 248, 0.9);
        font-size: 18px;
        letter-spacing: 0;
      }

      .steamloader-plugin-store-page-pill {
        min-height: 30px;
        padding: 0 12px;
        border-radius: 999px;
        display: inline-flex;
        align-items: center;
        background: #252c35;
        color: rgba(238, 243, 248, 0.72);
        font-size: 11px;
        font-weight: 900;
        text-transform: uppercase;
        letter-spacing: 0.08em;
      }

      .steamloader-plugin-store-gallery {
        box-sizing: border-box;
        height: 100%;
        min-width: 0;
        min-height: 0;
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 480px));
        grid-template-rows: none;
        grid-auto-rows: clamp(360px, 39vh, 410px);
        grid-auto-flow: row;
        column-gap: clamp(22px, 2vw, 34px);
        row-gap: clamp(28px, 3vh, 42px);
        overflow-x: hidden;
        overflow-y: auto;
        padding: 8px clamp(20px, 1.8vw, 32px) 48px 8px;
        scroll-padding: 8px clamp(20px, 1.8vw, 32px) 48px 8px;
        align-content: start;
        justify-content: start;
        scrollbar-gutter: stable;
        scrollbar-color: rgba(238, 243, 248, 0.46) rgba(255, 255, 255, 0.04);
        scrollbar-width: auto;
      }

      .steamloader-plugin-store-card {
        box-sizing: border-box;
        position: relative;
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: clamp(150px, 18vh, 190px) minmax(0, 1fr);
        align-items: stretch;
        gap: clamp(12px, 1vw, 16px);
        align-self: stretch;
        height: auto;
        min-height: 0;
        padding: clamp(14px, 1.2vw, 18px);
        border-radius: 22px;
        background:
          linear-gradient(145deg, rgba(255, 255, 255, 0.035), transparent 42%),
          repeating-linear-gradient(135deg, rgba(255, 255, 255, 0.012) 0 1px, transparent 1px 18px),
          #343a42;
        box-shadow:
          inset 0 0 0 1px rgba(238, 243, 248, 0.1),
          0 12px 30px rgba(0, 0, 0, 0.16);
        overflow: hidden;
        scroll-margin-block: 12px 28px;
      }

      .steamloader-plugin-store-card::after {
        content: "";
        position: absolute;
        inset: 0;
        border: 3px solid transparent;
        border-radius: inherit;
        pointer-events: none;
        transition: border-color 120ms ease, box-shadow 120ms ease;
      }

      .steamloader-plugin-store-card:hover,
      .steamloader-plugin-store-card:focus-visible,
      .steamloader-plugin-store-card.is-controller-focus {
        background: #48515d;
        box-shadow: inset 0 0 0 2px rgba(238, 243, 248, 0.12);
        transform: none;
      }

      .steamloader-plugin-store-card:hover::after,
      .steamloader-plugin-store-card:focus-visible::after,
      .steamloader-plugin-store-card.is-controller-focus::after {
        border-color: rgba(238, 243, 248, 0.96);
        box-shadow: inset 0 0 0 2px rgba(255, 255, 255, 0.12);
      }

      .steamloader-plugin-store-card.is-selected {
        background: #3d454f;
        box-shadow: inset 0 0 0 2px rgba(238, 243, 248, 0.14);
      }

      .steamloader-plugin-store-card.is-selected::after {
        border-color: rgba(238, 243, 248, 0.34);
      }

      .steamloader-plugin-store-card.is-context-open {
        background: #4a535f;
        box-shadow:
          inset 0 0 0 2px rgba(238, 243, 248, 0.18),
          inset 0 -18px 42px rgba(238, 243, 248, 0.035);
      }

      .steamloader-plugin-store-card.is-context-open::after {
        border-color: rgba(238, 243, 248, 0.82);
      }

      .steamloader-plugin-store-card-main {
        grid-row: 2;
        display: flex;
        flex-direction: column;
        gap: 6px;
        min-height: 0;
        overflow: hidden;
      }

      .steamloader-plugin-store-card-title {
        font-size: clamp(17px, 1.2vw, 22px);
        line-height: 1.05;
        letter-spacing: -0.055em;
      }

      .steamloader-plugin-store-card-author {
        color: rgba(190, 201, 213, 0.72);
        font-size: 12px;
      }

      .steamloader-plugin-store-card-description {
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
        color: rgba(190, 201, 213, 0.64);
        font-size: 12px;
        line-height: 1.4;
      }

      .steamloader-plugin-store-badges {
        display: flex;
        flex-wrap: wrap;
        gap: 5px;
        margin-top: 2px;
      }

      .steamloader-plugin-store-badge {
        min-height: 19px;
        padding: 0 7px;
        background: rgba(255, 255, 255, 0.08);
        color: rgba(238, 243, 248, 0.76);
        font-size: 8px;
      }

      .steamloader-plugin-store-badge.is-built-in,
      .steamloader-plugin-store-badge.is-update {
        color: rgba(238, 243, 248, 0.88);
        background: rgba(255, 255, 255, 0.1);
      }

      .steamloader-plugin-store-card-footer {
        display: flex;
        align-items: center;
        justify-content: flex-start;
        gap: 8px;
        flex-wrap: wrap;
        margin-top: auto;
        min-height: 0;
      }

      .steamloader-plugin-store-card-status {
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: normal;
      }

      .steamloader-plugin-store-card-preview {
        grid-row: 1;
        position: relative;
        width: 100%;
        height: 100%;
        min-height: 0;
        aspect-ratio: auto;
        border-radius: 16px;
        background: #1b222c;
        box-shadow:
          inset 0 0 0 1px rgba(238, 243, 248, 0.12),
          0 8px 20px rgba(0, 0, 0, 0.16);
        overflow: hidden;
        isolation: isolate;
        clip-path: inset(0 round 16px);
      }

      .steamloader-plugin-store-card-preview img {
        object-fit: contain;
        object-position: center center;
        background: #1b222c;
        border-radius: 16px;
        clip-path: inset(0 round 16px);
      }

      .steamloader-plugin-store-card-placeholder {
        gap: 2px;
      }

      .steamloader-plugin-store-card-placeholder-title {
        color: rgba(238, 243, 248, 0.78);
        letter-spacing: 0.04em;
      }

      .steamloader-plugin-store-card-placeholder-copy {
        color: rgba(190, 201, 213, 0.62);
      }

      .steamloader-plugin-store-context-scrim {
        position: absolute;
        inset: 0;
        z-index: 7;
        background:
          radial-gradient(circle at center, rgba(238, 243, 248, 0.035), transparent 46%),
          rgba(0, 0, 0, 0.2);
      }

      .steamloader-plugin-store-context-menu {
        position: absolute;
        z-index: 8;
        top: 50%;
        left: 50%;
        width: min(920px, calc(100vw - 72px));
        max-height: calc(100vh - 124px);
        transform: translate(-50%, -50%);
        padding: 18px;
        border-radius: 24px;
        display: grid;
        grid-template-columns: minmax(260px, 0.9fr) minmax(340px, 1.1fr);
        grid-template-rows: auto minmax(0, 1fr);
        gap: 18px;
        overflow: hidden;
        background: rgba(37, 44, 53, 0.98);
        box-shadow:
          0 28px 90px rgba(0, 0, 0, 0.48),
          inset 0 0 0 1px rgba(238, 243, 248, 0.1);
        backdrop-filter: blur(18px);
      }

      .steamloader-plugin-store-context-header {
        grid-column: 1 / -1;
        padding: 8px 10px 14px;
        border-bottom: 1px solid rgba(238, 243, 248, 0.08);
      }

      .steamloader-plugin-store-context-overview,
      .steamloader-plugin-store-context-panel {
        min-width: 0;
        min-height: 0;
      }

      .steamloader-plugin-store-context-overview {
        display: flex;
        flex-direction: column;
        gap: 12px;
        overflow: auto;
        padding: 0 4px 4px 0;
      }

      .steamloader-plugin-store-context-preview {
        position: relative;
        width: 100%;
        min-height: 170px;
        aspect-ratio: 16 / 9;
        overflow: hidden;
        border-radius: 18px;
        background: #101720;
      }

      .steamloader-plugin-store-context-preview img {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: contain;
      }

      .steamloader-plugin-store-context-description,
      .steamloader-plugin-store-context-status {
        color: rgba(190, 201, 213, 0.78);
        font-size: 13px;
        font-weight: 750;
        line-height: 1.45;
      }

      .steamloader-plugin-store-context-status {
        padding: 10px 12px;
        border-radius: 14px;
        background: rgba(255, 255, 255, 0.055);
      }

      .steamloader-plugin-store-context-panel {
        display: flex;
        flex-direction: column;
        gap: 12px;
        overflow: auto;
        padding: 0 4px 4px 0;
      }

      .steamloader-plugin-store-context-facts {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 8px;
      }

      .steamloader-plugin-store-context-fact {
        min-width: 0;
        padding: 10px 12px;
        border-radius: 14px;
        background: rgba(255, 255, 255, 0.055);
      }

      .steamloader-plugin-store-context-fact-label,
      .steamloader-plugin-store-context-permission-title {
        color: rgba(190, 201, 213, 0.62);
        font-size: 9px;
        font-weight: 950;
        letter-spacing: 0.1em;
        text-transform: uppercase;
      }

      .steamloader-plugin-store-context-fact-value {
        margin-top: 4px;
        color: #eef3f8;
        font-size: 13px;
        font-weight: 900;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }

      .steamloader-plugin-store-context-permissions {
        display: flex;
        flex-direction: column;
        gap: 7px;
      }

      .steamloader-plugin-store-context-permission {
        padding: 9px 11px;
        border-radius: 13px;
        background: rgba(255, 255, 255, 0.045);
      }

      .steamloader-plugin-store-context-permission-copy {
        margin-top: 3px;
        color: rgba(190, 201, 213, 0.7);
        font-size: 10px;
        font-weight: 750;
        line-height: 1.35;
      }

      .steamloader-plugin-store-context-permission.is-new {
        background: rgba(245, 196, 81, 0.1);
        box-shadow: inset 0 0 0 1px rgba(245, 196, 81, 0.18);
      }

      .steamloader-plugin-store-context-permission-new {
        margin-left: 7px;
        color: #f5c451;
        font-size: 8px;
        font-weight: 950;
        letter-spacing: 0.08em;
      }

      .steamloader-plugin-store-context-changelog {
        padding: 10px 12px;
        border-radius: 14px;
        background: rgba(102, 192, 244, 0.06);
      }

      .steamloader-plugin-store-context-changelog-copy {
        margin-top: 5px;
        color: rgba(210, 224, 236, 0.75);
        font-size: 11px;
        font-weight: 750;
        line-height: 1.4;
        white-space: pre-wrap;
      }

      .steamloader-plugin-store-context-kicker {
        margin-bottom: 5px;
        color: rgba(190, 201, 213, 0.72);
        font-size: 10px;
        font-weight: 950;
        text-transform: uppercase;
        letter-spacing: 0.1em;
      }

      .steamloader-plugin-store-context-title {
        color: #eef3f8;
        font-size: 22px;
        font-weight: 950;
        line-height: 1.05;
        letter-spacing: -0.045em;
      }

      .steamloader-plugin-store-context-list {
        display: flex;
        flex-direction: column;
        gap: 6px;
        padding-top: 4px;
      }

      .steamloader-plugin-store-context-action {
        width: 100%;
        min-height: 60px;
        border: 0;
        border-radius: 16px;
        padding: 10px 12px;
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 12px;
        align-items: center;
        text-align: left;
        background: transparent;
        color: rgba(238, 243, 248, 0.88);
        font: inherit;
        outline: none;
      }

      .steamloader-plugin-store-context-action:disabled,
      .steamloader-plugin-store-context-action.is-disabled {
        opacity: 0.48;
      }

      .steamloader-plugin-store-context-action:not(:disabled):hover,
      .steamloader-plugin-store-context-action:not(:disabled):focus-visible,
      .steamloader-plugin-store-context-action:not(:disabled).is-controller-focus {
        background: #eef3f8;
        color: #17212c;
        box-shadow: 0 0 0 4px rgba(238, 243, 248, 0.12);
      }

      .steamloader-plugin-store-context-action.is-danger:not(:disabled):hover,
      .steamloader-plugin-store-context-action.is-danger:not(:disabled):focus-visible,
      .steamloader-plugin-store-context-action.is-danger:not(:disabled).is-controller-focus {
        background: #dfe6ee;
      }

      .steamloader-plugin-store-context-action-text {
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 3px;
      }

      .steamloader-plugin-store-context-action-label {
        font-size: 17px;
        font-weight: 950;
        letter-spacing: -0.02em;
      }

      .steamloader-plugin-store-context-action-copy {
        color: rgba(190, 201, 213, 0.72);
        font-size: 11px;
        font-weight: 750;
      }

      .steamloader-plugin-store-context-action:not(:disabled):hover .steamloader-plugin-store-context-action-copy,
      .steamloader-plugin-store-context-action:not(:disabled):focus-visible .steamloader-plugin-store-context-action-copy,
      .steamloader-plugin-store-context-action:not(:disabled).is-controller-focus .steamloader-plugin-store-context-action-copy {
        color: rgba(23, 33, 44, 0.68);
      }

      .steamloader-plugin-store-context-action-icon {
        min-width: 32px;
        height: 32px;
        border-radius: 999px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: rgba(238, 243, 248, 0.1);
        color: inherit;
        font-size: 15px;
        font-weight: 950;
      }

      .steamloader-plugin-store-search-keyboard {
        position: absolute;
        left: 50%;
        bottom: 78px;
        z-index: 6;
        width: min(1040px, calc(100vw - 96px));
        transform: translateX(-50%);
        padding: 18px;
        border-radius: 28px;
        background: rgba(24, 31, 40, 0.98);
        box-shadow:
          0 28px 80px rgba(0, 0, 0, 0.45),
          inset 0 0 0 2px rgba(238, 243, 248, 0.08);
      }

      .steamloader-plugin-store-search-keyboard-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 16px;
        margin: 0 0 14px;
      }

      .steamloader-plugin-store-search-keyboard-title {
        color: rgba(238, 243, 248, 0.92);
        font-size: 18px;
        font-weight: 950;
      }

      .steamloader-plugin-store-search-keyboard-value {
        min-width: 260px;
        min-height: 42px;
        padding: 0 14px;
        border-radius: 16px;
        display: inline-flex;
        align-items: center;
        justify-content: flex-start;
        background: #101720;
        color: rgba(238, 243, 248, 0.86);
        font-size: 16px;
        font-weight: 800;
      }

      .steamloader-plugin-store-search-keyboard-grid {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }

      .steamloader-plugin-store-search-keyboard-row {
        display: flex;
        justify-content: center;
        gap: 8px;
      }

      .steamloader-plugin-store-search-key {
        min-width: 60px;
        min-height: 44px;
        border: 0;
        border-radius: 14px;
        background: #343a42;
        color: #eef3f8;
        font-size: 15px;
        font-weight: 950;
      }

      .steamloader-plugin-store-search-key.is-wide {
        min-width: 126px;
      }

      .steamloader-plugin-store-search-key:focus-visible,
      .steamloader-plugin-store-search-key.is-controller-focus {
        outline: none;
        background: #eef3f8;
        color: #17212c;
        box-shadow: 0 0 0 4px rgba(238, 243, 248, 0.14);
      }

      .steamloader-plugin-store-controller-bar {
        position: relative;
        z-index: 2;
        box-sizing: border-box;
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 34px;
        min-height: 64px;
        margin-top: 10px;
        padding: 12px 0 4px;
        border-top: 1px solid rgba(238, 243, 248, 0.08);
        background: #101720;
        box-shadow: 0 -16px 28px rgba(16, 23, 32, 0.94);
        color: rgba(238, 243, 248, 0.9);
        font-size: 18px;
        font-weight: 950;
        letter-spacing: 0.01em;
      }

      .steamloader-plugin-store-controller-hint {
        display: inline-flex;
        align-items: center;
        gap: 12px;
        text-transform: uppercase;
      }

      .steamloader-plugin-store-controller-key {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 48px;
        height: 36px;
        padding: 0 14px;
        border-radius: 999px;
        background: #eef3f8;
        color: #17212c;
        font-size: 18px;
        font-weight: 950;
        line-height: 1;
      }

      .steamloader-plugin-store-controller-label {
        color: rgba(238, 243, 248, 0.72);
      }

      @media (max-width: 1280px) {
        .steamloader-plugin-store-main {
          padding: 36px clamp(22px, 3.2vw, 42px) 16px;
        }

        .steamloader-plugin-store-topbar {
          grid-template-columns: minmax(220px, 1fr) minmax(240px, 1.15fr) auto;
          gap: 16px;
        }

        .steamloader-plugin-store-gallery {
          grid-template-columns: repeat(3, minmax(0, 1fr));
          grid-template-rows: none;
          grid-auto-rows: clamp(350px, 38vh, 390px);
          column-gap: clamp(16px, 1.8vw, 24px);
          row-gap: clamp(24px, 2.6vh, 34px);
        }

        .steamloader-plugin-store-card {
          grid-template-columns: minmax(0, 1fr);
          grid-template-rows: clamp(145px, 17vh, 175px) minmax(0, 1fr);
          min-height: 0;
          border-radius: 24px;
        }

        .steamloader-plugin-store-card-preview {
          height: 100%;
          min-height: 0;
        }

        .steamloader-plugin-store-card-title {
          font-size: 19px;
        }
      }

      @media (max-width: 1050px) {
        .steamloader-plugin-store-main {
          padding-inline: clamp(18px, 3vw, 30px);
        }

        .steamloader-plugin-store-topbar {
          grid-template-columns: minmax(0, 1fr) auto;
          grid-template-areas:
            "brand actions"
            "search search";
          gap: 12px 16px;
        }

        .steamloader-plugin-store-brand {
          grid-area: brand;
        }

        .steamloader-plugin-store-search {
          grid-area: search;
        }

        .steamloader-plugin-store-topbar-actions {
          grid-area: actions;
        }

        .steamloader-plugin-store-gallery {
          grid-template-columns: repeat(2, minmax(0, 1fr));
        }

        .steamloader-plugin-store-card {
          grid-template-rows: clamp(150px, 18vh, 180px) minmax(0, 1fr);
        }
      }

      @media (max-width: 900px) {
        .steamloader-plugin-store-main {
          padding: 28px 14px 20px;
        }

        .steamloader-plugin-store-topbar {
          grid-template-columns: 1fr;
          grid-template-areas:
            "brand"
            "search"
            "actions";
        }

        .steamloader-plugin-store-card {
          grid-template-columns: minmax(280px, 1.1fr) minmax(0, 0.9fr);
          grid-template-rows: minmax(0, 1fr);
          gap: 14px;
          min-height: 0;
        }

        .steamloader-plugin-store-card-preview {
          grid-column: 1;
          grid-row: 1;
          display: block;
          height: 100%;
        }

        .steamloader-plugin-store-card-main {
          grid-column: 2;
          grid-row: 1;
        }

        .steamloader-plugin-store-gallery {
          grid-template-columns: minmax(0, 1fr);
          grid-template-rows: none;
          grid-auto-rows: 280px;
          gap: 22px;
        }

        .steamloader-plugin-store-controller-bar {
          min-height: 46px;
          gap: 18px;
          font-size: 14px;
        }

        .steamloader-plugin-store-controller-key {
          min-width: 40px;
          height: 30px;
          font-size: 14px;
        }

        .steamloader-plugin-store-context-menu {
          width: min(620px, calc(100vw - 32px));
          max-height: calc(100vh - 88px);
          grid-template-columns: minmax(0, 1fr);
          grid-template-rows: auto auto minmax(0, 1fr);
          padding: 14px;
        }

        .steamloader-plugin-store-context-header {
          grid-column: 1;
        }

        .steamloader-plugin-store-context-overview {
          display: grid;
          grid-template-columns: minmax(150px, 0.7fr) minmax(0, 1.3fr);
          align-items: center;
        }

        .steamloader-plugin-store-context-preview {
          min-height: 110px;
        }
      }
    `;

    return style;
  }

  function ensureRoot() {
    let root = document.getElementById("steamloader-plugin-store-root");
    if (!root) {
      root = document.createElement("div");
      root.id = "steamloader-plugin-store-root";
      root.className = "steamloader-plugin-store-root";
      document.body.append(root);
    }

    return root;
  }

  function createNode(tagName, className = "", text = "") {
    const node = document.createElement(tagName);
    if (className) {
      node.className = className;
    }

    if (text) {
      node.textContent = text;
    }

    return node;
  }

  function getStoreChannel() {
    if (state.storeChannel || typeof BroadcastChannel !== "function") {
      return state.storeChannel;
    }

    try {
      state.storeChannel = new BroadcastChannel(storeChannelName);
      state.storeChannelHandler = (event) => {
        handleStoreChannelMessage(event.data);
      };
      state.storeChannel.addEventListener("message", state.storeChannelHandler);
    } catch {
      state.storeChannel = null;
    }

    return state.storeChannel;
  }

  function postStoreMessage(message) {
    const payload = {
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      ...message,
    };

    try {
      getStoreChannel()?.postMessage(payload);
    } catch {
    }

    try {
      const key = payload.type === "input" ? inputStorageKey : overlayStateStorageKey;
      localStorage.setItem(key, JSON.stringify(payload));
    } catch {
    }
  }

  function consumeStoreInput(raw) {
    if (!raw || !state.open) {
      return;
    }

    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type !== "input" || !payload.action || payload.nonce === state.lastStoreInputNonce) {
        return;
      }

      state.lastStoreInputNonce = payload.nonce;
      maybeRepeatGamepadAction(payload.action, String(payload.source || "remote"));
    } catch {
    }
  }

  function consumeStoreOverlayState(raw) {
    if (!raw) {
      return;
    }

    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type === "overlay-state") {
        const stillFresh = !payload.expiresAt || Number(payload.expiresAt) > Date.now();
        setRemoteStoreOverlayActive(Boolean(payload.active) && stillFresh);
      }
    } catch {
    }
  }

  function handleStoreChannelMessage(payload) {
    if (payload?.type === "input") {
      consumeStoreInput(payload);
    } else if (payload?.type === "overlay-state") {
      consumeStoreOverlayState(payload);
    }
  }

  function announceStoreOverlayState(active) {
    postStoreMessage({
      type: "overlay-state",
      active: Boolean(active),
      expiresAt: active ? Date.now() + 1800 : 0,
    });
  }

  function startStoreOverlayAnnouncements() {
    stopStoreOverlayAnnouncements();
    announceStoreOverlayState(true);
    state.overlayAnnounceTimer = window.setInterval(() => {
      if (state.open) {
        announceStoreOverlayState(true);
      }
    }, 700);
  }

  function stopStoreOverlayAnnouncements() {
    if (state.overlayAnnounceTimer) {
      window.clearInterval(state.overlayAnnounceTimer);
      state.overlayAnnounceTimer = 0;
    }

    announceStoreOverlayState(false);
  }

  function setupStoreInputBridge() {
    getStoreChannel();

    if (!state.inputStorageHandler) {
      state.inputStorageHandler = (event) => {
        if (event.key === inputStorageKey) {
          consumeStoreInput(event.newValue);
        } else if (event.key === overlayStateStorageKey) {
          consumeStoreOverlayState(event.newValue);
        }
      };
      window.addEventListener("storage", state.inputStorageHandler);
    }

    if (!state.inputPollTimer) {
      state.inputPollTimer = window.setInterval(() => {
        try {
          consumeStoreInput(localStorage.getItem(inputStorageKey));
          consumeStoreOverlayState(localStorage.getItem(overlayStateStorageKey));
        } catch {
        }
      }, 100);
    }
  }

  async function pollStoreApiInputQueue() {
    if (!state.open) {
      return;
    }

    try {
      const response = await fetch(`${apiBase}api/plugin-store/overlay/input?after=${state.lastApiInputNonce || 0}`, {
        cache: "no-store",
      });
      const payload = await response.json();
      if (!response.ok) {
        return;
      }

      const inputs = Array.isArray(payload?.inputs) ? payload.inputs : [];
      for (const input of inputs) {
        const nonce = Number(input?.nonce || 0);
        if (!nonce || nonce <= state.lastApiInputNonce || !input?.action) {
          continue;
        }

        state.lastApiInputNonce = Math.max(state.lastApiInputNonce, nonce);
        maybeRepeatGamepadAction(String(input.action), String(input.source || "api"));
      }

      const latestNonce = Number(payload?.latestNonce || 0);
      if (latestNonce > state.lastApiInputNonce && inputs.length === 0) {
        state.lastApiInputNonce = latestNonce;
      }
    } catch {
    }
  }

  function startStoreApiInputPolling() {
    if (state.apiInputPollTimer) {
      return;
    }

    void pollStoreApiInputQueue();
    state.apiInputPollTimer = window.setInterval(() => {
      void pollStoreApiInputQueue();
    }, 70);
  }

  function stopStoreApiInputPolling() {
    if (state.apiInputPollTimer) {
      window.clearInterval(state.apiInputPollTimer);
      state.apiInputPollTimer = 0;
    }
  }

  function requestStoreFocus(key = "") {
    if (key) {
      state.focusKey = key;
    }

    state.focusPending = true;
  }

  function getStoreRoot() {
    return state.root || document.getElementById("steamloader-plugin-store-root");
  }

  function getVisibleStoreFocusItems() {
    const root = getStoreRoot();
    if (!root) {
      return [];
    }

    return [...root.querySelectorAll("[data-steamloader-store-focusable='true']")]
      .filter((element) => element instanceof HTMLElement)
      .filter((element) => !element.disabled && element.offsetParent !== null);
  }

  function isStoreSearchInput(element) {
    return element instanceof HTMLInputElement && element.dataset.storeFocusKey === "top:search";
  }

  function applyStoreFocus(shouldFocus = true) {
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
    state.focusKey = item.dataset.storeFocusKey || state.focusKey;
    if (shouldFocus) {
      item.focus({ preventScroll: true });
    }

    if (item.closest(".steamloader-plugin-store-gallery")) {
      item.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
  }

  function syncSelectedStoreCard() {
    const root = getStoreRoot();
    if (!root) {
      return;
    }

    for (const card of root.querySelectorAll(".steamloader-plugin-store-card")) {
      if (!(card instanceof HTMLElement)) {
        continue;
      }

      card.classList.toggle("is-selected", card.dataset.storeCardId === state.selectedPluginId);
    }
  }

  function selectStorePlugin(pluginId) {
    const nextPluginId = String(pluginId || "");
    if (!nextPluginId) {
      return;
    }

    state.selectedPluginId = nextPluginId;
    state.focusKey = `card:${nextPluginId}`;
    syncSelectedStoreCard();
  }

  function decorateFocusable(element, key, onFocus = null) {
    if (!(element instanceof HTMLElement)) {
      return element;
    }

    const naturallyFocusable = /^(A|BUTTON|INPUT|SELECT|TEXTAREA)$/.test(element.tagName);
    if (!naturallyFocusable && !element.hasAttribute("tabindex")) {
      element.tabIndex = 0;
    }

    element.setAttribute("data-steamloader-store-focusable", "true");
    element.dataset.storeFocusKey = key;
    element.addEventListener("focus", () => {
      state.focusItems = getVisibleStoreFocusItems();
      const index = state.focusItems.indexOf(element);
      if (index >= 0) {
        state.focusIndex = index;
      }
      state.focusKey = key;

      if (typeof onFocus === "function") {
        onFocus(element);
      }

      if (document.contains(element)) {
        applyStoreFocus(false);
      }
    });
    element.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation?.();
        void closeOverlay();
      }
    });
    return element;
  }

  function refreshStoreFocus(preferredKey = "") {
    const items = getVisibleStoreFocusItems();
    state.focusItems = items;

    if (!items.length) {
      state.focusIndex = 0;
      return;
    }

    const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const activeKey = activeElement?.dataset?.storeFocusKey || "";
    const fallbackCardKey = state.selectedPluginId ? `card:${state.selectedPluginId}` : "";
    const fallbackSectionKey = `section:${state.activeSection}`;
    const nextKey = preferredKey || state.focusKey || activeKey || fallbackCardKey || fallbackSectionKey;

    let target = nextKey
      ? items.find((item) => item.dataset.storeFocusKey === nextKey)
      : null;

    if (!target && fallbackCardKey) {
      target = items.find((item) => item.dataset.storeFocusKey === fallbackCardKey) || null;
    }

    if (!target) {
      target = items.find((item) => item.dataset.storeFocusKey === fallbackSectionKey) || items[0];
    }

    state.focusIndex = Math.max(0, items.indexOf(target));
    applyStoreFocus();
  }

  function getStoreItemZone(item) {
    if (item?.closest?.(".steamloader-plugin-store-search-keyboard")) {
      return "keyboard";
    }

    if (item?.closest?.(".steamloader-plugin-store-context-menu")) {
      return "context-menu";
    }

    if (isStoreSearchInput(item)) {
      return "search";
    }

    if (item?.closest?.(".steamloader-plugin-store-topbar-actions")) {
      return "top";
    }

    if (item?.closest?.(".steamloader-plugin-store-nav")) {
      return "nav";
    }

    return item?.closest?.(".steamloader-plugin-store-gallery") ? "assets" : "nav";
  }

  function getStoreZoneItems(zone = getStoreItemZone(state.focusItems[state.focusIndex]) || "nav") {
    return state.focusItems.filter((item) => getStoreItemZone(item) === zone);
  }

  function getStoreKeyboardStep() {
    return 10;
  }

  function focusStoreElement(element) {
    const index = state.focusItems.indexOf(element);
    if (index < 0) {
      return false;
    }

    state.focusIndex = index;
    applyStoreFocus();
    return true;
  }

  function focusStoreZone(zone, index = 0) {
    const items = getStoreZoneItems(zone);
    if (!items.length) {
      return false;
    }

    const nextIndex = Math.max(0, Math.min(items.length - 1, index));
    return focusStoreElement(items[nextIndex]);
  }

  function focusSelectedStoreCard() {
    const cards = getStoreZoneItems("assets");
    if (!cards.length) {
      return false;
    }

    const selectedKey = state.selectedPluginId ? `card:${state.selectedPluginId}` : "";
    const selected = selectedKey
      ? cards.find((item) => item.dataset.storeFocusKey === selectedKey)
      : null;
    return focusStoreElement(selected || cards[0]);
  }

  function focusSpatialStoreCard(current, direction) {
    if (!(current instanceof HTMLElement)) {
      return false;
    }

    const currentRect = current.getBoundingClientRect();
    const currentX = currentRect.left + currentRect.width / 2;
    const currentY = currentRect.top + currentRect.height / 2;
    let bestCandidate = null;
    let bestScore = Number.POSITIVE_INFINITY;
    for (const candidate of getStoreZoneItems("assets")) {
      if (candidate === current) {
        continue;
      }

      const rect = candidate.getBoundingClientRect();
      const deltaX = rect.left + rect.width / 2 - currentX;
      const deltaY = rect.top + rect.height / 2 - currentY;
      const isEligible = direction === "left"
        ? deltaX < -8
        : direction === "right"
          ? deltaX > 8
          : direction === "up"
            ? deltaY < -8
            : deltaY > 8;
      if (!isEligible) {
        continue;
      }

      const primaryDistance = direction === "left" || direction === "right"
        ? Math.abs(deltaX)
        : Math.abs(deltaY);
      const secondaryDistance = direction === "left" || direction === "right"
        ? Math.abs(deltaY)
        : Math.abs(deltaX);
      if (secondaryDistance > primaryDistance * 1.25) {
        continue;
      }
      const score = primaryDistance + secondaryDistance * 0.55;
      if (score < bestScore) {
        bestCandidate = candidate;
        bestScore = score;
      }
    }

    return bestCandidate ? focusStoreElement(bestCandidate) : false;
  }

  function getStorePluginById(pluginId) {
    const id = String(pluginId || "");
    return getAllPlugins().find((plugin) => plugin?.id === id) || null;
  }

  function getPluginCapabilityChanges(plugin) {
    if (!plugin?.hasUpdate) {
      return [];
    }

    const installedPermissions = new Set(
      (Array.isArray(plugin.installedPermissions) ? plugin.installedPermissions : [])
        .map((value) => String(value || "").trim().toLowerCase()),
    );
    const installedHosts = new Set(
      (Array.isArray(plugin.installedNetworkHosts) ? plugin.installedNetworkHosts : [])
        .map((value) => String(value || "").trim().toLowerCase()),
    );
    const newPermissions = (Array.isArray(plugin.permissions) ? plugin.permissions : [])
      .map((value) => String(value || "").trim().toLowerCase())
      .filter((value) => value && !installedPermissions.has(value));
    const newHosts = (Array.isArray(plugin.networkHosts) ? plugin.networkHosts : [])
      .map((value) => String(value || "").trim().toLowerCase())
      .filter((value) => value && !installedHosts.has(value));
    return [
      ...newPermissions.map((value) => `permission: ${value}`),
      ...newHosts.map((value) => `network host: ${value}`),
    ];
  }

  function getStoreContextActions(plugin) {
    if (!plugin) {
      return [];
    }

    const actions = [];
    const capabilityChanges = getPluginCapabilityChanges(plugin);
    if (state.permissionReviewPluginId === plugin.id) {
      actions.push({
        id: "confirm-update",
        label: "Confirm update",
        copy: capabilityChanges.length
          ? `Allow ${capabilityChanges.length} new ${capabilityChanges.length === 1 ? "capability" : "capabilities"} and install the update.`
          : "Install the reviewed update.",
        icon: "A",
        kind: "danger",
        run: () => {
          state.permissionReviewPluginId = "";
          return runCommunityAction("api/plugin-store/plugins/update", plugin.id);
        },
      });
      actions.push({
        id: "cancel-review",
        label: "Keep current version",
        copy: "Cancel this update without changing the installed plugin.",
        icon: "B",
        keepOpen: true,
        run: () => {
          state.permissionReviewPluginId = "";
          return true;
        },
      });
      return actions;
    }
    if (plugin.isBuiltIn) {
      if (plugin.canToggleVisibility) {
        actions.push({
          id: "visibility",
          label: plugin.isEnabled ? "Hide from Home" : "Show in Home",
          copy: plugin.isEnabled
            ? "Keep it installed, but remove it from the TFS home list."
            : "Show this built-in plugin again on the TFS home list.",
          icon: plugin.isEnabled ? "H" : "S",
          kind: "primary",
          run: () => toggleBuiltInPlugin(plugin),
        });
      } else {
        actions.push({
          id: "core",
          label: "Core plugin",
          copy: "This built-in plugin is required and cannot be hidden.",
          icon: "i",
          disabled: true,
        });
      }
    } else {
      if (plugin.hasUpdate && plugin.canInstall) {
        actions.push({
          id: capabilityChanges.length ? "review-update" : "update",
          label: capabilityChanges.length ? "Review update" : "Update",
          copy: capabilityChanges.length
            ? `Review ${capabilityChanges.length} newly requested ${capabilityChanges.length === 1 ? "capability" : "capabilities"} before installing.`
            : "Download and install the newest available package.",
          icon: "U",
          kind: "primary",
          keepOpen: capabilityChanges.length > 0,
          run: () => {
            if (capabilityChanges.length) {
              state.permissionReviewPluginId = plugin.id;
              return true;
            }

            return runCommunityAction("api/plugin-store/plugins/update", plugin.id);
          },
        });
      } else if (!plugin.isInstalled && plugin.canInstall) {
        actions.push({
          id: "download",
          label: "Download",
          copy: "Install this community plugin from the catalog.",
          icon: "D",
          kind: "primary",
          run: () => runCommunityAction("api/plugin-store/plugins/install", plugin.id),
        });
      } else if (plugin.isInstalled) {
        actions.push({
          id: "installed",
          label: "Installed",
          copy: "This community plugin is already available on this device.",
          icon: "I",
          disabled: true,
        });
      }

      if (plugin.canUninstall) {
        actions.push({
          id: "uninstall",
          label: "Uninstall",
          copy: "Remove this community plugin from the local install folder.",
          icon: "!",
          kind: "danger",
          run: () => runCommunityAction("api/plugin-store/plugins/uninstall", plugin.id),
        });
      }
    }

    if (!actions.length) {
      actions.push({
        id: "none",
        label: "No actions available",
        copy: "This plugin does not expose a store action right now.",
        icon: "i",
        disabled: true,
      });
    }

    actions.push({
      id: "cancel",
      label: "Cancel",
      copy: "Close this menu and return to the plugin grid.",
      icon: "B",
      run: () => {
        closeStoreContextMenu();
        return true;
      },
    });

    return actions;
  }

  async function runStoreContextAction(action, plugin) {
    if (!action || action.disabled || state.busy) {
      return false;
    }

    if (action.id === "cancel") {
      closeStoreContextMenu();
      return true;
    }

    const pluginId = plugin?.id || state.contextMenuPluginId || state.selectedPluginId;
    if (action.keepOpen) {
      const result = await action.run?.();
      state.contextMenuPluginId = pluginId;
      requestStoreFocus(`context:${pluginId}:0`);
      render();
      return Boolean(result);
    }

    state.contextMenuPluginId = "";
    requestStoreFocus(pluginId ? `card:${pluginId}` : "");
    render();

    const result = await action.run?.();
    requestStoreFocus(pluginId ? `card:${pluginId}` : "");
    return Boolean(result);
  }

  function openStoreContextMenu(pluginId) {
    const nextPluginId = String(pluginId || "");
    if (!nextPluginId) {
      return false;
    }

    const plugin = getStorePluginById(nextPluginId);
    if (!plugin) {
      return false;
    }

    state.selectedPluginId = nextPluginId;
    state.contextMenuPluginId = nextPluginId;
    state.searchPadOpen = false;
    setStoreKeyboardLayer(false);
    const firstEnabledActionIndex = getStoreContextActions(plugin).findIndex((action) => !action.disabled);
    requestStoreFocus(`context:${nextPluginId}:${Math.max(0, firstEnabledActionIndex)}`);
    render();
    return true;
  }

  function closeStoreContextMenu() {
    const pluginId = state.contextMenuPluginId || state.selectedPluginId;
    state.contextMenuPluginId = "";
    state.permissionReviewPluginId = "";
    requestStoreFocus(pluginId ? `card:${pluginId}` : "");
    render();
  }

  function moveStoreFocus(direction) {
    refreshStoreFocus();
    if (!state.focusItems.length) {
      return;
    }

    const current = state.focusItems[state.focusIndex];
    const zone = getStoreItemZone(current);
    const zoneItems = getStoreZoneItems(zone);
    if (!zoneItems.length) {
      return;
    }

    const zoneIndex = Math.max(0, zoneItems.indexOf(current));

    if (zone === "context-menu") {
      if (direction === "up" || direction === "down") {
        focusStoreZone("context-menu", zoneIndex + (direction === "up" ? -1 : 1));
      }
      return;
    }

    if (zone === "keyboard") {
      if (direction === "up" && zoneIndex < getStoreKeyboardStep()) {
        focusStoreZone("search", 0);
        return;
      }

      if (direction === "down" || direction === "up") {
        focusStoreZone("keyboard", zoneIndex + (direction === "up" ? -getStoreKeyboardStep() : getStoreKeyboardStep()));
        return;
      }

      focusStoreZone("keyboard", zoneIndex + (direction === "left" ? -1 : 1));
      return;
    }

    if (zone === "search") {
      if (direction === "right") {
        focusStoreZone("top", 0);
      } else if (direction === "down") {
        if (state.searchPadOpen) {
          focusStoreZone("keyboard", 0);
        } else {
          focusSelectedStoreCard() || focusStoreZone("nav", 0);
        }
      }
      return;
    }

    if (zone === "top") {
      if (direction === "left" && zoneIndex === 0) {
        focusStoreZone("search", 0);
        return;
      }

      if (direction === "left" || direction === "right") {
        focusStoreZone("top", zoneIndex + (direction === "left" ? -1 : 1));
        return;
      }

      if (direction === "down") {
        focusSelectedStoreCard() || focusStoreZone("nav", 0);
      }
      return;
    }

    if (zone === "nav") {
      if (direction === "up") {
        focusStoreZone("search", 0) || focusStoreZone("top", 0);
        return;
      }

      if (direction === "down") {
        focusSelectedStoreCard();
        return;
      }

      if (direction === "left" || direction === "right") {
        focusStoreZone("nav", zoneIndex + (direction === "left" ? -1 : 1));
      }
      return;
    }

    if (!focusSpatialStoreCard(current, direction) && direction === "up") {
      focusStoreZone("search", 0) || focusStoreZone("nav", 0);
    }
  }

  function activateStoreFocus() {
    refreshStoreFocus();
    const item = state.focusItems[state.focusIndex];
    if (!item) {
      return;
    }

    if (isStoreSearchInput(item)) {
      state.searchPadOpen = true;
      requestStoreFocus("keyboard:Q");
      render();
      return;
    }

    item.click();
  }

  function cycleStoreSection(direction) {
    const sectionIds = storeSections.map(([sectionId]) => sectionId);
    const currentIndex = Math.max(0, sectionIds.indexOf(state.activeSection));
    const nextIndex = (currentIndex + direction + sectionIds.length) % sectionIds.length;
    state.activeSection = sectionIds[nextIndex];
    state.storePageIndex = 0;
    state.searchPadOpen = false;
    state.contextMenuPluginId = "";
    state.permissionReviewPluginId = "";
    state.selectedPluginId = "";
    ensureSelection();
    requestStoreFocus(state.selectedPluginId ? `card:${state.selectedPluginId}` : `section:${state.activeSection}`);
    render();
  }

  function handleStoreAction(action) {
    if (!state.open || Date.now() < state.ignoreOverlayInputUntil) {
      return;
    }

    if (action === "a") {
      activateStoreFocus();
      return;
    }

    if (action === "b") {
      if (state.searchPadOpen) {
        state.searchPadOpen = false;
        requestStoreFocus("top:search");
        render();
        return;
      }

      if (state.contextMenuPluginId) {
        closeStoreContextMenu();
        return;
      }

      void closeOverlay();
      return;
    }

    if (action === "search-back") {
      if (state.searchPadOpen) {
        handleStoreSearchKey("Back");
      }
      return;
    }

    if (action === "previous-section") {
      if (state.contextMenuPluginId) {
        return;
      }
      cycleStoreSection(-1);
      return;
    }

    if (action === "next-section") {
      if (state.contextMenuPluginId) {
        return;
      }
      cycleStoreSection(1);
      return;
    }

    moveStoreFocus(action);
  }

  function maybeRepeatGamepadAction(action, source = "browser-gamepad") {
    const now = Date.now();
    if (now < state.ignoreOverlayInputUntil) {
      return;
    }

    const isSteamSource = source.includes("steam");
    if (isSteamSource) {
      state.lastSteamGamepadInput = action;
      state.lastSteamGamepadInputAt = now;
    } else if (
      action === state.lastSteamGamepadInput &&
      now - state.lastSteamGamepadInputAt < 220
    ) {
      return;
    }

    const isMoveAction = action === "up" || action === "down" || action === "left" || action === "right";
    const isSectionAction = action === "previous-section" || action === "next-section";
    const repeatDelay =
      isMoveAction
        ? state.lastGamepadInput === action
          ? 170
          : 250
        : isSectionAction
          ? 210
          : action === "search-back"
            ? 170
            : 280;
    if (state.lastGamepadInput === action && now - state.lastGamepadInputAt < repeatDelay) {
      return;
    }

    state.lastGamepadInput = action;
    state.lastGamepadInputAt = now;
    handleStoreAction(action);
  }

  function getStoreActionFromSteamButton(button) {
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
    if (/\b(X|BUTTON_X|GAMEPADX|XBUTTON)\b/.test(namedButton)) {
      return "search-back";
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
      case 3:
        return "search-back";
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

  function shouldForwardStoreSteamButton(button, action) {
    const now = Date.now();
    const repeatMs = action === "up" || action === "down" || action === "left" || action === "right"
      ? 230
      : action === "previous-section" || action === "next-section"
        ? 220
        : action === "search-back"
          ? 170
          : 340;
    const lastMs = state.catchAllButtonState[button] || 0;
    if (now - lastMs < repeatMs) {
      return false;
    }

    state.catchAllButtonState[button] = now;
    return true;
  }

  function installStoreCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || state.catchAllInstalled) {
      return;
    }

    const previous = focusNav.m_fnCatchAllGamepadInput;
    const callback = (button) => {
      const action = getStoreActionFromSteamButton(button);
      const overlayInputActive = state.remoteOverlayActive || state.open;

      if (!overlayInputActive) {
        if (action && Date.now() < state.catchAllSuppressUntil) {
          return true;
        }

        return typeof previous === "function" ? previous(button) : false;
      }

      if (!action) {
        return true;
      }

      if (shouldForwardStoreSteamButton(button, action)) {
        postStoreMessage({ type: "input", action, source: "steam-catch-all" });
      }

      return true;
    };

    callback.__steamLoaderPluginStoreCatchAll = true;
    state.previousCatchAllGamepadInput = previous?.__steamLoaderPluginStoreCatchAll ? null : previous;
    focusNav.SetCatchAllGamepadInput(callback);
    state.catchAllInstalled = true;
  }

  function uninstallStoreCatchAllInput() {
    const focusNav = window.FocusNavController;
    if (!focusNav?.SetCatchAllGamepadInput || !state.catchAllInstalled) {
      return;
    }

    if (focusNav.m_fnCatchAllGamepadInput?.__steamLoaderPluginStoreCatchAll) {
      focusNav.SetCatchAllGamepadInput(state.previousCatchAllGamepadInput || undefined);
    }

    state.catchAllInstalled = false;
    state.previousCatchAllGamepadInput = null;
    state.catchAllButtonState = {};
    state.catchAllSuppressUntil = 0;
  }

  function releaseStoreInputCapture() {
    state.remoteOverlayActive = false;
    if (state.catchAllReleaseTimer) {
      window.clearTimeout(state.catchAllReleaseTimer);
      state.catchAllReleaseTimer = 0;
    }
    state.catchAllSuppressUntil = 0;
    uninstallStoreCatchAllInput();
  }

  function setRemoteStoreOverlayActive(active) {
    state.remoteOverlayActive = Boolean(active);
    if (state.remoteOverlayActive) {
      if (state.catchAllReleaseTimer) {
        window.clearTimeout(state.catchAllReleaseTimer);
        state.catchAllReleaseTimer = 0;
      }
      state.catchAllSuppressUntil = 0;
      installStoreCatchAllInput();
    } else {
      releaseStoreInputCapture();
    }
  }

  function getPressedStoreGamepadActions() {
    const gamepads = typeof navigator.getGamepads === "function" ? navigator.getGamepads() : [];
    const gamepad = [...gamepads].find(Boolean);
    const pressed = new Set();
    if (!gamepad) {
      return pressed;
    }

    const buttonMap = [
      [0, "a"],
      [1, "b"],
      [2, "search-back"],
      [4, "previous-section"],
      [5, "next-section"],
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

  function pollStoreGamepad() {
    if (!state.open) {
      stopStoreGamepadLoop();
      return;
    }

    const gamepads = typeof navigator.getGamepads === "function" ? navigator.getGamepads() : [];
    const gamepad = [...gamepads].find(Boolean);
    if (gamepad) {
      const pressed = new Set();
      const buttonMap = [
        [0, "a"],
        [1, "b"],
        [2, "search-back"],
        [4, "previous-section"],
        [5, "next-section"],
        [12, "up"],
        [13, "down"],
        [14, "left"],
        [15, "right"],
      ];

      for (const [index, action] of buttonMap) {
        if (gamepad.buttons[index]?.pressed) {
          pressed.add(action);
          if (
            !state.pressedGamepadButtons.has(action) ||
            action === "up" ||
            action === "down" ||
            action === "left" ||
            action === "right" ||
            action === "search-back"
          ) {
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
    } else {
      state.pressedGamepadButtons = new Set();
      state.lastGamepadInput = "";
    }

    state.gamepadFrame = window.requestAnimationFrame(pollStoreGamepad);
  }

  function startStoreGamepadLoop() {
    stopStoreGamepadLoop();
    state.lastGamepadInput = "";
    state.pressedGamepadButtons = getPressedStoreGamepadActions();
    state.gamepadFrame = window.requestAnimationFrame(pollStoreGamepad);
  }

  function stopStoreGamepadLoop() {
    if (state.gamepadFrame) {
      window.cancelAnimationFrame(state.gamepadFrame);
      state.gamepadFrame = 0;
    }

    state.pressedGamepadButtons = new Set();
    state.lastGamepadInput = "";
  }

  function handleStoreKeyDown(event) {
    if (!state.open) {
      return;
    }

    const key = event.key || event.code || "";
    const lowerKey = key.toLowerCase?.() || "";
    const isPreviousSectionKey =
      key === "PageUp" ||
      key === "GamepadLB" ||
      key === "GamepadL1" ||
      key === "GamepadLeftShoulder" ||
      lowerKey === "[";
    const isNextSectionKey =
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
      key === "GamepadUp" ||
      key === "GamepadDown" ||
      key === "GamepadLeft" ||
      key === "GamepadRight" ||
      key === "GamepadDPadUp" ||
      key === "GamepadDPadDown" ||
      key === "GamepadDPadLeft" ||
      key === "GamepadDPadRight" ||
      key === "Enter" ||
      key === " " ||
      key === "Space" ||
      key === "Escape" ||
      key === "GamepadA" ||
      key === "GamepadB" ||
      key === "GamepadX" ||
      isPreviousSectionKey ||
      isNextSectionKey;

    const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (
      isStoreSearchInput(activeElement) &&
      !isPreviousSectionKey &&
      !isNextSectionKey &&
      key !== "ArrowUp" &&
      key !== "ArrowDown" &&
      key !== "ArrowLeft" &&
      key !== "ArrowRight" &&
      key !== "GamepadUp" &&
      key !== "GamepadDown" &&
      key !== "GamepadLeft" &&
      key !== "GamepadRight" &&
      key !== "GamepadDPadUp" &&
      key !== "GamepadDPadDown" &&
      key !== "GamepadDPadLeft" &&
      key !== "GamepadDPadRight" &&
      key !== "Escape" &&
      key !== "GamepadA" &&
      key !== "GamepadB" &&
      key !== "GamepadX"
    ) {
      return;
    }

    if (!handled) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation?.();

    const source = key.startsWith("Gamepad") ? "steam-key" : "keyboard";
    if (key === "ArrowUp" || key === "GamepadUp" || key === "GamepadDPadUp") {
      maybeRepeatGamepadAction("up", source);
    } else if (key === "ArrowDown" || key === "GamepadDown" || key === "GamepadDPadDown") {
      maybeRepeatGamepadAction("down", source);
    } else if (key === "ArrowLeft" || key === "GamepadLeft" || key === "GamepadDPadLeft") {
      maybeRepeatGamepadAction("left", source);
    } else if (key === "ArrowRight" || key === "GamepadRight" || key === "GamepadDPadRight") {
      maybeRepeatGamepadAction("right", source);
    } else if (key === "Escape" || key === "GamepadB") {
      maybeRepeatGamepadAction("b", source);
    } else if (key === "GamepadX") {
      maybeRepeatGamepadAction("search-back", source);
    } else if (isPreviousSectionKey) {
      maybeRepeatGamepadAction("previous-section", source);
    } else if (isNextSectionKey) {
      maybeRepeatGamepadAction("next-section", source);
    } else {
      maybeRepeatGamepadAction("a", source);
    }
  }

  function swallowStoreInput(event) {
    if (!state.open) {
      return;
    }

    if (isStoreSearchInput(document.activeElement)) {
      return;
    }

    event.stopPropagation();
    event.stopImmediatePropagation?.();
  }

  function attachStoreInputTrap() {
    if (typeof state.keyHandler !== "function") {
      state.keyHandler = handleStoreKeyDown;
    }

    if (typeof state.keyUpHandler !== "function") {
      state.keyUpHandler = swallowStoreInput;
    }

    if (typeof state.keyPressHandler !== "function") {
      state.keyPressHandler = swallowStoreInput;
    }

    window.addEventListener("keydown", state.keyHandler, true);
    window.addEventListener("keyup", state.keyUpHandler, true);
    window.addEventListener("keypress", state.keyPressHandler, true);
  }

  function detachStoreInputTrap() {
    if (typeof state.keyHandler === "function") {
      window.removeEventListener("keydown", state.keyHandler, true);
    }

    if (typeof state.keyUpHandler === "function") {
      window.removeEventListener("keyup", state.keyUpHandler, true);
    }

    if (typeof state.keyPressHandler === "function") {
      window.removeEventListener("keypress", state.keyPressHandler, true);
    }
  }

  function syncStoreInputCapture() {
    if (state.open) {
      attachStoreInputTrap();
      installStoreCatchAllInput();
      setupStoreInputBridge();
      if (!state.overlayAnnounceTimer) {
        startStoreOverlayAnnouncements();
      }
      startStoreApiInputPolling();
      if (!state.gamepadFrame) {
        startStoreGamepadLoop();
      }
      return;
    }

    detachStoreInputTrap();
    stopStoreGamepadLoop();
    stopStoreApiInputPolling();
    stopStoreOverlayAnnouncements();
    if (!state.remoteOverlayActive) {
      uninstallStoreCatchAllInput();
    }
    state.focusItems = [];
    state.focusIndex = 0;
  }

  function getSnapshot() {
    return state.snapshot && typeof state.snapshot === "object" ? state.snapshot : null;
  }

  function getAllPlugins() {
    const snapshot = getSnapshot();
    return [
      ...(Array.isArray(snapshot?.builtInPlugins) ? snapshot.builtInPlugins : []),
      ...(Array.isArray(snapshot?.communityPlugins) ? snapshot.communityPlugins : []),
    ];
  }

  function sortStorePlugins(plugins) {
    return [...plugins].sort((left, right) => {
      const leftWeight = left?.isBuiltIn
        ? builtInStoreOrder.get(left?.id) ?? 500
        : 1000;
      const rightWeight = right?.isBuiltIn
        ? builtInStoreOrder.get(right?.id) ?? 500
        : 1000;
      if (leftWeight !== rightWeight) {
        return leftWeight - rightWeight;
      }

      return String(left?.title || "").localeCompare(String(right?.title || ""));
    });
  }

  function filterStorePluginsForSearch(plugins) {
    const query = String(state.searchQuery || "").trim().toLowerCase();
    if (!query) {
      return plugins;
    }

    const terms = query.split(/\s+/).filter(Boolean);
    return plugins.filter((plugin) => {
      const searchableText = [
        plugin?.title,
        plugin?.name,
        plugin?.description,
        plugin?.author,
        plugin?.source,
        plugin?.category,
        Array.isArray(plugin?.tags) ? plugin.tags.join(" ") : "",
      ]
        .filter(Boolean)
        .join(" ")
        .toLowerCase();

      return terms.every((term) => searchableText.includes(term));
    });
  }

  function getVisiblePlugins() {
    const snapshot = getSnapshot();
    const all = getAllPlugins();
    if (state.activeSection === "built-in") {
      return filterStorePluginsForSearch(sortStorePlugins(Array.isArray(snapshot?.builtInPlugins) ? snapshot.builtInPlugins : []));
    }

    if (state.activeSection === "community") {
      return filterStorePluginsForSearch(sortStorePlugins(Array.isArray(snapshot?.communityPlugins) ? snapshot.communityPlugins : []));
    }

    if (state.activeSection === "installed") {
      return filterStorePluginsForSearch(sortStorePlugins(all.filter((plugin) => Boolean(plugin?.isInstalled))));
    }

    if (state.activeSection === "updates") {
      return filterStorePluginsForSearch(sortStorePlugins(all.filter((plugin) => Boolean(plugin?.hasUpdate))));
    }

    return filterStorePluginsForSearch(sortStorePlugins(all));
  }

  function getStorePageCount(plugins = getVisiblePlugins()) {
    return Math.max(1, Math.ceil((plugins?.length || 0) / storePageSize));
  }

  function normalizeStorePageIndex(plugins = getVisiblePlugins()) {
    const pageCount = getStorePageCount(plugins);
    const selectedIndex = plugins.findIndex((plugin) => plugin?.id === state.selectedPluginId);
    if (selectedIndex >= 0) {
      state.storePageIndex = Math.floor(selectedIndex / storePageSize);
    }

    const pageIndex = Math.max(0, Math.min(pageCount - 1, Number(state.storePageIndex) || 0));
    state.storePageIndex = pageIndex;
    return pageIndex;
  }

  function getPagedStorePlugins(plugins = getVisiblePlugins()) {
    const pageIndex = normalizeStorePageIndex(plugins);
    return plugins.slice(pageIndex * storePageSize, pageIndex * storePageSize + storePageSize);
  }

  function selectStorePluginByVisibleIndex(index, preferredFocusKey = "") {
    const visiblePlugins = getVisiblePlugins();
    if (index < 0 || index >= visiblePlugins.length) {
      return false;
    }

    const targetIndex = index;
    const target = visiblePlugins[targetIndex];
    if (!target?.id) {
      return false;
    }

    state.selectedPluginId = target.id;
    state.storePageIndex = Math.floor(targetIndex / storePageSize);
    requestStoreFocus(preferredFocusKey || `card:${target.id}`);
    render();
    return true;
  }

  function ensureSelection() {
    const visiblePlugins = getVisiblePlugins();
    if (!visiblePlugins.length) {
      state.selectedPluginId = "";
      state.storePageIndex = 0;
      return;
    }

    if (!visiblePlugins.some((plugin) => plugin?.id === state.selectedPluginId)) {
      state.selectedPluginId = visiblePlugins[0]?.id || "";
    }

    normalizeStorePageIndex(visiblePlugins);
  }

  function getSelectedPlugin() {
    ensureSelection();
    return getVisiblePlugins().find((plugin) => plugin?.id === state.selectedPluginId) ||
      getAllPlugins().find((plugin) => plugin?.id === state.selectedPluginId) ||
      null;
  }

  async function requestJson(path, method = "GET", bodyPayload = null) {
    const response = await fetch(`${apiBase}${path}`, {
      method,
      headers: bodyPayload === null
        ? undefined
        : {
            "Content-Type": "application/json",
          },
      body: bodyPayload === null ? undefined : JSON.stringify(bodyPayload),
      cache: "no-store",
    });
    const payload = await response.json();
    if (!response.ok) {
      throw new Error(payload.message || `Store request failed (${response.status}).`);
    }

    return payload;
  }

  async function loadSnapshot(force = false) {
    if (state.loading) {
      return false;
    }

    state.loading = true;
    state.error = "";
    render();

    try {
      const payload = await requestJson(
        force ? "api/plugin-store/refresh" : "api/plugin-store/state",
        force ? "POST" : "GET",
        force ? {} : null,
      );
      state.snapshot = payload && typeof payload === "object" ? payload : null;
      ensureSelection();
      // Keep selection and controller focus separate. A snapshot can finish while the
      // user is navigating the search, tabs, or header actions; forcing the selected
      // card here would unexpectedly move focus into the gallery.
      return true;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.loading = false;
      render();
    }
  }

  async function setOverlayOpen(open) {
    try {
      await requestJson(
        open ? "api/plugin-store/overlay/open" : "api/plugin-store/overlay/close",
        "POST",
        {},
      );
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      render();
    }
  }

  async function toggleBuiltInPlugin(plugin) {
    if (!plugin?.id || !plugin.canToggleVisibility || state.busy) {
      return false;
    }

    state.busy = true;
    state.error = "";
    render();

    try {
      await requestJson("api/settings/plugins/enabled", "POST", {
        pluginId: plugin.id,
        enabled: !Boolean(plugin.isEnabled),
      });
      return await loadSnapshot(false);
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      render();
      return false;
    } finally {
      state.busy = false;
      render();
    }
  }

  async function runCommunityAction(path, pluginId) {
    if (!pluginId || state.busy) {
      return false;
    }

    state.busy = true;
    state.error = "";
    render();

    try {
      const payload = await requestJson(path, "POST", { pluginId });
      state.snapshot = payload && typeof payload === "object" ? payload : null;
      ensureSelection();
      try {
        window.__steamLoaderPluginStoreOverlayBridge?.refreshCommunityPlugins?.();
      } catch {
      }

      return true;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.busy = false;
      render();
    }
  }

  function getStoreSectionTitle(sectionId) {
    return storeSections.find(([candidate]) => candidate === sectionId)?.[1] || "Browse";
  }

  function getStoreSectionCopy(sectionId) {
    return storeSections.find(([candidate]) => candidate === sectionId)?.[2] || "";
  }

  function getStoreSectionCount(sectionId, snapshot = getSnapshot()) {
    const allPlugins = getAllPlugins();
    switch (sectionId) {
      case "built-in":
        return snapshot?.builtInCount ?? allPlugins.filter((plugin) => plugin?.isBuiltIn).length;
      case "community":
        return snapshot?.communityCount ?? allPlugins.filter((plugin) => !plugin?.isBuiltIn).length;
      case "installed":
        return allPlugins.filter((plugin) => Boolean(plugin?.isInstalled)).length;
      case "updates":
        return snapshot?.updateCount ?? allPlugins.filter((plugin) => Boolean(plugin?.hasUpdate)).length;
      case "discover":
      default:
        return allPlugins.length;
    }
  }

  function buildBadge(text, extraClass = "") {
    return createNode("span", `steamloader-plugin-store-badge${extraClass ? ` ${extraClass}` : ""}`, text);
  }

  function normalizeStoreImageUrl(imageUrl) {
    const value = String(imageUrl || "").trim();
    if (!value) {
      return "";
    }

    if (/^(https?:|data:|blob:|file:)/i.test(value)) {
      return value;
    }

    const base = apiBase.endsWith("/") ? apiBase : `${apiBase}/`;
    return value.startsWith("/")
      ? `${base.replace(/\/$/, "")}${value}`
      : `${base}${value}`;
  }

  function buildPreview(plugin, previewClassName, imageAltFallback, preferEager = false) {
    const preview = createNode("div", previewClassName);
    const placeholder = createNode("div", previewClassName === "steamloader-plugin-store-preview"
      ? "steamloader-plugin-store-preview-copy"
      : "steamloader-plugin-store-card-placeholder");
    const titleClass = previewClassName === "steamloader-plugin-store-preview"
      ? "steamloader-plugin-store-preview-kicker"
      : "steamloader-plugin-store-card-placeholder-title";
    const subtitleClass = previewClassName === "steamloader-plugin-store-preview"
      ? "steamloader-plugin-store-preview-subtitle"
      : "steamloader-plugin-store-card-placeholder-copy";

    placeholder.append(
      createNode("div", titleClass, previewClassName === "steamloader-plugin-store-preview" ? "Store Preview" : "No Preview"),
      createNode(
        "div",
        previewClassName === "steamloader-plugin-store-preview"
          ? "steamloader-plugin-store-preview-title"
          : "steamloader-plugin-store-card-placeholder-title",
        plugin?.title || "Plugin",
      ),
      createNode("div", subtitleClass, "No image available"),
    );
    preview.append(placeholder);

    const imageUrl = normalizeStoreImageUrl(Array.isArray(plugin?.images) ? plugin.images[0]?.url : "");
    if (imageUrl) {
      if (!(state.imageReadyUrls instanceof Set)) {
        state.imageReadyUrls = new Set();
      }

      if (state.imageReadyUrls.has(imageUrl)) {
        placeholder.style.display = "none";
      }

      const image = document.createElement("img");
      image.src = imageUrl;
      image.alt = imageAltFallback;
      image.decoding = "async";
      image.loading = preferEager ? "eager" : "lazy";
      image.fetchPriority = preferEager ? "high" : "low";
      image.addEventListener("load", () => {
        state.imageReadyUrls.add(imageUrl);
        placeholder.style.display = "none";
      });
      image.addEventListener("error", () => {
        image.remove();
        placeholder.style.display = "";
      });
      preview.append(image);
    }

    return preview;
  }

  function appendMetric(parent, label, value) {
    const metric = createNode("div", "steamloader-plugin-store-metric");
    metric.append(
      createNode("div", "steamloader-plugin-store-metric-label", label),
      createNode("div", "steamloader-plugin-store-metric-value", value || "Unknown"),
    );
    parent.append(metric);
  }

  function buildCard(plugin, index) {
    const card = createNode(
      "div",
      `steamloader-plugin-store-card${plugin?.id === state.selectedPluginId ? " is-selected" : ""}${plugin?.id === state.contextMenuPluginId ? " is-context-open" : ""}`,
    );
    card.setAttribute("role", "button");
    card.setAttribute("aria-label", plugin?.title || "Plugin");
    card.dataset.storeCardIndex = String(index);
    card.dataset.storeCardId = plugin?.id || "";
    card.addEventListener("click", () => {
      selectStorePlugin(plugin?.id || "");
      requestStoreFocus(`card:${plugin?.id || ""}`);
      openStoreContextMenu(plugin?.id || "");
    });
    decorateFocusable(card, `card:${plugin?.id || ""}`, () => {
      if (plugin?.id && plugin.id !== state.selectedPluginId) {
        selectStorePlugin(plugin.id);
      }
    });

    const main = createNode("div", "steamloader-plugin-store-card-main");
    main.append(
      createNode("div", "steamloader-plugin-store-card-title", plugin?.title || "Plugin"),
      createNode("div", "steamloader-plugin-store-card-author", plugin?.author || plugin?.source || "Tools for Steam"),
      createNode(
        "div",
        "steamloader-plugin-store-card-description",
        plugin?.description || "No description available.",
      ),
    );

    const badges = createNode("div", "steamloader-plugin-store-badges");
    const badgeKeys = new Set();
    const appendUniqueBadge = (label, extraClass = "") => {
      const value = String(label || "").trim();
      const key = value.toLowerCase().replace(/[\s_-]+/g, "");
      if (!value || !key || badgeKeys.has(key) || badges.childElementCount >= 3) {
        return;
      }
      badgeKeys.add(key);
      badges.append(buildBadge(value, extraClass));
    };
    appendUniqueBadge(plugin?.isBuiltIn ? "Built-In" : "Community", plugin?.isBuiltIn ? "is-built-in" : "");
    if (plugin?.hasUpdate) {
      appendUniqueBadge("Update", "is-update");
    }

    if (Array.isArray(plugin?.tags)) {
      for (const tag of plugin.tags) {
        appendUniqueBadge(tag);
        if (badges.childElementCount >= 3) {
          break;
        }
      }
    }

    const statusText = plugin?.statusText ||
      plugin?.installedVersion ||
      plugin?.version ||
      (plugin?.isInstalled ? "Installed" : "Not installed");
    const footer = createNode("div", "steamloader-plugin-store-card-footer");
    footer.append(
      createNode("div", "steamloader-plugin-store-card-status", statusText),
    );
    main.append(badges, footer);

    card.append(
      main,
      buildPreview(plugin, "steamloader-plugin-store-card-preview", `${plugin?.title || "Plugin"} preview`, index < 12),
    );
    return card;
  }

  function updateStoreSearchQuery(value, focusKey = "") {
    state.searchQuery = String(value || "");
    state.storePageIndex = 0;
    state.contextMenuPluginId = "";
    state.permissionReviewPluginId = "";
    state.selectedPluginId = "";
    ensureSelection();
    requestStoreFocus(focusKey || "keyboard:Q");
    render();
  }

  function handleStoreSearchKey(key) {
    const value = String(key || "");
    if (value === "Done") {
      state.searchPadOpen = false;
      requestStoreFocus("top:search");
      render();
      return;
    }

    if (value === "Back") {
      updateStoreSearchQuery(String(state.searchQuery || "").slice(0, -1), "keyboard:Back");
      return;
    }

    if (value === "Clear") {
      updateStoreSearchQuery("", "keyboard:Clear");
      return;
    }

    updateStoreSearchQuery(`${state.searchQuery || ""}${value === "Space" ? " " : value.toLowerCase()}`, `keyboard:${value}`);
  }

  function buildStoreSearchKeyboard() {
    const panel = createNode("div", "steamloader-plugin-store-search-keyboard");
    const header = createNode("div", "steamloader-plugin-store-search-keyboard-header");
    header.append(
      createNode("div", "steamloader-plugin-store-search-keyboard-title", "Search plugins"),
      createNode("div", "steamloader-plugin-store-search-keyboard-value", state.searchQuery || "A Type - X Back"),
    );

    const grid = createNode("div", "steamloader-plugin-store-search-keyboard-grid");
    for (const row of searchKeyboardRows) {
      const rowNode = createNode("div", "steamloader-plugin-store-search-keyboard-row");
      for (const key of row) {
        const button = createNode(
          "button",
          `steamloader-plugin-store-search-key${key.length > 1 ? " is-wide" : ""}`,
          key,
        );
        button.type = "button";
        button.addEventListener("click", () => {
          handleStoreSearchKey(key);
        });
        decorateFocusable(button, `keyboard:${key}`);
        rowNode.append(button);
      }
      grid.append(rowNode);
    }

    panel.append(header, grid);
    return panel;
  }

  function positionStoreContextMenu() {
    const root = getStoreRoot();
    const menu = root?.querySelector?.(".steamloader-plugin-store-context-menu");
    if (!(root instanceof HTMLElement) || !(menu instanceof HTMLElement)) {
      return;
    }

    menu.style.left = "50%";
    menu.style.top = "50%";
    menu.style.transform = "translate(-50%, -50%)";
  }

  function buildStoreContextMenu() {
    const plugin = getStorePluginById(state.contextMenuPluginId);
    if (!plugin) {
      return null;
    }

    const fragment = document.createDocumentFragment();
    const scrim = createNode("div", "steamloader-plugin-store-context-scrim");
    scrim.addEventListener("click", () => {
      closeStoreContextMenu();
    });

    const menu = createNode("div", "steamloader-plugin-store-context-menu");
    menu.setAttribute("role", "dialog");
    menu.setAttribute("aria-modal", "true");
    menu.setAttribute("aria-label", `${plugin.title || "Plugin"} details`);

    const header = createNode("div", "steamloader-plugin-store-context-header");
    header.append(
      createNode("div", "steamloader-plugin-store-context-kicker", plugin.isBuiltIn ? "Built-In Plugin" : "Community Plugin"),
      createNode("div", "steamloader-plugin-store-context-title", plugin.title || "Plugin"),
    );

    const overview = createNode("div", "steamloader-plugin-store-context-overview");
    overview.append(
      buildPreview(
        plugin,
        "steamloader-plugin-store-context-preview",
        `${plugin?.title || "Plugin"} preview`,
        true,
      ),
      createNode(
        "div",
        "steamloader-plugin-store-context-description",
        plugin.description || "No description is available for this plugin.",
      ),
      createNode(
        "div",
        "steamloader-plugin-store-context-status",
        plugin.statusText || (plugin.isInstalled ? "Installed and ready." : "Available in the store."),
      ),
    );

    const panel = createNode("div", "steamloader-plugin-store-context-panel");
    if (state.permissionReviewPluginId === plugin.id) {
      const capabilityChanges = getPluginCapabilityChanges(plugin);
      const reviewNotice = createNode("div", "steamloader-plugin-store-context-permission is-new");
      reviewNotice.append(
        createNode("div", "steamloader-plugin-store-context-permission-title", "Update approval required"),
        createNode(
          "div",
          "steamloader-plugin-store-context-permission-copy",
          capabilityChanges.length
            ? `This version adds ${capabilityChanges.join(", ")}. Confirm only if you trust the publisher.`
            : "Review this update before replacing the installed version.",
        ),
      );
      panel.append(reviewNotice);
    }
    const facts = createNode("div", "steamloader-plugin-store-context-facts");
    const factValues = [
      ["Available", plugin.version || "Built-in"],
      ["Installed", plugin.installedVersion || (plugin.isBuiltIn ? "Included" : "Not installed")],
      ["SDK", plugin.sdkVersion || (plugin.isBuiltIn ? "Core" : "Unknown")],
      ["Author", plugin.author || plugin.source || "Tools for Steam"],
    ];
    for (const [label, value] of factValues) {
      const fact = createNode("div", "steamloader-plugin-store-context-fact");
      fact.append(
        createNode("div", "steamloader-plugin-store-context-fact-label", label),
        createNode("div", "steamloader-plugin-store-context-fact-value", value),
      );
      facts.append(fact);
    }
    panel.append(facts);

    const permissions = Array.isArray(plugin.permissions) ? plugin.permissions.filter(Boolean) : [];
    const installedPermissions = new Set(
      (Array.isArray(plugin.installedPermissions) ? plugin.installedPermissions : [])
        .map((permission) => String(permission || "").trim().toLowerCase()),
    );
    const permissionList = createNode("div", "steamloader-plugin-store-context-permissions");
    const permissionHeading = createNode(
      "div",
      "steamloader-plugin-store-context-permission-title",
      permissions.length ? `Permissions (${permissions.length})` : "Permissions",
    );
    permissionList.append(permissionHeading);
    if (permissions.length) {
      for (const permission of permissions) {
        const normalizedPermission = String(permission).trim().toLowerCase();
        const isNewPermission = Boolean(plugin.hasUpdate) && !installedPermissions.has(normalizedPermission);
        const permissionRow = createNode(
          "div",
          `steamloader-plugin-store-context-permission${isNewPermission ? " is-new" : ""}`,
        );
        const permissionLabel = createNode(
          "div",
          "steamloader-plugin-store-context-permission-title",
          normalizedPermission,
        );
        if (isNewPermission) {
          permissionLabel.append(createNode("span", "steamloader-plugin-store-context-permission-new", "New in update"));
        }
        permissionRow.append(
          permissionLabel,
          createNode(
            "div",
            "steamloader-plugin-store-context-permission-copy",
            storePermissionDescriptions[normalizedPermission] || "Uses an additional Tools for Steam capability.",
          ),
        );
        permissionList.append(permissionRow);
      }
    } else {
      permissionList.append(
        createNode(
          "div",
          "steamloader-plugin-store-context-permission-copy",
          plugin.isBuiltIn ? "Core functionality managed by Tools for Steam." : "No optional SDK capabilities declared.",
        ),
      );
    }
    panel.append(permissionList);

    const networkHosts = Array.isArray(plugin.networkHosts) ? plugin.networkHosts.filter(Boolean) : [];
    if (networkHosts.length) {
      const installedNetworkHosts = new Set(
        (Array.isArray(plugin.installedNetworkHosts) ? plugin.installedNetworkHosts : [])
          .map((host) => String(host || "").trim().toLowerCase()),
      );
      const hostList = createNode("div", "steamloader-plugin-store-context-permissions");
      hostList.append(createNode(
        "div",
        "steamloader-plugin-store-context-permission-title",
        `Network hosts (${networkHosts.length})`,
      ));
      for (const host of networkHosts) {
        const normalizedHost = String(host).trim().toLowerCase();
        const isNewHost = Boolean(plugin.hasUpdate) && !installedNetworkHosts.has(normalizedHost);
        const hostRow = createNode(
          "div",
          `steamloader-plugin-store-context-permission${isNewHost ? " is-new" : ""}`,
        );
        const hostLabel = createNode("div", "steamloader-plugin-store-context-permission-title", normalizedHost);
        if (isNewHost) {
          hostLabel.append(createNode("span", "steamloader-plugin-store-context-permission-new", "New in update"));
        }
        hostRow.append(
          hostLabel,
          createNode(
            "div",
            "steamloader-plugin-store-context-permission-copy",
            normalizedHost === "<local>"
              ? "May connect only to local-network, loopback, or .local devices."
              : "May connect to this declared host through the TFS network proxy.",
          ),
        );
        hostList.append(hostRow);
      }
      panel.append(hostList);
    }

    if (String(plugin.changelog || "").trim()) {
      const changelog = createNode("div", "steamloader-plugin-store-context-changelog");
      changelog.append(
        createNode("div", "steamloader-plugin-store-context-permission-title", "What's new"),
        createNode("div", "steamloader-plugin-store-context-changelog-copy", String(plugin.changelog).trim()),
      );
      panel.append(changelog);
    }

    const list = createNode("div", "steamloader-plugin-store-context-list");
    getStoreContextActions(plugin).forEach((action, index) => {
      const button = createNode(
        "button",
        `steamloader-plugin-store-context-action${action.kind === "danger" ? " is-danger" : ""}${action.disabled ? " is-disabled" : ""}`,
      );
      button.type = "button";
      button.disabled = Boolean(action.disabled) || state.busy;
      button.addEventListener("click", (event) => {
        event.stopPropagation();
        void runStoreContextAction(action, plugin);
      });

      const text = createNode("span", "steamloader-plugin-store-context-action-text");
      text.append(
        createNode("span", "steamloader-plugin-store-context-action-label", action.label),
        createNode("span", "steamloader-plugin-store-context-action-copy", action.copy || ""),
      );
      button.append(
        text,
        createNode("span", "steamloader-plugin-store-context-action-icon", action.icon || ""),
      );

      if (!button.disabled) {
        decorateFocusable(button, `context:${plugin.id}:${index}`);
      }
      list.append(button);
    });

    panel.append(list);
    menu.append(header, overview, panel);
    fragment.append(scrim, menu);
    window.requestAnimationFrame(positionStoreContextMenu);
    return fragment;
  }

  async function closeOverlay() {
    state.open = false;
    state.searchPadOpen = false;
    state.contextMenuPluginId = "";
    state.permissionReviewPluginId = "";
    setStoreKeyboardLayer(false);
    setRemoteStoreOverlayActive(false);
    state.ignoreOverlayInputUntil = Date.now() + 280;
    render();
    await setOverlayOpen(false);
  }

  async function syncOverlayState() {
    try {
      const overlayState = await requestJson("api/plugin-store/overlay/state");
      const shouldOpen = Boolean(overlayState?.isOpen);
      const canHost = canHostPluginStoreOverlay();
      if (shouldOpen && !canHost) {
        setRemoteStoreOverlayActive(true);
        if (state.open) {
          state.open = false;
          render();
        }

        state.lastOverlayOpenValue = shouldOpen;
        return;
      }

      if (shouldOpen && !state.open) {
        setRemoteStoreOverlayActive(true);
        state.open = true;
        requestStoreFocus(state.selectedPluginId ? `card:${state.selectedPluginId}` : `section:${state.activeSection}`);
        render();
        await loadSnapshot(false);
      } else if (!shouldOpen && state.open) {
        setRemoteStoreOverlayActive(false);
        state.open = false;
        render();
      } else if (!shouldOpen) {
        setRemoteStoreOverlayActive(false);
      } else if (shouldOpen && !state.snapshot && !state.loading) {
        await loadSnapshot(false);
      }

      state.lastOverlayOpenValue = shouldOpen;
    } catch {
    } finally {
      queueOverlayStatePoll();
    }
  }

  function queueOverlayStatePoll() {
    if (state.pollTimer) {
      window.clearTimeout(state.pollTimer);
    }

    state.pollTimer = window.setTimeout(syncOverlayState, state.open ? openPollMs : closedPollMs);
  }

  function render() {
    ensureStyleElement();
    const root = ensureRoot();
    state.root = root;
    root.classList.toggle("is-open", state.open);
    root.classList.toggle(
      "is-keyboard-open",
      state.open &&
        Date.now() < (state.searchKeyboardActiveUntil || 0) &&
        isStoreSearchInput(document.activeElement),
    );
    root.replaceChildren();

    if (!state.open) {
      setStoreKeyboardLayer(false);
      syncStoreInputCapture();
      return;
    }

    const snapshot = getSnapshot();
    const surface = createNode("div", "steamloader-plugin-store-surface");
    const main = createNode("div", "steamloader-plugin-store-main");

    const topbar = createNode("div", "steamloader-plugin-store-topbar");
    const brand = createNode("div", "steamloader-plugin-store-brand");
    brand.append(
      createNode("div", "steamloader-plugin-store-kicker", "Tools for Steam"),
      createNode("h1", "steamloader-plugin-store-title", "Tools for Steam"),
      createNode(
        "div",
        "steamloader-plugin-store-subtitle",
        snapshot?.catalogDescription || "Built-in plugins live here permanently. Community entries can add installs, updates, previews, and downloads later.",
      ),
    );

    const search = createNode("div", "steamloader-plugin-store-search");
    const searchInput = document.createElement("input");
    searchInput.type = "search";
    searchInput.className = "steamloader-plugin-store-search-input";
    searchInput.placeholder = "Search plugins";
    searchInput.value = state.searchQuery || "";
    searchInput.autocomplete = "off";
    searchInput.spellcheck = false;
    searchInput.readOnly = true;
    searchInput.setAttribute("enterkeyhint", "search");
    searchInput.setAttribute("aria-label", "Search plugins");
    searchInput.addEventListener("click", () => {
      requestStoreFocus("top:search");
    });
    searchInput.addEventListener("input", () => {
      state.searchQuery = searchInput.value;
      ensureSelection();
      requestStoreFocus("top:search");

      if (state.searchRenderTimer) {
        window.clearTimeout(state.searchRenderTimer);
      }

      state.searchRenderTimer = window.setTimeout(() => {
        state.searchRenderTimer = 0;
        render();
      }, 120);
    });
    decorateFocusable(searchInput, "top:search");
    search.append(searchInput);

    const actions = createNode("div", "steamloader-plugin-store-topbar-actions");
    actions.append(
      createNode("span", "steamloader-plugin-store-chip", `${snapshot?.builtInCount || 0} Built-In`),
      createNode("span", "steamloader-plugin-store-chip", `${snapshot?.communityCount || 0} Community`),
      createNode("span", "steamloader-plugin-store-chip is-accent", `${snapshot?.updateCount || 0} Updates`),
    );

    const refreshButton = createNode(
      "button",
      "steamloader-plugin-store-button",
      state.loading ? "Refreshing..." : "Refresh",
    );
    refreshButton.type = "button";
    refreshButton.disabled = state.loading || state.busy;
    refreshButton.addEventListener("click", () => {
      void loadSnapshot(true);
    });
    decorateFocusable(refreshButton, "top:refresh");

    const closeButton = createNode("button", "steamloader-plugin-store-button", "Close");
    closeButton.type = "button";
    closeButton.addEventListener("click", () => {
      void closeOverlay();
    });
    decorateFocusable(closeButton, "top:close");
    actions.append(refreshButton, closeButton);
    topbar.append(brand, search, actions);
    main.append(topbar);

    const tabsRow = createNode("div", "steamloader-plugin-store-tabs-row");
    const previousHint = createNode("div", "steamloader-plugin-store-bumper", "LB");
    previousHint.setAttribute("aria-hidden", "true");
    const nextHint = createNode("div", "steamloader-plugin-store-bumper", "RB");
    nextHint.setAttribute("aria-hidden", "true");
    const nav = createNode("div", "steamloader-plugin-store-nav");
    for (const [sectionId, title] of storeSections) {
      const button = createNode(
        "button",
        `steamloader-plugin-store-nav-button${state.activeSection === sectionId ? " is-active" : ""}`,
      );
      button.type = "button";
      button.dataset.storeSectionId = sectionId;
      button.addEventListener("click", () => {
        state.activeSection = sectionId;
        state.storePageIndex = 0;
        state.searchPadOpen = false;
        state.contextMenuPluginId = "";
        state.permissionReviewPluginId = "";
        state.selectedPluginId = "";
        ensureSelection();
        requestStoreFocus(state.selectedPluginId ? `card:${state.selectedPluginId}` : `section:${sectionId}`);
        render();
      });
      decorateFocusable(button, `section:${sectionId}`);

      const label = createNode("span", "steamloader-plugin-store-tab-title");
      label.append(
        document.createTextNode(title),
        createNode("span", "steamloader-plugin-store-tab-count", String(getStoreSectionCount(sectionId, snapshot))),
      );
      button.append(label);
      nav.append(button);
    }
    tabsRow.append(previousHint, nav, nextHint);
    main.append(tabsRow);

    const statusRow = createNode("div", "steamloader-plugin-store-status-row");
    const statusText = state.busy
      ? "Working on the selected plugin..."
      : state.loading
        ? "Refreshing the community catalog..."
        : snapshot?.communityCatalogStatusText || snapshot?.statusText || "";
    if (statusText) {
      statusRow.append(createNode("div", "steamloader-plugin-store-status", statusText));
    }
    if (snapshot?.catalogTrustText) {
      statusRow.append(createNode(
        "div",
        `steamloader-plugin-store-status${snapshot?.isCustomCatalog ? " is-developer-catalog" : ""}`,
        snapshot.catalogTrustText,
      ));
    }

    if (state.error) {
      statusRow.append(createNode("div", "steamloader-plugin-store-error", state.error));
    }

    main.append(statusRow);

    const content = createNode("div", "steamloader-plugin-store-content");
    const browser = createNode("div", "steamloader-plugin-store-browser");
    const sectionHeading = createNode("div", "steamloader-plugin-store-section-heading");
    const visiblePlugins = getVisiblePlugins();
    sectionHeading.append(
      createNode("div", "steamloader-plugin-store-section-title", getStoreSectionTitle(state.activeSection)),
      createNode("div", "steamloader-plugin-store-section-copy", getStoreSectionCopy(state.activeSection)),
    );
    browser.append(sectionHeading);

    const gallery = createNode("div", "steamloader-plugin-store-gallery");
    if (visiblePlugins.length) {
      visiblePlugins.forEach((plugin, index) => {
        gallery.append(buildCard(plugin, index));
      });
    } else {
      gallery.append(
        createNode(
          "div",
          "steamloader-plugin-store-empty",
          state.loading
            ? "Loading plugin catalog..."
            : String(state.searchQuery || "").trim()
              ? "No plugins match your search."
              : "This section is still empty. Built-ins remain available, and community downloads can appear here as soon as your registry feed is connected.",
        ),
      );
    }

    browser.append(gallery);
    content.append(browser);
    main.append(content);

    const controllerBar = createNode("div", "steamloader-plugin-store-controller-bar");
    controllerBar.setAttribute("aria-hidden", "true");
    const openHint = createNode("div", "steamloader-plugin-store-controller-hint");
    openHint.append(
      createNode("span", "steamloader-plugin-store-controller-key", "A"),
      createNode("span", "steamloader-plugin-store-controller-label", state.contextMenuPluginId ? "Select" : "Open"),
    );
    const closeHint = createNode("div", "steamloader-plugin-store-controller-hint");
    closeHint.append(
      createNode("span", "steamloader-plugin-store-controller-key", "B"),
      createNode("span", "steamloader-plugin-store-controller-label", state.contextMenuPluginId ? "Back" : "Close"),
    );
    controllerBar.append(openHint, closeHint);
    main.append(controllerBar);

    if (state.searchPadOpen) {
      main.append(buildStoreSearchKeyboard());
    }

    const contextMenu = buildStoreContextMenu();
    if (contextMenu) {
      main.append(contextMenu);
    }

    surface.append(main);
    root.append(surface);

    syncStoreInputCapture();
    if (state.focusPending || !root.contains(document.activeElement)) {
      state.focusPending = false;
      window.requestAnimationFrame(() => {
        refreshStoreFocus();
      });
    }
  }

  ensureStyleElement();
  setupStoreInputBridge();
  queueOverlayStatePoll();
})();
