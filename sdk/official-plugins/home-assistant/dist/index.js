(() => {
  const manifest = {
    id: "home-assistant",
    name: "Home Assistant",
    version: "0.2.0",
    sdkVersion: "1.0.0",
    permissions: ["frontend", "storage", "secrets", "network"],
    networkHosts: ["<local>"],
  };
  const tokenSecretKey = "accessToken";
  let sdk = null;

  if (typeof window.TfsPluginSdk?.register !== "function") {
    console.warn("TFS plugin SDK is not available yet.");
    return;
  }

  let statusText = "Configure Home Assistant to load light entities.";
  let lights = [];
  let lastError = "";
  let configurationLoaded = false;
  let draftBaseUrl = "";
  let draftAccessToken = "";
  let hasAccessToken = false;
  let editingConfiguration = false;
  let activeContext = null;

  function requestRefresh() {
    try {
      activeContext?.refresh?.();
    } catch {
    }
  }

  function normalizeBaseUrl(value) {
    return String(value || "").trim().replace(/\/+$/, "");
  }

  function parseLightStates(bodyText) {
    const states = JSON.parse(bodyText || "[]");
    if (!Array.isArray(states)) {
      return [];
    }

    return states
      .filter((entity) => String(entity?.entity_id || "").startsWith("light."))
      .map((entity) => ({
        id: entity.entity_id,
        name: entity.attributes?.friendly_name || entity.entity_id,
        isOn: entity.state === "on",
      }))
      .sort((first, second) => first.name.localeCompare(second.name));
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

  async function configure(baseUrl, accessToken) {
    await sdk.storage.patch({ baseUrl: normalizeBaseUrl(baseUrl) });
    if (accessToken) {
      await sdk.secrets.set(tokenSecretKey, accessToken);
    }

    await refreshConfiguration();
    return refreshLights();
  }

  async function saveConfiguration() {
    lastError = "";
    await sdk.storage.patch({ baseUrl: normalizeBaseUrl(draftBaseUrl) });
    if (draftAccessToken) {
      await sdk.secrets.set(tokenSecretKey, draftAccessToken);
      draftAccessToken = "";
    }

    await refreshConfiguration();
    await refreshLights();
    editingConfiguration = false;
    requestRefresh();
  }

  async function clearAccessToken() {
    lastError = "";
    await sdk.secrets.clear(tokenSecretKey);
    draftAccessToken = "";
    hasAccessToken = false;
    lights = [];
    statusText = "Access token cleared.";
    editingConfiguration = true;
    requestRefresh();
  }

  async function refreshLights() {
    lastError = "";
    const configuration = await refreshConfiguration();
    if (!configuration.baseUrl || !configuration.hasToken) {
      lights = [];
      statusText = "Set a Home Assistant URL and access token before loading lights.";
      return lights;
    }

    const response = await sdk.network.get(`${configuration.baseUrl}/api/states`, {
      authorizationSecretKey: tokenSecretKey,
    });

    if (!response.ok) {
      throw new Error(`Home Assistant returned HTTP ${response.statusCode}.`);
    }

    lights = parseLightStates(response.bodyText);
    statusText = lights.length === 0
      ? "Connected, but no light entities were found."
      : `${lights.length} light${lights.length === 1 ? "" : "s"} loaded from Home Assistant.`;
    return lights;
  }

  async function setLight(entityId, turnOn) {
    const configuration = await getConfiguration();
    if (!configuration.baseUrl || !configuration.hasToken) {
      throw new Error("Home Assistant is not configured.");
    }

    const service = turnOn ? "turn_on" : "turn_off";
    const response = await sdk.network.post(
      `${configuration.baseUrl}/api/services/light/${service}`,
      { entity_id: entityId },
      { authorizationSecretKey: tokenSecretKey },
    );

    if (!response.ok) {
      throw new Error(`Home Assistant returned HTTP ${response.statusCode}.`);
    }

    await refreshLights();
    requestRefresh();
    return true;
  }

  function createConfigurationScreen() {
    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Community Plugin",
      note: statusText,
      error: lastError,
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
      slots: [
        sdk.ui.createCommandSlot(
          "Save Configuration",
          "Store the URL and write the token as a protected TFS secret.",
          () => saveConfiguration().catch((error) => {
            lastError = error.message || String(error);
            requestRefresh();
          }),
          { badge: hasAccessToken ? "Update" : "Save" },
        ),
        sdk.ui.createCommandSlot(
          "Clear Token",
          "Remove the stored token without changing the Home Assistant URL.",
          () => clearAccessToken().catch((error) => {
            lastError = error.message || String(error);
            requestRefresh();
          }),
          { disabled: !hasAccessToken },
        ),
      ],
      footerLegend: "A Edit / Save   B Back",
    });
  }

  function createLightsScreen() {
    const slots = [
      sdk.ui.createCommandSlot(
        "Refresh lights",
        "Load current light states from Home Assistant.",
        () => refreshLights().catch((error) => {
          lastError = error.message || String(error);
          statusText = "Refresh failed.";
          requestRefresh();
        }),
        { badge: "REST" },
      ),
      ...lights.map((light) =>
        sdk.ui.createCommandSlot(
          light.name,
          `${light.id} is currently ${light.isOn ? "on" : "off"}.`,
          () => setLight(light.id, !light.isOn).catch((error) => {
            lastError = error.message || String(error);
            statusText = "Light command failed.";
          }),
          { badge: light.isOn ? "On" : "Off" },
        ),
      ),
      sdk.ui.createCommandSlot(
        "Change Configuration",
        "Edit the Home Assistant URL or replace the stored token.",
        () => {
          editingConfiguration = true;
          statusText = "Update Home Assistant connection settings.";
          requestRefresh();
        },
        { badge: "Settings" },
      ),
    ];

    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Community Plugin",
      note: statusText,
      error: lastError,
      slots,
      footerLegend: "A Toggle / Refresh   B Back",
    });
  }

  function createScreen(context = {}) {
    activeContext = context;
    if (!configurationLoaded) {
      void refreshConfiguration()
        .then(() => {
          if (draftBaseUrl && hasAccessToken) {
            return refreshLights();
          }

          return null;
        })
        .catch((error) => {
          lastError = error.message || String(error);
        })
        .finally(() => requestRefresh());
    }

    if (editingConfiguration || !draftBaseUrl || !hasAccessToken) {
      return createConfigurationScreen();
    }

    return createLightsScreen();
  }

  window.TfsPluginSdk.register(manifest, (registeredSdk) => {
    sdk = registeredSdk;
    return {
      configure,
      refreshLights,
      setLight,
      createScreen,
    };
  });
})();
