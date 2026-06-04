# Steam Tools

Steam Tools is a Windows-first Quick Access toolkit for Steam Big Picture. It injects built-in tools directly into Steam's native side panel instead of opening a separate in-game window or shipping a plugin store.

> [!IMPORTANT]
> Steam Tools is still a work in progress.
> This project is part of **GCM - Gaming Console Mode** and is under active development. Expect rough edges, missing polish, and frequent behavior changes between preview releases.

## What It Does

- injects a native-feeling `Steam Tools` tab into Steam Big Picture / Gamepad UI
- keeps the interaction model controller-first and close to Steam's own Quick Access panels
- runs as a lightweight Windows tray app with a background host
- exposes built-in tools instead of a general plugin marketplace

## Current Built-In Tools

- `Processes`
  - list visible app windows in real time
  - bring a selected window to the front from the controller
- `Audio`
  - switch the default Windows output device
  - control system volume from Steam
- `Store Sync`
  - scan supported launchers and custom folders
  - sync discovered non-Steam games into Steam
  - optionally fetch artwork from SteamGridDB during sync
- `Themes`
  - enable and configure currently bundled themes
  - includes early CSS Loader-style groundwork and profile support
- `Display`
  - switch between internal and external display modes
- `Power`
  - restart Steam with the Steam Tools bridge enabled
  - recover the Windows desktop or trigger system power actions
- `HLTB`
  - show HowLongToBeat estimates on supported game detail pages
- `Settings`
  - general Steam Tools behavior such as Windows sign-in startup

## Project Status

Right now this repository should be treated as an early preview branch of the wider GCM idea.

That means:

- the visible product branding is already `Steam Tools`
- some internal file and executable names still use the older `SteamLoader` codename
- not every theme or overlay behaves perfectly on every Steam surface yet
- releases are meant for testing and iteration, not as a final polished stable build

## Requirements

- Windows
- Steam running in Big Picture / Gamepad UI mode
- Steam Tools starts Steam with the required DevTools endpoint on `127.0.0.1:8080` when needed

If Steam is already running without the DevTools endpoint, Steam Tools performs one controlled Steam restart so it can attach to Big Picture correctly.

## Run From Source

```powershell
dotnet build .\SteamLoader.slnx
dotnet run --project .\src\SteamLoader.App\SteamLoader.App.csproj
```

The background host serves a local control API on `http://127.0.0.1:47652/` and keeps trying to attach Steam Tools to Steam's Quick Access and supported Big Picture surfaces.

## Portable Build

Create a self-contained single-file Windows package with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
```

The publish script outputs:

- `dist\portable\SteamLoader.exe`
- `dist\SteamTools-portable-win-x64.zip`

Run `SteamLoader.exe` to start the tray app. Steam Tools can then start its background host automatically and attach to Steam when Big Picture is open.

## Repository Layout

- `src/SteamLoader.App/Hosting`
  - local API host and Steam injection loop
- `src/SteamLoader.App/Infrastructure/Steam`
  - Steam DevTools communication
- `src/SteamLoader.App/Infrastructure/Audio`
  - Core Audio integration
- `src/SteamLoader.App/Infrastructure/StoreSync`
  - launcher scanning, shortcut sync, artwork download
- `src/SteamLoader.App/Infrastructure/Themes`
  - theme state, CSS resolution, profiles
- `src/SteamLoader.App/Infrastructure/Hltb`
  - HowLongToBeat integration
- `src/SteamLoader.App/Assets`
  - injected Quick Access UI, theme surface logic, and bundled theme assets

## Notes

- Portable releases currently ship as preview builds.
- If Steam updates its internal UI structure, some tools or themes may need follow-up fixes.
- Feedback from real Big Picture usage is part of the expected development loop for this project.
