(() => {
  const apiBase = "__STEAMLOADER_API_BASE__";
  const stateVersion = 9;
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
    spaceRequired: localizedText(83, 80, 69, 73, 67, 72, 69, 82, 80, 76, 65, 84, 90),
    recentGames: localizedText(86, 79, 82, 32, 75, 85, 82, 90, 69, 77, 32, 71, 69, 83, 80, 73, 69, 76, 84),
    libraryMore: localizedText(77, 69, 72, 82, 32, 73, 78, 32, 73, 72, 82, 69, 82, 32, 66, 73, 66, 76, 73, 79, 84, 72, 69, 75),
    whatsNew: localizedText(87, 65, 83, 32, 73, 83, 84, 32, 78, 69, 85),
    recommended: localizedText(69, 77, 80, 70, 79, 72, 76, 69, 78),
  });
  const titleNoiseMarkers = [
    "PLAY",
    localizedSignals.play,
    "PLAYTIME",
    localizedSignals.playtime,
    "LAST PLAYED",
    localizedSignals.lastPlayed,
    "ACTIVITY",
    localizedSignals.activityAscii,
    localizedSignals.activityNative,
    "COMMUNITY",
    localizedSignals.gameInfo,
    "GAME INFO",
    "STEAM CLOUD",
    "ACHIEVEMENTS",
    localizedSignals.achievements,
    "CONTROLLER",
    "MAIN STORY",
    "MAIN + EXTRAS",
    "COMPLETIONIST",
    "ALL STYLES",
    "VIEW DETAILS",
    "DETAILS",
    "RECENT GAMES",
    "VIEW MORE IN YOUR LIBRARY",
    "WHAT'S NEW",
    "RECOMMENDED",
    "SP BPM",
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
  const detailTextPattern =
    new RegExp([
      "PLAY TIME",
      "PLAYTIME",
      localizedSignals.playtime,
      "LAST PLAYED",
      localizedSignals.lastPlayed,
      "STEAM CLOUD",
      "CONTROLLER",
      "ACTIVITY",
      localizedSignals.activityAscii,
      localizedSignals.activityNative,
      "YOUR STUFF",
      localizedSignals.yourStuff,
      "COMMUNITY",
      "GAME INFO",
      localizedSignals.gameInfo,
    ].join("|"), "i");

  const previousState = window.__steamToolsHltbSurfaceState;
  if (previousState?.version !== stateVersion) {
    if (previousState?.timerHandle) {
      window.clearInterval(previousState.timerHandle);
    }

    if (previousState?.refreshHandle) {
      window.clearTimeout(previousState.refreshHandle);
    }

    if (previousState?.observer) {
      previousState.observer.disconnect();
    }

    if (typeof previousState?.themeScanHandler === "function") {
      window.removeEventListener(themeScanEventName, previousState.themeScanHandler);
    }
  }

  const state =
    previousState?.version === stateVersion
      ? previousState
      : (window.__steamToolsHltbSurfaceState = {
          version: stateVersion,
          installed: false,
          timerHandle: null,
          refreshHandle: null,
          observer: null,
          themeScanHandler: null,
          inFlightKey: "",
          lastRequestKey: "",
          lastSnapshot: null,
          lastFetchAt: 0,
          lastRefreshAt: 0,
        });

  function ensureStyleElement() {
    let style = document.getElementById("steamtools-hltb-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "steamtools-hltb-style";
      document.head.append(style);
    }

    style.textContent = `
      .steamtools-hltb-host {
        position: relative;
      }

      .steamtools-hltb-panel {
        position: absolute;
        top: clamp(72px, 8vh, 104px);
        right: clamp(22px, 2.2vw, 34px);
        z-index: 7;
        display: flex;
        flex-direction: column;
        gap: 14px;
        width: min(620px, calc(100% - 44px));
        box-sizing: border-box;
        min-height: 136px;
        padding: 16px 18px;
        margin: 0;
        overflow: hidden;
        border-radius: 30px 34px 30px 34px;
        background:
          radial-gradient(circle at 86% 22%, rgba(255, 225, 155, 0.2), transparent 28%),
          radial-gradient(circle at 100% 0%, rgba(104, 181, 255, 0.14), transparent 24%),
          linear-gradient(135deg, rgba(9, 14, 20, 0.78), rgba(19, 28, 40, 0.48));
        border: 1px solid rgba(255, 255, 255, 0.14);
        box-shadow:
          0 20px 70px rgba(0, 0, 0, 0.3),
          inset 0 1px 0 rgba(255, 255, 255, 0.08);
        backdrop-filter: blur(22px) saturate(145%);
      }

      .steamtools-hltb-panel::before {
        content: "";
        position: absolute;
        inset: 0;
        background: linear-gradient(90deg, rgba(255, 255, 255, 0.04), transparent 28%, transparent 72%, rgba(255, 224, 156, 0.08));
        pointer-events: none;
      }

      .steamtools-hltb-panel > * {
        position: relative;
        z-index: 1;
      }

      .steamtools-hltb-header {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 14px;
      }

      .steamtools-hltb-header-copy {
        display: flex;
        flex-direction: column;
        gap: 6px;
        min-width: 0;
      }

      .steamtools-hltb-kicker {
        color: rgba(228, 237, 245, 0.72);
        font-size: 11px;
        line-height: 1;
        font-weight: 700;
        letter-spacing: 0.18em;
        text-transform: uppercase;
      }

      .steamtools-hltb-brand {
        color: #f5f9fc;
        font-size: clamp(20px, 2vw, 28px);
        line-height: 1;
        font-weight: 800;
        letter-spacing: -0.04em;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }

      .steamtools-hltb-toolbar {
        display: flex;
        align-items: center;
        justify-content: flex-end;
        gap: 8px;
        flex-wrap: wrap;
      }

      .steamtools-hltb-badge {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-height: 26px;
        padding: 0 10px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.08);
        color: rgba(236, 242, 248, 0.9);
        font-size: 10px;
        font-weight: 700;
        letter-spacing: 0.12em;
        text-transform: uppercase;
        white-space: nowrap;
      }

      .steamtools-hltb-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(118px, 1fr));
        gap: 10px;
        min-width: 0;
      }

      .steamtools-hltb-stat {
        display: flex;
        flex-direction: column;
        gap: 4px;
        min-width: 0;
        padding: 11px 12px 10px;
        border-radius: 18px;
        background: linear-gradient(180deg, rgba(255, 255, 255, 0.1), rgba(255, 255, 255, 0.04));
        border: 1px solid rgba(255, 255, 255, 0.09);
      }

      .steamtools-hltb-value {
        color: #f3f8fd;
        font-size: clamp(18px, 1.65vw, 24px);
        line-height: 1;
        font-weight: 800;
        letter-spacing: -0.04em;
        white-space: nowrap;
      }

      .steamtools-hltb-label {
        color: rgba(204, 216, 228, 0.74);
        font-size: clamp(9px, 0.78vw, 11px);
        line-height: 1.2;
        font-weight: 700;
        letter-spacing: 0.12em;
        text-transform: uppercase;
      }

      .steamtools-hltb-actions {
        display: flex;
        align-items: center;
        justify-content: flex-start;
      }

      .steamtools-hltb-button {
        appearance: none;
        border: 1px solid rgba(255, 255, 255, 0.14);
        border-radius: 999px;
        padding: 0 14px;
        min-height: 34px;
        background: rgba(255, 255, 255, 0.08);
        color: #f2f7fb;
        font-size: 11px;
        font-weight: 700;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        cursor: pointer;
        transition:
          background 160ms ease,
          border-color 160ms ease,
          transform 160ms ease;
      }

      .steamtools-hltb-button:hover {
        background: rgba(255, 255, 255, 0.14);
        border-color: rgba(255, 255, 255, 0.2);
        transform: translateY(-1px);
      }

      @media (max-width: 1360px) {
        .steamtools-hltb-panel {
          top: clamp(60px, 7vh, 86px);
          width: min(520px, calc(100% - 34px));
          min-height: 0;
          padding: 15px 16px;
          border-radius: 28px;
        }
      }

      @media (max-width: 1080px) {
        .steamtools-hltb-panel {
          width: min(360px, calc(100% - 24px));
          top: 58px;
          right: 16px;
          gap: 10px;
        }

        .steamtools-hltb-header {
          flex-direction: column;
          align-items: stretch;
        }

        .steamtools-hltb-toolbar {
          justify-content: flex-start;
        }

        .steamtools-hltb-stat {
          padding: 10px 11px 9px;
        }
      }

      @media (max-width: 860px) {
        .steamtools-hltb-panel {
          position: static;
          width: 100%;
          margin-top: 16px;
        }
      }
    `;

    return style;
  }

  function normalizeText(value) {
    return (value || "").replace(/\s+/g, " ").trim();
  }

  function normalizeTitleScore(value) {
    return normalizeText(value)
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

  function isVisibleElement(node) {
    return (
      node instanceof HTMLElement &&
      node.isConnected &&
      window.getComputedStyle(node).display !== "none" &&
      window.getComputedStyle(node).visibility !== "hidden"
    );
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

  function getSemanticDetailScore(container) {
    if (!(container instanceof Element)) {
      return 0;
    }

    const matches = new Set();
    for (const fragment of detailClassFragments) {
      if (hasClassFragment(container, fragment)) {
        matches.add(fragment);
      }
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

  function getAppDetailRouteId() {
    const match = String(location.href || "").match(/\/appdetails\/(\d+)/i);
    if (!match) {
      return null;
    }

    const parsed = Number(match[1]);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
  }

  function hasNativeDetailPlaySection(container) {
    if (!(container instanceof Element)) {
      return false;
    }

    if (container.querySelector(".steamloader-theme-game-detail-playbar")) {
      return true;
    }

    return [...container.querySelectorAll("[class]")].some((node) => {
      return (
        node instanceof HTMLElement &&
        (hasClassFragment(node, "basicappdetailssectionstyler_playsection_") ||
          hasClassFragment(node, "appdetailsplaysection_"))
      );
    });
  }

  function getDetailTextSignalCount(container) {
    const text = normalizeTitleScore(container?.innerText || "");
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
      localizedSignals.activityNative,
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
    const text = normalizeTitleScore(container?.innerText || "");
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

  function hasReliableDetailSignal(container) {
    if (isHomeOrLibraryHub(container)) {
      return false;
    }

    return hasNativeDetailPlaySection(container) || getDetailTextSignalCount(container) >= 2;
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

  function isLikelyDetailContainer(container) {
    if (!isVisibleElement(container)) {
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

    if (
      container.classList.contains("steamloader-theme-game-detail") &&
      hasReliableDetailSignal(container) &&
      hasLargeDetailHero(container, rect)
    ) {
      return true;
    }

    const semanticScore = getSemanticDetailScore(container);
    if (semanticScore >= 2 && hasReliableDetailSignal(container)) {
      return true;
    }

    const routeAppId = getAppDetailRouteId();
    const containedAppId = findAppIdInElement(container);
    if (!routeAppId && !containedAppId) {
      return false;
    }

    const hasAnyAppId = Boolean(routeAppId || containedAppId);
    if (hasAnyAppId && hasReliableDetailSignal(container) && hasLargeDetailHero(container, rect)) {
      return true;
    }

    return semanticScore >= 1 && hasReliableDetailSignal(container) && hasLargeDetailHero(container, rect);
  }

  function collectDetailContainerCandidates() {
    const candidates = new Set(
      [...document.querySelectorAll(".steamloader-theme-game-detail")]
        .filter((node) => !isHomeOrLibraryHub(node)),
    );
    const semanticNodes = [...document.querySelectorAll("[class]")]
      .filter((node) => node instanceof HTMLElement)
      .filter((node) => detailClassFragments.some((fragment) => hasClassFragment(node, fragment)));

    for (const node of semanticNodes) {
      let current = node;
      for (let depth = 0; current && depth < 10; depth += 1, current = current.parentElement) {
        if (isLikelyDetailContainer(current)) {
          candidates.add(current);
        }
      }
    }

    const textSignalNodes = [...document.querySelectorAll("div, section, main")]
      .filter((node) => node instanceof HTMLElement)
      .filter((node) => {
        const rect = node.getBoundingClientRect();
        return rect.width >= 320 && rect.height >= 80 && rect.height <= Math.max(760, window.innerHeight * 0.82);
      })
      .filter((node) => detailTextPattern.test(node.innerText || ""));

    for (const node of textSignalNodes) {
      let current = node;
      for (let depth = 0; current && depth < 8; depth += 1, current = current.parentElement) {
        if (isLikelyDetailContainer(current)) {
          candidates.add(current);
          break;
        }
      }
    }

    return [...candidates];
  }

  function getActiveDetailContainer() {
    const containers = collectDetailContainerCandidates()
      .filter((node) => isVisibleElement(node))
      .filter((node) => {
        const rect = node.getBoundingClientRect();
        return rect.width >= 320 && rect.height >= 200 && isLikelyDetailContainer(node);
      })
      .sort((left, right) => {
        const leftRect = left.getBoundingClientRect();
        const rightRect = right.getBoundingClientRect();
        return rightRect.width * rightRect.height - leftRect.width * leftRect.height;
      });

    return containers[0] || null;
  }

  function removeDetachedPanels(activeContainer) {
    for (const panel of document.querySelectorAll(".steamtools-hltb-panel")) {
      if (!(panel instanceof HTMLElement)) {
        continue;
      }

      if (!activeContainer || !activeContainer.contains(panel)) {
        panel.remove();
      }
    }
  }

  function isNoiseTitle(value) {
    const text = normalizeTitleScore(value);
    if (!text || text.length < 2 || text.length > 120) {
      return true;
    }

    if (/^SP\s+BPM(?:_|$)/i.test(text) || /_UID\d+$/i.test(text)) {
      return true;
    }

    if (!/[A-Z]/.test(text)) {
      return true;
    }

    return titleNoiseMarkers.some((marker) => text.includes(marker));
  }

  function readReactRoots(node) {
    const roots = [];
    for (let depth = 0, current = node; current && depth < 7; depth += 1, current = current.parentElement) {
      for (const key of Object.keys(current)) {
        if (key.startsWith("__reactProps$") || key.startsWith("__reactFiber$")) {
          roots.push(current[key]);
        }
      }
    }

    return roots;
  }

  function tryExtractGameInfoFromObject(root) {
    const seen = new WeakSet();
    const queue = [root];
    let inspected = 0;
    let bestTitle = "";
    let bestAppId = null;

    while (queue.length && inspected < 900) {
      const current = queue.shift();
      if (!current || typeof current !== "object") {
        continue;
      }

      if (seen.has(current)) {
        continue;
      }

      seen.add(current);
      inspected += 1;

      const titleCandidate = normalizeText(
        current.display_name ||
          current.strDisplayName ||
          current.title ||
          current.name ||
          current.appName ||
          current.localized_name ||
          "",
      );
      const appIdCandidate = Number(
        current.appid || current.appId || current.appID || current.app_id || 0,
      );

      if (!bestTitle && titleCandidate && !isNoiseTitle(titleCandidate)) {
        bestTitle = titleCandidate;
      }

      if (!bestAppId && Number.isInteger(appIdCandidate) && appIdCandidate > 0) {
        bestAppId = appIdCandidate;
      }

      if (bestTitle && bestAppId) {
        return { title: bestTitle, appId: bestAppId };
      }

      const priorityKeys = [
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
        "sibling",
      ];

      for (const key of priorityKeys) {
        if (current[key] && typeof current[key] === "object") {
          queue.push(current[key]);
        }
      }

      for (const key of Object.keys(current)) {
        if (priorityKeys.includes(key) || key.startsWith("__react")) {
          continue;
        }

        const value = current[key];
        if (!value || typeof value !== "object") {
          continue;
        }

        if (value instanceof Element || value instanceof Node) {
          continue;
        }

        queue.push(value);
      }
    }

    return {
      title: bestTitle,
      appId: bestAppId,
    };
  }

  function extractGameInfoFromReact(node) {
    const roots = readReactRoots(node);
    for (const root of roots) {
      const result = tryExtractGameInfoFromObject(root);
      if (result.title || result.appId) {
        return result;
      }
    }

    return { title: "", appId: null };
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

  function findAppId(container) {
    for (const node of container.querySelectorAll("img, a, [style], [data-panel]")) {
      if (!(node instanceof HTMLElement)) {
        continue;
      }

      if (node instanceof HTMLImageElement) {
        const imageAppId =
          extractAppIdFromUrlish(node.currentSrc) ||
          extractAppIdFromUrlish(node.src) ||
          extractAppIdFromUrlish(node.alt);
        if (imageAppId) {
          return imageAppId;
        }
      }

      const hrefAppId = extractAppIdFromUrlish(node.getAttribute("href"));
      if (hrefAppId) {
        return hrefAppId;
      }

      const styleAppId = extractAppIdFromUrlish(node.style.backgroundImage);
      if (styleAppId) {
        return styleAppId;
      }
    }

    return null;
  }

  function scoreTitleCandidate(value) {
    const text = normalizeText(value);
    const upper = normalizeTitleScore(text);

    if (isNoiseTitle(text)) {
      return -1;
    }

    let score = 0;
    if (text.length >= 4 && text.length <= 64) {
      score += 3;
    }

    if (/[A-Za-z]/.test(text) && /\s/.test(text)) {
      score += 3;
    }

    if (/\d/.test(text)) {
      score += 1;
    }

    if (!upper.includes("STEAM")) {
      score += 1;
    }

    return score;
  }

  function collectTitleCandidates(container) {
    const candidates = [];
    const pushCandidate = (value) => {
      const text = normalizeText(value);
      if (!text) {
        return;
      }

      candidates.push(text);
    };

    for (const node of container.querySelectorAll("[aria-label], [title], img[alt]")) {
      if (!(node instanceof HTMLElement)) {
        continue;
      }

      pushCandidate(node.getAttribute("aria-label"));
      pushCandidate(node.getAttribute("title"));
      if (node instanceof HTMLImageElement) {
        pushCandidate(node.alt);
      }
    }

    const textCandidates = [...container.querySelectorAll("h1, h2, h3, div, span")]
      .filter((node) => node instanceof HTMLElement)
      .filter((node) => isVisibleElement(node))
      .filter((node) => {
        const rect = node.getBoundingClientRect();
        return rect.width >= 80 && rect.height >= 14 && rect.height <= 120;
      })
      .map((node) => normalizeText(node.innerText || ""))
      .filter((text) => text && text.length <= 90)
      .slice(0, 120);

    for (const text of textCandidates) {
      pushCandidate(text);
    }

    return candidates
      .map((value) => ({ value, score: scoreTitleCandidate(value) }))
      .filter((candidate) => candidate.score >= 0)
      .sort((left, right) => right.score - left.score || left.value.length - right.value.length);
  }

  function extractGameContext(container) {
    const reactInfo = extractGameInfoFromReact(container);
    const titleCandidates = collectTitleCandidates(container);
    const reactTitle = !isNoiseTitle(reactInfo.title) ? reactInfo.title : "";
    const bestTitle =
      reactTitle ||
      (titleCandidates.length > 0 ? titleCandidates[0].value : "");
    const bestAppId = reactInfo.appId || findAppId(container);

    return {
      title: bestTitle,
      appId: bestAppId,
    };
  }

  function findInsertionAnchor(container) {
    const playbar = container.querySelector(".steamloader-theme-game-detail-playbar");
    if (playbar instanceof HTMLElement) {
      return playbar;
    }

    for (const candidate of container.querySelectorAll("[class]")) {
      if (candidate instanceof HTMLElement && hasClassFragment(candidate, "basicappdetailssectionstyler_playsection_")) {
        return candidate;
      }
    }

    const fallback = [...container.querySelectorAll("div, section")]
      .filter((node) => node instanceof HTMLElement)
      .find((node) => new RegExp([
        "PLAYTIME",
        localizedSignals.playtime,
        "LAST PLAYED",
        localizedSignals.lastPlayed,
        "CONTROLLER",
      ].join("|"), "i").test(node.innerText || ""));

    return fallback instanceof HTMLElement ? fallback : null;
  }

  function findOverlayHost(container) {
    const preferredSelectors = [
      ".steamloader-theme-game-detail-topcapsule",
      ".steamloader-theme-game-detail-hero",
      ".steamloader-theme-game-detail-title-section",
    ];

    for (const selector of preferredSelectors) {
      const node = container.querySelector(selector);
      if (node instanceof HTMLElement) {
        return node;
      }
    }

    for (const candidate of container.querySelectorAll("[class]")) {
      if (!(candidate instanceof HTMLElement)) {
        continue;
      }

      if (hasClassFragment(candidate, "sharedappdetailsheader_topcapsule_")) {
        return candidate;
      }

      if (hasClassFragment(candidate, "sharedappdetailsheader_titlesection_")) {
        return candidate;
      }

      if (hasClassFragment(candidate, "sharedappdetailsheader_headerbackgroundimage_")) {
        return candidate.parentElement instanceof HTMLElement ? candidate.parentElement : candidate;
      }
    }

    const anchor = findInsertionAnchor(container);
    return anchor?.parentElement instanceof HTMLElement ? anchor.parentElement : container;
  }

  function formatHours(value) {
    return value && value !== "--" ? `${value}h` : "--";
  }

  function buildVisibleStats(snapshot) {
    const settings = snapshot?.settings || {};
    const stats = [];

    if (settings.showMainStory) {
      stats.push({ label: "Main Story", value: snapshot.mainStory });
    }

    if (settings.showMainPlus) {
      stats.push({ label: "Main + Extras", value: snapshot.mainPlus });
    }

    if (settings.showCompletionist) {
      stats.push({ label: "Completionist", value: snapshot.completionist });
    }

    if (settings.showAllStyles) {
      stats.push({ label: "All Styles", value: snapshot.allStyles });
    }

    return stats.filter((entry) => entry.value && entry.value !== "--");
  }

  function ensurePanel(container) {
    const host = findOverlayHost(container);
    if (host instanceof HTMLElement) {
      host.classList.add("steamtools-hltb-host");
    }

    let panel = container.querySelector(".steamtools-hltb-panel");
    if (!(panel instanceof HTMLElement)) {
      panel = document.createElement("section");
      panel.className = "steamtools-hltb-panel";
    }

    if (host instanceof HTMLElement && panel.parentElement !== host) {
      host.append(panel);
    } else if (!panel.isConnected) {
      container.append(panel);
    }

    return panel;
  }

  function clearPanel(container) {
    if (!container) {
      return;
    }

    const panel = container.querySelector(".steamtools-hltb-panel");
    if (panel) {
      panel.remove();
    }
  }

  function renderSnapshot(container, snapshot) {
    if (!snapshot || !snapshot.settings?.enabled || !snapshot.found) {
      clearPanel(container);
      return;
    }

    const stats = buildVisibleStats(snapshot);
    if (!stats.length) {
      clearPanel(container);
      return;
    }

    const host = findOverlayHost(container);
    if (!(host instanceof HTMLElement)) {
      clearPanel(container);
      return;
    }

    const panel = ensurePanel(container);
    panel.replaceChildren();

    const header = document.createElement("div");
    header.className = "steamtools-hltb-header";

    const headerCopy = document.createElement("div");
    headerCopy.className = "steamtools-hltb-header-copy";

    const kicker = document.createElement("div");
    kicker.className = "steamtools-hltb-kicker";
    kicker.textContent = "HowLongToBeat";

    const brand = document.createElement("div");
    brand.className = "steamtools-hltb-brand";
    brand.textContent = snapshot.matchedTitle || snapshot.requestedTitle || "Playtime";

    headerCopy.append(kicker, brand);

    const toolbar = document.createElement("div");
    toolbar.className = "steamtools-hltb-toolbar";

    const badge = document.createElement("div");
    badge.className = "steamtools-hltb-badge";
    badge.textContent = snapshot.cached ? "Cached" : "Live";

    toolbar.append(badge);

    if (snapshot.settings.showViewDetails && snapshot.detailUrl) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "steamtools-hltb-button";
      button.textContent = "Details";
      button.setAttribute("aria-label", "Open HowLongToBeat details");
      button.addEventListener("click", () => {
        void fetch(`${apiBase}api/hltb/open-details`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({ value: snapshot.detailUrl }),
        });
      });

      toolbar.append(button);
    }

    header.append(headerCopy, toolbar);
    panel.append(header);

    const grid = document.createElement("div");
    grid.className = "steamtools-hltb-grid";

    for (const stat of stats) {
      const item = document.createElement("div");
      item.className = "steamtools-hltb-stat";

      const value = document.createElement("div");
      value.className = "steamtools-hltb-value";
      value.textContent = formatHours(stat.value);

      const label = document.createElement("div");
      label.className = "steamtools-hltb-label";
      label.textContent = stat.label;

      item.append(value, label);
      grid.append(item);
    }

    panel.append(grid);
  }

  async function fetchSnapshot(context) {
    const query = new URLSearchParams();
    if (context.title) {
      query.set("title", context.title);
    }

    if (context.appId) {
      query.set("appId", String(context.appId));
    }

    const response = await fetch(`${apiBase}api/hltb/game?${query.toString()}`, { cache: "no-store" });
    const payload = await response.json();
    if (!response.ok) {
      throw new Error(payload?.message || `HLTB could not be loaded (${response.status}).`);
    }

    return payload;
  }

  async function refreshHltb() {
    state.refreshHandle = null;
    ensureStyleElement();

    if (document.hidden) {
      return;
    }

    const now = Date.now();
    if (now - (state.lastRefreshAt || 0) < 650) {
      queueRefresh(650);
      return;
    }
    state.lastRefreshAt = now;

    const container = getActiveDetailContainer();
    removeDetachedPanels(container);

    if (!container) {
      state.lastSnapshot = null;
      state.lastRequestKey = "";
      return;
    }

    const context = extractGameContext(container);
    const requestKey = `${context.appId || 0}|${context.title || ""}`;
    if (!context.title && !context.appId) {
      clearPanel(container);
      return;
    }

    if (
      state.lastSnapshot &&
      state.lastRequestKey === requestKey &&
      now - state.lastFetchAt < 4000
    ) {
      renderSnapshot(container, state.lastSnapshot);
      return;
    }

    if (state.inFlightKey === requestKey) {
      return;
    }

    state.inFlightKey = requestKey;
    try {
      const snapshot = await fetchSnapshot(context);
      state.lastRequestKey = requestKey;
      state.lastSnapshot = snapshot;
      state.lastFetchAt = Date.now();
      renderSnapshot(container, snapshot);
    } catch {
      clearPanel(container);
    } finally {
      if (state.inFlightKey === requestKey) {
        state.inFlightKey = "";
      }
    }
  }

  function queueRefresh(delay = 320) {
    if (state.refreshHandle) {
      return;
    }

    state.refreshHandle = window.setTimeout(() => {
      void refreshHltb();
    }, delay);
  }

  function install() {
    ensureStyleElement();
    queueRefresh();

    if (!state.observer) {
      state.observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
          if (mutation.type === "childList" && (mutation.addedNodes.length > 0 || mutation.removedNodes.length > 0)) {
            queueRefresh(480);
            break;
          }
        }
      });

      state.observer.observe(document.documentElement, {
        childList: true,
        subtree: true,
      });
    }

    if (typeof state.themeScanHandler !== "function") {
      state.themeScanHandler = () => {
        queueRefresh(520);
      };
      window.addEventListener(themeScanEventName, state.themeScanHandler);
    }

    if (state.timerHandle) {
      window.clearInterval(state.timerHandle);
    }

    state.timerHandle = window.setInterval(() => {
      if (!document.hidden) {
        queueRefresh(900);
      }
    }, 12000);

    state.installed = true;
    return true;
  }

  return install() ? "injected" : "waiting";
})();
