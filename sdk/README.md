# Tools for Steam Plugin SDK

New here? Follow [QUICKSTART.md](QUICKSTART.md) to create, run, and sideload a first plugin in about ten minutes. Use [full-trust-plugin-template/](full-trust-plugin-template/) when the plugin needs its own native backend.

For the complete developer workflow, native capability reference, Xbox Mode rules, testing matrix, and release checklist, read [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md).

This folder documents the Tools for Steam community plugin contract.

The current public SDK is **v1.0.0**. It is designed for trusted gaming plugins in the same spirit as Decky-style loaders: installed plugin JavaScript runs inside Steam's frontend realm and can build deep integrations. The manifest capability list drives the supported SDK bridges, Store disclosure, and update review; it is not a hostile-code sandbox. Install plugins only from publishers you trust.

Compatibility rule: SDK v1 updates should be additive. Existing v1 plugins should keep working as the SDK grows. If a future change needs to break the runtime contract, it should become SDK v2 while the app keeps a v1 loader for existing plugins.

## Store Contract

- The store refreshes the default GitHub catalog from `https://raw.githubusercontent.com/toonymak1993/tfs-plugin-database/main/catalog.json`.
- On a fresh install, TFS tries to download that catalog automatically on first Store use.
- The downloaded catalog is cached as `data/plugin-store/catalog.json`.
- Advanced users can override the feed by creating `data/plugin-store/catalog-source.json` with `{ "catalogUrl": "https://example.com/catalog.json" }`.
- Each catalog entry points to a zip package through `packagePath` or `packageUrl`.
- Each zip package must contain `tfs-plugin.json` at its root.
- The installer validates manifest id, name, version, SDK version, entry point, permissions, package size, zip paths, and the required SHA-256 checksum before installing.
- Installed community plugins live under `data/plugin-store/community/<plugin-id>`.
- SDK data lives under `data/plugin-store/sdk-data/<plugin-id>`.
- Built-in plugins remain part of the app and can only be hidden from Home.
- Community catalog images are recommended. If an entry has no image, the Store shows a fallback preview card instead of hiding the plugin.

## Runtime

Community plugins should use the same frontend foundation as built-in plugins:

```js
window.TfsPluginSdk.register(manifest, (sdk) => ({
  createScreen: () => sdk.ui.createScreenModel({ title: manifest.name, slots: [] }),
}));
```

The SDK wraps the existing `STFrontendLib` helpers so plugin authors can create controller-friendly TFS screens without copying UI code.

Available helpers:

- `sdk.state()` returns installed SDK metadata, declared permissions, public settings, and secret configured flags.
- `sdk.storage.get()` and `sdk.storage.set(settings)` read and write a per-plugin JSON object.
- `sdk.storage.patch(partialSettings)` merges a small settings update into the current settings object.
- `sdk.storage.remove(...keys)` and `sdk.storage.clear()` remove public settings.
- `sdk.secrets.status()` returns `{ key: true }` flags without exposing secret values.
- `sdk.secrets.set(key, value)` stores a per-plugin secret through the core.
- `sdk.secrets.clear(key)` removes a stored secret.
- `sdk.network.request(options)` sends an HTTP or HTTPS request through the core network proxy, limited to manifest `networkHosts`.
- `sdk.network.get(url, options)` and `sdk.network.post(url, body, options)` are small convenience wrappers.
- `sdk.network.put()`, `sdk.network.patch()`, and `sdk.network.delete()` cover all supported HTTP methods. Responses expose `text()` and `json()` helpers.
- `sdk.files` manages text or binary data inside an isolated per-plugin file area.
- `sdk.notifications` shows rate-limited, non-blocking notices inside the Steam UI, including Xbox Mode.
- `sdk.log` writes bounded structured diagnostic logs without opening a console window.
- `sdk.lifecycle` owns timers, cancellation, and cleanup when a plugin is reloaded or removed.
- `sdk.events.watch()` observes audio, process, display, or performance snapshots with lifecycle-owned, change-only polling.
- `sdk.hasPermission(name)` lets a plugin adapt to optional manifest capabilities.
- `sdk.ui` exposes shared controller-friendly screen and slot factories.
- `sdk.ui.createSliderSlot()` and `sdk.ui.createProgressSlot()` provide controller-native value and progress widgets.
- `sdk.ui.createSecretEditor(options)` creates a password-style editor model for tokens or passwords. The entered value should be saved with `sdk.secrets.set()`.
- `sdk.performance` exposes the managed TFS FPS/frametime service when `native.performance` is declared.
- `sdk.power` exposes explicitly confirmed Steam and Windows power actions when `native.power` is declared.
- `sdk.backend` starts a bundled executable, PowerShell, Python, or Node backend and provides lifecycle-managed JSON RPC.
- `sdk.system` runs commands, manages long-lived processes, opens shell targets, injects protected secrets into backend environments, and exposes system paths/information.
- `sdk.filesystem` reads and writes arbitrary Windows paths for explicitly full-trust plugins.
- `sdk.steam` lists Steam CEF targets, evaluates/injects scripts on selected surfaces, and exposes the current Steam client realm.

## Dynamic Loader

Installed community plugins are loaded into Quick Access from `data/plugin-store/community/<plugin-id>` without special core access.

The runtime flow is:

- TFS reads installed plugin manifests from the store service.
- Quick Access requests `/api/plugin-store/community/installed`.
- Each plugin entry point is served from `/api/plugin-store/community/<plugin-id>/files/<entry-point>`.
- The entry point registers itself through `window.TfsPluginSdk.register()`.
- Quick Access adds registered community plugins to the Home screen after the built-in plugins.

Entry points should register synchronously:

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

`register()` creates one managed SDK instance for the plugin. `createScreen(context)` must return a screen model synchronously. Async work should run in command handlers or lifecycle-owned background tasks and then call `context.refresh()` when the screen should re-render.

## Permissions

Permissions are declared in `tfs-plugin.json` and enforced by the core SDK routes.

- `frontend`: The plugin contains a frontend entry point.
- `storage`: The plugin may persist a public JSON settings object.
- `secrets`: The plugin may store secrets and pass them to SDK network requests.
- `network`: The plugin may send HTTP or HTTPS requests through the SDK proxy, but only to declared `networkHosts`.
- `files`: The plugin may manage its own sandboxed files. It cannot access another plugin's files or arbitrary user paths.
- `notifications`: The plugin may show non-blocking TFS notifications. Notifications are limited to five per 30 seconds.
- `logging`: The plugin may write structured diagnostics to its private rotating SDK log.
- `native.audio`: The plugin may read and control Windows playback, capture, and mixer state.
- `native.processes`: The plugin may list visible windows and activate a selected window.
- `native.display`: The plugin may change supported display, resolution, and refresh-rate modes.
- `native.themes`: The plugin may manage CSSLoader themes and profiles.
- `native.artwork`: The plugin may search and apply Steam artwork.
- `native.app-start`: The plugin may manage and launch curated App Start shortcuts.
- `native.store-sync`: The plugin may manage launcher discovery and Steam shortcut synchronization.
- `native.automation`: The plugin may configure reviewed TFS automation integrations such as Auto SISR.
- `native.performance`: The plugin may read TFS FPS, frametime, process, CPU, and memory telemetry and control the TFS overlay.
- `native.power`: The plugin may invoke confirmed Steam, sleep, restart, and shutdown actions.
- `native.full-trust`: The plugin may run arbitrary trusted native code, access arbitrary files, open shell targets, and inject into Steam surfaces. This is a Store warning and not a sandbox.

Secrets are write-only from the plugin perspective. A plugin can check whether a key exists, but it cannot read the secret value back. To use a token in an HTTP request, pass `authorizationSecretKey`; the core injects the `Authorization` header server-side.

## Secret Editor Example

```js
let tokenDraft = "";
const secretStatus = await sdk.secrets.status();

const editor = sdk.ui.createSecretEditor({
  inputKey: "access-token",
  label: "Access Token",
  configured: Boolean(secretStatus.accesstoken),
  onInput: (value) => {
    tokenDraft = value;
  },
});

const saveSlot = sdk.ui.createCommandSlot("Save Token", "Store the token securely.", async () => {
  if (tokenDraft) {
    await sdk.secrets.set("accessToken", tokenDraft);
    tokenDraft = "";
  }
});
```

## Package Layout

```text
my-plugin.zip
  tfs-plugin.json
  dist/
    index.js
  assets/
    preview.png
```

The preview image is optional but recommended for store listings. Use PNG, JPG, WEBP, GIF, or SVG when you provide one.

## Manifest

```json
{
  "id": "example-plugin",
  "name": "Example Plugin",
  "version": "1.0.0",
  "sdkVersion": "1.0.0",
  "entryPoint": "dist/index.js",
  "permissions": ["frontend", "storage", "secrets", "network"],
  "networkHosts": ["api.example.com"]
}
```

Rules enforced today:

- `id` must match the catalog entry id.
- `version` must match the catalog entry version.
- `sdkVersion` must be `1.0.0` for this TFS release. A plugin that requests a newer v1 feature level is rejected until the runtime supports it.
- `entryPoint` must be a relative JavaScript path inside the package.
- `frontend` is required for every community plugin.
- `permissions` must contain only permissions listed in this document and [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md). Native bridges use the `native.*` permission namespace.
- `networkHosts` is mandatory when `network` is declared and must match the catalog entry exactly. Use exact hosts, a leading wildcard such as `*.example.com`, or `<local>` for reviewed LAN integrations.

## Package Safety Rules

The app rejects packages that do not fit the v1 store safety contract:

- `packageSha256` is required for every downloadable catalog entry.
- Package files must be `.zip`.
- Remote `packageUrl` values must use HTTP or HTTPS.
- Local `packagePath` values must be relative to the catalog file and stay inside that catalog folder.
- Packages are limited to 256 MB compressed, 512 MB extracted, 2,048 files, and 256 MB per extracted file so self-contained native backends can be bundled.
- Zip entries may not escape the package root.
- Unknown permissions are rejected instead of ignored.

## Network Example

```js
await sdk.storage.set({ baseUrl: "http://homeassistant.local:8123" });
await sdk.secrets.set("accessToken", "long-lived-access-token");

const settings = await sdk.storage.get();
const response = await sdk.network.get(`${settings.baseUrl}/api/states`, {
  authorizationSecretKey: "accessToken",
});

const states = JSON.parse(response.bodyText);
```

## Sandboxed Files Example

Declare `"files"` in the manifest before using this API. Each plugin receives a private file area under its SDK data directory, limited to 32 MB total and 8 MB per file. Paths are always relative; absolute paths, `..`, links, and reparse points are rejected.

```js
await sdk.files.mkdir("cache/images");
await sdk.files.writeText("cache/state.json", JSON.stringify({ page: 2 }));
await sdk.files.appendText("logs/plugin.log", "refreshed\n");

const state = JSON.parse(await sdk.files.readText("cache/state.json"));
const entries = await sdk.files.list("cache", { recursive: true });

const bytes = new TextEncoder().encode("binary-safe");
await sdk.files.writeBytes("cache/images/copy.png", bytes);
const restoredBytes = await sdk.files.readBytes("cache/images/copy.png");
await sdk.files.copy("cache/state.json", "cache/state.backup.json");
await sdk.files.move("cache/state.backup.json", "archive/state.json");
await sdk.files.remove("archive", { recursive: true });
```

## Xbox-Mode Notifications, Logs, and Lifecycle

SDK notifications render inside the Steam interface and never open a desktop dialog. Logging rotates at 1 MB, and lifecycle-owned timers are automatically stopped when TFS reloads or removes the plugin.

```js
await sdk.notifications.success("Home Assistant", "Lights refreshed.");
await sdk.log.info("Entity refresh completed", { entityCount: 12 });

sdk.lifecycle.setInterval(() => {
  void refreshEntities({ signal: sdk.lifecycle.signal });
}, 30_000);

sdk.lifecycle.onDispose(() => closeWebSocket());
```

Home Assistant's REST API is documented at https://developers.home-assistant.io/docs/api/rest/. It uses `/api/states` for entity states and `/api/services/<domain>/<service>` for service calls, authenticated with a Bearer token.

## Catalog

See these files for the store-side registry format:

- `catalog.example.json`: human-readable example.
- `catalog.local.json`: local development catalog written by the build script and ignored by git.
- `tfs-plugin-database/catalog.json`: the live official catalog consumed by the default Store Refresh action.
- `tfs-catalog.schema.json`: JSON schema for catalog validation.

The catalog is display and delivery metadata. Its `sdkVersion` and `permissions` fields must match the package manifest exactly, allowing the Store to explain capabilities before download. The manifest inside the package remains the enforced runtime contract.

Community catalog images are recommended but not required. Entries without images still appear in the local store and use the built-in fallback preview card.

For local development, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1
```

This builds `sdk/packages/home-assistant.zip`, writes `sdk/catalog.local.json`, copies the package to the active runtime `data/plugin-store/packages/` folder, and writes the active runtime `data/plugin-store/catalog.json`.

For the online catalog, clone `tfs-plugin-database` and pass its path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 `
  -ImagePath "$env:USERPROFILE\Downloads\hassio.png" `
  -PluginDatabaseRoot "C:\path\to\tfs-plugin-database"
```

That writes these files into the database repository:

- `catalog.json`
- `images/<plugin-id>.<extension>`
- `packages/<plugin-id>.zip`

The online catalog uses GitHub Raw URLs and SHA-256 package validation.

## Templates And Official Examples

- `plugin-template/` is a minimal frontend plugin skeleton.
- `official-plugins/home-assistant/` is the first SDK-based community plugin example. It demonstrates storing a Home Assistant URL, storing a token as a secret, listing `light.*` entities, and calling `light.turn_on` / `light.turn_off` through the SDK network proxy.
- `full-trust-plugin-template/` demonstrates an auto-started PowerShell backend, JSON RPC, arbitrary process execution, and unrestricted filesystem access.

## Publishing A Community Plugin

Recommended flow:

1. Fork `tfs-plugin-template` or copy `sdk/plugin-template/` into your own plugin repository.
2. Build a zip with `tfs-plugin.json` at the package root.
3. Add a preview image if you want a custom store card.
4. Publish the zip somewhere stable, or add it to `tfs-plugin-database/packages/` for official catalog inclusion.
5. Add a catalog entry in `tfs-plugin-database/catalog.json` with `packageUrl`, `packageSha256`, matching `sdkVersion` and `permissions`, plus optional images and changelog.
6. Open a pull request against `tfs-plugin-database` and document what permissions the plugin needs.

Use the smallest permission set possible. If your plugin only renders UI, use `frontend`. Add `storage`, `secrets`, `network`, or `files` only when the plugin truly needs them.

See `SUBMITTING.md` for the full pull request checklist and minimal plugin example.

## One-command local sideload

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tfs-plugin.ps1 sideload .\my-plugin
```

The command validates and packs the plugin, computes its SHA-256 hash, copies its preview, and updates the active local developer catalog. Open the TFS Store, press Refresh, and install it from Community. Override detection with `-RuntimeDataDirectory <path>` for portable or alternate builds.
