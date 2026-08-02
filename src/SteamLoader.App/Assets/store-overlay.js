(() => {
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";
  const version = 22;
  const maxConcurrentArtworkLoads = 3;
  const rootId = "steamloader-store-root";
  const styleId = "steamloader-store-style";
  const inputStorageKey = "ToolsForSteamPluginStoreInput";
  const overlayStateStorageKey = "ToolsForSteamPluginStoreOverlayState";
  const wishlistViewStorageKey = "ToolsForSteamWishlistView";
  const channelName = "ToolsForSteamPluginStoreChannel";
  const tabs = ["discover", "search", "wishlist", "alerts", "settings"];
  const searchKeyboardRows = [
    [["Q", 1], ["W", 1], ["E", 1], ["R", 1], ["T", 1], ["Z", 1], ["U", 1], ["I", 1], ["O", 1], ["P", 1], ["Ü", 1], ["Back", 1]],
    [["A", 1], ["S", 1], ["D", 1], ["F", 1], ["G", 1], ["H", 1], ["J", 1], ["K", 1], ["L", 1], ["Ö", 1], ["Ä", 1], ["Done", 1]],
    [["Y", 1], ["X", 1], ["C", 1], ["V", 1], ["B", 1], ["N", 1], ["M", 1], ["0", 1], ["1", 1], ["2", 1], ["-", 1], ["'", 1]],
    [["Clear", 2], ["3", 1], ["4", 1], ["5", 1], ["6", 1], ["7", 1], ["8", 1], ["9", 1], ["Space", 3]],
  ];
  const storeRegions = [
    ["US", "United States", "USD", "$"],
    ["DE", "Eurozone", "EUR", "€"],
    ["GB", "United Kingdom", "GBP", "£"],
    ["CA", "Canada", "CAD", "CA$"],
    ["AU", "Australia", "AUD", "A$"],
    ["NZ", "New Zealand", "NZD", "NZ$"],
    ["BR", "Brazil", "BRL", "R$"],
    ["MX", "Mexico", "MXN", "MX$"],
    ["CL", "Chile", "CLP", "CLP$"],
    ["CO", "Colombia", "COP", "COL$"],
    ["JP", "Japan", "JPY", "¥"],
    ["KR", "South Korea", "KRW", "₩"],
    ["CN", "China", "CNY", "CN¥"],
  ];

  const previous = window.__steamLoaderStoreOverlay;
  if (previous?.version === version) {
    previous.ensureMounted?.();
    return;
  }
  previous?.cleanup?.();
  document.getElementById(rootId)?.remove();
  document.getElementById(styleId)?.remove();

  const savedWishlistView = loadWishlistViewPreferences();
  const state = {
    open: false,
    loading: false,
    refreshing: false,
    snapshot: null,
    activeTab: "discover",
    selectedGame: null,
    offers: [],
    offersLoading: false,
    alertDraft: null,
    searchQuery: "",
    searchResults: [],
    searchLoading: false,
    searchKeyboardOpen: false,
    searchKeyboardDraft: "",
    searchKeyboardPurpose: "store",
    wishlistQuery: "",
    wishlistFilter: savedWishlistView.filter,
    wishlistSort: savedWishlistView.sort,
    wishlistManageMode: false,
    selectedWishlistIds: new Set(),
    recentlyRemovedGames: [],
    undoRemoveTimer: 0,
    statusTimer: 0,
    statusTimerMessage: "",
    settingsBusy: false,
    snapshotSyncing: false,
    lastSnapshotPollAt: 0,
    alertUpdatingId: "",
    discoverySeed: Date.now(),
    regionMenuOpen: false,
    error: "",
    status: "",
    focusIndex: 0,
    lastInputNonce: "",
    pollTimer: 0,
    gamepadTimer: 0,
    broadcastTimer: 0,
    buttonState: {},
    lastActionAt: {},
    navigationSoundLastAt: 0,
    inputReadyAt: 0,
    keyHandler: null,
    storageHandler: null,
    channel: null,
    channelHandler: null,
  };
  const focusScrollFrames = new WeakMap();
  const artworkFallbackCache = new Map();
  let artworkLoadGeneration = 0;
  let artworkLoadObserver = null;
  let artworkLoadQueue = [];
  let activeArtworkLoads = 0;

  window.__steamLoaderStoreOverlay = {
    version,
    ensureMounted,
    cleanup,
  };

  function normalizeApiPath(path) {
    return `${apiBase}${String(path || "").replace(/^\/+/, "")}`.replace(/([^:]\/)\/+/g, "$1");
  }

  function loadWishlistViewPreferences() {
    const validFilters = ["all", "changes", "sale", "alerts", "unreleased", "unpriced", "pinned"];
    const validSorts = ["smart", "discount", "price", "added", "changed", "title"];
    try {
      const value = JSON.parse(window.localStorage?.getItem(wishlistViewStorageKey) || "{}");
      return {
        filter: validFilters.includes(value?.filter) || String(value?.filter || "").startsWith("tag:") ? value.filter : "all",
        sort: validSorts.includes(value?.sort) ? value.sort : "smart",
      };
    } catch {
      return { filter: "all", sort: "smart" };
    }
  }

  function persistWishlistViewPreferences() {
    try {
      window.localStorage?.setItem(wishlistViewStorageKey, JSON.stringify({
        filter: state.wishlistFilter,
        sort: state.wishlistSort,
      }));
    } catch {}
  }

  async function fetchJson(path, options = {}) {
    const response = await fetch(normalizeApiPath(path), {
      cache: "no-store",
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(options.headers || {}),
      },
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(payload?.message || `Request failed (${response.status}).`);
    }
    return payload;
  }

  function cleanup() {
    resetArtworkLoading();
    for (const timerName of ["pollTimer", "gamepadTimer", "broadcastTimer"]) {
      if (state[timerName]) {
        window.clearInterval(state[timerName]);
        state[timerName] = 0;
      }
    }
    if (state.undoRemoveTimer) {
      window.clearTimeout(state.undoRemoveTimer);
      state.undoRemoveTimer = 0;
    }
    if (state.statusTimer) {
      window.clearTimeout(state.statusTimer);
      state.statusTimer = 0;
    }
    if (state.keyHandler) {
      window.removeEventListener("keydown", state.keyHandler, true);
    }
    if (state.storageHandler) {
      window.removeEventListener("storage", state.storageHandler);
    }
    if (state.channel && state.channelHandler) {
      state.channel.removeEventListener?.("message", state.channelHandler);
    }
    try { state.channel?.close?.(); } catch {}
    document.body?.classList?.remove("steamloader-store-open");
  }

  function install() {
    ensureStyle();
    ensureMounted();
    installInput();
    state.pollTimer = window.setInterval(pollOverlayState, 700);
    state.gamepadTimer = window.setInterval(pollGamepads, 58);
    void pollOverlayState();
  }

  function getChannel() {
    if (state.channel || typeof BroadcastChannel !== "function") return state.channel;
    try {
      state.channel = new BroadcastChannel(channelName);
      state.channelHandler = (event) => consumeBridgeInput(event.data);
      state.channel.addEventListener("message", state.channelHandler);
    } catch {
      state.channel = null;
    }
    return state.channel;
  }

  function broadcastOverlayState(active) {
    const payload = {
      type: "overlay-state",
      active: Boolean(active),
      expiresAt: active ? Date.now() + 2200 : 0,
      nonce: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      source: "store",
    };
    try { getChannel()?.postMessage(payload); } catch {}
    try { window.localStorage?.setItem(overlayStateStorageKey, JSON.stringify(payload)); } catch {}
  }

  function installInput() {
    state.keyHandler = (event) => {
      if (!state.open || isTextInput(event.target)) return;
      const action = actionFromKey(event);
      if (!action) return;
      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation?.();
      if (!event.repeat) handleAction(action, "host-key");
    };
    window.addEventListener("keydown", state.keyHandler, true);
    state.storageHandler = (event) => {
      if (event.key === inputStorageKey) consumeBridgeInput(event.newValue);
    };
    window.addEventListener("storage", state.storageHandler);
    getChannel();
  }

  function consumeBridgeInput(raw) {
    if (!state.open || !raw) return;
    try {
      const payload = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (payload?.type !== "input" || payload.nonce === state.lastInputNonce) return;
      state.lastInputNonce = payload.nonce || String(Date.now());
      handleAction(payload.action, "quick-access-bridge");
    } catch {}
  }

  async function pollOverlayState() {
    try {
      const payload = await fetchJson("api/store/overlay/state");
      const isOpen = Boolean(payload?.isOpen);
      if (isOpen && !canHostOverlay()) {
        if (state.open) {
          state.open = false;
          render();
        }
        return;
      }
      if (isOpen !== state.open) setOpen(isOpen);
      if (isOpen && !state.snapshot && !state.loading) void loadStore(false);
      else if (isOpen && state.snapshot?.isRefreshing && !state.snapshotSyncing && !state.loading && !state.refreshing && !state.settingsBusy &&
        Date.now() - state.lastSnapshotPollAt >= 2500) void syncRefreshingSnapshot();
    } catch {
      if (state.open) {
        state.error = "The local Store service is not reachable.";
        render();
      }
    }
  }

  async function syncRefreshingSnapshot() {
    state.snapshotSyncing = true;
    state.lastSnapshotPollAt = Date.now();
    try {
      const snapshot = await fetchJson("api/store/state");
      const changed = snapshot?.refreshedAtUtc !== state.snapshot?.refreshedAtUtc ||
        Boolean(snapshot?.isRefreshing) !== Boolean(state.snapshot?.isRefreshing);
      state.snapshot = snapshot;
      if (changed && !snapshot?.isRefreshing) {
        state.status = "Wishlist prices and availability are up to date.";
        render();
      }
    } catch {}
    finally {
      state.snapshotSyncing = false;
    }
  }

  function isQuickAccessSurface() {
    const identity = `${document.title || ""} ${window.location?.href || ""}`;
    return Boolean(
      document.getElementById("QuickAccess-NA") ||
      document.querySelector("[id^='QuickAccess']") ||
      /quick[\s_-]*access/i.test(identity)
    );
  }

  function canHostOverlay() {
    return Boolean(
      document.body &&
      document.visibilityState !== "hidden" &&
      !isQuickAccessSurface() &&
      window.innerWidth >= 900 &&
      window.innerHeight >= 500
    );
  }

  function setOpen(open) {
    state.open = Boolean(open);
    document.body?.classList?.toggle("steamloader-store-open", state.open);
    if (state.open) {
      state.error = "";
      state.focusIndex = 0;
      state.buttonState = {};
      state.lastActionAt = {};
      state.inputReadyAt = Date.now() + 380;
      state.discoverySeed = Date.now() + Math.floor(Math.random() * 100000);
      broadcastOverlayState(true);
      if (!state.broadcastTimer) {
        state.broadcastTimer = window.setInterval(() => {
          if (state.open) broadcastOverlayState(true);
        }, 450);
      }
      void loadStore(false);
    } else {
      closeDetails(false);
      broadcastOverlayState(false);
    }
    render();
  }

  async function loadStore(force) {
    if (state.loading || state.refreshing) return;
    state[force ? "refreshing" : "loading"] = true;
    state.error = "";
    render();
    try {
      state.snapshot = force
        ? await fetchJson("api/store/refresh", { method: "POST", body: "{}" })
        : await fetchJson("api/store/state");
      state.status = state.snapshot?.statusText || "Wishlist is ready.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.loading = false;
      state.refreshing = false;
      render();
    }
  }

  async function closeOverlay() {
    state.open = false;
    state.regionMenuOpen = false;
    state.searchKeyboardOpen = false;
    closeDetails(false);
    render();
    broadcastOverlayState(false);
    try { await fetchJson("api/store/overlay/close", { method: "POST", body: "{}" }); } catch {}
    document.body?.classList?.remove("steamloader-store-open");
  }

  async function openDetails(game) {
    state.selectedGame = game;
    state.offers = Array.isArray(game?.offers) ? game.offers : [];
    state.offersLoading = Boolean(game?.priceProviderGameId);
    const alertIdentity = getGameAlertIdentity(game);
    const existingAlert = getAlerts().find((alert) => getStoredAlertIdentity(alert) === alertIdentity);
    const currency = existingAlert?.targetCurrencyCode || getPreferredAlertCurrencyCode();
    const current = currency === "EUR" ? game?.cheapestPriceEur : game?.cheapestPrice;
    state.alertDraft = alertIdentity
      ? {
          targetPrice: Number(existingAlert?.targetPrice ?? current ?? 10).toFixed(2),
          currencyCode: existingAlert?.targetCurrencyCode || currency,
          enabled: Boolean(existingAlert),
          mode: existingAlert?.mode || "price",
          targetDiscountPercent: Number(existingAlert?.targetDiscountPercent) || 0,
          snoozedUntilUtc: existingAlert?.snoozedUntilUtc || null,
          edited: false,
        }
      : null;
    state.focusIndex = 0;
    render();
    if (!game?.priceProviderGameId) {
      state.offersLoading = false;
      render();
      return;
    }
    try {
      const offers = await fetchJson(`api/store/offers?gameId=${encodeURIComponent(game.priceProviderGameId)}`);
      if (state.selectedGame?.id === game.id) {
        state.offers = Array.isArray(offers) ? offers : [];
        const best = state.offers[0];
        if (best) {
          const updatedGame = {
            ...state.selectedGame,
            cheapestPrice: best.price,
            regularPrice: best.regularPrice,
            cheapestPriceEur: best.priceEur,
            regularPriceEur: best.regularPriceEur,
            regionalPrice: best.regionalPrice,
            regionalRegularPrice: best.regionalRegularPrice,
            regionalCurrencyCode: best.regionalCurrencyCode,
            discountPercent: best.discountPercent,
            bestStoreName: best.storeName,
            bestDealUrl: best.dealUrl,
            offers: state.offers,
          };
          state.selectedGame = updatedGame;
          replaceGameEverywhere(updatedGame);
          if (state.alertDraft && !existingAlert && !state.alertDraft.edited) {
            const bestAlertPrice = state.alertDraft.currencyCode === "EUR"
              ? updatedGame.cheapestPriceEur
              : updatedGame.cheapestPrice;
            if (Number(bestAlertPrice) > 0) {
              state.alertDraft.targetPrice = Number(bestAlertPrice).toFixed(2);
            }
          }
        }
      }
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.offersLoading = false;
      render();
    }
  }

  function replaceGameEverywhere(updatedGame) {
    const matches = (game) => game?.id === updatedGame?.id ||
      (game?.priceProviderGameId && game.priceProviderGameId === updatedGame?.priceProviderGameId) ||
      (Number(game?.steamAppId) > 0 && Number(game.steamAppId) === Number(updatedGame?.steamAppId));
    const replace = (games) => Array.isArray(games)
      ? games.map((game) => matches(game)
        ? {
            ...game,
            ...updatedGame,
            isWishlisted: game.isWishlisted,
            isSteamWishlisted: game.isSteamWishlisted,
            isLocallyWishlisted: game.isLocallyWishlisted,
          }
        : game)
      : games;

    if (state.snapshot) {
      state.snapshot = {
        ...state.snapshot,
        wishlist: replace(state.snapshot.wishlist),
        trending: replace(state.snapshot.trending),
        featuredDeals: replace(state.snapshot.featuredDeals),
      };
    }
    state.searchResults = replace(state.searchResults) || [];
  }

  function closeDetails(shouldRender = true) {
    state.selectedGame = null;
    state.offers = [];
    state.offersLoading = false;
    state.alertDraft = null;
    state.focusIndex = 0;
    if (shouldRender) render();
  }

  async function openDeal(dealUrl) {
    if (!dealUrl) return;
    try {
      await fetchJson("api/store/offers/open", {
        method: "POST",
        body: JSON.stringify({ dealUrl }),
      });
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      render();
    }
  }

  async function searchStore() {
    const query = state.searchQuery.trim();
    if (query.length < 2 || state.searchLoading) {
      if (query.length < 2) {
        state.error = "Enter at least two characters to search Steam, GOG, Xbox and Epic Games.";
        render();
      }
      return;
    }

    state.searchLoading = true;
    state.error = "";
    render();
    try {
      const results = await fetchJson(`api/store/search?q=${encodeURIComponent(query)}`);
      state.searchResults = Array.isArray(results) ? results : [];
      state.status = `${state.searchResults.length} direct store results for “${query}”.`;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.searchLoading = false;
      render();
    }
  }

  async function setLocalWishlist(game, enabled) {
    if (!game || state.refreshing) return;
    state.refreshing = true;
    state.error = "";
    render();
    try {
      state.snapshot = await fetchJson("api/store/wishlist", {
        method: "POST",
        body: JSON.stringify({ game, enabled }),
      });
      state.searchResults = state.searchResults.map((item) => item.id === game.id
        ? {
            ...item,
            isLocallyWishlisted: enabled,
            isWishlisted: enabled || Boolean(item.isSteamWishlisted),
          }
        : item);
      if (state.selectedGame?.id === game.id) {
        state.selectedGame = {
          ...state.selectedGame,
          isLocallyWishlisted: enabled,
          isWishlisted: enabled || Boolean(state.selectedGame.isSteamWishlisted),
        };
      }
      if (!enabled && game.isLocallyWishlisted) rememberRemovedGames([game]);
      else if (enabled) clearRememberedRemovedGames([game.id]);
      state.status = enabled ? `${game.title} added to the TFS wishlist.` : `${game.title} removed from the TFS wishlist.`;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.refreshing = false;
      render();
    }
  }

  async function setWishlistMetadata(game, changes) {
    if (!game || state.refreshing) return;
    state.refreshing = true;
    state.error = "";
    render();
    try {
      state.snapshot = await fetchJson("api/store/wishlist/metadata", {
        method: "POST",
        body: JSON.stringify({ gameId: game.id, ...changes }),
      });
      state.selectedGame = getWishlist().find((item) => item.id === game.id) || state.selectedGame;
      state.status = "Wishlist organization saved.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.refreshing = false;
      render();
    }
  }

  async function markWishlistChangesSeen() {
    try {
      state.snapshot = await fetchJson("api/store/wishlist/seen", { method: "POST", body: "{}" });
      state.status = "Wishlist changes marked as seen.";
      render();
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      render();
    }
  }

  async function bulkSetPinned(games, isPinned) {
    await runWishlistBulkAction(games, () => fetchJson("api/store/wishlist/bulk", {
      method: "POST",
      body: JSON.stringify({ gameIds: games.map((game) => game.id), isPinned }),
    }), `${games.length} games ${isPinned ? "pinned" : "unpinned"}.`);
  }

  async function bulkAddTag(games, tag) {
    await runWishlistBulkAction(games, () => fetchJson("api/store/wishlist/bulk", {
      method: "POST",
      body: JSON.stringify({ gameIds: games.map((game) => game.id), addTag: tag }),
    }), `${tag} added to ${games.length} games.`);
  }

  async function bulkCreateAlerts(games, multiplier) {
    const currencyCode = getPreferredAlertCurrencyCode();
    const priced = games.filter((game) => Number(currencyCode === "EUR" ? game.cheapestPriceEur : game.cheapestPrice) > 0);
    if (!priced.length) {
      state.status = "None of the selected games has a verified price for this alert yet.";
      render();
      return;
    }
    await runWishlistBulkAction(priced, () => fetchJson("api/store/wishlist/bulk", {
      method: "POST",
      body: JSON.stringify({
        gameIds: priced.map((game) => game.id),
        alertMultiplier: multiplier,
        alertCurrencyCode: currencyCode,
      }),
    }), `${priced.length} price alerts created.`);
  }

  async function bulkRemoveLocal(games) {
    const localGames = games.filter((game) => game.isLocallyWishlisted);
    if (!localGames.length) {
      state.status = "The selected games only belong to Steam; no local TFS entry was removed.";
      render();
      return;
    }
    const completed = await runWishlistBulkAction(localGames, () => fetchJson("api/store/wishlist/bulk", {
      method: "POST",
      body: JSON.stringify({ gameIds: localGames.map((game) => game.id), removeLocal: true }),
    }), `${localGames.length} TFS wishlist entries removed.`);
    if (completed) rememberRemovedGames(localGames);
  }

  async function runWishlistBulkAction(games, action, successMessage) {
    if (!games.length || state.refreshing) return false;
    state.refreshing = true;
    state.error = "";
    render();
    try {
      state.snapshot = await action();
      state.selectedWishlistIds.clear();
      state.status = successMessage;
      return true;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      return false;
    } finally {
      state.refreshing = false;
      render();
    }
  }

  function rememberRemovedGames(games) {
    const merged = new Map(state.recentlyRemovedGames.map((game) => [game.id, game]));
    games.forEach((game) => { if (game?.id) merged.set(game.id, game); });
    state.recentlyRemovedGames = [...merged.values()].slice(-50);
    if (state.undoRemoveTimer) window.clearTimeout(state.undoRemoveTimer);
    state.undoRemoveTimer = window.setTimeout(() => {
      state.recentlyRemovedGames = [];
      state.undoRemoveTimer = 0;
      if (state.open && state.activeTab === "wishlist") render();
    }, 15000);
  }

  function clearRememberedRemovedGames(gameIds = []) {
    const ids = new Set(gameIds);
    state.recentlyRemovedGames = ids.size
      ? state.recentlyRemovedGames.filter((game) => !ids.has(game.id))
      : [];
    if (!state.recentlyRemovedGames.length && state.undoRemoveTimer) {
      window.clearTimeout(state.undoRemoveTimer);
      state.undoRemoveTimer = 0;
    }
  }

  async function restoreRecentlyRemovedGames() {
    const games = [...state.recentlyRemovedGames];
    if (!games.length) return;
    const completed = await runWishlistBulkAction(games, async () => {
      let snapshot = state.snapshot;
      for (const game of games) {
        snapshot = await fetchJson("api/store/wishlist", {
          method: "POST",
          body: JSON.stringify({ game, enabled: true }),
        });
      }
      return snapshot;
    }, `${games.length} removed game${games.length === 1 ? "" : "s"} restored.`);
    if (completed) {
      clearRememberedRemovedGames();
      render();
    }
  }

  function formatChangeKind(kind) {
    return {
      "price-drop": "PRICE DROP",
      "new-deal": "NEW DEAL",
      "back-on-sale": "BACK ON SALE",
      released: "RELEASED",
      "new-store": "NEW STORE",
    }[String(kind || "").toLowerCase()] || "UPDATED";
  }

  async function saveAlert(enabled = true) {
    const game = state.selectedGame;
    if (!getGameAlertIdentity(game) || !state.alertDraft) return;
    const targetPrice = Math.max(0.01, Number(state.alertDraft.targetPrice) || 0);
    const steamAppId = Number(game.steamAppId) > 0 ? Number(game.steamAppId) : null;
    try {
      state.snapshot = await fetchJson("api/store/alerts", {
        method: "POST",
        body: JSON.stringify({
          steamAppId,
          gameId: game.id,
          title: game.title,
          targetPrice,
          currencyCode: state.alertDraft.currencyCode,
          enabled,
          mode: state.alertDraft.mode || "price",
          targetDiscountPercent: Number(state.alertDraft.targetDiscountPercent) || 0,
          snoozedUntilUtc: state.alertDraft.snoozedUntilUtc || null,
        }),
      });
      state.alertDraft.enabled = enabled;
      state.status = enabled ? "Price alert saved." : "Price alert removed.";
      render();
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      render();
    }
  }

  async function adjustSavedAlert(alert, delta) {
    const alertIdentity = getStoredAlertIdentity(alert);
    if (!alertIdentity || state.alertUpdatingId) return;
    const steamAppId = Number(alert?.steamAppId) > 0 ? Number(alert.steamAppId) : null;
    const targetPrice = Math.max(0.01, Math.round((Number(alert.targetPrice || 0) + delta) * 100) / 100);
    state.alertUpdatingId = alertIdentity;
    state.error = "";
    render();
    try {
      state.snapshot = await fetchJson("api/store/alerts", {
        method: "POST",
        body: JSON.stringify({
          steamAppId,
          gameId: alert.gameId,
          title: alert.title,
          targetPrice,
          currencyCode: alert.targetCurrencyCode,
          enabled: true,
          mode: alert.mode || "price",
          targetDiscountPercent: Number(alert.targetDiscountPercent) || 0,
          snoozedUntilUtc: alert.snoozedUntilUtc || null,
        }),
      });
      state.status = `Price target updated to ${formatSinglePrice(targetPrice, alert.targetCurrencyCode)}.`;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.alertUpdatingId = "";
      render();
    }
  }

  async function removeSavedAlert(alert) {
    const alertIdentity = getStoredAlertIdentity(alert);
    if (!alertIdentity || state.alertUpdatingId) return;
    state.alertUpdatingId = alertIdentity;
    state.error = "";
    render();
    try {
      state.snapshot = await fetchJson("api/store/alerts", {
        method: "POST",
        body: JSON.stringify({
          steamAppId: Number(alert?.steamAppId) > 0 ? Number(alert.steamAppId) : null,
          gameId: alert.gameId,
          title: alert.title,
          targetPrice: Number(alert.targetPrice) || 0,
          currencyCode: alert.targetCurrencyCode,
          enabled: false,
          mode: alert.mode || "price",
          targetDiscountPercent: Number(alert.targetDiscountPercent) || 0,
          snoozedUntilUtc: null,
        }),
      });
      state.status = `${alert.title} price alert removed.`;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.alertUpdatingId = "";
      render();
    }
  }

  async function snoozeSavedAlert(alert, days = 7) {
    const alertIdentity = getStoredAlertIdentity(alert);
    if (!alertIdentity || state.alertUpdatingId) return;
    state.alertUpdatingId = alertIdentity;
    state.error = "";
    render();
    try {
      const snoozedUntilUtc = days > 0 ? new Date(Date.now() + days * 86400000).toISOString() : null;
      state.snapshot = await fetchJson("api/store/alerts", {
        method: "POST",
        body: JSON.stringify({
          steamAppId: Number(alert?.steamAppId) > 0 ? Number(alert.steamAppId) : null,
          gameId: alert.gameId,
          title: alert.title,
          targetPrice: Number(alert.targetPrice) || 0,
          currencyCode: alert.targetCurrencyCode,
          enabled: true,
          mode: alert.mode || "price",
          targetDiscountPercent: Number(alert.targetDiscountPercent) || 0,
          snoozedUntilUtc,
        }),
      });
      state.status = days > 0
        ? `${alert.title} notifications snoozed for ${days} days.`
        : `${alert.title} notifications resumed.`;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.alertUpdatingId = "";
      render();
    }
  }

  async function setStorePreferences(changes) {
    if (state.settingsBusy) return;
    state.settingsBusy = true;
    state.error = "";
    render();
    try {
      state.snapshot = await fetchJson("api/store/settings/preferences", {
        method: "POST",
        body: JSON.stringify(changes || {}),
      });
      if (Object.prototype.hasOwnProperty.call(changes || {}, "includeKeyshops")) {
        state.snapshot = await fetchJson("api/store/refresh", { method: "POST", body: "{}" });
      }
      state.status = "Wishlist preferences saved.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.settingsBusy = false;
      render();
    }
  }

  async function setArtworkCachePolicy(maximumMegabytes, retentionDays) {
    if (state.settingsBusy) return;
    state.settingsBusy = true;
    render();
    try {
      state.snapshot = await fetchJson("api/store/settings/cache", {
        method: "POST",
        body: JSON.stringify({ maximumMegabytes, retentionDays }),
      });
      state.status = "Artwork cache policy saved.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.settingsBusy = false;
      render();
    }
  }

  async function clearArtworkCache() {
    if (state.settingsBusy) return;
    state.settingsBusy = true;
    render();
    try {
      state.snapshot = await fetchJson("api/store/cache/clear", { method: "POST", body: "{}" });
      state.status = "Artwork cache cleared. Visible images will be loaded again as needed.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.settingsBusy = false;
      render();
    }
  }

  async function copyWishlistBackup() {
    try {
      const backup = await fetchJson("api/store/backup");
      if (!navigator.clipboard?.writeText) throw new Error("Clipboard access is unavailable in this Steam surface.");
      await navigator.clipboard.writeText(String(backup?.json || ""));
      state.status = "Wishlist backup copied to the clipboard.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    }
    render();
  }

  async function importWishlistBackup() {
    try {
      if (!navigator.clipboard?.readText) throw new Error("Clipboard access is unavailable in this Steam surface.");
      const json = await navigator.clipboard.readText();
      state.snapshot = await fetchJson("api/store/backup/import", {
        method: "POST",
        body: JSON.stringify({ json }),
      });
      state.status = "Wishlist backup imported. Prices will refresh in the background.";
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    }
    render();
  }

  function formatBytes(bytes) {
    const value = Math.max(0, Number(bytes) || 0);
    if (value < 1024 * 1024) return `${Math.round(value / 1024)} KB`;
    return `${(value / (1024 * 1024)).toFixed(value >= 100 * 1024 * 1024 ? 0 : 1)} MB`;
  }

  async function setCurrency(value) {
    try {
      state.snapshot = await fetchJson("api/store/settings/currency", {
        method: "POST",
        body: JSON.stringify({ value }),
      });
      state.status = `Price display changed to ${value === "BOTH" ? "USD + EUR" : value}.`;
      render();
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
      render();
    }
  }

  async function setStoreRegion(value) {
    if (state.refreshing) return;
    state.regionMenuOpen = false;
    state.focusIndex = 0;
    state.refreshing = true;
    state.error = "";
    render();
    try {
      state.snapshot = await fetchJson("api/store/settings/region", {
        method: "POST",
        body: JSON.stringify({ value }),
      });
      state.status = `Store region changed to ${state.snapshot?.storeRegionName || value}.`;
    } catch (error) {
      state.error = error instanceof Error ? error.message : String(error);
    } finally {
      state.refreshing = false;
      render();
    }
  }

  function switchTab(delta) {
    const index = Math.max(0, tabs.indexOf(state.activeTab));
    state.regionMenuOpen = false;
    state.activeTab = tabs[(index + delta + tabs.length) % tabs.length];
    state.focusIndex = 0;
    render();
  }

  function handleAction(action, source = "unknown") {
    const normalizedAction = String(action || "").toLowerCase();
    const now = Date.now();
    if (now < state.inputReadyAt) return;

    // Steam can surface the same physical controller press through its Quick
    // Access bridge, a translated key event, and the browser Gamepad API. Keep
    // one edge across those paths so a single D-pad press moves exactly once.
    const duplicateWindow = /^(up|down|left|right)$/.test(normalizedAction) ? 210 : 320;
    if (now - Number(state.lastActionAt[normalizedAction] || 0) < duplicateWindow) return;
    state.lastActionAt[normalizedAction] = now;

    switch (normalizedAction) {
      case "up": moveFocus("up"); break;
      case "down": moveFocus("down"); break;
      case "left": moveFocus("left"); break;
      case "right": moveFocus("right"); break;
      case "a":
      case "select": getFocusables()[state.focusIndex]?.click?.(); break;
      case "b":
      case "back":
        if (state.searchKeyboardOpen) closeSearchKeyboard(false);
        else if (state.regionMenuOpen) { state.regionMenuOpen = false; state.focusIndex = 0; render(); }
        else if (state.selectedGame) closeDetails();
        else void closeOverlay();
        break;
      case "search-back":
      case "x":
        if (state.searchKeyboardOpen) handleSearchKeyboardKey("Back");
        break;
      case "keyboard-space":
      case "y":
        if (state.searchKeyboardOpen) handleSearchKeyboardKey("Space");
        else void loadStore(true);
        break;
      case "keyboard-done":
      case "start":
      case "menu":
        if (state.searchKeyboardOpen) handleSearchKeyboardKey("Done");
        break;
      case "previous-section": switchTab(-1); break;
      case "next-section": switchTab(1); break;
      case "refresh": void loadStore(true); break;
    }
  }

  function moveFocus(direction) {
    const elements = getFocusables();
    if (!elements.length) return;
    const current = elements[Math.min(state.focusIndex, elements.length - 1)] || elements[0];
    if (state.searchKeyboardOpen && moveSearchKeyboardFocus(direction, elements, current)) return;
    if (state.selectedGame && !state.searchKeyboardOpen && (direction === "up" || direction === "down")) {
      const currentRow = Number(current.dataset.storeNavRow);
      if (Number.isFinite(currentRow)) {
        const candidateRows = [...new Set(elements
          .map((element) => Number(element.dataset.storeNavRow))
          .filter((row) => Number.isFinite(row) && (direction === "up" ? row < currentRow : row > currentRow)))]
          .sort((left, right) => direction === "up" ? right - left : left - right);
        if (candidateRows.length) {
          const currentRect = current.getBoundingClientRect();
          const currentCenterX = currentRect.left + currentRect.width / 2;
          const candidates = elements.filter((element) => Number(element.dataset.storeNavRow) === candidateRows[0]);
          const next = candidates.reduce((best, element) => {
            const rect = element.getBoundingClientRect();
            const distance = Math.abs(rect.left + rect.width / 2 - currentCenterX);
            return !best || distance < best.distance ? { element, distance } : best;
          }, null)?.element;
          const nextIndex = elements.indexOf(next);
          if (nextIndex >= 0) {
            setFocus(nextIndex, direction);
            return;
          }
        }
      }
    }
    const currentRect = current.getBoundingClientRect();
    const currentCenter = { x: currentRect.left + currentRect.width / 2, y: currentRect.top + currentRect.height / 2 };
    let bestIndex = state.focusIndex;
    let bestScore = Number.POSITIVE_INFINITY;
    elements.forEach((element, index) => {
      if (element === current) return;
      const rect = element.getBoundingClientRect();
      const center = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
      const dx = center.x - currentCenter.x;
      const dy = center.y - currentCenter.y;
      const valid = direction === "left" ? dx < -8 : direction === "right" ? dx > 8 : direction === "up" ? dy < -8 : dy > 8;
      if (!valid) return;
      const primary = direction === "left" || direction === "right" ? Math.abs(dx) : Math.abs(dy);
      const secondary = direction === "left" || direction === "right" ? Math.abs(dy) : Math.abs(dx);
      const score = primary + secondary * 2.4;
      if (score < bestScore) { bestScore = score; bestIndex = index; }
    });
    setFocus(bestIndex, direction);
  }

  function moveSearchKeyboardFocus(direction, elements, current) {
    const currentRow = Number(current.dataset.storeKeyboardRow);
    const currentColumn = Number(current.dataset.storeKeyboardColumn);
    const currentSpan = Number(current.dataset.storeKeyboardSpan) || 1;
    if (!Number.isFinite(currentRow) || !Number.isFinite(currentColumn)) return false;

    if (direction === "left" || direction === "right") {
      const rowElements = elements.filter((element) => Number(element.dataset.storeKeyboardRow) === currentRow);
      const rowIndex = rowElements.indexOf(current);
      const next = rowElements[rowIndex + (direction === "left" ? -1 : 1)];
      const nextIndex = elements.indexOf(next);
      if (nextIndex >= 0) setFocus(nextIndex, direction);
      return true;
    }

    if (direction !== "up" && direction !== "down") return false;
    const nextRow = currentRow + (direction === "up" ? -1 : 1);
    const candidates = elements.filter((element) => Number(element.dataset.storeKeyboardRow) === nextRow);
    if (!candidates.length) return true;
    const currentCenter = currentColumn + currentSpan / 2;
    const next = candidates.reduce((best, element) => {
      const column = Number(element.dataset.storeKeyboardColumn);
      const span = Number(element.dataset.storeKeyboardSpan) || 1;
      const distance = Math.abs(column + span / 2 - currentCenter);
      return !best || distance < best.distance ? { element, distance } : best;
    }, null)?.element;
    const nextIndex = elements.indexOf(next);
    if (nextIndex >= 0) setFocus(nextIndex, direction);
    return true;
  }

  function setFocus(index, direction = "") {
    const elements = getFocusables();
    if (!elements.length) return;
    const previousIndex = state.focusIndex;
    state.focusIndex = Math.max(0, Math.min(index, elements.length - 1));
    elements.forEach((element, elementIndex) => element.classList.toggle("is-controller-focus", elementIndex === state.focusIndex));
    const focused = elements[state.focusIndex];
    if (document.activeElement !== focused) focused.focus({ preventScroll: true });
    if (direction && previousIndex !== state.focusIndex) playNavigationSound();
    scrollFocusedElementIntoView(focused, direction);
  }

  function playNavigationSound() {
    const now = performance.now();
    if (now - Number(state.navigationSoundLastAt || 0) < 36) return false;
    state.navigationSoundLastAt = now;
    try {
      const audio = new Audio("/sounds/deck_ui_navigation.wav");
      audio.volume = 0.72;
      const promise = audio.play();
      if (promise && typeof promise.catch === "function") promise.catch(() => {});
      return true;
    } catch {
      return false;
    }
  }

  function getFocusables() {
    const root = ensureMounted();
    const scope = state.searchKeyboardOpen
      ? root.querySelector(".steamloader-store-search-keyboard") || root
      : state.regionMenuOpen
        ? root.querySelector(".steamloader-store-region-menu") || root
        : state.selectedGame
          ? root.querySelector(".steamloader-store-modal") || root
          : root;
    return Array.from(scope.querySelectorAll("[data-store-focus='true']"))
      .filter((element) => !element.disabled && element.getClientRects().length > 0);
  }

  function scrollFocusedElementIntoView(element, direction = "") {
    if (!element) return;
    const pageMain = ensureMounted().querySelector(".steamloader-store-main");
    if (pageMain && element.closest(".steamloader-store-header")) {
      animateFocusScroll(pageMain, -pageMain.scrollTop, 0);
      return;
    }

    if (pageMain && state.activeTab === "wishlist" && direction === "up" && element.matches(".steamloader-store-grid > .steamloader-store-card")) {
      const grid = element.closest(".steamloader-store-grid");
      const firstRowTop = Math.min(...Array.from(grid.children).map((card) => card.getBoundingClientRect().top));
      if (element.getBoundingClientRect().top <= firstRowTop + 4) {
        animateFocusScroll(pageMain, -pageMain.scrollTop, 0);
        return;
      }
    }

    const containers = [];
    const rail = element.closest(".steamloader-store-rail, .steamloader-store-region-menu, .steamloader-store-modal-body");
    const main = element.closest(".steamloader-store-main");
    const hero = element.closest(".steamloader-store-hero");
    if (rail) containers.push(rail);
    if (main && main !== rail) containers.push(main);
    containers.forEach((container) => {
      const isHorizontalRail = container.classList.contains("steamloader-store-rail");
      const isRegionMenuOuter = container === main && rail?.classList.contains("steamloader-store-region-menu");
      if (container === main && rail && (direction === "left" || direction === "right")) return;
      if (isRegionMenuOuter && rail.dataset.storeMainScrollAligned === "true") return;
      const outerTarget = container === main
        ? hero || (rail
          ? rail
          : element)
        : element;
      const target = outerTarget.getBoundingClientRect();
      const viewport = container.getBoundingClientRect();
      const padding = isRegionMenuOuter ? 0 : container.classList.contains("steamloader-store-main") ? 28 : 14;
      const tolerance = 3;
      let top = 0;
      let left = 0;
      if (!isHorizontalRail) {
        if (target.top < viewport.top + padding - tolerance) top = target.top - viewport.top - padding;
        else if (target.bottom > viewport.bottom - padding + tolerance) top = target.bottom - viewport.bottom + padding;
      }
      if (target.left < viewport.left + padding - tolerance) left = target.left - viewport.left - padding;
      else if (target.right > viewport.right - padding + tolerance) left = target.right - viewport.right + padding;
      if (isRegionMenuOuter) rail.dataset.storeMainScrollAligned = "true";
      if (Math.abs(top) > tolerance || Math.abs(left) > tolerance) animateFocusScroll(container, top, left);
    });
  }

  function animateFocusScroll(container, top, left) {
    const startTop = container.scrollTop;
    const startLeft = container.scrollLeft;
    const targetTop = Math.max(0, Math.min(container.scrollHeight - container.clientHeight, startTop + top));
    const targetLeft = Math.max(0, Math.min(container.scrollWidth - container.clientWidth, startLeft + left));
    const active = focusScrollFrames.get(container);
    if (active && Math.abs(active.targetTop - targetTop) < 0.75 && Math.abs(active.targetLeft - targetLeft) < 0.75) return;
    if (active) {
      window.cancelAnimationFrame(active.frame);
      container.style.scrollBehavior = active.previousScrollBehavior;
    }

    if (Math.abs(targetTop - startTop) < 0.75 && Math.abs(targetLeft - startLeft) < 0.75) return;
    const reduceMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches;
    const distance = Math.hypot(targetTop - startTop, targetLeft - startLeft);
    const duration = reduceMotion ? 0 : Math.min(190, Math.max(110, distance * 0.28));
    const previousScrollBehavior = container.style.scrollBehavior;
    container.style.scrollBehavior = "auto";

    if (!duration) {
      container.scrollTop = targetTop;
      container.scrollLeft = targetLeft;
      container.style.scrollBehavior = previousScrollBehavior;
      focusScrollFrames.delete(container);
      return;
    }

    const startedAt = Date.now();
    const tick = () => {
      const progress = Math.min(1, (Date.now() - startedAt) / duration);
      const eased = 1 - Math.pow(1 - progress, 3);
      container.scrollTop = startTop + (targetTop - startTop) * eased;
      container.scrollLeft = startLeft + (targetLeft - startLeft) * eased;
      if (progress < 1) {
        const frame = window.requestAnimationFrame(tick);
        focusScrollFrames.set(container, { frame, previousScrollBehavior, targetTop, targetLeft });
      } else {
        container.style.scrollBehavior = previousScrollBehavior;
        focusScrollFrames.delete(container);
      }
    };
    const frame = window.requestAnimationFrame(tick);
    focusScrollFrames.set(container, { frame, previousScrollBehavior, targetTop, targetLeft });
  }

  function actionFromKey(event) {
    switch (event.key) {
      case "ArrowUp": return "up";
      case "ArrowDown": return "down";
      case "ArrowLeft": return "left";
      case "ArrowRight": return "right";
      case "Enter":
      case " ": return "a";
      case "Escape":
      case "Backspace": return "b";
      case "GamepadX": return "search-back";
      case "GamepadY": return "keyboard-space";
      case "GamepadStart":
      case "GamepadMenu":
      case "Start":
      case "Menu": return "keyboard-done";
      case "PageUp": return "previous-section";
      case "PageDown": return "next-section";
      case "F5": return "refresh";
      default: return "";
    }
  }

  function pollGamepads() {
    if (!state.open || typeof navigator.getGamepads !== "function") return;
    const gamepad = Array.from(navigator.getGamepads() || []).find(Boolean);
    if (!gamepad) return;
    const mapping = [
      ["a", Boolean(gamepad.buttons?.[0]?.pressed)],
      ["b", Boolean(gamepad.buttons?.[1]?.pressed)],
      ["search-back", Boolean(gamepad.buttons?.[2]?.pressed)],
      ["keyboard-space", Boolean(gamepad.buttons?.[3]?.pressed)],
      ["keyboard-done", Boolean(gamepad.buttons?.[9]?.pressed)],
      ["previous-section", Boolean(gamepad.buttons?.[4]?.pressed)],
      ["next-section", Boolean(gamepad.buttons?.[5]?.pressed)],
      ["up", Boolean(gamepad.buttons?.[12]?.pressed) || Number(gamepad.axes?.[1] || 0) < -0.58],
      ["down", Boolean(gamepad.buttons?.[13]?.pressed) || Number(gamepad.axes?.[1] || 0) > 0.58],
      ["left", Boolean(gamepad.buttons?.[14]?.pressed) || Number(gamepad.axes?.[0] || 0) < -0.58],
      ["right", Boolean(gamepad.buttons?.[15]?.pressed) || Number(gamepad.axes?.[0] || 0) > 0.58],
    ];
    for (const [action, pressed] of mapping) {
      const wasPressed = Boolean(state.buttonState[action]);
      state.buttonState[action] = pressed;
      if (pressed && !wasPressed) handleAction(action, "gamepad-api");
    }
  }

  function ensureMounted() {
    ensureStyle();
    let root = document.getElementById(rootId);
    if (!root) {
      root = document.createElement("div");
      root.id = rootId;
      root.className = "steamloader-store-root";
      document.body?.append(root);
    }
    return root;
  }

  function render() {
    const root = ensureMounted();
    const previousTab = root.dataset.storeActiveTab || "";
    const previousGameId = root.dataset.storeGameId || "";
    const previousMainScrollTop = root.querySelector(".steamloader-store-main")?.scrollTop || 0;
    const previousModalScrollTop = root.querySelector(".steamloader-store-modal-body")?.scrollTop || 0;
    const preserveMainScroll = previousTab === state.activeTab;
    const preserveModalScroll = previousGameId && previousGameId === String(state.selectedGame?.id || "");
    resetArtworkLoading();
    root.classList.toggle("is-open", state.open);
    root.replaceChildren();
    if (!state.open) return;
    root.dataset.storeActiveTab = state.activeTab;
    root.dataset.storeGameId = String(state.selectedGame?.id || "");

    const shell = el("div", "steamloader-store-shell");
    shell.append(renderHeader(), renderTabs(), renderMain(), renderFooter());
    root.append(shell);
    if (state.status) root.append(renderStatusToast());
    if (state.selectedGame) root.append(renderDetails());
    if (state.searchKeyboardOpen) root.append(renderSearchKeyboard());
    window.requestAnimationFrame(() => {
      if (preserveMainScroll) {
        const main = root.querySelector(".steamloader-store-main");
        if (main) main.scrollTop = Math.min(previousMainScrollTop, Math.max(0, main.scrollHeight - main.clientHeight));
      }
      if (preserveModalScroll) {
        const modalBody = root.querySelector(".steamloader-store-modal-body");
        if (modalBody) modalBody.scrollTop = Math.min(previousModalScrollTop, Math.max(0, modalBody.scrollHeight - modalBody.clientHeight));
      }
      setFocus(Math.min(state.focusIndex, Math.max(0, getFocusables().length - 1)));
    });
  }

  function renderStatusToast() {
    const toast = el("div", "steamloader-store-status-toast");
    toast.setAttribute("role", "status");
    toast.append(textEl("span", "", state.status));
    if (state.statusTimerMessage !== state.status) {
      if (state.statusTimer) window.clearTimeout(state.statusTimer);
      state.statusTimerMessage = state.status;
      state.statusTimer = window.setTimeout(() => {
        state.status = "";
        state.statusTimer = 0;
        state.statusTimerMessage = "";
        document.querySelector(`#${rootId} .steamloader-store-status-toast`)?.remove();
      }, 4200);
    }
    return toast;
  }

  function renderHeader() {
    const header = el("header", "steamloader-store-header");
    const brand = el("div", "steamloader-store-brand");
    brand.append(
      textEl("div", "steamloader-store-kicker", "TOOLS FOR STEAM"),
      textEl("h1", "steamloader-store-title", "Wishlist"),
      textEl("p", "steamloader-store-subtitle", state.snapshot?.statusText || "Wishlist deals, trends and price alerts in one place."),
    );
    const actions = el("div", "steamloader-store-header-actions");
    if (state.snapshot?.refreshedAtUtc) {
      actions.append(textEl("div", "steamloader-store-updated", `Updated ${formatRelativeTime(state.snapshot.refreshedAtUtc)}`));
    }
    actions.append(
      buttonEl(state.refreshing ? "Refreshing…" : "Refresh", "steamloader-store-button is-soft", () => void loadStore(true), { disabled: state.refreshing }),
      buttonEl("Close", "steamloader-store-button is-soft", () => void closeOverlay()),
    );
    header.append(brand, actions);
    return header;
  }

  function renderTabs() {
    const nav = el("nav", "steamloader-store-tabs");
    nav.append(textEl("span", "steamloader-store-bumper", "LB"));
    for (const tab of tabs) {
      let label = tab === "discover" ? "Discover" : tab === "search" ? "Search" : tab === "wishlist" ? "Wishlist" : tab === "alerts" ? "Price Alerts" : "Settings";
      if (tab === "wishlist" && Number(state.snapshot?.unseenChangeCount) > 0) label += ` (${state.snapshot.unseenChangeCount})`;
      const button = buttonEl(label, `steamloader-store-tab${state.activeTab === tab ? " is-active" : ""}`, () => {
        state.activeTab = tab;
        state.focusIndex = 0;
        render();
      });
      button.dataset.storeFocus = "false";
      nav.append(button);
    }
    nav.append(textEl("span", "steamloader-store-bumper", "RB"));
    return nav;
  }

  function renderMain() {
    const main = el("main", "steamloader-store-main");
    if (state.error) main.append(textEl("div", "steamloader-store-notice is-error", state.error));
    if (state.loading && !state.snapshot) {
      main.append(renderSkeletons());
      return main;
    }
    if (state.activeTab === "search") renderSearch(main);
    else if (state.activeTab === "wishlist") renderWishlist(main);
    else if (state.activeTab === "alerts") renderAlerts(main);
    else if (state.activeTab === "settings") renderSettings(main);
    else renderDiscover(main);
    return main;
  }

  function renderDiscover(main) {
    const trending = getTrending();
    const hero = getWishlist().find((game) => game.isOnSale) || trending[0];
    if (hero) main.append(renderHero(hero));
    const wishlistDeals = getWishlist().filter((game) => game.isOnSale);
    appendGameSection(main, "From your wishlist · on sale", "Steam and TFS wishlist games discounted right now", wishlistDeals, "portrait");
    const suggestions = shuffledGames(trending, "fresh");
    main.append(renderSuggestionControls());
    appendGameSection(main, "Fresh picks for you", "A new mix from the direct stores every time Wishlist opens", suggestions.slice(0, 12), "landscape");
    appendGameSection(main, "Try something different", "", suggestions.slice(12, 24), "landscape");
    appendGameSection(main, "Deep discounts", "Randomized regional deals with direct links to the actual store", shuffledGames(getFeatured(), "discounts").slice(0, 14), "portrait");
  }

  function renderSuggestionControls() {
    const controls = el("div", "steamloader-store-suggestion-controls");
    controls.append(
      textEl("span", "", "Suggestions rotate on every visit."),
      buttonEl("Shuffle suggestions", "steamloader-store-button is-soft", () => {
        state.discoverySeed = Date.now() + Math.floor(Math.random() * 100000);
        state.focusIndex = 0;
        render();
      }),
    );
    return controls;
  }

  function renderSearch(main) {
    main.append(renderPageHead("Search every store", "Find a game, compare its direct store results and save it to the local TFS wishlist—even when it is not on Steam."));
    const search = el("form", "steamloader-store-search");
    const input = el("input", "steamloader-store-search-input");
    input.type = "search";
    input.value = state.searchQuery;
    input.placeholder = "Search games across Steam, GOG, Xbox and Epic Games";
    input.autocomplete = "off";
    input.spellcheck = false;
    input.readOnly = true;
    input.setAttribute("aria-label", "Open game search keyboard");
    input.dataset.storeFocus = "true";
    input.addEventListener("click", () => openSearchKeyboard());
    input.addEventListener("focus", () => {
      const index = getFocusables().indexOf(input);
      if (index >= 0 && (state.focusIndex !== index || !input.classList.contains("is-controller-focus"))) setFocus(index);
    });
    search.addEventListener("submit", (event) => {
      event.preventDefault();
      if (state.searchQuery.trim()) void searchStore();
      else openSearchKeyboard();
    });
    search.append(
      input,
      buttonEl(state.searchLoading ? "Searching…" : "Search", "steamloader-store-button is-primary", () => {
        if (state.searchQuery.trim()) void searchStore();
        else openSearchKeyboard();
      }, { disabled: state.searchLoading }),
    );
    main.append(search);
    main.append(textEl("div", "steamloader-store-search-note", "Steam, GOG and Xbox provide live search results. Epic results come from its current public keyless catalog; TFS keeps the game locally and continues checking all direct stores by title."));
    if (state.searchLoading) {
      main.append(renderSkeletons());
      return;
    }
    if (!state.searchResults.length) {
      main.append(textEl("div", "steamloader-store-empty", state.searchQuery.trim()
        ? "No exact result yet. Try the main game title without an edition or subtitle."
        : "Type a game title to build your own store-independent wishlist."));
      return;
    }
    main.append(renderGameGrid(state.searchResults));
  }

  function openSearchKeyboard(purpose = "store") {
    state.searchKeyboardPurpose = purpose === "wishlist" ? "wishlist" : "store";
    state.searchKeyboardDraft = state.searchKeyboardPurpose === "wishlist"
      ? state.wishlistQuery
      : state.searchQuery;
    state.searchKeyboardOpen = true;
    state.focusIndex = 0;
    render();
  }

  function closeSearchKeyboard(commit) {
    if (commit) {
      if (state.searchKeyboardPurpose === "wishlist") state.wishlistQuery = state.searchKeyboardDraft.trim();
      else state.searchQuery = state.searchKeyboardDraft.trim();
    }
    state.searchKeyboardOpen = false;
    state.focusIndex = 0;
    render();
    if (commit && state.searchKeyboardPurpose === "store" && state.searchQuery) void searchStore();
  }

  function handleSearchKeyboardKey(key) {
    const value = String(key || "");
    if (value === "Done") {
      closeSearchKeyboard(true);
      return;
    }
    if (value === "Back") state.searchKeyboardDraft = state.searchKeyboardDraft.slice(0, -1);
    else if (value === "Clear") state.searchKeyboardDraft = "";
    else state.searchKeyboardDraft += value === "Space" ? " " : value.toLowerCase();
    render();
  }

  function renderSearchKeyboard() {
    const backdrop = el("div", "steamloader-store-keyboard-backdrop");
    const panel = el("section", "steamloader-store-search-keyboard");
    panel.setAttribute("aria-label", "Game search keyboard");
    const header = el("div", "steamloader-store-search-keyboard-header");
    header.append(
      textEl("div", "steamloader-store-search-keyboard-title", state.searchKeyboardPurpose === "wishlist" ? "Filter wishlist" : "Search games"),
      textEl("div", "steamloader-store-search-keyboard-value", state.searchKeyboardDraft || "Enter a game title"),
    );
    const grid = el("div", "steamloader-store-search-keyboard-grid");
    searchKeyboardRows.forEach((row, rowIndex) => {
      const rowNode = el("div", "steamloader-store-search-keyboard-row");
      let columnIndex = 0;
      row.forEach(([key, span]) => {
        const button = buttonEl(
          key,
          `steamloader-store-search-key${key.length > 1 ? " is-action" : ""}`,
          () => handleSearchKeyboardKey(key),
          { navRow: rowIndex },
        );
        button.dataset.storeKeyboardRow = String(rowIndex);
        button.dataset.storeKeyboardColumn = String(columnIndex);
        button.dataset.storeKeyboardSpan = String(span);
        button.style.setProperty("--store-key-span", String(span));
        columnIndex += span;
        rowNode.append(button);
      });
      grid.append(rowNode);
    });
    panel.append(header, grid, textEl("div", "steamloader-store-search-keyboard-hint", "A Select   X Delete   Y Space   Start Done   B Cancel"));
    backdrop.append(panel);
    backdrop.addEventListener("click", (event) => { if (event.target === backdrop) closeSearchKeyboard(false); });
    return backdrop;
  }

  function renderHero(game) {
    const hero = el("section", "steamloader-store-hero");
    setArtworkBackground(hero, game, "header", "--store-hero-image");
    const content = el("div", "steamloader-store-hero-content");
    content.append(
      textEl("div", "steamloader-store-hero-label", game.isWishlisted ? "WISHLIST SPOTLIGHT" : "DEAL SPOTLIGHT"),
      textEl("h2", "steamloader-store-hero-title", game.title),
      textEl("div", "steamloader-store-hero-store", `${game.bestStoreName || "PC Store"}${game.discountPercent ? ` · -${game.discountPercent}%` : ""}`),
      renderPrice(game, "steamloader-store-hero-price"),
      buttonEl("Compare prices", "steamloader-store-button is-primary", () => void openDetails(game)),
    );
    hero.append(content);
    return hero;
  }

  function renderWishlist(main) {
    const allGames = getWishlist();
    if (state.wishlistFilter.startsWith("tag:")) {
      const activeTag = state.wishlistFilter.slice(4);
      const tagStillExists = allGames.some((game) => (game.tags || []).some((tag) => tag.toLowerCase() === activeTag.toLowerCase()));
      if (!tagStillExists) {
        state.wishlistFilter = "all";
        persistWishlistViewPreferences();
      }
    }
    const wishlist = getVisibleWishlist();
    main.append(renderPageHead(
      "Your Steam + TFS wishlist",
      `${wishlist.length} of ${allGames.length} games shown`,
    ));
    if (state.recentlyRemovedGames.length) main.append(renderWishlistUndoBanner());
    if (Number(state.snapshot?.unseenChangeCount) > 0) main.append(renderWishlistActivityBanner());
    main.append(renderWishlistToolbar(allGames));
    if (state.wishlistManageMode) main.append(renderWishlistManageBar(allGames));
    if (!allGames.length) {
      const empty = el("div", "steamloader-store-empty is-actionable");
      empty.append(
        textEl("strong", "", "Your combined wishlist is empty"),
        textEl("span", "", "Search Steam, GOG, Xbox, Epic Games and verified Instant Gaming matches, or make Steam profile and game details public."),
        buttonEl("Search games", "steamloader-store-button is-primary", () => { state.activeTab = "search"; state.focusIndex = 0; render(); }),
      );
      main.append(empty);
      return;
    }
    if (!wishlist.length) {
      const empty = el("div", "steamloader-store-empty is-actionable");
      empty.append(
        textEl("strong", "", "No games match this view"),
        textEl("span", "", "Your games are still safe. Clear the search and filters to show everything again."),
        buttonEl("Clear filters", "steamloader-store-button is-primary", () => {
          state.wishlistQuery = "";
          state.wishlistFilter = "all";
          persistWishlistViewPreferences();
          state.focusIndex = 0;
          render();
        }),
      );
      main.append(empty);
      return;
    }
    main.append(renderGameGrid(wishlist));
  }

  function renderWishlistUndoBanner() {
    const count = state.recentlyRemovedGames.length;
    const banner = el("section", "steamloader-store-undo-banner");
    banner.append(
      textEl("strong", "", `${count} TFS wishlist game${count === 1 ? "" : "s"} removed`),
      textEl("span", "", "Undo is available briefly. Steam wishlist entries were never deleted."),
      buttonEl("Undo remove", "steamloader-store-button is-primary", () => void restoreRecentlyRemovedGames()),
      buttonEl("Dismiss", "steamloader-store-button is-soft", () => { clearRememberedRemovedGames(); render(); }),
    );
    return banner;
  }

  function renderWishlistActivityBanner() {
    const banner = el("section", "steamloader-store-activity-banner");
    const count = Number(state.snapshot?.unseenChangeCount) || 0;
    banner.append(
      textEl("strong", "", `${count} change${count === 1 ? "" : "s"} since your last visit`),
      textEl("span", "", "Price drops, returning sales, releases and newly verified stores are highlighted."),
      buttonEl("Mark seen", "steamloader-store-button is-soft", () => void markWishlistChangesSeen()),
    );
    return banner;
  }

  function renderWishlistToolbar(allGames) {
    const toolbar = el("section", "steamloader-store-wishlist-toolbar");
    const searchButton = buttonEl(
      state.wishlistQuery ? `Search: ${state.wishlistQuery}` : "Filter titles",
      `steamloader-store-filter-search${state.wishlistQuery ? " is-active" : ""}`,
      () => openSearchKeyboard("wishlist"),
    );
    const filters = [
      ["all", "All"], ["changes", "New changes"], ["sale", "On sale"],
      ["alerts", "With alert"], ["unreleased", "Unreleased"],
      ["unpriced", "No price"], ["pinned", "Pinned"],
    ];
    const filterRail = el("div", "steamloader-store-filter-rail");
    for (const [value, label] of filters) {
      filterRail.append(buttonEl(
        `${label} ${countWishlistFilter(allGames, value)}`,
        `steamloader-store-filter-chip${state.wishlistFilter === value ? " is-active" : ""}`,
        () => { state.wishlistFilter = value; persistWishlistViewPreferences(); state.focusIndex = 0; render(); },
      ));
    }
    const tags = [...new Set(allGames.flatMap((game) => Array.isArray(game.tags) ? game.tags : []))]
      .sort((left, right) => left.localeCompare(right))
      .slice(0, 8);
    for (const tag of tags) {
      const value = `tag:${tag}`;
      filterRail.append(buttonEl(
        tag,
        `steamloader-store-filter-chip is-tag${state.wishlistFilter === value ? " is-active" : ""}`,
        () => { state.wishlistFilter = value; persistWishlistViewPreferences(); state.focusIndex = 0; render(); },
      ));
    }
    const actions = el("div", "steamloader-store-wishlist-toolbar-actions");
    actions.append(
      buttonEl(`Sort: ${wishlistSortLabel()}`, "steamloader-store-button is-soft", () => {
        const modes = ["smart", "discount", "price", "added", "changed", "title"];
        state.wishlistSort = modes[(modes.indexOf(state.wishlistSort) + 1) % modes.length];
        persistWishlistViewPreferences();
        render();
      }),
      buttonEl("Reset", "steamloader-store-button is-soft", () => {
        state.wishlistQuery = "";
        state.wishlistFilter = "all";
        state.wishlistSort = "smart";
        persistWishlistViewPreferences();
        state.focusIndex = 0;
        render();
      }, { disabled: !state.wishlistQuery && state.wishlistFilter === "all" && state.wishlistSort === "smart" }),
      buttonEl(state.wishlistManageMode ? "Done" : "Manage", `steamloader-store-button ${state.wishlistManageMode ? "is-primary" : "is-soft"}`, () => {
        state.wishlistManageMode = !state.wishlistManageMode;
        state.selectedWishlistIds.clear();
        render();
      }),
    );
    toolbar.append(searchButton, filterRail, actions);
    return toolbar;
  }

  function renderWishlistManageBar(allGames) {
    const bar = el("section", "steamloader-store-manage-bar");
    const selected = getSelectedWishlistGames(allGames);
    const visible = getVisibleWishlist();
    bar.append(
      textEl("strong", "", `${selected.length} selected`),
      buttonEl(selected.length === visible.length && selected.length ? "Clear" : "Select visible", "steamloader-store-button is-soft", () => {
        if (selected.length === visible.length && selected.length) state.selectedWishlistIds.clear();
        else visible.forEach((game) => state.selectedWishlistIds.add(game.id));
        render();
      }),
      buttonEl("Pin", "steamloader-store-button is-soft", () => void bulkSetPinned(selected, true), { disabled: !selected.length }),
      buttonEl("Unpin", "steamloader-store-button is-soft", () => void bulkSetPinned(selected, false), { disabled: !selected.length }),
      buttonEl("Must Buy", "steamloader-store-button is-soft", () => void bulkAddTag(selected, "Must Buy"), { disabled: !selected.length }),
      buttonEl("Later", "steamloader-store-button is-soft", () => void bulkAddTag(selected, "Later"), { disabled: !selected.length }),
      buttonEl("Co-op", "steamloader-store-button is-soft", () => void bulkAddTag(selected, "Co-op"), { disabled: !selected.length }),
      buttonEl("Alert -25%", "steamloader-store-button is-soft", () => void bulkCreateAlerts(selected, 0.75), { disabled: !selected.length }),
      buttonEl("Remove TFS", "steamloader-store-button is-danger", () => void bulkRemoveLocal(selected), { disabled: !selected.length }),
    );
    return bar;
  }

  function getVisibleWishlist() {
    const query = normalizeArtworkTitle(state.wishlistQuery);
    const alertIds = new Set(getAlerts().map(getStoredAlertIdentity));
    const filtered = getWishlist().filter((game) => {
      if (query && !normalizeArtworkTitle(game.title).includes(query)) return false;
      const filter = state.wishlistFilter;
      if (filter === "changes") return Boolean(game.hasUnseenChange);
      if (filter === "sale") return Boolean(game.isOnSale);
      if (filter === "alerts") return alertIds.has(getGameAlertIdentity(game));
      if (filter === "unreleased") return Boolean(game.isUnreleased);
      if (filter === "unpriced") return !getDisplayPriceParts(game).length;
      if (filter === "pinned") return Boolean(game.isPinned);
      if (filter.startsWith("tag:")) return (game.tags || []).some((tag) => tag.toLowerCase() === filter.slice(4).toLowerCase());
      return true;
    });
    const price = (game) => Number(getDisplayPriceParts(game)[0]?.value ?? Number.MAX_SAFE_INTEGER);
    return filtered.sort((left, right) => {
      if (state.wishlistSort === "discount") return Number(right.discountPercent || 0) - Number(left.discountPercent || 0) || left.title.localeCompare(right.title);
      if (state.wishlistSort === "price") return price(left) - price(right) || left.title.localeCompare(right.title);
      if (state.wishlistSort === "added") return Date.parse(right.addedAtUtc || 0) - Date.parse(left.addedAtUtc || 0);
      if (state.wishlistSort === "changed") return Date.parse(right.changedAtUtc || 0) - Date.parse(left.changedAtUtc || 0);
      if (state.wishlistSort === "title") return left.title.localeCompare(right.title);
      return Number(right.isPinned) - Number(left.isPinned) ||
        Number(right.hasUnseenChange) - Number(left.hasUnseenChange) ||
        Number(right.isOnSale) - Number(left.isOnSale) ||
        Number(right.discountPercent || 0) - Number(left.discountPercent || 0) ||
        left.title.localeCompare(right.title);
    });
  }

  function countWishlistFilter(games, filter) {
    if (filter === "all") return games.length;
    if (filter === "changes") return games.filter((game) => game.hasUnseenChange).length;
    if (filter === "sale") return games.filter((game) => game.isOnSale).length;
    if (filter === "alerts") {
      const ids = new Set(getAlerts().map(getStoredAlertIdentity));
      return games.filter((game) => ids.has(getGameAlertIdentity(game))).length;
    }
    if (filter === "unreleased") return games.filter((game) => game.isUnreleased).length;
    if (filter === "unpriced") return games.filter((game) => !getDisplayPriceParts(game).length).length;
    if (filter === "pinned") return games.filter((game) => game.isPinned).length;
    return games.length;
  }

  function wishlistSortLabel() {
    return { smart: "Smart", discount: "Discount", price: "Lowest price", added: "Recently added", changed: "Recently changed", title: "A-Z" }[state.wishlistSort] || "Smart";
  }

  function getSelectedWishlistGames(games = getWishlist()) {
    return games.filter((game) => state.selectedWishlistIds.has(game.id));
  }

  function renderAlerts(main) {
    const alerts = getAlerts();
    main.append(renderPageHead("Price alerts", "TFS checks your enabled targets in the background and only notifies on a newly reached price."));
    if (!alerts.length) {
      main.append(textEl("div", "steamloader-store-empty", "Open a Steam wishlist game, choose your target price and save an alert."));
      return;
    }
    const list = el("div", "steamloader-store-alert-list");
    alerts.forEach((alert, alertIndex) => {
      const card = el("article", `steamloader-store-alert-card${alert.reached ? " is-reached" : ""}`);
      const artwork = el("div", "steamloader-store-alert-art");
      artwork.append(createArtworkImage(alert, "poster", "steamloader-store-alert-image", "steamloader-store-alert-image-placeholder"));
      const copy = el("div");
      copy.append(
        textEl("div", "steamloader-store-alert-title", alert.title),
        textEl("div", "steamloader-store-alert-copy", alert.reached ? "Target reached · ready to buy" : "Watching in the background"),
      );
      const prices = el("div", "steamloader-store-alert-prices");
      const current = alert.targetCurrencyCode === "EUR" ? alert.currentPriceEur : alert.currentPrice;
      const original = alert.targetCurrencyCode === "EUR" ? alert.originalPriceEur : alert.originalPrice;
      prices.append(
        textEl("span", "steamloader-store-alert-label", "CURRENT"),
        textEl("strong", "", formatSinglePrice(current, alert.targetCurrencyCode)),
        textEl("span", "", original
          ? `Started at ${formatSinglePrice(original, alert.targetCurrencyCode)}`
          : "Tracking starts with the first price"),
      );
      const target = el("div", "steamloader-store-alert-target");
      const isPriceAlert = !alert.mode || alert.mode === "price";
      const isSnoozed = Date.parse(alert.snoozedUntilUtc || "") > Date.now();
      target.append(textEl("span", "steamloader-store-alert-label", isPriceAlert ? "ALERT AT" : "SMART ALERT"));
      const controls = el("div", "steamloader-store-alert-target-controls");
      const updating = state.alertUpdatingId === getStoredAlertIdentity(alert);
      if (isPriceAlert) {
        controls.append(
          buttonEl("−", "steamloader-store-mini-button", () => void adjustSavedAlert(alert, -1), { disabled: updating, navRow: 30 + alertIndex }),
          textEl("strong", "steamloader-store-alert-target-value", describeAlertTarget(alert)),
          buttonEl("+", "steamloader-store-mini-button", () => void adjustSavedAlert(alert, 1), { disabled: updating, navRow: 30 + alertIndex }),
        );
      } else {
        controls.append(textEl("strong", "steamloader-store-alert-target-value", describeAlertTarget(alert)));
      }
      target.append(
        controls,
        textEl("span", `steamloader-store-alert-state${alert.reached ? " is-reached" : ""}`, updating
          ? "Saving…"
          : isSnoozed ? `Snoozed until ${new Date(alert.snoozedUntilUtc).toLocaleDateString()}`
            : alert.reached ? "Below target" : "Watching"),
      );
      const actions = el("div", "steamloader-store-alert-actions");
      actions.append(buttonEl(isSnoozed ? "Resume now" : "Snooze 7d", "steamloader-store-button is-soft", () => void snoozeSavedAlert(alert, isSnoozed ? 0 : 7), { disabled: updating, navRow: 30 + alertIndex }));
      if (alert.dealUrl) {
        actions.append(buttonEl("Open deal", "steamloader-store-button is-primary", () => void openDeal(alert.dealUrl), { disabled: updating, navRow: 30 + alertIndex }));
      }
      actions.append(buttonEl(updating ? "Removing..." : "Remove", "steamloader-store-button is-danger", () => void removeSavedAlert(alert), { disabled: updating, navRow: 30 + alertIndex }));
      card.append(artwork, copy, prices, renderAlertTrend(alert), target, actions);
      list.append(card);
    });
    main.append(list);
  }

  function describeAlertTarget(alert) {
    if (alert?.mode === "discount") return `-${Number(alert.targetDiscountPercent) || 0}%`;
    if (alert?.mode === "new-low") return "New TFS low";
    if (alert?.mode === "release") return "On release";
    return formatSinglePrice(alert?.targetPrice, alert?.targetCurrencyCode);
  }

  function renderAlertTrend(alert) {
    const currency = alert.targetCurrencyCode === "EUR" ? "EUR" : "USD";
    const original = currency === "EUR" ? alert.originalPriceEur : alert.originalPrice;
    const current = currency === "EUR" ? alert.currentPriceEur : alert.currentPrice;
    const history = (Array.isArray(alert.priceHistory) ? alert.priceHistory : [])
      .map((point) => ({
        recordedAtUtc: point.recordedAtUtc,
        value: Number(currency === "EUR" ? point.priceEur : point.price),
      }))
      .filter((point) => Number.isFinite(point.value) && point.value > 0);
    if (!history.length && Number(original) > 0) history.push({ recordedAtUtc: alert.createdAtUtc, value: Number(original) });
    if (Number(current) > 0 && (!history.length || history[history.length - 1].value !== Number(current))) {
      history.push({ recordedAtUtc: new Date().toISOString(), value: Number(current) });
    }

    const chart = el("div", "steamloader-store-alert-trend");
    chart.append(textEl("span", "steamloader-store-alert-label", "PRICE HISTORY"));
    if (!history.length) {
      chart.append(textEl("div", "steamloader-store-alert-trend-empty", "Waiting for release price"));
      return chart;
    }

    const values = history.map((point) => point.value);
    if (values.length === 1) values.push(values[0]);
    const width = 170;
    const height = 48;
    const inset = 4;
    const min = Math.min(...values);
    const max = Math.max(...values);
    const range = Math.max(0.01, max - min);
    const isFlat = max === min;
    const coordinates = values.map((value, index) => ({
      x: inset + index * ((width - inset * 2) / Math.max(1, values.length - 1)),
      y: isFlat ? height / 2 : inset + ((max - value) / range) * (height - inset * 2),
    }));
    const lineData = coordinates.map((point, index) => `${index ? "L" : "M"}${point.x.toFixed(1)} ${point.y.toFixed(1)}`).join(" ");
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.setAttribute("aria-label", `${history.length} stored price points`);
    const area = document.createElementNS("http://www.w3.org/2000/svg", "path");
    area.setAttribute("d", `${lineData} L${width - inset} ${height - inset} L${inset} ${height - inset} Z`);
    area.setAttribute("class", "steamloader-store-alert-trend-area");
    const line = document.createElementNS("http://www.w3.org/2000/svg", "path");
    line.setAttribute("d", lineData);
    line.setAttribute("class", "steamloader-store-alert-trend-line");
    const end = coordinates[coordinates.length - 1];
    const dot = document.createElementNS("http://www.w3.org/2000/svg", "circle");
    dot.setAttribute("cx", end.x.toFixed(1));
    dot.setAttribute("cy", end.y.toFixed(1));
    dot.setAttribute("r", "3.2");
    dot.setAttribute("class", "steamloader-store-alert-trend-dot");
    svg.append(area, line, dot);
    const meta = el("div", "steamloader-store-alert-trend-meta");
    meta.append(
      textEl("span", "", `${history.length} point${history.length === 1 ? "" : "s"}`),
      textEl("span", "", alert.createdAtUtc ? `Since ${formatRelativeTime(alert.createdAtUtc)}` : "Tracking active"),
    );
    chart.append(svg, meta);
    return chart;
  }

  function renderSettings(main) {
    main.append(renderPageHead("Wishlist settings", "Choose the storefront region and how prices appear. Dollar remains the default."));
    const panel = el("section", "steamloader-store-settings-panel");
    panel.append(renderRegionSelector());
    panel.append(textEl("h3", "", "Display currency"));
    const choices = el("div", "steamloader-store-currency-grid");
    const active = state.snapshot?.displayCurrencyCode || "USD";
    const selectedRegion = findStoreRegion(state.snapshot?.storeRegionCode);
    [
      ["USD", "US Dollar", "$19.99", "Direct US store price"],
      ["EUR", "Euro", `17,49 ${String.fromCharCode(0x20ac)}`, "Direct German store price when available"],
      ["BOTH", "Dollar + Euro", `$19.99 ${String.fromCharCode(0xb7)} 17,49 ${String.fromCharCode(0x20ac)}`, "Show both regional prices"],
      ["REGION", `${selectedRegion[1]} region`, formatSinglePrice(19.99, selectedRegion[2]), "Use the selected storefront's local price"],
    ].forEach(([value, title, sample, copy]) => {
      const button = buttonEl("", `steamloader-store-currency-card${active === value ? " is-active" : ""}`, () => void setCurrency(value));
      button.append(
        textEl("div", "steamloader-store-currency-check", active === value ? "✓" : ""),
        textEl("div", "steamloader-store-currency-title", title),
        textEl("div", "steamloader-store-currency-sample", sample),
        textEl("div", "steamloader-store-currency-copy", copy),
      );
      choices.append(button);
    });
    panel.append(choices);
    panel.append(renderSourcePreferences(), renderArtworkCacheSettings(), renderWishlistBackupSettings());
    const note = el("div", "steamloader-store-data-note");
    note.append(
      textEl("strong", "", "No API key required"),
      textEl("span", "", "Prices are loaded directly from the selected Steam, GOG, Xbox and Epic Games storefronts. Region changes can affect price, currency and availability."),
      textEl("span", "", "Every priced Buy button opens the matching product page. Instant Gaming appears only for an exact, in-stock PC match with a readable regional price."),
      textEl("span", "", "Artwork tries public Steam CDN covers and headers first, then the artwork returned by the direct store."),
      textEl("span", "", state.snapshot?.usdPerEur ? `Current reference: 1 € = $${Number(state.snapshot.usdPerEur).toFixed(4)}` : "EUR conversion will appear after the ECB rate is available."),
    );
    panel.append(note);
    main.append(panel);
  }

  function renderSourcePreferences() {
    const section = el("section", "steamloader-store-qol-settings");
    section.append(textEl("h3", "", "Deal sources and refresh"));
    const rows = el("div", "steamloader-store-setting-rows");
    const notifications = buttonEl("", `steamloader-store-setting-row${state.snapshot?.notificationsEnabled !== false ? " is-active" : ""}`, () => void setStorePreferences({ notificationsEnabled: state.snapshot?.notificationsEnabled === false }));
    notifications.append(
      textEl("strong", "", "Price notifications"),
      textEl("span", "", state.snapshot?.notificationsEnabled !== false ? "Windows alerts are shown when an enabled target is newly reached." : "Alerts keep tracking silently without desktop notifications."),
      textEl("em", "", state.snapshot?.notificationsEnabled !== false ? "ON" : "OFF"),
    );
    rows.append(notifications);
    const keyshops = buttonEl("", `steamloader-store-setting-row${state.snapshot?.includeKeyshops !== false ? " is-active" : ""}`, () => void setStorePreferences({ includeKeyshops: state.snapshot?.includeKeyshops === false }));
    keyshops.append(
      textEl("strong", "", "Verified keyshops"),
      textEl("span", "", state.snapshot?.includeKeyshops !== false ? "Instant Gaming exact regional PC matches are included." : "Only official storefronts are compared."),
      textEl("em", "", state.snapshot?.includeKeyshops !== false ? "ON" : "OFF"),
    );
    rows.append(keyshops);
    const interval = Number(state.snapshot?.refreshIntervalMinutes) || 30;
    const intervalRow = el("div", "steamloader-store-setting-row is-static");
    intervalRow.append(textEl("strong", "", "Background refresh"), textEl("span", "", `Every ${interval} minutes`));
    const intervalActions = el("div", "steamloader-store-setting-inline-actions");
    [15, 30, 60, 120].forEach((minutes) => intervalActions.append(buttonEl(
      `${minutes}m`,
      `steamloader-store-filter-chip${interval === minutes ? " is-active" : ""}`,
      () => void setStorePreferences({ refreshIntervalMinutes: minutes }),
    )));
    intervalRow.append(intervalActions);
    rows.append(intervalRow);
    section.append(rows);
    return section;
  }

  function renderArtworkCacheSettings() {
    const cache = state.snapshot?.artworkCache || {};
    const maximum = Number(cache.maximumMegabytes) || 256;
    const retention = Number(cache.retentionDays) || 45;
    const section = el("section", "steamloader-store-qol-settings");
    section.append(
      textEl("h3", "", "Artwork cache"),
      textEl("p", "steamloader-store-setting-copy", `${Number(cache.fileCount) || 0} images - ${formatBytes(Number(cache.totalBytes) || 0)} used. Visible artwork loads first; nearby rows are prefetched quietly.`),
    );
    const choices = el("div", "steamloader-store-setting-inline-actions");
    [128, 256, 512, 1024].forEach((size) => choices.append(buttonEl(
      `${size} MB`,
      `steamloader-store-filter-chip${maximum === size ? " is-active" : ""}`,
      () => void setArtworkCachePolicy(size, retention),
    )));
    [15, 45, 90].forEach((days) => choices.append(buttonEl(
      `${days} days`,
      `steamloader-store-filter-chip${retention === days ? " is-active" : ""}`,
      () => void setArtworkCachePolicy(maximum, days),
    )));
    choices.append(buttonEl("Clear cache", "steamloader-store-button is-danger", () => void clearArtworkCache()));
    section.append(choices);
    return section;
  }

  function renderWishlistBackupSettings() {
    const section = el("section", "steamloader-store-qol-settings");
    section.append(
      textEl("h3", "", "Backup and restore"),
      textEl("p", "steamloader-store-setting-copy", "Copy your local wishlist, alerts, tags and TFS price history as portable JSON. Store credentials are never included."),
    );
    const actions = el("div", "steamloader-store-setting-inline-actions");
    actions.append(
      buttonEl("Copy backup", "steamloader-store-button is-soft", () => void copyWishlistBackup()),
      buttonEl("Import clipboard", "steamloader-store-button is-soft", () => void importWishlistBackup()),
    );
    section.append(actions);
    return section;
  }

  function renderRegionSelector() {
    const selected = findStoreRegion(state.snapshot?.storeRegionCode);
    const section = el("section", "steamloader-store-region-setting");
    const copy = el("div");
    copy.append(
      textEl("h3", "", "Store region"),
      textEl("p", "", "Changes local prices and availability across the direct stores."),
    );
    const trigger = buttonEl(
      `${selected[1]} (${selected[3]})`,
      `steamloader-store-region-trigger${state.regionMenuOpen ? " is-open" : ""}`,
      () => {
        state.regionMenuOpen = !state.regionMenuOpen;
        state.focusIndex = Math.max(0, storeRegions.findIndex((region) => region[0] === selected[0]));
        render();
      },
    );
    trigger.dataset.storeFocus = state.regionMenuOpen ? "false" : "true";
    section.append(copy, trigger);
    if (state.regionMenuOpen) {
      const menu = el("div", "steamloader-store-region-menu");
      storeRegions.forEach((region) => {
        const option = buttonEl("", `steamloader-store-region-option${region[0] === selected[0] ? " is-active" : ""}`, () => void setStoreRegion(region[0]));
        option.append(
          textEl("span", "steamloader-store-region-marker", ""),
          textEl("strong", "", region[1]),
          textEl("span", "", `(${region[3]})`),
        );
        menu.append(option);
      });
      section.append(menu);
    }
    return section;
  }

  function findStoreRegion(code) {
    return storeRegions.find((region) => region[0] === code) || storeRegions[0];
  }

  function appendGameSection(parent, title, copy, games, cardMode) {
    const section = el("section", "steamloader-store-section");
    const heading = el("div", "steamloader-store-section-head");
    heading.append(textEl("h2", "", title));
    if (copy) heading.append(textEl("p", "", copy));
    section.append(heading);
    if (!games.length) {
      section.append(textEl("div", "steamloader-store-empty is-compact", title.startsWith("From") ? "No wishlist sale is available right now." : "No games available."));
    } else {
      const rail = el("div", `steamloader-store-rail is-${cardMode}`);
      games.forEach((game) => rail.append(renderGameCard(game, cardMode)));
      section.append(rail);
    }
    parent.append(section);
  }

  function renderGameGrid(games) {
    const grid = el("div", "steamloader-store-grid");
    games.forEach((game) => grid.append(renderGameCard(game, "portrait")));
    return grid;
  }

  function renderGameCard(game, mode) {
    const selectable = state.activeTab === "wishlist" && state.wishlistManageMode;
    const selected = selectable && state.selectedWishlistIds.has(game.id);
    const card = buttonEl("", `steamloader-store-card is-${mode}${selected ? " is-selected" : ""}`, () => {
      if (selectable) {
        if (selected) state.selectedWishlistIds.delete(game.id);
        else state.selectedWishlistIds.add(game.id);
        render();
      } else {
        void openDetails(game);
      }
    });
    const art = el("div", "steamloader-store-card-art");
    art.append(createArtworkImage(game, mode === "portrait" ? "poster" : "header"));
    if (game.reviewPercent) art.append(textEl("div", "steamloader-store-rating", `● ${game.reviewPercent}%`));
    if (game.isWishlisted) art.append(textEl("div", "steamloader-store-heart", "♥"));
    if (selectable) art.append(textEl("div", `steamloader-store-select-check${selected ? " is-selected" : ""}`, selected ? "OK" : ""));
    if (game.hasUnseenChange) art.append(textEl("div", "steamloader-store-change-badge", formatChangeKind(game.changeKind)));
    else if (game.isPinned) art.append(textEl("div", "steamloader-store-pin-badge", "PINNED"));
    const info = el("div", "steamloader-store-card-info");
    const wishlistSource = game.isSteamWishlisted && game.isLocallyWishlisted
      ? "Steam + TFS"
      : game.isSteamWishlisted ? "Steam wishlist" : game.isLocallyWishlisted ? "TFS wishlist" : "";
    info.append(
      textEl("div", "steamloader-store-card-title", game.title),
      wishlistSource ? textEl("div", "steamloader-store-card-source", wishlistSource) : document.createTextNode(""),
      textEl("div", "steamloader-store-card-store", game.bestStoreName || "Compare stores"),
      renderPrice(game, "steamloader-store-card-price"),
    );
    if (game.discountPercent > 0) info.append(textEl("div", "steamloader-store-discount", `-${game.discountPercent}%`));
    card.append(art, info);
    return card;
  }

  function renderPrice(game, className) {
    const wrap = el("div", className);
    const parts = getDisplayPriceParts(game);
    if (!parts.length) {
      wrap.append(textEl("span", "steamloader-store-unreleased-badge", "UNRELEASED"));
      return wrap;
    }
    parts.forEach((part, index) => wrap.append(
      textEl(index === 0 ? "strong" : "span", "", formatSinglePrice(part.value, part.currency)),
    ));
    return wrap;
  }

  function getDisplayPriceParts(game) {
    const mode = state.snapshot?.displayCurrencyCode || "USD";
    if (mode === "EUR") {
      return isPositivePrice(game?.cheapestPriceEur) ? [{ value: game.cheapestPriceEur, currency: "EUR" }] : [];
    }
    if (mode === "REGION") {
      return isPositivePrice(game?.regionalPrice)
        ? [{ value: game.regionalPrice, currency: game.regionalCurrencyCode || state.snapshot?.regionalCurrencyCode || "USD" }]
        : [];
    }
    if (mode === "BOTH") {
      return [
        ...(isPositivePrice(game?.cheapestPrice) ? [{ value: game.cheapestPrice, currency: "USD" }] : []),
        ...(isPositivePrice(game?.cheapestPriceEur) ? [{ value: game.cheapestPriceEur, currency: "EUR" }] : []),
      ];
    }
    return isPositivePrice(game?.cheapestPrice) ? [{ value: game.cheapestPrice, currency: "USD" }] : [];
  }

  function isPositivePrice(value) {
    if (value == null || value === "") return false;
    const number = Number(value);
    return Number.isFinite(number) && number > 0;
  }

  function renderDetails() {
    const game = state.selectedGame;
    const backdrop = el("div", "steamloader-store-modal-backdrop");
    const modal = el("section", "steamloader-store-modal");
    const banner = el("div", "steamloader-store-modal-banner");
    setArtworkBackground(banner, game, "header", "--store-detail-image");
    banner.append(
      buttonEl("×", "steamloader-store-modal-close", () => closeDetails(), { navRow: 0 }),
      textEl("h2", "steamloader-store-modal-title", game.title),
    );
    const body = el("div", "steamloader-store-modal-body");
    const summary = el("div", "steamloader-store-summary");
    const summaryCopy = el("div");
    const hasPrice = getDisplayPriceParts(game).length > 0;
    summaryCopy.append(
      textEl("div", "steamloader-store-best-label", `${hasPrice ? "BEST PRICE" : "COMING SOON"} · ${game.bestStoreName || "PC STORE"}`),
      renderPrice(game, "steamloader-store-summary-price"),
    );
    const summaryActions = el("div", "steamloader-store-summary-actions");
    summaryActions.append(
      buttonEl(
        game.isLocallyWishlisted ? "Remove from TFS wishlist" : "Add to TFS wishlist",
        `steamloader-store-button ${game.isLocallyWishlisted ? "is-soft" : "is-wishlist"}`,
        () => void setLocalWishlist(game, !game.isLocallyWishlisted),
        { navRow: 10 },
      ),
      buttonEl(hasPrice ? "Buy best price" : "View store page", "steamloader-store-button is-primary is-buy", () => void openDeal(game.bestDealUrl), { disabled: !game.bestDealUrl, navRow: 10 }),
    );
    if (game.isSteamWishlisted) summaryActions.prepend(textEl("span", "steamloader-store-steam-wishlist-badge", "♥ Steam wishlist"));
    summary.append(summaryCopy, summaryActions);
    body.append(summary);
    if (game.isWishlisted) body.append(renderWishlistGameTools(game), renderGameTracking(game));
    if (getGameAlertIdentity(game) && game.isWishlisted) body.append(renderAlertEditor(game));
    body.append(renderOfferList());
    modal.append(banner, body);
    backdrop.append(modal);
    backdrop.addEventListener("click", (event) => { if (event.target === backdrop) closeDetails(); });
    return backdrop;
  }

  function renderWishlistGameTools(game) {
    const panel = el("section", "steamloader-store-game-tools");
    const copy = el("div", "steamloader-store-game-tools-copy");
    copy.append(
      textEl("strong", "", game.matchConfidence === "exact" ? "Verified exact match" : "Price match pending"),
      textEl("span", "", game.matchNote || "TFS verifies title, PC platform and selected region before comparing a price."),
      textEl("span", "", game.priceCheckedAtUtc ? `Prices checked ${formatRelativeTime(game.priceCheckedAtUtc)}` : "Waiting for the first verified price check"),
    );
    const actions = el("div", "steamloader-store-game-tools-actions");
    actions.append(buttonEl(game.isPinned ? "Unpin" : "Pin", "steamloader-store-button is-soft", () => void setWishlistMetadata(game, { isPinned: !game.isPinned, tags: null }), { navRow: 15 }));
    for (const tag of ["Must Buy", "Later", "Co-op"]) {
      const active = (game.tags || []).some((value) => value.toLowerCase() === tag.toLowerCase());
      actions.append(buttonEl(tag, `steamloader-store-filter-chip${active ? " is-active" : ""}`, () => {
        const tags = active
          ? (game.tags || []).filter((value) => value.toLowerCase() !== tag.toLowerCase())
          : [...(game.tags || []), tag];
        void setWishlistMetadata(game, { isPinned: null, tags });
      }, { navRow: 15 }));
    }
    panel.append(copy, actions);
    return panel;
  }

  function renderGameTracking(game) {
    const currency = getPreferredAlertCurrencyCode();
    const current = currency === "EUR" ? game.cheapestPriceEur : game.cheapestPrice;
    const original = currency === "EUR" ? game.trackingStartPriceEur : game.trackingStartPrice;
    const low = currency === "EUR" ? game.trackedLowPriceEur : game.trackedLowPrice;
    const panel = el("section", "steamloader-store-game-tracking");
    const stats = el("div", "steamloader-store-game-tracking-stats");
    [["STARTED AT", original], ["CURRENT", current], ["TFS LOW", low]].forEach(([label, value]) => {
      const stat = el("div", "steamloader-store-game-tracking-stat");
      stat.append(textEl("span", "", label), textEl("strong", "", formatSinglePrice(value, currency)));
      stats.append(stat);
    });
    const chart = renderAlertTrend({
      targetCurrencyCode: currency,
      originalPrice: game.trackingStartPrice,
      originalPriceEur: game.trackingStartPriceEur,
      currentPrice: game.cheapestPrice,
      currentPriceEur: game.cheapestPriceEur,
      createdAtUtc: game.trackingStartedAtUtc,
      priceHistory: game.priceHistory,
    });
    panel.append(stats, chart);
    return panel;
  }

  function renderAlertEditor(game) {
    const editor = el("div", "steamloader-store-alert-editor");
    const copy = el("div");
    copy.append(
      textEl("div", "steamloader-store-alert-editor-title", "Price alert"),
      textEl("div", "steamloader-store-alert-editor-copy", state.alertDraft?.enabled ? "Alert active · adjust or remove it anytime" : "Notify me when this wishlist game reaches my price"),
    );
    const controls = el("div", "steamloader-store-alert-controls");
    const adjust = (delta) => {
      const next = Math.max(0.01, (Number(state.alertDraft.targetPrice) || 0) + delta);
      state.alertDraft.targetPrice = next.toFixed(2);
      state.alertDraft.mode = "price";
      state.alertDraft.edited = true;
      render();
    };
    controls.append(
      buttonEl("−", "steamloader-store-mini-button", () => adjust(-1), { navRow: 20 }),
      textEl("div", "steamloader-store-alert-value", formatSinglePrice(Number(state.alertDraft?.targetPrice), state.alertDraft?.currencyCode)),
      buttonEl("+", "steamloader-store-mini-button", () => adjust(1), { navRow: 20 }),
      buttonEl(state.alertDraft?.currencyCode || "USD", "steamloader-store-mini-button is-wide", () => {
        state.alertDraft.currencyCode = state.alertDraft.currencyCode === "USD" ? "EUR" : "USD";
        const value = state.alertDraft.currencyCode === "EUR" ? game.cheapestPriceEur : game.cheapestPrice;
        if (value) state.alertDraft.targetPrice = Number(value).toFixed(2);
        state.alertDraft.mode = "price";
        state.alertDraft.edited = true;
        render();
      }, { navRow: 20 }),
      buttonEl(state.alertDraft?.enabled ? "Update" : "Save alert", "steamloader-store-button is-primary", () => void saveAlert(true), { navRow: 20 }),
    );
    if (state.alertDraft?.enabled) controls.append(buttonEl("Remove", "steamloader-store-button is-danger", () => void saveAlert(false), { navRow: 20 }));
    const presets = el("div", "steamloader-store-alert-presets");
    const setPricePreset = (multiplier) => {
      const current = Number(state.alertDraft.currencyCode === "EUR" ? game.cheapestPriceEur : game.cheapestPrice);
      if (current > 0) state.alertDraft.targetPrice = Math.max(0.01, Math.round(current * multiplier * 100) / 100).toFixed(2);
      state.alertDraft.mode = "price";
      state.alertDraft.edited = true;
      render();
    };
    presets.append(
      buttonEl("Price -10%", `steamloader-store-filter-chip${state.alertDraft?.mode === "price" ? " is-active" : ""}`, () => setPricePreset(0.9), { navRow: 21 }),
      buttonEl("Price -25%", "steamloader-store-filter-chip", () => setPricePreset(0.75), { navRow: 21 }),
      buttonEl("At least -50%", `steamloader-store-filter-chip${state.alertDraft?.mode === "discount" ? " is-active" : ""}`, () => {
        state.alertDraft.mode = "discount";
        state.alertDraft.targetDiscountPercent = 50;
        state.alertDraft.edited = true;
        render();
      }, { navRow: 21 }),
      buttonEl("New TFS low", `steamloader-store-filter-chip${state.alertDraft?.mode === "new-low" ? " is-active" : ""}`, () => {
        state.alertDraft.mode = "new-low";
        state.alertDraft.edited = true;
        render();
      }, { navRow: 21 }),
      buttonEl("On release", `steamloader-store-filter-chip${state.alertDraft?.mode === "release" ? " is-active" : ""}`, () => {
        state.alertDraft.mode = "release";
        state.alertDraft.edited = true;
        render();
      }, { navRow: 21 }),
    );
    editor.append(copy, controls, presets);
    return editor;
  }

  function renderOfferList() {
    const section = el("section", "steamloader-store-offers");
    const heading = el("div", "steamloader-store-offers-head");
    heading.append(textEl("h3", "", "Compare stores"), textEl("span", "", `${state.offers.length || ""} offers`));
    section.append(heading);
    if (state.offersLoading) {
      for (let index = 0; index < 4; index++) section.append(el("div", "steamloader-store-offer-skeleton"));
      return section;
    }
    if (!state.offers.length) {
      section.append(textEl("div", "steamloader-store-empty is-compact", "No exact direct price was returned by another store."));
      section.append(renderRetailerShortcuts());
      return section;
    }
    state.offers.forEach((offer, index) => {
      const row = el("div", `steamloader-store-offer${index === 0 ? " is-best" : ""}`);
      const store = el("div", "steamloader-store-offer-store");
      store.append(
        textEl("strong", "", offer.storeName),
        index === 0 ? textEl("span", "", "BEST PRICE") : document.createTextNode(""),
        offer.storeKind === "keyshop" ? textEl("span", "steamloader-store-kind-badge", "KEYSHOP") : textEl("span", "steamloader-store-kind-badge is-official", "OFFICIAL"),
        offer.checkedAtUtc ? textEl("small", "", `Checked ${formatRelativeTime(offer.checkedAtUtc)}`) : document.createTextNode(""),
      );
      const price = el("div", "steamloader-store-offer-price");
      const mode = state.snapshot?.displayCurrencyCode || "USD";
      if (mode === "EUR") price.append(textEl("strong", "", formatSinglePrice(offer.priceEur, "EUR")));
      else if (mode === "REGION") price.append(textEl("strong", "", formatSinglePrice(offer.regionalPrice, offer.regionalCurrencyCode || state.snapshot?.regionalCurrencyCode)));
      else if (mode === "BOTH") price.append(textEl("strong", "", formatSinglePrice(offer.price, "USD")), textEl("span", "", formatSinglePrice(offer.priceEur, "EUR")));
      else price.append(textEl("strong", "", formatSinglePrice(offer.price, "USD")));
      if (offer.discountPercent > 0) price.append(textEl("em", "", `-${offer.discountPercent}%`));
      row.append(store, price, buttonEl("Buy", `steamloader-store-button${index === 0 ? " is-primary" : " is-soft"}`, () => void openDeal(offer.dealUrl), { navRow: 100 + index }));
      section.append(row);
    });
    section.append(renderRetailerShortcuts());
    return section;
  }

  function renderRetailerShortcuts() {
    const game = state.selectedGame;
    const wrap = el("div", "steamloader-store-retailer-shortcuts");
    wrap.append(
      textEl("strong", "", "More direct store searches"),
      textEl("span", "", "Search shortcuts do not claim a price; the store shows its current final price."),
    );
    const existing = new Set(state.offers.map((offer) => String(offer.storeName || "").toLowerCase()));
    const query = encodeURIComponent(game?.title || "");
    const xboxLocale = getXboxStoreLocale();
    const epicLanguage = getEpicStoreLanguage();
    const links = [
      ["Steam", `https://store.steampowered.com/search/?term=${query}`, "steam"],
      ["GOG", `https://www.gog.com/en/games?query=${query}`, "gog"],
      ["Xbox", `https://www.xbox.com/${xboxLocale}/search/results?q=${query}`, "xbox"],
      ["Epic Games", `https://store.epicgames.com/browse?q=${query}&sortBy=relevancy&sortDir=DESC&lang=${epicLanguage}`, "epic games"],
    ];
    const actions = el("div", "steamloader-store-retailer-actions");
    links
      .filter(([, , storeKey]) => !existing.has(storeKey))
      .forEach(([name, url]) => actions.append(
        buttonEl(`Search ${name}`, "steamloader-store-button is-soft", () => void openDeal(url), { navRow: 1000 }),
      ));
    wrap.append(actions);
    return wrap;
  }

  function getXboxStoreLocale() {
    return {
      US: "en-US", DE: "de-DE", GB: "en-GB", CA: "en-CA", AU: "en-AU", NZ: "en-NZ",
      BR: "pt-BR", MX: "es-MX", CL: "es-CL", CO: "es-CO", JP: "ja-JP", KR: "ko-KR", CN: "zh-CN",
    }[state.snapshot?.storeRegionCode] || "en-US";
  }

  function getEpicStoreLanguage() {
    return {
      US: "en-US", DE: "de", GB: "en", CA: "en", AU: "en", NZ: "en",
      BR: "pt-BR", MX: "es-MX", CL: "es-MX", CO: "es-MX", JP: "ja", KR: "ko", CN: "zh-CN",
    }[state.snapshot?.storeRegionCode] || "en-US";
  }

  function renderPageHead(title, copy) {
    const head = el("div", "steamloader-store-page-head");
    head.append(textEl("h2", "", title), textEl("p", "", copy));
    return head;
  }

  function renderFooter() {
    const footer = el("footer", "steamloader-store-footer");
    footer.append(
      textEl("span", "", "A Select"),
      textEl("span", "", "B Back"),
      textEl("span", "", "Y Refresh"),
      textEl("span", "steamloader-store-footer-source", state.snapshot?.priceSource || "No API key required"),
    );
    return footer;
  }

  function renderSkeletons() {
    const wrap = el("div", "steamloader-store-skeleton-wrap");
    wrap.append(el("div", "steamloader-store-hero-skeleton"));
    const rail = el("div", "steamloader-store-skeleton-rail");
    for (let index = 0; index < 7; index++) rail.append(el("div", "steamloader-store-card-skeleton"));
    wrap.append(rail);
    return wrap;
  }

  function getWishlist() { return Array.isArray(state.snapshot?.wishlist) ? state.snapshot.wishlist : []; }
  function getTrending() { return Array.isArray(state.snapshot?.trending) ? state.snapshot.trending : []; }
  function getFeatured() { return Array.isArray(state.snapshot?.featuredDeals) ? state.snapshot.featuredDeals : []; }
  function getAlerts() { return Array.isArray(state.snapshot?.alerts) ? state.snapshot.alerts : []; }

  function getGameAlertIdentity(game) {
    const steamAppId = Number(game?.steamAppId);
    if (Number.isSafeInteger(steamAppId) && steamAppId > 0) return `steam:${steamAppId}`;
    const gameId = String(game?.id || "").trim();
    return gameId ? `game:${gameId.toLowerCase()}` : "";
  }

  function getStoredAlertIdentity(alert) {
    const steamAppId = Number(alert?.steamAppId);
    if (Number.isSafeInteger(steamAppId) && steamAppId > 0) return `steam:${steamAppId}`;
    const gameId = String(alert?.gameId || "").trim();
    return gameId ? `game:${gameId.toLowerCase()}` : "";
  }

  function getPreferredAlertCurrencyCode() {
    const displayCurrency = String(state.snapshot?.displayCurrencyCode || "USD").toUpperCase();
    const regionalCurrency = String(state.snapshot?.regionalCurrencyCode || "").toUpperCase();
    if (displayCurrency === "EUR") return "EUR";
    if ((displayCurrency === "REGION" || displayCurrency === "BOTH") && regionalCurrency === "EUR") return "EUR";
    return "USD";
  }

  function shuffledGames(games, salt) {
    return [...(Array.isArray(games) ? games : [])]
      .map((game) => ({ game, rank: seededRank(`${state.discoverySeed}:${salt}:${game.id || game.title}`) }))
      .sort((left, right) => left.rank - right.rank)
      .map((item) => item.game);
  }

  function seededRank(value) {
    let hash = 2166136261;
    for (const character of String(value)) {
      hash ^= character.charCodeAt(0);
      hash = Math.imul(hash, 16777619);
    }
    return hash >>> 0;
  }

  function formatSinglePrice(value, currency = "USD") {
    if (value == null || value === "") return "—";
    const number = Number(value);
    if (!Number.isFinite(number)) return "—";
    const normalizedCurrency = String(currency || "USD").toUpperCase();
    const locales = {
      EUR: "de-DE", USD: "en-US", GBP: "en-GB", CAD: "en-CA", AUD: "en-AU", NZD: "en-NZ",
      BRL: "pt-BR", MXN: "es-MX", CLP: "es-CL", COP: "es-CO", JPY: "ja-JP", KRW: "ko-KR", CNY: "zh-CN",
    };
    try {
      return new Intl.NumberFormat(locales[normalizedCurrency] || "en-US", {
        style: "currency",
        currency: normalizedCurrency,
        currencyDisplay: "narrowSymbol",
      }).format(number);
    } catch {
      return `${number.toFixed(2)} ${normalizedCurrency}`;
    }
  }

  function getArtworkCandidates(subject, kind = "poster") {
    const appId = Number(subject?.steamAppId);
    const steam = Number.isSafeInteger(appId) && appId > 0
      ? kind === "poster"
        ? [
            `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/library_600x900_2x.jpg`,
            `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/library_600x900.jpg`,
            `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900.jpg`,
            `https://shared.akamai.steamstatic.com/steam/apps/${appId}/library_600x900.jpg`,
            `https://steamcdn-a.akamaihd.net/steam/apps/${appId}/library_600x900.jpg`,
          ]
        : [
            `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/library_hero_2x.jpg`,
            `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/library_hero.jpg`,
            `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/header.jpg`,
            `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/header.jpg`,
            `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/capsule_616x353.jpg`,
            `https://shared.akamai.steamstatic.com/steam/apps/${appId}/header.jpg`,
          ]
      : [];
    const supplied = kind === "poster"
      ? [subject?.imageUrl, subject?.fallbackImageUrl]
      : [subject?.headerImageUrl, subject?.imageUrl, subject?.fallbackImageUrl];
    return [...new Set([...steam, ...supplied].map((value) => String(value || "").trim()).filter(Boolean))];
  }

  function resolveArtworkFallback(subject, kind) {
    const title = String(subject?.title || "").trim();
    const emergencyImage = kind === "poster" ? String(subject?.headerImageUrl || "").trim() : "";
    if (!title) return Promise.resolve(emergencyImage);
    const assetType = kind === "poster" ? "grid_p" : "hero";
    const cacheKey = `${normalizeArtworkTitle(title)}:${assetType}`;
    if (artworkFallbackCache.has(cacheKey)) return artworkFallbackCache.get(cacheKey);
    const request = (async () => {
      try {
        const matches = await fetchJson(`api/artwork/search?term=${encodeURIComponent(title)}`);
        const expected = normalizeArtworkTitle(title);
        const available = Array.isArray(matches) ? matches : [];
        const match = available.find((item) => normalizeArtworkTitle(item?.name) === expected) || available[0];
        if (!match?.id) return emergencyImage;
        const assets = await fetchJson(`api/artwork/assets?gameId=${encodeURIComponent(match.id)}&type=${assetType}&page=0`);
        const asset = Array.isArray(assets) ? assets.find((item) => item?.url || item?.thumbnailUrl) : null;
        return String(asset?.url || asset?.thumbnailUrl || emergencyImage).trim();
      } catch {
        return emergencyImage;
      }
    })();
    artworkFallbackCache.set(cacheKey, request);
    return request;
  }

  function normalizeArtworkTitle(value) {
    return String(value || "")
      .normalize("NFKD")
      .replace(/[™®©]/g, "")
      .replace(/[^a-z0-9]+/gi, " ")
      .trim()
      .toLowerCase();
  }

  function getCachedArtworkUrl(source) {
    const value = String(source || "").trim();
    if (!value || typeof window.__steamLoaderApiUrl !== "function") return value;
    try {
      const url = new URL(value);
      const allowedHosts = new Set([
        "shared.fastly.steamstatic.com",
        "cdn.cloudflare.steamstatic.com",
        "shared.akamai.steamstatic.com",
        "steamcdn-a.akamaihd.net",
        "cdn.steamgriddb.com",
        "cdn2.steamgriddb.com",
        "images.gog.com",
        "store-images.s-microsoft.com",
      ]);
      const allowedSuffixes = [".gog-statics.com", ".epicgames.com", ".unrealengine.com", ".s-microsoft.com"];
      const host = url.hostname.toLowerCase();
      if (url.protocol !== "https:" || (!allowedHosts.has(host) && !allowedSuffixes.some((suffix) => host.endsWith(suffix)))) return value;
      return window.__steamLoaderApiUrl(`api/store/artwork?source=${encodeURIComponent(value)}`);
    } catch {
      return value;
    }
  }

  function resetArtworkLoading() {
    artworkLoadGeneration += 1;
    artworkLoadObserver?.disconnect();
    artworkLoadObserver = null;
    artworkLoadQueue = [];
    activeArtworkLoads = 0;
  }

  function scheduleArtworkLoad(element, start) {
    const generation = artworkLoadGeneration;
    window.requestAnimationFrame(() => {
      if (generation !== artworkLoadGeneration || !element.isConnected) return;
      if (typeof window.IntersectionObserver !== "function") {
        enqueueArtworkLoad({ element, start, generation });
        return;
      }
      if (!artworkLoadObserver) {
        const main = ensureMounted().querySelector(".steamloader-store-main");
        artworkLoadObserver = new window.IntersectionObserver((entries, observer) => {
          for (const entry of entries) {
            if (!entry.isIntersecting) continue;
            observer.unobserve(entry.target);
            const queuedStart = entry.target.__steamLoaderArtworkStart;
            const queuedGeneration = Number(entry.target.__steamLoaderArtworkGeneration);
            delete entry.target.__steamLoaderArtworkStart;
            delete entry.target.__steamLoaderArtworkGeneration;
            if (typeof queuedStart === "function") {
              enqueueArtworkLoad({
                element: entry.target,
                start: queuedStart,
                generation: queuedGeneration,
              });
            }
          }
        }, {
          root: main,
          rootMargin: "90px 120px",
          threshold: 0.01,
        });
      }
      element.__steamLoaderArtworkStart = start;
      element.__steamLoaderArtworkGeneration = generation;
      artworkLoadObserver.observe(element);
    });
  }

  function enqueueArtworkLoad(job) {
    if (job.generation !== artworkLoadGeneration || !job.element.isConnected) return;
    artworkLoadQueue.push(job);
    drainArtworkLoadQueue();
  }

  function drainArtworkLoadQueue() {
    while (activeArtworkLoads < maxConcurrentArtworkLoads && artworkLoadQueue.length) {
      const job = artworkLoadQueue.shift();
      if (!job || job.generation !== artworkLoadGeneration || !job.element.isConnected) continue;
      activeArtworkLoads += 1;
      let finished = false;
      const finish = () => {
        if (finished) return;
        finished = true;
        if (job.generation !== artworkLoadGeneration) return;
        activeArtworkLoads = Math.max(0, activeArtworkLoads - 1);
        drainArtworkLoadQueue();
      };
      try {
        job.start(finish);
      } catch {
        finish();
      }
    }
  }

  function createArtworkImage(subject, kind = "poster", className = "", placeholderClass = "steamloader-store-card-placeholder") {
    const candidates = getArtworkCandidates(subject, kind);
    const frame = el("div", "steamloader-store-artwork-frame is-loading");
    const image = document.createElement("img");
    image.loading = "lazy";
    image.decoding = "async";
    image.alt = "";
    if (className) image.className = className;
    const loadingIndicator = el("div", "steamloader-store-artwork-loader");
    loadingIndicator.setAttribute("aria-hidden", "true");
    for (let index = 0; index < 3; index += 1) {
      loadingIndicator.append(el("span", "steamloader-store-artwork-loader-dot"));
    }
    let candidateIndex = 0;
    let fallbackRequested = false;
    let finishQueuedLoad = () => {};
    const showPlaceholder = () => {
      frame.classList.remove("is-loading");
      frame.replaceChildren(textEl("div", placeholderClass, initials(subject?.title)));
      finishQueuedLoad();
    };
    const loadNext = () => {
      if (candidateIndex < candidates.length) {
        image.src = getCachedArtworkUrl(candidates[candidateIndex++]);
        return;
      }
      if (!fallbackRequested) {
        fallbackRequested = true;
        void resolveArtworkFallback(subject, kind).then((source) => {
          if (source) image.src = getCachedArtworkUrl(source);
          else showPlaceholder();
        });
        return;
      }
      showPlaceholder();
    };
    image.addEventListener("load", () => {
      image.classList.add("is-loaded");
      frame.classList.remove("is-loading");
      loadingIndicator.remove();
      finishQueuedLoad();
    });
    image.addEventListener("error", loadNext);
    frame.append(image, loadingIndicator);
    scheduleArtworkLoad(frame, (finish) => {
      finishQueuedLoad = finish;
      loadNext();
    });
    return frame;
  }

  function setArtworkBackground(element, subject, kind, propertyName) {
    const candidates = getArtworkCandidates(subject, kind);
    let candidateIndex = 0;
    let fallbackRequested = false;
    const loadNext = () => {
      if (candidateIndex >= candidates.length) {
        if (!fallbackRequested) {
          fallbackRequested = true;
          void resolveArtworkFallback(subject, kind).then((source) => {
            if (source) trySource(source);
          });
        }
        return;
      }
      trySource(candidates[candidateIndex++]);
    };
    const trySource = (source) => {
      const cachedSource = getCachedArtworkUrl(source);
      const probe = new Image();
      probe.onload = () => element.style.setProperty(propertyName, `url("${safeCssUrl(cachedSource)}")`);
      probe.onerror = loadNext;
      probe.src = cachedSource;
    };
    loadNext();
  }

  function formatRelativeTime(value) {
    const time = Date.parse(value);
    if (!Number.isFinite(time)) return "recently";
    const minutes = Math.max(0, Math.round((Date.now() - time) / 60000));
    if (minutes < 1) return "just now";
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    return hours < 24 ? `${hours}h ago` : `${Math.round(hours / 24)}d ago`;
  }

  function safeCssUrl(value) { return String(value || "").replace(/["\\\n\r]/g, ""); }
  function initials(value) { return String(value || "?").split(/\s+/).filter(Boolean).slice(0, 2).map((part) => part[0]).join("").toUpperCase(); }
  function isTextInput(target) { return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target?.isContentEditable; }

  function el(tag, className = "") {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return node;
  }
  function textEl(tag, className, value) {
    const node = el(tag, className);
    node.textContent = value == null ? "" : String(value);
    return node;
  }
  function buttonEl(label, className, onClick, options = {}) {
    const button = el("button", className);
    button.type = "button";
    button.textContent = label;
    button.disabled = Boolean(options.disabled);
    button.dataset.storeFocus = options.focusable === false ? "false" : "true";
    if (Number.isFinite(options.navRow)) button.dataset.storeNavRow = String(options.navRow);
    button.addEventListener("click", onClick);
    button.addEventListener("focus", () => {
      const index = getFocusables().indexOf(button);
      if (index >= 0 && (state.focusIndex !== index || !button.classList.contains("is-controller-focus"))) setFocus(index);
    });
    return button;
  }

  function ensureStyle() {
    if (document.getElementById(styleId)) return;
    const style = document.createElement("style");
    style.id = styleId;
    style.textContent = `
      .steamloader-store-root {
        --store-bg: #071017;
        --store-panel: rgba(15, 25, 34, .9);
        --store-panel-solid: #101b24;
        --store-text: #f1f5f7;
        --store-muted: #94a3ad;
        --store-dim: #60717d;
        --store-accent: #66c0f4;
        --store-accent-strong: #1a9fff;
        --store-blue: #66c0f4;
        --store-success: #5ee6a8;
        --store-success-strong: #13d889;
        --store-success-ink: #032016;
        position: fixed;
        inset: 0;
        z-index: 2147483635;
        display: none;
        color: var(--store-text);
        font-family: "Motiva Sans", "Segoe UI", sans-serif;
        background:
          radial-gradient(circle at 18% 12%, rgba(26, 159, 255, .18), transparent 34%),
          radial-gradient(circle at 85% 72%, rgba(25, 124, 198, .17), transparent 32%),
          linear-gradient(145deg, #081119 0%, #0a1117 52%, #07151a 100%);
      }
      .steamloader-store-root, .steamloader-store-root * { box-sizing: border-box; }
      .steamloader-store-root.is-open { display: block; }
      .steamloader-store-shell { height: 100%; display: grid; grid-template-rows: auto auto minmax(0, 1fr) auto; overflow: hidden; }
      .steamloader-store-header { display: flex; justify-content: space-between; align-items: center; gap: 28px; padding: 30px 48px 18px; }
      .steamloader-store-brand { min-width: 0; }
      .steamloader-store-kicker { color: var(--store-accent); font-size: 11px; font-weight: 900; letter-spacing: .2em; }
      .steamloader-store-title { margin: 2px 0 0; font-size: clamp(30px, 3.2vw, 52px); line-height: 1; letter-spacing: -.04em; }
      .steamloader-store-subtitle { margin: 8px 0 0; color: var(--store-muted); font-size: 14px; font-weight: 650; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .steamloader-store-header-actions { display: flex; align-items: center; gap: 10px; flex: none; }
      .steamloader-store-updated { margin-right: 4px; color: var(--store-dim); font-size: 12px; font-weight: 750; }
      .steamloader-store-button, .steamloader-store-mini-button, .steamloader-store-tab, .steamloader-store-card, .steamloader-store-currency-card, .steamloader-store-modal-close, .steamloader-store-region-trigger, .steamloader-store-region-option {
        border: 1px solid rgba(255,255,255,.09); color: var(--store-text); font: inherit; cursor: pointer; outline: none;
      }
      .steamloader-store-button { min-height: 46px; padding: 0 20px; border-radius: 14px; font-weight: 850; background: rgba(255,255,255,.07); }
      .steamloader-store-button.is-primary { border-color: rgba(102,192,244,.48); color: #061522; background: linear-gradient(135deg, #66c0f4, #1a9fff); box-shadow: 0 10px 34px rgba(26,159,255,.24); }
      .steamloader-store-button.is-soft { background: rgba(255,255,255,.06); }
      .steamloader-store-button.is-wishlist { border-color: rgba(102,192,244,.38); color: var(--store-accent); background: rgba(26,159,255,.12); }
      .steamloader-store-button.is-danger { color: #ffb7bd; background: rgba(255,91,106,.1); }
      .steamloader-store-button:disabled { opacity: .45; cursor: default; }
      .steamloader-store-button:hover, .steamloader-store-button:focus-visible, .steamloader-store-button.is-controller-focus,
      .steamloader-store-mini-button:hover, .steamloader-store-mini-button.is-controller-focus,
      .steamloader-store-card:hover, .steamloader-store-card:focus-visible, .steamloader-store-card.is-controller-focus,
      .steamloader-store-currency-card:hover, .steamloader-store-currency-card.is-controller-focus,
      .steamloader-store-region-trigger:hover, .steamloader-store-region-trigger.is-controller-focus,
      .steamloader-store-region-option:hover, .steamloader-store-region-option.is-controller-focus,
      .steamloader-store-modal-close:hover, .steamloader-store-modal-close.is-controller-focus {
        border-color: var(--store-accent); box-shadow: 0 0 0 3px rgba(102,192,244,.2), 0 18px 48px rgba(0,0,0,.38); transform: translateY(-2px);
      }
      .steamloader-store-card.is-controller-focus, .steamloader-store-currency-card.is-controller-focus, .steamloader-store-region-option.is-controller-focus { transform: none; }
      .steamloader-store-tabs { display: flex; align-items: center; justify-content: center; gap: 10px; min-height: 58px; padding: 0 48px 12px; border-bottom: 1px solid rgba(255,255,255,.06); }
      .steamloader-store-bumper { padding: 5px 9px; border-radius: 7px; color: #101820; background: rgba(255,255,255,.8); font-size: 10px; font-weight: 950; }
      .steamloader-store-tab { position: relative; min-width: 132px; padding: 13px 20px; border-color: transparent; border-radius: 13px; color: var(--store-muted); background: transparent; font-weight: 800; }
      .steamloader-store-tab.is-active { color: #fff; background: rgba(255,255,255,.065); }
      .steamloader-store-tab.is-active::after { content: ""; position: absolute; left: 30%; right: 30%; bottom: -7px; height: 4px; border-radius: 9px; background: var(--store-accent-strong); box-shadow: 0 0 18px rgba(26,159,255,.6); }
      .steamloader-store-main { overflow: auto; padding: 26px 48px 50px; scroll-padding: 28px 28px 96px; scrollbar-width: thin; scrollbar-color: rgba(102,192,244,.38) transparent; }
      .steamloader-store-main::-webkit-scrollbar { width: 8px; height: 8px; }
      .steamloader-store-main::-webkit-scrollbar-thumb { background: rgba(102,192,244,.34); border-radius: 999px; }
      .steamloader-store-notice, .steamloader-store-empty { padding: 24px; border: 1px solid rgba(255,255,255,.08); border-radius: 18px; color: var(--store-muted); background: rgba(255,255,255,.035); }
      .steamloader-store-notice.is-error { margin-bottom: 18px; color: #ffc0c5; border-color: rgba(255,91,106,.28); background: rgba(255,91,106,.08); }
      .steamloader-store-status-toast { position: fixed; left: 50%; bottom: 70px; z-index: 2147483646; max-width: min(720px, calc(100vw - 80px)); padding: 12px 18px; border: 1px solid rgba(102,192,244,.42); border-radius: 14px; color: var(--store-text); background: rgba(12,28,39,.96); box-shadow: 0 18px 55px rgba(0,0,0,.48); font-size: 12px; font-weight: 800; transform: translateX(-50%); pointer-events: none; animation: steamloader-store-toast-in .18s ease-out; }
      @keyframes steamloader-store-toast-in { from { opacity: 0; transform: translate(-50%, 8px); } to { opacity: 1; transform: translate(-50%, 0); } }
      .steamloader-store-empty.is-actionable { display: flex; align-items: center; flex-wrap: wrap; gap: 10px 16px; margin-top: 18px; }
      .steamloader-store-empty.is-actionable > strong { color: var(--store-text); font-size: 16px; }
      .steamloader-store-empty.is-actionable > span { flex: 1 1 360px; }
      .steamloader-store-empty.is-compact { padding: 18px; }
      .steamloader-store-hero { position: relative; isolation: isolate; min-height: min(36vh, 390px); overflow: hidden; border: 1px solid rgba(255,255,255,.1); border-radius: 30px; background: #111b22; box-shadow: 0 24px 80px rgba(0,0,0,.35); }
      .steamloader-store-hero::before { content: ""; position: absolute; inset: 0; z-index: -2; background-image: var(--store-hero-image); background-size: cover; background-position: center 30%; transform: scale(1.02); }
      .steamloader-store-hero::after { content: ""; position: absolute; inset: 0; z-index: -1; background: linear-gradient(90deg, rgba(5,12,17,.97) 0%, rgba(5,12,17,.78) 38%, rgba(5,12,17,.16) 75%), linear-gradient(0deg, rgba(5,12,17,.62), transparent 55%); }
      .steamloader-store-hero-content { width: min(620px, 62%); padding: 48px; }
      .steamloader-store-hero-label, .steamloader-store-best-label { color: var(--store-accent); font-size: 11px; font-weight: 900; letter-spacing: .17em; }
      .steamloader-store-hero-title { margin: 9px 0 8px; font-size: clamp(34px, 4vw, 66px); line-height: .98; letter-spacing: -.045em; text-shadow: 0 6px 24px rgba(0,0,0,.45); }
      .steamloader-store-hero-store { color: var(--store-muted); font-size: 15px; font-weight: 750; }
      .steamloader-store-hero-price { display: flex; align-items: baseline; gap: 12px; min-height: 44px; margin: 16px 0 20px; }
      .steamloader-store-hero-price strong { font-size: 30px; }
      .steamloader-store-hero-price span { color: var(--store-muted); font-size: 17px; }
      .steamloader-store-section { margin-top: 36px; }
      .steamloader-store-suggestion-controls { display: flex; align-items: center; justify-content: space-between; gap: 18px; margin-top: 34px; padding: 14px 16px 14px 20px; border: 1px solid rgba(102,192,244,.18); border-radius: 17px; color: var(--store-muted); background: rgba(26,159,255,.07); font-size: 12px; font-weight: 750; }
      .steamloader-store-suggestion-controls .steamloader-store-button { min-height: 38px; padding: 0 15px; font-size: 11px; }
      .steamloader-store-section-head { margin-bottom: 14px; }
      .steamloader-store-section-head h2, .steamloader-store-page-head h2 { margin: 0; font-size: 24px; letter-spacing: -.025em; }
      .steamloader-store-section-head p, .steamloader-store-page-head p { margin: 5px 0 0; color: var(--store-muted); font-size: 13px; font-weight: 650; }
      .steamloader-store-rail { display: grid; grid-auto-flow: column; grid-auto-columns: minmax(190px, 15.5vw); gap: 16px; overflow-x: auto; overflow-y: hidden; padding: 5px 5px 18px; margin: -5px; scroll-snap-type: x proximity; }
      .steamloader-store-rail.is-landscape { grid-auto-columns: minmax(280px, 23vw); }
      .steamloader-store-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 18px; margin-top: 24px; }
      .steamloader-store-card { position: relative; min-width: 0; padding: 0; overflow: hidden; border-radius: 20px; text-align: left; background: var(--store-panel-solid); box-shadow: 0 14px 38px rgba(0,0,0,.24); transition: transform 150ms ease, border-color 150ms ease, box-shadow 150ms ease; scroll-snap-align: start; }
      .steamloader-store-card-art { position: relative; aspect-ratio: 2 / 3; overflow: hidden; background: linear-gradient(135deg, #173140, #112029); }
      .steamloader-store-card.is-landscape .steamloader-store-card-art { aspect-ratio: 16 / 10; }
      .steamloader-store-artwork-frame { position: relative; width: 100%; height: 100%; overflow: hidden; }
      .steamloader-store-artwork-frame > img { opacity: 0; }
      .steamloader-store-artwork-frame > img.is-loaded { opacity: 1; }
      .steamloader-store-artwork-loader { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; gap: 7px; opacity: 0; background: linear-gradient(135deg, rgba(23,49,64,.96), rgba(10,24,33,.98)); animation: steamloader-store-artwork-loader-reveal 1ms linear 140ms forwards; }
      .steamloader-store-artwork-loader-dot { width: 7px; height: 7px; border-radius: 999px; background: var(--store-accent); box-shadow: 0 0 12px rgba(102,192,244,.38); animation: steamloader-store-artwork-dot 1.05s ease-in-out infinite; }
      .steamloader-store-artwork-loader-dot:nth-child(2) { animation-delay: 140ms; }
      .steamloader-store-artwork-loader-dot:nth-child(3) { animation-delay: 280ms; }
      @keyframes steamloader-store-artwork-dot {
        0%, 70%, 100% { opacity: .34; transform: translateY(0) scale(.82); }
        35% { opacity: 1; transform: translateY(-5px) scale(1); }
      }
      @keyframes steamloader-store-artwork-loader-reveal { to { opacity: 1; } }
      .steamloader-store-card-art img { width: 100%; height: 100%; object-fit: cover; display: block; transition: opacity 180ms ease, transform 250ms ease; }
      .steamloader-store-card:hover .steamloader-store-card-art img { transform: scale(1.035); }
      .steamloader-store-card-placeholder { width: 100%; height: 100%; display: grid; place-items: center; color: rgba(255,255,255,.5); font-size: 42px; font-weight: 950; }
      .steamloader-store-rating, .steamloader-store-heart { position: absolute; top: 10px; padding: 7px 10px; border-radius: 10px; font-weight: 900; backdrop-filter: blur(12px); }
      .steamloader-store-rating { left: 10px; color: #baf8d9; background: rgba(4,13,18,.82); font-size: 12px; }
      .steamloader-store-heart { right: 10px; color: var(--store-accent); background: rgba(4,13,18,.75); font-size: 18px; }
      .steamloader-store-card-info { position: relative; min-height: 116px; padding: 14px; }
      .steamloader-store-card-title { min-height: 38px; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden; font-size: 15px; font-weight: 900; line-height: 1.25; }
      .steamloader-store-card-source { margin-top: 4px; color: var(--store-blue); font-size: 9px; font-weight: 900; letter-spacing: .07em; text-transform: uppercase; }
      .steamloader-store-card-store { margin-top: 4px; padding-right: 50px; color: var(--store-muted); font-size: 11px; font-weight: 750; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .steamloader-store-card-price { display: flex; align-items: baseline; gap: 7px; margin-top: 8px; }
      .steamloader-store-card-price strong { font-size: 17px; }
      .steamloader-store-card-price span { color: var(--store-muted); font-size: 11px; }
      .steamloader-store-unreleased-badge { width: fit-content; padding: 6px 9px; border: 1px solid rgba(90,184,255,.32); border-radius: 8px; color: #9bd5ff !important; background: rgba(45,132,198,.13); font-size: 10px !important; font-weight: 950; letter-spacing: .08em; }
      .steamloader-store-discount { position: absolute; right: 12px; bottom: 13px; padding: 6px 8px; border-radius: 8px; color: var(--store-success-ink); background: var(--store-success); font-size: 12px; font-weight: 950; }
      .steamloader-store-page-head { max-width: 760px; margin: 2px 0 8px; }
      .steamloader-store-search { display: grid; grid-template-columns: minmax(0,1fr) auto; gap: 12px; margin-top: 24px; padding: 10px; border: 1px solid rgba(255,255,255,.09); border-radius: 20px; background: rgba(8,17,24,.76); }
      .steamloader-store-search-input { min-width: 0; min-height: 52px; padding: 0 18px; border: 1px solid transparent; border-radius: 14px; outline: none; color: var(--store-text); background: rgba(255,255,255,.045); font: inherit; font-size: 16px; font-weight: 750; }
      .steamloader-store-search-input::placeholder { color: var(--store-dim); }
      .steamloader-store-search-input:focus, .steamloader-store-search-input.is-controller-focus { border-color: var(--store-accent); box-shadow: 0 0 0 3px rgba(102,192,244,.18); }
      .steamloader-store-search-note { margin: 12px 4px 0; color: var(--store-muted); font-size: 11px; line-height: 1.5; }
      .steamloader-store-keyboard-backdrop { position: fixed; inset: 0; z-index: 9; display: flex; align-items: flex-end; justify-content: center; padding: 36px; background: rgba(0,5,9,.7); backdrop-filter: blur(12px); }
      .steamloader-store-search-keyboard { width: min(1260px, calc(100vw - 72px)); padding: 22px; border: 1px solid rgba(255,255,255,.13); border-radius: 28px; background: rgba(17,24,32,.985); box-shadow: 0 32px 110px rgba(0,0,0,.68); }
      .steamloader-store-search-keyboard-header { display: grid; grid-template-columns: auto minmax(280px,1fr); align-items: center; gap: 18px; margin-bottom: 16px; }
      .steamloader-store-search-keyboard-title { color: var(--store-accent); font-size: 17px; font-weight: 950; }
      .steamloader-store-search-keyboard-value { min-height: 48px; display: flex; align-items: center; padding: 0 16px; overflow: hidden; border: 1px solid rgba(255,255,255,.08); border-radius: 14px; color: var(--store-text); background: #081119; font-size: 17px; font-weight: 800; white-space: nowrap; text-overflow: ellipsis; }
      .steamloader-store-search-keyboard-grid { display: flex; flex-direction: column; gap: 9px; }
      .steamloader-store-search-keyboard-row { display: grid; grid-template-columns: repeat(12, minmax(0, 1fr)); gap: 9px; }
      .steamloader-store-search-key { grid-column: span var(--store-key-span, 1); width: 100%; min-width: 0; min-height: 54px; padding: 0 8px; border: 1px solid rgba(255,255,255,.08); border-radius: 14px; outline: none; color: var(--store-text); background: #343c45; font: inherit; font-size: 15px; font-weight: 950; }
      .steamloader-store-search-key.is-action { color: #baf8d9; background: #29343c; }
      .steamloader-store-search-key:hover, .steamloader-store-search-key:focus-visible, .steamloader-store-search-key.is-controller-focus { border-color: var(--store-accent); color: #061522; background: var(--store-accent); box-shadow: 0 0 0 3px rgba(102,192,244,.2), 0 14px 36px rgba(0,0,0,.38); transform: translateY(-2px); }
      .steamloader-store-search-keyboard-hint { margin-top: 14px; color: var(--store-muted); font-size: 11px; font-weight: 800; text-align: center; white-space: pre; }
      .steamloader-store-alert-list { display: grid; gap: 12px; margin-top: 24px; padding-bottom: 76px; }
      .steamloader-store-alert-card { display: grid; grid-template-columns: 78px minmax(190px,1fr) 145px 190px minmax(205px,auto) auto; align-items: center; gap: 18px; min-height: 126px; padding: 16px 18px; border: 1px solid rgba(255,255,255,.08); border-radius: 22px; background: rgba(255,255,255,.035); }
      .steamloader-store-alert-card.is-reached { border-color: rgba(94,230,168,.42); background: rgba(48,181,123,.1); }
      .steamloader-store-alert-art { width: 78px; height: 94px; overflow: hidden; border-radius: 14px; background: linear-gradient(135deg, #173140, #112029); box-shadow: 0 9px 22px rgba(0,0,0,.28); }
      .steamloader-store-alert-image { width: 100%; height: 100%; display: block; object-fit: cover; transition: opacity 180ms ease; }
      .steamloader-store-alert-image-placeholder { width: 100%; height: 100%; display: grid; place-items: center; color: rgba(255,255,255,.55); font-size: 18px; font-weight: 950; }
      .steamloader-store-alert-title { overflow: hidden; font-size: 16px; font-weight: 900; white-space: nowrap; text-overflow: ellipsis; }
      .steamloader-store-alert-copy { margin-top: 4px; color: var(--store-muted); font-size: 12px; }
      .steamloader-store-alert-label { color: var(--store-dim) !important; font-size: 9px !important; font-weight: 950; letter-spacing: .13em; }
      .steamloader-store-alert-prices { display: flex; flex-direction: column; align-items: flex-start; gap: 4px; }
      .steamloader-store-alert-prices strong { font-size: 21px; }
      .steamloader-store-alert-prices span { color: var(--store-muted); font-size: 11px; }
      .steamloader-store-alert-trend { min-width: 0; display: grid; gap: 5px; }
      .steamloader-store-alert-trend svg { width: 100%; height: 48px; overflow: visible; }
      .steamloader-store-alert-trend-area { fill: rgba(26,159,255,.13); }
      .steamloader-store-alert-trend-line { fill: none; stroke: var(--store-accent-strong); stroke-width: 2.3; stroke-linecap: round; stroke-linejoin: round; }
      .steamloader-store-alert-trend-dot { fill: var(--store-accent); filter: drop-shadow(0 0 5px rgba(102,192,244,.75)); }
      .steamloader-store-alert-trend-meta { display: flex; justify-content: space-between; gap: 8px; color: var(--store-dim); font-size: 9px; font-weight: 750; }
      .steamloader-store-alert-trend-empty { min-height: 48px; display: flex; align-items: center; color: var(--store-muted); font-size: 11px; }
      .steamloader-store-alert-target { display: grid; justify-items: center; gap: 7px; }
      .steamloader-store-alert-target-controls { display: flex; align-items: center; gap: 8px; }
      .steamloader-store-alert-target-controls .steamloader-store-mini-button { width: 38px; height: 38px; }
      .steamloader-store-alert-target-controls .steamloader-store-mini-button:disabled { opacity: .45; cursor: default; transform: none; }
      .steamloader-store-alert-target-value { min-width: 86px; padding: 9px 10px; border: 1px solid rgba(255,255,255,.09); border-radius: 12px; text-align: center; background: rgba(255,255,255,.045); font-size: 16px; }
      .steamloader-store-alert-state { color: var(--store-muted); font-size: 10px; font-weight: 800; }
      .steamloader-store-alert-state.is-reached { color: var(--store-success); }
      .steamloader-store-alert-actions { display: grid; gap: 7px; min-width: 116px; }
      .steamloader-store-alert-actions .steamloader-store-button { min-height: 42px; padding: 0 13px; }
      .steamloader-store-settings-panel { position: relative; margin-top: 26px; padding: 28px; border: 1px solid rgba(255,255,255,.08); border-radius: 24px; background: rgba(255,255,255,.035); }
      .steamloader-store-settings-panel h3 { margin: 0 0 16px; }
      .steamloader-store-region-setting { position: relative; display: grid; grid-template-columns: minmax(0,1fr) minmax(300px, .55fr); align-items: center; gap: 24px; margin-bottom: 28px; padding: 22px; border: 1px solid rgba(255,255,255,.08); border-radius: 20px; background: rgba(8,17,24,.62); }
      .steamloader-store-region-setting h3 { margin: 0 0 5px; }
      .steamloader-store-region-setting p { margin: 0; color: var(--store-muted); font-size: 12px; }
      .steamloader-store-region-trigger { min-height: 64px; padding: 0 20px; border-radius: 16px; text-align: left; background: rgba(255,255,255,.07); font-size: 16px; font-weight: 850; }
      .steamloader-store-region-trigger::after { content: "⌄"; float: right; color: var(--store-accent); }
      .steamloader-store-region-trigger.is-open::after { content: "⌃"; }
      .steamloader-store-region-menu { position: absolute; z-index: 12; top: calc(100% - 8px); right: 22px; width: min(420px, calc(100% - 44px)); max-height: min(62vh, 620px); overflow-y: auto; padding: 10px; border: 1px solid rgba(255,255,255,.12); border-radius: 22px; background: rgba(18,22,26,.98); box-shadow: 0 28px 90px rgba(0,0,0,.6); backdrop-filter: blur(24px); scroll-padding: 12px; scrollbar-width: thin; }
      .steamloader-store-region-option { width: 100%; min-height: 58px; display: grid; grid-template-columns: 6px minmax(0,1fr) auto; align-items: center; gap: 12px; padding: 8px 14px; border-color: transparent; border-radius: 14px; text-align: left; background: transparent; }
      .steamloader-store-region-option > span:last-child { color: var(--store-muted); }
      .steamloader-store-region-marker { width: 5px; height: 28px; border-radius: 999px; background: transparent; }
      .steamloader-store-region-option.is-active { background: rgba(255,255,255,.075); }
      .steamloader-store-region-option.is-active .steamloader-store-region-marker { background: var(--store-accent); box-shadow: 0 0 16px rgba(102,192,244,.55); }
      .steamloader-store-currency-grid { display: grid; grid-template-columns: repeat(4, minmax(0,1fr)); gap: 14px; }
      .steamloader-store-currency-card { position: relative; min-height: 180px; padding: 24px; border-radius: 20px; text-align: left; background: rgba(8,17,24,.75); }
      .steamloader-store-currency-card.is-active { border-color: var(--store-accent); background: rgba(26,159,255,.15); }
      .steamloader-store-currency-check { position: absolute; top: 16px; right: 18px; width: 28px; height: 28px; display: grid; place-items: center; border-radius: 50%; color: #061522; background: var(--store-accent); font-weight: 950; }
      .steamloader-store-currency-check:empty { background: rgba(255,255,255,.08); }
      .steamloader-store-currency-title { font-size: 17px; font-weight: 900; }
      .steamloader-store-currency-sample { margin-top: 18px; font-size: 24px; font-weight: 950; }
      .steamloader-store-currency-copy { margin-top: 8px; color: var(--store-muted); font-size: 12px; line-height: 1.45; }
      .steamloader-store-data-note { display: flex; flex-direction: column; gap: 7px; margin-top: 20px; padding: 18px; border-radius: 16px; color: var(--store-muted); background: rgba(0,0,0,.18); font-size: 12px; line-height: 1.45; }
      .steamloader-store-data-note strong { color: var(--store-accent); font-size: 13px; }
      .steamloader-store-modal-backdrop { position: fixed; inset: 0; z-index: 5; display: grid; place-items: center; padding: 42px; background: rgba(0,5,9,.7); backdrop-filter: blur(12px); }
      .steamloader-store-modal { width: min(1500px, 94vw); height: min(850px, 90vh); display: grid; grid-template-rows: minmax(220px, 34%) minmax(0, 1fr); overflow: hidden; border: 1px solid rgba(255,255,255,.13); border-radius: 30px; background: #111820; box-shadow: 0 35px 120px rgba(0,0,0,.65); }
      .steamloader-store-modal-banner { position: relative; isolation: isolate; display: flex; align-items: flex-end; padding: 38px 44px; overflow: hidden; }
      .steamloader-store-modal-banner::before { content: ""; position: absolute; inset: 0; z-index: -2; background-image: var(--store-detail-image); background-size: cover; background-position: center 25%; }
      .steamloader-store-modal-banner::after { content: ""; position: absolute; inset: 0; z-index: -1; background: linear-gradient(0deg, #111820 0%, rgba(17,24,32,.15) 75%), linear-gradient(90deg, rgba(9,13,18,.72), transparent 70%); }
      .steamloader-store-modal-title { margin: 0; font-size: clamp(34px, 4.2vw, 70px); line-height: .96; letter-spacing: -.045em; text-shadow: 0 6px 25px #000; }
      .steamloader-store-modal-close { position: absolute; top: 22px; right: 24px; width: 54px; height: 54px; border-radius: 17px; color: #fff; background: rgba(5,8,12,.62); font-size: 30px; }
      .steamloader-store-modal-body { overflow: auto; padding: 0 32px 32px; }
      .steamloader-store-modal-body { scrollbar-width: thin; scrollbar-color: rgba(102,192,244,.4) transparent; }
      .steamloader-store-modal-body::-webkit-scrollbar { width: 8px; }
      .steamloader-store-modal-body::-webkit-scrollbar-track { background: transparent; }
      .steamloader-store-modal-body::-webkit-scrollbar-thumb { border-radius: 999px; background: rgba(102,192,244,.36); }
      .steamloader-store-summary { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 20px 22px; border: 1px solid rgba(102,192,244,.3); border-radius: 20px; background: linear-gradient(100deg, rgba(26,159,255,.14), rgba(255,255,255,.03)); }
      .steamloader-store-summary-price { display: flex; align-items: baseline; gap: 10px; margin-top: 6px; }
      .steamloader-store-summary-price strong { font-size: 27px; }
      .steamloader-store-summary-price span { color: var(--store-muted); }
      .steamloader-store-summary-actions { display: flex; align-items: center; justify-content: flex-end; flex-wrap: wrap; gap: 10px; }
      .steamloader-store-steam-wishlist-badge { padding: 8px 10px; border-radius: 10px; color: var(--store-accent); background: rgba(102,192,244,.1); font-size: 11px; font-weight: 850; }
      .steamloader-store-button.is-buy { min-width: 150px; }
      .steamloader-store-alert-editor { display: flex; align-items: center; justify-content: space-between; gap: 18px; margin-top: 12px; padding: 16px 20px; border: 1px solid rgba(255,255,255,.08); border-radius: 18px; background: rgba(255,255,255,.03); }
      .steamloader-store-alert-editor-title { font-weight: 900; }
      .steamloader-store-alert-editor-copy { margin-top: 3px; color: var(--store-muted); font-size: 11px; }
      .steamloader-store-alert-controls { display: flex; align-items: center; gap: 8px; }
      .steamloader-store-mini-button { width: 42px; height: 42px; border-radius: 12px; background: rgba(255,255,255,.07); font-size: 20px; font-weight: 900; }
      .steamloader-store-mini-button.is-wide { width: 62px; font-size: 12px; }
      .steamloader-store-alert-value { min-width: 90px; text-align: center; font-size: 17px; font-weight: 900; }
      .steamloader-store-offers { margin-top: 18px; }
      .steamloader-store-offers-head { display: flex; justify-content: space-between; align-items: baseline; padding: 0 4px 10px; }
      .steamloader-store-offers-head h3 { margin: 0; }
      .steamloader-store-offers-head span { color: var(--store-muted); font-size: 12px; }
      .steamloader-store-offer { display: grid; grid-template-columns: minmax(0,1fr) auto 100px; align-items: center; gap: 18px; min-height: 72px; margin-top: 9px; padding: 10px 12px 10px 20px; border: 1px solid rgba(255,255,255,.07); border-radius: 16px; background: rgba(255,255,255,.025); }
      .steamloader-store-offer.is-best { border-color: rgba(94,230,168,.36); }
      .steamloader-store-offer-store { display: flex; align-items: center; gap: 10px; }
      .steamloader-store-offer-store span { padding: 5px 7px; border-radius: 6px; color: var(--store-success-ink); background: var(--store-success); font-size: 9px; font-weight: 950; }
      .steamloader-store-offer-price { display: flex; align-items: center; gap: 9px; }
      .steamloader-store-offer-price strong { font-size: 18px; }
      .steamloader-store-offer-price span { color: var(--store-muted); font-size: 11px; }
      .steamloader-store-offer-price em { padding: 5px 7px; border-radius: 7px; color: var(--store-success); background: rgba(94,230,168,.1); font-size: 11px; font-style: normal; font-weight: 900; }
      .steamloader-store-retailer-shortcuts { display: grid; gap: 9px; margin-top: 16px; padding: 16px 18px; border: 1px solid rgba(255,255,255,.07); border-radius: 16px; background: rgba(255,255,255,.025); }
      .steamloader-store-retailer-shortcuts > span { color: var(--store-muted); font-size: 11px; }
      .steamloader-store-retailer-actions { display: flex; flex-wrap: wrap; gap: 8px; }
      .steamloader-store-retailer-actions .steamloader-store-button { min-height: 38px; padding: 0 14px; font-size: 11px; }
      .steamloader-store-activity-banner { display: grid; grid-template-columns: auto minmax(0,1fr) auto; align-items: center; gap: 14px; margin-top: 20px; padding: 14px 16px 14px 20px; border: 1px solid rgba(102,192,244,.3); border-radius: 17px; background: rgba(26,159,255,.1); }
      .steamloader-store-activity-banner > span { color: var(--store-muted); font-size: 12px; }
      .steamloader-store-undo-banner { display: grid; grid-template-columns: auto minmax(0,1fr) auto auto; align-items: center; gap: 12px; margin-top: 18px; padding: 13px 14px 13px 18px; border: 1px solid rgba(102,192,244,.34); border-radius: 17px; background: rgba(17,48,70,.92); box-shadow: 0 14px 34px rgba(0,0,0,.25); }
      .steamloader-store-undo-banner > span { color: var(--store-muted); font-size: 12px; }
      .steamloader-store-wishlist-toolbar { display: grid; grid-template-columns: minmax(170px,.45fr) minmax(0,1fr) auto; align-items: center; gap: 12px; margin-top: 16px; padding: 12px; border: 1px solid rgba(255,255,255,.08); border-radius: 19px; background: rgba(8,17,24,.72); }
      .steamloader-store-filter-search, .steamloader-store-filter-chip, .steamloader-store-setting-row { min-height: 40px; padding: 0 14px; border: 1px solid rgba(255,255,255,.09); border-radius: 12px; outline: none; color: var(--store-text); background: rgba(255,255,255,.055); font: inherit; font-size: 11px; font-weight: 850; white-space: nowrap; }
      .steamloader-store-filter-search { overflow: hidden; text-align: left; text-overflow: ellipsis; }
      .steamloader-store-filter-search.is-active, .steamloader-store-filter-chip.is-active { border-color: var(--store-accent); color: #061522; background: var(--store-accent); }
      .steamloader-store-filter-chip.is-tag { color: #c5eaff; background: rgba(102,192,244,.1); }
      .steamloader-store-filter-search:hover, .steamloader-store-filter-search.is-controller-focus, .steamloader-store-filter-chip:hover, .steamloader-store-filter-chip.is-controller-focus, .steamloader-store-setting-row:hover, .steamloader-store-setting-row.is-controller-focus { border-color: var(--store-accent); box-shadow: 0 0 0 3px rgba(102,192,244,.18); }
      .steamloader-store-filter-rail { display: flex; gap: 7px; overflow-x: auto; padding: 3px; scrollbar-width: none; }
      .steamloader-store-filter-rail::-webkit-scrollbar { display: none; }
      .steamloader-store-wishlist-toolbar-actions, .steamloader-store-setting-inline-actions, .steamloader-store-alert-presets, .steamloader-store-game-tools-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; }
      .steamloader-store-manage-bar { position: sticky; top: -18px; z-index: 4; display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-top: 12px; padding: 11px 14px; border: 1px solid rgba(102,192,244,.35); border-radius: 16px; background: rgba(12,28,39,.96); box-shadow: 0 14px 36px rgba(0,0,0,.34); backdrop-filter: blur(18px); }
      .steamloader-store-manage-bar > strong { margin-right: auto; }
      .steamloader-store-card.is-selected { border-color: var(--store-accent); box-shadow: 0 0 0 4px rgba(102,192,244,.2), 0 18px 48px rgba(0,0,0,.4); }
      .steamloader-store-select-check, .steamloader-store-change-badge, .steamloader-store-pin-badge { position: absolute; z-index: 2; left: 10px; bottom: 10px; padding: 6px 8px; border-radius: 8px; color: #061522; background: var(--store-accent); font-size: 9px; font-weight: 950; letter-spacing: .06em; }
      .steamloader-store-select-check { bottom: auto; top: 10px; width: 34px; height: 34px; display: grid; place-items: center; padding: 0; border: 2px solid rgba(255,255,255,.55); border-radius: 50%; color: transparent; background: rgba(5,12,17,.75); }
      .steamloader-store-select-check.is-selected { color: #061522; background: var(--store-accent); }
      .steamloader-store-pin-badge { color: #c5eaff; background: rgba(5,12,17,.82); }
      .steamloader-store-game-tools, .steamloader-store-game-tracking { display: grid; grid-template-columns: minmax(0,1fr) auto; align-items: center; gap: 18px; margin-top: 12px; padding: 16px 20px; border: 1px solid rgba(255,255,255,.08); border-radius: 18px; background: rgba(255,255,255,.03); }
      .steamloader-store-game-tools-copy { display: grid; gap: 4px; }
      .steamloader-store-game-tools-copy > span, .steamloader-store-setting-copy { color: var(--store-muted); font-size: 11px; line-height: 1.45; }
      .steamloader-store-game-tracking { grid-template-columns: minmax(280px,.8fr) minmax(280px,1fr); }
      .steamloader-store-game-tracking-stats { display: grid; grid-template-columns: repeat(3,minmax(0,1fr)); gap: 8px; }
      .steamloader-store-game-tracking-stat { display: grid; gap: 5px; padding: 12px; border-radius: 13px; background: rgba(0,0,0,.18); }
      .steamloader-store-game-tracking-stat > span { color: var(--store-dim); font-size: 9px; font-weight: 950; letter-spacing: .1em; }
      .steamloader-store-game-tracking-stat > strong { font-size: 16px; }
      .steamloader-store-alert-editor { flex-wrap: wrap; }
      .steamloader-store-alert-presets { flex-basis: 100%; padding-top: 8px; border-top: 1px solid rgba(255,255,255,.06); }
      .steamloader-store-kind-badge { color: #ffd8a8 !important; background: rgba(255,153,61,.13) !important; }
      .steamloader-store-kind-badge.is-official { color: #bfe8ff !important; background: rgba(102,192,244,.12) !important; }
      .steamloader-store-offer-store small { color: var(--store-dim); font-size: 9px; }
      .steamloader-store-qol-settings { margin-top: 22px; padding: 20px; border: 1px solid rgba(255,255,255,.08); border-radius: 20px; background: rgba(8,17,24,.62); }
      .steamloader-store-qol-settings h3 { margin-bottom: 8px; }
      .steamloader-store-setting-rows { display: grid; gap: 10px; }
      .steamloader-store-setting-row { width: 100%; min-height: 68px; display: grid; grid-template-columns: minmax(180px,.4fr) minmax(0,1fr) auto; align-items: center; gap: 14px; text-align: left; white-space: normal; }
      .steamloader-store-setting-row > span { color: var(--store-muted); font-weight: 650; }
      .steamloader-store-setting-row > em { color: var(--store-accent); font-style: normal; }
      .steamloader-store-setting-row.is-static { padding: 12px 14px; }
      .steamloader-store-offer-skeleton, .steamloader-store-card-skeleton, .steamloader-store-hero-skeleton { overflow: hidden; background: linear-gradient(100deg, rgba(255,255,255,.035) 25%, rgba(255,255,255,.09) 45%, rgba(255,255,255,.035) 65%); background-size: 300% 100%; animation: steamloader-store-shimmer 1.4s infinite; }
      .steamloader-store-offer-skeleton { height: 72px; margin-top: 9px; border-radius: 16px; }
      .steamloader-store-hero-skeleton { height: min(36vh, 390px); border-radius: 30px; }
      .steamloader-store-skeleton-rail { display: grid; grid-template-columns: repeat(7, 1fr); gap: 16px; margin-top: 36px; }
      .steamloader-store-card-skeleton { aspect-ratio: 2/3; border-radius: 20px; }
      .steamloader-store-footer { min-height: 54px; display: flex; align-items: center; gap: 25px; padding: 0 48px; border-top: 1px solid rgba(255,255,255,.06); color: var(--store-muted); background: rgba(5,11,16,.88); font-size: 12px; font-weight: 750; }
      .steamloader-store-footer-source { margin-left: auto; color: var(--store-dim); }
      @keyframes steamloader-store-shimmer { to { background-position: -200% 0; } }
      @media (max-width: 980px) {
        .steamloader-store-header { padding: 22px 24px 14px; }
        .steamloader-store-subtitle, .steamloader-store-updated { display: none; }
        .steamloader-store-tabs { padding-left: 24px; padding-right: 24px; overflow-x: auto; justify-content: flex-start; }
        .steamloader-store-main { padding-left: 24px; padding-right: 24px; }
        .steamloader-store-rail { grid-auto-columns: 210px; }
        .steamloader-store-hero-content { width: 78%; padding: 32px; }
        .steamloader-store-currency-grid { grid-template-columns: 1fr; }
        .steamloader-store-region-setting { grid-template-columns: 1fr; }
        .steamloader-store-region-menu { left: 18px; right: 18px; width: auto; }
        .steamloader-store-alert-editor { align-items: flex-start; flex-direction: column; }
        .steamloader-store-alert-controls { flex-wrap: wrap; }
        .steamloader-store-alert-card { grid-template-columns: 66px minmax(0,1fr) auto; }
        .steamloader-store-alert-art { width: 66px; height: 82px; }
        .steamloader-store-alert-trend { grid-column: 2 / 4; }
        .steamloader-store-alert-target { grid-column: 2 / 3; justify-items: start; }
        .steamloader-store-summary { align-items: flex-start; flex-direction: column; }
        .steamloader-store-summary-actions { justify-content: flex-start; }
        .steamloader-store-search { grid-template-columns: 1fr; }
        .steamloader-store-wishlist-toolbar { grid-template-columns: 1fr; }
        .steamloader-store-activity-banner { grid-template-columns: 1fr; }
        .steamloader-store-undo-banner { grid-template-columns: 1fr 1fr; }
        .steamloader-store-undo-banner > span { grid-column: 1 / -1; }
        .steamloader-store-game-tools, .steamloader-store-game-tracking { grid-template-columns: 1fr; }
        .steamloader-store-setting-row { grid-template-columns: 1fr; }
        .steamloader-store-keyboard-backdrop { padding: 18px; }
        .steamloader-store-search-keyboard { width: calc(100vw - 36px); padding: 16px; }
        .steamloader-store-search-keyboard-header { grid-template-columns: 1fr; }
        .steamloader-store-search-keyboard-row { gap: 6px; }
        .steamloader-store-search-key { min-height: 48px; padding: 0 4px; font-size: 13px; }
        .steamloader-store-modal-backdrop { padding: 18px; }
        .steamloader-store-modal { width: 96vw; height: 94vh; }
      }
      @media (prefers-reduced-motion: reduce) { .steamloader-store-root * { scroll-behavior: auto !important; animation: none !important; transition: none !important; } }
    `;
    document.head?.append(style);
  }

  install();
})();
