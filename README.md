# Steam Tools

Steam Tools is a Windows-first Quick Access host for Steam Big Picture. Instead of shipping a plugin store or a separate in-game window, it injects a fixed set of built-in pages directly into Steam's native Quick Access flow and keeps the overall look close to the real Steam UI.

## Architecture

- `src/SteamLoader.App/Infrastructure/Audio`: Windows Core Audio device discovery and default-device switching
- `src/SteamLoader.App/Infrastructure/Steam`: access to Steam's DevTools and runtime targets
- `src/SteamLoader.App/Hosting`: local control API and Quick Access injection loop
- `src/SteamLoader.App/Assets/quickaccess-popup.js`: the injected Quick Access UI shell and built-in plugin pages
- `src/SteamLoader.App/MainWindow.xaml`: the Windows management UI for the portable build

## Current Features

- Connects to the running Steam client through DevTools on `127.0.0.1:8080`
- Hooks into the real Quick Access flow used by Steam Big Picture
- Renders a built-in `Steam Tools -> Audio -> Output Device Changer` path
- Lists active playback devices
- Changes the Windows default output device directly from Steam
- Ships with a Steam-styled Windows manager UI
- Supports optional Windows autostart for the silent background host

## Run From Source

Requirement: Steam must be running in Big Picture or `-gamepadui` mode, and the CEF debug endpoint must be reachable on `127.0.0.1:8080`.

```powershell
dotnet build .\SteamLoader.slnx
dotnet run --project .\src\SteamLoader.App\SteamLoader.App.csproj
```

Launching the app opens the manager UI. The manager automatically starts the background host if it is not already running. The host exposes a local API on `http://127.0.0.1:47652/` and keeps trying to attach Steam Tools to Quick Access.

## Portable Build

Use the publish script to create a self-contained single-file Windows build and a matching ZIP package:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
```

The script outputs:

- `dist\portable\SteamLoader.exe`
- `dist\SteamLoader-portable-win-x64.zip`

Run `SteamLoader.exe` to open the manager. From there you can start or stop the background host, enable autostart, and open the portable folder.
