# TFS Plugin Template

This template shows the smallest useful Tools for Steam community plugin.

Build output should be zipped with `tfs-plugin.json` at the package root:

```text
tfs-plugin.json
dist/index.js
assets/preview.png
```

Store listings require a preview image. Keep it readable at controller distance and use PNG, JPG, WEBP, GIF, or SVG.
Catalog entries also require a SHA-256 checksum for the final zip. Packages without a checksum are blocked by the Store.

Use `window.TfsPluginSdk.create(manifest)` from `dist/index.js`. The SDK provides shared UI helpers plus optional `storage`, `secrets`, and `network` helpers when the manifest declares the matching permissions.

Register synchronously:

```js
window.ToolsForSteamCommunityPlugins ??= {};
window.ToolsForSteamCommunityPlugins[manifest.id] = {
  manifest,
  createScreen(context) {
    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Community Plugin",
      slots: [],
    });
  },
};
```

`createScreen(context)` must return a screen model immediately. Run async work inside commands or background promises, then call `context.refresh()` when the UI should redraw.

Keep packages small and predictable: the Store rejects zips larger than 64 MB, more than 512 files, files larger than 32 MB, or entries that escape the package root.
