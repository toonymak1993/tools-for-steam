// Tools for Steam - OmniLibrary label on Steam's native game details action.
(() => {
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";
  const stateVersion = 26;
  const activeDownloadRefreshIntervalMs = 1000;
  const managedGameRefreshIntervalMs = 15000;
  const idleRefreshIntervalMs = 30000;
  const styleId = "steamtools-xbox-library-style";
  const downloadStyleId = "steamtools-omnilibrary-download-style";
  const downloadStatusId = "steamtools-omnilibrary-download-status";
  const uninstallActionId = "steamtools-omnilibrary-uninstall-action";
  const uninstallDialogId = "steamtools-omnilibrary-uninstall-dialog";
  const omniLibraryStoreStorageKey = "ToolsForSteamOmniLibraryStoresChanged";
  const omniLibraryStoreChannelName = "ToolsForSteamOmniLibraryStores";

  const previousState = window.__steamToolsXboxLibrarySurfaceState;
  if (previousState?.version !== stateVersion) {
    if (previousState?.timer) {
      window.clearTimeout(previousState.timer);
    }
    if (previousState?.mutationTimer) {
      window.clearTimeout(previousState.mutationTimer);
    }
    previousState?.observer?.disconnect?.();
    restorePreviousManagedLabelsSafely(previousState);
    try {
      previousState?.omniLibraryStateUnsubscribe?.();
    } catch (_) {}
    if (typeof previousState?.storageHandler === "function") {
      window.removeEventListener("storage", previousState.storageHandler);
    }
    if (typeof previousState?.focusHandler === "function") {
      window.removeEventListener("focus", previousState.focusHandler);
    }
    try {
      previousState?.channel?.close?.();
    } catch (_) {}
    document.getElementById("steamtools-xbox-library-action")?.remove();
    document.getElementById(uninstallActionId)?.remove();
    document.getElementById(uninstallDialogId)?.remove();
    document.getElementById(styleId)?.remove();
    document.getElementById(downloadStatusId)?.remove();
    document.getElementById(downloadStyleId)?.remove();
    if (typeof previousState?.dialogKeyHandler === "function") {
      window.removeEventListener("keydown", previousState.dialogKeyHandler, true);
    }
    try {
      previousState?.uninstallNavigation?.unregister?.();
    } catch (_) {}
    for (const registration of previousState?.dialogNavigation || []) {
      try {
        registration?.unregister?.();
      } catch (_) {}
    }
    for (const element of document.querySelectorAll(
      "[data-steamtools-xbox-native-action]",
    )) {
      element.removeAttribute("data-steamtools-xbox-native-action");
    }
    for (const element of document.querySelectorAll(
      "[data-steamtools-omni-download-state]",
    )) {
      element.removeAttribute("data-steamtools-omni-download-state");
      element.removeAttribute("aria-busy");
    }
    for (const icon of document.querySelectorAll(".steamtools-omni-download-icon")) {
      icon.classList.remove("steamtools-omni-download-icon");
    }
  }

  const state =
    previousState?.version === stateVersion
      ? previousState
      : (window.__steamToolsXboxLibrarySurfaceState = {
          version: stateVersion,
          timer: 0,
          mutationTimer: 0,
          lastMutationRefreshAt: 0,
          observer: null,
          summary: null,
          currentGame: null,
          currentStoreId: "",
          currentAppId: 0,
          requestInFlight: false,
          lastGameFetchAt: 0,
          lastRenderError: "",
          managedLabels: new Map(),
          restoreManagedLabels: null,
          channel: null,
          storageHandler: null,
          focusHandler: null,
          omniLibraryStateUnsubscribe: null,
        });
  if (!(state.managedLabels instanceof Map)) {
    state.managedLabels = new Map();
  }

  function restorePreviousManagedLabelsSafely(previous) {
    const injectedLabelPattern =
      /^(download|preparing|queued|downloading(?:\s+.*)?|reconnecting(?:\s+.*)?|resume download|finalizing(?:\s+.*)?|canceling|open xbox|open gog|retry download)$/i;
    for (const [textNode, record] of previous?.managedLabels || []) {
      const originalLabel =
        typeof record === "string" ? record : record?.originalLabel;
      const appliedLabel =
        typeof record === "string" ? "" : record?.appliedLabel;
      const currentLabel = String(textNode?.nodeValue || "").trim();
      const stillOwnedByOmniLibrary = appliedLabel
        ? currentLabel === appliedLabel
        : injectedLabelPattern.test(currentLabel);
      if (
        textNode?.isConnected &&
        typeof originalLabel === "string" &&
        stillOwnedByOmniLibrary &&
        textNode.nodeValue !== originalLabel
      ) {
        textNode.nodeValue = originalLabel;
      }
    }
    previous?.managedLabels?.clear?.();
  }

  function isManagedShortcutAppId(value) {
    const appId = Number(value);
    return Number.isInteger(appId) && appId >= 0x80000000;
  }

  function getCurrentAppId() {
    const route = `${window.location.pathname || ""}${window.location.hash || ""}${window.location.search || ""}`;
    const routeMatch = route.match(/\/(?:library\/app|appdetails)\/(\d+)/i);
    if (routeMatch) {
      return Number(routeMatch[1]);
    }

    const patterns = [
      /\/customimages\/(\d+)(?:p|_hero|-icon)?(?:\.[a-z0-9]+)?/i,
      /\/apps\/(\d+)\//i,
      /\/images\/apps\/(\d+)\//i,
      /\/libraryassets\/(\d+)\//i,
      /[?&]appid=(\d+)/i,
      /\/appdetails\/(\d+)/i,
    ];
    let container = findPlaySection();
    const reactAppId = findCurrentAppIdInReact(container);
    if (reactAppId) {
      return reactAppId;
    }

    let artworkAppId = 0;
    for (let depth = 0; container && depth < 7; depth += 1, container = container.parentElement) {
      for (const element of [container, ...container.querySelectorAll("img, [src], [href], [style]")]) {
        const values = [
          element.getAttribute?.("src"),
          element.getAttribute?.("href"),
          element.getAttribute?.("style"),
          element instanceof HTMLImageElement ? element.currentSrc : "",
        ];
        for (const value of values.filter(Boolean)) {
          for (const pattern of patterns) {
            const match = String(value).match(pattern);
            if (match) {
              const appId = Number(match[1]);
              if (Number.isInteger(appId) && appId > 0) {
                if (isKnownXboxAppId(appId)) {
                  return appId;
                }
                artworkAppId ||= appId;
              }
            }
          }
        }
      }
    }

    const libraryState = window.__steamLoaderLibraryTabsState;
    const recentAppId = Number(
      libraryState?.lastActivatedManagedAppId ||
      0,
    );
    const recentAt = Number(
      libraryState?.lastActivatedManagedAppAt ||
      0,
    );
    if (
      isManagedShortcutAppId(recentAppId) &&
      Date.now() - recentAt < 5000 &&
      isKnownXboxAppId(recentAppId)
    ) {
      return recentAppId;
    }

    return artworkAppId;
  }

  function isKnownXboxAppId(value) {
    const appId = Number(value);
    return isManagedShortcutAppId(appId) &&
      (state.summary?.stores || [])
        .filter((store) => store?.enabled === true)
        .some((store) => (store?.appIds || [])
          .some((candidate) => Number(candidate) === appId));
  }

  function findCurrentAppIdInReact(element) {
    const roots = [];
    for (
      let depth = 0, current = element;
      current && depth < 8;
      depth += 1, current = current.parentElement
    ) {
      for (const key of Object.getOwnPropertyNames(current)) {
        if (key.startsWith("__reactProps") || key.startsWith("__reactFiber")) {
          roots.push(current[key]);
        }
      }
    }

    const queue = roots.slice();
    const visited = new WeakSet();
    let fallbackAppId = 0;
    for (let inspected = 0; queue.length && inspected < 1200; inspected += 1) {
      const current = queue.shift();
      if (!current || typeof current !== "object" || visited.has(current)) {
        continue;
      }

      visited.add(current);
      const candidate = Number(
        current.appid ?? current.appId ?? current.appID ?? current.app_id ?? 0,
      );
      if (Number.isInteger(candidate) && candidate > 0) {
        if (isKnownXboxAppId(candidate)) {
          return candidate;
        }
        fallbackAppId ||= candidate;
      }

      for (const key of [
        "overview",
        "app",
        "game",
        "item",
        "data",
        "props",
        "memoizedProps",
        "pendingProps",
        "memoizedState",
        "return",
        "child",
      ]) {
        const value = current[key];
        if (
          value &&
          typeof value === "object" &&
          !(value instanceof Element) &&
          !(value instanceof Node)
        ) {
          queue.push(value);
        }
      }
    }

    return fallbackAppId;
  }

  function getCurrentXboxGame() {
    const appId = getCurrentAppId();
    if (!appId || appId !== state.currentAppId || !state.currentGame) {
      return null;
    }
    return { ...state.currentGame, storeId: state.currentStoreId };
  }

  function isVisible(element) {
    if (!(element instanceof HTMLElement)) {
      return false;
    }
    const rect = element.getBoundingClientRect();
    const style = window.getComputedStyle(element);
    return rect.width > 120 && rect.height > 20 && style.display !== "none" && style.visibility !== "hidden";
  }

  function findPlaySection() {
    const themedPlaySection = document.querySelector(
      ".steamloader-theme-game-detail-playbar",
    );
    if (isVisible(themedPlaySection)) {
      return themedPlaySection;
    }

    const candidates = Array.from(document.querySelectorAll("div, section")).filter((element) => {
      const className = String(element.className || "").toLowerCase();
      return (
        className.includes("appdetailsplaysection_") ||
        className.includes("basicappdetailssectionstyler_playsection_")
      );
    });

    const nativePlaySection = candidates.find((element) =>
      isVisible(element) && element.querySelector("button, [role='button'], .Focusable"));
    if (nativePlaySection) {
      return nativePlaySection;
    }

    const primaryAction = Array.from(document.querySelectorAll("button, [role='button']"))
      .filter((element) => isVisible(element))
      .find((element) => /^(play|spielen|start|launch|resume|fortsetzen|install|download|preparing|downloading|reconnecting|finalizing|canceling|open xbox|open gog|queued|retry download|stop|cancel)(\s|$)/i
        .test(String(element.textContent || "").trim()));
    return primaryAction?.parentElement || null;
  }

  function findNativePlayButton(playSection) {
    const candidates = Array.from(playSection?.querySelectorAll?.("button, [role='button']") || [])
      .filter((element) => isVisible(element));
    const actionPattern = /^(play|spielen|start|launch|resume|fortsetzen|install|download|preparing|downloading|reconnecting|finalizing|canceling|open xbox|open gog|queued|retry download|update|stop|cancel)(\s|$)/i;
    const namedAction = candidates.find((element) =>
      actionPattern.test(String(element.textContent || "").trim()));
    if (namedAction) {
      return namedAction;
    }

    return candidates
      .filter((element) => {
        const rect = element.getBoundingClientRect();
        return rect.width >= 120 && rect.height >= 36;
      })
      .sort((left, right) => {
        const leftRect = left.getBoundingClientRect();
        const rightRect = right.getBoundingClientRect();
        return rightRect.width * rightRect.height - leftRect.width * leftRect.height;
      })[0] || null;
  }

  function findNativeActionLabel(button) {
    if (!button) {
      return null;
    }

    const labelPattern =
      /^(play|spielen|start|launch|resume|fortsetzen|install|download|update|stop|cancel|preparing|queued|retry download|resume download|open xbox|open gog|canceling|downloading(?:\s+.*)?|reconnecting(?:\s+.*)?|finalizing(?:\s+.*)?)$/i;
    const walker = document.createTreeWalker(
      button,
      window.NodeFilter?.SHOW_TEXT || 4,
    );
    let textNode = walker.nextNode();
    while (textNode) {
      if (labelPattern.test(String(textNode.nodeValue || "").trim())) {
        return textNode;
      }
      textNode = walker.nextNode();
    }

    return null;
  }

  function restoreManagedLabels(except = null) {
    for (const [textNode, record] of state.managedLabels) {
      if (textNode === except) {
        continue;
      }

      const originalLabel =
        typeof record === "string" ? record : record?.originalLabel;
      const appliedLabel =
        typeof record === "string" ? "" : record?.appliedLabel;
      if (
        textNode?.isConnected &&
        typeof originalLabel === "string" &&
        (!appliedLabel || textNode.nodeValue === appliedLabel) &&
        textNode.nodeValue !== originalLabel
      ) {
        textNode.nodeValue = originalLabel;
      }
      state.managedLabels.delete(textNode);
    }
  }

  function ensureDownloadStyle() {
    let style = document.getElementById(downloadStyleId);
    if (!style) {
      style = document.createElement("style");
      style.id = downloadStyleId;
      (document.head || document.documentElement).append(style);
    }
    const css = `
      @keyframes steamtools-omni-download-bob {
        0%, 100% { transform: translateY(-1px); opacity: 0.76; }
        50% { transform: translateY(3px); opacity: 1; }
      }
      @keyframes steamtools-omni-download-pulse {
        0%, 100% { transform: scale(0.94); opacity: 0.68; }
        50% { transform: scale(1.08); opacity: 1; }
      }
      [data-steamtools-omni-download-state] .steamtools-omni-download-icon {
        transform-origin: center;
        will-change: transform, opacity;
      }
      [data-steamtools-omni-download-state="downloading"] .steamtools-omni-download-icon {
        animation: steamtools-omni-download-bob 900ms ease-in-out infinite;
      }
      [data-steamtools-omni-download-state="preparing"] .steamtools-omni-download-icon,
      [data-steamtools-omni-download-state="queued"] .steamtools-omni-download-icon,
      [data-steamtools-omni-download-state="reconnecting"] .steamtools-omni-download-icon,
      [data-steamtools-omni-download-state="finalizing"] .steamtools-omni-download-icon,
      [data-steamtools-omni-download-state="canceling"] .steamtools-omni-download-icon {
        animation: steamtools-omni-download-pulse 1150ms ease-in-out infinite;
      }
      .steamtools-omni-download-message-host {
        position: relative !important;
      }
      #${downloadStatusId} {
        position: absolute;
        left: 2.4rem;
        bottom: calc(100% + 0.55rem);
        z-index: 120;
        display: grid;
        grid-template-columns: 0.72rem minmax(0, 1fr);
        column-gap: 0.72rem;
        align-items: start;
        width: min(54rem, calc(100vw - 5rem));
        box-sizing: border-box;
        padding: 0.72rem 0.95rem;
        border: 1px solid rgba(255, 255, 255, 0.11);
        border-radius: 0.48rem;
        background: linear-gradient(135deg, rgba(29, 35, 43, 0.96), rgba(20, 25, 32, 0.94));
        box-shadow: 0 0.45rem 1.25rem rgba(0, 0, 0, 0.34);
        color: rgba(244, 248, 252, 0.94);
        pointer-events: none;
      }
      #${downloadStatusId} .steamtools-omni-download-dot {
        width: 0.58rem;
        height: 0.58rem;
        margin-top: 0.28rem;
        border-radius: 50%;
        background: #58a6ff;
        box-shadow: 0 0 0.65rem rgba(88, 166, 255, 0.52);
      }
      #${downloadStatusId}[data-state="failed"] .steamtools-omni-download-dot {
        background: #e36d74;
        box-shadow: 0 0 0.65rem rgba(227, 109, 116, 0.48);
      }
      #${downloadStatusId}[data-state="paused"] .steamtools-omni-download-dot,
      #${downloadStatusId}[data-state="action-required"] .steamtools-omni-download-dot {
        background: #e8b65d;
        box-shadow: 0 0 0.65rem rgba(232, 182, 93, 0.48);
      }
      #${downloadStatusId}[data-active="true"] .steamtools-omni-download-dot {
        animation: steamtools-omni-download-pulse 1150ms ease-in-out infinite;
      }
      #${downloadStatusId} .steamtools-omni-download-title {
        display: block;
        overflow: hidden;
        font-size: 0.92rem;
        font-weight: 700;
        line-height: 1.25;
        letter-spacing: 0.035em;
        text-overflow: ellipsis;
        text-transform: uppercase;
        white-space: nowrap;
      }
      #${downloadStatusId} .steamtools-omni-download-detail {
        display: block;
        margin-top: 0.16rem;
        color: rgba(218, 226, 235, 0.82);
        font-size: 0.82rem;
        line-height: 1.3;
        overflow-wrap: anywhere;
        white-space: normal;
      }
      @media (prefers-reduced-motion: reduce) {
        .steamtools-omni-download-icon,
        #${downloadStatusId} .steamtools-omni-download-dot {
          animation: none !important;
        }
      }
    `;
    if (style.textContent !== css) {
      style.textContent = css;
    }
  }

  function clearNativeDownloadVisual(exceptButton = null) {
    for (const button of document.querySelectorAll(
      "[data-steamtools-omni-download-state]",
    )) {
      if (button === exceptButton) {
        continue;
      }
      button.removeAttribute("data-steamtools-omni-download-state");
      button.removeAttribute("aria-busy");
      for (const icon of button.querySelectorAll(".steamtools-omni-download-icon")) {
        icon.classList.remove("steamtools-omni-download-icon");
      }
    }
    if (!exceptButton) {
      document.getElementById(downloadStatusId)?.remove();
      for (const host of document.querySelectorAll(".steamtools-omni-download-message-host")) {
        host.classList.remove("steamtools-omni-download-message-host");
      }
    }
  }

  function formatBytes(value) {
    const bytes = Math.max(0, Number(value) || 0);
    if (bytes >= 1024 ** 3) {
      return `${(bytes / 1024 ** 3).toFixed(1)} GiB`;
    }
    if (bytes >= 1024 ** 2) {
      return `${(bytes / 1024 ** 2).toFixed(1)} MiB`;
    }
    return `${(bytes / 1024).toFixed(1)} KiB`;
  }

  function formatDuration(seconds) {
    const total = Math.max(0, Math.round(Number(seconds) || 0));
    if (total < 60) {
      return `${total}s remaining`;
    }
    const minutes = Math.floor(total / 60);
    if (minutes < 60) {
      return `${minutes}m remaining`;
    }
    const hours = Math.floor(minutes / 60);
    const remainingMinutes = minutes % 60;
    return `${hours}h ${remainingMinutes}m remaining`;
  }

  function getStoreTitle(storeId) {
    return storeId === "xbox-game-pass"
      ? "Xbox"
      : storeId === "epic-games"
        ? "Epic Games"
        : storeId === "gog-galaxy"
          ? "GOG"
          : storeId === "rom-library"
            ? "Emulator"
          : "OmniLibrary";
  }

  function getDownloadStageTitle(status, progress) {
    return status === "preparing"
      ? "Preparing download"
      : status === "queued"
        ? "Waiting in download queue"
        : status === "downloading"
          ? progress > 0 ? `Downloading · ${progress}%` : "Downloading"
          : status === "reconnecting"
            ? progress > 0 ? `Reconnecting · ${progress}%` : "Reconnecting"
            : status === "paused"
              ? progress > 0 ? `Download paused · ${progress}%` : "Download paused"
            : status === "finalizing"
              ? "Finalizing installation"
              : status === "canceling"
                ? "Removing partial files"
              : status === "action-required"
                  ? "Action required"
                  : status === "failed"
                    ? "Download failed"
                    : "";
  }

  function renderDownloadStatus(game, playSection, nativePlayButton) {
    const download = game?.download || {};
    const status = String(download.status || "idle").toLowerCase();
    const progress = Math.max(0, Math.min(99, Number(download.progressPercent) || 0));
    const visibleStatuses = new Set([
      "preparing",
      "queued",
      "downloading",
      "reconnecting",
      "paused",
      "finalizing",
      "canceling",
      "action-required",
      "failed",
      "cancel-failed",
    ]);
    if (!visibleStatuses.has(status)) {
      clearNativeDownloadVisual();
      return;
    }

    ensureDownloadStyle();
    clearNativeDownloadVisual(nativePlayButton);
    nativePlayButton.dataset.steamtoolsOmniDownloadState = status;
    if (["preparing", "queued", "downloading", "reconnecting", "finalizing", "canceling"]
      .includes(status)) {
      nativePlayButton.setAttribute("aria-busy", "true");
    } else {
      nativePlayButton.removeAttribute("aria-busy");
    }
    const icon = nativePlayButton.querySelector("svg");
    icon?.classList?.add("steamtools-omni-download-icon");

    let detail = String(download.detailText || "").trim();
    const downloadedBytes = Math.max(0, Number(download.downloadedBytes) || 0);
    const totalBytes = Math.max(0, Number(download.totalBytes) || 0);
    const speedBytes = Math.max(0, Number(download.downloadBytesPerSecond) || 0);
    const decompressedBytes = Math.max(
      0,
      Number(download.decompressedBytesPerSecond) || 0,
    );
    const diskWriteBytes = Math.max(
      0,
      Number(download.diskWriteBytesPerSecond) || 0,
    );
    const metrics = [];
    if (downloadedBytes > 0 && totalBytes > 0) {
      metrics.push(`${formatBytes(downloadedBytes)} / ${formatBytes(totalBytes)}`);
    }
    if (speedBytes > 0) {
      metrics.push(`Network ${formatBytes(speedBytes)}/s`);
      if (totalBytes > downloadedBytes) {
        metrics.push(formatDuration((totalBytes - downloadedBytes) / speedBytes));
      }
    }
    if (decompressedBytes > 0) {
      metrics.push(`Processing ${formatBytes(decompressedBytes)}/s`);
    }
    if (diskWriteBytes > 0) {
      metrics.push(`Disk ${formatBytes(diskWriteBytes)}/s`);
    }
    if (metrics.length) {
      detail = detail
        ? `${metrics.join(" · ")} — ${detail}`
        : metrics.join(" · ");
    }
    if (!detail) {
      detail = status === "reconnecting"
        ? "The connection stopped. OmniLibrary will resume automatically."
        : status === "failed"
          ? "The store reported a terminal error. Select Retry Download to try again."
          : "OmniLibrary is waiting for the store to report its next step.";
    }

    let banner = document.getElementById(downloadStatusId);
    if (!banner) {
      banner = document.createElement("div");
      banner.id = downloadStatusId;
      banner.setAttribute("aria-live", "polite");
      banner.setAttribute("aria-atomic", "true");
      playSection.append(banner);
    } else if (banner.parentElement !== playSection) {
      playSection.append(banner);
    }
    playSection.classList.add("steamtools-omni-download-message-host");
    banner.setAttribute(
      "role",
      ["failed", "cancel-failed", "action-required"].includes(status) ? "alert" : "status",
    );
    banner.dataset.state = status;
    banner.dataset.active = String(
      ["preparing", "queued", "downloading", "reconnecting", "finalizing", "canceling"]
        .includes(status),
    );
    const titleText =
      `${getStoreTitle(game.storeId)} · ${getDownloadStageTitle(status, progress)}`;
    const signature = `${status}\n${titleText}\n${detail}`;
    if (banner.dataset.signature !== signature) {
      banner.dataset.signature = signature;
      const dot = document.createElement("span");
      dot.className = "steamtools-omni-download-dot";
      const copy = document.createElement("span");
      const title = document.createElement("span");
      title.className = "steamtools-omni-download-title";
      title.textContent = titleText;
      const detailElement = document.createElement("span");
      detailElement.className = "steamtools-omni-download-detail";
      detailElement.textContent = detail;
      copy.append(title, detailElement);
      banner.replaceChildren(dot, copy);
    }
  }

  function installNativeDownloadFeedback(button) {
    if (!button || button.dataset.steamtoolsOmniDownloadListener === String(stateVersion)) {
      return;
    }

    button.dataset.steamtoolsOmniDownloadListener = String(stateVersion);
    button.addEventListener("click", (event) => {
      const game = getCurrentXboxGame();
      if (!game ||
          (game.installed && game.updateAvailable !== true) ||
          game.cloudPlayable) {
        return;
      }

      const currentStatus = String(game.download?.status || "idle").toLowerCase();
      if ([
        "preparing",
        "queued",
        "downloading",
        "reconnecting",
        "finalizing",
        "canceling",
      ].includes(currentStatus)) {
        // Steam sees the hidden launcher process as a running non-Steam game
        // and may bind this same native action to Stop. Once OmniLibrary has
        // relabelled it as download progress, activating it must not kill the
        // resumable worker behind the user's back.
        event.preventDefault();
        event.stopImmediatePropagation();
        return;
      }

      const storeTitle =
        game.requiresExternalLauncher === true &&
        String(game.providerDisplayName || "").trim()
          ? String(game.providerDisplayName).trim()
          : getStoreTitle(game.storeId);
      const detailText = currentStatus === "failed"
        ? `Retry requested. ${storeTitle} is checking the saved files before resuming.`
        : currentStatus === "paused"
          ? `${storeTitle} is resuming the saved download.`
          : currentStatus === "action-required"
            ? `Opening ${storeTitle} so the required installation step can be completed.`
            : game.installed && game.updateAvailable === true
              ? `${storeTitle} is preparing the update.`
              : `${storeTitle} is preparing the download.`;
      state.currentGame = {
        ...state.currentGame,
        download: {
          ...(state.currentGame?.download || {}),
          status: "preparing",
          detailText,
        },
      };
      try {
        state.channel?.postMessage?.({
          type: "download-status-changed",
          appId: getCurrentAppId(),
          status: "preparing",
        });
      } catch (_) {}
      render();

      if (state.timer) {
        window.clearTimeout(state.timer);
      }
      state.timer = window.setTimeout(() => {
        state.timer = 0;
        void refreshCurrentGame(true);
      }, 250);
    }, true);
  }

  function patchNativeDownloadLabel(game, playSection) {
    const downloadStatus = String(game?.download?.status || "idle").toLowerCase();
    if ((game.installed && game.updateAvailable !== true) || game.cloudPlayable) {
      restoreManagedLabels();
      clearNativeDownloadVisual();
      return;
    }

    const nativePlayButton = findNativePlayButton(playSection);
    const label = findNativeActionLabel(nativePlayButton);
    if (!label) {
      restoreManagedLabels();
      clearNativeDownloadVisual();
      return;
    }
    installNativeDownloadFeedback(nativePlayButton);

    restoreManagedLabels(label);
    if (!state.managedLabels.has(label)) {
      state.managedLabels.set(label, {
        originalLabel: label.nodeValue || "Play",
        appliedLabel: "",
      });
    }
    const progress = Math.max(0, Math.min(99, Number(game?.download?.progressPercent) || 0));
    const speedBytes = Math.max(0, Number(game?.download?.downloadBytesPerSecond) || 0);
    const speedLabel = speedBytes >= 1024 * 1024 * 1024
      ? `${(speedBytes / (1024 * 1024 * 1024)).toFixed(1)} GiB/s`
      : speedBytes >= 1024 * 1024
        ? `${(speedBytes / (1024 * 1024)).toFixed(1)} MiB/s`
        : speedBytes >= 1024
          ? `${(speedBytes / 1024).toFixed(1)} KiB/s`
          : "";
    const nextLabel = downloadStatus === "downloading"
      ? `${progress > 0 ? `Downloading ${progress}%` : "Downloading"}${speedLabel ? ` · ${speedLabel}` : ""}`
      : downloadStatus === "reconnecting"
        ? progress > 0 ? `Reconnecting ${progress}%` : "Reconnecting"
        : downloadStatus === "preparing"
          ? "Preparing"
          : downloadStatus === "queued"
            ? "Queued"
            : downloadStatus === "paused"
              ? "Resume Download"
              : downloadStatus === "finalizing"
                ? progress > 0 ? `Finalizing ${progress}%` : "Finalizing"
                : downloadStatus === "canceling"
                  ? "Canceling"
                : downloadStatus === "action-required"
                  ? game.externalAction === "install-client"
                    ? "Install EA app"
                    : game.externalAction === "link-account"
                      ? "Link EA"
                      : game.externalAction === "continue-provider"
                        ? "Open EA app"
                    : game.requiresAccountLink === true &&
                      /(?:link|account)/i.test(String(game.download?.detailText || ""))
                      ? game.deliveryProvider === "ea-app"
                        ? "Link EA"
                        : "Link Ubisoft"
                    : game.storeId === "xbox-game-pass"
                    ? "Open Xbox"
                    : game.storeId === "gog-galaxy"
                      ? "Open GOG"
                      : `Open ${String(game.providerDisplayName || "Store")}`
                  : ["failed", "cancel-failed"].includes(downloadStatus)
                    ? game.requiresExternalLauncher === true
                      ? `Retry ${String(game.providerDisplayName || "Store")}`
                      : "Retry Download"
                    : game.installed && game.updateAvailable === true
                      ? "Update"
                      : game.externalAction === "install-client"
                        ? "Install EA app"
                        : game.externalAction === "link-account"
                          ? "Link EA"
                          : game.externalAction === "continue-provider"
                            ? "Open EA app"
                            : "Download";
    const managedLabel = state.managedLabels.get(label);
    if (managedLabel && typeof managedLabel === "object") {
      managedLabel.appliedLabel = nextLabel;
    }
    if (label.nodeValue !== nextLabel) {
      label.nodeValue = nextLabel;
    }
    renderDownloadStatus(game, playSection, nativePlayButton);
  }

  function removeLegacyUninstallUi() {
    document.getElementById(uninstallActionId)?.remove();
    document.getElementById(uninstallDialogId)?.remove();
    document.getElementById(styleId)?.remove();
  }

  function render() {
    try {
      const game = getCurrentXboxGame();
      if (!game) {
        restoreManagedLabels();
        clearNativeDownloadVisual();
        removeLegacyUninstallUi();
        return;
      }

      const playSection = findPlaySection();
      if (!playSection) {
        return;
      }

      patchNativeDownloadLabel(game, playSection);
      removeLegacyUninstallUi();
      state.lastRenderError = "";
    } catch (error) {
      state.lastRenderError = error instanceof Error
        ? `${error.name}: ${error.message}`
        : String(error);
      console.warn("OmniLibrary game-detail surface could not be rendered.", error);
    }
  }

  async function refreshSummary(force = false) {
    const shared = window.__steamLoaderOmniLibraryStateStore;
    try {
      if (shared?.refresh) {
        state.summary = await shared.refresh(force);
        return state.summary;
      }

      const response = await fetch(`${apiBase}api/unifystore/summary`, { cache: "no-store" });
      if (!response.ok) {
        throw new Error(`OmniLibrary summary failed (${response.status}).`);
      }
      state.summary = await response.json();
      return state.summary;
    } catch (_) {
      return state.summary;
    }
  }

  function clearCurrentGame() {
    state.currentAppId = 0;
    state.currentStoreId = "";
    state.currentGame = null;
    state.lastGameFetchAt = 0;
    restoreManagedLabels();
    clearNativeDownloadVisual();
    removeLegacyUninstallUi();
  }

  function isOmniLibraryEnabled() {
    return state.summary?.pluginEnabled === true;
  }

  function deactivateSurface() {
    if (state.timer) {
      window.clearTimeout(state.timer);
      state.timer = 0;
    }
    if (state.mutationTimer) {
      window.clearTimeout(state.mutationTimer);
      state.mutationTimer = 0;
    }
    state.observer?.disconnect?.();
    state.observer = null;
    clearCurrentGame();
    document.getElementById(styleId)?.remove();
    document.getElementById(downloadStyleId)?.remove();
  }

  function ensureSurfaceObserver() {
    if (!isOmniLibraryEnabled() || state.observer) {
      return;
    }
    state.observer = new MutationObserver(() => scheduleSurfaceRefresh());
    state.observer.observe(
      document.getElementById("GamepadUI_Full_Root") || document.body,
      { childList: true, subtree: true },
    );
  }

  function scheduleNextRefresh() {
    if (!isOmniLibraryEnabled()) {
      deactivateSurface();
      return;
    }
    if (state.timer) {
      window.clearTimeout(state.timer);
    }
    const downloadStatus = String(state.currentGame?.download?.status || "").toLowerCase();
    const activeDownload = downloadStatus === "preparing" ||
      downloadStatus === "queued" ||
      downloadStatus === "downloading" ||
      downloadStatus === "reconnecting" ||
      downloadStatus === "finalizing" ||
      downloadStatus === "canceling" ||
      downloadStatus === "uninstalling" ||
      downloadStatus === "uninstall-action-required";
    const detectedAppId = getCurrentAppId();
    const managedSurface = isKnownXboxAppId(detectedAppId);
    const delay = activeDownload
      ? activeDownloadRefreshIntervalMs
      : state.currentGame
        ? managedGameRefreshIntervalMs
        : managedSurface
          ? 2000
          : idleRefreshIntervalMs;
    state.timer = window.setTimeout(async () => {
      state.timer = 0;
      const currentSurfaceIsManaged = isKnownXboxAppId(getCurrentAppId());
      if (
        document.visibilityState !== "hidden" ||
        state.currentGame ||
        currentSurfaceIsManaged
      ) {
        await refreshSummary(false);
        await refreshCurrentGame(activeDownload || currentSurfaceIsManaged);
      } else {
        scheduleNextRefresh();
      }
    }, delay);
  }

  async function refreshCurrentGame(force = false) {
    if (state.requestInFlight) {
      return;
    }
    if (!isOmniLibraryEnabled()) {
      deactivateSurface();
      return;
    }

    const appId = getCurrentAppId();
    if (!appId || !isKnownXboxAppId(appId)) {
      clearCurrentGame();
      scheduleNextRefresh();
      return;
    }
    if (
      !force &&
      appId === state.currentAppId &&
      state.currentGame &&
      Date.now() - state.lastGameFetchAt < managedGameRefreshIntervalMs
    ) {
      render();
      scheduleNextRefresh();
      return;
    }

    state.requestInFlight = true;
    try {
      const response = await fetch(
        `${apiBase}api/unifystore/games/${encodeURIComponent(appId)}`,
        { cache: "no-store" },
      );
      if (!response.ok) {
        throw new Error(`OmniLibrary game state failed (${response.status}).`);
      }
      const payload = await response.json();
      const payloadStoreId = String(payload?.storeId || "");
      const payloadGame = payload?.game || null;
      const payloadAppId = Number(payloadGame?.steamAppId || 0);
      if (
        !payloadStoreId ||
        !payloadGame ||
        !isManagedShortcutAppId(payloadAppId) ||
        payloadAppId !== appId
      ) {
        throw new Error("The current page is not an OmniLibrary-managed shortcut.");
      }
      state.currentAppId = appId;
      state.currentStoreId = payloadStoreId;
      state.currentGame = payloadGame;
      state.lastGameFetchAt = Date.now();
      render();
    } catch (_) {
      if (state.currentGame && state.currentAppId === appId) {
        render();
      } else {
        clearCurrentGame();
      }
    } finally {
      state.requestInFlight = false;
      scheduleNextRefresh();
    }
  }

  function scheduleSurfaceRefresh(force = false) {
    if (!isOmniLibraryEnabled()) {
      deactivateSurface();
      return;
    }
    if (state.mutationTimer) {
      return;
    }
    const elapsed = Date.now() - Number(state.lastMutationRefreshAt || 0);
    const delay = force ? 0 : Math.max(120, 400 - elapsed);
    state.mutationTimer = window.setTimeout(() => {
      state.mutationTimer = 0;
      state.lastMutationRefreshAt = Date.now();
      const appId = getCurrentAppId();
      if (force || appId !== state.currentAppId) {
        void refreshCurrentGame(true);
      } else {
        render();
      }
    }, delay);
  }

  state.restoreManagedLabels = restoreManagedLabels;
  if (!state.channel && typeof window.BroadcastChannel === "function") {
    try {
      state.channel = new window.BroadcastChannel(omniLibraryStoreChannelName);
      state.channel.addEventListener("message", (event) => {
        if (event?.data?.type === "stores-changed") {
          void refreshSummary(true).then(() => {
            if (!isOmniLibraryEnabled()) {
              deactivateSurface();
              return;
            }
            ensureSurfaceObserver();
            return refreshCurrentGame(true);
          });
        }
      });
    } catch (_) {
      state.channel = null;
    }
  }
  if (!state.storageHandler) {
    state.storageHandler = (event) => {
      if (event?.key === omniLibraryStoreStorageKey) {
        void refreshSummary(true).then(() => {
          if (!isOmniLibraryEnabled()) {
            deactivateSurface();
            return;
          }
          ensureSurfaceObserver();
          return refreshCurrentGame(true);
        });
      }
    };
    window.addEventListener("storage", state.storageHandler);
  }
  if (!state.focusHandler) {
    state.focusHandler = () => scheduleSurfaceRefresh(true);
    window.addEventListener("focus", state.focusHandler);
  }
  const sharedStateStore = window.__steamLoaderOmniLibraryStateStore;
  if (!state.omniLibraryStateUnsubscribe && sharedStateStore?.subscribe) {
    state.omniLibraryStateUnsubscribe = sharedStateStore.subscribe((summary) => {
      state.summary = summary;
      if (!isOmniLibraryEnabled()) {
        deactivateSurface();
        return;
      }
      ensureSurfaceObserver();
      scheduleSurfaceRefresh();
    });
  }
  state.observer?.disconnect?.();
  state.observer = null;
  if (state.timer) {
    window.clearTimeout(state.timer);
  }
  void refreshSummary(true).then(() => {
    if (!isOmniLibraryEnabled()) {
      deactivateSurface();
      return;
    }
    ensureSurfaceObserver();
    return refreshCurrentGame(true);
  });
})();
