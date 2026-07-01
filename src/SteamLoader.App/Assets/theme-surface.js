(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const stateVersion = 14;
  const themeScanEventName = "steamtools:theme-scan-complete";
  const localizedText = (...codes) => String.fromCharCode(...codes);
  const localizedSignals = Object.freeze({
    play: localizedText(83, 80, 73, 69, 76, 69, 78),
    playtime: localizedText(83, 80, 73, 69, 76, 90, 69, 73, 84),
    lastPlayed: localizedText(90, 85, 76, 69, 84, 90, 84, 32, 71, 69, 83, 80, 73, 69, 76, 84),
    activityAscii: localizedText(65, 75, 84, 73, 86, 73, 84, 65, 84),
    activityNative: localizedText(65, 75, 84, 73, 86, 73, 84, 196, 84),
    yourStuff: localizedText(73, 72, 82, 69, 32, 68, 73, 78, 71, 69),
    gameInfo: localizedText(83, 80, 73, 69, 76, 73, 78, 70, 79),
    achievements: localizedText(69, 82, 70, 79, 76, 71, 69),
    lastSession: localizedText(76, 69, 84, 90, 84, 69, 32, 83, 73, 84, 90, 85, 78, 71),
    manage: localizedText(86, 69, 82, 87, 65, 76, 84, 69, 78),
    friendsPlaying: localizedText(70, 82, 69, 85, 78, 68, 69, 44, 32, 68, 73, 69, 32, 83, 80, 73, 69, 76, 69, 78),
    spaceRequired: localizedText(83, 80, 69, 73, 67, 72, 69, 82, 80, 76, 65, 84, 90),
    recentGames: localizedText(86, 79, 82, 32, 75, 85, 82, 90, 69, 77, 32, 71, 69, 83, 80, 73, 69, 76, 84),
    libraryMore: localizedText(77, 69, 72, 82, 32, 73, 78, 32, 73, 72, 82, 69, 82, 32, 66, 73, 66, 76, 73, 79, 84, 72, 69, 75),
    whatsNew: localizedText(87, 65, 83, 32, 73, 83, 84, 32, 78, 69, 85),
    recommended: localizedText(69, 77, 80, 70, 79, 72, 76, 69, 78),
    allGames: localizedText(65, 76, 76, 69, 32, 83, 80, 73, 69, 76, 69),
    installed: localizedText(73, 78, 83, 84, 65, 76, 76, 73, 69, 82, 84),
  });
  const detailViewMarkers = [
    localizedSignals.playtime,
    "PLAYTIME",
    "PLAY TIME",
    localizedSignals.activityNative,
    "ACTIVITY",
    localizedSignals.achievements,
    "ACHIEVEMENTS",
    localizedSignals.lastSession,
    "LAST SESSION",
    localizedSignals.manage,
    "MANAGE",
    localizedSignals.friendsPlaying,
    "FRIENDS WHO PLAY",
  ];
  const appIdPatterns = [
    /\/apps\/(\d+)\//i,
    /\/images\/apps\/(\d+)\//i,
    /\/libraryassets\/(\d+)\//i,
    /[?&]appid=(\d+)/i,
    /\/appdetails\/(\d+)/i,
  ];
  const detailClassFragments = [
    "sharedappdetailsheader_",
    "basicappdetailssectionstyler_",
    "appdetailsplaysection_",
  ];
  const semanticDetailMappings = [
    ["sharedappdetailsheader_topcapsule_", "steamloader-theme-game-detail-topcapsule"],
    ["sharedappdetailsheader_headerbackgroundimage_", "steamloader-theme-game-detail-hero"],
    ["sharedappdetailsheader_imgsrc_", "steamloader-theme-game-detail-hero-image"],
    ["sharedappdetailsheader_titlesection_", "steamloader-theme-game-detail-title-section"],
    ["sharedappdetailsheader_svgtitle_", "steamloader-theme-game-detail-logo"],
    ["sharedappdetailsheader_boxsizer_", "steamloader-theme-game-detail-logo-box"],
    ["basicappdetailssectionstyler_playsection_", "steamloader-theme-game-detail-playbar"],
    ["appdetailsplaysection_cloudstatusrow_", "steamloader-theme-game-detail-cloud-status"],
    ["appdetailsplaysection_cloudstatuslabel_", "steamloader-theme-game-detail-cloud-label"],
    ["appdetailsplaysection_cloudstatusicon_", "steamloader-theme-game-detail-cloud-icon"],
    ["appdetailsplaysection_cloudsyncproblem_", "steamloader-theme-game-detail-cloud-problem"],
  ];
  const themeMarkerClasses = [
    "steamloader-theme-artwork",
    "steamloader-theme-artwork-host",
    "steamloader-theme-artwork-panel",
    "steamloader-theme-artwork-bg",
    "steamloader-theme-artwork-portrait",
    "steamloader-theme-artwork-landscape",
    "steamloader-theme-artwork-square",
    "steamloader-theme-portrait",
    "steamloader-theme-portrait-host",
    "steamloader-theme-portrait-bg",
    "steamloader-theme-game-card",
    "steamloader-theme-game-card-host",
    "steamloader-theme-game-card-portrait",
    "steamloader-theme-game-card-landscape",
    "steamloader-theme-game-card-square",
    "steamloader-theme-game-detail",
    "steamloader-theme-game-detail-art",
    "steamloader-theme-game-detail-copy",
    "steamloader-theme-game-detail-topcapsule",
    "steamloader-theme-game-detail-hero",
    "steamloader-theme-game-detail-hero-image",
    "steamloader-theme-game-detail-playbar",
    "steamloader-theme-game-detail-title-section",
    "steamloader-theme-game-detail-logo",
    "steamloader-theme-game-detail-logo-box",
    "steamloader-theme-game-detail-cloud-status",
    "steamloader-theme-game-detail-cloud-label",
    "steamloader-theme-game-detail-cloud-icon",
    "steamloader-theme-game-detail-cloud-problem",
    "steamloader-theme-ui-toggle",
    "steamloader-theme-ui-toggle-on",
  ];

  const previousState = window.__steamLoaderThemeSurfaceState;
  if (previousState?.version !== stateVersion) {
    if (previousState?.timerHandle) {
      window.clearInterval(previousState.timerHandle);
    }

    if (previousState?.scanHandle) {
      window.clearTimeout(previousState.scanHandle);
    }

    if (previousState?.quickScanHandle) {
      window.cancelAnimationFrame(previousState.quickScanHandle);
    }

    if (previousState?.cleanupHandle) {
      window.clearTimeout(previousState.cleanupHandle);
    }

    if (previousState?.observer) {
      previousState.observer.disconnect();
    }
  }

  const state =
    previousState?.version === stateVersion
      ? previousState
      : (window.__steamLoaderThemeSurfaceState = {
          version: stateVersion,
          installed: false,
          timerHandle: null,
          scanHandle: null,
          quickScanHandle: null,
          quickScanRoots: new Set(),
          cleanupHandle: null,
          activeScanNodes: null,
          observer: null,
          markedNodes: new Set(),
          didInitialFullScan: false,
          lastResolveKey: "",
          lastResolvedAt: 0,
          lastCssText: "",
          lastScanAt: 0,
          lastSurfaceProbeAt: 0,
          lastSurfaceProbeIsLibraryGrid: false,
        });

  if (!(state.markedNodes instanceof Set)) {
    state.markedNodes = new Set();
  }

  if (!(state.quickScanRoots instanceof Set)) {
    state.quickScanRoots = new Set();
  }

  function ensureStyleElement() {
    let style = document.getElementById("steamloader-global-theme-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "steamloader-global-theme-style";
      document.head.append(style);
    }

    return style;
  }

  function clearThemeMarkers() {
    for (const node of state.markedNodes) {
      if (node instanceof Element) {
        node.classList.remove(...themeMarkerClasses);
      }
    }

    state.markedNodes.clear();
  }

  function beginFullScan() {
    state.activeScanNodes = new Set();
  }

  function finishFullScan() {
    const activeScanNodes = state.activeScanNodes;
    state.activeScanNodes = null;

    if (!(activeScanNodes instanceof Set)) {
      return;
    }

    for (const node of [...state.markedNodes]) {
      if (!(node instanceof Element) || !node.isConnected || !activeScanNodes.has(node)) {
        if (node instanceof Element) {
          node.classList.remove(...themeMarkerClasses);
        }

        state.markedNodes.delete(node);
      }
    }
  }

  function pruneDisconnectedMarkers() {
    state.cleanupHandle = null;

    for (const node of [...state.markedNodes]) {
      if (!(node instanceof Element) || !node.isConnected) {
        if (node instanceof Element) {
          node.classList.remove(...themeMarkerClasses);
        }

        state.markedNodes.delete(node);
      }
    }
  }

  function queueCleanup(delay = 1800) {
    if (state.cleanupHandle) {
      return;
    }

    state.cleanupHandle = window.setTimeout(pruneDisconnectedMarkers, delay);
  }

  function markNode(node, ...classes) {
    if (!(node instanceof Element)) {
      return;
    }

    node.classList.add(...classes);
    state.markedNodes.add(node);

    if (state.activeScanNodes instanceof Set) {
      state.activeScanNodes.add(node);
    }
  }

  function normalizeText(value) {
    return (value || "")
      .replace(/\s+/g, " ")
      .trim()
      .normalize("NFKD")
      .replace(/[\u0300-\u036f]/g, "")
      .toUpperCase();
  }

  function classText(node) {
    return String(node?.className || "").toLowerCase();
  }

  function hasClassFragment(node, fragment) {
    return classText(node).includes(fragment.toLowerCase());
  }

  function extractAppIdFromUrlish(value) {
    if (!value) {
      return null;
    }

    for (const pattern of appIdPatterns) {
      const match = String(value).match(pattern);
      if (match) {
        const parsed = Number(match[1]);
        if (Number.isInteger(parsed) && parsed > 0) {
          return parsed;
        }
      }
    }

    return null;
  }

  function getElementUrlishValues(node) {
    const values = [
      node?.getAttribute?.("href"),
      node?.getAttribute?.("src"),
      node?.getAttribute?.("style"),
      node instanceof HTMLImageElement ? node.currentSrc : "",
      node instanceof HTMLImageElement ? node.src : "",
      node instanceof HTMLImageElement ? node.alt : "",
      node instanceof HTMLElement ? node.style.backgroundImage : "",
    ];

    return values.filter(Boolean);
  }

  function findAppIdInElement(node) {
    if (!(node instanceof Element)) {
      return null;
    }

    for (const value of getElementUrlishValues(node)) {
      const appId = extractAppIdFromUrlish(value);
      if (appId) {
        return appId;
      }
    }

    for (const candidate of node.querySelectorAll("img, a, [style], [href], [src]")) {
      for (const value of getElementUrlishValues(candidate)) {
        const appId = extractAppIdFromUrlish(value);
        if (appId) {
          return appId;
        }
      }
    }

    return null;
  }

  function hasDetailClassMarker(node) {
    return detailClassFragments.some((fragment) => hasClassFragment(node, fragment));
  }

  function getSemanticDetailScore(container) {
    if (!(container instanceof Element)) {
      return 0;
    }

    const matches = new Set();
    if (hasDetailClassMarker(container)) {
      matches.add("self");
    }

    for (const node of container.querySelectorAll("[class]")) {
      for (const fragment of detailClassFragments) {
        if (hasClassFragment(node, fragment)) {
          matches.add(fragment);
        }
      }
    }

    return matches.size;
  }

  function getDetailTextSignalCount(container) {
    const text = normalizeText(container?.innerText || "");
    if (!text) {
      return 0;
    }

    const signals = [
      "PLAY TIME",
      "PLAYTIME",
      localizedSignals.playtime,
      "LAST PLAYED",
      localizedSignals.lastPlayed,
      "STEAM CLOUD",
      "CONTROLLER",
      "ACTIVITY",
      localizedSignals.activityAscii,
      "YOUR STUFF",
      localizedSignals.yourStuff,
      "COMMUNITY",
      "GAME INFO",
      localizedSignals.gameInfo,
      "ACHIEVEMENTS",
      localizedSignals.achievements,
      "SPACE REQUIRED",
      localizedSignals.spaceRequired,
    ];

    return signals.reduce((count, signal) => count + (text.includes(signal) ? 1 : 0), 0);
  }

  function isHomeOrLibraryHub(container) {
    const text = normalizeText(container?.innerText || "");
    if (!text) {
      return false;
    }

    const hubSignals = [
      "RECENT GAMES",
      localizedSignals.recentGames,
      "VIEW MORE IN YOUR LIBRARY",
      localizedSignals.libraryMore,
      "WHAT'S NEW",
      localizedSignals.whatsNew,
      "RECOMMENDED",
      localizedSignals.recommended,
    ];

    const detailSignals = getDetailTextSignalCount(container);
    const hasCloudOrTabs =
      text.includes("STEAM CLOUD") ||
      text.includes("YOUR STUFF") ||
      text.includes(localizedSignals.yourStuff) ||
      text.includes("GAME INFO") ||
      text.includes(localizedSignals.gameInfo);

    return hubSignals.some((signal) => text.includes(signal)) && (!hasCloudOrTabs || detailSignals < 3);
  }

  function isLibraryGridSurface() {
    const now = Date.now();
    if (now - (state.lastSurfaceProbeAt || 0) < 600) {
      return Boolean(state.lastSurfaceProbeIsLibraryGrid);
    }

    state.lastSurfaceProbeAt = now;

    const portraitCards = document.querySelectorAll(
      "img._24_AuLm54JVe1Zc0AApCDR, img._3d_bT685lnWotXxgzKW6am, img[src^='/customimages/'][src*='p.'], img[src*='library_600x900']",
    ).length;

    if (portraitCards < 4) {
      state.lastSurfaceProbeIsLibraryGrid = false;
      return false;
    }

    const text = normalizeText(document.body?.innerText || "");
    const detailSignals = getDetailTextSignalCount(document.body);
    const hasLibraryTabs =
      text.includes("ALL GAMES") ||
      text.includes(localizedSignals.allGames) ||
      text.includes("INSTALLED") ||
      text.includes(localizedSignals.installed) ||
      text.includes("NON-STEAM") ||
      text.includes("SOUNDTRACKS") ||
      text.includes("CONTROLLER FRIENDLY");
    const hasDetailChrome =
      text.includes("STEAM CLOUD") ||
      text.includes("YOUR STUFF") ||
      text.includes(localizedSignals.yourStuff) ||
      text.includes("GAME INFO") ||
      text.includes(localizedSignals.gameInfo);

    state.lastSurfaceProbeIsLibraryGrid = hasLibraryTabs && (!hasDetailChrome || detailSignals < 3);
    return Boolean(state.lastSurfaceProbeIsLibraryGrid);
  }

  function hasLargeDetailHero(container, containerRect) {
    for (const candidate of container.querySelectorAll("img, canvas, video, [style*='background']")) {
      if (!(candidate instanceof HTMLElement)) {
        continue;
      }

      const rect = candidate.getBoundingClientRect();
      if (
        rect.width >= Math.min(520, containerRect.width * 0.5) &&
        rect.height >= 180 &&
        rect.top <= containerRect.top + Math.max(180, containerRect.height * 0.35)
      ) {
        return true;
      }
    }

    return false;
  }

  function isLikelyDetailViewContainer(container) {
    if (!(container instanceof HTMLElement)) {
      return false;
    }

    if (container === document.body || container === document.documentElement) {
      return false;
    }

    const rect = container.getBoundingClientRect();
    if (rect.width < 480 || rect.height < 260) {
      return false;
    }

    if (isHomeOrLibraryHub(container)) {
      return false;
    }

    const semanticScore = getSemanticDetailScore(container);
    const detailSignalCount = getDetailTextSignalCount(container);
    if (semanticScore >= 2 && detailSignalCount >= 2) {
      return true;
    }

    const routeAppId = extractAppIdFromUrlish(location.href);
    const containedAppId = findAppIdInElement(container);
    if (!routeAppId && !containedAppId) {
      return false;
    }

    return detailSignalCount >= 1 && hasLargeDetailHero(container, rect);
  }

  function classifyArtworkRect(rect) {
    if (rect.width < 96 || rect.height < 96) {
      return null;
    }

    if (rect.height >= 170 && rect.height >= rect.width * 1.18) {
      return "portrait";
    }

    if (rect.width >= 180 && rect.width >= rect.height * 1.55) {
      return "landscape";
    }

    if (rect.width >= 160 && rect.height >= 160) {
      return "square";
    }

    return null;
  }

  function isNearlySameSize(referenceRect, candidateRect) {
    const widthDifference = Math.abs(referenceRect.width - candidateRect.width);
    const heightDifference = Math.abs(referenceRect.height - candidateRect.height);
    const widthTolerance = Math.max(10, referenceRect.width * 0.08);
    const heightTolerance = Math.max(10, referenceRect.height * 0.08);

    return widthDifference <= widthTolerance && heightDifference <= heightTolerance;
  }

  function isArtworkSource(source) {
    if (!source) {
      return false;
    }

    const normalizedSource = source.toLowerCase();
    return (
      normalizedSource.includes("/assets/") ||
      normalizedSource.includes("/customimages/") ||
      normalizedSource.includes("steamstatic.com") ||
      normalizedSource.includes("shared.steamstatic.com") ||
      normalizedSource.includes("steamloopback.host") ||
      normalizedSource.includes("library_") ||
      normalizedSource.includes("librarycapsule") ||
      normalizedSource.includes("library_capsule") ||
      normalizedSource.includes("libraryhero") ||
      normalizedSource.includes("library_hero") ||
      normalizedSource.includes("library_header") ||
      normalizedSource.includes("/header.") ||
      normalizedSource.includes("/capsule.") ||
      normalizedSource.includes("/capsule_") ||
      normalizedSource.includes("/hero")
    );
  }

  function hasVisibleText(node) {
    return normalizeText(node.innerText || "").length > 0;
  }

  function getDetailViewContainer(node) {
    let current = node;
    for (let depth = 0; current && depth < 10; depth += 1, current = current.parentElement) {
      const rect = current.getBoundingClientRect();
      if (rect.width < 320 || rect.height < 160) {
        continue;
      }

      const text = normalizeText(current.innerText || "");
      let markerHits = 0;
      for (const marker of detailViewMarkers) {
        if (text.includes(marker)) {
          markerHits += 1;
        }
      }

      if (!isHomeOrLibraryHub(current) && (markerHits >= 2 || isLikelyDetailViewContainer(current))) {
        return current;
      }
    }

    return null;
  }

  function isDetailViewArtwork(node) {
    return Boolean(getDetailViewContainer(node));
  }

  function hasImageDescendant(node) {
    return Boolean(node.querySelector("img, video, canvas"));
  }

  function isLikelyDetailCopy(node) {
    if (!node || hasImageDescendant(node)) {
      return false;
    }

    const text = normalizeText(node.innerText || "");
    if (text.length < 3 || text.length > 220) {
      return false;
    }

    const rect = node.getBoundingClientRect();
    return rect.width >= 240 && rect.height >= 18 && rect.height <= 180;
  }

  function findBestDetailNode(container, predicate) {
    const candidates = [...container.querySelectorAll("div, section, span, img, canvas, video")]
      .filter((candidate) => candidate instanceof HTMLElement && predicate(candidate));

    if (candidates.length === 0) {
      return null;
    }

    candidates.sort((left, right) => {
      const leftRect = left.getBoundingClientRect();
      const rightRect = right.getBoundingClientRect();
      return rightRect.width * rightRect.height - leftRect.width * leftRect.height;
    });

    return candidates[0];
  }

  function markDetailViewStructure(container) {
    const containerRect = container.getBoundingClientRect();

    for (const node of container.querySelectorAll("[class]")) {
      for (const [fragment, markerClass] of semanticDetailMappings) {
        if (hasClassFragment(node, fragment)) {
          markNode(node, markerClass);
        }
      }
    }

    const topCapsule = findBestDetailNode(container, (candidate) => {
      const rect = candidate.getBoundingClientRect();
      const text = normalizeText(candidate.innerText || "");
      return (
        hasClassFragment(candidate, "sharedappdetailsheader_topcapsule_") ||
        (rect.width >= containerRect.width * 0.92 &&
        rect.height >= 220 &&
        rect.height <= 520 &&
        rect.top <= containerRect.top + 80 &&
        rect.bottom <= containerRect.top + containerRect.height * 0.55 &&
        text.length < 16)
      );
    });

    if (topCapsule) {
      markNode(topCapsule, "steamloader-theme-game-detail-topcapsule");
    }

    const heroImages = [...container.querySelectorAll("img, canvas, video")].filter((candidate) => {
      if (!(candidate instanceof HTMLElement)) {
        return false;
      }

      const rect = candidate.getBoundingClientRect();
      return (
        hasClassFragment(candidate, "sharedappdetailsheader_imgsrc_") ||
        hasClassFragment(candidate, "sharedappdetailsheader_headerbackgroundimage_") ||
        (rect.width >= containerRect.width * 0.65 &&
        rect.height >= 220 &&
        rect.top <= containerRect.top + containerRect.height * 0.35)
      );
    });

    for (const heroImage of heroImages) {
      markNode(heroImage, "steamloader-theme-game-detail-hero-image");

      let current = heroImage.parentElement;
      for (let depth = 0; current && depth < 4; depth += 1, current = current.parentElement) {
        const rect = current.getBoundingClientRect();
        if (rect.width >= containerRect.width * 0.75 && rect.height >= 220) {
          markNode(current, "steamloader-theme-game-detail-hero");
        }
      }
    }

    const logoImage = findBestDetailNode(container, (candidate) => {
      if (!(candidate instanceof HTMLImageElement)) {
        return false;
      }

      const rect = candidate.getBoundingClientRect();
      const centerX = rect.left + rect.width / 2;
      return (
        hasClassFragment(candidate, "sharedappdetailsheader_svgtitle_") ||
        (rect.width >= 180 &&
        rect.width <= 720 &&
        rect.height >= 50 &&
        rect.height <= 240 &&
        rect.top <= containerRect.top + containerRect.height * 0.4 &&
        centerX >= containerRect.left + containerRect.width * 0.25 &&
        centerX <= containerRect.right - containerRect.width * 0.25)
      );
    });

    if (logoImage) {
      markNode(logoImage, "steamloader-theme-game-detail-logo");
      if (logoImage.parentElement) {
        markNode(logoImage.parentElement, "steamloader-theme-game-detail-logo-box");
      }
    }

    const playBar = findBestDetailNode(container, (candidate) => {
      const rect = candidate.getBoundingClientRect();
      const text = normalizeText(candidate.innerText || "");
      const steamClassMatch =
        hasClassFragment(candidate, "basicappdetailssectionstyler_playsection_") ||
        hasClassFragment(candidate, "sharedappdetailsheader_titlesection_");
      return (
        steamClassMatch ||
        (rect.width >= containerRect.width * 0.7 &&
        rect.height >= 40 &&
        rect.height <= 140 &&
        rect.top >= containerRect.top + 180 &&
        rect.top <= containerRect.top + 470 &&
        (text.includes("PLAYTIME") ||
          text.includes(localizedSignals.playtime) ||
          text.includes("LAST PLAYED") ||
          text.includes(localizedSignals.lastPlayed) ||
          text.includes("CONTROLLER") ||
          text.startsWith("PLAY ") ||
          text.startsWith(`${localizedSignals.play} `)))
      );
    });

    if (playBar) {
      markNode(
        playBar,
        "steamloader-theme-game-detail-playbar",
        "steamloader-theme-game-detail-title-section",
      );
    }

    const cloudStatus = findBestDetailNode(container, (candidate) => {
      const rect = candidate.getBoundingClientRect();
      const text = normalizeText(candidate.innerText || "");
      return (
        hasClassFragment(candidate, "appdetailsplaysection_cloudstatusrow_") ||
        (rect.width >= 120 &&
        rect.width <= containerRect.width &&
        rect.height >= 18 &&
        rect.height <= 60 &&
        text.includes("STEAM CLOUD"))
      );
    });

    if (cloudStatus) {
      markNode(cloudStatus, "steamloader-theme-game-detail-cloud-status");

      for (const candidate of cloudStatus.querySelectorAll("div, span")) {
        const text = normalizeText(candidate.innerText || "");
        if (text.includes("STEAM CLOUD") || hasClassFragment(candidate, "appdetailsplaysection_cloudstatuslabel_")) {
          markNode(candidate, "steamloader-theme-game-detail-cloud-label");
        }
      }

      for (const candidate of cloudStatus.querySelectorAll("svg")) {
        if (candidate.parentElement) {
          markNode(candidate.parentElement, "steamloader-theme-game-detail-cloud-icon");
        }
      }
    }
  }

  function markDetailContainer(container, sourceNode = null) {
    if (!container || isHomeOrLibraryHub(container)) {
      return;
    }

    markNode(container, "steamloader-theme-game-detail");
    if (sourceNode) {
      markNode(sourceNode, "steamloader-theme-game-detail-art");
    }

    const copyCandidates = [...container.querySelectorAll("div, section, span, h1, h2, h3")]
      .filter((candidate) => candidate instanceof HTMLElement && isLikelyDetailCopy(candidate))
      .slice(0, 6);

    for (const candidate of copyCandidates) {
      markNode(candidate, "steamloader-theme-game-detail-copy");
    }

    markDetailViewStructure(container);
  }

  function markDetailView(node) {
    markDetailContainer(getDetailViewContainer(node), node);
  }

  function scanSemanticDetailContainers() {
    const containers = new Set();
    const semanticNodes = [...document.querySelectorAll("[class]")]
      .filter((node) => node instanceof HTMLElement)
      .filter((node) => detailClassFragments.some((fragment) => hasClassFragment(node, fragment)));

    for (const node of semanticNodes) {
      let current = node;
      for (let depth = 0; current && depth < 10; depth += 1, current = current.parentElement) {
        if (isLikelyDetailViewContainer(current)) {
          containers.add(current);
        }
      }
    }

    for (const container of containers) {
      markDetailContainer(container);
    }
  }

  function getArtworkHost(node) {
    const referenceRect = node.getBoundingClientRect();
    let current = node.parentElement;
    let bestSizedWrapper = null;

    while (current) {
      const candidateRect = current.getBoundingClientRect();
      if (!isNearlySameSize(referenceRect, candidateRect)) {
        break;
      }

      if (!current.classList.contains("Panel") && !hasVisibleText(current)) {
        bestSizedWrapper = current;
      }

      current = current.parentElement;
    }

    return (
      bestSizedWrapper ||
      node.closest(".Focusable") ||
      node.closest("[role='link']") ||
      node.closest("a") ||
      node.parentElement
    );
  }

  function markArtworkImage(image) {
    const rect = image.getBoundingClientRect();
    const artworkType = classifyArtworkRect(rect);
    if (!artworkType) {
      return;
    }

    const src = image.currentSrc || image.src || "";
    if (!isArtworkSource(src)) {
      return;
    }

    if (isDetailViewArtwork(image)) {
      markDetailView(image);
      return;
    }

    markNode(
      image,
      "steamloader-theme-artwork",
      `steamloader-theme-artwork-${artworkType}`,
      "steamloader-theme-game-card",
      `steamloader-theme-game-card-${artworkType}`,
    );

    if (artworkType === "portrait") {
      markNode(image, "steamloader-theme-portrait");
    }

    const host = getArtworkHost(image);

    if (host) {
      markNode(
        host,
        "steamloader-theme-artwork-host",
        `steamloader-theme-artwork-${artworkType}`,
        "steamloader-theme-game-card-host",
        `steamloader-theme-game-card-${artworkType}`,
      );

      if (artworkType === "portrait") {
        markNode(host, "steamloader-theme-portrait-host");
      }
    }
  }

  function markArtworkBackground(node) {
    const style = window.getComputedStyle(node);
    const backgroundImage = style.backgroundImage || "";
    if (!backgroundImage || backgroundImage === "none") {
      return;
    }

    if (!isArtworkSource(backgroundImage)) {
      return;
    }

    const rect = node.getBoundingClientRect();
    const artworkType = classifyArtworkRect(rect);
    if (!artworkType) {
      return;
    }

    if (isDetailViewArtwork(node)) {
      markDetailView(node);
      return;
    }

    if (hasVisibleText(node)) {
      return;
    }

    markNode(
      node,
      "steamloader-theme-artwork-bg",
      `steamloader-theme-artwork-${artworkType}`,
      "steamloader-theme-game-card",
      "steamloader-theme-game-card-host",
      `steamloader-theme-game-card-${artworkType}`,
    );

    if (artworkType === "portrait") {
      markNode(node, "steamloader-theme-portrait-bg");
    }
  }

  function isLikelyToggleNode(node) {
    if (!(node instanceof HTMLElement)) {
      return false;
    }

    const rect = node.getBoundingClientRect();
    if (rect.width < 22 || rect.width > 180 || rect.height < 12 || rect.height > 80) {
      return false;
    }

    const className = `${node.className || ""}`.toLowerCase();
    return (
      node.getAttribute("role") === "switch" ||
      node.hasAttribute("aria-checked") ||
      node.hasAttribute("aria-pressed") ||
      className.includes("toggle") ||
      className.includes("switch")
    );
  }

  function isToggleOn(node) {
    const ariaChecked = node.getAttribute("aria-checked");
    const ariaPressed = node.getAttribute("aria-pressed");
    const className = `${node.className || ""}`.toLowerCase();

    return (
      ariaChecked === "true" ||
      ariaPressed === "true" ||
      className.includes("is-on") ||
      className.includes("checked") ||
      className.includes("active")
    );
  }

  function markToggleNode(node) {
    if (!isLikelyToggleNode(node)) {
      return;
    }

    markNode(node, "steamloader-theme-ui-toggle");
    if (isToggleOn(node)) {
      markNode(node, "steamloader-theme-ui-toggle-on");
    }
  }

  function scanThemeSubtree(root) {
    const scope =
      root instanceof Document
        ? root
        : root instanceof Element
          ? root
          : null;

    if (!scope) {
      return;
    }

    if (scope instanceof HTMLImageElement) {
      markArtworkImage(scope);
    }

    if (scope instanceof HTMLElement) {
      markArtworkBackground(scope);
      markToggleNode(scope);
    }

    const images = scope instanceof Document ? scope.images : scope.querySelectorAll("img");
    for (const image of images) {
      markArtworkImage(image);
    }

    const backgroundCandidates = scope.querySelectorAll(
      ".Panel, .Focusable, [role='link'], a, [style*='background'], [role='switch'], [aria-checked], [aria-pressed], [class*='Toggle'], [class*='toggle'], [class*='Switch'], [class*='switch']",
    );

    for (const node of backgroundCandidates) {
      if (!(node instanceof HTMLElement)) {
        continue;
      }

      markArtworkBackground(node);
      markToggleNode(node);
    }
  }

  function scanThemeTargets() {
    state.scanHandle = null;
    state.lastScanAt = Date.now();

    if (isLibraryGridSurface()) {
      state.didInitialFullScan = true;
      queueCleanup();
      window.dispatchEvent(new CustomEvent(themeScanEventName));
      return;
    }

    beginFullScan();
    try {
      scanThemeSubtree(document);
      scanSemanticDetailContainers();
    } finally {
      finishFullScan();
    }

    state.didInitialFullScan = true;
    window.dispatchEvent(new CustomEvent(themeScanEventName));
  }

  function queueQuickScan(root) {
    if (root instanceof Document || root instanceof Element) {
      state.quickScanRoots.add(root);
    }

    if (state.quickScanHandle) {
      return;
    }

    state.quickScanHandle = window.requestAnimationFrame(() => {
      state.quickScanHandle = null;
      const roots = [...state.quickScanRoots];
      state.quickScanRoots.clear();

      for (const queuedRoot of roots) {
        scanThemeSubtree(queuedRoot);
      }

      queueCleanup();
    });
  }

  function queueScan(delay = 420) {
    if (state.scanHandle) {
      return;
    }

    const elapsed = Date.now() - (state.lastScanAt || 0);
    const quietWindow = Math.max(0, 900 - elapsed);
    state.scanHandle = window.setTimeout(scanThemeTargets, Math.max(delay, quietWindow));
  }

  async function refreshThemeCss(force = false) {
    const resolveKey = `${document.title || ""}|${location.href || ""}`;
    const now = Date.now();
    if (!force && state.lastResolveKey === resolveKey && now - state.lastResolvedAt < 8000) {
      return;
    }

    try {
      const query = new URLSearchParams({
        title: document.title || "",
        url: location.href || "",
      });
      const response = await fetch(`${apiBase}api/themes/resolve-css?${query.toString()}`, { cache: "no-store" });
      if (!response.ok) {
        return;
      }

      const payload = await response.json();
      const cssText = payload && typeof payload.css === "string" ? payload.css : "";
      const styleElement = ensureStyleElement();
      const cssChanged = styleElement.textContent !== cssText;
      styleElement.textContent = cssText;
      state.lastCssText = cssText;

      state.lastResolveKey = resolveKey;
      state.lastResolvedAt = now;

      if (!state.didInitialFullScan || cssChanged) {
        queueScan(0);
      }
    } catch {
    }
  }

  function install() {
    ensureStyleElement();
    queueScan(0);

    if (!state.observer) {
      state.observer = new MutationObserver((mutations) => {
        let shouldCleanup = false;
        const cssOnlyLibraryGrid = isLibraryGridSurface();

        for (const mutation of mutations) {
          if (mutation.type === "childList") {
            if (mutation.addedNodes.length > 0 || mutation.removedNodes.length > 0) {
              if (!cssOnlyLibraryGrid) {
                for (const node of mutation.addedNodes) {
                  queueQuickScan(node);
                }
              }

              if (mutation.removedNodes.length > 0) {
                shouldCleanup = true;
              }
            }
          }

          if (
            mutation.type === "attributes" &&
            (mutation.target instanceof HTMLImageElement || mutation.attributeName === "href")
          ) {
            if (!cssOnlyLibraryGrid) {
              queueQuickScan(mutation.target);
            }
          }
        }

        if (shouldCleanup) {
          queueCleanup();
        }
      });

      state.observer.observe(document.documentElement, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["src", "href"],
      });
    }

    if (state.timerHandle) {
      window.clearInterval(state.timerHandle);
    }

    state.timerHandle = window.setInterval(() => {
      if (!document.hidden) {
        queueCleanup();
        void refreshThemeCss();
      }
    }, 8000);

    void refreshThemeCss(true);
    state.installed = true;
    return true;
  }

  return install() ? "injected" : "waiting";
})();
