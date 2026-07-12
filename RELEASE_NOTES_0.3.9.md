# Tools for Steam 0.3.9 Beta 2

This is the first official beta of the complete Tools for Steam platform: controller-first Steam integration, three startup modes, the Community Store, Plugin SDK 1.0, the Home Assistant reference plugin, and the first verified handheld TDP controls.

> This is a pre-release for real-world testing. Use Tray Mode first if you do not yet want TFS to change the current user's console startup flow.

## Store and controller experience

- Reworked the Store with compact, consistently sized cards, safe inner spacing, contained previews, and responsive 3/2/1-column grids.
- Community plugins stay left-aligned when fewer than three entries are available.
- Added Discover, Built-In, Community, Installed, and Updates sections.
- Added curated-catalog trust disclosure and a visible warning for custom developer catalogs.
- Added controller-friendly review when an update requests new permissions or network hosts.
- Fixed the lower controller bar overlapping plugin cards.
- Fixed cards and artwork being cropped or shifted at Xbox Mode resolutions.
- Fixed snapshot refreshes moving controller focus unexpectedly from tabs or header actions to the first plugin card.

## Plugin SDK 1.0.0

- Managed registration and cleanup through `window.TfsPluginSdk.register()`.
- Controller-native screens, commands, toggles, choices, accordions, steppers, sliders, progress, and secret editors.
- Per-plugin settings, write-only secrets, private text/binary files, structured logs, notifications, lifecycle timers, and observable state.
- Proxied HTTP methods with declared host access and protected authorization-secret injection.
- Native bridges for audio, windows/process activation, display modes, themes, artwork, App Start, Store Sync, automation, performance telemetry, overlay control, and confirmed power actions.
- Decky-style `native.full-trust` support for bundled executable, PowerShell, Python, or Node backends.
- Managed backend JSON-RPC, process lifecycle, captured output, arbitrary Windows files, shell/UAC targets, and selected Steam CEF evaluation/injection.
- TypeScript declarations, JSON schemas, minimal and full-trust templates, a complete developer guide, and a create/validate/pack/sideload CLI.
- Explicit feature-level compatibility: this runtime accepts SDK `1.0.0` and rejects plugins requiring a newer v1 feature level.

## New and updated plugins

### Handheld Performance / TDP Control

- First verified device: **MSI Claw A8** with board product `MS-1T8K`.
- Direct control from 15 W to 35 W.
- Battery 15 W, Balanced 20 W, and Performance 28 W presets.
- Separate plugged-in and battery targets.
- Automatic per-game profiles, profile notifications, and global-profile restoration.
- Live power, battery, game, and applied-TDP state.
- The plugin remains hidden on unsupported devices. More verified handheld profiles will follow.

### Home Assistant 0.2.0

- First official SDK 1.0 community reference plugin.
- Uses TFS storage for the server URL, write-only secrets for the token, and the network bridge restricted to local hosts.
- Lists and controls Home Assistant light entities from a controller-native TFS screen.

### Built-in tools

- FPS Overlay, Processes, App Start, Store Sync, SteamGridDB, Audio, Display, CSSLoader, HLTB, Auto SISR, Homey, and Power remain available through the built-in catalog.
- Built-ins can be hidden from Home where safe; recovery actions remain available.

## Xbox Mode and installer

- Includes the signed Xbox Host MSIX and public certificate.
- Adds safer suspend/restore behavior when updating during an active Xbox Mode session.
- Adds rollback snapshots before replacing an existing installation.
- Adds installer diagnostics next to the setup executable, with a Desktop fallback.
- Keeps Store installation, permission review, cancel, open, and close flows controller reachable.
- Keeps SDK notifications and plugin UI inside Steam rather than opening desktop dialogs.

## Trust and local security

TFS uses a trusted gaming-loader model. Installed plugins execute in Steam's frontend realm and may build deep integrations. Manifest permissions gate and disclose supported SDK bridges, but they are not a hostile-code sandbox. Install full-trust plugins only from publishers you trust.

- Replaced wildcard local-API CORS with trusted Steam/TFS origins.
- Added a strong per-installation API session for protected operations.
- Added package SDK, manifest, catalog permission, network-host, ZIP-path, size, and SHA-256 validation.
- Added explicit warnings for unreviewed catalogs and full-trust backends.
- Stops managed plugin processes on reload, update, uninstall, and TFS shutdown.

## Validation

- 136 .NET regression tests passed.
- 4 SDK and Store JavaScript tests passed.
- Minimal, Home Assistant, and full-trust templates pass the official CLI validator.
- Manifest and catalog schemas validate.
- Portable app, signed Xbox Host MSIX, and Inno Setup installer build successfully from `main`.

The existing AudioSwitcher packages still emit known .NET 10 `NU1701` compatibility warnings; builds and tests complete successfully.

## Beta installation note

The bundled Xbox Host MSIX is signed by `CN=GCM Gaming Console Mode`. The outer beta setup executable is not yet Authenticode-signed, so Windows may show an Unknown Publisher or SmartScreen warning. Verify the SHA-256 published with the GitHub release before running it.

## For plugin developers

```powershell
.\scripts\tfs-plugin.ps1 new .\plugins\my-plugin -Id my-plugin -Name "My Plugin"
.\scripts\tfs-plugin.ps1 validate .\plugins\my-plugin
.\scripts\tfs-plugin.ps1 sideload .\plugins\my-plugin
```

The standalone starter kit is available at <https://github.com/toonymak1993/tfs-plugin-template>. Submit packages to <https://github.com/toonymak1993/tfs-plugin-database> after testing install, update, uninstall, controller navigation, Xbox Mode behavior, offline handling, and cleanup.
