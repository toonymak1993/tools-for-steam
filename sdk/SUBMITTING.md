# Submitting A Tools for Steam Plugin

Tools for Steam community plugins are normal zip packages plus a catalog entry. The app never gives a community plugin direct core access; plugins use the SDK surface declared by their manifest permissions.

## Recommended Developer Flow

1. Fork `https://github.com/toonymak1993/tfs-plugin-template`.
2. Build your plugin in that fork or in your own repository.
3. Create `tfs-plugin.json` at the package root.
4. Build your frontend entry point, normally `dist/index.js`.
5. Add a required store image, normally `assets/preview.png`.
6. Zip the package with `tfs-plugin.json` at the zip root.
7. Compute the SHA-256 hash of the zip.
8. Add or update a catalog entry in `https://github.com/toonymak1993/tfs-plugin-database/blob/main/catalog.json`.
9. Open a pull request against `tfs-plugin-database` and explain what the plugin does and why each permission is needed.

## Minimal Package

```text
my-plugin.zip
  tfs-plugin.json
  dist/
    index.js
  assets/
    preview.png
```

## Minimal Manifest

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "description": "A controller-friendly Tools for Steam plugin.",
  "sdkVersion": "1.0.0",
  "entryPoint": "dist/index.js",
  "permissions": ["frontend"]
}
```

## Minimal Entry Point

```js
(function () {
  const manifest = {
    id: "my-plugin",
    name: "My Plugin",
    version: "1.0.0",
  };
  const sdk = window.TfsPluginSdk.create(manifest);

  window.ToolsForSteamCommunityPlugins ??= {};
  window.ToolsForSteamCommunityPlugins[manifest.id] = {
    manifest,
    createScreen(context) {
      return sdk.ui.createScreenModel({
        title: manifest.name,
        subtitle: "Community Plugin",
        slots: [
          sdk.ui.createCommandSlot("Refresh", "Redraw this plugin.", () => {
            context.refresh();
          }),
        ],
      });
    },
  };
})();
```

## Catalog Entry

```json
{
  "id": "my-plugin",
  "title": "My Plugin",
  "description": "A controller-friendly Tools for Steam plugin.",
  "author": "Your Name",
  "category": "Utility",
  "version": "1.0.0",
  "packageUrl": "https://example.com/my-plugin.zip",
  "packageSha256": "64_HEX_CHARACTERS",
  "images": ["https://example.com/my-plugin.png"],
  "tags": ["sdk-v1"],
  "repositoryUrl": "https://github.com/you/my-plugin"
}
```

`images` is required. Plugins without an image are not listed in the store.

## Permission Review

Use the smallest permission set possible:

- `frontend`: required for a visible Quick Access plugin.
- `storage`: stores public per-plugin JSON settings.
- `secrets`: stores write-only tokens or passwords through the core.
- `network`: sends HTTP/HTTPS requests through the core network proxy.

If a plugin declares `secrets` or `network`, the pull request should explain what service it connects to and why the permission is needed.

## Local Test

For the official Home Assistant example:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 -ImagePath "$env:USERPROFILE\Downloads\hassio.png"
```

Then open the TFS Store, refresh the catalog, install the plugin, and verify that the plugin appears on the Home screen without restarting Tools for Steam.

To update the live official catalog, clone `tfs-plugin-database` and pass its path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 `
  -ImagePath "$env:USERPROFILE\Downloads\hassio.png" `
  -PluginDatabaseRoot "C:\path\to\tfs-plugin-database"
```
