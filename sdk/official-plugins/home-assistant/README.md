# Home Assistant Community Plugin

This is the first official SDK-based community plugin example for Tools for Steam.

It deliberately uses only public SDK capabilities:

- `storage` stores the Home Assistant base URL.
- `secrets` stores the long-lived access token without exposing it back to JavaScript.
- `network` calls the Home Assistant REST API through the TFS core proxy.
- `frontend` registers a controller-friendly screen model for the future dynamic loader.

The Home Assistant REST API is documented at https://developers.home-assistant.io/docs/api/rest/.

## Current Scope

The first version is intentionally small:

- configure the Home Assistant URL from Quick Access
- enter or replace the long-lived access token through a password-style secret editor
- list entities whose `entity_id` starts with `light.`
- show the current on/off state
- call `light.turn_on` or `light.turn_off`

Brightness, color, areas, scenes, and WebSocket live updates should be added in later SDK iterations.

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
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 -ImagePath "$env:USERPROFILE\Downloads\hassio.png"
```

This writes the installable package to `sdk/packages/home-assistant.zip`, updates the local development catalog, and copies the preview image to `sdk/images/home-assistant.png`.

To publish the official store entry, pass a local checkout of `tfs-plugin-database`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 `
  -ImagePath "$env:USERPROFILE\Downloads\hassio.png" `
  -PluginDatabaseRoot "C:\path\to\tfs-plugin-database"
```

## Manual Setup During Development

The plugin includes a Quick Access configuration screen. It also exposes a small `configure` helper for quick console testing:

```js
await window.ToolsForSteamCommunityPlugins["home-assistant"].configure(
  "http://homeassistant.local:8123",
  "long-lived-access-token"
);
```
