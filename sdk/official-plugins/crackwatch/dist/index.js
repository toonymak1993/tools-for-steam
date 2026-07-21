(() => {
  const manifest = {
    id: "crackwatch",
    name: "Crackwatch",
    version: "0.3.0",
    sdkVersion: "1.0.0",
    permissions: ["frontend", "storage", "notifications", "logging", "native.full-trust"],
    backend: {
      entryPoint: "backend/plugin.ps1",
      runtime: "powershell",
      arguments: [],
      autoStart: true,
      createNoWindow: true,
    },
  };

  if (typeof window.TfsPluginSdk?.register !== "function") {
    console.warn("TFS plugin SDK is not available yet.");
    return;
  }

  const sourceHost = "crackrelease.com";
  const logoUrl = "https://crackrelease.com/wp-content/uploads/2025/09/cropped-crack-release-icon.png";
  const pageSize = 6;
  const refreshOptions = [
    { minutes: 30, label: "30 min" },
    { minutes: 60, label: "1 hour" },
    { minutes: 180, label: "3 hours" },
    { minutes: 360, label: "6 hours" },
  ];
  const viewOptions = [
    { id: "hot", label: "Hot Games" },
    { id: "cracked", label: "All cracked games" },
    { id: "favorites", label: "My favorites" },
  ];

  let sdk = null;
  let activeContext = null;
  let initializePromise = null;
  let refreshPromise = null;
  let cancelScheduledRefresh = null;
  let cancelSearchRefresh = null;
  let refreshScheduleRevision = 0;
  let searchEditorRevision = 0;
  let searchQuery = "";
  let currentPage = 0;
  let activeView = "hot";
  let favoriteIds = new Set();
  let statusText = "Loading the local Crackwatch cache.";
  let lastError = "";
  let settings = {
    notifications: true,
    refreshMinutes: 60,
  };
  let snapshot = {
    fetchedAtUtc: "",
    hotFetchedAtUtc: "",
    checkedAtUtc: "",
    sourceUrl: "https://crackrelease.com/games/",
    hotSourceUrl: "https://crackrelease.com/",
    totalGames: 0,
    totalCracked: 0,
    games: [],
    allGames: [],
    hotGames: [],
  };

  function requestRefresh() {
    try {
      activeContext?.refresh?.();
    } catch {
    }
  }

  function normalizeSearch(value) {
    return String(value || "")
      .normalize("NFKD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLocaleLowerCase()
      .replace(/[^\p{L}\p{N}]+/gu, " ")
      .trim()
      .replace(/\s+/g, " ");
  }

  function normalizeTimestamp(value) {
    const date = new Date(String(value || ""));
    return Number.isNaN(date.getTime()) ? "" : date.toISOString();
  }

  function isAllowedSourceUrl(value, imageOnly = false) {
    try {
      const url = new URL(String(value || ""));
      if (url.protocol !== "https:" || url.hostname.toLocaleLowerCase() !== sourceHost) {
        return false;
      }

      return !imageOnly || url.pathname.startsWith("/wp-content/uploads/");
    } catch {
      return false;
    }
  }

  function normalizeGame(game, index) {
    const badge = String(game?.badge || "").trim().slice(0, 40);
    const badgeStatus = badge.split(/\s+/)[0].toLocaleLowerCase();
    const status = ["cracked", "uncracked", "unreleased"].includes(String(game?.status || "").toLocaleLowerCase())
      ? String(game.status).toLocaleLowerCase()
      : ["cracked", "uncracked", "unreleased"].includes(badgeStatus) ? badgeStatus : "uncracked";

    return {
      sourceId: Number(game?.sourceId) || index + 1,
      rank: Number(game?.rank) || index + 1,
      title: String(game?.title || "").trim().slice(0, 200),
      status,
      badge: badge || status.toLocaleUpperCase(),
      dayOffset: game?.dayOffset === null || game?.dayOffset === undefined || game?.dayOffset === ""
        ? null
        : Number.isFinite(Number(game.dayOffset)) ? Number(game.dayOffset) : null,
      sourceUrl: isAllowedSourceUrl(game?.sourceUrl) ? String(game.sourceUrl) : "",
      imageUrl: isAllowedSourceUrl(game?.imageUrl, true) ? String(game.imageUrl) : "",
      publishedAtUtc: normalizeTimestamp(game?.publishedAtUtc),
      updatedAtUtc: normalizeTimestamp(game?.updatedAtUtc),
    };
  }

  function normalizeGameList(value) {
    const seenIds = new Set();
    return (Array.isArray(value) ? value : [])
      .map(normalizeGame)
      .filter((game) => {
        const id = String(game.sourceId);
        if (!game.title || !game.sourceUrl || seenIds.has(id)) {
          return false;
        }

        seenIds.add(id);
        return true;
      });
  }

  function normalizeSnapshot(value) {
    const games = normalizeGameList(value?.games)
      .filter((game) => game.status === "cracked")
      .sort((left, right) => {
        const leftTime = new Date(left.updatedAtUtc || 0).getTime();
        const rightTime = new Date(right.updatedAtUtc || 0).getTime();
        return (rightTime - leftTime) || (left.rank - right.rank);
      });
    const allGames = normalizeGameList(
      Array.isArray(value?.allGames) && value.allGames.length > 0 ? value.allGames : games,
    );
    const hotGames = normalizeGameList(value?.hotGames);

    return {
      fetchedAtUtc: String(value?.fetchedAtUtc || ""),
      hotFetchedAtUtc: String(value?.hotFetchedAtUtc || ""),
      checkedAtUtc: String(value?.checkedAtUtc || value?.fetchedAtUtc || ""),
      sourceUrl: isAllowedSourceUrl(value?.sourceUrl)
        ? String(value.sourceUrl)
        : "https://crackrelease.com/games/",
      hotSourceUrl: isAllowedSourceUrl(value?.hotSourceUrl)
        ? String(value.hotSourceUrl)
        : "https://crackrelease.com/",
      totalGames: allGames.length,
      totalCracked: games.length,
      games,
      allGames,
      hotGames,
    };
  }

  function getActiveViewGames() {
    const allGamesById = new Map(snapshot.allGames.map((game) => [String(game.sourceId), game]));
    if (activeView === "hot") {
      return snapshot.hotGames.map((hotGame) => ({
        ...(allGamesById.get(String(hotGame.sourceId)) || hotGame),
        rank: hotGame.rank,
      }));
    }

    if (activeView === "favorites") {
      return [...favoriteIds]
        .map((id) => allGamesById.get(String(id)) || snapshot.hotGames.find((game) => String(game.sourceId) === String(id)))
        .filter(Boolean);
    }

    return snapshot.games;
  }

  function getFilteredGames() {
    const query = normalizeSearch(searchQuery);
    if (!query) {
      return getActiveViewGames();
    }

    const searchGames = normalizeGameList([...snapshot.allGames, ...snapshot.hotGames]);
    const tokens = query.split(" ").filter(Boolean);
    return searchGames.filter((game) => {
      const haystack = normalizeSearch([
        game.title,
        game.status,
        game.badge,
        game.sourceUrl,
      ].join(" "));
      return tokens.every((token) => haystack.includes(token));
    });
  }

  function getActiveViewLabel() {
    return viewOptions.find((option) => option.id === activeView)?.label || viewOptions[0].label;
  }

  function getDisplayLabel() {
    return normalizeSearch(searchQuery) ? "Search results" : getActiveViewLabel();
  }

  function formatTimestamp(value) {
    if (!value) {
      return "not updated yet";
    }

    const date = new Date(value);
    return Number.isNaN(date.getTime())
      ? "not updated yet"
      : date.toLocaleString([], { dateStyle: "short", timeStyle: "short" });
  }

  function formatGameDate(value) {
    if (!value) {
      return "";
    }

    const date = new Date(value);
    return Number.isNaN(date.getTime())
      ? ""
      : date.toLocaleDateString([], { year: "numeric", month: "short", day: "numeric" });
  }

  function getDayOffsetLabel(game) {
    if (!Number.isFinite(game.dayOffset)) {
      return game.badge;
    }

    if (game.status === "cracked") {
      if (game.dayOffset === 0) {
        return "Cracked on release day";
      }

      if (game.dayOffset < 0) {
        return `Listed as cracked ${Math.abs(game.dayOffset)} day${game.dayOffset === -1 ? "" : "s"} before release`;
      }

      return `Cracked ${game.dayOffset} day${game.dayOffset === 1 ? "" : "s"} after release`;
    }

    if (game.status === "unreleased") {
      const days = Math.abs(game.dayOffset);
      return game.dayOffset === 0
        ? "Release expected today"
        : `Release expected in ${days} day${days === 1 ? "" : "s"}`;
    }

    return game.dayOffset === 0
      ? "Uncracked on release day"
      : `Uncracked ${Math.abs(game.dayOffset)} day${Math.abs(game.dayOffset) === 1 ? "" : "s"} after release`;
  }

  function applySnapshot(value) {
    snapshot = normalizeSnapshot(value);
    const filtered = getFilteredGames();
    const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
    currentPage = Math.min(currentPage, pageCount - 1);
  }

  async function loadSettings() {
    const stored = await sdk.storage.get();
    const storedMinutes = Number(stored.refreshMinutes);
    const supportedMinutes = refreshOptions.some((option) => option.minutes === storedMinutes)
      ? storedMinutes
      : 60;
    settings = {
      notifications: stored.notifications !== false,
      refreshMinutes: supportedMinutes,
    };
    favoriteIds = new Set(
      (Array.isArray(stored.favoriteIds) ? stored.favoriteIds : [])
        .map((value) => String(value))
        .filter(Boolean)
        .slice(0, 200),
    );
  }

  async function persistSettings({ reschedule = true } = {}) {
    await sdk.storage.patch({
      ...settings,
      favoriteIds: [...favoriteIds],
    });
    if (reschedule) {
      scheduleBackgroundRefresh();
    }
    requestRefresh();
  }

  function scheduleBackgroundRefresh() {
    const scheduleRevision = ++refreshScheduleRevision;
    cancelScheduledRefresh?.();
    cancelScheduledRefresh = sdk.lifecycle.setTimeout(() => {
      if (scheduleRevision !== refreshScheduleRevision) {
        return;
      }

      void refreshGames("background").finally(() => {
        if (scheduleRevision === refreshScheduleRevision) {
          scheduleBackgroundRefresh();
        }
      });
    }, settings.refreshMinutes * 60_000);
  }

  async function notifyAboutChanges(newGames, newlyCrackedFavorites) {
    if (!settings.notifications) {
      return;
    }

    if (newlyCrackedFavorites.length > 0) {
      const names = newlyCrackedFavorites.slice(0, 3).map((game) => game.title).join(", ");
      const suffix = newlyCrackedFavorites.length > 3
        ? ` and ${newlyCrackedFavorites.length - 3} more`
        : "";
      await sdk.notifications.success(
        "Crackwatch favorite cracked",
        `${newlyCrackedFavorites.length} favorite${newlyCrackedFavorites.length === 1 ? " is" : "s are"} now cracked: ${names}${suffix}.`,
        { durationMs: 10_000 },
      );
      return;
    }

    if (newGames.length === 0) {
      return;
    }

    const names = newGames.slice(0, 3).map((game) => game.title).join(", ");
    const suffix = newGames.length > 3 ? ` and ${newGames.length - 3} more` : "";
    await sdk.notifications.success(
      "Crackwatch update",
      `${newGames.length} newly cracked: ${names}${suffix}.`,
      { durationMs: 8_000 },
    );
  }

  async function refreshGames(reason = "manual") {
    if (refreshPromise) {
      return refreshPromise;
    }

    refreshPromise = (async () => {
      const knownCrackedIds = new Set(snapshot.games.map((game) => String(game.sourceId)));
      const previousStatuses = new Map(
        [...snapshot.allGames, ...snapshot.hotGames].map((game) => [String(game.sourceId), game.status]),
      );
      statusText = reason === "background"
        ? "Refreshing CrackRelease in the background."
        : "Refreshing CrackRelease.";
      lastError = "";
      requestRefresh();

      try {
        const fresh = await sdk.backend.call("refresh", { reason }, { timeoutMs: 60_000 });
        applySnapshot(fresh);
        const newGames = knownCrackedIds.size > 0
          ? snapshot.games.filter((game) => !knownCrackedIds.has(String(game.sourceId)))
          : [];
        const newlyCrackedFavorites = snapshot.allGames.filter((game) => (
          favoriteIds.has(String(game.sourceId))
          && game.status === "cracked"
          && previousStatuses.has(String(game.sourceId))
          && previousStatuses.get(String(game.sourceId)) !== "cracked"
        ));
        statusText = `${snapshot.totalCracked} cracked games, ${snapshot.hotGames.length} Hot Games, ${favoriteIds.size} favorites. Last checked ${formatTimestamp(snapshot.checkedAtUtc)}.`;
        await sdk.log.info("CrackRelease refresh completed", {
          reason,
          totalGames: snapshot.totalGames,
          totalCracked: snapshot.totalCracked,
          totalHot: snapshot.hotGames.length,
          newGames: newGames.length,
          newlyCrackedFavorites: newlyCrackedFavorites.length,
        });
        await notifyAboutChanges(newGames, newlyCrackedFavorites);
        return snapshot;
      } catch (error) {
        lastError = error instanceof Error ? error.message : String(error);
        statusText = snapshot.games.length > 0
          ? `Refresh failed; showing the cache from ${formatTimestamp(snapshot.fetchedAtUtc)}.`
          : "CrackRelease could not be loaded.";
        await sdk.log.error("CrackRelease refresh failed", { reason, error: lastError });
        throw error;
      } finally {
        refreshPromise = null;
        requestRefresh();
      }
    })();

    return refreshPromise;
  }

  async function initialize() {
    if (initializePromise) {
      return initializePromise;
    }

    initializePromise = (async () => {
      try {
        await loadSettings();
      } catch (error) {
        lastError = error instanceof Error ? error.message : String(error);
      }

      try {
        const cached = await sdk.backend.call("getSnapshot", {}, { timeoutMs: 10_000 });
        applySnapshot(cached);
        if (snapshot.games.length > 0) {
          statusText = `${snapshot.totalCracked} cached cracked games. Checking for updates.`;
        }
      } catch (error) {
        lastError = error instanceof Error ? error.message : String(error);
      }

      scheduleBackgroundRefresh();
      try {
        await refreshGames("startup");
      } catch {
      }
    })();

    return initializePromise;
  }

  async function openSource(game) {
    if (!isAllowedSourceUrl(game?.sourceUrl)) {
      throw new Error("The source URL is invalid.");
    }

    await sdk.system.open(game.sourceUrl);
  }

  async function toggleFavorite(game) {
    const id = String(game?.sourceId || "");
    if (!id) {
      return;
    }

    if (favoriteIds.has(id)) {
      favoriteIds.delete(id);
      statusText = `${game.title} removed from favorites.`;
    } else {
      favoriteIds.add(id);
      statusText = `${game.title} added to favorites.`;
    }

    await persistSettings({ reschedule: false });
  }

  async function toggleNotifications() {
    settings.notifications = !settings.notifications;
    await persistSettings();
  }

  async function moveRefreshInterval(direction) {
    const currentIndex = Math.max(0, refreshOptions.findIndex((option) => option.minutes === settings.refreshMinutes));
    const nextIndex = (currentIndex + direction + refreshOptions.length) % refreshOptions.length;
    settings.refreshMinutes = refreshOptions[nextIndex].minutes;
    await persistSettings();
  }

  function moveView(direction) {
    const currentIndex = Math.max(0, viewOptions.findIndex((option) => option.id === activeView));
    const nextIndex = (currentIndex + direction + viewOptions.length) % viewOptions.length;
    activeView = viewOptions[nextIndex].id;
    clearSearch({ refresh: false });
    currentPage = 0;
    requestRefresh();
  }

  function updateSearch(value) {
    searchQuery = String(value || "");
    currentPage = 0;
    cancelSearchRefresh?.();
    cancelSearchRefresh = sdk.lifecycle.setTimeout(() => {
      cancelSearchRefresh = null;
      requestRefresh();
    }, 150);
  }

  function clearSearch({ refresh = true } = {}) {
    searchQuery = "";
    currentPage = 0;
    searchEditorRevision += 1;
    cancelSearchRefresh?.();
    cancelSearchRefresh = null;
    if (refresh) {
      requestRefresh();
    }
  }

  function movePage(direction) {
    const filtered = getFilteredGames();
    const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
    currentPage = Math.max(0, Math.min(pageCount - 1, currentPage + direction));
    requestRefresh();
  }

  function getGameEyebrow(game) {
    if (normalizeSearch(searchQuery)) {
      return "Search result";
    }
    if (activeView === "hot") {
      return `Hot Game #${game.rank}`;
    }
    if (activeView === "favorites") {
      return "Favorite";
    }
    const updatedDate = formatGameDate(game.updatedAtUtc);
    return updatedDate ? `Updated ${updatedDate}` : `Cracked game #${game.rank}`;
  }

  function createGameSlots(game) {
    const favorite = favoriteIds.has(String(game.sourceId));
    const updatedDate = formatGameDate(game.updatedAtUtc);
    return [
      sdk.ui.createFeatureNavigationSlot(
        game.title,
        getDayOffsetLabel(game),
        () => openSource(game).catch((error) => {
          lastError = error instanceof Error ? error.message : String(error);
          requestRefresh();
        }),
        {
          eyebrow: getGameEyebrow(game),
          badge: game.status.toLocaleUpperCase(),
          meta: [
            game.badge,
            updatedDate ? `Updated ${updatedDate}` : "CrackRelease",
            favorite ? "★ Favorite" : "",
          ].filter(Boolean),
          mediaImageSrc: game.imageUrl,
          mediaImageAlt: `${game.title} cover art from CrackRelease`,
          footerLabel: "Open source",
          slotKey: `crackwatch-game-${activeView}-${game.sourceId}`,
        },
      ),
      sdk.ui.createChoiceSlot(
        favorite ? "★ Favorite" : "☆ Add favorite",
        favorite ? `Stop watching ${game.title}.` : `Watch ${game.title} for status changes.`,
        () => toggleFavorite(game).catch((error) => {
          lastError = error instanceof Error ? error.message : String(error);
          requestRefresh();
        }),
        {
          selected: favorite,
          badge: favorite ? "Watching" : "Favorite",
          slotKey: `crackwatch-favorite-${activeView}-${game.sourceId}`,
        },
      ),
    ];
  }

  function createScreen(context = {}) {
    activeContext = context;
    void initialize();

    const filtered = getFilteredGames();
    const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
    currentPage = Math.max(0, Math.min(pageCount - 1, currentPage));
    const pageGames = filtered.slice(currentPage * pageSize, (currentPage + 1) * pageSize);
    const interval = refreshOptions.find((option) => option.minutes === settings.refreshMinutes) || refreshOptions[1];
    const hasSearch = Boolean(normalizeSearch(searchQuery));

    const slots = [
      sdk.ui.createCommandSlot(
        "Refresh now",
        "Scrape the public CrackRelease pages and update the local cache.",
        () => refreshGames("manual").catch(() => {}),
        { badge: refreshPromise ? "Updating" : "Refresh", disabled: Boolean(refreshPromise), slotKey: "crackwatch-refresh" },
      ),
      sdk.ui.createToggleSlot(
        "New-crack notifications",
        "Show one Steam notification for new cracks, prioritizing favorited games.",
        settings.notifications,
        () => toggleNotifications().catch((error) => {
          lastError = error instanceof Error ? error.message : String(error);
          requestRefresh();
        }),
        { switchLabel: settings.notifications ? "On" : "Off", slotKey: "crackwatch-notifications" },
      ),
      sdk.ui.createInlineStepperSlot(
        "Background refresh",
        interval.label,
        () => moveRefreshInterval(-1).catch(() => {}),
        () => moveRefreshInterval(1).catch(() => {}),
        { slotKey: "crackwatch-refresh-interval" },
      ),
      sdk.ui.createInlineStepperSlot(
        "Category",
        getActiveViewLabel(),
        () => moveView(-1),
        () => moveView(1),
        { slotKey: "crackwatch-view" },
      ),
      ...(hasSearch ? [
        sdk.ui.createCommandSlot(
          "Clear search",
          `Show ${getActiveViewLabel()} again.`,
          () => clearSearch(),
          { badge: `${filtered.length} found`, slotKey: "crackwatch-clear-search" },
        ),
      ] : []),
      sdk.ui.createInlineStepperSlot(
        "Results page",
        `${currentPage + 1} / ${pageCount}`,
        () => movePage(-1),
        () => movePage(1),
        {
          leftDisabled: currentPage <= 0,
          rightDisabled: currentPage >= pageCount - 1,
          slotKey: "crackwatch-results-page",
        },
      ),
      ...pageGames.flatMap(createGameSlots),
    ];

    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: `${getDisplayLabel()} · Crack status tracker`,
      note: statusText,
      error: lastError,
      cards: [
        {
          title: "CrackRelease status data",
          imageSrc: logoUrl,
          imageAlt: "CrackRelease CR logo",
          lines: [
            hasSearch
              ? `${filtered.length} global results for "${searchQuery.trim()}"`
              : `${filtered.length} results in ${getActiveViewLabel()}`,
            `${snapshot.totalCracked} cracked of ${snapshot.totalGames} tracked games · ${snapshot.hotGames.length} Hot Games · ${favoriteIds.size} favorites`,
            activeView === "cracked" && !hasSearch
              ? "Sorted by CrackRelease update date, newest first."
              : "Search checks every tracked game, regardless of category.",
            `Cached ${formatTimestamp(snapshot.fetchedAtUtc)}`,
            "Status information only — no downloads, torrents, or repacks.",
          ],
        },
      ],
      editors: [
        {
          inputKey: `crackwatch-search-${searchEditorRevision}`,
          inputType: "search",
          label: "Search games",
          help: "Search every tracked title and status. Multiple words may be entered in any order.",
          value: searchQuery,
          placeholder: "Game title",
          rows: 1,
          onInput: updateSearch,
        },
      ],
      slots,
    });
  }

  window.TfsPluginSdk.register(manifest, (registeredSdk) => {
    sdk = registeredSdk;
    const definition = {
      createScreen,
      refresh: () => refreshGames("manual"),
      dispose() {
        refreshScheduleRevision += 1;
        cancelScheduledRefresh?.();
        cancelScheduledRefresh = null;
        cancelSearchRefresh?.();
        cancelSearchRefresh = null;
      },
    };

    void initialize();
    return definition;
  });
})();
