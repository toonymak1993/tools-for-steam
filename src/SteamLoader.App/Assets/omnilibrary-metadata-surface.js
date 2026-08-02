// Tools for Steam - native OmniLibrary metadata bridge.
//
// The game details page belongs to Steam. This integration only supplies the
// missing data through Steam's own stores so its native tabs, focus tree,
// controller hints and layouts remain untouched.
(() => {
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";
  const stateVersion = 23;
  const legacyPanelId = "steamtools-omnilibrary-metadata-panel";
  const legacyStyleId = "steamtools-omnilibrary-metadata-style";
  const achievementNoticeId =
    "steamtools-omnilibrary-achievement-unavailable";
  const storageKey = "ToolsForSteamOmniLibraryStoresChanged";
  const channelName = "ToolsForSteamOmniLibraryStores";
  const nativeMetadataMessageType = "native-metadata";
  const readyRefreshMs = 60000;
  const updatingRefreshMs = 2500;
  const postPlayMinimumMs = 45000;
  const postPlayDelayMs = 8000;
  const postPlayThrottleMs = 10 * 60 * 1000;
  const postPlayFallbackPollMs = 10000;
  const routePattern =
    /\/(?:library\/(?:[^/?#]+\/)?app|library\/details|appdetails)\/(\d+)/i;
  const artworkAppIdPatterns = Object.freeze([
    /\/customimages\/(\d+)(?:_|\/)/i,
    /\/images\/apps\/(\d+)\//i,
    /\/libraryassets\/(\d+)\//i,
    /\/apps\/(\d+)\//i,
  ]);
  const detailTabLabels = Object.freeze([
    "ACTIVITY",
    "AKTIVITÄT",
    "ACTIVITÉ",
    "ATTIVITÀ",
    "ACTIVIDAD",
    "ATIVIDADE",
    "YOUR STUFF",
    "IHRE DINGE",
    "DEINE INHALTE",
    "VOS CONTENUS",
    "I TUOI CONTENUTI",
    "TUS COSAS",
    "COMMUNITY",
    "COMMUNITY-INHALTE",
    "COMMUNAUTÉ",
    "COMUNITÀ",
    "COMUNIDAD",
    "GAME INFO",
    "SPIELINFO",
    "SPIELINFORMATIONEN",
    "INFOS DU JEU",
    "INFORMAZIONI SUL GIOCO",
  ]);
  const featureCategories = Object.freeze({
    "MULTIPLAYER": 1,
    "SINGLE-PLAYER": 2,
    "SINGLE PLAYER": 2,
    "CO-OP": 9,
    "COOP": 9,
    "ACHIEVEMENTS": 22,
    "SPLIT SCREEN": 24,
    "FULL CONTROLLER SUPPORT": 28,
    "CONTROLLER SUPPORT": 28,
    "ONLINE MULTIPLAYER": 36,
    "LOCAL MULTIPLAYER": 37,
    "ONLINE CO-OP": 38,
    "LOCAL CO-OP": 392,
  });

  const previous = window.__steamToolsOmniLibraryMetadataState;

  function removeLegacySurface() {
    document.getElementById(legacyPanelId)?.remove();
    document.getElementById(legacyStyleId)?.remove();
    for (const element of document.querySelectorAll(
      "[data-steamtools-omni-native-content]",
    )) {
      element.removeAttribute("data-steamtools-omni-native-content");
    }
  }

  if (previous?.version !== stateVersion) {
    try {
      previous?.dispose?.();
    } catch (_) {}
    previous?.observer?.disconnect?.();
    if (previous?.timer) {
      window.clearTimeout(previous.timer);
    }
    if (previous?.mutationTimer) {
      window.clearTimeout(previous.mutationTimer);
    }
    if (typeof previous?.clickHandler === "function") {
      document.removeEventListener("click", previous.clickHandler, true);
    }
    if (typeof previous?.keyHandler === "function") {
      document.removeEventListener("keydown", previous.keyHandler, true);
      window.removeEventListener("keydown", previous.keyHandler, true);
    }
    if (typeof previous?.storageHandler === "function") {
      window.removeEventListener("storage", previous.storageHandler);
    }
    if (typeof previous?.focusHandler === "function") {
      window.removeEventListener("focus", previous.focusHandler);
    }
    try {
      previous?.summaryUnsubscribe?.();
    } catch (_) {}
    try {
      previous?.channel?.close?.();
    } catch (_) {}
  }

  removeLegacySurface();

  if (previous?.version === stateVersion) {
    previous.ensureActive?.();
    return;
  }

  const state = {
    version: stateVersion,
    summary: null,
    currentAppId: 0,
    requestInFlight: false,
    refreshPending: false,
    forceRefreshPending: false,
    timer: 0,
    mutationTimer: 0,
    metadataUiTimer: 0,
    patchRetryTimer: 0,
    postPlayPollTimer: 0,
    postPlayInitialTimer: 0,
    postPlayUnregister: null,
    postPlayRunning: new Map(),
    postPlayPending: new Map(),
    postPlayLastRefreshAt: new Map(),
    observer: null,
    storageHandler: null,
    focusHandler: null,
    detailsUiHandler: null,
    summaryUnsubscribe: null,
    channel: null,
    nativeSnapshots: new Map(),
    nativeActivity: new Map(),
    nativeAchievements: new Map(),
    achievementStorePayloads: new Map(),
    achievementPrimedStores: new WeakMap(),
    appliedSnapshotByApp: new Map(),
    lastFetchAtByApp: new Map(),
    originalByApp: new Map(),
    snapshotWaiters: new Map(),
    unpatchers: [],
    patchedDetailsStore: null,
    patchedAppStore: null,
    patchedActivityStore: null,
    patchedAchievementStore: null,
    patchedAjaxRequest: null,
    patchedSectionsPrototype: null,
    disposed: false,
    ensureActive: null,
    dispose: null,
  };
  window.__steamToolsOmniLibraryMetadataState = state;

  function normalizeText(value) {
    return String(value || "")
      .normalize("NFKD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/\s+/g, " ")
      .trim()
      .toUpperCase();
  }

  function isPluginEnabled() {
    return state.summary?.pluginEnabled === true;
  }

  function managedAppIds() {
    const ids = new Set();
    for (const store of state.summary?.stores || []) {
      if (store?.enabled !== true || store?.readyForLibraryTab !== true) {
        continue;
      }
      for (const candidate of store?.appIds || []) {
        const appId = Number(candidate);
        if (Number.isInteger(appId) && appId >= 0x80000000) {
          ids.add(appId);
        }
      }
    }
    return ids;
  }

  function isManagedAppId(value) {
    const appId = Number(value);
    return Number.isInteger(appId) &&
      appId >= 0x80000000 &&
      managedAppIds().has(appId);
  }

  function notificationAppId(notification) {
    return Number(
      notification?.unAppID ??
        notification?.unAppId ??
        notification?.appid ??
        notification?.nAppID ??
        0,
    );
  }

  function boolLike(value) {
    if (typeof value === "boolean") {
      return value;
    }
    if (typeof value === "number") {
      return value !== 0;
    }
    if (typeof value === "string") {
      const normalized = value.trim().toLowerCase();
      if (["true", "running", "1", "yes"].includes(normalized)) {
        return true;
      }
      if (["false", "stopped", "0", "no"].includes(normalized)) {
        return false;
      }
    }
    return undefined;
  }

  function readOverviewRunning(overview) {
    if (!overview) {
      return undefined;
    }
    for (const methodName of [
      "BIsRunning",
      "BIsAppRunning",
      "BIsPlaying",
      "IsRunning",
    ]) {
      try {
        if (typeof overview?.[methodName] === "function") {
          const result = boolLike(overview[methodName]());
          if (typeof result === "boolean") {
            return result;
          }
        }
      } catch (_) {}
    }
    for (const fieldName of [
      "bRunning",
      "m_bRunning",
      "isRunning",
      "running",
      "bIsRunning",
      "m_bIsRunning",
      "bPlaying",
    ]) {
      if (fieldName in overview) {
        const result = boolLike(overview[fieldName]);
        if (typeof result === "boolean") {
          return result;
        }
      }
    }
    return undefined;
  }

  async function refreshMetadataForApp(appId) {
    if (!isManagedAppId(appId) || state.disposed) {
      return;
    }
    try {
      const response = await fetch(
        `${apiBase}api/unifystore/metadata/games/${encodeURIComponent(appId)}`,
        {
          method: "POST",
          cache: "no-store",
        },
      );
      if (!response.ok) {
        return;
      }
      const snapshot = await response.json();
      if (
        Number(snapshot?.steamAppId || 0) !== appId ||
        !isManagedAppId(appId)
      ) {
        return;
      }
      state.nativeSnapshots.set(appId, snapshot);
      state.lastFetchAtByApp.set(appId, Date.now());
      applyNativeSnapshot(snapshot);
      broadcastNativeSnapshot(snapshot);
      if (state.currentAppId === appId) {
        scheduleNextRefresh(snapshot);
      }
    } catch (_) {
      // Provider backoff and last-good preservation are handled by the backend.
    }
  }

  function queuePostPlayRefresh(appId) {
    if (!isManagedAppId(appId) || state.postPlayPending.has(appId)) {
      return;
    }
    const lastRefresh = Number(state.postPlayLastRefreshAt.get(appId) || 0);
    if (Date.now() - lastRefresh < postPlayThrottleMs) {
      return;
    }
    const timer = window.setTimeout(async () => {
      state.postPlayPending.delete(appId);
      if (!isManagedAppId(appId) || state.disposed) {
        return;
      }
      state.postPlayLastRefreshAt.set(appId, Date.now());
      await refreshMetadataForApp(appId);
    }, postPlayDelayMs);
    state.postPlayPending.set(appId, timer);
  }

  function observeAppLifetime(appId, running) {
    if (!isManagedAppId(appId) || typeof running !== "boolean") {
      return;
    }
    const now = Date.now();
    const previous = state.postPlayRunning.get(appId);
    if (running) {
      state.postPlayRunning.set(appId, {
        running: true,
        startedAt: previous?.running ? previous.startedAt || now : now,
      });
      return;
    }
    state.postPlayRunning.delete(appId);
    if (!previous?.running) {
      return;
    }
    const playedMs = previous.startedAt
      ? now - previous.startedAt
      : Number.POSITIVE_INFINITY;
    if (playedMs >= postPlayMinimumMs) {
      queuePostPlayRefresh(appId);
    }
  }

  function handleAppLifetimeNotification(notification) {
    const appId = notificationAppId(notification);
    const running = boolLike(
      notification?.bRunning ??
        notification?.running ??
        notification?.isRunning,
    );
    observeAppLifetime(appId, running);
  }

  function pollManagedRunningApps() {
    if (!isPluginEnabled() || state.disposed) {
      state.postPlayRunning.clear();
      return;
    }
    const observed = new Set();
    for (const appId of managedAppIds()) {
      const overview = window.appStore?.GetAppOverviewByAppID?.(appId);
      const running = readOverviewRunning(overview);
      if (typeof running !== "boolean") {
        continue;
      }
      observed.add(appId);
      observeAppLifetime(appId, running);
    }
    for (const appId of state.postPlayRunning.keys()) {
      if (!observed.has(appId)) {
        state.postPlayRunning.delete(appId);
      }
    }
  }

  function installPostPlaySync() {
    if (
      state.postPlayUnregister ||
      state.postPlayPollTimer ||
      state.disposed ||
      !isPluginEnabled()
    ) {
      return;
    }
    try {
      const registration =
        window.SteamClient?.GameSessions?.RegisterForAppLifetimeNotifications?.(
          handleAppLifetimeNotification,
        );
      if (typeof registration?.unregister === "function") {
        state.postPlayUnregister = () => registration.unregister();
        return;
      }
      if (typeof registration === "function") {
        state.postPlayUnregister = registration;
        return;
      }
    } catch (_) {}
    state.postPlayInitialTimer = window.setTimeout(() => {
      state.postPlayInitialTimer = 0;
      pollManagedRunningApps();
    }, 1500);
    state.postPlayPollTimer = window.setInterval(
      pollManagedRunningApps,
      postPlayFallbackPollMs,
    );
  }

  function uninstallPostPlaySync() {
    try {
      state.postPlayUnregister?.();
    } catch (_) {}
    state.postPlayUnregister = null;
    if (state.postPlayPollTimer) {
      window.clearInterval(state.postPlayPollTimer);
      state.postPlayPollTimer = 0;
    }
    if (state.postPlayInitialTimer) {
      window.clearTimeout(state.postPlayInitialTimer);
      state.postPlayInitialTimer = 0;
    }
    for (const timer of state.postPlayPending.values()) {
      window.clearTimeout(timer);
    }
    state.postPlayPending.clear();
    state.postPlayRunning.clear();
  }

  function routeAppId() {
    const route =
      `${window.location.pathname || ""}${window.location.hash || ""}${window.location.search || ""}`;
    const match = route.match(routePattern);
    const appId = Number(match?.[1] || 0);
    return Number.isInteger(appId) && appId >= 0x80000000 ? appId : 0;
  }

  function artworkAppId() {
    const candidates = [];
    for (const element of document.querySelectorAll(
      [
        "img[src*='/customimages/']",
        "img[src*='/images/apps/']",
        "img[src*='/libraryassets/']",
        "img[src*='/apps/']",
        "video[poster]",
        "[style*='/customimages/']",
        "[style*='/libraryassets/']",
      ].join(","),
    )) {
      if (!(element instanceof HTMLElement)) {
        continue;
      }
      const signal = [
        element.getAttribute("src"),
        element.getAttribute("poster"),
        element.getAttribute("style"),
      ].filter(Boolean).join(" ");
      let appId = 0;
      for (const pattern of artworkAppIdPatterns) {
        const match = signal.match(pattern);
        if (match) {
          appId = Number(match[1]);
          break;
        }
      }
      if (!isManagedAppId(appId)) {
        continue;
      }
      const rect = element.getBoundingClientRect();
      if (rect.width < 100 || rect.height < 40) {
        continue;
      }
      const lowerSignal = signal.toLowerCase();
      const score =
        Math.min(80, Math.round((rect.width * rect.height) / 12000)) +
        (lowerSignal.includes("_hero.") ? 120 : 0) +
        (lowerSignal.includes("_logo.") ? 60 : 0) +
        (rect.top < window.innerHeight ? 20 : 0);
      candidates.push({ appId, score });
    }
    return candidates.sort((left, right) => right.score - left.score)[0]?.appId || 0;
  }

  function hasNativeDetailTabs() {
    const labels = new Set(detailTabLabels.map(normalizeText));
    let found = 0;
    for (const element of document.querySelectorAll(
      "[role='tab'], button, [role='button'], .Focusable",
    )) {
      if (!(element instanceof HTMLElement)) {
        continue;
      }
      if (!labels.has(normalizeText(element.textContent))) {
        continue;
      }
      const rect = element.getBoundingClientRect();
      const style = window.getComputedStyle(element);
      if (
        rect.width > 20 &&
        rect.height > 12 &&
        style.display !== "none" &&
        style.visibility !== "hidden"
      ) {
        found += 1;
      }
    }
    return found >= 3;
  }

  function currentManagedAppId() {
    const detected = routeAppId() || artworkAppId();
    if (isManagedAppId(detected)) {
      return detected;
    }
    if (isManagedAppId(state.currentAppId) && hasNativeDetailTabs()) {
      return state.currentAppId;
    }
    return 0;
  }

  function metadataAppIdForRequest(value) {
    const requestedAppId = Number(value);
    if (isManagedAppId(requestedAppId)) {
      return requestedAppId;
    }
    // SteamUI sometimes asks the details stores for the matched Steam app
    // instead of the managed shortcut. Reject unrelated Steam app requests
    // before touching the DOM; these getters are hot on normal Steam pages.
    const trackedAppId = Number(state.currentAppId || 0);
    const trackedSnapshot = state.nativeSnapshots.get(trackedAppId);
    if (
      Number(trackedSnapshot?.sourceSteamAppId || 0) !== requestedAppId ||
      !isManagedAppId(trackedAppId)
    ) {
      return 0;
    }
    return currentManagedAppId() === trackedAppId ? trackedAppId : 0;
  }

  function removeAchievementNotice() {
    document.getElementById(achievementNoticeId)?.remove();
  }

  function isVisibleElement(element) {
    if (!(element instanceof HTMLElement)) {
      return false;
    }
    const rect = element.getBoundingClientRect();
    const style = window.getComputedStyle(element);
    return rect.width > 20 &&
      rect.height > 10 &&
      style.display !== "none" &&
      style.visibility !== "hidden";
  }

  function findVisibleHeading(labels) {
    const wanted = new Set(labels.map(normalizeText));
    const selectors = [
      "[role='heading']",
      "[class*='SectionTitle']",
      "[class*='sectiontitle']",
      "h1",
      "h2",
      "h3",
      "h4",
      "h5",
      "h6",
    ].join(",");
    for (const element of document.querySelectorAll(selectors)) {
      if (
        !element.closest?.(`#${achievementNoticeId}`) &&
        wanted.has(normalizeText(element.textContent)) &&
        isVisibleElement(element)
      ) {
        return element;
      }
    }
    return null;
  }

  function isYourStuffVisible() {
    const route =
      `${window.location.pathname || ""}${window.location.hash || ""}`
        .toLowerCase();
    if (/\/(?:yourstuff|achievements)(?:\/|$)/.test(route)) {
      return true;
    }
    for (const element of document.querySelectorAll(
      "[role='tab'], button, [role='button']",
    )) {
      if (
        normalizeText(element.textContent) !== "YOUR STUFF" ||
        !isVisibleElement(element)
      ) {
        continue;
      }
      const className = String(element.className || "").toLowerCase();
      if (
        element.getAttribute("aria-selected") === "true" ||
        element.getAttribute("data-state") === "active" ||
        className.includes("active") ||
        className.includes("selected")
      ) {
        return true;
      }
    }
    return Boolean(
      findVisibleHeading(["MEDIA"]) ||
      findVisibleHeading(["NOTES"]),
    );
  }

  function achievementUnavailableDetail(snapshot) {
    const status = String(snapshot?.achievements?.status || "").toLowerCase();
    const providerDetail = String(
      snapshot?.achievements?.detailText || snapshot?.warning || "",
    ).trim();
    if (providerDetail) {
      return providerDetail;
    }
    if (status === "disabled") {
      return "Achievement access is disabled for this store.";
    }
    if (status === "not-connected" || status === "not-configured") {
      return "Connect and configure this store in OmniLibrary settings to retry.";
    }
    if (status === "mapping-unavailable") {
      return "The store did not provide a reliable achievement identity for this title.";
    }
    if (status === "provider-required") {
      return "This store does not provide a verified, user-scoped achievement source.";
    }
    if (status === "unsupported-rom") {
      return "RetroAchievements does not recognize this exact ROM revision. The game can still launch normally.";
    }
    if (status === "authentication-required" || status === "setup-required") {
      return "Check the provider username and API credential in OmniLibrary settings.";
    }
    if (status === "no-achievements") {
      return "The provider has no achievement set for this exact title.";
    }
    if (status === "mapping-required") {
      return "OmniLibrary could not map this game to an exact provider identity without guessing.";
    }
    return "The connected store does not expose an achievement set for this title.";
  }

  function achievementUnavailableTitle(snapshot) {
    switch (String(snapshot?.achievements?.status || "").toLowerCase()) {
      case "unsupported-rom":
        return "Unsupported ROM revision";
      case "authentication-required":
      case "setup-required":
      case "not-configured":
        return "Provider setup required";
      case "not-connected":
        return "Store connection required";
      case "mapping-required":
      case "mapping-unavailable":
        return "Game identity unavailable";
      case "no-achievements":
        return "No achievements for this game";
      case "disabled":
        return "Achievements disabled";
      default:
        return "Achievements unavailable";
    }
  }

  async function openMetadataUrl(url) {
    try {
      await fetch(`${apiBase}api/unifystore/metadata/open`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ value: url }),
      });
    } catch (_) {}
  }

  function createAchievementNoticeAction(label, onSelected) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.setAttribute("data-focusable", "true");
    Object.assign(button.style, {
      minHeight: "38px",
      padding: "0 18px",
      border: "0",
      borderRadius: "3px",
      background: "rgba(92,104,124,0.92)",
      color: "white",
      fontSize: "14px",
      fontWeight: "700",
      cursor: "pointer",
    });
    const activate = (event) => {
      event?.preventDefault?.();
      event?.stopPropagation?.();
      onSelected();
    };
    button.addEventListener("click", activate);
    button.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " " || event.key === "GamepadA") {
        activate(event);
      }
    });
    return button;
  }

  function createLoadingDots() {
    const dots = document.createElement("span");
    dots.setAttribute("aria-hidden", "true");
    Object.assign(dots.style, {
      display: "inline-flex",
      alignItems: "center",
      gap: "5px",
      marginLeft: "10px",
      minWidth: "31px",
    });
    const reducedMotion = window.matchMedia?.(
      "(prefers-reduced-motion: reduce)",
    )?.matches === true;
    for (let index = 0; index < 3; index += 1) {
      const dot = document.createElement("span");
      Object.assign(dot.style, {
        width: "6px",
        height: "6px",
        borderRadius: "50%",
        background: "rgba(255,255,255,0.88)",
        opacity: reducedMotion ? "0.72" : "0.28",
      });
      if (!reducedMotion && typeof dot.animate === "function") {
        dot.animate(
          [
            { opacity: 0.28, transform: "translateY(0)" },
            { opacity: 1, transform: "translateY(-2px)" },
            { opacity: 0.28, transform: "translateY(0)" },
          ],
          {
            duration: 900,
            delay: index * 140,
            iterations: Number.POSITIVE_INFINITY,
            easing: "ease-in-out",
          },
        );
      }
      dots.append(dot);
    }
    return dots;
  }

  function createAchievementNotice(snapshot, appId, mode) {
    const section = document.createElement("section");
    section.id = achievementNoticeId;
    section.setAttribute("role", "status");
    section.setAttribute("aria-live", "polite");
    section.dataset.appId = String(snapshot?.steamAppId || appId || 0);
    section.dataset.status = mode === "loading"
      ? "loading"
      : String(snapshot?.achievements?.status || "");
    section.dataset.mode = mode;
    Object.assign(section.style, {
      margin: "12px 0 24px",
      color: "rgba(255,255,255,0.96)",
    });

    const heading = document.createElement("div");
    heading.textContent = "Achievements";
    Object.assign(heading.style, {
      marginBottom: "10px",
      fontSize: "20px",
      fontWeight: "700",
      lineHeight: "1.2",
    });

    const card = document.createElement("div");
    Object.assign(card.style, {
      padding: "18px 22px",
      border: "1px solid rgba(255,255,255,0.08)",
      borderRadius: "5px",
      background: "rgba(59,66,79,0.72)",
      boxShadow: "0 2px 8px rgba(0,0,0,0.18)",
    });

    const title = document.createElement("div");
    Object.assign(title.style, {
      display: "flex",
      alignItems: "center",
      marginBottom: "7px",
      fontSize: "18px",
      fontWeight: "700",
      lineHeight: "1.25",
    });

    const message = document.createElement("div");
    Object.assign(message.style, {
      fontSize: "15px",
      lineHeight: "1.4",
      color: "rgba(255,255,255,0.78)",
    });

    if (mode === "loading") {
      const titleText = document.createElement("span");
      titleText.textContent = "OmniLibrary is preparing this game";
      title.append(titleText, createLoadingDots());
      message.textContent =
        "Loading metadata and checking for verified achievements.";
      card.append(title, message);
      section.append(heading, card);
      return section;
    }

    title.textContent = achievementUnavailableTitle(snapshot);
    message.textContent =
      "OmniLibrary cannot access verified achievements for this game.";

    const detail = document.createElement("div");
    detail.textContent = achievementUnavailableDetail(snapshot);
    Object.assign(detail.style, {
      marginTop: "5px",
      fontSize: "14px",
      lineHeight: "1.35",
      color: "rgba(255,255,255,0.58)",
    });

    const actions = document.createElement("div");
    Object.assign(actions.style, {
      display: "flex",
      gap: "10px",
      flexWrap: "wrap",
      marginTop: "14px",
    });
    actions.append(
      createAchievementNoticeAction("Retry", () => {
        void refreshCurrentMetadata(true);
      }),
    );
    if (String(snapshot?.achievements?.provider || "").toLowerCase() === "retroachievements") {
      actions.append(
        createAchievementNoticeAction("Open RetroAchievements", () => {
          void openMetadataUrl("https://retroachievements.org/gameList.php");
        }),
      );
    }

    card.append(title, message, detail, actions);
    section.append(heading, card);
    return section;
  }

  function renderAchievementNotice() {
    const appId = currentManagedAppId();
    const snapshot = state.nativeSnapshots.get(appId);
    const achievements = snapshot?.achievements;
    const firstLoad =
      !snapshot &&
      state.requestInFlight &&
      Number(state.currentAppId || 0) === appId;
    const achievementStatus = String(
      achievements?.status || "",
    ).toLowerCase();
    const providerLoad =
      achievements &&
      (achievements.items || []).length === 0 &&
      ["loading", "updating"].includes(achievementStatus);
    const loading = firstLoad || providerLoad;
    const unavailable =
      achievements &&
      (achievements.items || []).length === 0 &&
      !loading;
    const shouldShow =
      isPluginEnabled() &&
      isManagedAppId(appId) &&
      isYourStuffVisible() &&
      (loading || unavailable);
    if (!shouldShow) {
      removeAchievementNotice();
      return;
    }

    const mode = loading ? "loading" : "unavailable";
    const renderedStatus = loading
      ? "loading"
      : String(achievements?.status || "");
    const existing = document.getElementById(achievementNoticeId);
    if (
      existing?.dataset?.appId === String(appId) &&
      existing?.dataset?.status === renderedStatus &&
      existing?.dataset?.mode === mode &&
      existing.isConnected
    ) {
      return;
    }
    existing?.remove();

    const achievementHeading = findVisibleHeading(["ACHIEVEMENTS"]);
    const nextHeading = findVisibleHeading(["MEDIA", "NOTES"]);
    const notice = createAchievementNotice(snapshot, appId, mode);
    if (achievementHeading) {
      notice.firstElementChild?.remove();
      achievementHeading.insertAdjacentElement("afterend", notice);
      return;
    }
    if (nextHeading) {
      nextHeading.insertAdjacentElement("beforebegin", notice);
    }
  }

  function scheduleMetadataUiRender(delay = 80) {
    if (state.metadataUiTimer || state.disposed) {
      return;
    }
    state.metadataUiTimer = window.setTimeout(() => {
      state.metadataUiTimer = 0;
      renderAchievementNotice();
    }, delay);
  }

  function patchMethod(target, methodName, handler) {
    if (!target || typeof target[methodName] !== "function") {
      return null;
    }
    const original = target[methodName];
    const patched = function steamToolsOmniNativePatch(...args) {
      const thisValue = this;
      const invokeOriginal = (...nextArgs) =>
        original.apply(thisValue, nextArgs.length ? nextArgs : args);
      return handler(thisValue, invokeOriginal, args);
    };
    patched.__steamToolsOmniNativeOriginal = original;
    target[methodName] = patched;
    return () => {
      if (target[methodName] === patched) {
        target[methodName] = original;
      }
    };
  }

  function addUnpatcher(unpatcher) {
    if (typeof unpatcher === "function") {
      state.unpatchers.push(unpatcher);
    }
  }

  function getWebpackRequire() {
    if (window.__steamToolsOmniMetadataWebpackRequire) {
      return window.__steamToolsOmniMetadataWebpackRequire;
    }
    const chunk = window.webpackChunksteamui;
    if (!Array.isArray(chunk) || typeof chunk.push !== "function") {
      return null;
    }
    let runtimeRequire = null;
    try {
      chunk.push([
        [`steam-tools-omni-metadata-${Date.now()}`],
        {},
        (require) => {
          runtimeRequire = require;
          window.__steamToolsOmniMetadataWebpackRequire = require;
        },
      ]);
    } catch (_) {}
    return runtimeRequire;
  }

  function findDetailsSectionsPrototype() {
    const cached =
      window.__steamToolsOmniMetadataDetailsSectionsPrototype;
    if (typeof cached?.GetSections === "function") {
      return cached;
    }
    const runtimeRequire = getWebpackRequire();
    if (!runtimeRequire?.m) {
      return null;
    }
    for (const moduleId of Object.keys(runtimeRequire.m)) {
      let exportsObject;
      try {
        exportsObject = runtimeRequire(moduleId);
      } catch (_) {
        continue;
      }
      const entries =
        exportsObject && typeof exportsObject === "object"
          ? Object.values(exportsObject)
          : [exportsObject];
      for (const value of entries) {
        if (typeof value?.prototype?.GetSections !== "function") {
          continue;
        }
        const source = String(value.prototype.GetSections);
        if (
          !source.includes("activity") ||
          !source.includes("screenshots")
        ) {
          continue;
        }
        window.__steamToolsOmniMetadataDetailsSectionsPrototype =
          value.prototype;
        return value.prototype;
      }
    }
    return null;
  }

  function findAchievementStore() {
    const cached = window.__steamToolsOmniMetadataAchievementStore;
    if (
      cached?.m_mapMyAchievements?.set &&
      typeof Object.getPrototypeOf(cached)?.LoadMyAchievements === "function"
    ) {
      return cached;
    }
    const runtimeRequire = getWebpackRequire();
    if (!runtimeRequire?.m) {
      return null;
    }
    for (const moduleId of Object.keys(runtimeRequire.m)) {
      let exportsObject;
      try {
        exportsObject = runtimeRequire(moduleId);
      } catch (_) {
        continue;
      }
      const entries =
        exportsObject && typeof exportsObject === "object"
          ? Object.values(exportsObject)
          : [exportsObject];
      for (const value of entries) {
        if (
          !value?.m_mapMyAchievements?.set ||
          typeof Object.getPrototypeOf(value)?.LoadMyAchievements !== "function"
        ) {
          continue;
        }
        window.__steamToolsOmniMetadataAchievementStore = value;
        return value;
      }
    }
    return null;
  }

  function uninstallNativePatches() {
    for (const unpatch of state.unpatchers.splice(0).reverse()) {
      try {
        unpatch();
      } catch (_) {}
    }
    state.patchedDetailsStore = null;
    state.patchedAppStore = null;
    state.patchedActivityStore = null;
    state.patchedAchievementStore = null;
    state.patchedAjaxRequest = null;
    state.patchedSectionsPrototype = null;
  }

  function ensureDetailSafety(appId, appData) {
    const details = appData?.details;
    if (!details) {
      return;
    }
    const detailsAppId = Number(
      details.unAppID ?? details.appid ?? details.nAppID ?? 0,
    );
    const detailsOverview =
      Number.isFinite(detailsAppId) && detailsAppId > 0
        ? window.appStore?.GetAppOverviewByAppID?.(detailsAppId)
        : null;
    if (!detailsOverview) {
      details.unAppID = appId;
    }
    if (!Array.isArray(details.vecDLC)) {
      details.vecDLC = [];
    }
    if (!Array.isArray(details.vecChildConfigApps)) {
      details.vecChildConfigApps = [];
    }
    if (!Array.isArray(details.vecScreenShots)) {
      details.vecScreenShots = [];
    }
    if (details.appid == null) {
      details.appid = appId;
    }
    if (details.nAppID == null) {
      details.nAppID = appId;
    }
  }

  function featureCategoryIds(snapshot) {
    const categories = new Set();
    for (const feature of snapshot?.gameInfo?.features || []) {
      const category = featureCategories[normalizeText(feature)];
      if (category) {
        categories.add(category);
      }
    }
    if ((snapshot?.achievements?.items || []).length > 0) {
      categories.add(22);
    }
    return categories;
  }

  function nativeScreenshots(snapshot) {
    const appId = Number(snapshot?.steamAppId || 0);
    const source = [
      ...(snapshot?.gameInfo?.screenshots || []),
      ...(snapshot?.community || [])
        .filter((item) => item?.thumbnailUrl)
        .map((item) => ({
          id: item.id,
          thumbnailUrl: item.thumbnailUrl,
          fullImageUrl: item.thumbnailUrl,
          caption: item.title,
        })),
    ];
    const seen = new Set();
    const screenshots = [];
    for (const image of source) {
      const full = String(
        image?.fullImageUrl || image?.thumbnailUrl || "",
      ).trim();
      if (!/^https:\/\//i.test(full) || seen.has(full)) {
        continue;
      }
      seen.add(full);
      const thumb = String(image?.thumbnailUrl || full).trim();
      screenshots.push({
        appid: appId,
        id: String(image?.id || `${appId}-${screenshots.length}`),
        nScreenshotID: screenshots.length + 1,
        strCaption: String(image?.caption || snapshot?.title || ""),
        strImageURL: full,
        strThumbnailURL: /^https:\/\//i.test(thumb) ? thumb : full,
        strURL: full,
        url: full,
        nWidth: 1280,
        nHeight: 720,
        width: 1280,
        height: 720,
        bSpoiler: false,
      });
      if (screenshots.length >= 12) {
        break;
      }
    }
    return screenshots;
  }

  function nativeAchievementItem(item) {
    const icon = String(item?.iconUrl || "");
    const unlockedAt = item?.unlockedAtUtc
      ? Math.floor(new Date(item.unlockedAtUtc).getTime() / 1000)
      : 0;
    return {
      strID: String(item?.id || item?.name || ""),
      strName: String(item?.name || ""),
      strDescription: String(item?.description || ""),
      bAchieved: item?.unlocked === true,
      rtUnlocked: Number.isFinite(unlockedAt) ? unlockedAt : 0,
      strImage: icon,
      strImageURL: icon,
      strImageUrl: icon,
      strIcon: icon,
      strIconURL: icon,
      iconUrl: icon,
      imageUrl: icon,
      bHidden: item?.hidden === true,
      flMinProgress: 0,
      flCurrentProgress: Number(item?.currentProgress || 0),
      flMaxProgress: Math.max(1, Number(item?.targetProgress || 1)),
      flAchieved: item?.unlocked === true ? 1 : 0,
    };
  }

  function buildNativeAchievements(snapshot) {
    const items = (snapshot?.achievements?.items || [])
      .map(nativeAchievementItem)
      .filter((item) => item.strID && item.strName);
    if (!items.length) {
      return null;
    }
    const achieved = items
      .filter((item) => item.bAchieved && !item.bHidden)
      .sort((left, right) => right.rtUnlocked - left.rtUnlocked);
    const hidden = items.filter((item) => item.bAchieved && item.bHidden);
    const unachieved = items.filter((item) => !item.bAchieved);
    return {
      nAchieved: achieved.length + hidden.length,
      nTotal: items.length,
      vecAchievedHidden: hidden,
      vecHighlight: achieved.slice(0, 12),
      vecUnachieved: unachieved,
    };
  }

  function communityPublishedFileId(appId, index, value) {
    const suffix = stableNumericId(value, index + 1).slice(-10);
    return `90909${String(appId).padStart(10, "0")}${suffix.padStart(10, "0")}`;
  }

  function buildNativeCommunityPayload(appId, snapshot) {
    const source = [
      ...(snapshot?.community || []).map((item) => ({
        id: item?.id,
        title: item?.title,
        url: item?.url,
        image: item?.thumbnailUrl,
        source: item?.source,
        kind: item?.kind,
      })),
      ...(snapshot?.gameInfo?.screenshots || []).map((item) => ({
        id: item?.id,
        title: item?.caption || snapshot?.title,
        url: item?.fullImageUrl,
        image: item?.thumbnailUrl || item?.fullImageUrl,
        source: snapshot?.sourceLabel || "Store",
        kind: "screenshot",
      })),
    ];
    const seen = new Set();
    const hub = [];
    for (const item of source) {
      const image = String(item?.image || "").trim();
      const targetUrl = String(item?.url || image).trim();
      if (!/^https:\/\//i.test(image) || seen.has(image)) {
        continue;
      }
      seen.add(image);
      const index = hub.length;
      const sourceName = String(item?.source || snapshot?.sourceLabel || "Community");
      const publishedFileId = communityPublishedFileId(
        appId,
        index,
        item?.id || targetUrl,
      );
      const isVideo =
        String(item?.kind || "").toLowerCase().includes("video") ||
        /(?:youtube\.com|youtu\.be)/i.test(targetUrl);
      const youtubeId = targetUrl.match(
        /(?:youtube\.com\/(?:watch\?v=|embed\/)|youtu\.be\/)([A-Za-z0-9_-]{11})/i,
      )?.[1] || "";
      const creator = {
        steamid: "76561197960287930",
        name: sourceName,
        avatar: image,
        avatar_url: image,
        avatar_medium: image,
        avatar_full: image,
        avatarFullURL: image,
      };
      hub.push({
        appid: appId,
        consumer_appid: appId,
        published_file_id: publishedFileId,
        publishedfileid: publishedFileId,
        type: isVideo ? 4 : 5,
        title: String(item?.title || snapshot?.title || "Community Content"),
        description: String(item?.title || snapshot?.title || ""),
        preview_image_url: image,
        full_image_url: targetUrl,
        youtube_video_id: youtubeId,
        image_width: 1280,
        image_height: 720,
        spoiler_tag: false,
        content_descriptorids: [],
        reactions: [],
        avatar: image,
        avatar_url: image,
        creator_avatar_url: image,
        author_avatar_url: image,
        owner_avatar_url: image,
        creator,
        time_created: Math.floor(Date.now() / 1000) - index * 60,
        votes_up: 0,
        votes_down: 0,
        num_comments_public: 0,
      });
      if (hub.length >= 20) {
        break;
      }
    }
    return hub.length ? { hub } : null;
  }

  function stableNumericId(value, fallback) {
    const digits = String(value || "").match(/\d{6,}/)?.[0];
    if (digits) {
      return digits.slice(-18);
    }
    let hash = 2166136261;
    for (const character of String(value || fallback || "")) {
      hash ^= character.charCodeAt(0);
      hash = Math.imul(hash, 16777619);
    }
    return String(Math.abs(hash >>> 0) || fallback || 1);
  }

  function fakeSteamId(
    accountId = 0,
    steamId64 = "76561197960287930",
  ) {
    return {
      GetAccountID: () => accountId,
      ConvertTo64BitString: () => steamId64,
      toString: () => steamId64,
    };
  }

  function buildPartnerEvent(appId, snapshot, item, index) {
    const sourceSteamAppId = Number(snapshot?.sourceSteamAppId || appId);
    const dateValue = item?.publishedAtUtc
      ? Math.floor(new Date(item.publishedAtUtc).getTime() / 1000)
      : Math.floor(Date.now() / 1000) - index * 60;
    const date = Number.isFinite(dateValue) ? dateValue : 0;
    const announcementGid = stableNumericId(
      item?.id || item?.url,
      date + index,
    );
    const eventGid = `old_announce_${announcementGid}`;
    const title = String(item?.title || "News");
    const summary = String(item?.summary || "");
    const image = /^https:\/\//i.test(String(item?.imageUrl || ""))
      ? String(item.imageUrl)
      : "";
    const url = String(item?.url || "");
    const clanSteamID = fakeSteamId();
    const tags = ["news", "tools_for_steam"];
    const partnerEvent = {
      __steamToolsOmniNativePartnerEvent: true,
      GID: eventGid,
      gid: eventGid,
      event_gid: eventGid,
      AnnouncementGID: announcementGid,
      announcement_gid: announcementGid,
      announcementGID: announcementGid,
      appid: appId,
      reference_appid: sourceSteamAppId,
      steam_appid: sourceSteamAppId,
      type: 28,
      event_type: 28,
      bOldAnnouncement: true,
      bLoaded: true,
      loadedAllLanguages: true,
      visibility_state: 2,
      postTime: date,
      createTime: date,
      startTime: date,
      endTime: date,
      visibilityStartTime: date,
      visibilityEndTime: date + 86400 * 365,
      nVotesUp: 0,
      nVotesDown: 0,
      nCommentCount: 0,
      forumTopicGID: "0",
      clanSteamID,
      announcementClanSteamID: clanSteamID,
      jsondata: {
        localized_summary: [summary],
        localized_subtitle: [""],
        localized_body: [summary],
        localized_title_image: [image],
        localized_capsule_image: [image],
        localized_spotlight_image: [image],
        localized_header_image: [image],
        library_spotlight: true,
        library_spotlight_text: true,
        referenced_appids: sourceSteamAppId ? [sourceSteamAppId] : [],
      },
      name: new Map([[0, title]]),
      description: new Map([[0, summary]]),
      timestamp_loc_updated: new Map([[0, date]]),
      vecTags: tags,
      tags,
      BHasTag: (tag) => tags.includes(String(tag || "")),
      BHasTagStartingWith: (prefix) =>
        tags.some((tag) => tag.startsWith(String(prefix || ""))),
      GetAllTags: () => tags,
      BMatchesAllTags: (values) =>
        !Array.isArray(values) ||
        values.every((tag) => tags.includes(String(tag || ""))),
      BInRealmGlobal: () => true,
      BInRealmChina: () => false,
      BIsLanguageValidForRealms: () => true,
      GetNameWithFallback: () => title,
      GetGameTitle: () => title,
      GetDescriptionWithFallback: () => summary,
      GetSummaryWithFallback: () => summary,
      GetSummary: () => summary,
      BHasSummary: () => Boolean(summary),
      GetSubTitle: () => "",
      BHasSubTitle: () => false,
      GetSubTitleWithLanguageFallback: () => "",
      GetSubTitleWithSummaryFallback: () => "",
      GetCategoryAsString: () => String(item?.feedLabel || "News"),
      GetEventTypeAsString: () => String(item?.feedLabel || "News"),
      GetImgArray: () => image ? [image] : [],
      GetImageHash: () => null,
      GetImageHashAndExt: () => null,
      GetImageFromBeginningOfDescription: () => image,
      GetImageURL: () => image,
      GetImageURLWithFallback: () => image,
      GetImageForSizeAsArrayWithFallback: () => image ? [image] : [],
      BImageNeedScreenshotFallback: () => !image,
      BHasSomeImage: () => Boolean(image),
      BHasImage: () => Boolean(image),
      GetFallbackArtworkScreenshot: () => image,
      GetStartTimeAndDateUnixSeconds: () => date,
      GetEndTimeAndDateUnixSeconds: () => date,
      GetPostTimeAndDateUnixSeconds: () => date,
      GetAnnouncementGID: () => announcementGid,
      BHasAnnouncementGID: () => true,
      GetAppID: () => appId,
      GetReferenceAppID: () => sourceSteamAppId,
      GetStoreAppID: () => sourceSteamAppId,
      BIsPartnerEvent: () => false,
      BIsOGGEvent: () => Boolean(sourceSteamAppId),
      BIsEventInFuture: () => false,
      BHasEventEnded: () => false,
      BIsEventActionEnabled: () => false,
      BShowLibrarySpotlight: () => true,
      BShowLibrarySpotlightText: () => true,
      BIsImageSafeForAllAges: () => true,
      BHasBroadcastEnabled: () => false,
      BEventCanShowBroadcastWidget: () => false,
      BHasBroadcastForceBanner: () => false,
      BSaleShowBroadcastAtTopOfPage: () => false,
      GetVisibilityStartTimeAndDateUnixSeconds: () => date,
      BHasForumTopicGID: () => false,
      GetForumTopicURL: () => "",
      GetAppIDOrReferenceAppID: () => sourceSteamAppId,
      GetEventType: () => 28,
      BIsVisibleEvent: () => true,
      BIsStagedEvent: () => false,
      BIsUnlistedEvent: () => false,
      GetStoreOrCommunityURL: () => url,
      GetCommunityDiscussionURL: () => url,
      GetStoreNewsURL: () => url,
      url,
    };
    return partnerEvent;
  }

  function buildNativeActivity(appId, snapshot) {
    const events = (snapshot?.activity || [])
      .slice(0, 20)
      .map((item, index) => {
        const partnerEvent = buildPartnerEvent(
          appId,
          snapshot,
          item,
          index,
        );
        const date = Number(partnerEvent.postTime || 0);
        const gid = String(partnerEvent.AnnouncementGID);
        return {
          __steamToolsOmniNativeActivityEvent: true,
          gameid: String(appId),
          unUniqueID: Number(`${gid.slice(-8)}${index}`.slice(-9)) ||
            date + index,
          rtEventTime: date,
          steamIDActor: fakeSteamId(),
          steamIDTarget: fakeSteamId(),
          eEventType: 1002,
          eEventSubType: 0,
          eGameActivityType: 0,
          bIsGameActivity: false,
          commentThreads: [],
          activeThread: 0,
          appid: appId,
          referenceAppID: Number(snapshot?.sourceSteamAppId || appId),
          announcementGID: gid,
          clan_announcementid: gid,
          eventModel: partnerEvent,
          forumTopicGID: "0",
          upvotes: 0,
          downvotes: 0,
          comment_count: 0,
          BIsValid: () => true,
          IsEventLoaded: () => true,
          GetEvent: async () => partnerEvent,
          ReloadEvent: async () => partnerEvent,
          GetParentalFeature: () => 0,
          BUserCanDelete: () => false,
          BSupportsCommentThreads: () => false,
          GetActiveCommentThread: () => null,
          SetActiveCommentThread: () => undefined,
        };
      })
      .sort((left, right) => right.rtEventTime - left.rtEventTime);
    if (!events.length) {
      return null;
    }
    const grouped = new Map();
    for (const event of events) {
      const day = Math.floor(event.rtEventTime / 86400) * 86400;
      if (!grouped.has(day)) {
        grouped.set(day, []);
      }
      grouped.get(day).push(event);
    }
    const days = Array.from(grouped.entries())
      .sort((left, right) => right[0] - left[0])
      .map(([, dayEvents]) => ({
        isValid: dayEvents.length > 0,
        events: dayEvents,
        GetLatestEventTime: () =>
          Math.max(...dayEvents.map((event) => event.rtEventTime)),
        GetEarliestEventTime: () =>
          Math.min(...dayEvents.map((event) => event.rtEventTime)),
        BHasEvents: () => dayEvents.length > 0,
      }));
    return {
      __steamToolsOmniNativeActivity: true,
      appid: appId,
      m_bNoMoreHistoryAvailable: true,
      lastAddedEventType: 1002,
      lastAddedPartnerEvent: null,
      appActivityByDay: days,
      latest_user_news_time: events[0].rtEventTime,
      earliest_user_news_time: events.at(-1).rtEventTime,
      latest_game_activity_time: 0,
      earliest_game_activity_time: 0,
      BHasEvents: () => true,
      SortEvents: () => undefined,
      RequestStoreItems: async () => undefined,
      MergeUserNews: async () => undefined,
      MergeGameActivity: () => undefined,
      GetAchievementMapCache: () => "[]",
      GetUserNewsCache: () => [],
      GetGameActivityCache: () => [],
    };
  }

  function buildNativeActivityFeedPayload(appId, snapshot) {
    const items = (snapshot?.activity || []).slice(0, 20).map((item, index) => {
      const dateValue = item?.publishedAtUtc
        ? Math.floor(new Date(item.publishedAtUtc).getTime() / 1000)
        : Math.floor(Date.now() / 1000) - index * 60;
      const date = Number.isFinite(dateValue) ? dateValue : 0;
      const gid = stableNumericId(item?.id || item?.url, date + index);
      const title = String(item?.title || snapshot?.title || "News");
      const summary = String(item?.summary || "");
      const image = /^https:\/\//i.test(String(item?.imageUrl || ""))
        ? String(item.imageUrl)
        : "";
      const url = String(item?.url || "");
      return {
        appid: appId,
        gid,
        id: `steamtools-activity-${appId}-${gid}-${index}`,
        news_id: gid,
        announcement_gid: gid,
        clan_steamid: "103582791429521412",
        event_name: title,
        event_type: 28,
        type: 28,
        title,
        headline: title,
        description: summary,
        summary,
        body: summary,
        raw_body: summary,
        contents: summary,
        url,
        external_url: url,
        link: url,
        image,
        image_url: image,
        preview_image_url: image,
        full_image_url: image || url,
        rtime32_start_time: date,
        rtime32_end_time: date,
        rtime32_last_modified: date,
        posttime: date,
        published: date,
        time_created: date,
        date,
        feedlabel: String(item?.feedLabel || item?.author || "News"),
        author: String(item?.author || item?.feedLabel || "News"),
        comment_count: 0,
        upvotes: 0,
        downvotes: 0,
        announcement_body: {
          gid,
          clanid: "0",
          posterid: "0",
          headline: title,
          posttime: date,
          updatetime: date,
          body: summary,
          commentcount: 0,
          tags: ["news", "tools_for_steam"],
          language: 0,
          hidden: 0,
          forum_topic_id: "0",
          event_gid: gid,
          voteupcount: 0,
          votedowncount: 0,
        },
      };
    });
    if (!items.length) {
      return null;
    }
    return {
      events: items,
      rgEvents: items,
      rgNews: items,
      rgActivity: items,
      rgFeedItems: items,
      activity: items,
      activities: items,
      news: items,
      items,
      results: items,
      count: items.length,
      bHasMore: false,
      success: 1,
    };
  }

  function activityAppIdFromUrl(value) {
    const url = decodeURIComponent(String(value || ""));
    for (const pattern of [
      /library\/(?:appactivityfeed|appactivity|activityfeed|activity|appnews|appupdates)\/(\d+)/i,
      /(?:appactivityfeed|appactivity|activityfeed|activity|appnews|appupdates)[^?]*[?&](?:appid|app_id|appId)=(\d+)/i,
      /(?:appid|app_id|appId)=(\d+).*?(?:appactivity|activity|appnews|appupdates)/i,
    ]) {
      const match = url.match(pattern);
      if (match) {
        return Number(match[1]);
      }
    }
    return 0;
  }

  function communityAppIdFromUrl(value) {
    const url = decodeURIComponent(String(value || ""));
    const match = url.match(
      /library\/appcommunityfeed\/(\d+)|appcommunityfeed[^?]*[?&](?:appid|app_id|appId)=(\d+)/i,
    );
    return Number(match?.[1] || match?.[2] || 0);
  }

  function captureOriginalState(appId) {
    if (state.originalByApp.has(appId)) {
      return;
    }
    const appData = window.appDetailsStore?.GetAppData?.(appId);
    const details = appData?.details;
    const overview = window.appStore?.GetAppOverviewByAppID?.(appId);
    const activityStore = window.appActivityStore;
    const progressCache =
      window.appAchievementProgressCache?.m_achievementProgress?.mapCache;
    state.originalByApp.set(appId, {
      descriptionsData: appData?.descriptionsData,
      associationData: appData?.associationData,
      screenshots: appData?.screenshots,
      details: details
        ? {
            achievements: details.achievements,
            nScreenshots: details.nScreenshots,
            vecScreenShots: details.vecScreenShots,
            bCommunityMarketPresence: details.bCommunityMarketPresence,
          }
        : null,
      categories: overview?.m_setStoreCategories instanceof Set
        ? new Set(overview.m_setStoreCategories)
        : null,
      activity: activityStore?.m_mapAppActivity?.get?.(appId),
      achievementProgress: progressCache?.get?.(appId),
      achievementStore: captureAchievementStoreState(appId),
    });
  }

  function captureAchievementStoreState(appId) {
    const store = findAchievementStore();
    if (!store) {
      return null;
    }
    const snapshot = {};
    for (const mapName of [
      "m_mapMyAchievements",
      "m_mapAchievements",
      "m_mapGlobalAchievements",
    ]) {
      const map = store[mapName];
      if (!map?.has) {
        continue;
      }
      snapshot[mapName] = [appId, String(appId)].map((key) => ({
        key,
        hadValue: map.has(key),
        value: map.get(key),
      }));
    }
    return snapshot;
  }

  function restoreAchievementStoreState(snapshot) {
    if (!snapshot) {
      return;
    }
    const store = findAchievementStore();
    if (!store) {
      return;
    }
    for (const [mapName, entries] of Object.entries(snapshot)) {
      const map = store[mapName];
      if (!map?.set) {
        continue;
      }
      for (const entry of entries || []) {
        if (entry.hadValue) {
          map.set(entry.key, entry.value);
        } else {
          map.delete?.(entry.key);
        }
      }
    }
  }

  function cacheNativeDetails(appId, section, version, value) {
    try {
      window.appDetailsCache?.SetCachedDataForApp?.(
        appId,
        section,
        version,
        value,
      );
    } catch (_) {}
  }

  function nativeDescriptionData(snapshot) {
    const gameInfo = snapshot?.gameInfo || {};
    const description = String(
      gameInfo.description || gameInfo.shortDescription || "",
    );
    return {
      strFullDescription: description,
      // SteamUI uses either field depending on its current Game Info layout.
      // Keep both tied to OmniLibrary's canonical description so a native
      // shortcut snippet can never replace the store metadata on re-render.
      strSnippet: description,
    };
  }

  function ensureNativeDescriptions(appId, snapshot, appData) {
    if (!appData?.details) {
      return null;
    }
    const descriptions = nativeDescriptionData(snapshot);
    const current = appData.descriptionsData;
    if (
      String(current?.strFullDescription || "") !==
        descriptions.strFullDescription ||
      String(current?.strSnippet || "") !== descriptions.strSnippet
    ) {
      appData.descriptionsData = descriptions;
      cacheNativeDetails(appId, "descriptions", 1, descriptions);
    }
    return appData.descriptionsData || descriptions;
  }

  function primeAchievementStores(appId, payload) {
    if (!payload) {
      return;
    }
    const storePayload = getAchievementStorePayload(appId, payload);
    const signature = storePayload.signature;
    const stores = new Set([
      findAchievementStore(),
      window.appAchievementStore,
      window.achievementStore,
      window.g_AppAchievementStore,
    ]);
    for (const store of [
      ...stores,
    ]) {
      if (!store) {
        continue;
      }
      let primed = state.achievementPrimedStores.get(store);
      if (!primed) {
        primed = new Map();
        state.achievementPrimedStores.set(store, primed);
      }
      const currentUser =
        store.m_mapMyAchievements?.get?.(appId) ||
        store.m_mapMyAchievements?.get?.(String(appId));
      const currentGlobal =
        store.m_mapGlobalAchievements?.get?.(appId) ||
        store.m_mapGlobalAchievements?.get?.(String(appId));
      if (
        primed.get(appId) === signature &&
        currentUser === storePayload.user &&
        currentGlobal === storePayload.global
      ) {
        continue;
      }
      for (const key of [appId, String(appId)]) {
        store.m_mapMyAchievements?.set?.(key, storePayload.user);
        store.m_mapAchievements?.set?.(key, storePayload.user);
        store.m_mapGlobalAchievements?.set?.(key, storePayload.global);
        store.m_mapInflightMyAchievementsRequests?.delete?.(key);
      }
      primed.set(appId, signature);
    }
  }

  function achievementPayloadSignature(payload) {
    const items = [
      ...(payload?.vecHighlight || []),
      ...(payload?.vecAchievedHidden || []),
      ...(payload?.vecUnachieved || []),
    ];
    return [
      Number(payload?.nAchieved || 0),
      Number(payload?.nTotal || 0),
      ...items.map((item) => [
        item?.strID,
        item?.bAchieved ? 1 : 0,
        Number(item?.rtUnlocked || 0),
        Number(item?.flCurrentProgress || 0),
        Number(item?.flMaxProgress || 0),
        item?.strImageURL || item?.strImage || "",
      ].join(":")),
    ].join("|");
  }

  function getAchievementStorePayload(appId, payload) {
    const signature = achievementPayloadSignature(payload);
    const cached = state.achievementStorePayloads.get(appId);
    if (cached?.signature === signature) {
      return cached;
    }
    const achieved = Object.fromEntries(
      (payload?.vecHighlight || []).map((item) => [item.strID, item]),
    );
    const hidden = Object.fromEntries(
      (payload?.vecAchievedHidden || []).map((item) => [item.strID, item]),
    );
    const unachieved = Object.fromEntries(
      (payload?.vecUnachieved || []).map((item) => [item.strID, item]),
    );
    const storePayload = {
      signature,
      user: {
        loading: false,
        data: { achieved, hidden, unachieved },
      },
      global: {
        loading: false,
        data: {},
      },
    };
    state.achievementStorePayloads.set(appId, storePayload);
    return storePayload;
  }

  function emptyAchievementPayload() {
    return {
      nAchieved: 0,
      nTotal: 0,
      vecAchievedHidden: [],
      vecHighlight: [],
      vecUnachieved: [],
    };
  }

  function managedAchievementPayload(appId) {
    return state.nativeAchievements.get(appId) ||
      buildNativeAchievements(state.nativeSnapshots.get(appId)) ||
      emptyAchievementPayload();
  }

  function installAchievementStorePatches(store) {
    if (!store) {
      return;
    }
    const prototype = Object.getPrototypeOf(store);
    addUnpatcher(patchMethod(
      prototype,
      "LoadMyAchievements",
      (thisValue, _original, args) => {
        const appId = Number(args[0]);
        if (!isManagedAppId(appId)) {
          return _original();
        }
        const apply = (snapshot) => {
          const payload = buildNativeAchievements(snapshot) ||
            state.nativeAchievements.get(appId) ||
            emptyAchievementPayload();
          if (payload.nTotal > 0) {
            state.nativeAchievements.set(appId, payload);
          }
          primeAchievementStores(appId, payload);
          thisValue.m_mapInflightMyAchievementsRequests?.delete?.(appId);
          thisValue.m_mapInflightMyAchievementsRequests?.delete?.(String(appId));
        };
        const snapshot = state.nativeSnapshots.get(appId);
        if (snapshot) {
          apply(snapshot);
          return Promise.resolve();
        }
        return waitForNativeSnapshot(appId).then(apply);
      },
    ));
    for (const methodName of ["GetMyAchievements", "GetGlobalAchievements"]) {
      addUnpatcher(patchMethod(
        prototype,
        methodName,
        (thisValue, original, args) => {
          const appId = Number(args[0]);
          if (!isManagedAppId(appId)) {
            return original();
          }
          const payload = managedAchievementPayload(appId);
          const map = methodName === "GetGlobalAchievements"
            ? thisValue.m_mapGlobalAchievements
            : thisValue.m_mapMyAchievements;
          const existing =
            map?.get?.(appId) ||
            map?.get?.(String(appId));
          if (existing) {
            return existing;
          }
          const storePayload = getAchievementStorePayload(appId, payload);
          return methodName === "GetGlobalAchievements"
            ? storePayload.global
            : storePayload.user;
        },
      ));
    }
  }

  function registerPartnerEvents(appId, activity) {
    const events = (activity?.appActivityByDay || [])
      .flatMap((day) => day?.events || [])
      .map((event) => event?.eventModel)
      .filter(Boolean);
    if (!events.length) {
      return;
    }
    for (const store of [
      window.appPartnerEventStore,
      window.partnerEventStore,
      window.g_PartnerEventStore,
    ]) {
      if (!store) {
        continue;
      }
      for (const event of events) {
        const keys = [
          event.GID,
          event.AnnouncementGID,
          String(event.AnnouncementGID),
          `old_announce_${event.AnnouncementGID}`,
        ];
        for (const key of keys) {
          store.m_mapExistingEvents?.set?.(key, event);
        }
        if (store.m_mapAppIDToGIDs?.get && store.m_mapAppIDToGIDs?.set) {
          const current = store.m_mapAppIDToGIDs.get(appId) || [];
          if (!current.includes(event.GID)) {
            store.m_mapAppIDToGIDs.set(appId, [...current, event.GID]);
          }
        }
      }
    }
  }

  function resolveSnapshotWaiters(appId, snapshot) {
    const waiters = state.snapshotWaiters.get(appId);
    if (!waiters?.size) {
      return;
    }
    state.snapshotWaiters.delete(appId);
    for (const waiter of waiters) {
      window.clearTimeout(waiter.timer);
      waiter.resolve(snapshot);
    }
  }

  function waitForNativeSnapshot(appId, timeoutMs = 10000) {
    const cached = state.nativeSnapshots.get(appId);
    if (cached) {
      return Promise.resolve(cached);
    }
    if (!isManagedAppId(appId)) {
      return Promise.resolve(null);
    }
    state.currentAppId = appId;
    queueRefresh(0);
    return new Promise((resolve) => {
      const waiter = {
        resolve,
        timer: window.setTimeout(() => {
          const waiters = state.snapshotWaiters.get(appId);
          waiters?.delete(waiter);
          if (!waiters?.size) {
            state.snapshotWaiters.delete(appId);
          }
          resolve(state.nativeSnapshots.get(appId) || null);
        }, timeoutMs),
      };
      const waiters = state.snapshotWaiters.get(appId) || new Set();
      waiters.add(waiter);
      state.snapshotWaiters.set(appId, waiters);
    });
  }

  function applyNativeSnapshot(snapshot) {
    const appId = Number(snapshot?.steamAppId || 0);
    if (!isManagedAppId(appId)) {
      return false;
    }
    const appData = window.appDetailsStore?.GetAppData?.(appId);
    const overview = window.appStore?.GetAppOverviewByAppID?.(appId);
    if (!appData || !overview) {
      return false;
    }
    const revision = Number(snapshot?.revision || 0);
    const applied = state.appliedSnapshotByApp.get(appId);
    if (
      revision > 0 &&
      applied?.revision === revision &&
      applied?.appData === appData &&
      applied?.overview === overview
    ) {
      ensureNativeDescriptions(appId, snapshot, appData);
      const payload = managedAchievementPayload(appId);
      primeAchievementStores(appId, payload);
      state.nativeSnapshots.set(appId, snapshot);
      resolveSnapshotWaiters(appId, snapshot);
      scheduleMetadataUiRender();
      return true;
    }
    captureOriginalState(appId);
    ensureDetailSafety(appId, appData);

    const gameInfo = snapshot?.gameInfo || {};
    const descriptions = nativeDescriptionData(snapshot);
    const associations = {
      rgDevelopers: (gameInfo.developers || []).map((name) => ({
        strName: String(name),
        strURL: "",
      })),
      rgPublishers: (gameInfo.publishers || []).map((name) => ({
        strName: String(name),
        strURL: "",
      })),
      rgFranchises: [],
    };
    const screenshots = nativeScreenshots(snapshot);
    const screenshotData = {
      rgScreenshots: screenshots,
      screenshots,
      vecScreenshots: screenshots,
      vecScreenShots: screenshots,
    };
    const achievements = buildNativeAchievements(snapshot);
    const achievementPayload = achievements || emptyAchievementPayload();
    const activity = buildNativeActivity(appId, snapshot);

    appData.descriptionsData = descriptions;
    appData.associationData = associations;
    if (screenshots.length) {
      appData.screenshots = screenshotData;
    }
    if (appData.details) {
      if (screenshots.length) {
        appData.details.nScreenshots = screenshots.length;
        appData.details.vecScreenShots = screenshots;
        appData.details.bCommunityMarketPresence = true;
      }
      appData.details.achievements = achievementPayload;
      appData.bLoadingAchievments = false;
    }

    cacheNativeDetails(appId, "descriptions", 1, descriptions);
    cacheNativeDetails(appId, "associations", 1, associations);
    if (screenshots.length) {
      cacheNativeDetails(appId, "screenshots", 1, screenshotData);
    }
    cacheNativeDetails(appId, "achievements", 2, achievementPayload);

    if (!overview.m_setStoreCategories) {
      overview.m_setStoreCategories = new Set();
    }
    for (const category of featureCategoryIds(snapshot)) {
      overview.m_setStoreCategories.add(category);
    }
    if (typeof gameInfo.rating === "number") {
      overview.metacritic_score = gameInfo.rating;
    }

    if (activity) {
      state.nativeActivity.set(appId, activity);
      window.appActivityStore?.m_mapAppActivity?.set?.(appId, activity);
      registerPartnerEvents(appId, activity);
    }
    primeAchievementStores(appId, achievementPayload);
    if (achievements) {
      state.nativeAchievements.set(appId, achievements);
      const progress =
        window.appAchievementProgressCache?.m_achievementProgress?.mapCache;
      progress?.set?.(appId, {
        all_unlocked: achievements.nAchieved === achievements.nTotal,
        appid: appId,
        cache_time: Date.now(),
        percentage: achievements.nTotal
          ? (achievements.nAchieved / achievements.nTotal) * 100
          : 0,
        total: achievements.nTotal,
          unlocked: achievements.nAchieved,
      });
    } else {
      state.nativeAchievements.delete(appId);
      const progress =
        window.appAchievementProgressCache?.m_achievementProgress?.mapCache;
      progress?.delete?.(appId);
      progress?.delete?.(String(appId));
    }

    state.nativeSnapshots.set(appId, snapshot);
    state.appliedSnapshotByApp.set(appId, {
      revision,
      appData,
      overview,
    });
    resolveSnapshotWaiters(appId, snapshot);
    window.dispatchEvent(new CustomEvent(
      "steamtools-omnilibrary-native-metadata-updated",
      { detail: { appId, revision: Number(snapshot?.revision || 0) } },
    ));
    scheduleMetadataUiRender();
    return true;
  }

  function restoreOriginalState() {
    for (const [appId, original] of state.originalByApp) {
      try {
        const appData = window.appDetailsStore?.GetAppData?.(appId);
        const overview = window.appStore?.GetAppOverviewByAppID?.(appId);
        if (appData) {
          appData.descriptionsData = original.descriptionsData;
          appData.associationData = original.associationData;
          appData.screenshots = original.screenshots;
          if (appData.details && original.details) {
            appData.details.achievements = original.details.achievements;
            appData.details.nScreenshots = original.details.nScreenshots;
            appData.details.vecScreenShots = original.details.vecScreenShots;
            appData.details.bCommunityMarketPresence =
              original.details.bCommunityMarketPresence;
          }
          cacheNativeDetails(
            appId,
            "descriptions",
            1,
            original.descriptionsData,
          );
          cacheNativeDetails(
            appId,
            "associations",
            1,
            original.associationData,
          );
          cacheNativeDetails(appId, "screenshots", 1, original.screenshots);
          cacheNativeDetails(
            appId,
            "achievements",
            2,
            original.details?.achievements,
          );
        }
        if (overview && original.categories) {
          overview.m_setStoreCategories = new Set(original.categories);
        }
        if (window.appActivityStore?.m_mapAppActivity) {
          if (original.activity == null) {
            window.appActivityStore.m_mapAppActivity.delete?.(appId);
          } else {
            window.appActivityStore.m_mapAppActivity.set?.(
              appId,
              original.activity,
            );
          }
        }
        const progress =
          window.appAchievementProgressCache?.m_achievementProgress?.mapCache;
        if (progress) {
          if (original.achievementProgress == null) {
            progress.delete?.(appId);
          } else {
            progress.set?.(appId, original.achievementProgress);
          }
        }
        restoreAchievementStoreState(original.achievementStore);
      } catch (_) {}
    }
    state.originalByApp.clear();
    state.nativeSnapshots.clear();
    state.nativeActivity.clear();
    state.nativeAchievements.clear();
    state.achievementStorePayloads.clear();
    state.appliedSnapshotByApp.clear();
    state.lastFetchAtByApp.clear();
    for (const waiters of state.snapshotWaiters.values()) {
      for (const waiter of waiters) {
        window.clearTimeout(waiter.timer);
        waiter.resolve(null);
      }
    }
    state.snapshotWaiters.clear();
  }

  function installNativePatches() {
    const detailsStore = window.appDetailsStore;
    const appStore = window.appStore;
    const activityStore = window.appActivityStore;
    const achievementStore = findAchievementStore();
    const ajaxRequest = window.steamAjaxRequest;
    const sectionsPrototype = findDetailsSectionsPrototype();
    if (!detailsStore || !appStore || !sectionsPrototype) {
      return false;
    }
    if (
      state.patchedDetailsStore === detailsStore &&
      state.patchedAppStore === appStore &&
      state.patchedActivityStore === activityStore &&
      state.patchedAchievementStore === achievementStore &&
      state.patchedAjaxRequest === ajaxRequest &&
      state.patchedSectionsPrototype === sectionsPrototype
    ) {
      return true;
    }

    uninstallNativePatches();
    state.patchedDetailsStore = detailsStore;
    state.patchedAppStore = appStore;
    state.patchedActivityStore = activityStore;
    state.patchedAchievementStore = achievementStore;
    state.patchedAjaxRequest = ajaxRequest;
    state.patchedSectionsPrototype = sectionsPrototype;

    const detailsProto = Object.getPrototypeOf(detailsStore);
    const overviewSample = appStore.allApps?.[0] ||
      appStore.GetAppOverviewByAppID?.(state.currentAppId);
    const overviewProto = overviewSample
      ? Object.getPrototypeOf(overviewSample)
      : null;

    addUnpatcher(patchMethod(
      sectionsPrototype,
      "GetSections",
      (thisValue, original, args) => {
        const sections = original();
        const overview = args[0] || thisValue?.props?.overview;
        const appId = Number(overview?.appid || 0);
        if (!isManagedAppId(appId)) {
          return sections;
        }
        const nativeSections = sections instanceof Set
          ? new Set(sections)
          : new Set(sections || []);
        // Steam restricts shortcuts to nonsteam/notes/screenshots. Keep that
        // native base and opt our managed games into the same native sections
        // PlayHub enables for enriched non-Steam entries.
        nativeSections.add("activity");
        nativeSections.add("community");
        const snapshot = state.nativeSnapshots.get(appId);
        if ((snapshot?.achievements?.items || []).length > 0) {
          nativeSections.add("achievements");
        } else {
          nativeSections.delete("achievements");
        }
        nativeSections.add("screenshots");
        if ("m_setSectionsMemo" in thisValue) {
          thisValue.m_setSectionsMemo = nativeSections;
        }
        return nativeSections;
      },
    ));

    addUnpatcher(patchMethod(
      detailsProto,
      "GetDescriptions",
      (_thisValue, original, args) => {
        const appId = metadataAppIdForRequest(args[0]);
        const originalResult = original();
        const snapshot = state.nativeSnapshots.get(appId);
        if (!snapshot || !appId) {
          return originalResult;
        }
        applyNativeSnapshot(snapshot);
        return ensureNativeDescriptions(
          appId,
          snapshot,
          detailsStore.GetAppData?.(appId),
        ) || originalResult;
      },
    ));
    addUnpatcher(patchMethod(
      detailsProto,
      "GetAssociations",
      (_thisValue, original, args) => {
        const appId = metadataAppIdForRequest(args[0]);
        const originalResult = original();
        const snapshot = state.nativeSnapshots.get(appId);
        if (!snapshot || !appId) {
          return originalResult;
        }
        applyNativeSnapshot(snapshot);
        return detailsStore.GetAppData?.(appId)?.associationData ||
          originalResult;
      },
    ));
    addUnpatcher(patchMethod(
      detailsProto,
      "GetAchievements",
      (_thisValue, original, args) => {
        const appId = metadataAppIdForRequest(args[0]);
        return appId
          ? managedAchievementPayload(appId)
          : original();
      },
    ));

    if (overviewProto) {
      addUnpatcher(patchMethod(
        overviewProto,
        "BHasStoreCategory",
        (thisValue, original, args) => {
          const appId = Number(thisValue?.appid || 0);
          const category = Number(args[0]);
          if (
            isManagedAppId(appId) &&
            featureCategoryIds(state.nativeSnapshots.get(appId)).has(category)
          ) {
            return true;
          }
          return original();
        },
      ));
    }

    if (activityStore) {
      addUnpatcher(patchMethod(
        activityStore,
        "GetAppActivity",
        (_thisValue, original, args) => {
          const appId = metadataAppIdForRequest(args[0]);
          return appId && state.nativeActivity.has(appId)
            ? state.nativeActivity.get(appId)
            : original();
        },
      ));
      for (const methodName of [
        "RequestRestoreActivity",
        "RestoreActivity",
        "FetchLatestActivity",
        "FetchLatestActivityFromServer",
        "FetchActivityHistory",
      ]) {
        addUnpatcher(patchMethod(
          activityStore,
          methodName,
          (_thisValue, original, args) => {
            const appId = Number(args[0]);
            const native = state.nativeActivity.get(appId);
            if (!isManagedAppId(appId) || !native) {
              return original();
            }
            return /History|Server|Restore/.test(methodName)
              ? Promise.resolve(native)
              : undefined;
          },
        ));
      }
    }

    installAchievementStorePatches(achievementStore);

    const ajaxProto = ajaxRequest ? Object.getPrototypeOf(ajaxRequest) : null;
    for (const methodName of ["get", "post"]) {
      addUnpatcher(patchMethod(
        ajaxProto,
        methodName,
        (_thisValue, original, args) => {
          const url = String(args[0] || "");
          const activityAppId = activityAppIdFromUrl(url);
          const activitySnapshot = state.nativeSnapshots.get(activityAppId);
          if (isManagedAppId(activityAppId)) {
            if (activitySnapshot) {
              const payload = buildNativeActivityFeedPayload(
                activityAppId,
                activitySnapshot,
              );
              if (payload) {
                return Promise.resolve(payload);
              }
            }
            return waitForNativeSnapshot(activityAppId).then((snapshot) =>
              buildNativeActivityFeedPayload(activityAppId, snapshot) ||
              original()
            );
          }
          const communityAppId = communityAppIdFromUrl(url);
          const communitySnapshot = state.nativeSnapshots.get(communityAppId);
          if (isManagedAppId(communityAppId)) {
            if (communitySnapshot) {
              const payload = buildNativeCommunityPayload(
                communityAppId,
                communitySnapshot,
              );
              if (payload) {
                return Promise.resolve(payload);
              }
            }
            return waitForNativeSnapshot(communityAppId).then((snapshot) =>
              buildNativeCommunityPayload(communityAppId, snapshot) ||
              original()
            );
          }
          return original();
        },
      ));
    }

    for (const snapshot of state.nativeSnapshots.values()) {
      applyNativeSnapshot(snapshot);
    }
    return true;
  }

  function schedulePatchRetry() {
    if (state.patchRetryTimer || state.disposed || !isPluginEnabled()) {
      return;
    }
    let attempts = 0;
    state.patchRetryTimer = window.setInterval(() => {
      attempts += 1;
      if (
        state.disposed ||
        !isPluginEnabled() ||
        installNativePatches() ||
        attempts >= 40
      ) {
        window.clearInterval(state.patchRetryTimer);
        state.patchRetryTimer = 0;
      }
    }, 500);
  }

  async function refreshSummary(force = false) {
    const shared = window.__steamLoaderOmniLibraryStateStore;
    try {
      if (shared?.refresh) {
        state.summary = await shared.refresh(force);
      } else {
        const response = await fetch(`${apiBase}api/unifystore/summary`, {
          cache: "no-store",
        });
        if (!response.ok) {
          throw new Error(`OmniLibrary summary failed (${response.status}).`);
        }
        state.summary = await response.json();
      }
    } catch (_) {
      if (!state.summary) {
        deactivate();
      }
    }
    return state.summary;
  }

  function scheduleNextRefresh(snapshot) {
    if (state.timer) {
      window.clearTimeout(state.timer);
    }
    if (!isPluginEnabled() || !state.currentAppId) {
      state.timer = 0;
      return;
    }
    state.timer = window.setTimeout(() => {
      state.timer = 0;
      if (document.hidden) {
        scheduleNextRefresh(snapshot);
      } else {
        void refreshCurrentMetadata(false);
      }
    }, snapshot?.refreshing ? updatingRefreshMs : readyRefreshMs);
  }

  function broadcastNativeSnapshot(snapshot) {
    if (!snapshot) {
      return;
    }
    try {
      state.channel?.postMessage?.({
        type: nativeMetadataMessageType,
        snapshot,
      });
    } catch (_) {}
  }

  async function refreshCurrentMetadata(force = false) {
    if (state.requestInFlight) {
      state.refreshPending = true;
      state.forceRefreshPending ||= force;
      return;
    }
    if (!isPluginEnabled()) {
      deactivate();
      return;
    }
    installNativePatches() || schedulePatchRetry();
    const appId = currentManagedAppId();
    if (!appId) {
      state.currentAppId = 0;
      return;
    }
    state.currentAppId = appId;
    const cachedSnapshot = state.nativeSnapshots.get(appId);
    const lastFetchAt = Number(state.lastFetchAtByApp.get(appId) || 0);
    if (!force && cachedSnapshot && Date.now() - lastFetchAt < 5000) {
      applyNativeSnapshot(cachedSnapshot);
      broadcastNativeSnapshot(cachedSnapshot);
      scheduleNextRefresh(cachedSnapshot);
      return;
    }
    state.requestInFlight = true;
    scheduleMetadataUiRender(0);
    try {
      const response = await fetch(
        `${apiBase}api/unifystore/metadata/games/${encodeURIComponent(appId)}`,
        {
          method: force ? "POST" : "GET",
          cache: "no-store",
        },
      );
      if (!response.ok) {
        throw new Error(`OmniLibrary metadata failed (${response.status}).`);
      }
      const snapshot = await response.json();
      if (
        Number(snapshot?.steamAppId || 0) !== appId ||
        !isManagedAppId(appId)
      ) {
        return;
      }
      state.nativeSnapshots.set(appId, snapshot);
      state.lastFetchAtByApp.set(appId, Date.now());
      applyNativeSnapshot(snapshot);
      broadcastNativeSnapshot(snapshot);
      scheduleNextRefresh(snapshot);
    } catch (_) {
      scheduleNextRefresh(state.nativeSnapshots.get(appId));
    } finally {
      state.requestInFlight = false;
      scheduleMetadataUiRender(0);
      const pending = state.refreshPending;
      const pendingForce = state.forceRefreshPending;
      state.refreshPending = false;
      state.forceRefreshPending = false;
      if (pending) {
        void refreshCurrentMetadata(pendingForce);
      }
    }
  }

  function queueRefresh(delay = 180) {
    if (state.mutationTimer || state.disposed) {
      return;
    }
    state.mutationTimer = window.setTimeout(() => {
      state.mutationTimer = 0;
      if (isPluginEnabled()) {
        void refreshCurrentMetadata(false);
      }
    }, delay);
  }

  function ensureObserver() {
    if (state.observer || state.disposed) {
      return;
    }
    state.observer = new MutationObserver((mutations) => {
      if (
        mutations.some((mutation) =>
          Array.from(mutation.addedNodes || []).some((node) =>
            node instanceof Element &&
            (
              node.matches?.("[role='tab'], img, video") ||
              node.querySelector?.("[role='tab'], img, video")
            )))
      ) {
        queueRefresh();
        scheduleMetadataUiRender();
      }
    });
    state.observer.observe(document.documentElement, {
      childList: true,
      subtree: true,
    });
  }

  function installDetailsUiTracking() {
    if (state.detailsUiHandler || state.disposed) {
      return;
    }
    state.detailsUiHandler = () => scheduleMetadataUiRender();
    document.addEventListener("click", state.detailsUiHandler, true);
    document.addEventListener("focusin", state.detailsUiHandler, true);
    window.addEventListener("popstate", state.detailsUiHandler);
    window.addEventListener("hashchange", state.detailsUiHandler);
  }

  function uninstallDetailsUiTracking() {
    if (!state.detailsUiHandler) {
      return;
    }
    document.removeEventListener("click", state.detailsUiHandler, true);
    document.removeEventListener("focusin", state.detailsUiHandler, true);
    window.removeEventListener("popstate", state.detailsUiHandler);
    window.removeEventListener("hashchange", state.detailsUiHandler);
    state.detailsUiHandler = null;
  }

  function deactivate() {
    if (state.timer) {
      window.clearTimeout(state.timer);
      state.timer = 0;
    }
    if (state.patchRetryTimer) {
      window.clearInterval(state.patchRetryTimer);
      state.patchRetryTimer = 0;
    }
    if (state.metadataUiTimer) {
      window.clearTimeout(state.metadataUiTimer);
      state.metadataUiTimer = 0;
    }
    restoreOriginalState();
    uninstallNativePatches();
    uninstallPostPlaySync();
    uninstallDetailsUiTracking();
    state.currentAppId = 0;
    removeAchievementNotice();
    removeLegacySurface();
  }

  function ensureActive() {
    removeLegacySurface();
    if (!isPluginEnabled()) {
      deactivate();
      return;
    }
    ensureObserver();
    installDetailsUiTracking();
    installNativePatches() || schedulePatchRetry();
    installPostPlaySync();
    queueRefresh(25);
    scheduleMetadataUiRender(120);
  }
  state.ensureActive = ensureActive;

  state.storageHandler = (event) => {
    if (event?.key !== storageKey) {
      return;
    }
    void refreshSummary(true).then(ensureActive);
  };
  window.addEventListener("storage", state.storageHandler);

  state.focusHandler = () => {
    void refreshSummary(false).then(ensureActive);
  };
  window.addEventListener("focus", state.focusHandler);

  if (typeof window.BroadcastChannel === "function") {
    try {
      state.channel = new window.BroadcastChannel(channelName);
      state.channel.addEventListener("message", (event) => {
        if (event?.data?.type === "stores-changed") {
          void refreshSummary(true).then(ensureActive);
          return;
        }
        if (event?.data?.type === nativeMetadataMessageType) {
          const snapshot = event?.data?.snapshot;
          const appId = Number(snapshot?.steamAppId || 0);
          const applyReceivedSnapshot = () => {
            if (!isPluginEnabled() || !isManagedAppId(appId)) {
              return;
            }
            state.nativeSnapshots.set(appId, snapshot);
            state.lastFetchAtByApp.set(appId, Date.now());
            installNativePatches() || schedulePatchRetry();
            applyNativeSnapshot(snapshot);
          };
          if (!state.summary) {
            void refreshSummary(false).then(applyReceivedSnapshot);
          } else {
            applyReceivedSnapshot();
          }
        }
      });
    } catch (_) {
      state.channel = null;
    }
  }

  const shared = window.__steamLoaderOmniLibraryStateStore;
  if (shared?.subscribe) {
    state.summaryUnsubscribe = shared.subscribe((summary) => {
      state.summary = summary;
      ensureActive();
    });
  }

  state.dispose = () => {
    if (state.disposed) {
      return;
    }
    state.disposed = true;
    deactivate();
    state.observer?.disconnect?.();
    state.observer = null;
    if (state.mutationTimer) {
      window.clearTimeout(state.mutationTimer);
      state.mutationTimer = 0;
    }
    if (state.metadataUiTimer) {
      window.clearTimeout(state.metadataUiTimer);
      state.metadataUiTimer = 0;
    }
    uninstallDetailsUiTracking();
    window.removeEventListener("storage", state.storageHandler);
    window.removeEventListener("focus", state.focusHandler);
    try {
      state.summaryUnsubscribe?.();
    } catch (_) {}
    try {
      state.channel?.close?.();
    } catch (_) {}
    removeLegacySurface();
  };

  void refreshSummary(true).then(ensureActive);
})();
