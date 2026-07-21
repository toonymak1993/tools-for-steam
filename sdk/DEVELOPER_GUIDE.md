# Tools for Steam Plugin Developer Guide

This guide is the authoritative starting point for building controller-first plugins for Tools for Steam (TFS). It covers the runtime model, permissions, UI, data, native Windows bridges, Xbox Mode requirements, testing, packaging, and publication.

JavaScript projects can use [`tfs-plugin-sdk.d.ts`](tfs-plugin-sdk.d.ts) for editor completion and type checking. Copy it into the plugin project or reference it from `jsconfig.json`/`tsconfig.json` until an npm SDK package is published.

## 1. What a TFS plugin is

A TFS plugin is a JavaScript application loaded into the Steam frontend and rendered as a Tools for Steam screen. It can combine:

- controller-friendly TFS UI models;
- public JSON settings and protected secrets;
- HTTP integrations;
- private text and binary files;
- in-Steam notifications and structured logs;
- managed background timers and cancellation;
- explicitly declared native TFS capabilities.

Native capabilities are implemented by the TFS core. A plugin never needs to ship C#, execute PowerShell, invoke COM, or request administrator rights for normal SDK work.

## 2. Runtime architecture

```text
Plugin JavaScript
    |
    | TfsPluginSdk.register(manifest, setup)
    v
TFS JavaScript SDK
    |
    | /api/plugin-sdk/plugins/<plugin-id>/...
    v
TFS capability and permission boundary
    |
    +-- storage / secrets / files / network
    +-- notifications / logging / lifecycle
    +-- audio / processes / display / themes
    +-- artwork / app-start / store-sync
```

The manifest is the plugin's requested capability contract. The Store shows this contract before installation. The catalog entry and packaged manifest must contain the same SDK version and permissions.

### Trust model

TFS intentionally uses a full-trust gaming-loader model. Installed plugin scripts execute in Steam's frontend JavaScript realm. Declared permissions are enforced on documented SDK routes and are shown before installation and when an update expands access, but they are not a hostile-code sandbox. The official catalog is therefore curated. A custom developer catalog is visibly marked as unreviewed. Install packages only from publishers you trust.

The loopback API is protected from unrelated websites by a per-installation session. Plugins receive that session because they run as part of TFS. Plugin authors should use documented SDK methods: private `/api/...` routes, Steam DOM patches, React fiber access, and undocumented Steam internals have no compatibility guarantee and are not accepted into the curated catalog without a justified, reviewed exception.

## 3. Create a plugin

Start with this structure:

```text
my-plugin/
  tfs-plugin.json
  dist/
    index.js
  assets/
    preview.png
  README.md
```

Minimal manifest:

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "description": "A short description shown by Tools for Steam.",
  "version": "1.0.0",
  "sdkVersion": "1.0.0",
  "entryPoint": "dist/index.js",
  "permissions": ["frontend"]
}
```

Rules:

- `id` is permanent and must use lowercase letters, digits, `.`, `_`, or `-`.
- `version` must match the catalog entry.
- `sdkVersion` must be `1.0.0` and match the catalog entry.
- `entryPoint` is relative to the package root.
- every community plugin declares `frontend`.
- request only permissions the plugin actually uses;
- never place tokens, passwords, or private endpoints in the manifest.

## 4. Register the plugin

Use `TfsPluginSdk.register()`. It creates one managed SDK instance and disposes that instance when TFS reloads, updates, or removes the plugin.

```js
(() => {
  const manifest = {
    id: "my-plugin",
    name: "My Plugin",
    version: "1.0.0",
    sdkVersion: "1.0.0",
    permissions: ["frontend", "storage", "notifications", "logging"],
  };

  let statusText = "Ready";

  window.TfsPluginSdk.register(manifest, (sdk) => ({
    createScreen(context) {
      return sdk.ui.createScreenModel({
        title: manifest.name,
        subtitle: "Community Plugin",
        note: statusText,
        slots: [
          sdk.ui.createCommandSlot(
            "Run action",
            "Execute the plugin action.",
            async () => {
              statusText = "Working...";
              context.refresh();

              try {
                await sdk.log.info("Action started");
                await sdk.notifications.success("My Plugin", "Action completed.");
                statusText = "Completed";
              } catch (error) {
                statusText = error instanceof Error ? error.message : String(error);
                await sdk.log.error("Action failed", { message: statusText });
              }

              context.refresh();
            },
          ),
        ],
      });
    },
  }));
})();
```

`createScreen(context)` must return synchronously. Command handlers and lifecycle tasks may be asynchronous. Call `context.refresh()` whenever asynchronous state changes should be rendered.

## 5. Screen and state design

Keep mutable plugin state outside `createScreen()`. TFS may call `createScreen()` repeatedly.

```js
let loading = false;
let errorText = "";
let items = [];

window.TfsPluginSdk.register(manifest, (sdk) => ({
  createScreen(context) {
    return sdk.ui.createScreenModel({
      title: manifest.name,
      subtitle: loading ? "Loading" : `${items.length} items`,
      error: errorText,
      slots: [
        sdk.ui.createCommandSlot("Refresh", "Load the latest data.", async () => {
          loading = true;
          errorText = "";
          context.refresh();

          try {
            items = await loadItems(sdk.lifecycle.signal);
          } catch (error) {
            if (!sdk.lifecycle.signal?.aborted) {
              errorText = error instanceof Error ? error.message : String(error);
            }
          } finally {
            loading = false;
            context.refresh();
          }
        }),
      ],
    });
  },
}));
```

Available UI factories include:

- `sdk.ui.createScreenModel()`
- `sdk.ui.createCommandSlot()`
- `sdk.ui.createNavigationSlot()`
- `sdk.ui.createBackSlot()`
- `sdk.ui.createToggleSlot()`
- `sdk.ui.createChoiceSlot()`
- `sdk.ui.createAccordionSlot()`
- `sdk.ui.createInlineStepperSlot()`
- `sdk.ui.createSliderSlot()`
- `sdk.ui.createProgressSlot()`
- `sdk.ui.createSecretEditor()`
- `sdk.ui.createPanelShell()`

Use shared factories instead of custom HTML. Shared UI keeps focus, spacing, controller sounds, and Steam styling consistent.

```js
sdk.ui.createSliderSlot("Volume", 65, decreaseVolume, increaseVolume, {
  min: 0,
  max: 100,
  valueLabel: "65%",
  onValueChange: (value) => setVolume(value),
});

sdk.ui.createProgressSlot("Syncing library", "Applying Steam shortcuts...", 7, {
  max: 12,
  label: "7 / 12",
});
```

`onValueChange` receives the absolute value when the user clicks or drags the slider track. The left and right actions remain available for controller and keyboard input.

## 6. Permission reference

| Permission | SDK surface | Use it for |
|---|---|---|
| `frontend` | `sdk.ui` | A visible TFS screen |
| `storage` | `sdk.storage` | Public JSON preferences |
| `secrets` | `sdk.secrets` | Passwords and tokens |
| `network` | `sdk.network` | HTTP/HTTPS integrations |
| `files` | `sdk.files` | Private plugin files and caches |
| `notifications` | `sdk.notifications` | In-Steam status notices |
| `logging` | `sdk.log` | Structured diagnostics |
| `native.audio` | `sdk.audio` | Windows audio and mixer control |
| `native.processes` | `sdk.processes` | Visible window discovery and activation |
| `native.display` | `sdk.display` | Display mode, resolution, and refresh rate |
| `native.themes` | `sdk.themes` | CSSLoader themes and profiles |
| `native.artwork` | `sdk.artwork` | SteamGridDB search and Steam artwork writes |
| `native.app-start` | `sdk.appStart` | Curated application shortcuts and launching |
| `native.store-sync` | `sdk.storeSync` | Launcher discovery and Steam shortcut sync |
| `native.automation` | `sdk.automation` | Reviewed TFS automation integrations |
| `native.performance` | `sdk.performance` | FPS, frametime, target-process, CPU, memory, and overlay state |
| `native.power` | `sdk.power` | Explicitly confirmed Steam and Windows power actions |
| `native.full-trust` | `sdk.system`, `sdk.filesystem`, `sdk.backend`, `sdk.steam` | Native backends and unrestricted gaming integrations |

Native permissions deserve extra review because they change Windows or Steam state. Explain every native permission in the plugin README and catalog submission.

## 7. Settings

Declare `storage`.

```js
const settings = await sdk.storage.get();

await sdk.storage.set({
  baseUrl: "https://example.com",
  refreshSeconds: 30,
});

await sdk.storage.patch({ refreshSeconds: 60 });
await sdk.storage.remove("obsoleteKey");
await sdk.storage.clear();
```

Storage is a JSON object, not a database. Keep it small and store large or binary data with `sdk.files`.

## 8. Secrets

Declare `secrets`. Secret values are write-only.

```js
await sdk.secrets.set("accessToken", tokenDraft);

const configured = await sdk.secrets.status();
if (configured.accesstoken) {
  // A value exists, but the plugin cannot read it back.
}

await sdk.secrets.clear("accessToken");
```

Use the secret in a network request without exposing it to JavaScript again:

```js
const response = await sdk.network.get("https://api.example.com/profile", {
  authorizationSecretKey: "accessToken",
  authorizationScheme: "Bearer",
});
```

Never put secrets in settings, files, logs, notification text, URLs, or error reports.

## 9. Network requests

Declare `network`.

Every network-enabled manifest and catalog entry must also declare the same `networkHosts` list. Exact hosts and leading wildcard domains are supported. `<local>` is reserved for reviewed LAN integrations such as Home Assistant and permits loopback, private IP ranges, single-label hosts, and `.local` names. URLs outside the declared list are rejected before a request is sent.

```json
{
  "permissions": ["frontend", "network"],
  "networkHosts": ["api.example.com", "*.service.example.com"]
}
```

```js
const response = await sdk.network.get("https://api.example.com/items", {
  headers: { Accept: "application/json" },
  authorizationSecretKey: "accessToken",
});

if (!response.ok) {
  throw new Error(`Service returned ${response.statusCode}`);
}

const items = response.json();
```

Available methods:

- `sdk.network.request(options)`
- `sdk.network.get(url, options)`
- `sdk.network.post(url, body, options)`
- `sdk.network.put(url, body, options)`
- `sdk.network.patch(url, body, options)`
- `sdk.network.delete(url, options)`

Limits:

- absolute HTTP or HTTPS URLs only;
- GET, POST, PUT, PATCH, and DELETE;
- 512 KB request body;
- 1 MB response body;
- plugins cannot manually set `Authorization`, `Host`, `Content-Length`, `Connection`, or `Transfer-Encoding`;
- redirects are not followed automatically; request the destination explicitly so its host is checked against `networkHosts`;
- use a stored secret for authorization.

## 10. Private files

Declare `files`.

```js
await sdk.files.mkdir("cache/images");
await sdk.files.writeText("cache/state.json", JSON.stringify({ page: 2 }));

const state = JSON.parse(await sdk.files.readText("cache/state.json"));
const entries = await sdk.files.list("cache", { recursive: true });

const bytes = new Uint8Array([1, 2, 3, 4]);
await sdk.files.writeBytes("cache/data.bin", bytes);
const restored = await sdk.files.readBytes("cache/data.bin");

await sdk.files.copy("cache/state.json", "cache/state.backup.json");
await sdk.files.move("cache/state.backup.json", "archive/state.json");
await sdk.files.remove("archive", { recursive: true });
```

Limits and safety rules:

- paths are relative to the plugin's private sandbox;
- no absolute paths or `..` traversal;
- no links, junctions, or reparse points;
- 8 MB per file;
- 32 MB total;
- 1,024 entries;
- UTF-8 and Base64 transport are supported.

## 11. Notifications

Declare `notifications`.

```js
await sdk.notifications.show("Sync", "Scanning launcher libraries...");
await sdk.notifications.success("Sync", "Steam shortcuts updated.");
await sdk.notifications.warning("Sync", "Epic Games is not signed in.");
await sdk.notifications.error("Sync failed", "Open the plugin for details.");
```

Notifications appear inside Steam and do not open Windows desktop dialogs. They are limited to five notifications per plugin per 30 seconds. Use notifications for meaningful transitions, not continuous progress.

## 12. Logging

Declare `logging`.

```js
await sdk.log.debug("Refresh scheduled", { delayMs: 30000 });
await sdk.log.info("Refresh completed", { itemCount: 18 });
await sdk.log.warn("Service responded slowly", { elapsedMs: 4200 });
await sdk.log.error("Refresh failed", { code: "timeout" });
```

Logs are structured JSON lines, private to the plugin, and rotate at 1 MB. One previous log is retained. Do not log credentials, authorization headers, personal data, or full third-party responses.

## 13. Lifecycle and background work

Lifecycle-owned work stops when TFS reloads or removes the plugin.

```js
sdk.lifecycle.setTimeout(() => {
  void refreshOnce();
}, 1000);

sdk.lifecycle.setInterval(() => {
  void refreshOnce();
}, 30000);

sdk.lifecycle.onDispose(() => {
  socket?.close();
});

const response = await fetch(url, {
  signal: sdk.lifecycle.signal,
});
```

Do not use unmanaged global intervals. Do not assume the Steam frontend stays alive forever. Keep background polling conservative, especially in Xbox Mode and on battery-powered handhelds.

For supported native snapshots, prefer the managed change observer:

```js
sdk.events.watch("performance", (snapshot) => {
  latestPerformance = snapshot;
  context.refresh();
}, { intervalMs: 2000 });
```

Observers emit only when the serialized snapshot changes and stop automatically with `sdk.lifecycle`.

## 14. Native audio

Declare `native.audio`.

```js
const audio = await sdk.audio.getState();

await sdk.audio.setDefaultPlayback(audio.playbackDevices[0].id);
await sdk.audio.setPlaybackVolume(0.65);
await sdk.audio.adjustPlaybackVolume(-0.05);
await sdk.audio.togglePlaybackMute();

await sdk.audio.setDefaultCapture(audio.captureDevices[0].id);
await sdk.audio.setCaptureVolume(0.8);

const session = audio.mixerSessions[0];
await sdk.audio.setMixerVolume(session.sessionId, 0.5);
await sdk.audio.toggleMixerMute(session.sessionId);
```

Every mutation returns the latest audio dashboard snapshot.

## 15. Native processes

Declare `native.processes`.

```js
const snapshot = await sdk.processes.getState();
const target = snapshot.windows.find((windowInfo) => windowInfo.processName === "notepad");
if (target) {
  await sdk.processes.activate(target.handle);
}
```

The standard `native.processes` bridge lists visible top-level windows and activates a selected window. Plugins that intentionally need arbitrary executables, managed backends, or Steam injection use the separately disclosed `native.full-trust` API below.

## 16. Native display

Declare `native.display`.

```js
const modes = await sdk.display.getState();
await sdk.display.setResolution("full-hd");
await sdk.display.setRefreshRate(120);
await sdk.display.switchExternal();
await sdk.display.switchInternal();
```

Only presets reported as available should be selectable. Display changes can briefly blank the screen, so require an explicit controller action and show a confirmation description beforehand.

## 17. Native themes

Declare `native.themes`.

```js
const snapshot = await sdk.themes.getState();
const catalog = await sdk.themes.getStoreCatalog({
  search: "clean",
  page: 1,
  perPage: 12,
});

await sdk.themes.installStoreTheme(catalog.items[0].storeId);
await sdk.themes.setEnabled("Clean Gameview", true);
await sdk.themes.toggleOption("Clean Gameview", "blur");
await sdk.themes.setChoice("Clean Gameview", "alignment", "center");
await sdk.themes.adjustRange("Clean Gameview", "opacity", 1);

await sdk.themes.createProfile("Living Room");
await sdk.themes.applyProfile("Living Room");
await sdk.themes.setWatchEnabled(true);
```

Theme operations use the managed CSSLoader backend. Plugins must handle the backend-not-installed state and present recovery instructions instead of opening Explorer.

## 18. Native artwork

Declare `native.artwork`.

```js
const games = await sdk.artwork.searchGames("Hades");
const assets = await sdk.artwork.searchAssets(games[0].id, "grid_p", { page: 0 });
const result = await sdk.artwork.apply(1145360, "grid_p", assets[0].url);

if (!result.success) {
  throw new Error(result.message);
}
```

Supported asset types are determined by the TFS artwork service. Applying artwork writes to Steam's grid folder and therefore requires a deliberate user action.

## 19. Native App Start

Declare `native.app-start`.

```js
const catalog = await sdk.appStart.getCatalog();
await sdk.appStart.toggleFavorite(catalog.apps[0].id);

const shortcuts = await sdk.appStart.getState();
await sdk.appStart.launch(shortcuts.shortcuts[0].id);
await sdk.appStart.remove(shortcuts.shortcuts[0].id);
await sdk.appStart.add(shortcuts.shortcuts[0].id); // restore a hidden app
await sdk.appStart.refreshCatalog();
```

App Start uses TFS's cached catalog of launchable desktop and packaged Windows apps. It is not an arbitrary process execution API.

## 20. Native Store Sync

Declare `native.store-sync`.

```js
const state = await sdk.storeSync.getState();
const titles = await sdk.storeSync.getTitles("epic-games");

await sdk.storeSync.setStoreEnabled("epic-games", true);
await sdk.storeSync.setStorePath("custom-locations", "D:\\Games");
await sdk.storeSync.setAdditionalPaths("custom-locations", ["E:\\Portable Games"]);

await sdk.storeSync.setTitleOverride(titles[0].id, {
  titleOverride: "My Preferred Title",
  artworkTitleOverride: "Official Artwork Search Name",
  excluded: false,
});

const preview = await sdk.storeSync.getArtworkPreview(titles[0].id);
await sdk.storeSync.sync();
```

Sync operations can close or restart Steam depending on user settings. Never start a sync automatically when a screen opens. Require an explicit command and explain the impact.

## 21. Native automation

Declare `native.automation` when building an automation frontend such as Auto SISR.

```js
const state = await sdk.automation.getState();
await sdk.automation.toggleSetting("enabled");
await sdk.automation.toggleWatchedTitle(state.titles[0].id);
```

Automation plugins must never silently opt users into process monitoring or launch behavior. Require an explicit enable action and keep the current automation status visible.

## 22. Native performance

Declare `native.performance`.

```js
const performance = await sdk.performance.getState();
await sdk.performance.setOverlayLevel(2);
await sdk.performance.setSettingValue("frame-limit", 60);
```

The snapshot includes RTSS installation state, FPS, frametime, rolling one-percent-low FPS, frame pacing, target CPU usage, target memory, overlay state, and the active per-game frame limit. Setting an overlay level applies it immediately: `0` Off, `1` FPS, `2` SteamOS Strip, `3` SteamOS Full, and `4` Frame Pacing. `startOverlay()` and `stopOverlay()` remain compatibility aliases for selecting FPS and Off. The compatibility method `prepareElevatedHelper()` closes RTSS background components, repairs RTSS, and restarts it; it no longer creates an elevated TFS FPS helper. Do not poll faster than the configured telemetry rate.

## 23. Native power

Declare `native.power`. Every mutation requires `{ confirmed: true }` after a visible controller confirmation. Calling a power method without it is rejected in both JavaScript and the server.

```js
const power = await sdk.power.getState();
await sdk.power.restartSteam({ confirmed: true });
await sdk.power.sleepWindows({ confirmed: true });
```

Never invoke a power action when a screen opens, from a timer, or as a side effect of another setting.

## 24. Full-trust plugins and native backends

Declare `native.full-trust` when a plugin must go beyond the managed native bridges. This is the Decky-style escape hatch: it is intentionally capable of running arbitrary trusted code as the current Windows user. The Store presents a prominent warning before installation and again if an update adds this permission.

### Bundled backend

Add a backend to `tfs-plugin.json`:

```json
{
  "permissions": ["frontend", "native.full-trust"],
  "backend": {
    "entryPoint": "backend/plugin.exe",
    "runtime": "executable",
    "arguments": [],
    "autoStart": true,
    "createNoWindow": true
  }
}
```

`runtime` can be `executable`, `powershell`, `python`, or `node`. A self-contained executable is the most portable option. For Python or Node, bundle a runtime and set `runtimeExecutable`, or document the required system runtime. TFS supplies these environment variables:

- `TFS_PLUGIN_ID`
- `TFS_PLUGIN_DIR`
- `TFS_PLUGIN_DATA_DIR`

Use `secretEnvironment` to map protected SDK secret keys to backend environment variables without putting values in the manifest:

```json
{
  "secretEnvironment": {
    "SERVICE_TOKEN": "accessToken"
  }
}
```

This mapping also requires `secrets`.

### Backend RPC

TFS provides line-delimited JSON RPC over standard input/output. The backend receives:

```json
{"tfsRpcId":"unique-id","method":"ping","arguments":{"value":1}}
```

It answers with one JSON line:

```json
{"tfsRpcId":"unique-id","result":{"message":"pong"}}
```

or:

```json
{"tfsRpcId":"unique-id","error":"Explanation"}
```

The frontend calls it with:

```js
const result = await sdk.backend.call("ping", { value: 1 });
```

TFS captures backend output, exposes status and errors, terminates processes on plugin reload/update/uninstall, and stops all remaining plugin processes during shutdown.

### Programs and shell targets

```js
const result = await sdk.system.run("tool.exe", ["--json"], {
  packageRelative: true,
  timeoutMs: 60_000,
  environment: { MODE: "gaming" },
});

const process = await sdk.system.start("worker.exe", [], { packageRelative: true });
await sdk.system.status(process.processId);
await sdk.system.stop(process.processId);
await sdk.system.open("steam://open/bigpicture");
await sdk.system.open("installer.exe", [], { runAsAdministrator: true });
```

Arguments use `ProcessStartInfo.ArgumentList`, not a concatenated shell command. `sdk.system.run()` captures output and errors. `start()` manages a long-running child process.

### Full filesystem

`sdk.files` remains the safe private store. `sdk.filesystem` can access arbitrary Windows paths:

```js
const paths = await sdk.filesystem.paths();
await sdk.filesystem.writeText(`${paths.documents}\\plugin-export.json`, "{}", { scope: "absolute" });
const entries = await sdk.filesystem.list("C:\\Games", { scope: "absolute", recursive: false });
```

Relative paths default to the plugin data directory. Supported scopes are `data`, `plugin`, `app`, `temp`, and `absolute`.

### Steam surfaces

The plugin already runs inside a Steam frontend realm and can use `sdk.steam.client`. For other Steam CEF surfaces:

```js
const targets = await sdk.steam.targets();
const bigPicture = targets.find((target) => target.title.includes("Big-Picture"));
const result = await sdk.steam.evaluate(bigPicture.id, "document.title");
await sdk.steam.inject(bigPicture.id, "window.MyPluginHook = true; 'installed';");
```

Steam internals can change without notice. Keep surface injection isolated, detectable, and removable. Curated submissions must explain why a documented TFS screen or bridge is insufficient.

See `full-trust-plugin-template/` for a working PowerShell backend, RPC call, native command, and arbitrary-file example.

Local sideload requires one command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tfs-plugin.ps1 sideload .\my-plugin
```

It validates, packs, hashes, copies the preview, and updates the active developer catalog. Press Refresh in the TFS Store and install the plugin from Community.

## 25. Xbox Mode requirements

Every Store plugin must work without a mouse, physical keyboard, Explorer, or desktop taskbar.

Mandatory rules:

1. Every action is reachable by D-pad or analog navigation.
2. `A` activates and `B` returns or closes.
3. Initial focus is predictable.
4. Focus never disappears after asynchronous refreshes.
5. Text is readable from television distance.
6. Do not depend on hover.
7. Do not open native file dialogs, console windows, or Explorer.
8. Use the Steam keyboard only for necessary short text input.
9. Preserve user drafts while re-rendering.
10. Show progress for operations longer than 500 ms.
11. Do not run expensive polling faster than needed.
12. Respect reduced motion and avoid flashing UI.
13. Confirm disruptive display, artwork, launcher, or sync mutations.
14. Handle network loss and service timeouts without trapping focus.
15. Stop timers and connections through `sdk.lifecycle`.

Recommended controller copy:

- action labels should be verbs: `Refresh`, `Install`, `Apply`, `Launch`;
- descriptions should explain the result, not repeat the title;
- errors should say what failed and what the user can do next;
- avoid instructions such as “click”, “right-click”, or “open Explorer”.

## 26. Error handling

Every SDK method can reject. Handle errors at the user action boundary.

```js
async function runWithFeedback(context, action) {
  try {
    await action();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    await sdk.log.error("User action failed", { message });
    await sdk.notifications.error(manifest.name, message);
    errorText = message;
  } finally {
    context.refresh();
  }
}
```

Do not expose raw stack traces, secret values, local usernames, or full filesystem paths in the normal UI.

## 27. Package the plugin

The zip root must contain `tfs-plugin.json` directly:

```text
my-plugin.zip
  tfs-plugin.json
  dist/index.js
  backend/plugin.exe
  assets/preview.png
```

Package limits:

- 256 MB compressed;
- 512 MB extracted;
- 2,048 archive entries;
- 256 MB for one archive entry;
- no absolute paths;
- no `..` traversal;
- no unsafe extraction outside the plugin root.

Compute the package checksum:

```powershell
(Get-FileHash .\my-plugin.zip -Algorithm SHA256).Hash
```

## 28. Create the catalog entry

```json
{
  "id": "my-plugin",
  "title": "My Plugin",
  "description": "A controller-friendly Tools for Steam plugin.",
  "author": "Your Name",
  "category": "Utility",
  "version": "1.0.0",
  "sdkVersion": "1.0.0",
  "permissions": ["frontend", "network", "notifications", "logging"],
  "networkHosts": ["api.example.com"],
  "packageUrl": "https://example.com/my-plugin.zip",
  "packageSha256": "64_HEX_CHARACTERS",
  "images": ["https://example.com/my-plugin.png"],
  "tags": ["utility", "controller"],
  "homepageUrl": "https://example.com/my-plugin",
  "repositoryUrl": "https://github.com/you/my-plugin",
  "changelog": "Initial release."
}
```

The following must match the packaged manifest exactly:

- `id`
- `version`
- `sdkVersion`
- complete `permissions` set
- complete `networkHosts` set

The Store verifies the SHA-256 checksum before extraction and rejects mismatched metadata.

## 29. Test before submission

Test at minimum:

- fresh install;
- update from the previous version;
- uninstall and reinstall;
- missing settings and secrets;
- invalid credentials;
- offline startup;
- slow API response;
- empty results;
- maximum realistic result count;
- 1280x720, 1920x1080, and 3840x2160;
- 100%, 125%, and 150% Windows scaling where applicable;
- controller-only navigation;
- Xbox Mode without Explorer or taskbar;
- plugin reload without duplicated timers;
- TFS restart with persisted state;
- every declared permission shown in the Store;
- no secret or personal data in logs.

## 30. Submission checklist

- [ ] Manifest ID is stable and lowercase.
- [ ] Version and SDK version are correct.
- [ ] Catalog metadata matches the manifest.
- [ ] Only required permissions are declared.
- [ ] Native permissions are explained in README.
- [ ] All UI is controller reachable.
- [ ] `A` and `B` behavior is consistent.
- [ ] Async errors are visible and recoverable.
- [ ] Timers use `sdk.lifecycle`.
- [ ] Tokens use `sdk.secrets`.
- [ ] Large data uses `sdk.files`.
- [ ] No private TFS endpoints are called.
- [ ] No Steam DOM or React internals are patched.
- [ ] Full-trust executable, filesystem, and Steam access is declared and explained when used.
- [ ] Managed backends stop cleanly on reload, update, uninstall, and TFS shutdown.
- [ ] Preview image remains legible at controller distance.
- [ ] Changelog describes user-visible changes.
- [ ] Zip checksum is from the final uploaded archive.
- [ ] Fresh install, update, and uninstall were tested.

## 31. API stability

SDK 1.0.0 is the first public compatibility contract:

- additive SDK updates within major version 1 remain backward compatible;
- breaking runtime changes require a new SDK major version;
- deprecated APIs should remain available for a documented migration window;
- a plugin requesting a newer v1 feature level is rejected until that TFS runtime supports it.

Build new plugins against documented SDK surfaces only. Private core routes and Steam internals have no compatibility guarantee.
