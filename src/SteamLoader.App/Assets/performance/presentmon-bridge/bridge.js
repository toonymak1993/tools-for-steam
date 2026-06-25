(function () {
  "use strict";

  const statusElement = document.getElementById("status");
  const state = {
    lastAppliedHash: null,
    lastLoadedHash: null,
    introspection: null,
    running: false,
  };

  function setStatus(message) {
    if (statusElement) {
      statusElement.textContent = message;
    }
  }

  function invokeEndpointFuture(key, payload) {
    return new Promise((resolve, reject) => {
      if (!window.core || typeof window.core.invokeEndpoint !== "function") {
        reject(new Error("PresentMon core bridge is not available."));
        return;
      }

      window.core.invokeEndpoint(key, payload, resolve, reject);
    });
  }

  function deepClone(value) {
    return value == null ? value : JSON.parse(JSON.stringify(value));
  }

  async function fetchBridgeConfig() {
    const response = await fetch(`./bridge-config.json?ts=${Date.now()}`, {
      cache: "no-store",
    });

    if (!response.ok) {
      throw new Error(`Bridge config load failed with HTTP ${response.status}.`);
    }

    return await response.json();
  }

  async function loadIntrospection() {
    if (state.introspection) {
      return state.introspection;
    }

    const intro = await invokeEndpointFuture("Introspect", {});
    state.introspection = intro;
    return intro;
  }

  function resolveAdapterId(preferences, intro) {
    if (typeof preferences?.adapterId === "number" && preferences.adapterId !== 0) {
      return preferences.adapterId;
    }

    return typeof intro?.defaultAdapterId === "number" ? intro.defaultAdapterId : 0;
  }

  function normalizeWidgets(widgets, intro, preferences) {
    const metrics = Array.isArray(intro?.metrics) ? intro.metrics : [];
    const systemDeviceId = typeof intro?.systemDeviceId === "number" ? intro.systemDeviceId : 0;
    const adapterId = resolveAdapterId(preferences, intro);

    return (Array.isArray(widgets) ? widgets : [])
      .map((widget) => {
        const clone = deepClone(widget) || {};
        const widgetMetrics = Array.isArray(clone.metrics) ? clone.metrics : [];
        clone.metrics = widgetMetrics.filter((widgetMetric) => {
          const metricSpec = widgetMetric?.metric;
          if (!metricSpec || typeof metricSpec.metricId !== "number") {
            return false;
          }

          const introMetric = metrics.find((metric) => metric.id === metricSpec.metricId);
          if (!introMetric || !Array.isArray(introMetric.availableDeviceIds) || !introMetric.availableDeviceIds.length) {
            return false;
          }

          metricSpec.deviceId = 0;
          if (!introMetric.availableDeviceIds.includes(0)) {
            if (introMetric.availableDeviceIds.includes(systemDeviceId)) {
              metricSpec.deviceId = systemDeviceId;
            } else if (adapterId && introMetric.availableDeviceIds.includes(adapterId)) {
              metricSpec.deviceId = adapterId;
            } else {
              return false;
            }
          }

          metricSpec.desiredUnitId = introMetric.preferredUnitId;
          return true;
        });

        return clone;
      })
      .filter((widget) => Array.isArray(widget.metrics) && widget.metrics.length > 0);
  }

  async function applyBridgeConfig(config) {
    const introspection = await loadIntrospection();
    const preferences = deepClone(config.preferences || {});
    const widgets = normalizeWidgets(config.widgets, introspection, preferences);
    const pid = typeof config.pid === "number" && config.pid > 0 ? config.pid : null;

    await invokeEndpointFuture("PushSpecification", {
      pid,
      preferences,
      widgets,
    });

    const overlayLevelTitle = config.overlayLevelTitle || "Basic";
    if (pid) {
      const targetName = config.targetProcessName || `PID ${pid}`;
      setStatus(`Overlay live\nPreset: ${overlayLevelTitle}\nTarget: ${targetName}`);
      return;
    }

    setStatus(`Overlay waiting\nPreset: ${overlayLevelTitle}\nWaiting for a valid foreground game window.`);
  }

  async function tick() {
    const config = await fetchBridgeConfig();
    const loadedHash = JSON.stringify(config);
    state.lastLoadedHash = loadedHash;

    if (state.lastAppliedHash === loadedHash) {
      return;
    }

    await applyBridgeConfig(config);
    state.lastAppliedHash = loadedHash;
  }

  function forceRepush(reason) {
    state.lastAppliedHash = null;
    if (reason) {
      setStatus(reason);
    }
  }

  function registerSignalHandlers() {
    if (!window.core || typeof window.core.registerSignalHandler !== "function") {
      return;
    }

    window.core.registerSignalHandler("targetLost", function () {
      forceRepush("Target lost\nRetrying on next bridge poll...");
    });

    window.core.registerSignalHandler("stalePid", function () {
      forceRepush("Target exited\nWaiting for the next target update...");
    });

    window.core.registerSignalHandler("overlayDied", function () {
      forceRepush("Overlay crashed\nAttempting to restore...");
    });

    window.core.registerSignalHandler("presentmonInitFailed", function () {
      setStatus("PresentMon initialization failed.");
    });
  }

  async function loop() {
    if (state.running) {
      return;
    }

    state.running = true;
    registerSignalHandlers();

    for (;;) {
      try {
        await tick();
      } catch (error) {
        const message = error && error.message ? error.message : String(error);
        setStatus(`Bridge retrying\n${message}`);
      }

      await new Promise((resolve) => setTimeout(resolve, 750));
    }
  }

  setStatus("Booting bridge...");
  void loop();
})();
