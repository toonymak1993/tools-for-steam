# Tools for Steam 0.3.9

Tools for Steam 0.3.9 introduces the first public TFS Plugin SDK contract and finishes the controller-first Community Store experience.

## Highlights

- Reworked Store layout with compact, consistently sized cards, safe inner spacing, fixed footer separation, responsive 3/2/1-column grids, contained preview images, and stable controller focus.
- Community grids stay left-aligned when fewer than three plugins are available.
- Added full Store sections for discovery, built-ins, community plugins, installed plugins, and updates.
- Added curated-catalog trust disclosure and a visible warning for custom developer catalogs.
- Updates that request new permissions or network hosts now require a controller-friendly confirmation.

## Plugin SDK 1.0.0

- Managed registration and cleanup through `TfsPluginSdk.register()`.
- Controller-native screens, commands, toggles, choices, accordions, steppers, sliders, progress, and secret editors.
- Per-plugin settings, write-only secrets, private text/binary files, structured logs, notifications, lifecycle timers, and observable state.
- Proxied HTTP methods with declared host access and protected authorization-secret injection.
- Native bridges for audio, windows/process activation, display modes, themes, artwork, App Start, Store Sync, automation, performance telemetry, overlay control, and confirmed power actions.
- TypeScript declarations, JSON schemas, a PowerShell create/validate/pack CLI, a complete developer guide, a minimal template, and the Home Assistant reference plugin.
- Explicit feature-level compatibility: this runtime accepts SDK `1.0.0` and rejects plugins that require a newer v1 runtime.
- Decky-style `native.full-trust` plugins can bundle executable, PowerShell, Python, or Node backends, use managed JSON RPC, run arbitrary programs, access arbitrary filesystem paths, open shell targets, and evaluate or inject code on selected Steam CEF surfaces.
- `tfs-plugin.ps1 sideload` validates, packages, hashes, copies previews, and updates a persistent local developer catalog in one command.

## Trust and local security

TFS follows a trusted gaming-loader model: installed plugins execute in Steam's frontend realm and can build deep integrations. Manifest permissions describe and gate supported SDK bridges, but are not a hostile-code sandbox. Install only plugins from trusted publishers.

The local TFS API is no longer exposed with wildcard CORS. A per-installation session protects API operations from unrelated websites while allowing Steam surfaces, TFS processes, EventSource updates, Store media, and installed plugin entry points to work normally.

## Xbox Mode

- All Store install, update-review, cancel, open, and close flows remain controller reachable.
- SDK notifications and UI stay inside Steam instead of opening desktop dialogs.
- The release installer includes the signed Xbox Host MSIX and its public certificate.

## Developer migration

Community entry points should register synchronously:

```js
window.TfsPluginSdk.register(manifest, (sdk) => ({
  createScreen(context) {
    return sdk.ui.createScreenModel({
      title: manifest.name,
      slots: [],
    });
  },
}));
```

Manual writes to `ToolsForSteamCommunityPlugins` and the old, undocumented `sdk.post()` pattern are not part of SDK 1.0.0. Validate packages with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tfs-plugin.ps1 validate .\path\to\plugin
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tfs-plugin.ps1 pack .\path\to\plugin
```
