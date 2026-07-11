# Tools for Steam Plugin SDK

This folder documents the Tools for Steam community plugin contract.

The current SDK is **v1 preview**. It is intentionally small and capability based: core owns installation, package validation, storage, secrets, and network access. Community plugins only receive the frontend SDK surface that Tools for Steam exposes to them.

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
const sdk = window.TfsPluginSdk.create(manifest);
```

The SDK wraps the existing `STFrontendLib` helpers so plugin authors can create controller-friendly TFS screens without copying UI code.

Available helpers:

- `sdk.state()` returns installed SDK metadata, declared permissions, public settings, and secret configured flags.
- `sdk.storage.get()` and `sdk.storage.set(settings)` read and write a per-plugin JSON object.
- `sdk.storage.patch(partialSettings)` merges a small settings update into the current settings object.
- `sdk.secrets.status()` returns `{ key: true }` flags without exposing secret values.
- `sdk.secrets.set(key, value)` stores a per-plugin secret through the core.
- `sdk.secrets.clear(key)` removes a stored secret.
- `sdk.network.request(options)` sends an HTTP or HTTPS request through the core network proxy.
- `sdk.network.get(url, options)` and `sdk.network.post(url, body, options)` are small convenience wrappers.
- `sdk.ui` exposes shared controller-friendly screen and slot factories.
- `sdk.ui.createSecretEditor(options)` creates a password-style editor model for tokens or passwords. The entered value should be saved with `sdk.secrets.set()`.

## Dynamic Loader

Installed community plugins are loaded into Quick Access from `data/plugin-store/community/<plugin-id>` without special core access.

The runtime flow is:

- TFS reads installed plugin manifests from the store service.
- Quick Access requests `/api/plugin-store/community/installed`.
- Each plugin entry point is served from `/api/plugin-store/community/<plugin-id>/files/<entry-point>`.
- The entry point registers itself in `window.ToolsForSteamCommunityPlugins`.
- Quick Access adds registered community plugins to the Home screen after the built-in plugins.

Entry points should register synchronously:

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

`createScreen(context)` must return a screen model synchronously. Async work should run in command handlers or background promises and then call `context.refresh()` when the screen should re-render.

## Permissions

Permissions are declared in `tfs-plugin.json` and enforced by the core SDK routes.

- `frontend`: The plugin contains a frontend entry point.
- `storage`: The plugin may persist a public JSON settings object.
- `secrets`: The plugin may store secrets and pass them to SDK network requests.
- `network`: The plugin may send HTTP or HTTPS requests through the SDK proxy.

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
  "permissions": ["frontend", "storage", "secrets", "network"]
}
```

Rules enforced today:

- `id` must match the catalog entry id.
- `version` must match the catalog entry version.
- `sdkVersion` must use major version `1`.
- `entryPoint` must be a relative JavaScript path inside the package.
- `permissions` must contain only SDK v1 permissions: `frontend`, `storage`, `secrets`, and `network`.

## Package Safety Rules

The app rejects packages that do not fit the v1 store safety contract:

- `packageSha256` is required for every downloadable catalog entry.
- Package files must be `.zip`.
- Remote `packageUrl` values must use HTTP or HTTPS.
- Local `packagePath` values must be relative to the catalog file and stay inside that catalog folder.
- Packages are limited to 64 MB compressed, 128 MB extracted, 512 files, and 32 MB per extracted file.
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

Home Assistant's REST API is documented at https://developers.home-assistant.io/docs/api/rest/. It uses `/api/states` for entity states and `/api/services/<domain>/<service>` for service calls, authenticated with a Bearer token.

## Catalog

See these files for the store-side registry format:

- `catalog.example.json`: human-readable example.
- `catalog.local.json`: local development catalog written by the build script and ignored by git.
- `tfs-plugin-database/catalog.json`: the live official catalog consumed by the default Store Refresh action.
- `tfs-catalog.schema.json`: JSON schema for catalog validation.

The catalog is display and delivery metadata. The manifest inside the package is the runtime contract.

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

## Publishing A Community Plugin

Recommended flow:

1. Fork `tfs-plugin-template` or copy `sdk/plugin-template/` into your own plugin repository.
2. Build a zip with `tfs-plugin.json` at the package root.
3. Add a preview image if you want a custom store card.
4. Publish the zip somewhere stable, or add it to `tfs-plugin-database/packages/` for official catalog inclusion.
5. Add a catalog entry in `tfs-plugin-database/catalog.json` with `packageUrl`, `packageSha256`, and optionally one or more image URLs.
6. Open a pull request against `tfs-plugin-database` and document what permissions the plugin needs.

Use the smallest permission set possible. If your plugin only renders UI, use `frontend`. Add `storage`, `secrets`, or `network` only when the plugin truly needs them.

See `SUBMITTING.md` for the full pull request checklist and minimal plugin example.
