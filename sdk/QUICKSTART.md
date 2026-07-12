# Build Your First Tools for Steam Plugin

This guide takes a plugin from an empty folder to the TFS Community Store on your own PC. A basic controller plugin takes about ten minutes. No TFS Core changes are required.

## 1. Choose the plugin type

Use a normal SDK plugin when the app needs TFS UI, settings, secrets, network requests, private files, notifications, or one of the documented native bridges.

Use a full-trust plugin when it needs its own executable or script, arbitrary Windows files, registry or hardware access, UAC, or custom integration with another Steam CEF surface.

| Goal | Start with |
|---|---|
| REST integration, dashboard, smart home, game API | Normal template |
| Audio, display, artwork, performance, launcher helper | Normal template plus `native.*` permission |
| Rust, Go, C++, .NET, PowerShell, Python, or Node backend | Full-trust template |
| Registry, drivers, hardware APIs, process injection | Full-trust template |
| Steam page or context-surface modification | Full-trust template and `sdk.steam` |

## 2. Scaffold a normal plugin

From the Tools for Steam repository:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\tfs-plugin.ps1 new .\plugins\hello-tfs `
  -Id "hello-tfs" `
  -Name "Hello TFS"
```

The command creates:

```text
plugins/hello-tfs/
  tfs-plugin.json
  dist/index.js
  assets/
```

## 3. Understand the manifest

`tfs-plugin.json` is both the runtime contract and the Store permission disclosure:

```json
{
  "id": "hello-tfs",
  "name": "Hello TFS",
  "version": "0.1.0",
  "sdkVersion": "1.0.0",
  "entryPoint": "dist/index.js",
  "permissions": ["frontend"]
}
```

The ID is permanent and must be lowercase. SDK 1.0.0 always requires `frontend`. Add permissions only when the plugin uses the matching API.

Common permissions:

- `storage`: public JSON settings.
- `secrets`: protected tokens and passwords.
- `network`: HTTP/HTTPS through declared `networkHosts`.
- `files`: private plugin files.
- `notifications` and `logging`: Steam notices and diagnostics.
- `native.audio`, `native.display`, `native.processes`, `native.artwork`, `native.performance`, and the other `native.*` bridges.
- `native.full-trust`: native backends and unrestricted Windows/Steam access.

## 4. Build the first screen

Replace `dist/index.js` with:

```js
(() => {
  const manifest = {
    id: "hello-tfs",
    name: "Hello TFS",
    version: "0.1.0",
    sdkVersion: "1.0.0",
    permissions: ["frontend"],
  };

  window.TfsPluginSdk.register(manifest, (sdk) => {
    let presses = 0;

    return {
      createScreen(context) {
        return sdk.ui.createScreenModel({
          title: manifest.name,
          subtitle: "Community Plugin",
          note: `Pressed ${presses} times`,
          slots: [
            sdk.ui.createCommandSlot(
              "Say hello",
              "Update this controller-friendly screen.",
              () => {
                presses += 1;
                context.refresh();
              },
              { badge: "A" },
            ),
          ],
        });
      },
    };
  });
})();
```

Important rules:

- Register synchronously with `TfsPluginSdk.register()`.
- `createScreen()` returns immediately.
- Async work belongs in button handlers, promises, or lifecycle tasks.
- Call `context.refresh()` after state changes.
- Every action must work with a controller in Xbox Mode.

## 5. Validate and sideload

Validate before every package build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\tfs-plugin.ps1 validate .\plugins\hello-tfs
```

Sideload the current build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\tfs-plugin.ps1 sideload .\plugins\hello-tfs
```

Then:

1. Open `Tools for Steam > Store`.
2. Press `Refresh`.
3. Open `Community`.
4. Select `Hello TFS` and install it.
5. Return to TFS Home and open the plugin.

The generated catalog is marked as a local developer catalog. Refresh does not replace it with the official online catalog. Use `-RuntimeDataDirectory <path>` when testing a portable or alternate TFS build.

When testing an update, increment the plugin version, run `sideload` again, press Refresh, and install the update. If the update requests new permissions or network hosts, the Store requires a second controller confirmation.

## 6. Add settings, secrets, and HTTP

Manifest:

```json
{
  "permissions": ["frontend", "storage", "secrets", "network"],
  "networkHosts": ["api.example.com"]
}
```

Plugin code:

```js
await sdk.storage.set({ baseUrl: "https://api.example.com" });
await sdk.secrets.set("accessToken", "secret-value");

const settings = await sdk.storage.get();
const response = await sdk.network.get(`${settings.baseUrl}/status`, {
  authorizationSecretKey: "accessToken",
});
const data = response.json();
```

Secret values cannot be read back by normal frontend code. The network bridge injects the selected secret into the authorization header on the Core side.

## 7. Add a native backend

Copy [full-trust-plugin-template/](full-trust-plugin-template/) or declare:

```json
{
  "permissions": ["frontend", "native.full-trust"],
  "backend": {
    "entryPoint": "backend/plugin.exe",
    "runtime": "executable",
    "autoStart": true,
    "createNoWindow": true
  }
}
```

TFS can run a bundled self-contained executable or a PowerShell, Python, or Node entry point. It supplies:

- `TFS_PLUGIN_ID`
- `TFS_PLUGIN_DIR`
- `TFS_PLUGIN_DATA_DIR`

Call the backend from JavaScript:

```js
const result = await sdk.backend.call("scanGames", {
  includeHidden: false,
});
```

The backend receives one JSON line on standard input:

```json
{"tfsRpcId":"request-id","method":"scanGames","arguments":{"includeHidden":false}}
```

Return one line on standard output:

```json
{"tfsRpcId":"request-id","result":{"games":[]}}
```

TFS captures output and errors and stops managed processes on reload, update, uninstall, and shutdown.

Full-trust plugins also receive:

```js
await sdk.system.run("tool.exe", ["--scan"], { packageRelative: true });
await sdk.filesystem.readText("C:\\Games\\config.json", { scope: "absolute" });
await sdk.system.open("installer.exe", [], { runAsAdministrator: true });

const targets = await sdk.steam.targets();
await sdk.steam.evaluate(targets[0].id, "document.title");
```

Full trust means exactly that: the plugin can act with the current Windows user's authority. Only install it from a trusted publisher.

## 8. Test like a console app

Before publishing, verify:

- install, update, uninstall, and reinstall;
- controller-only navigation and predictable focus;
- `A` activates and `B` returns;
- 1280x720, 1920x1080, and 3840x2160 where possible;
- Xbox Mode without Explorer or taskbar;
- offline and timeout behavior;
- plugin reload without duplicated timers or processes;
- no secrets or personal data in logs;
- backend shutdown during update and uninstall.

## 9. Package and publish

Create a release ZIP and checksum:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\tfs-plugin.ps1 pack .\plugins\hello-tfs
```

Add the ZIP, SHA-256, manifest metadata, permissions, network hosts, preview image, source repository, and changelog to the TFS plugin catalog. Follow [SUBMITTING.md](SUBMITTING.md) for the complete review checklist.

Package limits support self-contained native backends:

- 256 MB compressed;
- 512 MB extracted;
- 2,048 files;
- 256 MB per file.

## Where to continue

- [Complete SDK API and architecture guide](DEVELOPER_GUIDE.md)
- [SDK manifest, permissions, and Store contract](README.md)
- [TypeScript declarations](tfs-plugin-sdk.d.ts)
- [Minimal frontend template](plugin-template/)
- [Full-trust backend template](full-trust-plugin-template/)
- [Home Assistant example](official-plugins/home-assistant/)
- [Official catalog submission checklist](SUBMITTING.md)
