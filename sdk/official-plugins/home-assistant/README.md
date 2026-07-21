# Home Assistant Community Plugin

The official Home Assistant community plugin provides a controller-friendly smart-home dashboard inside Tools for Steam. It uses only public SDK capabilities and remains disabled until the user installs and enables it from the plugin store.

## Features

- discovers Home Assistant areas and hides rooms without configurable devices automatically
- groups supported entities by their Home Assistant device and keeps every control inside that device's accordion
- hides sensors, read-only entities, unsupported domains, and unavailable devices instead of rendering disabled rows
- gives lights directly draggable Homey-style brightness, hue, saturation, and white-temperature sliders with matching color gradients and no visible arrow buttons
- supports toggles, buttons, scripts, covers, selects, number helpers, and climate target temperatures
- provides a dedicated scene library with one-press activation
- refreshes state changes every eight seconds in the background and refreshes the area layout every minute
- updates slider values optimistically and debounces Home Assistant service calls for smooth pointer, touch, and controller input
- stores the Home Assistant URL in plugin storage and the long-lived access token as a protected TFS secret

The public SDK currently exposes HTTP networking rather than raw WebSockets. The integration therefore uses adaptive REST polling for automatic updates. Area discovery is provided by Home Assistant's `/api/template` endpoint with the official `areas()`, `area_name()`, and `area_entities()` template functions; state and service actions use the documented REST API.

## Package Layout

```text
home-assistant.zip
  tfs-plugin.json
  dist/
    index.js
  assets/
    preview.png
```

## Build

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 -PluginId home-assistant
```

This writes the installable package to `sdk/packages/home-assistant.zip`, updates the local development catalog, and reuses the bundled preview image.

To publish the official store entry, pass a local checkout of `tfs-plugin-database`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 `
  -PluginId home-assistant `
  -PluginDatabaseRoot "C:\path\to\tfs-plugin-database"
```

## Manual Setup During Development

The plugin includes its own Quick Access connection screen. It also retains a small helper for console testing:

```js
await window.ToolsForSteamCommunityPlugins["home-assistant"].configure(
  "http://homeassistant.local:8123",
  "long-lived-access-token"
);
```

Home Assistant REST API: https://developers.home-assistant.io/docs/api/rest/

Home Assistant template functions: https://www.home-assistant.io/template-functions/
