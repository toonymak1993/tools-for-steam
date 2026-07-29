// Tools for Steam - OmniLibrary dynamic store tabs
// Store games remain managed Steam shortcuts so they keep native artwork,
// ALL GAMES integration, controller navigation, and Steam game-detail pages.
// This layer separates those shortcuts into enabled store tabs and removes them from
// NON-STEAM without creating a persistent Steam collection.
(() => {
  const stateVersion = "omnilibrary-store-tabs-v45";
  const xboxTabId = "tfs-xbox";
  const xboxCloudTabId = "tfs-xbox-cloud";
  const epicTabId = "tfs-epic";
  const gogTabId = "tfs-gog";
  const tabTopology = window.__steamLoaderOmniLibraryTabTopology;
  const defaultStoreDefinitions = [
    {
      id: "xbox-game-pass",
      sourceStoreId: "xbox-game-pass",
      tabId: xboxTabId,
      title: "Xbox",
      mode: "xbox",
      appFilter: "non-cloud",
    },
    {
      id: "xbox-cloud",
      sourceStoreId: "xbox-game-pass",
      tabId: xboxCloudTabId,
      title: "Xbox Cloud",
      mode: "xbox-cloud",
      appFilter: "cloud",
      requiresXboxCloud: true,
    },
    {
      id: "epic-games",
      sourceStoreId: "epic-games",
      tabId: epicTabId,
      title: "Epic",
      mode: "epic",
    },
    {
      id: "gog-galaxy",
      sourceStoreId: "gog-galaxy",
      tabId: gogTabId,
      title: "GOG",
      mode: "gog",
      appFilter: "all",
    },
  ];
  let storeDefinitions = defaultStoreDefinitions.map((definition) => ({
    ...definition,
  }));
  const xboxTileStyleId = "steamtools-xbox-library-tile-style";
  const patchIntervalMs = 60000;
  const libraryRefreshIntervalMs = 60000;
  const activeDownloadRefreshIntervalMs = 2000;
  const idleDownloadRefreshIntervalMs = 10000;
  const bumperRebindIntervalMs = 1000;
  const omniLibraryStoreStorageKey = "ToolsForSteamOmniLibraryStoresChanged";
  const omniLibraryStoreChannelName = "ToolsForSteamOmniLibraryStores";
  const omniLibraryUninstallNoticeId =
    "steamtools-omnilibrary-uninstall-notice";
  const activeStoreTabSessionKey = "ToolsForSteamOmniLibraryActiveStoreTab";
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";

  async function fetchOmniLibraryState(path, timeoutMs = 10000) {
    if (typeof window.AbortController !== "function") {
      return fetch(`${apiBase}${path}`, { cache: "no-store" });
    }

    const controller = new window.AbortController();
    const timeout = window.setTimeout(() => controller.abort(), timeoutMs);
    try {
      return await fetch(`${apiBase}${path}`, {
        cache: "no-store",
        signal: controller.signal,
      });
    } finally {
      window.clearTimeout(timeout);
    }
  }

  function getOmniLibraryStateStore() {
    const existing = window.__steamLoaderOmniLibraryStateStore;
    if (existing?.version === 4) {
      return existing;
    }

    const listeners = new Set();
    const shared = {
      version: 4,
      snapshot: null,
      request: null,
      followUpRequest: null,
      lastFetchAt: 0,
      async refresh(force = false) {
        if (shared.request) {
          if (!force) {
            return shared.request;
          }
          if (!shared.followUpRequest) {
            const followUp = shared.request
              .catch(() => null)
              .then(() => shared.refresh(true))
              .finally(() => {
                if (shared.followUpRequest === followUp) {
                  shared.followUpRequest = null;
                }
              });
            shared.followUpRequest = followUp;
          }
          return shared.followUpRequest;
        }
        if (
          !force &&
          shared.snapshot &&
          Date.now() - shared.lastFetchAt < 15000
        ) {
          return shared.snapshot;
        }

        shared.request = (async () => {
          try {
            const response = await fetchOmniLibraryState(
              "api/unifystore/summary",
            );
            if (!response.ok) {
              throw new Error(`OmniLibrary summary failed (${response.status}).`);
            }

            const snapshot = await response.json();
            const changed =
              Number(snapshot?.revision || 0) !==
                Number(shared.snapshot?.revision || 0) ||
              snapshot?.pluginEnabled !== shared.snapshot?.pluginEnabled;
            shared.snapshot = snapshot;
            shared.lastFetchAt = Date.now();
            if (changed) {
              for (const listener of listeners) {
                try {
                  listener(snapshot);
                } catch (_) {}
              }
            }
            return snapshot;
          } catch (error) {
            // Fail closed when the local TFS service disappears. Keeping the
            // previous snapshot would leave stale tabs, labels, and context
            // actions active even though their backend can no longer respond.
            const unavailableSnapshot = {
              revision: Number(shared.snapshot?.revision || 0) + 1,
              pluginEnabled: false,
              stores: [],
              backendUnavailable: true,
            };
            shared.snapshot = unavailableSnapshot;
            shared.lastFetchAt = 0;
            for (const listener of listeners) {
              try {
                listener(unavailableSnapshot);
              } catch (_) {}
            }
            throw error;
          }
        })();

        try {
          return await shared.request;
        } finally {
          shared.request = null;
        }
      },
      subscribe(listener) {
        listeners.add(listener);
        if (shared.snapshot) {
          listener(shared.snapshot);
        }
        return () => listeners.delete(listener);
      },
    };
    window.__steamLoaderOmniLibraryStateStore = shared;
    return shared;
  }

  const omniLibraryStateStore = getOmniLibraryStateStore();

  function readPersistedVirtualTabId() {
    try {
      // sessionStorage belongs to one Steam browsing context. Using it here
      // allowed SharedJSContext and the visible Big Picture renderer to restore
      // different tabs. localStorage is the single cross-context authority.
      return String(window.localStorage?.getItem(activeStoreTabSessionKey) || "");
    } catch (_) {
      return "";
    }
  }

  function setVirtualActiveTabId(tabId, persist = true) {
    const normalizedTabId = String(tabId || "");
    state.virtualActiveTabId = normalizedTabId;
    if (!normalizedTabId) {
      state.nativeRouteEchoTabId = "";
    }
    if (!persist) {
      return;
    }

    try {
      if (normalizedTabId) {
        window.localStorage?.setItem(activeStoreTabSessionKey, normalizedTabId);
      } else {
        window.localStorage?.removeItem(activeStoreTabSessionKey);
      }
    } catch (_) {}
  }

  const previousState = window.__steamLoaderLibraryTabsState;
  if (previousState?.version !== stateVersion) {
    if (previousState?.patchTimer) {
      window.clearInterval(previousState.patchTimer);
    }

    if (previousState?.patchSoonTimer) {
      window.clearTimeout(previousState.patchSoonTimer);
    }

    if (previousState?.navigationMigrationTimer) {
      window.clearTimeout(previousState.navigationMigrationTimer);
    }

    if (previousState?.libraryRefreshTimer) {
      window.clearInterval(previousState.libraryRefreshTimer);
    }

    if (previousState?.downloadRefreshTimer) {
      window.clearTimeout(previousState.downloadRefreshTimer);
    }

    if (previousState?.bumperRebindTimer) {
      window.clearInterval(previousState.bumperRebindTimer);
    }

    if (previousState?.tilePatchTimer) {
      window.clearTimeout(previousState.tilePatchTimer);
    }

    if (previousState?.uninstallNoticeTimer) {
      window.clearTimeout(previousState.uninstallNoticeTimer);
    }
    document.getElementById(omniLibraryUninstallNoticeId)?.remove();

    try {
      previousState?.tileObserver?.disconnect?.();
    } catch (_) {}

    if (typeof previousState?.tileActivationHandler === "function") {
      document.removeEventListener("click", previousState.tileActivationHandler, true);
      document.removeEventListener("focusin", previousState.tileActivationHandler, true);
    }

    if (typeof previousState?.libraryBumperKeyHandler === "function") {
      document.removeEventListener(
        "keydown",
        previousState.libraryBumperKeyHandler,
        true,
      );
    }

    if (typeof previousState?.libraryBumperEventHandler === "function") {
      document.removeEventListener(
        "vgp_onbuttondown",
        previousState.libraryBumperEventHandler,
        true,
      );
    }

    if (typeof previousState?.libraryTabFocusHandler === "function") {
      document.removeEventListener(
        "focusin",
        previousState.libraryTabFocusHandler,
        true,
      );
    }

    if (previousState?.libraryDpadActivationTimer) {
      window.clearTimeout(previousState.libraryDpadActivationTimer);
    }

    const focusNav = window.FocusNavController;
    const currentCatchAll = focusNav?.m_fnCatchAllGamepadInput;
    if (
      previousState?.catchAllInstalled &&
      focusNav?.SetCatchAllGamepadInput &&
      (
        currentCatchAll === previousState.catchAllGamepadInput ||
        currentCatchAll?.__steamLoaderXboxLibraryBumpers ===
          previousState.version
      )
    ) {
      focusNav.SetCatchAllGamepadInput(
        currentCatchAll?.__steamLoaderXboxPreviousCatchAll ||
          previousState.previousCatchAllGamepadInput ||
          undefined,
      );
    }

    if (typeof previousState?.storageHandler === "function") {
      window.removeEventListener("storage", previousState.storageHandler);
    }

    if (typeof previousState?.visibilityHandler === "function") {
      document.removeEventListener("visibilitychange", previousState.visibilityHandler);
    }

    try {
      previousState?.omniLibraryStateUnsubscribe?.();
    } catch (_) {}

    try {
      previousState?.channel?.close?.();
    } catch (_) {}
  }

  const state =
    previousState?.version === stateVersion
      ? previousState
      : (window.__steamLoaderLibraryTabsState = {
          version: stateVersion,
          patchTimer: 0,
          patchSoonTimer: 0,
          navigationMigrationTimer: 0,
          navigationMigrationRequested: true,
          lastStatus: "initializing OmniLibrary store tabs",
          lastError: "",
          lastPatchedAt: 0,
          mutationCount: 0,
          wrappedCount: 0,
          virtualActiveTabId:
            previousState?.virtualActiveTabId || readPersistedVirtualTabId(),
          navigationIntentTabId: "",
          navigationIntentAt: 0,
          navigationCursorTabId: "",
          navigationCursorAt: 0,
          nativeRouteEchoTabId: "",
          navigationHandlers: new WeakMap(),
          virtualCollections: new WeakMap(),
          derivedTopologyCache: null,
          activationResolved: false,
          pluginEnabled: false,
          xboxEnabled: previousState?.xboxEnabled === true,
          xboxAppIds: new Set(previousState?.xboxAppIds || []),
          xboxInstalledAppIds: new Set(previousState?.xboxInstalledAppIds || []),
          xboxAppIdsSignature: "",
          storeStates: new Map(),
          managedAppIds: new Set(),
          managedInstalledAppIds: new Set(),
          managedCloudAppIds: new Set(),
          managedActiveDownloadAppIds: new Set(),
          libraryRefreshTimer: 0,
          downloadRefreshTimer: 0,
          downloadRequestInFlight: false,
          downloadAppIdsSignature: "",
          pendingLifecycleAppIds: new Set(),
          pendingLifecycleSignature: "",
          confirmedInstalledHints: new Map(),
          bumperRebindTimer: 0,
          libraryRequestInFlight: false,
          libraryForceRefreshPending: false,
          forceRenderRequested: false,
          routeRefreshRequested: false,
          tileObserver: null,
          tilePatchTimer: 0,
          lastTilePatchAt: 0,
          tileBadgeCount: 0,
          tileActivationHandler: null,
          lastFocusedXboxAppId: Number(previousState?.lastFocusedXboxAppId || 0),
          lastFocusedXboxAppAt: Number(previousState?.lastFocusedXboxAppAt || 0),
          lastActivatedManagedAppId: 0,
          lastActivatedManagedAppAt: 0,
          navigationRuntime: null,
          catchAllInstalled: false,
          catchAllController: null,
          catchAllGamepadInput: null,
          catchAllMissingSince: 0,
          previousCatchAllGamepadInput: null,
          libraryBumperKeyHandler: null,
          libraryBumperEventHandler: null,
          libraryTabFocusHandler: null,
          libraryDpadActivationTimer: 0,
          lastBumperDirection: 0,
          lastBumperInputAt: 0,
          channel: null,
          storageHandler: null,
          visibilityHandler: null,
          omniLibraryStateUnsubscribe: null,
          refreshStoreState: null,
          uninstallNoticeTimer: 0,
          showUninstallNotice: null,
        });

  window.__steamLoaderLibraryTabsInstalled = true;

  function setStatus(status, error = "") {
    state.lastStatus = status;
    state.lastError = error;
  }

  function removeOmniLibraryUninstallNotice() {
    if (state.uninstallNoticeTimer) {
      window.clearTimeout(state.uninstallNoticeTimer);
      state.uninstallNoticeTimer = 0;
    }
    document.getElementById(omniLibraryUninstallNoticeId)?.remove();
  }

  function showOmniLibraryUninstallNotice(payload = {}) {
    if (!state.pluginEnabled) {
      removeOmniLibraryUninstallNotice();
      return;
    }

    removeOmniLibraryUninstallNotice();

    const storeId = String(payload.storeId || "");
    const errorMessage = String(payload.errorMessage || "").trim();
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
    document.body.appendChild(notice);
    notice.animate?.(
      [
        { opacity: 0, transform: "translateY(14px) scale(.985)" },
        { opacity: 1, transform: "translateY(0) scale(1)" },
      ],
      { duration: 180, easing: "cubic-bezier(.2,.8,.2,1)" },
    );
    state.uninstallNoticeTimer = window.setTimeout(
      removeOmniLibraryUninstallNotice,
      failed ? 7000 : 6000,
    );
  }

  state.showUninstallNotice = showOmniLibraryUninstallNotice;

  function getEnabledStoreDefinitions() {
    if (!state.pluginEnabled) {
      return [];
    }
    return storeDefinitions.filter((definition) =>
      state.storeStates.get(definition.id)?.ready === true);
  }

  function synchronizeStoreDefinitions(stores) {
    if (!tabTopology?.buildDefinitionsFromSummary) {
      return false;
    }
    const nextDefinitions = tabTopology.buildDefinitionsFromSummary(
      stores,
      defaultStoreDefinitions,
    );
    const signature = (definitions) => definitions
      .map((definition) => [
        definition.id,
        definition.sourceStoreId,
        definition.tabId,
        definition.title,
        definition.appFilter,
        definition.requiresXboxCloud === true,
      ].join(":"))
      .join("|");
    if (signature(nextDefinitions) === signature(storeDefinitions)) {
      return false;
    }
    storeDefinitions = nextDefinitions;
    state.derivedTopologyCache = null;
    return true;
  }

  function isManagedStoreTabId(tabId) {
    return getEnabledStoreDefinitions().some(
      (definition) => definition.tabId === String(tabId || ""));
  }

  function getNativeRouteTabId(tabId) {
    const normalizedTabId = String(tabId || "");
    const enabledDefinitions = getEnabledStoreDefinitions();
    const storeIndex = enabledDefinitions.findIndex(
      (definition) => definition.tabId === normalizedTabId,
    );
    if (storeIndex < 0) {
      return normalizedTabId;
    }

    // Steam's router only understands native tab ids. Adjacent virtual stores
    // therefore need different native backing routes; otherwise Xbox -> Epic
    // is treated as selecting the same route and Steam drops the transition.
    const backingRoutes = ["Installed", "AllGames", "DesktopApps"];
    return backingRoutes[storeIndex % backingRoutes.length];
  }

  function getStoreDefinitionForAppId(appId) {
    return getEnabledStoreDefinitions().find((definition) =>
      state.storeStates.get(definition.id)?.appIds?.has(Number(appId))) || null;
  }

  async function refreshXboxAppIds(force = false) {
    if (state.libraryRequestInFlight) {
      state.libraryForceRefreshPending =
        state.libraryForceRefreshPending || force;
      return;
    }

    state.libraryRequestInFlight = true;
    try {
      const snapshot = await omniLibraryStateStore.refresh(force);
      const wasRuntimeActive =
        state.pluginEnabled && getEnabledStoreDefinitions().length > 0;
      const activationWasResolved = state.activationResolved === true;
      const previousEnabledSignature = getEnabledStoreDefinitions()
        .map((definition) => definition.id)
        .join("|");
      state.pluginEnabled = snapshot?.pluginEnabled === true;
      state.activationResolved = true;
      const stores = snapshot?.stores || [];
      const definitionsChanged = synchronizeStoreDefinitions(stores);
      const now = Date.now();
      for (const [appId, hint] of state.confirmedInstalledHints) {
        if (Number(hint?.expiresAt || 0) <= now) {
          state.confirmedInstalledHints.delete(appId);
        }
      }
      const nextStoreStates = new Map();
      const allManagedAppIds = new Set();
      const allInstalledAppIds = new Set();
      const allCloudAppIds = new Set();
      const allActiveDownloadAppIds = new Set();
      const seenSourceStoreIds = new Set();
      const signatureParts = [];
      for (const definition of storeDefinitions) {
        const sourceStoreId = definition.sourceStoreId || definition.id;
        const store = stores.find(
          (candidate) => String(candidate?.id || "").toLowerCase() === sourceStoreId,
        );
        const sourceAppIds = (store?.appIds || [])
          .map((appId) => Number(appId))
          .filter((appId) => Number.isInteger(appId) && appId > 0)
          .sort((left, right) => left - right);
        const sourceInstalledAppIdSet = new Set((store?.installedAppIds || [])
          .map((appId) => Number(appId))
          .filter((appId) => Number.isInteger(appId) && appId > 0)
          .sort((left, right) => left - right));
        for (const [appId, hint] of state.confirmedInstalledHints) {
          if (
            String(hint?.storeId || "") === sourceStoreId &&
            sourceAppIds.includes(appId)
          ) {
            sourceInstalledAppIdSet.add(appId);
          }
        }
        const sourceInstalledAppIds = Array
          .from(sourceInstalledAppIdSet)
          .sort((left, right) => left - right);
        const cloudAppIds = (store?.cloudAppIds || [])
          .map((appId) => Number(appId))
          .filter((appId) => Number.isInteger(appId) && appId > 0)
          .sort((left, right) => left - right);
        const activeDownloadAppIds = (store?.activeDownloadAppIds || [])
          .map((appId) => Number(appId))
          .filter((appId) => Number.isInteger(appId) && appId > 0)
          .sort((left, right) => left - right);
        const cloudAppIdSet = new Set(cloudAppIds);
        const appIds = definition.appFilter === "cloud"
          ? cloudAppIds
          : definition.appFilter === "non-cloud"
            ? sourceAppIds.filter((appId) => !cloudAppIdSet.has(appId))
            : sourceAppIds;
        const appIdSet = new Set(appIds);
        const installedAppIds = sourceInstalledAppIds.filter((appId) =>
          appIdSet.has(appId));
        const cloudSourceEnabled =
          definition.requiresXboxCloud !== true ||
          store?.xboxCloudGamingEnabled === true;
        const ready =
          store?.enabled === true &&
          store?.readyForLibraryTab === true &&
          cloudSourceEnabled &&
          appIds.length > 0;
        if (!seenSourceStoreIds.has(sourceStoreId)) {
          seenSourceStoreIds.add(sourceStoreId);
          sourceAppIds.forEach((appId) => allManagedAppIds.add(appId));
          sourceInstalledAppIds.forEach((appId) => allInstalledAppIds.add(appId));
          cloudAppIds.forEach((appId) => allCloudAppIds.add(appId));
          activeDownloadAppIds.forEach((appId) =>
            allActiveDownloadAppIds.add(appId));
        }
        nextStoreStates.set(definition.id, {
          enabled: store?.enabled === true,
          ready,
          sourceStoreId,
          appIds: new Set(appIds),
          installedAppIds: new Set(installedAppIds),
          cloudAppIds: new Set(cloudAppIds),
        });
        signatureParts.push(
          `${definition.id}:${ready}:${store?.xboxCloudGamingEnabled === true}:${appIds.join(",")}:${installedAppIds.join(",")}:${cloudAppIds.join(",")}:${activeDownloadAppIds.join(",")}`,
        );
      }
      const signature = signatureParts.join("|");
      if (signature !== state.xboxAppIdsSignature) {
        state.storeStates = nextStoreStates;
        state.managedAppIds = allManagedAppIds;
        state.managedInstalledAppIds = allInstalledAppIds;
        state.managedCloudAppIds = allCloudAppIds;
        state.managedActiveDownloadAppIds = allActiveDownloadAppIds;
        state.downloadAppIdsSignature = Array
          .from(allActiveDownloadAppIds)
          .sort((left, right) => left - right)
          .join(",");
        const xboxState = nextStoreStates.get("xbox-game-pass");
        state.xboxEnabled = xboxState?.ready === true;
        state.xboxAppIds = xboxState?.appIds || new Set();
        state.xboxInstalledAppIds = xboxState?.installedAppIds || new Set();
        state.xboxAppIdsSignature = signature;
        if (state.virtualActiveTabId && !isManagedStoreTabId(state.virtualActiveTabId)) {
          setVirtualActiveTabId("", false);
        }
        const persistedVirtualTabId = readPersistedVirtualTabId();
        if (!state.virtualActiveTabId && isManagedStoreTabId(persistedVirtualTabId)) {
          setVirtualActiveTabId(persistedVirtualTabId, false);
          state.routeRefreshRequested = true;
        }
        const enabledSignature = getEnabledStoreDefinitions()
          .map((definition) => definition.id)
          .join("|");
        const topologyChanged =
          definitionsChanged || previousEnabledSignature !== enabledSignature;
        state.navigationMigrationRequested =
          state.navigationMigrationRequested || topologyChanged;
        state.forceRenderRequested = state.forceRenderRequested || topologyChanged;
        state.routeRefreshRequested = state.routeRefreshRequested || topologyChanged;
        if (state.pluginEnabled && getEnabledStoreDefinitions().length > 0) {
          patchXboxTabBasis();
          scheduleXboxTileBadges();
        }
      }
      const runtimeActive =
        state.pluginEnabled && getEnabledStoreDefinitions().length > 0;
      if (!runtimeActive) {
        setVirtualActiveTabId("", true);
        removeOmniLibraryUninstallNotice();
        state.pendingLifecycleAppIds = new Set();
        state.pendingLifecycleSignature = "";
        state.confirmedInstalledHints = new Map();
        state.navigationIntentTabId = "";
        state.navigationIntentAt = 0;
        state.navigationCursorTabId = "";
        state.navigationCursorAt = 0;
        state.navigationMigrationRequested = false;
        state.navigationRuntime = null;
        state.lastActivatedManagedAppId = 0;
        state.lastActivatedManagedAppAt = 0;
        uninstallLibraryBumperInput();
        disableXboxTileObserver();
        disableActiveRuntimeTimers();
        if (
          wasRuntimeActive ||
          !activationWasResolved ||
          libraryTabLayoutNeedsPatch()
        ) {
          state.forceRenderRequested = true;
          state.routeRefreshRequested = true;
          scheduleXboxTabPatch();
        }
      } else {
        if (recoverXboxTabStateFromDom()) {
          state.routeRefreshRequested = true;
        }
        ensureActiveRuntimeTimers();
        ensureXboxTileObserver();
        installLibraryBumperInput();
        scheduleDownloadStateRefresh(0);
      }
    } catch (error) {
      state.lastError = String(error?.message || error);
      const wasRuntimeActive =
        state.pluginEnabled && getEnabledStoreDefinitions().length > 0;
      state.pluginEnabled = false;
      state.activationResolved = true;
      state.storeStates = new Map();
      state.managedAppIds = new Set();
      state.managedInstalledAppIds = new Set();
      state.managedCloudAppIds = new Set();
      state.managedActiveDownloadAppIds = new Set();
      state.downloadAppIdsSignature = "";
      state.pendingLifecycleAppIds = new Set();
      state.pendingLifecycleSignature = "";
      state.confirmedInstalledHints = new Map();
      state.xboxEnabled = false;
      state.xboxAppIds = new Set();
      state.xboxInstalledAppIds = new Set();
      state.xboxAppIdsSignature = "";
      setVirtualActiveTabId("", true);
      state.navigationRuntime = null;
      uninstallLibraryBumperInput();
      disableXboxTileObserver();
      disableActiveRuntimeTimers();
      removeOmniLibraryUninstallNotice();
      if (wasRuntimeActive || libraryTabLayoutNeedsPatch()) {
        state.forceRenderRequested = true;
        state.routeRefreshRequested = true;
        scheduleXboxTabPatch();
      }
    } finally {
      state.libraryRequestInFlight = false;
      if (state.libraryForceRefreshPending) {
        state.libraryForceRefreshPending = false;
        window.setTimeout(() => {
          void refreshXboxAppIds(true);
        }, 0);
      }
    }
  }

  function getReactPropertyKey(element, prefix) {
    return element
      ? Object.getOwnPropertyNames(element).find((name) => name.startsWith(prefix))
      : null;
  }

  function getReactFiber(element) {
    const key =
      getReactPropertyKey(element, "__reactFiber") ||
      getReactPropertyKey(element, "__reactContainer");
    return key ? element[key] : null;
  }

  function getRootFiber() {
    for (const root of [
      document.getElementById("GamepadUI_Full_Root"),
      document.getElementById("root"),
      document.getElementById("Main"),
      document.body,
    ]) {
      const fiber = getReactFiber(root);
      if (fiber) {
        return fiber;
      }
    }

    for (const element of document.querySelectorAll("div, main, section")) {
      const fiber = getReactFiber(element);
      if (fiber) {
        return fiber;
      }
    }

    return null;
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

  function ensureXboxTileStyle() {
    let style = document.getElementById(xboxTileStyleId);
    if (!style) {
      style = document.createElement("style");
      style.id = xboxTileStyleId;
      (document.head || document.documentElement).append(style);
    }

    const css = `
      [data-steamtools-xbox-status] {
        position: relative !important;
      }

      .steamtools-xbox-tile-badge {
        position: absolute;
        top: 0.46rem;
        right: 0.46rem;
        z-index: 100;
        display: grid;
        width: 1.46rem;
        height: 1.46rem;
        box-sizing: border-box;
        padding: 0.25rem;
        place-items: center;
        border: 1px solid rgba(255, 255, 255, 0.12);
        border-radius: 50%;
        background: rgba(34, 91, 58, 0.72);
        box-shadow: 0 0.12rem 0.3rem rgba(0, 0, 0, 0.34);
        color: rgba(255, 255, 255, 0.88);
        pointer-events: none;
      }

      .steamtools-xbox-tile-badge > svg {
        display: block;
        width: 100%;
        height: 100%;
      }

      [data-steamtools-xbox-status="available"] > .steamtools-xbox-tile-badge {
        background: rgba(31, 74, 105, 0.72);
      }

      [data-steamtools-xbox-status="cloud"] > .steamtools-xbox-tile-badge {
        background: rgba(53, 65, 111, 0.74);
      }

      [data-steamtools-xbox-status="downloading"] > .steamtools-xbox-tile-badge {
        background: rgba(34, 94, 143, 0.86);
        border-color: rgba(112, 193, 255, 0.58);
        box-shadow:
          0 0.12rem 0.3rem rgba(0, 0, 0, 0.34),
          0 0 0.58rem rgba(74, 166, 232, 0.32);
        animation: steamtools-omni-tile-download-pulse 1150ms ease-in-out infinite;
      }

      .steamtools-omni-tile-download-spinner {
        transform-box: fill-box;
        transform-origin: center;
        animation: steamtools-omni-tile-download-spin 850ms linear infinite;
      }

      [data-steamtools-xbox-status="available"] img {
        filter: brightness(0.88) saturate(0.9);
        transition: filter 140ms ease;
      }

      [data-steamtools-xbox-status="available"]:hover img,
      [data-steamtools-xbox-status="available"]:focus img,
      [data-steamtools-xbox-status="available"].gpfocus img,
      [data-steamtools-xbox-status="available"].gpfocuswithin img {
        filter: brightness(1) saturate(1);
      }

      @keyframes steamtools-omni-tile-download-spin {
        to { transform: rotate(360deg); }
      }

      @keyframes steamtools-omni-tile-download-pulse {
        0%, 100% { opacity: 0.76; }
        50% { opacity: 1; }
      }

      @media (prefers-reduced-motion: reduce) {
        [data-steamtools-xbox-status="downloading"] > .steamtools-xbox-tile-badge,
        .steamtools-omni-tile-download-spinner {
          animation: none !important;
        }
      }
    `;
    if (style.textContent !== css) {
      style.textContent = css;
    }
  }

  function clearXboxTileBadge(element) {
    if (!element?.dataset) {
      return;
    }

    delete element.dataset.steamtoolsXboxStatus;
    delete element.dataset.steamtoolsXboxLabel;
    delete element.dataset.steamtoolsXboxAppId;
    for (const badge of element.querySelectorAll?.(".steamtools-xbox-tile-badge") || []) {
      badge.remove();
    }
  }

  function createXboxTileStatusIcon(status) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("fill", "none");
    svg.setAttribute("aria-hidden", "true");

    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    if (status === "installed") {
      path.setAttribute("d", "M7 5.2v13.6L18 12 7 5.2Z");
      path.setAttribute("fill", "currentColor");
    } else if (status === "cloud") {
      path.setAttribute(
        "d",
        "M6.5 18h10.8a4.2 4.2 0 0 0 .7-8.34A6.3 6.3 0 0 0 6.2 8.8 4.6 4.6 0 0 0 6.5 18Z",
      );
      path.setAttribute("stroke", "currentColor");
      path.setAttribute("stroke-width", "2");
      path.setAttribute("stroke-linecap", "round");
      path.setAttribute("stroke-linejoin", "round");
    } else if (status === "downloading") {
      const spinner = document.createElementNS(
        "http://www.w3.org/2000/svg",
        "circle",
      );
      spinner.classList.add("steamtools-omni-tile-download-spinner");
      spinner.setAttribute("cx", "12");
      spinner.setAttribute("cy", "12");
      spinner.setAttribute("r", "9");
      spinner.setAttribute("stroke", "currentColor");
      spinner.setAttribute("stroke-width", "1.8");
      spinner.setAttribute("stroke-linecap", "round");
      spinner.setAttribute("stroke-dasharray", "13 44");
      svg.append(spinner);
      path.setAttribute("d", "M12 6v9m0 0 3-3m-3 3-3-3");
      path.setAttribute("stroke", "currentColor");
      path.setAttribute("stroke-width", "2");
      path.setAttribute("stroke-linecap", "round");
      path.setAttribute("stroke-linejoin", "round");
    } else {
      path.setAttribute("d", "M12 4v11m0 0 4-4m-4 4-4-4M5 20h14");
      path.setAttribute("stroke", "currentColor");
      path.setAttribute("stroke-width", "2");
      path.setAttribute("stroke-linecap", "round");
      path.setAttribute("stroke-linejoin", "round");
    }
    svg.append(path);
    return svg;
  }

  function ensureXboxTileBadge(
    element,
    installed,
    cloudPlayable,
    downloading,
  ) {
    let badge = Array.from(element.children || []).find((child) =>
      child.classList?.contains("steamtools-xbox-tile-badge"));
    if (!badge) {
      badge = document.createElement("div");
      badge.className = "steamtools-xbox-tile-badge";
      badge.setAttribute("aria-hidden", "true");
      element.append(badge);
    }

    const status = downloading
      ? "downloading"
      : installed
        ? "installed"
        : cloudPlayable
          ? "cloud"
          : "download";
    if (badge.dataset.steamtoolsIcon !== status) {
      badge.dataset.steamtoolsIcon = status;
      badge.replaceChildren(createXboxTileStatusIcon(status));
    }
  }

  function parseXboxAppIdFromTile(element) {
    for (const candidate of element?.querySelectorAll?.(
      "[data-appid], [data-app-id], [data-steam-appid]",
    ) || []) {
      for (const name of ["data-appid", "data-app-id", "data-steam-appid"]) {
        const appId = Number(candidate.getAttribute(name));
        if (Number.isInteger(appId) && appId > 0 && state.managedAppIds.has(appId)) {
          return appId;
        }
      }
    }

    for (const image of element?.querySelectorAll?.("img[src]") || []) {
      const source = String(image.currentSrc || image.src || "");
      const match = source.match(/\/customimages\/(\d+)(?:p|_hero|-icon)?(?:\.[a-z0-9]+)?/i);
      const appId = Number(match?.[1]);
      if (Number.isInteger(appId) && appId > 0 && state.managedAppIds.has(appId)) {
        return appId;
      }
    }

    return 0;
  }

  function getXboxTileMetadata(element) {
    let appId = 0;
    let collectionId = "";
    let fiber = getReactFiber(element);

    for (let depth = 0; fiber && depth < 16; depth += 1, fiber = fiber.return) {
      for (const props of [fiber.memoizedProps, fiber.pendingProps]) {
        if (!props || typeof props !== "object") {
          continue;
        }

        if (!appId) {
          const candidate = Number(
            props?.app?.appid ?? props?.app?.appId ?? props?.appid ?? props?.appId ?? 0,
          );
          if (Number.isInteger(candidate) && candidate > 0) {
            appId = candidate;
          }
        }

        if (!collectionId) {
          collectionId = String(
            props?.strCollectionId ?? props?.collection?.id ?? props?.collectionId ?? "",
          );
        }
      }

      if (appId && collectionId) {
        break;
      }
    }

    if (!appId) {
      appId = parseXboxAppIdFromTile(element);
    }

    return { appId, collectionId };
  }

  function refreshXboxTileBadges() {
    if (!state.pluginEnabled) {
      for (const tile of document.querySelectorAll("[data-steamtools-xbox-status]")) {
        clearXboxTileBadge(tile);
      }
      state.tileBadgeCount = 0;
      document.getElementById(xboxTileStyleId)?.remove();
      return;
    }

    ensureXboxTileStyle();
    const activeBadges = new Set();

    for (const tile of document.querySelectorAll('[role="gridcell"] [role="link"]')) {
      const { appId, collectionId } = getXboxTileMetadata(tile);
      const definition = getStoreDefinitionForAppId(appId);
      const belongsToStoreTab = definition?.tabId === collectionId;
      const isStoreFallback =
        !collectionId &&
        definition &&
        state.virtualActiveTabId === definition.tabId;

      if (!definition || (!belongsToStoreTab && !isStoreFallback)) {
        clearXboxTileBadge(tile);
        continue;
      }

      const storeState = state.storeStates.get(definition.id);
      const installed = storeState?.installedAppIds?.has(appId) === true;
      const cloudPlayable = storeState?.cloudAppIds?.has(appId) === true;
      const downloading =
        state.managedActiveDownloadAppIds.has(appId);
      tile.dataset.steamtoolsXboxAppId = String(appId);
      tile.dataset.steamtoolsXboxStatus = downloading
        ? "downloading"
        : installed
          ? "installed"
          : cloudPlayable
            ? "cloud"
            : "available";
      ensureXboxTileBadge(
        tile,
        installed,
        cloudPlayable,
        downloading,
      );
      if (
        tile.matches(":focus") ||
        tile.classList.contains("gpfocus") ||
        tile.classList.contains("gpfocuswithin")
      ) {
        state.lastFocusedXboxAppId = appId;
        state.lastFocusedXboxAppAt = Date.now();
        state.lastFocusedManagedAppId = appId;
        state.lastFocusedManagedAppAt = Date.now();
      }
      activeBadges.add(tile);
    }

    for (const tile of document.querySelectorAll("[data-steamtools-xbox-status]")) {
      if (!activeBadges.has(tile)) {
        clearXboxTileBadge(tile);
      }
    }

    state.tileBadgeCount = activeBadges.size;
  }

  function scheduleXboxTileBadges() {
    if (!state.pluginEnabled) {
      refreshXboxTileBadges();
      return;
    }
    if (state.tilePatchTimer) {
      return;
    }

    const delay = Math.max(
      40,
      250 - (Date.now() - Number(state.lastTilePatchAt || 0)),
    );
    state.tilePatchTimer = window.setTimeout(() => {
      state.tilePatchTimer = 0;
      state.lastTilePatchAt = Date.now();
      refreshXboxTileBadges();
    }, delay);
  }

  function ensureXboxTileObserver() {
    if (!state.pluginEnabled || getEnabledStoreDefinitions().length === 0) {
      return;
    }
    ensureXboxTileStyle();
    if (state.tileObserver || !document.documentElement) {
      return;
    }

    state.tileObserver = new MutationObserver((mutations) => {
      const touchesLibraryGrid = mutations.some((mutation) => {
        if (mutation.target?.closest?.('[role="gridcell"], [role="tablist"]')) {
          return true;
        }

        return [...mutation.addedNodes, ...mutation.removedNodes].some((node) =>
          node instanceof Element &&
          (node.matches?.('[role="gridcell"], [role="tablist"], [role="tab"]') ||
           node.querySelector?.('[role="gridcell"], [role="tablist"], [role="tab"]')));
      });
      if (touchesLibraryGrid) {
        scheduleXboxTileBadges();
      }
    });
    state.tileObserver.observe(document.documentElement, {
      childList: true,
      subtree: true,
    });

    state.tileActivationHandler = (event) => {
      const tab = event.target?.closest?.('[role="tab"]');
      const visibleLibraryTabs = tab ? getVisibleLibraryTabs() : [];
      const isLibraryTab = tab && visibleLibraryTabs.includes(tab);
      if (event.type === "click" && isLibraryTab) {
        commitLibraryNavigationTarget(getVisibleLibraryTabId(tab));
      }
      if (isLibraryTab) {
        scheduleXboxTabPatch();
      }
      const tile = event.target?.closest?.('[role="gridcell"] [role="link"]');
      if (!tile) {
        return;
      }

      const { appId } = getXboxTileMetadata(tile);
      state.lastFocusedXboxAppId = state.managedAppIds.has(appId) ? appId : 0;
      state.lastFocusedXboxAppAt = Date.now();
      state.lastFocusedManagedAppId = state.lastFocusedXboxAppId;
      state.lastFocusedManagedAppAt = state.lastFocusedXboxAppAt;
      if (event.type === "click") {
        state.lastActivatedManagedAppId = state.lastFocusedXboxAppId;
        state.lastActivatedManagedAppAt = state.lastFocusedXboxAppAt;
      }
    };
    document.addEventListener("click", state.tileActivationHandler, true);
    document.addEventListener("focusin", state.tileActivationHandler, true);
  }

  function disableXboxTileObserver() {
    if (state.tilePatchTimer) {
      window.clearTimeout(state.tilePatchTimer);
      state.tilePatchTimer = 0;
    }
    try {
      state.tileObserver?.disconnect?.();
    } catch (_) {}
    state.tileObserver = null;
    if (typeof state.tileActivationHandler === "function") {
      document.removeEventListener("click", state.tileActivationHandler, true);
      document.removeEventListener("focusin", state.tileActivationHandler, true);
    }
    state.tileActivationHandler = null;
    refreshXboxTileBadges();
  }

  function getTabArrays(node) {
    const arrays = [];
    for (const tabs of [
      node?.memoizedProps?.tabs,
      node?.pendingProps?.tabs,
      node?.alternate?.memoizedProps?.tabs,
      node?.alternate?.pendingProps?.tabs,
    ]) {
      if (Array.isArray(tabs) && !arrays.includes(tabs)) {
        arrays.push(tabs);
      }
    }

    return arrays;
  }

  function isLibraryTabsArray(tabs) {
    if (!Array.isArray(tabs) || tabs.length < 2 || tabs.length > 40) {
      return false;
    }

    const ids = new Set(tabs.map((tab) => String(tab?.id || "")));
    return ids.has("AllGames") && ids.has("Installed") && ids.has("DesktopApps");
  }

  function isInjectedXboxTab(tab) {
    const id = String(tab?.id || "");
    return storeDefinitions.some((definition) =>
      id === definition.tabId || id === definition.title);
  }

  function getContentCollection(element, visited = new Set()) {
    if (!element || typeof element !== "object" || visited.has(element)) {
      return null;
    }

    visited.add(element);
    if (element.props?.collection) {
      return element.props.collection;
    }

    const children = element.props?.children;
    if (Array.isArray(children)) {
      for (const child of children) {
        const collection = getContentCollection(child, visited);
        if (collection) {
          return collection;
        }
      }
      return null;
    }

    return getContentCollection(children, visited);
  }

  function cloneContentElement(element, sourceCollection, collection, key, isRoot = true) {
    if (!element || typeof element !== "object") {
      return element;
    }

    if (Array.isArray(element)) {
      return element.map((child) =>
        cloneContentElement(child, sourceCollection, collection, key, false));
    }

    if (!element.props) {
      return element;
    }

    const props = { ...element.props };
    if (props.collection === sourceCollection) {
      props.collection = collection;
    }
    if (props.children && typeof props.children === "object") {
      props.children = cloneContentElement(
        props.children,
        sourceCollection,
        collection,
        key,
        false,
      );
    }

    return {
      ...element,
      key: isRoot ? key : element.key,
      props,
    };
  }

  function getCollectionAppIds(collection) {
    try {
      if (collection?.apps && typeof collection.apps[Symbol.iterator] === "function") {
        return Array.from(collection.apps)
          .map(Number)
          .filter((appId) => Number.isInteger(appId) && appId > 0);
      }

      return Array.from(collection?.allApps || [])
        .map((app) => Number(app?.appid))
        .filter((appId) => Number.isInteger(appId) && appId > 0);
    } catch (_) {
      return [];
    }
  }

  function getVirtualCollection(sourceCollection, mode) {
    if (!sourceCollection || typeof sourceCollection !== "object") {
      return sourceCollection;
    }

    let cached = state.virtualCollections.get(sourceCollection);
    if (!cached) {
      cached = {};
      state.virtualCollections.set(sourceCollection, cached);
    }

    const sourceIds = getCollectionAppIds(sourceCollection);
    let topology = state.derivedTopologyCache;
    if (topology?.signature !== state.xboxAppIdsSignature) {
      const enabledDefinitions = getEnabledStoreDefinitions();
      const visibleManagedAppIds = enabledDefinitions.flatMap((definition) =>
        Array.from(state.storeStates.get(definition.id)?.appIds || []));
      const visibleInstalledAppIds = enabledDefinitions.flatMap((definition) =>
        Array.from(state.storeStates.get(definition.id)?.installedAppIds || []));
      const visibleManagedAppIdSet = new Set(visibleManagedAppIds);
      topology = {
        signature: state.xboxAppIdsSignature,
        visibleManagedAppIds,
        visibleInstalledAppIds,
        hiddenManagedAppIds: new Set(
          Array.from(state.managedAppIds)
            .filter((appId) => !visibleManagedAppIdSet.has(appId)),
        ),
      };
      state.derivedTopologyCache = topology;
    }
    const storeDefinition = storeDefinitions.find((definition) => definition.mode === mode);
    const requestedIds = storeDefinition
      ? Array.from(state.storeStates.get(storeDefinition.id)?.appIds || [])
      : mode === "allGames"
        ? sourceIds
            .filter((appId) => !state.managedAppIds.has(appId))
            .concat(topology.visibleManagedAppIds)
        : mode === "installed"
          ? sourceIds
              .filter((appId) => !state.managedAppIds.has(appId))
              .concat(topology.visibleInstalledAppIds)
          : sourceIds.filter((appId) => !topology.hiddenManagedAppIds.has(appId));
    const appIds = Array.from(new Set(requestedIds));
    appIds.sort((left, right) => left - right);
    const signature = appIds.join(",");
    let entry = cached[mode];

    if (!entry) {
      try {
        const Collection = sourceCollection.constructor;
        const collection = new Collection(
          storeDefinition?.tabId || `tfs-${mode}`,
          storeDefinition?.title || sourceCollection.displayName || mode,
        );
        entry = { collection, signature: "", lastSetAt: 0 };
      } catch (_) {
        entry = { collection: sourceCollection, signature: "", lastSetAt: 0 };
      }
      cached[mode] = entry;
    }

    const currentSignature = getCollectionAppIds(entry.collection)
      .sort((left, right) => left - right)
      .join(",");
    const shouldRetryHydration =
      currentSignature !== signature &&
      Date.now() - Number(entry.lastSetAt || 0) >= 2500;
    if (
      (entry.signature !== signature || shouldRetryHydration) &&
      typeof entry.collection?.SetApps === "function"
    ) {
      entry.collection.SetApps(appIds);
      entry.collection.ClearAppCounts?.();
      entry.signature = signature;
      entry.lastSetAt = Date.now();
    }

    return entry.collection;
  }

  function buildCountAddon(templateTab, collection) {
    const nativeAddon = templateTab?.renderTabAddon;
    return () => {
      const count = (() => {
        try {
          return collection?.visibleApps?.length ?? collection?.allApps?.length ?? 0;
        } catch (_) {
          return 0;
        }
      })();
      if (typeof nativeAddon !== "function") {
        return count;
      }

      const element = nativeAddon();
      return element && typeof element === "object"
        ? {
            ...element,
            props: {
              ...(element.props || {}),
              collection,
              count,
            },
          }
        : element;
    };
  }

  function buildFilteredDesktopTab(templateTab, collection) {
    return {
      ...templateTab,
      content: cloneContentElement(
        templateTab.content,
        getContentCollection(templateTab.content),
        collection,
        "tfs-filtered-non-steam-content",
      ),
      renderTabAddon: buildCountAddon(templateTab, collection),
      __steamLoaderNativeDesktopTab: templateTab,
    };
  }

  function buildXboxTab(templateTab, collection, definition) {
    return {
      ...templateTab,
      id: definition.tabId,
      key: definition.tabId,
      title: definition.title,
      content: cloneContentElement(
        templateTab.content,
        getContentCollection(templateTab.content),
        collection,
        `${definition.tabId}-native-content`,
      ),
      renderTabAddon: buildCountAddon(templateTab, collection),
      __steamLoaderOmniLibraryStoreFilter: definition.id,
    };
  }

  function buildExpandedSystemTab(templateTab, collection, key, marker) {
    return {
      ...templateTab,
      content: cloneContentElement(
        templateTab.content,
        getContentCollection(templateTab.content),
        collection,
        key,
      ),
      renderTabAddon: buildCountAddon(templateTab, collection),
      [marker]: templateTab,
    };
  }

  function mergeXboxTab(tabs) {
    if (!isLibraryTabsArray(tabs)) {
      return tabs;
    }

    const baseTabs = tabs.filter((tab) => !isInjectedXboxTab(tab));
    const enabledDefinitions = getEnabledStoreDefinitions();
    if (!state.pluginEnabled || !enabledDefinitions.length) {
      return baseTabs.map((tab) =>
        tab?.__steamLoaderNativeDesktopTab ||
        tab?.__steamLoaderNativeAllGamesTab ||
        tab?.__steamLoaderNativeInstalledTab ||
        tab);
    }
    const currentDesktopTab = baseTabs.find((tab) => tab?.id === "DesktopApps" && tab?.content);
    const nativeDesktopTab = currentDesktopTab?.__steamLoaderNativeDesktopTab || currentDesktopTab;
    const currentAllGamesTab = baseTabs.find((tab) => tab?.id === "AllGames" && tab?.content);
    const nativeAllGamesTab = currentAllGamesTab?.__steamLoaderNativeAllGamesTab || currentAllGamesTab;
    const currentInstalledTab = baseTabs.find((tab) => tab?.id === "Installed" && tab?.content);
    const nativeInstalledTab = currentInstalledTab?.__steamLoaderNativeInstalledTab || currentInstalledTab;
    const nativeCollection = nativeDesktopTab?.content?.props?.collection;
    const nativeAllGamesCollection = getContentCollection(nativeAllGamesTab?.content);
    const nativeInstalledCollection = getContentCollection(nativeInstalledTab?.content);
    if (
      !nativeDesktopTab ||
      !nativeCollection ||
      !nativeAllGamesTab ||
      !nativeAllGamesCollection ||
      !nativeInstalledTab ||
      !nativeInstalledCollection
    ) {
      setStatus("waiting for Steam's Non-Steam collection");
      return baseTabs;
    }

    const filteredDesktopCollection = getVirtualCollection(nativeCollection, "nonSteam");
    const storeCollections = new Map(storeDefinitions.map((definition) => [
      definition.id,
      getVirtualCollection(nativeCollection, definition.mode),
    ]));
    const allGamesCollection = getVirtualCollection(nativeAllGamesCollection, "allGames");
    const installedCollection = getVirtualCollection(nativeInstalledCollection, "installed");
    const normalizedTabs = baseTabs.map((tab) =>
      tab?.id === "DesktopApps"
        ? buildFilteredDesktopTab(nativeDesktopTab, filteredDesktopCollection)
        : tab?.id === "AllGames"
          ? buildExpandedSystemTab(
              nativeAllGamesTab,
              allGamesCollection,
              "tfs-all-games-content",
              "__steamLoaderNativeAllGamesTab",
            )
          : tab?.id === "Installed"
            ? buildExpandedSystemTab(
                nativeInstalledTab,
                installedCollection,
                "tfs-installed-content",
                "__steamLoaderNativeInstalledTab",
              )
        : tab,
    );
    const storeTabs = enabledDefinitions.map((definition) =>
      buildXboxTab(nativeDesktopTab, storeCollections.get(definition.id), definition));
    const desktopIndex = normalizedTabs.findIndex((tab) => tab?.id === "DesktopApps");
    const soundtrackIndex = normalizedTabs.findIndex((tab) => tab?.id === "Soundtracks");
    const insertIndex = desktopIndex >= 0
      ? desktopIndex + 1
      : soundtrackIndex >= 0
        ? soundtrackIndex
        : normalizedTabs.length;

    return [
      ...normalizedTabs.slice(0, insertIndex),
      ...storeTabs,
      ...normalizedTabs.slice(insertIndex),
    ];
  }

  function tabsSignature(tabs) {
    return tabs.map((tab) => `${tab?.id || ""}:${tab?.title || ""}`).join("|");
  }

  function resolveBumperButton(input) {
    let current = input;
    const visited = new Set();
    for (
      let depth = 0;
      current &&
      typeof current === "object" &&
      depth < 5 &&
      !visited.has(current);
      depth += 1
    ) {
      visited.add(current);
      current =
        current.detail?.button ??
        current.detail?.gamepadButton ??
        current.button ??
        current.gamepadButton ??
        current.detail ??
        current.code ??
        current.key ??
        current.name ??
        current.value;
    }
    return current;
  }

  function getBumperDirection(input) {
    const button = resolveBumperButton(input);
    const namedButton = String(button || "").toUpperCase();
    if (namedButton === "PAGEUP") {
      return -1;
    }
    if (namedButton === "PAGEDOWN") {
      return 1;
    }
    if (/(LEFT|L).*(BUMPER|SHOULDER)|\b(LB|L1)\b/.test(namedButton)) {
      return -1;
    }
    if (/(RIGHT|R).*(BUMPER|SHOULDER)|\b(RB|R1)\b/.test(namedButton)) {
      return 1;
    }

    const numericButton = Number(button);
    if (numericButton === 5 || numericButton === 7) {
      return -1;
    }
    if (numericButton === 6 || numericButton === 8) {
      return 1;
    }
    return 0;
  }

  function getAdjacentTabId(tabs, activeTabId, direction, wrapAround = true) {
    if (tabTopology?.getAdjacentTabId) {
      return tabTopology.getAdjacentTabId(
        tabs,
        activeTabId,
        direction,
        wrapAround,
      );
    }
    if (!Array.isArray(tabs) || !tabs.length || !direction) {
      return "";
    }

    const currentIndex = tabs.findIndex(
      (tab) => String(tab?.id || "") === String(activeTabId || ""),
    );
    if (currentIndex < 0) {
      return String(tabs[0]?.id || "");
    }

    let nextIndex = currentIndex + direction;
    if (wrapAround) {
      nextIndex = (nextIndex + tabs.length) % tabs.length;
    } else if (nextIndex < 0 || nextIndex >= tabs.length) {
      return "";
    }
    return String(tabs[nextIndex]?.id || "");
  }

  function getVisibleLibraryTabId(element) {
    const rawId = String(element?.id || "");
    const markerIndex = rawId.lastIndexOf("\u00bb");
    return markerIndex >= 0 ? rawId.slice(markerIndex + 1) : rawId;
  }

  function getVisibleLibraryTabs() {
    const selected = document.querySelector('[role="tab"][aria-selected="true"]');
    const tabList = selected?.closest?.('[role="tablist"]');
    if (!tabList) {
      return [];
    }

    const tabs = Array.from(tabList.querySelectorAll('[role="tab"]')).filter(
      (element) => {
        const rect = element.getBoundingClientRect();
        const style = window.getComputedStyle(element);
        return (
          rect.width > 0 &&
          rect.height > 0 &&
          style.display !== "none" &&
          style.visibility !== "hidden"
        );
      },
    );
    const ids = new Set(tabs.map(getVisibleLibraryTabId));
    return (
      ids.has("AllGames") &&
      ids.has("Installed")
    )
      ? tabs
      : [];
  }

  function getLiveLibraryNavigation() {
    const enabledDefinitions = getEnabledStoreDefinitions();
    if (!enabledDefinitions.length) {
      return null;
    }

    const visibleTabs = getVisibleLibraryTabs();
    if (!visibleTabs.length) {
      return null;
    }

    const runtime = state.navigationRuntime;
    const sourceTabs = Array.isArray(runtime?.tabs) && runtime.tabs.length
      ? runtime.tabs
      : visibleTabs.map((element) => ({
          id: getVisibleLibraryTabId(element),
        }));
    const tabs = tabTopology?.buildCanonicalTabOrder
      ? tabTopology.buildCanonicalTabOrder(
          sourceTabs,
          enabledDefinitions,
          storeDefinitions,
        )
      : sourceTabs;
    if (!tabs.length) {
      return null;
    }

    const selectedElement = visibleTabs.find(
      (element) => element.getAttribute("aria-selected") === "true",
    );
    const selectedTabId = selectedElement
      ? getVisibleLibraryTabId(selectedElement)
      : "";
    const virtualTabId = isManagedStoreTabId(state.virtualActiveTabId)
      ? state.virtualActiveTabId
      : "";
    const runtimeTabId = String(runtime?.activeTab || "");
    const availableTabIds = new Set(tabs.map((tab) => String(tab?.id || "")));
    // The runtime value is advanced synchronously by our navigation proxy.
    // Prefer it over the DOM selection, because Steam may render aria-selected
    // one frame later; otherwise a quick second bumper press repeats the same tab.
    const recentNavigationCursorTabId =
      Date.now() - Number(state.navigationCursorAt || 0) < 1500
        ? String(state.navigationCursorTabId || "")
        : "";
    const activeCandidates = [
      recentNavigationCursorTabId,
      selectedTabId,
      runtimeTabId,
      virtualTabId,
      String(state.navigationCursorTabId || ""),
    ];
    const activeTabId = tabTopology?.resolveActiveTabId
      ? tabTopology.resolveActiveTabId(tabs, activeCandidates)
      : activeCandidates.find((tabId) => tabId && availableTabIds.has(tabId)) ||
        String(tabs[0]?.id || "");

    return {
      tabs,
      activeTabId,
      visibleTabs,
      onShowTab: runtime?.onShowTab,
    };
  }

  function markExplicitNavigationIntent(tabId) {
    state.navigationIntentTabId = String(tabId || "");
    state.navigationIntentAt = Date.now();
  }

  function consumeExplicitNavigationIntent(tabId) {
    const normalizedTabId = String(tabId || "");
    const matches =
      state.navigationIntentTabId === normalizedTabId &&
      Date.now() - Number(state.navigationIntentAt || 0) < 1500;
    if (matches || Date.now() - Number(state.navigationIntentAt || 0) >= 1500) {
      state.navigationIntentTabId = "";
      state.navigationIntentAt = 0;
    }
    return matches;
  }

  function commitLibraryNavigationTarget(tabId, persist = true) {
    const normalizedTabId = String(tabId || "");
    if (!normalizedTabId) {
      return false;
    }

    const now = Date.now();
    const selectingStoreTab = isManagedStoreTabId(normalizedTabId);
    setVirtualActiveTabId(selectingStoreTab ? normalizedTabId : "", persist);
    state.nativeRouteEchoTabId = selectingStoreTab
      ? getNativeRouteTabId(normalizedTabId)
      : "";
    state.navigationCursorTabId = normalizedTabId;
    state.navigationCursorAt = now;
    if (state.navigationRuntime) {
      state.navigationRuntime.activeTab = normalizedTabId;
    }
    markExplicitNavigationIntent(normalizedTabId);
    return true;
  }

  function navigateLibraryByDirection(direction) {
    if (!state.pluginEnabled || !direction) {
      return false;
    }

    const navigation = getLiveLibraryNavigation();
    if (!navigation) {
      return false;
    }

    const now = Date.now();
    if (
      state.lastBumperDirection === direction &&
      now - state.lastBumperInputAt < 45
    ) {
      return true;
    }
    state.lastBumperDirection = direction;
    state.lastBumperInputAt = now;

    const nextTabId = getAdjacentTabId(
      navigation.tabs,
      navigation.activeTabId,
      direction,
      true,
    );
    if (!nextTabId) {
      return true;
    }

    commitLibraryNavigationTarget(nextTabId);
    const visibleDestination = navigation.visibleTabs
      .find((element) => getVisibleLibraryTabId(element) === nextTabId);
    if (visibleDestination) {
      visibleDestination.click?.();
    } else if (typeof navigation.onShowTab === "function") {
      navigation.onShowTab(nextTabId);
    }
    scheduleXboxTabPatch();
    return true;
  }

  function handleLibraryBumperInput(button) {
    const bumperDirection = getBumperDirection(button);
    if (bumperDirection) {
      return navigateLibraryByDirection(bumperDirection);
    }

    if (getHorizontalDpadDirection(button) && getControllerFocusedLibraryTab()) {
      scheduleFocusedLibraryTabActivation();
    }
    return false;
  }

  function getHorizontalDpadDirection(input) {
    const button = resolveBumperButton(input);
    const namedButton = String(button || "").toUpperCase();
    if (/(DPAD|GAMEPAD|ARROW).*LEFT|\bLEFT\b/.test(namedButton)) {
      return -1;
    }
    if (/(DPAD|GAMEPAD|ARROW).*RIGHT|\bRIGHT\b/.test(namedButton)) {
      return 1;
    }

    const numericButton = Number(button);
    return numericButton === 11 ? -1 : numericButton === 12 ? 1 : 0;
  }

  function getControllerFocusedLibraryTab() {
    const visibleTabs = getVisibleLibraryTabs();
    if (!visibleTabs.length) {
      return null;
    }

    const activeElement = document.activeElement;
    return visibleTabs.find((tab) =>
      tab === activeElement ||
      tab.contains?.(activeElement) ||
      tab.matches?.(":focus, .gpfocus, .gpfocuswithin") ||
      tab.querySelector?.(":focus, .gpfocus, .gpfocuswithin")) || null;
  }

  function activateFocusedLibraryTab() {
    state.libraryDpadActivationTimer = 0;
    if (!state.pluginEnabled) {
      return false;
    }

    const focusedTab = getControllerFocusedLibraryTab();
    const tabId = getVisibleLibraryTabId(focusedTab);
    if (!focusedTab || !tabId) {
      return false;
    }

    const navigation = getLiveLibraryNavigation();
    if (
      !navigation ||
      !navigation.tabs.some((tab) => String(tab?.id || "") === tabId) ||
      navigation.activeTabId === tabId
    ) {
      return false;
    }

    commitLibraryNavigationTarget(tabId);
    focusedTab.click?.();
    scheduleXboxTabPatch();
    return true;
  }

  function scheduleFocusedLibraryTabActivation() {
    if (state.libraryDpadActivationTimer) {
      window.clearTimeout(state.libraryDpadActivationTimer);
    }
    // Steam moves controller focus after dispatching the gamepad event. Read
    // the focused tab on the next task so D-Pad and keyboard arrows activate
    // the destination rather than the tab that focus is leaving.
    state.libraryDpadActivationTimer = window.setTimeout(
      activateFocusedLibraryTab,
      0,
    );
  }

  function isTemporaryInputCapture(callback) {
    return Boolean(
      callback?.__steamToolsArtworkCatchAll ||
      callback?.__steamLoaderPluginStoreCatchAll ||
      callback?.__steamLoaderPluginStoreQuickAccessCatchAll ||
      callback?.__steamToolsHomeReorderCatchAll ||
      callback?.__steamLoaderOverlayCaptureCatchAll
    );
  }

  function uninstallLibraryBumperInput() {
    const focusNav = state.catchAllController || window.FocusNavController;
    const current = focusNav?.m_fnCatchAllGamepadInput;
    if (
      state.catchAllInstalled &&
      focusNav?.SetCatchAllGamepadInput &&
      (
        current === state.catchAllGamepadInput ||
        current?.__steamLoaderXboxLibraryBumpers === stateVersion
      )
    ) {
      try {
        focusNav.SetCatchAllGamepadInput(
          current?.__steamLoaderXboxPreviousCatchAll ||
            state.previousCatchAllGamepadInput ||
            undefined,
        );
      } catch (_) {}
    }

    if (typeof state.libraryBumperKeyHandler === "function") {
      document.removeEventListener(
        "keydown",
        state.libraryBumperKeyHandler,
        true,
      );
    }
    if (typeof state.libraryBumperEventHandler === "function") {
      document.removeEventListener(
        "vgp_onbuttondown",
        state.libraryBumperEventHandler,
        true,
      );
    }
    if (typeof state.libraryTabFocusHandler === "function") {
      document.removeEventListener(
        "focusin",
        state.libraryTabFocusHandler,
        true,
      );
    }
    if (state.libraryDpadActivationTimer) {
      window.clearTimeout(state.libraryDpadActivationTimer);
      state.libraryDpadActivationTimer = 0;
    }

    state.handleLibraryBumperInput = null;
    state.libraryBumperKeyHandler = null;
    state.libraryBumperEventHandler = null;
    state.libraryTabFocusHandler = null;
    state.catchAllInstalled = false;
    state.catchAllController = null;
    state.catchAllGamepadInput = null;
    state.catchAllMissingSince = 0;
    state.previousCatchAllGamepadInput = null;
  }

  function installLibraryBumperInput() {
    if (!state.pluginEnabled || getEnabledStoreDefinitions().length === 0) {
      uninstallLibraryBumperInput();
      return;
    }

    state.handleLibraryBumperInput = handleLibraryBumperInput;
    const focusNav = window.FocusNavController;
    if (focusNav?.SetCatchAllGamepadInput) {
      const current = focusNav.m_fnCatchAllGamepadInput;
      if (
        current?.__steamLoaderXboxLibraryBumpers === stateVersion
      ) {
        state.catchAllController = focusNav;
        state.catchAllGamepadInput = current;
        state.catchAllInstalled = true;
        state.catchAllMissingSince = 0;
      } else if (isTemporaryInputCapture(current)) {
        // Modal Tools for Steam surfaces intentionally own the catch-all while
        // open. They restore this Library callback when they close.
        state.catchAllMissingSince = 0;
      } else {
        const replacingLostHook =
          state.catchAllInstalled &&
          state.catchAllController === focusNav &&
          state.catchAllGamepadInput &&
          current !== state.catchAllGamepadInput;
        if (replacingLostHook && !state.catchAllMissingSince) {
          state.catchAllMissingSince = Date.now();
        }

        const shouldBind =
          !replacingLostHook ||
          Date.now() - Number(state.catchAllMissingSince || 0) >=
            bumperRebindIntervalMs;
        if (shouldBind) {
          const previous =
            current?.__steamLoaderXboxPreviousCatchAll ||
            (
              current?.__steamLoaderXboxLibraryBumpers &&
              current.__steamLoaderXboxLibraryBumpers !== stateVersion
                ? previousState?.previousCatchAllGamepadInput
                : current
            );
          const callback = function (button, ...args) {
            const callbackThis = this;
            const currentState = window.__steamLoaderLibraryTabsState;
            if (currentState?.handleLibraryBumperInput?.(button)) {
              return true;
            }
            return typeof previous === "function"
              ? previous.call(callbackThis, button, ...args)
              : false;
          };
          callback.__steamLoaderXboxLibraryBumpers = stateVersion;
          callback.__steamLoaderXboxPreviousCatchAll = previous;
          state.previousCatchAllGamepadInput = previous;
          focusNav.SetCatchAllGamepadInput(callback);
          state.catchAllController = focusNav;
          state.catchAllGamepadInput = callback;
          state.catchAllInstalled = true;
          state.catchAllMissingSince = 0;
        }
      }
    } else {
      state.catchAllInstalled = false;
      state.catchAllController = null;
      state.catchAllGamepadInput = null;
      state.catchAllMissingSince = 0;
    }

    if (!state.libraryBumperKeyHandler) {
      state.libraryBumperKeyHandler = (event) => {
        const key = event.key || event.code || "";
        if (
          [
            "ArrowLeft",
            "ArrowRight",
            "GamepadLeft",
            "GamepadRight",
            "GamepadDPadLeft",
            "GamepadDPadRight",
          ].includes(key) &&
          getControllerFocusedLibraryTab()
        ) {
          scheduleFocusedLibraryTabActivation();
          return;
        }
        if (
          ![
            "PageUp",
            "PageDown",
            "GamepadLB",
            "GamepadRB",
            "GamepadL1",
            "GamepadR1",
            "GamepadLeftShoulder",
            "GamepadRightShoulder",
          ].includes(key) ||
          !handleLibraryBumperInput(key)
        ) {
          return;
        }

        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation?.();
      };
      document.addEventListener("keydown", state.libraryBumperKeyHandler, true);
    }

    if (!state.libraryBumperEventHandler) {
      state.libraryBumperEventHandler = (event) => {
        if (!handleLibraryBumperInput(event)) {
          return;
        }

        event.preventDefault?.();
        event.stopPropagation?.();
        event.stopImmediatePropagation?.();
      };
      document.addEventListener(
        "vgp_onbuttondown",
        state.libraryBumperEventHandler,
        true,
      );
    }

    if (!state.libraryTabFocusHandler) {
      state.libraryTabFocusHandler = (event) => {
        const focusedTab = event.target?.closest?.('[role="tab"]');
        if (
          focusedTab?.closest?.('[role="tablist"]') &&
          getVisibleLibraryTabs().includes(focusedTab)
        ) {
          scheduleFocusedLibraryTabActivation();
        }
      };
      document.addEventListener("focusin", state.libraryTabFocusHandler, true);
    }
  }

  function isNativeBumperTabHandler(handler) {
    if (typeof handler !== "function") {
      return false;
    }

    if (handler.__steamLoaderXboxBumperNavigation) {
      return true;
    }

    const source = String(handler);
    return source.includes("BUMPER_LEFT") && source.includes("BUMPER_RIGHT");
  }

  function buildBumperTabHandler(originalHandler, navigation) {
    const nativeHandler =
      originalHandler.__steamLoaderXboxOriginalBumperHandler ||
      originalHandler;
    const handler = function (event, ...args) {
      const handlerThis = this;
      const direction = getBumperDirection(event);
      if (!direction) {
        return nativeHandler.call(handlerThis, event, ...args);
      }

      if (!navigateLibraryByDirection(direction)) {
        return nativeHandler.call(handlerThis, event, ...args);
      }

      event?.preventDefault?.();
      event?.stopPropagation?.();
      event?.stopImmediatePropagation?.();
      return true;
    };

    handler.__steamLoaderXboxBumperNavigation = stateVersion;
    handler.__steamLoaderXboxOriginalBumperHandler = nativeHandler;
    return handler;
  }

  function reconcileTabs(tabs) {
    const merged = mergeXboxTab(tabs);
    if (tabsSignature(tabs) === tabsSignature(merged) && tabs.length === merged.length) {
      return false;
    }

    tabs.splice(0, tabs.length, ...merged);
    return true;
  }

  function mutateRenderedTabs(element, navigation = null, visited = new Set()) {
    if (!element || typeof element !== "object" || visited.has(element)) {
      return false;
    }

    visited.add(element);
    let changed = false;
    if (
      navigation &&
      isNativeBumperTabHandler(element.props?.onButtonDown) &&
      element.props.onButtonDown.__steamLoaderXboxBumperNavigation !== stateVersion
    ) {
      element.props = {
        ...element.props,
        onButtonDown: buildBumperTabHandler(
          element.props.onButtonDown,
          navigation,
        ),
      };
      changed = true;
    }

    if (element.props && isLibraryTabsArray(element.props.tabs)) {
      const merged = mergeXboxTab(element.props.tabs);
      if (
        tabsSignature(element.props.tabs) !== tabsSignature(merged) ||
        element.props.tabs.length !== merged.length
      ) {
        element.props = { ...element.props, tabs: merged };
        changed = true;
      }
    }

    const children = element.props?.children;
    if (Array.isArray(children)) {
      for (const child of children) {
        changed = mutateRenderedTabs(child, navigation, visited) || changed;
      }
    } else if (children && typeof children === "object") {
      changed = mutateRenderedTabs(children, navigation, visited) || changed;
    }

    return changed;
  }

  function recoverXboxTabStateFromDom() {
    if (!getEnabledStoreDefinitions().length) {
      return false;
    }

    const selectedTab = getVisibleLibraryTabs().find(
      (element) =>
        element.getAttribute("aria-selected") === "true" &&
        isManagedStoreTabId(getVisibleLibraryTabId(element)),
    );
    if (selectedTab) {
      const tabId = getVisibleLibraryTabId(selectedTab);
      setVirtualActiveTabId(tabId);
      state.navigationCursorTabId = tabId;
      state.navigationCursorAt = Date.now();
      return true;
    }

    for (const panel of document.querySelectorAll('[role="tabpanel"]')) {
      const labelledTabId = getVisibleLibraryTabId({
        id: panel.getAttribute("aria-labelledby") || "",
      });
      if (!isManagedStoreTabId(labelledTabId)) {
        continue;
      }

      const rect = panel.getBoundingClientRect();
      const style = window.getComputedStyle(panel);
      if (
        rect.width > 0 &&
        rect.height > 0 &&
        style.display !== "none" &&
        style.visibility !== "hidden"
      ) {
        setVirtualActiveTabId(labelledTabId);
        state.navigationCursorTabId = labelledTabId;
        state.navigationCursorAt = Date.now();
        return true;
      }
    }

    return false;
  }

  function scheduleXboxTabPatch() {
    state.forceRenderRequested = true;
    if (state.patchSoonTimer) {
      return;
    }

    state.patchSoonTimer = window.setTimeout(() => {
      state.patchSoonTimer = 0;
      patchXboxTabBasis();
    }, 25);
  }

  function getNavigationHandler(onShowTab) {
    if (typeof onShowTab !== "function") {
      return onShowTab;
    }

    const nativeOnShowTab = getNativeNavigationHandler(onShowTab);
    if (nativeOnShowTab.__steamLoaderXboxNavigationProxy === stateVersion) {
      return nativeOnShowTab;
    }

    const cached = state.navigationHandlers.get(nativeOnShowTab);
    if (cached) {
      return cached;
    }

    const wrapped = function (tabId, ...args) {
      // Steam's tab row already includes the injected entry when it calculates
      // the next LB/RB target. The Library router does not know that custom id,
      // though, so it immediately reports a native fallback as active. Remember
      // the selection here and feed it back into the native tab components.
      const previousVirtualTabId = state.virtualActiveTabId;
      const normalizedTabId = String(tabId || "");
      const selectingStoreTab = isManagedStoreTabId(normalizedTabId);
      const explicitNavigation = consumeExplicitNavigationIntent(normalizedTabId);
      // A virtual tab is backed by a native Steam route. Steam can echo that
      // route, or even the route of the previously selected virtual tab, after
      // the requested transition has already rendered. Keep the virtual tab
      // authoritative unless the user explicitly selected a different tab.
      const preserveVirtualSelection =
        tabTopology?.shouldPreserveVirtualSelection
          ? tabTopology.shouldPreserveVirtualSelection(
              previousVirtualTabId,
              normalizedTabId,
              state.nativeRouteEchoTabId,
              explicitNavigation,
            )
          : (
              isManagedStoreTabId(previousVirtualTabId) &&
              normalizedTabId !== previousVirtualTabId &&
              !explicitNavigation &&
              normalizedTabId === state.nativeRouteEchoTabId
            );
      if (selectingStoreTab && !preserveVirtualSelection) {
        setVirtualActiveTabId(normalizedTabId);
        state.nativeRouteEchoTabId = getNativeRouteTabId(normalizedTabId);
      } else if (!preserveVirtualSelection) {
        setVirtualActiveTabId("");
        state.nativeRouteEchoTabId = "";
      }
      const effectiveTabId = isManagedStoreTabId(state.virtualActiveTabId)
        ? state.virtualActiveTabId
        : normalizedTabId;
      state.navigationCursorTabId = effectiveTabId;
      state.navigationCursorAt = Date.now();
      if (state.navigationRuntime) {
        state.navigationRuntime.activeTab = effectiveTabId;
      }
      // Persist a real native route in Steam's history. Alternating backing
      // routes make every transition between adjacent virtual stores observable
      // to Steam while our wrapper continues to render the requested store.
      const nativeRouteTabId = isManagedStoreTabId(state.virtualActiveTabId)
        ? getNativeRouteTabId(state.virtualActiveTabId)
        : normalizedTabId;
      const result = nativeOnShowTab.call(this, nativeRouteTabId, ...args);
      if (previousVirtualTabId !== state.virtualActiveTabId) {
        scheduleXboxTabPatch();
      }
      scheduleXboxTileBadges();
      return result;
    };

    wrapped.__steamLoaderXboxNavigationProxy = stateVersion;
    wrapped.__steamLoaderXboxOriginalNavigationHandler = nativeOnShowTab;
    state.navigationHandlers.set(nativeOnShowTab, wrapped);
    return wrapped;
  }

  function getNativeNavigationHandler(onShowTab) {
    let current = onShowTab;
    const visited = new Set();
    while (
      typeof current === "function" &&
      current.__steamLoaderXboxNavigationProxy &&
      typeof current.__steamLoaderXboxOriginalNavigationHandler === "function" &&
      !visited.has(current)
    ) {
      visited.add(current);
      current = current.__steamLoaderXboxOriginalNavigationHandler;
    }
    return current;
  }

  function buildLibraryTabProps(props) {
    const tabs = mergeXboxTab(props.tabs);
    const onShowTab = state.pluginEnabled
      ? getNavigationHandler(props.onShowTab)
      : getNativeNavigationHandler(props.onShowTab);
    const nextProps = {
      ...props,
      tabs,
      activeTab: isManagedStoreTabId(state.virtualActiveTabId)
        ? state.virtualActiveTabId
        : props.activeTab,
      onShowTab,
    };
    if (
      !isManagedStoreTabId(state.virtualActiveTabId) &&
      String(nextProps.activeTab || "")
    ) {
      state.navigationCursorTabId = String(nextProps.activeTab);
      state.navigationCursorAt = Date.now();
    }
    state.navigationRuntime = state.pluginEnabled
      ? {
          tabs: nextProps.tabs,
          activeTab: nextProps.activeTab,
          onShowTab: nextProps.onShowTab,
          wrapAround: nextProps.wrapAround ?? true,
        }
      : null;
    return nextProps;
  }

  function getOriginalComponent(component) {
    let original = component;
    const visited = new Set();
    while (typeof original === "function" && !visited.has(original)) {
      visited.add(original);
      const candidate =
        original.__steamLoaderXboxTabOriginal ||
        original.__steamLoaderLibraryTabsOriginal;
      if (typeof candidate !== "function" || candidate === original) {
        break;
      }
      original = candidate;
    }
    return original;
  }

  function wrapComponent(component) {
    const original = getOriginalComponent(component);
    if (component?.__steamLoaderXboxTabWrapped === stateVersion) {
      return component;
    }

    if (typeof original !== "function") {
      return component;
    }

    const wrapped = function (props, ...rest) {
      const nextProps = props && isLibraryTabsArray(props.tabs)
        ? buildLibraryTabProps(props)
        : props;
      const result = original.call(this, nextProps, ...rest);
      const navigation = nextProps && isLibraryTabsArray(nextProps.tabs)
        ? {
            tabs: nextProps.tabs,
            activeTab: nextProps.activeTab,
            onShowTab: nextProps.onShowTab,
            wrapAround: nextProps.wrapAround ?? true,
          }
        : null;
      mutateRenderedTabs(result, navigation);
      return result;
    };

    wrapped.__steamLoaderXboxTabWrapped = stateVersion;
    wrapped.__steamLoaderXboxTabOriginal = original;
    try {
      wrapped.displayName = original.displayName || original.name || "SteamLoaderXboxTabBasis";
    } catch (_) {}
    return wrapped;
  }

  function wrapFiberNode(node) {
    const memoType = node?.elementType?.type;
    const directType = node?.type;
    const component =
      typeof memoType === "function"
        ? memoType
        : typeof directType === "function"
          ? directType
          : null;
    if (!component) {
      return false;
    }

    const original = getOriginalComponent(component);
    const wrapped = wrapComponent(component);
    let changed = false;
    for (const target of [node, node?.alternate]) {
      if (!target) {
        continue;
      }

      const targetMemoType = target.elementType?.type;
      if (
        typeof targetMemoType === "function" &&
        getOriginalComponent(targetMemoType) === original &&
        targetMemoType !== wrapped
      ) {
        target.elementType.type = wrapped;
        changed = true;
      }

      if (
        typeof target.type === "function" &&
        getOriginalComponent(target.type) === original &&
        target.type !== wrapped
      ) {
        target.type = wrapped;
        changed = true;
      }
    }

    return changed;
  }

  function restoreFiberNode(node) {
    let changed = false;
    for (const target of [node, node?.alternate]) {
      if (!target) {
        continue;
      }

      const targetMemoType = target.elementType?.type;
      if (typeof targetMemoType === "function") {
        const originalMemoType = getOriginalComponent(targetMemoType);
        if (originalMemoType !== targetMemoType) {
          target.elementType.type = originalMemoType;
          changed = true;
        }
      }

      if (typeof target.type === "function") {
        const originalType = getOriginalComponent(target.type);
        if (originalType !== target.type) {
          target.type = originalType;
          changed = true;
        }
      }
    }

    return changed;
  }

  function refreshCurrentLibraryRoute(libraryNodes) {
    for (const node of libraryNodes) {
      for (const props of [node?.memoizedProps, node?.pendingProps]) {
        if (
          typeof props?.onShowTab === "function" &&
          typeof props?.activeTab === "string" &&
          props.activeTab
        ) {
          try {
            const onShowTab =
              state.pluginEnabled && getEnabledStoreDefinitions().length > 0
                ? getNavigationHandler(props.onShowTab)
                : getNativeNavigationHandler(props.onShowTab);
            onShowTab(
              isManagedStoreTabId(state.virtualActiveTabId)
                ? state.virtualActiveTabId
                : props.activeTab,
            );
          } catch (_) {}
          return;
        }
      }
    }
  }

  function migrateLibraryNavigation(libraryNodes) {
    if (!state.navigationMigrationRequested) {
      return;
    }

    for (const node of libraryNodes) {
      for (const props of [node?.memoizedProps, node?.pendingProps]) {
        if (
          typeof props?.onShowTab !== "function" ||
          !isLibraryTabsArray(props?.tabs)
        ) {
          continue;
        }

        const desiredTabId =
          isManagedStoreTabId(state.virtualActiveTabId)
            ? state.virtualActiveTabId
            : String(props.activeTab || "AllGames");
        const fallbackTabId = desiredTabId === "AllGames"
          ? "Installed"
          : "AllGames";
        const onShowTab = getNavigationHandler(props.onShowTab);
        state.navigationMigrationRequested = false;
        try {
          markExplicitNavigationIntent(fallbackTabId);
          onShowTab(fallbackTabId);
        } catch (_) {
          return;
        }

        state.navigationMigrationTimer = window.setTimeout(() => {
          state.navigationMigrationTimer = 0;
          try {
            markExplicitNavigationIntent(desiredTabId);
            onShowTab(desiredTabId);
          } catch (_) {}
          scheduleXboxTabPatch();
        }, 90);
        return;
      }
    }
  }

  function libraryTabLayoutNeedsPatch() {
    const visibleTabs = getVisibleLibraryTabs();
    if (!visibleTabs.length) {
      return false;
    }

    const visibleTabIds = new Set(visibleTabs.map(getVisibleLibraryTabId));
    const enabledTabIds = new Set(
      getEnabledStoreDefinitions().map((definition) => definition.tabId),
    );
    return (
      [...enabledTabIds].some((tabId) => !visibleTabIds.has(tabId)) ||
      storeDefinitions.some((definition) =>
        !enabledTabIds.has(definition.tabId) &&
        visibleTabIds.has(definition.tabId))
    );
  }

  function patchXboxTabBasis() {
    try {
      installLibraryBumperInput();
      const runtimeActive =
        state.pluginEnabled && getEnabledStoreDefinitions().length > 0;
      const rootFiber = getRootFiber();
      if (!rootFiber) {
        setStatus("waiting for Steam Library");
        return false;
      }

      const libraryNodes = [];
      const forceUpdateHosts = new Set();
      walkFiber(rootFiber, (node) => {
        if (!getTabArrays(node).some(isLibraryTabsArray)) {
          return;
        }

        libraryNodes.push(node);
        let current = node;
        for (let depth = 0; current && depth < 12; depth += 1, current = current.return) {
          if (typeof current.stateNode?.forceUpdate === "function") {
            forceUpdateHosts.add(current.stateNode);
          }
        }
      });

      if (!libraryNodes.length) {
        setStatus("waiting for native Library tabs");
        return false;
      }

      const tabArrays = new Set();
      let wrapped = false;
      for (const node of libraryNodes) {
        let current = node;
        for (let depth = 0; current && depth < 12; depth += 1, current = current.return) {
          wrapped =
            (runtimeActive
              ? wrapFiberNode(current)
              : restoreFiberNode(current)) || wrapped;
        }

        for (const tabs of getTabArrays(node)) {
          if (isLibraryTabsArray(tabs)) {
            tabArrays.add(tabs);
          }
        }
      }

      let changed = false;
      for (const tabs of tabArrays) {
        changed = reconcileTabs(tabs) || changed;
      }

      const forceRenderRequested = state.forceRenderRequested;
      const routeRefreshRequested = state.routeRefreshRequested;
      state.forceRenderRequested = false;
      state.routeRefreshRequested = false;
      if (changed || wrapped || forceRenderRequested) {
        for (const host of forceUpdateHosts) {
          try {
            host.forceUpdate();
          } catch (_) {}
        }
      }
      if (routeRefreshRequested) {
        refreshCurrentLibraryRoute(libraryNodes);
      }
      if (runtimeActive) {
        migrateLibraryNavigation(libraryNodes);
      }

      state.lastPatchedAt = Date.now();
      state.mutationCount += changed ? 1 : 0;
      state.wrappedCount += wrapped ? 1 : 0;
      const enabledDefinitions = getEnabledStoreDefinitions();
      setStatus(
        enabledDefinitions.length
          ? `${enabledDefinitions.map((definition) => definition.title).join(" + ")} native filters active (${state.managedAppIds.size} managed games, dynamic LB/RB navigation)`
          : "No connected OmniLibrary store is ready for a native tab",
      );
      scheduleXboxTileBadges();
      return changed || wrapped;
    } catch (error) {
      setStatus("OmniLibrary tab basis failed", String(error?.message || error));
      return false;
    }
  }

  function requestOmniLibraryStoreStateRefresh() {
    void refreshXboxAppIds(true);
  }

  function isActiveDownloadCenterStatus(status) {
    return [
      "preparing",
      "queued",
      "downloading",
      "reconnecting",
      "finalizing",
      "canceling",
    ].includes(String(status || "").toLowerCase());
  }

  function isPendingLifecycleStatus(status) {
    return isActiveDownloadCenterStatus(status) ||
      ["uninstalling", "uninstall-action-required"].includes(
        String(status || "").toLowerCase(),
      );
  }

  function applyConfirmedInstalledLifecycleDeltas(entries) {
    let changed = false;
    const now = Date.now();
    for (const entry of entries || []) {
      const appId = Number(entry?.steamAppId || 0);
      const storeId = String(entry?.storeId || "");
      const status = String(entry?.status || "").toLowerCase();
      if (
        !Number.isInteger(appId) ||
        appId <= 0 ||
        !storeId
      ) {
        continue;
      }

      if (status === "uninstalling" || status === "uninstall-action-required") {
        state.confirmedInstalledHints.delete(appId);
        continue;
      }
      if (
        status !== "completed" ||
        entry?.installed !== true ||
        !state.pendingLifecycleAppIds.has(appId)
      ) {
        continue;
      }

      // A managed worker always persists its provider install state before it
      // publishes completed. Preserve that confirmed edge briefly so an older
      // summary response cannot turn Play back into Download.
      state.confirmedInstalledHints.set(appId, {
        storeId,
        expiresAt: now + 15000,
      });
      for (const definition of storeDefinitions) {
        const sourceStoreId = definition.sourceStoreId || definition.id;
        const storeState = state.storeStates.get(definition.id);
        if (
          sourceStoreId !== storeId ||
          storeState?.appIds?.has(appId) !== true ||
          storeState.installedAppIds.has(appId)
        ) {
          continue;
        }
        storeState.installedAppIds.add(appId);
        changed = true;
      }
      if (!state.managedInstalledAppIds.has(appId)) {
        state.managedInstalledAppIds.add(appId);
        changed = true;
      }
    }

    if (!changed) {
      return false;
    }

    state.xboxInstalledAppIds =
      state.storeStates.get("xbox-game-pass")?.installedAppIds || new Set();
    state.derivedTopologyCache = null;
    state.forceRenderRequested = true;
    state.routeRefreshRequested = true;
    scheduleXboxTileBadges();
    scheduleXboxTabPatch();
    return true;
  }

  function applyPendingLifecycleAppIds(appIds) {
    const next = new Set(
      Array.from(appIds || [])
        .map((appId) => Number(appId))
        .filter((appId) =>
          Number.isInteger(appId) &&
          appId > 0 &&
          state.managedAppIds.has(appId)),
    );
    const signature = Array.from(next)
      .sort((left, right) => left - right)
      .join(",");
    const changed = signature !== state.pendingLifecycleSignature;
    const completed =
      state.pendingLifecycleAppIds.size > 0 &&
      next.size === 0;
    state.pendingLifecycleAppIds = next;
    state.pendingLifecycleSignature = signature;
    return { changed, completed };
  }

  function applyActiveDownloadAppIds(appIds) {
    const next = new Set(
      Array.from(appIds || [])
        .map((appId) => Number(appId))
        .filter((appId) =>
          Number.isInteger(appId) &&
          appId > 0 &&
          state.managedAppIds.has(appId)),
    );
    const signature = Array.from(next)
      .sort((left, right) => left - right)
      .join(",");
    if (signature === state.downloadAppIdsSignature) {
      return false;
    }

    state.managedActiveDownloadAppIds = next;
    state.downloadAppIdsSignature = signature;
    scheduleXboxTileBadges();
    return true;
  }

  function scheduleDownloadStateRefresh(delayOverride = null) {
    if (state.downloadRefreshTimer) {
      window.clearTimeout(state.downloadRefreshTimer);
      state.downloadRefreshTimer = 0;
    }
    if (
      !state.pluginEnabled ||
      getEnabledStoreDefinitions().length === 0 ||
      document.visibilityState === "hidden"
    ) {
      return;
    }

    const delay = Number.isFinite(delayOverride)
      ? Math.max(0, Number(delayOverride))
      : state.managedActiveDownloadAppIds.size > 0 ||
          state.pendingLifecycleAppIds.size > 0
        ? activeDownloadRefreshIntervalMs
        : idleDownloadRefreshIntervalMs;
    state.downloadRefreshTimer = window.setTimeout(() => {
      state.downloadRefreshTimer = 0;
      void refreshOmniLibraryDownloadStates();
    }, delay);
  }

  async function refreshOmniLibraryDownloadStates() {
    if (
      state.downloadRequestInFlight ||
      !state.pluginEnabled ||
      getEnabledStoreDefinitions().length === 0
    ) {
      scheduleDownloadStateRefresh();
      return;
    }

    state.downloadRequestInFlight = true;
    try {
      const response = await fetchOmniLibraryState(
        "api/unifystore/downloads",
      );
      if (!response.ok) {
        throw new Error(
          `OmniLibrary download state failed (${response.status}).`,
        );
      }

      const snapshot = await response.json();
      const entries = snapshot?.entries || [];
      applyConfirmedInstalledLifecycleDeltas(entries);
      applyActiveDownloadAppIds(
        entries
          .filter((entry) => isActiveDownloadCenterStatus(entry?.status))
          .map((entry) => entry?.steamAppId),
      );
      const lifecycle = applyPendingLifecycleAppIds(
        entries
          .filter((entry) => isPendingLifecycleStatus(entry?.status))
          .map((entry) => Number(entry?.steamAppId || 0))
          .filter((appId) => Number.isInteger(appId) && appId > 0),
      );
      if (lifecycle.completed) {
        void refreshXboxAppIds(true);
      } else if (lifecycle.changed && state.pendingLifecycleAppIds.size > 0) {
        // Prime the compact summary once when a managed mutation begins. The
        // 2-second status poll remains the only recurring request.
        void refreshXboxAppIds(true);
      }
    } catch (_) {
      // Keep the last confirmed icon state during a brief API interruption.
    } finally {
      state.downloadRequestInFlight = false;
      scheduleDownloadStateRefresh();
    }
  }

  function disableActiveRuntimeTimers() {
    if (state.patchTimer) {
      window.clearInterval(state.patchTimer);
      state.patchTimer = 0;
    }
    if (state.bumperRebindTimer) {
      window.clearInterval(state.bumperRebindTimer);
      state.bumperRebindTimer = 0;
    }
    if (state.navigationMigrationTimer) {
      window.clearTimeout(state.navigationMigrationTimer);
      state.navigationMigrationTimer = 0;
    }
    if (state.downloadRefreshTimer) {
      window.clearTimeout(state.downloadRefreshTimer);
      state.downloadRefreshTimer = 0;
    }
  }

  function ensureActiveRuntimeTimers() {
    if (!state.pluginEnabled || getEnabledStoreDefinitions().length === 0) {
      disableActiveRuntimeTimers();
      return;
    }

    if (!state.patchTimer) {
      state.patchTimer = window.setInterval(() => {
        if (document.visibilityState === "hidden") {
          return;
        }

        installLibraryBumperInput();
        if (state.forceRenderRequested || libraryTabLayoutNeedsPatch()) {
          patchXboxTabBasis();
        }
      }, patchIntervalMs);
    }

    if (!state.bumperRebindTimer) {
      state.bumperRebindTimer = window.setInterval(() => {
        if (document.visibilityState !== "hidden") {
          // Steam recreates FocusNavController independently from the React
          // Library tree. This identity-only check repairs a lost hook without
          // walking fibers, refetching stores, or forcing a render.
          installLibraryBumperInput();
        }
      }, bumperRebindIntervalMs);
    }
  }

  state.refreshStoreState = requestOmniLibraryStoreStateRefresh;
  if (!state.channel && typeof window.BroadcastChannel === "function") {
    try {
      state.channel = new window.BroadcastChannel(omniLibraryStoreChannelName);
      state.channel.addEventListener("message", (event) => {
        if (event?.data?.type === "stores-changed") {
          requestOmniLibraryStoreStateRefresh();
        } else if (event?.data?.type === "download-status-changed") {
          const appId = Number(event.data.appId || 0);
          const next = new Set(state.managedActiveDownloadAppIds);
          if (isActiveDownloadCenterStatus(event.data.status)) {
            next.add(appId);
          } else {
            next.delete(appId);
          }
          applyActiveDownloadAppIds(next);
          const pendingLifecycle = new Set(state.pendingLifecycleAppIds);
          if (isPendingLifecycleStatus(event.data.status)) {
            pendingLifecycle.add(appId);
          } else {
            pendingLifecycle.delete(appId);
          }
          applyPendingLifecycleAppIds(pendingLifecycle);
          scheduleDownloadStateRefresh(500);
        } else if (
          event?.data?.type === "uninstall-notice" &&
          state.pluginEnabled
        ) {
          showOmniLibraryUninstallNotice(event.data);
        }
      });
    } catch (_) {
      state.channel = null;
    }
  }

  if (!state.storageHandler) {
    state.storageHandler = (event) => {
      if (event?.key === omniLibraryStoreStorageKey) {
        requestOmniLibraryStoreStateRefresh();
      } else if (event?.key === activeStoreTabSessionKey) {
        const remoteTabId = String(event.newValue || "");
        const remoteStoreTabId = isManagedStoreTabId(remoteTabId)
          ? remoteTabId
          : "";
        setVirtualActiveTabId(remoteStoreTabId, false);
        state.navigationCursorTabId = remoteStoreTabId;
        state.navigationCursorAt = Date.now();
        state.nativeRouteEchoTabId = remoteStoreTabId
          ? getNativeRouteTabId(remoteStoreTabId)
          : "";
        if (state.navigationRuntime && remoteStoreTabId) {
          state.navigationRuntime.activeTab = remoteStoreTabId;
        }
      }
    };
    window.addEventListener("storage", state.storageHandler);
  }

  if (!state.visibilityHandler) {
    state.visibilityHandler = () => {
      if (document.visibilityState !== "hidden") {
        void refreshXboxAppIds();
        void refreshOmniLibraryDownloadStates();
        if (
          state.pluginEnabled &&
          getEnabledStoreDefinitions().length > 0
        ) {
          scheduleXboxTabPatch();
        }
      }
    };
    document.addEventListener("visibilitychange", state.visibilityHandler);
  }

  if (!state.omniLibraryStateUnsubscribe) {
    state.omniLibraryStateUnsubscribe = omniLibraryStateStore.subscribe(() => {
      if (!state.libraryRequestInFlight) {
        void refreshXboxAppIds();
      }
    });
  }

  if (!state.libraryRefreshTimer) {
    state.libraryRefreshTimer = window.setInterval(() => {
      void refreshXboxAppIds();
    }, libraryRefreshIntervalMs);
  }

  void refreshXboxAppIds(true);
})();
