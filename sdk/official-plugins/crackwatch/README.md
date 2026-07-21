# Crackwatch Community Plugin

Crackwatch is an optional Tools for Steam Store plugin. It is not installed or enabled by default. After installation, it shows the public crack-status metadata published by [CrackRelease](https://crackrelease.com/) without linking to downloads, torrents, cracks, or repacks.

## Features

- scrapes the complete public `https://crackrelease.com/games/` page and the homepage Hot Games section in a hidden PowerShell backend
- starts on the site's five current Hot Games and provides all cracked games sorted by the latest CrackRelease update date
- provides a global, punctuation-tolerant multi-word search across every tracked title and status
- keeps locally stored personal favorites and makes each search easy to clear
- keeps the complete public status list so favorited uncracked or unreleased games can be monitored
- caches the last valid result so the screen remains useful when the source is offline
- refreshes asynchronously at startup and every hour by default
- lets the user select a polite 30-minute, 1-hour, 3-hour, or 6-hour interval
- sends one optional notification when new cracked entries appear and prioritizes favorites whose status changes to cracked
- searches globally and pages results with controller-friendly controls
- lazy-loads and asynchronously decodes CrackRelease artwork only for visible result cards

The site's `robots.txt` currently permits crawling. The default hourly schedule and conditional HTTP headers keep background traffic deliberately low. If CrackRelease changes its HTML card structure, the plugin keeps the previous cache and reports a refresh error instead of replacing it with an empty result.

## Permissions

- `frontend` registers the Quick Access screen.
- `storage` stores the refresh interval and notification preference.
- `notifications` reports newly detected cracked games.
- `logging` records bounded refresh diagnostics.
- `native.full-trust` runs the bundled PowerShell scraper. This is required because the complete games page is larger than the SDK v1 network response limit.

The backend only contacts `https://crackrelease.com/games/`, `https://crackrelease.com/`, and the site's WordPress post-metadata endpoint under `https://crackrelease.com/wp-json/`. It validates all parsed source and image URLs against `crackrelease.com` and stores its cache in the plugin's TFS data directory. Favorites and preferences are stored locally through the TFS SDK.

The bundled `assets/crackrelease-logo.png` and Store preview use the official CrackRelease logo retrieved from the site's own WordPress uploads. CrackRelease and its logo remain the property of their respective owner; the asset is included only to identify the data source.

## Package Layout

```text
crackwatch.zip
  tfs-plugin.json
  dist/
    index.js
  backend/
    plugin.ps1
  assets/
    preview.png
    crackrelease-logo.png
```

## Validate and Sideload

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tfs-plugin.ps1 validate sdk\official-plugins\crackwatch
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tfs-plugin.ps1 sideload sdk\official-plugins\crackwatch
```

Open the TFS Store, press Refresh, and install Crackwatch from Community. Installing the catalog entry is the explicit opt-in; the plugin is never installed automatically.

## Build the Store Package

The shared Store builder reads `store.json`, uses the bundled preview, packages the native backend, and preserves other catalog entries:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-community-plugin-store.ps1 -PluginId crackwatch
```

Pass `-PluginDatabaseRoot C:\path\to\tfs-plugin-database` to write or update the matching entry in a local checkout of the public catalog repository.
