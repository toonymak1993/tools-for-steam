# TFS Plugin Template

This template shows the smallest useful Tools for Steam community plugin.

Build output should be zipped with `tfs-plugin.json` at the package root:

```text
tfs-plugin.json
dist/index.js
assets/preview.png
```

Store preview images are recommended. Without one, the Store renders a controller-readable fallback card. Use PNG, JPG, WEBP, GIF, or SVG.
Catalog entries also require a SHA-256 checksum for the final zip. Packages without a checksum are blocked by the Store.

Use `window.TfsPluginSdk.register(manifest, setup)` from `dist/index.js`. The SDK provides shared UI helpers plus optional `storage`, `secrets`, `network`, private `files`, in-Steam `notifications`, structured `logging`, native gaming bridges, and managed lifecycle helpers.

Before packaging, run `scripts\tfs-plugin.ps1 validate <plugin-path>`. Network plugins must declare exact `networkHosts`; native performance and confirmed power actions use `native.performance` and `native.power`.

Register synchronously:

```js
window.TfsPluginSdk.register(manifest, (sdk) => ({
  createScreen(context) {
    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: "Community Plugin",
      slots: [],
    });
  },
}));
```

`createScreen(context)` must return a screen model immediately. Run async work inside commands or background promises, then call `context.refresh()` when the UI should redraw.

Keep packages predictable: the Store rejects zips larger than 256 MB, more than 2,048 files, files larger than 256 MB, or entries that escape the package root. Use `full-trust-plugin-template/` when the plugin needs a managed native backend.
