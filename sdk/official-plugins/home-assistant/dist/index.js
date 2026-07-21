(() => {
  const manifest = {
    id: "home-assistant",
    name: "Home Assistant",
    version: "0.4.1",
    sdkVersion: "1.0.0",
    permissions: ["frontend", "storage", "secrets", "network"],
    networkHosts: ["<local>"],
  };

  const tokenSecretKey = "accessToken";
  const stateRefreshIntervalMs = 8_000;
  const areaRefreshIntervalMs = 60_000;
  const sliderCommitDelayMs = 400;
  const areaTemplate = `[
{%- for area_id in areas() %}
{"id": {{ area_id | to_json }}, "name": {{ area_name(area_id) | to_json }}, "entities": [
{%- for entity_id in area_entities(area_id) %}
{"id": {{ entity_id | to_json }}, "deviceId": {{ device_id(entity_id) | to_json }}, "deviceName": {{ device_name(entity_id) | to_json }}}{% if not loop.last %},{% endif %}
{%- endfor %}
]}{% if not loop.last %},{% endif %}
{%- endfor %}
]`;
  const toggleDomains = new Set([
    "automation",
    "fan",
    "group",
    "humidifier",
    "input_boolean",
    "light",
    "remote",
    "switch",
  ]);
  const pressDomains = new Set(["button", "input_button", "script"]);
  const domainOrder = [
    "light",
    "switch",
    "input_boolean",
    "fan",
    "cover",
    "climate",
    "number",
    "input_number",
    "select",
    "input_select",
    "button",
    "input_button",
    "script",
    "automation",
    "media_player",
    "sensor",
    "binary_sensor",
  ];

  let sdk = null;
  let activeContext = null;
  let configurationLoaded = false;
  let initializationPromise = null;
  let refreshPromise = null;
  let cancelAutoRefresh = null;
  let refreshScheduleRevision = 0;
  let draftBaseUrl = "";
  let draftAccessToken = "";
  let hasAccessToken = false;
  let allStates = [];
  let statesById = new Map();
  let areaDefinitions = [];
  let areaMappingLoaded = false;
  let areas = [];
  let scenes = [];
  let activeView = { kind: "home", areaId: "" };
  let expandedDeviceIds = new Set();
  let sliderCommitTimers = new Map();
  let lastRefreshAt = 0;
  let lastAreaRefreshAt = 0;
  let snapshotFingerprint = "";
  let statusText = "Configure Home Assistant to discover areas, entities, and scenes.";
  let lastError = "";
  let mappingWarning = "";

  if (typeof window.TfsPluginSdk?.register !== "function") {
    console.warn("TFS plugin SDK is not available yet.");
    return;
  }

  function requestRefresh() {
    try {
      activeContext?.refresh?.();
    } catch {
    }
  }

  function normalizeBaseUrl(value) {
    return String(value || "").trim().replace(/\/+$/, "");
  }

  function getDomain(entityId) {
    return String(entityId || "").split(".", 1)[0];
  }

  function getEntityName(entity) {
    return String(entity?.attributes?.friendly_name || entity?.entity_id || "Entity");
  }

  function isUnavailable(entity) {
    return !entity || entity.state === "unavailable" || entity.state === "unknown";
  }

  function isEntityOn(entity) {
    return entity?.state === "on";
  }

  function isControllableEntity(entity) {
    if (isUnavailable(entity)) {
      return false;
    }

    const domain = getDomain(entity.entity_id);
    if (toggleDomains.has(domain) || pressDomains.has(domain) || domain === "cover") {
      return true;
    }

    if (domain === "number" || domain === "input_number") {
      return Number.isFinite(Number(entity.state)) &&
        Number.isFinite(Number(entity.attributes?.min)) &&
        Number.isFinite(Number(entity.attributes?.max));
    }

    if (domain === "climate") {
      return Number.isFinite(Number(entity.attributes?.temperature));
    }

    if (domain === "select" || domain === "input_select") {
      return Array.isArray(entity.attributes?.options) && entity.attributes.options.length > 0;
    }

    return false;
  }

  function clamp(value, min, max) {
    return Math.max(min, Math.min(max, Number(value) || 0));
  }

  function roundToStep(value, step) {
    const safeStep = Number(step) > 0 ? Number(step) : 1;
    const precision = Math.max(0, (String(safeStep).split(".")[1] || "").length);
    return Number((Math.round(value / safeStep) * safeStep).toFixed(precision));
  }

  function getSection(index, title, copy) {
    return {
      index,
      title,
      copy,
      sectionKey: `home-assistant-${title.toLowerCase().replace(/[^a-z0-9]+/g, "-")}-${index}`,
    };
  }

  function formatCount(value, singular, plural = `${singular}s`) {
    const count = Number(value) || 0;
    return `${count} ${count === 1 ? singular : plural}`;
  }

  function formatLastRefresh() {
    if (!lastRefreshAt) {
      return "Not refreshed yet";
    }

    try {
      return `Updated ${new Date(lastRefreshAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
    } catch {
      return "Recently updated";
    }
  }

  function parseStates(bodyText) {
    const parsed = JSON.parse(bodyText || "[]");
    if (!Array.isArray(parsed)) {
      throw new Error("Home Assistant returned an invalid state list.");
    }

    return parsed.filter((entity) => entity && typeof entity.entity_id === "string");
  }

  function parseAreaDefinitions(bodyText) {
    const parsed = JSON.parse(String(bodyText || "[]").trim() || "[]");
    if (!Array.isArray(parsed)) {
      throw new Error("Home Assistant returned an invalid area list.");
    }

    return parsed
      .filter((area) => area && typeof area.id === "string")
      .map((area) => {
        const rawEntities = Array.isArray(area.entities)
          ? area.entities
          : Array.isArray(area.entityIds)
            ? area.entityIds.map((entityId) => ({ id: entityId }))
            : [];
        const entityRefs = [];
        const seenEntityIds = new Set();
        for (const entry of rawEntities) {
          const entityId = typeof entry === "string" ? entry : entry?.id;
          if (typeof entityId !== "string" || seenEntityIds.has(entityId)) {
            continue;
          }

          seenEntityIds.add(entityId);
          entityRefs.push({
            id: entityId,
            deviceId: typeof entry?.deviceId === "string" ? entry.deviceId : "",
            deviceName: typeof entry?.deviceName === "string" ? entry.deviceName : "",
          });
        }

        return {
          id: area.id,
          name: String(area.name || area.id),
          entityIds: entityRefs.map((entry) => entry.id),
          entityRefs,
        };
      })
      .sort((first, second) => first.name.localeCompare(second.name));
  }

  function sortEntities(first, second) {
    const firstDomainRank = domainOrder.indexOf(getDomain(first.entity_id));
    const secondDomainRank = domainOrder.indexOf(getDomain(second.entity_id));
    const normalizedFirstRank = firstDomainRank < 0 ? domainOrder.length : firstDomainRank;
    const normalizedSecondRank = secondDomainRank < 0 ? domainOrder.length : secondDomainRank;
    return normalizedFirstRank - normalizedSecondRank || getEntityName(first).localeCompare(getEntityName(second));
  }

  function buildFingerprint() {
    const visibleIds = new Set(scenes.map((scene) => scene.entity_id));
    for (const area of areas) {
      for (const entity of area.entities) {
        visibleIds.add(entity.entity_id);
      }
    }

    return JSON.stringify([
      areaDefinitions.map((area) => [
        area.id,
        area.name,
        area.entityRefs.map((entry) => [entry.id, entry.deviceId, entry.deviceName]),
      ]),
      [...visibleIds]
        .sort()
        .map((entityId) => {
          const entity = statesById.get(entityId);
          return [entityId, entity?.state || "", entity?.last_updated || "", entity?.last_changed || ""];
        }),
    ]);
  }

  function rebuildModel() {
    statesById = new Map(allStates.map((entity) => [entity.entity_id, entity]));
    scenes = allStates
      .filter((entity) => getDomain(entity.entity_id) === "scene")
      .sort((first, second) => getEntityName(first).localeCompare(getEntityName(second)));

    let definitions = areaDefinitions;
    if (!areaMappingLoaded && definitions.length === 0 && allStates.length > 0) {
      definitions = [{
        id: "__all__",
        name: "All entities",
        entityRefs: allStates
          .filter((entity) => getDomain(entity.entity_id) !== "scene")
          .map((entity) => ({ id: entity.entity_id, deviceId: "", deviceName: "" })),
      }];
    }

    areas = definitions
      .map((area) => {
        const devicesById = new Map();
        for (const entityRef of area.entityRefs || []) {
          const entity = statesById.get(entityRef.id);
          if (!isControllableEntity(entity)) {
            continue;
          }

          const deviceId = entityRef.deviceId || `entity:${entity.entity_id}`;
          const groupId = entityRef.deviceId ? `device:${deviceId}` : deviceId;
          if (!devicesById.has(groupId)) {
            devicesById.set(groupId, {
              id: groupId,
              deviceId: entityRef.deviceId || "",
              name: entityRef.deviceName || getEntityName(entity),
              entities: [],
            });
          }

          devicesById.get(groupId).entities.push(entity);
        }

        const devices = [...devicesById.values()]
          .map((device) => ({ ...device, entities: device.entities.sort(sortEntities) }))
          .sort((first, second) => first.name.localeCompare(second.name));
        return {
          id: area.id,
          name: area.name,
          devices,
          entities: devices.flatMap((device) => device.entities),
        };
      })
      .filter((area) => area.devices.length > 0);

    if (activeView.kind === "area" && !areas.some((area) => area.id === activeView.areaId)) {
      activeView = { kind: "home", areaId: "" };
    }
  }

  async function getConfiguration() {
    const [settings, secrets] = await Promise.all([
      sdk.storage.get(),
      sdk.secrets.status(),
    ]);

    return {
      baseUrl: normalizeBaseUrl(settings.baseUrl),
      hasToken: Boolean(secrets[tokenSecretKey.toLowerCase()] || secrets[tokenSecretKey]),
    };
  }

  async function refreshConfiguration() {
    const configuration = await getConfiguration();
    draftBaseUrl = configuration.baseUrl;
    hasAccessToken = configuration.hasToken;
    configurationLoaded = true;
    return configuration;
  }

  async function fetchStates(baseUrl) {
    const response = await sdk.network.get(`${baseUrl}/api/states`, {
      authorizationSecretKey: tokenSecretKey,
    });
    if (!response.ok) {
      throw new Error(`Home Assistant returned HTTP ${response.statusCode} while loading entities.`);
    }

    return parseStates(response.bodyText);
  }

  async function fetchAreas(baseUrl) {
    const response = await sdk.network.post(
      `${baseUrl}/api/template`,
      { template: areaTemplate },
      { authorizationSecretKey: tokenSecretKey },
    );
    if (!response.ok) {
      throw new Error(`Home Assistant returned HTTP ${response.statusCode} while loading areas.`);
    }

    return parseAreaDefinitions(response.bodyText);
  }

  async function refreshHomeAssistant(options = {}) {
    if (refreshPromise) {
      return refreshPromise;
    }

    refreshPromise = (async () => {
      const configuration = configurationLoaded
        ? { baseUrl: draftBaseUrl, hasToken: hasAccessToken }
        : await refreshConfiguration();
      if (!configuration.baseUrl || !configuration.hasToken) {
        allStates = [];
        areaDefinitions = [];
        areaMappingLoaded = false;
        rebuildModel();
        statusText = "Set a Home Assistant URL and long-lived access token to get started.";
        return [];
      }

      const shouldRefreshAreas = options.forceAreas === true ||
        !areaMappingLoaded ||
        Date.now() - lastAreaRefreshAt >= areaRefreshIntervalMs;
      const statePromise = fetchStates(configuration.baseUrl);
      const areaPromise = shouldRefreshAreas
        ? fetchAreas(configuration.baseUrl)
          .then((nextAreas) => ({ ok: true, value: nextAreas }))
          .catch((error) => ({ ok: false, error }))
        : Promise.resolve(null);
      const [nextStates, areaResult] = await Promise.all([statePromise, areaPromise]);

      if (areaResult?.ok) {
        areaDefinitions = areaResult.value;
        areaMappingLoaded = true;
        lastAreaRefreshAt = Date.now();
        mappingWarning = "";
      } else if (areaResult && !areaResult.ok) {
        mappingWarning = areaDefinitions.length > 0
          ? "Area discovery failed; the last known room layout is still shown."
          : "Area discovery is unavailable; controllable devices are shown in one fallback group.";
      }

      allStates = nextStates;
      rebuildModel();
      const nextFingerprint = buildFingerprint();
      const changed = nextFingerprint !== snapshotFingerprint;
      snapshotFingerprint = nextFingerprint;
      lastRefreshAt = Date.now();
      lastError = "";
      statusText = `${formatCount(areas.length, "area")} · ${formatCount(
        areas.reduce((count, area) => count + area.devices.length, 0),
        "controllable device",
      )} · ${formatCount(scenes.length, "scene")}`;

      if (changed || options.silent !== true) {
        requestRefresh();
      }

      return nextStates;
    })();

    try {
      return await refreshPromise;
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
      statusText = options.reason === "background"
        ? "Home Assistant background refresh failed."
        : "Home Assistant could not be refreshed.";
      requestRefresh();
      throw error;
    } finally {
      refreshPromise = null;
    }
  }

  function scheduleAutoRefresh() {
    const scheduleRevision = ++refreshScheduleRevision;
    cancelAutoRefresh?.();
    cancelAutoRefresh = null;
    if (typeof sdk?.lifecycle?.setTimeout !== "function") {
      return;
    }

    cancelAutoRefresh = sdk.lifecycle.setTimeout(() => {
      cancelAutoRefresh = null;
      if (scheduleRevision !== refreshScheduleRevision) {
        return;
      }

      const work = draftBaseUrl && hasAccessToken
        ? refreshHomeAssistant({ reason: "background", silent: true }).catch(() => {})
        : Promise.resolve();
      void work.finally(() => {
        if (scheduleRevision === refreshScheduleRevision) {
          scheduleAutoRefresh();
        }
      });
    }, stateRefreshIntervalMs);
  }

  function ensureInitialized() {
    if (initializationPromise || configurationLoaded) {
      return initializationPromise;
    }

    initializationPromise = refreshConfiguration()
      .then((configuration) => configuration.baseUrl && configuration.hasToken
        ? refreshHomeAssistant({ forceAreas: true, reason: "initial" })
        : null)
      .catch((error) => {
        lastError = error instanceof Error ? error.message : String(error);
      })
      .finally(() => {
        initializationPromise = null;
        scheduleAutoRefresh();
        requestRefresh();
      });
    return initializationPromise;
  }

  function resetSnapshot() {
    allStates = [];
    statesById = new Map();
    areaDefinitions = [];
    areaMappingLoaded = false;
    areas = [];
    scenes = [];
    expandedDeviceIds = new Set();
    snapshotFingerprint = "";
    lastRefreshAt = 0;
    lastAreaRefreshAt = 0;
    mappingWarning = "";
  }

  async function configure(baseUrl, accessToken) {
    await sdk.storage.patch({ baseUrl: normalizeBaseUrl(baseUrl) });
    if (accessToken) {
      await sdk.secrets.set(tokenSecretKey, accessToken);
    }

    resetSnapshot();
    await refreshConfiguration();
    activeView = { kind: "home", areaId: "" };
    const result = await refreshHomeAssistant({ forceAreas: true, reason: "configure" });
    scheduleAutoRefresh();
    return result;
  }

  async function saveConfiguration() {
    lastError = "";
    await sdk.storage.patch({ baseUrl: normalizeBaseUrl(draftBaseUrl) });
    if (draftAccessToken) {
      await sdk.secrets.set(tokenSecretKey, draftAccessToken);
      draftAccessToken = "";
    }

    resetSnapshot();
    await refreshConfiguration();
    activeView = { kind: "home", areaId: "" };
    await refreshHomeAssistant({ forceAreas: true, reason: "configure" });
    scheduleAutoRefresh();
    requestRefresh();
  }

  async function clearAccessToken() {
    await sdk.secrets.clear(tokenSecretKey);
    draftAccessToken = "";
    hasAccessToken = false;
    resetSnapshot();
    activeView = { kind: "settings", areaId: "" };
    statusText = "Access token cleared.";
    lastError = "";
    requestRefresh();
  }

  function mergeChangedStates(changedStates) {
    if (!Array.isArray(changedStates) || changedStates.length === 0) {
      return false;
    }

    const nextById = new Map(allStates.map((entity) => [entity.entity_id, entity]));
    let changed = false;
    for (const entity of changedStates) {
      if (!entity || typeof entity.entity_id !== "string") {
        continue;
      }

      nextById.set(entity.entity_id, entity);
      changed = true;
    }

    if (changed) {
      allStates = [...nextById.values()];
      rebuildModel();
      snapshotFingerprint = buildFingerprint();
      requestRefresh();
    }

    return changed;
  }

  function updateEntityOptimistically(entityId, mutate) {
    const index = allStates.findIndex((entity) => entity.entity_id === entityId);
    if (index < 0) {
      return null;
    }

    const current = allStates[index];
    const next = mutate({
      ...current,
      attributes: { ...(current.attributes || {}) },
      last_updated: new Date().toISOString(),
    });
    allStates = [...allStates.slice(0, index), next, ...allStates.slice(index + 1)];
    rebuildModel();
    snapshotFingerprint = buildFingerprint();
    requestRefresh();
    return next;
  }

  async function callService(domain, service, data) {
    if (!draftBaseUrl || !hasAccessToken) {
      throw new Error("Home Assistant is not configured.");
    }

    const response = await sdk.network.post(
      `${draftBaseUrl}/api/services/${domain}/${service}`,
      data,
      { authorizationSecretKey: tokenSecretKey },
    );
    if (!response.ok) {
      throw new Error(`Home Assistant returned HTTP ${response.statusCode} for ${domain}.${service}.`);
    }

    try {
      const changedStates = JSON.parse(response.bodyText || "[]");
      mergeChangedStates(Array.isArray(changedStates) ? changedStates : changedStates?.changed_states);
    } catch {
    }

    lastError = "";
    void refreshHomeAssistant({ reason: "command", silent: true }).catch(() => {});
    return true;
  }

  function reportCommandError(error) {
    lastError = error instanceof Error ? error.message : String(error);
    statusText = "Home Assistant command failed.";
    requestRefresh();
    void refreshHomeAssistant({ reason: "command-recovery", silent: true }).catch(() => {});
  }

  function queueSliderService(entityId, controlId, domain, service, data) {
    const key = `${entityId}::${controlId}`;
    sliderCommitTimers.get(key)?.();
    sliderCommitTimers.delete(key);
    const commit = () => {
      sliderCommitTimers.delete(key);
      void callService(domain, service, data).catch(reportCommandError);
    };

    if (typeof sdk?.lifecycle?.setTimeout === "function") {
      sliderCommitTimers.set(key, sdk.lifecycle.setTimeout(commit, sliderCommitDelayMs));
    } else {
      commit();
    }
  }

  async function setEntityPower(entityId, turnOn) {
    const entity = statesById.get(entityId);
    if (!entity) {
      throw new Error(`Entity ${entityId} is no longer available.`);
    }

    const domain = getDomain(entityId);
    const previousState = entity.state;
    updateEntityOptimistically(entityId, (next) => ({ ...next, state: turnOn ? "on" : "off" }));
    try {
      await callService(domain === "group" ? "homeassistant" : domain, turnOn ? "turn_on" : "turn_off", {
        entity_id: entityId,
      });
    } catch (error) {
      updateEntityOptimistically(entityId, (next) => ({ ...next, state: previousState }));
      throw error;
    }
  }

  async function activateScene(entityId) {
    await callService("scene", "turn_on", { entity_id: entityId });
    statusText = `${getEntityName(statesById.get(entityId))} activated.`;
    requestRefresh();
  }

  function getLightBrightness(entity) {
    return Math.round((clamp(entity?.attributes?.brightness, 0, 255) / 255) * 100);
  }

  function supportsBrightness(entity) {
    const modes = Array.isArray(entity?.attributes?.supported_color_modes)
      ? entity.attributes.supported_color_modes
      : [];
    return Number.isFinite(Number(entity?.attributes?.brightness)) || modes.some((mode) => mode !== "onoff");
  }

  function supportsColor(entity) {
    const modes = Array.isArray(entity?.attributes?.supported_color_modes)
      ? entity.attributes.supported_color_modes
      : [];
    return Array.isArray(entity?.attributes?.hs_color) || modes.some((mode) =>
      ["hs", "rgb", "rgbw", "rgbww", "xy"].includes(mode),
    );
  }

  function getHsColor(entity) {
    const color = entity?.attributes?.hs_color;
    return Array.isArray(color) && color.length >= 2
      ? [clamp(color[0], 0, 360), clamp(color[1], 0, 100)]
      : [0, 100];
  }

  function supportsColorTemperature(entity) {
    return Number.isFinite(Number(entity?.attributes?.color_temp_kelvin)) ||
      Number.isFinite(Number(entity?.attributes?.min_color_temp_kelvin)) ||
      (Number.isFinite(Number(entity?.attributes?.color_temp)) &&
        Number.isFinite(Number(entity?.attributes?.min_mireds)));
  }

  function getLightSwatch(entity) {
    if (!isEntityOn(entity)) {
      return "";
    }

    if (supportsColor(entity)) {
      const [hue, saturation] = getHsColor(entity);
      const lightness = Math.max(28, Math.round(30 + (getLightBrightness(entity) * 0.35)));
      return `hsl(${Math.round(hue)} ${Math.round(saturation)}% ${lightness}%)`;
    }

    return "#FFE49A";
  }

  function setBrightness(entityId, nextValue) {
    const entity = statesById.get(entityId);
    if (!entity || isUnavailable(entity)) {
      return;
    }

    const current = getLightBrightness(entity);
    const next = clamp(roundToStep(nextValue, 5), 0, 100);
    if (next === current) {
      return;
    }

    updateEntityOptimistically(entityId, (updated) => ({
      ...updated,
      state: next === 0 ? "off" : "on",
      attributes: { ...updated.attributes, brightness: Math.round((next / 100) * 255) },
    }));
    queueSliderService(
      entityId,
      "brightness",
      "light",
      next === 0 ? "turn_off" : "turn_on",
      next === 0 ? { entity_id: entityId } : { entity_id: entityId, brightness_pct: next },
    );
  }

  function adjustBrightness(entityId, direction) {
    const entity = statesById.get(entityId);
    if (entity) {
      setBrightness(entityId, getLightBrightness(entity) + (direction < 0 ? -5 : 5));
    }
  }

  function setLightColor(entityId, component, nextValue) {
    const entity = statesById.get(entityId);
    if (!entity || isUnavailable(entity)) {
      return;
    }

    const [currentHue, currentSaturation] = getHsColor(entity);
    const hue = component === "hue"
      ? clamp(roundToStep(nextValue, 10), 0, 360)
      : currentHue;
    const saturation = component === "saturation"
      ? clamp(roundToStep(nextValue, 5), 0, 100)
      : currentSaturation;
    if (hue === currentHue && saturation === currentSaturation) {
      return;
    }
    updateEntityOptimistically(entityId, (updated) => ({
      ...updated,
      state: "on",
      attributes: { ...updated.attributes, hs_color: [hue, saturation] },
    }));
    queueSliderService(entityId, component, "light", "turn_on", {
      entity_id: entityId,
      hs_color: [hue, saturation],
    });
  }

  function adjustLightColor(entityId, component, direction) {
    const entity = statesById.get(entityId);
    if (!entity) {
      return;
    }

    const [hue, saturation] = getHsColor(entity);
    const current = component === "hue" ? hue : saturation;
    setLightColor(entityId, component, current + (direction < 0 ? (component === "hue" ? -10 : -5) : (component === "hue" ? 10 : 5)));
  }

  function getKelvinRange(entity) {
    const minimum = Number(entity?.attributes?.min_color_temp_kelvin);
    const maximum = Number(entity?.attributes?.max_color_temp_kelvin);
    return {
      min: Number.isFinite(minimum) ? minimum : 2_000,
      max: Number.isFinite(maximum) ? maximum : 6_500,
    };
  }

  function getColorTemperatureKelvin(entity) {
    const kelvin = Number(entity?.attributes?.color_temp_kelvin);
    if (Number.isFinite(kelvin)) {
      return kelvin;
    }

    const mired = Number(entity?.attributes?.color_temp);
    return Number.isFinite(mired) && mired > 0 ? Math.round(1_000_000 / mired) : 3_500;
  }

  function setColorTemperature(entityId, nextValue) {
    const entity = statesById.get(entityId);
    if (!entity || isUnavailable(entity)) {
      return;
    }

    const range = getKelvinRange(entity);
    const current = clamp(getColorTemperatureKelvin(entity), range.min, range.max);
    const next = clamp(roundToStep(nextValue, 100), range.min, range.max);
    if (next === current) {
      return;
    }
    updateEntityOptimistically(entityId, (updated) => ({
      ...updated,
      state: "on",
      attributes: { ...updated.attributes, color_temp_kelvin: next },
    }));
    queueSliderService(entityId, "color-temperature", "light", "turn_on", {
      entity_id: entityId,
      color_temp_kelvin: next,
    });
  }

  function adjustColorTemperature(entityId, direction) {
    const entity = statesById.get(entityId);
    if (entity) {
      setColorTemperature(entityId, getColorTemperatureKelvin(entity) + (direction < 0 ? -100 : 100));
    }
  }

  function setNumberEntity(entityId, nextValue) {
    const entity = statesById.get(entityId);
    const domain = getDomain(entityId);
    if (!entity || isUnavailable(entity)) {
      return;
    }

    const min = Number.isFinite(Number(entity.attributes?.min)) ? Number(entity.attributes.min) : 0;
    const max = Number.isFinite(Number(entity.attributes?.max)) ? Number(entity.attributes.max) : 100;
    const step = Number.isFinite(Number(entity.attributes?.step)) ? Number(entity.attributes.step) : 1;
    const current = Number.isFinite(Number(entity.state)) ? Number(entity.state) : min;
    const next = clamp(roundToStep(nextValue, step), min, max);
    if (next === current) {
      return;
    }
    updateEntityOptimistically(entityId, (updated) => ({ ...updated, state: String(next) }));
    queueSliderService(entityId, "value", domain, "set_value", { entity_id: entityId, value: next });
  }

  function adjustNumberEntity(entityId, direction) {
    const entity = statesById.get(entityId);
    if (!entity) {
      return;
    }

    const step = Number.isFinite(Number(entity.attributes?.step)) ? Number(entity.attributes.step) : 1;
    const current = Number.isFinite(Number(entity.state)) ? Number(entity.state) : 0;
    setNumberEntity(entityId, current + (direction < 0 ? -step : step));
  }

  function setClimateTemperature(entityId, nextValue) {
    const entity = statesById.get(entityId);
    if (!entity || isUnavailable(entity)) {
      return;
    }

    const min = Number.isFinite(Number(entity.attributes?.min_temp)) ? Number(entity.attributes.min_temp) : 7;
    const max = Number.isFinite(Number(entity.attributes?.max_temp)) ? Number(entity.attributes.max_temp) : 35;
    const step = Number.isFinite(Number(entity.attributes?.target_temp_step))
      ? Number(entity.attributes.target_temp_step)
      : 0.5;
    const current = Number.isFinite(Number(entity.attributes?.temperature))
      ? Number(entity.attributes.temperature)
      : min;
    const next = clamp(roundToStep(nextValue, step), min, max);
    if (next === current) {
      return;
    }
    updateEntityOptimistically(entityId, (updated) => ({
      ...updated,
      attributes: { ...updated.attributes, temperature: next },
    }));
    queueSliderService(entityId, "temperature", "climate", "set_temperature", {
      entity_id: entityId,
      temperature: next,
    });
  }

  function adjustClimateTemperature(entityId, direction) {
    const entity = statesById.get(entityId);
    if (!entity) {
      return;
    }

    const step = Number.isFinite(Number(entity.attributes?.target_temp_step))
      ? Number(entity.attributes.target_temp_step)
      : 0.5;
    const current = Number.isFinite(Number(entity.attributes?.temperature))
      ? Number(entity.attributes.temperature)
      : 7;
    setClimateTemperature(entityId, current + (direction < 0 ? -step : step));
  }

  function moveSelectOption(entityId, direction) {
    const entity = statesById.get(entityId);
    const options = Array.isArray(entity?.attributes?.options) ? entity.attributes.options : [];
    if (!entity || options.length === 0 || isUnavailable(entity)) {
      return;
    }

    const currentIndex = Math.max(0, options.indexOf(entity.state));
    const nextIndex = clamp(currentIndex + (direction < 0 ? -1 : 1), 0, options.length - 1);
    const option = options[nextIndex];
    if (option === entity.state) {
      return;
    }

    updateEntityOptimistically(entityId, (updated) => ({ ...updated, state: option }));
    void callService(getDomain(entityId), "select_option", { entity_id: entityId, option }).catch(reportCommandError);
  }

  function formatEntityState(entity) {
    if (!entity) {
      return "Unknown";
    }

    const unit = entity.attributes?.unit_of_measurement;
    const translations = {
      on: "On",
      off: "Off",
      open: "Open",
      closed: "Closed",
      opening: "Opening",
      closing: "Closing",
      home: "Home",
      not_home: "Away",
      unavailable: "Unavailable",
      unknown: "Unknown",
      idle: "Idle",
      playing: "Playing",
      paused: "Paused",
      locked: "Locked",
      unlocked: "Unlocked",
    };
    const value = translations[entity.state] || String(entity.state || "Unknown").replaceAll("_", " ");
    return unit ? `${value} ${unit}` : value;
  }

  function getDomainLabel(domain) {
    const labels = {
      automation: "Automation",
      binary_sensor: "Binary sensor",
      button: "Button",
      climate: "Climate",
      cover: "Cover",
      fan: "Fan",
      humidifier: "Humidifier",
      input_boolean: "Helper",
      input_button: "Button helper",
      input_number: "Number helper",
      input_select: "Select helper",
      light: "Light",
      lock: "Lock",
      media_player: "Media player",
      number: "Number",
      person: "Person",
      remote: "Remote",
      script: "Script",
      select: "Select",
      sensor: "Sensor",
      switch: "Switch",
      vacuum: "Vacuum",
    };
    return labels[domain] || domain.replaceAll("_", " ").replace(/^./, (letter) => letter.toUpperCase());
  }

  function createLightControlSlots(entity, options = {}) {
    const titlePrefix = options.showEntityName ? `${getEntityName(entity)} · ` : "";
    const slots = [];
    slots.push(sdk.ui.createToggleSlot(
      `${titlePrefix}Power`,
      `Turn ${getEntityName(entity)} ${isEntityOn(entity) ? "off" : "on"}.`,
      isEntityOn(entity),
      () => setEntityPower(entity.entity_id, !isEntityOn(statesById.get(entity.entity_id))).catch(reportCommandError),
      {
        switchLabel: isEntityOn(entity) ? "On" : "Off",
        slotKey: `ha-power-${entity.entity_id}`,
      },
    ));

    if (supportsBrightness(entity)) {
      const brightness = getLightBrightness(entity);
      slots.push(sdk.ui.createSliderSlot(
        `${titlePrefix}Brightness`,
        brightness,
        () => adjustBrightness(entity.entity_id, -1),
        () => adjustBrightness(entity.entity_id, 1),
        {
          min: 0,
          max: 100,
          step: 5,
          valueLabel: `${brightness}%`,
          onValueChange: (nextValue) => setBrightness(entity.entity_id, nextValue),
          leftDisabled: brightness <= 0,
          rightDisabled: brightness >= 100,
          trackStyle: { background: "linear-gradient(90deg, #18202B 0%, #7C6A3B 52%, #FFF0A8 100%)" },
          fillStyle: { background: "linear-gradient(90deg, #6B5930 0%, #FFE28A 100%)" },
          thumbStyle: { background: "#FFF7D6" },
          slotKey: `ha-brightness-${entity.entity_id}`,
        },
      ));
    }

    if (supportsColor(entity)) {
      const [hue, saturation] = getHsColor(entity);
      const hueColor = `hsl(${Math.round(hue)} 100% 50%)`;
      slots.push(sdk.ui.createSliderSlot(
        `${titlePrefix}Color`,
        hue,
        () => adjustLightColor(entity.entity_id, "hue", -1),
        () => adjustLightColor(entity.entity_id, "hue", 1),
        {
          min: 0,
          max: 360,
          step: 10,
          valueLabel: `${Math.round(hue)}°`,
          onValueChange: (nextValue) => setLightColor(entity.entity_id, "hue", nextValue),
          leftDisabled: hue <= 0,
          rightDisabled: hue >= 360,
          trackStyle: { background: "linear-gradient(90deg, #FF3B30 0%, #FFD60A 17%, #34C759 34%, #32ADE6 50%, #0A84FF 67%, #AF52DE 84%, #FF3B30 100%)" },
          fillStyle: { background: "transparent" },
          thumbStyle: { background: hueColor },
          slotKey: `ha-hue-${entity.entity_id}`,
        },
      ));
      slots.push(sdk.ui.createSliderSlot(
        `${titlePrefix}Color intensity`,
        saturation,
        () => adjustLightColor(entity.entity_id, "saturation", -1),
        () => adjustLightColor(entity.entity_id, "saturation", 1),
        {
          min: 0,
          max: 100,
          step: 5,
          valueLabel: `${Math.round(saturation)}%`,
          onValueChange: (nextValue) => setLightColor(entity.entity_id, "saturation", nextValue),
          leftDisabled: saturation <= 0,
          rightDisabled: saturation >= 100,
          trackStyle: { background: `linear-gradient(90deg, #F4F4F4 0%, ${hueColor} 100%)` },
          fillStyle: { background: "transparent" },
          thumbStyle: { background: `hsl(${Math.round(hue)} ${Math.round(saturation)}% 50%)` },
          slotKey: `ha-saturation-${entity.entity_id}`,
        },
      ));
    }

    if (supportsColorTemperature(entity)) {
      const range = getKelvinRange(entity);
      const temperature = clamp(getColorTemperatureKelvin(entity), range.min, range.max);
      slots.push(sdk.ui.createSliderSlot(
        `${titlePrefix}White temperature`,
        temperature,
        () => adjustColorTemperature(entity.entity_id, -1),
        () => adjustColorTemperature(entity.entity_id, 1),
        {
          min: range.min,
          max: range.max,
          step: 100,
          valueLabel: `${Math.round(temperature)} K`,
          onValueChange: (nextValue) => setColorTemperature(entity.entity_id, nextValue),
          leftDisabled: temperature <= range.min,
          rightDisabled: temperature >= range.max,
          trackStyle: { background: "linear-gradient(90deg, #FFB45C 0%, #FFF2CE 50%, #BFD9FF 100%)" },
          fillStyle: { background: "transparent" },
          thumbStyle: { background: "#FFF4DC" },
          slotKey: `ha-temperature-${entity.entity_id}`,
        },
      ));
    }

    return slots;
  }

  function createEntityControlSlots(entity, options = {}) {
    const domain = getDomain(entity.entity_id);
    if (domain === "light") {
      return createLightControlSlots(entity, options);
    }

    if (toggleDomains.has(domain)) {
      return [sdk.ui.createToggleSlot(
        getEntityName(entity),
        `${getDomainLabel(domain)} · ${formatEntityState(entity)}`,
        isEntityOn(entity),
        () => setEntityPower(entity.entity_id, !isEntityOn(statesById.get(entity.entity_id))).catch(reportCommandError),
        {
          switchLabel: isEntityOn(entity) ? "On" : "Off",
          slotKey: `ha-entity-${entity.entity_id}`,
        },
      )];
    }

    if (pressDomains.has(domain)) {
      const service = domain === "script" ? "turn_on" : "press";
      return [sdk.ui.createCommandSlot(
        getEntityName(entity),
        domain === "script" ? "Run this Home Assistant script." : "Press this Home Assistant button.",
        () => callService(domain, service, { entity_id: entity.entity_id }).catch(reportCommandError),
        {
          badge: domain === "script" ? "Run" : "Press",
          slotKey: `ha-entity-${entity.entity_id}`,
        },
      )];
    }

    if (domain === "number" || domain === "input_number") {
      const min = Number.isFinite(Number(entity.attributes?.min)) ? Number(entity.attributes.min) : 0;
      const max = Number.isFinite(Number(entity.attributes?.max)) ? Number(entity.attributes.max) : 100;
      const step = Number.isFinite(Number(entity.attributes?.step)) ? Number(entity.attributes.step) : 1;
      const value = clamp(entity.state, min, max);
      const unit = entity.attributes?.unit_of_measurement || "";
      return [sdk.ui.createSliderSlot(
        getEntityName(entity),
        value,
        () => adjustNumberEntity(entity.entity_id, -1),
        () => adjustNumberEntity(entity.entity_id, 1),
        {
          min,
          max,
          step,
          valueLabel: `${value}${unit ? ` ${unit}` : ""}`,
          onValueChange: (nextValue) => setNumberEntity(entity.entity_id, nextValue),
          leftDisabled: value <= min,
          rightDisabled: value >= max,
          trackStyle: { background: "linear-gradient(90deg, #273746 0%, #3A9AD9 100%)" },
          fillStyle: { background: "#3A9AD9" },
          thumbStyle: { background: "#D9F0FF" },
          slotKey: `ha-entity-${entity.entity_id}`,
        },
      )];
    }

    if (domain === "climate" && Number.isFinite(Number(entity.attributes?.temperature))) {
      const min = Number.isFinite(Number(entity.attributes?.min_temp)) ? Number(entity.attributes.min_temp) : 7;
      const max = Number.isFinite(Number(entity.attributes?.max_temp)) ? Number(entity.attributes.max_temp) : 35;
      const step = Number.isFinite(Number(entity.attributes?.target_temp_step)) ? Number(entity.attributes.target_temp_step) : 0.5;
      const value = clamp(entity.attributes.temperature, min, max);
      const unit = entity.attributes?.temperature_unit || "°C";
      return [sdk.ui.createSliderSlot(
        getEntityName(entity),
        value,
        () => adjustClimateTemperature(entity.entity_id, -1),
        () => adjustClimateTemperature(entity.entity_id, 1),
        {
          min,
          max,
          step,
          valueLabel: `${value}${unit}`,
          onValueChange: (nextValue) => setClimateTemperature(entity.entity_id, nextValue),
          leftDisabled: value <= min,
          rightDisabled: value >= max,
          trackStyle: { background: "linear-gradient(90deg, #3A8DDE 0%, #E8D35E 52%, #E05A47 100%)" },
          fillStyle: { background: "transparent" },
          thumbStyle: { background: "#FFF2C6" },
          slotKey: `ha-entity-${entity.entity_id}`,
        },
      )];
    }

    if ((domain === "select" || domain === "input_select") && Array.isArray(entity.attributes?.options)) {
      const options = entity.attributes.options;
      const index = Math.max(0, options.indexOf(entity.state));
      return [sdk.ui.createInlineStepperSlot(
        getEntityName(entity),
        String(entity.state || "Select an option"),
        () => moveSelectOption(entity.entity_id, -1),
        () => moveSelectOption(entity.entity_id, 1),
        {
          leftDisabled: index <= 0,
          rightDisabled: index >= options.length - 1,
          slotKey: `ha-entity-${entity.entity_id}`,
        },
      )];
    }

    if (domain === "cover") {
      const closed = entity.state === "closed" || entity.state === "closing";
      return [sdk.ui.createInlineStepperSlot(
        getEntityName(entity),
        `${formatEntityState(entity)} · Close / Open`,
        () => {
          updateEntityOptimistically(entity.entity_id, (updated) => ({ ...updated, state: "closing" }));
          void callService("cover", "close_cover", { entity_id: entity.entity_id }).catch(reportCommandError);
        },
        () => {
          updateEntityOptimistically(entity.entity_id, (updated) => ({ ...updated, state: "opening" }));
          void callService("cover", "open_cover", { entity_id: entity.entity_id }).catch(reportCommandError);
        },
        {
          leftDisabled: closed,
          rightDisabled: !closed && entity.state === "open",
          slotKey: `ha-entity-${entity.entity_id}`,
        },
      )];
    }

    return [];
  }

  function createDeviceSlots(device) {
    const expanded = expandedDeviceIds.has(device.id);
    const light = device.entities.find((entity) => getDomain(entity.entity_id) === "light");
    const activeCount = device.entities.filter((entity) =>
      toggleDomains.has(getDomain(entity.entity_id)) && isEntityOn(entity),
    ).length;
    const header = sdk.ui.createAccordionSlot(
      device.name,
      `${formatCount(device.entities.length, "control")} · ${expanded ? "Hide settings" : "Show settings"}`,
      expanded,
      () => {
        if (expanded) {
          expandedDeviceIds.delete(device.id);
        } else {
          expandedDeviceIds.add(device.id);
        }
        requestRefresh();
      },
      {
        badge: activeCount ? "On" : "Ready",
        slotKey: `ha-device-${device.id}`,
        swatchHex: light ? getLightSwatch(light) : "",
        swatchLabel: light && supportsColor(light) ? "Current color" : "",
      },
    );

    if (!expanded) {
      return [header];
    }

    const showEntityName = device.entities.filter((entity) => getDomain(entity.entity_id) === "light").length > 1;
    return [
      header,
      ...device.entities.flatMap((entity) => createEntityControlSlots(entity, { showEntityName })),
    ];
  }

  function openView(kind, areaId = "") {
    activeView = { kind, areaId };
    requestRefresh();
  }

  function moveArea(direction) {
    if (areas.length === 0) {
      openView("home");
      return;
    }

    const currentIndex = Math.max(0, areas.findIndex((area) => area.id === activeView.areaId));
    const nextIndex = clamp(currentIndex + (direction < 0 ? -1 : 1), 0, areas.length - 1);
    openView("area", areas[nextIndex].id);
  }

  function createHomeScreen() {
    const populatedDeviceCount = areas.reduce((count, area) => count + area.devices.length, 0);
    const areaSlots = areas.map((area) => {
      const activeCount = area.entities.filter((entity) => toggleDomains.has(getDomain(entity.entity_id)) && isEntityOn(entity)).length;
      return sdk.ui.createNavigationSlot(
        area.name,
        `${formatCount(area.devices.length, "controllable device")}${activeCount ? ` · ${activeCount} active` : ""}`,
        () => openView("area", area.id),
        {
          badge: String(area.devices.length),
          slotKey: `ha-area-${area.id}`,
        },
      );
    });

    if (areaSlots.length === 0) {
      areaSlots.push(sdk.ui.createCommandSlot(
        refreshPromise ? "Loading areas…" : "No populated areas found",
        refreshPromise
          ? "Home Assistant rooms and controllable devices are being discovered."
          : "No reachable device with supported controls is assigned to a Home Assistant area.",
        () => {},
        { disabled: true, slotKey: "ha-no-areas" },
      ));
    }

    const sceneIndex = areaSlots.length;
    const maintenanceIndex = sceneIndex + 1;
    const slots = [
      ...areaSlots,
      sdk.ui.createNavigationSlot(
        "Scenes",
        scenes.length
          ? `${formatCount(scenes.length, "scene")} ready to activate.`
          : "No Home Assistant scenes were found yet.",
        () => openView("scenes"),
        { badge: String(scenes.length), slotKey: "ha-scenes" },
      ),
      sdk.ui.createCommandSlot(
        "Refresh now",
        "Reload areas, entities, and scenes immediately.",
        () => refreshHomeAssistant({ forceAreas: true, reason: "manual" }).catch(() => {}),
        { badge: refreshPromise ? "Updating" : "Refresh", disabled: Boolean(refreshPromise), slotKey: "ha-refresh" },
      ),
      sdk.ui.createNavigationSlot(
        "Connection settings",
        "Change the Home Assistant address or long-lived access token.",
        () => openView("settings"),
        { badge: "Settings", slotKey: "ha-settings" },
      ),
    ];

    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Areas, entities, and scenes",
      note: mappingWarning || `${statusText} · ${formatLastRefresh()} · Auto-refresh every 8 seconds.`,
      error: lastError,
      cards: [
        {
          title: "Home overview",
          lines: [
            formatCount(areas.length, "populated area"),
            formatCount(populatedDeviceCount, "controllable device"),
            formatCount(scenes.length, "scene"),
            "Empty, unavailable, and read-only devices stay hidden automatically.",
          ],
        },
      ],
      sectionHeaders: [
        getSection(0, "Areas", "Only rooms that contain reachable devices with supported controls are shown."),
        getSection(sceneIndex, "Scenes", "Activate a complete Home Assistant scene with one press."),
        getSection(maintenanceIndex, "Maintenance", "Refresh manually or update the connection."),
      ],
      slots,
      footerLegend: "A Open / Activate   Left / Right Adjust   B Back",
    });
  }

  function createAreaScreen() {
    const area = areas.find((entry) => entry.id === activeView.areaId);
    if (!area) {
      activeView = { kind: "home", areaId: "" };
      return createHomeScreen();
    }

    const areaIndex = areas.findIndex((entry) => entry.id === area.id);
    const deviceSlots = area.devices.flatMap(createDeviceSlots);
    const slots = [
      sdk.ui.createInlineStepperSlot(
        "Area",
        `${areaIndex + 1} / ${areas.length} · ${area.name}`,
        () => moveArea(-1),
        () => moveArea(1),
        {
          leftDisabled: areaIndex <= 0,
          rightDisabled: areaIndex >= areas.length - 1,
          slotKey: "ha-area-stepper",
        },
      ),
      ...deviceSlots,
    ];

    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: area.name,
      note: `${formatCount(area.devices.length, "controllable device")} · Expand a device to see only its available settings.`,
      error: lastError,
      topSlots: [sdk.ui.createBackSlot(
        "All areas",
        "Return to the Home Assistant overview.",
        () => openView("home"),
        { slotKey: "ha-area-back" },
      )],
      cards: [],
      sectionHeaders: [
        getSection(0, "Area", "Move between populated Home Assistant areas."),
        getSection(1, "Devices", "Only controllable devices are listed; every setting stays inside its device."),
      ],
      slots,
      footerLegend: "A Expand / Toggle   Left / Right Adjust   B All Areas",
    });
  }

  function createScenesScreen() {
    const sceneSlots = scenes.length
      ? scenes.map((scene) => {
          const targetCount = Array.isArray(scene.attributes?.entity_id) ? scene.attributes.entity_id.length : 0;
          return sdk.ui.createCommandSlot(
            getEntityName(scene),
            targetCount ? `${formatCount(targetCount, "target entity", "target entities")} in this scene.` : "Activate this Home Assistant scene.",
            () => activateScene(scene.entity_id).catch(reportCommandError),
            { badge: "Activate", slotKey: `ha-scene-${scene.entity_id}` },
          );
        })
      : [sdk.ui.createCommandSlot(
          "No scenes found",
          "Create a scene in Home Assistant, then refresh this plugin.",
          () => {},
          { disabled: true, slotKey: "ha-no-scenes" },
        )];

    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Scenes",
      note: scenes.length
        ? "Scenes apply several saved Home Assistant states at once."
        : "No scene entities are currently available.",
      error: lastError,
      topSlots: [sdk.ui.createBackSlot(
        "All areas",
        "Return to the Home Assistant overview.",
        () => openView("home"),
        { slotKey: "ha-scenes-back" },
      )],
      cards: [{
        title: "Scene library",
        lines: [formatCount(scenes.length, "scene"), "Select a scene once to activate it immediately."],
      }],
      sectionHeaders: [getSection(0, "Scenes", "Activate saved lighting and device arrangements.")],
      slots: sceneSlots,
      footerLegend: "A Activate Scene   B All Areas",
    });
  }

  function createConfigurationScreen() {
    const configured = Boolean(draftBaseUrl && hasAccessToken);
    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Connection settings",
      note: configured
        ? "The token remains protected by TFS. Save to reconnect and rediscover areas."
        : "Enter the local Home Assistant address and a long-lived access token.",
      error: lastError,
      topSlots: configured
        ? [sdk.ui.createBackSlot(
            "Home Assistant",
            "Return without changing the connection.",
            () => openView("home"),
            { slotKey: "ha-settings-back" },
          )]
        : [],
      cards: [{
        title: "Connection",
        lines: [
          `Address: ${draftBaseUrl || "Not configured"}`,
          hasAccessToken ? "Access token is stored securely." : "Access token is missing.",
          "Home Assistant must be reachable from this PC on the local network.",
        ],
      }],
      editors: [
        {
          inputKey: "home-assistant-base-url",
          label: "Home Assistant URL",
          help: "Example: http://homeassistant.local:8123",
          value: draftBaseUrl,
          placeholder: "http://homeassistant.local:8123",
          rows: 1,
          onInput: (value) => {
            draftBaseUrl = value;
          },
        },
        sdk.ui.createSecretEditor({
          inputKey: "home-assistant-access-token",
          label: "Long-Lived Access Token",
          configured: hasAccessToken,
          onInput: (value) => {
            draftAccessToken = value;
          },
        }),
      ],
      sectionHeaders: [getSection(0, "Connection", "Save and verify the Home Assistant REST connection.")],
      slots: [
        sdk.ui.createCommandSlot(
          "Save and connect",
          "Store the URL and token, then discover areas, entities, and scenes.",
          () => saveConfiguration().catch(reportCommandError),
          { badge: hasAccessToken ? "Reconnect" : "Connect", disabled: Boolean(refreshPromise), slotKey: "ha-save" },
        ),
        sdk.ui.createCommandSlot(
          "Clear access token",
          "Remove the protected token while keeping the Home Assistant URL.",
          () => clearAccessToken().catch(reportCommandError),
          { disabled: !hasAccessToken, slotKey: "ha-clear-token" },
        ),
      ],
      footerLegend: "A Edit / Save   B Back",
    });
  }

  function createLoadingScreen() {
    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Loading connection",
      note: "Reading the protected Home Assistant configuration.",
      error: lastError,
      slots: [sdk.ui.createCommandSlot(
        "Loading Home Assistant…",
        "The integration will open automatically when the connection is ready.",
        () => {},
        { disabled: true },
      )],
    });
  }

  function createScreen(context = {}) {
    activeContext = context;
    if (!configurationLoaded) {
      void ensureInitialized();
      return createLoadingScreen();
    }

    if (!cancelAutoRefresh) {
      scheduleAutoRefresh();
    }

    if (!draftBaseUrl || !hasAccessToken || activeView.kind === "settings") {
      return createConfigurationScreen();
    }

    if (activeView.kind === "area") {
      return createAreaScreen();
    }

    if (activeView.kind === "scenes") {
      return createScenesScreen();
    }

    return createHomeScreen();
  }

  window.TfsPluginSdk.register(manifest, (registeredSdk) => {
    sdk = registeredSdk;
    sdk.lifecycle?.onDispose?.(() => {
      cancelAutoRefresh?.();
      cancelAutoRefresh = null;
      for (const cancel of sliderCommitTimers.values()) {
        cancel?.();
      }
      sliderCommitTimers.clear();
    });

    return {
      configure,
      refresh: (options = {}) => refreshHomeAssistant({ forceAreas: true, ...options }),
      refreshLights: () => refreshHomeAssistant().then(() => allStates.filter((entity) => getDomain(entity.entity_id) === "light")),
      setLight: setEntityPower,
      activateScene,
      createScreen,
      getSnapshot: () => ({
        areas,
        scenes,
        statusText,
        lastRefreshAt,
      }),
    };
  });
})();
