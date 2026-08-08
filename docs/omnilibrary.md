# OmniLibrary

OmniLibrary brings external account libraries into Steam's native Library UI. The current beta supports Xbox / PC Game Pass, optional Xbox Cloud, Epic Games, and GOG. It is an alternative to Store Sync: enabling one disables and hides the other so their background work and managed entries never compete.

## User flow

1. Open **OmniLibrary** in Quick Access and enable only the stores you want.
2. Use **Download Center** at the top of the plugin to inspect all current and recent store transfers.
3. Xbox reuses the signed-in official Xbox app. Epic and GOG sign in through TFS's isolated browser window.
4. Let the initial catalog and artwork preparation finish.
5. Use **Restart Steam Now** when OmniLibrary reports that a restart is required.
6. Browse the dedicated **Xbox**, optional **Xbox Cloud**, **Epic**, and **GOG** tabs with LB/RB.
7. Open a game normally. Steam's original action reads **Download** while the managed game is absent and returns to its original **Play** label once installed.
8. For an installed managed game, use Steam's controller context menu for the confirmed uninstall flow.

The plugin page contains settings plus the shared Download Center. Browsing and selecting new games still happens on Steam's existing Library screens.

## Native Steam bridge

Catalog items are represented by managed non-Steam entries with stable provider IDs. Delta sync updates only added, changed, or removed games after the first preparation; unchanged artwork is reused. Each enabled, connected, fully prepared provider is published as its own tab. Disabled or disconnected providers do no polling and do not expose a tab.

The detail-page surface first resolves the current Steam app ID against OmniLibrary's own cache. It changes labels and exposes managed context actions only for that exact managed entry. Native Steam games and unrelated non-Steam games are not modified.

## Download Center

The first section in OmniLibrary is a controller-native Download Center shared by every enabled provider. Multiple transfers remain separate and are ordered by urgency: active work first, then finalization or cleanup, items requiring attention, failures, and recent history.

- Each row exposes its store, phase, percentage, transferred size, speed, estimated time, and the provider's current detail message when available.
- Starting a download publishes an optimistic `Preparing` state immediately. The library tile switches from the static download badge to an animated transfer badge without waiting for a catalog refresh.
- Epic and GOG transfers can be paused, resumed from their helper checkpoint, canceled, and have only their own partial directory removed. Destructive cleanup requires a second confirmation.
- Completed games can be launched from the center. Completed and canceled history can be dismissed without removing an installed game.
- Xbox acquisition is owned by Windows and the official Xbox app. The center tracks its progress and opens the exact product page for native pause, resume, or cancellation instead of pretending an unsupported private control succeeded.
- Active transfers refresh every two seconds only while the Download Center is visible; idle history backs off to ten seconds and hidden UI does no polling.
- Catalog metadata and live transfer status use separate caches. A percentage update never rebuilds the full external library.
- Transfer and cleanup workers persist independently of the Quick Access UI. Disabling OmniLibrary stops new catalog work and removes its Steam UI, but it does not terminate or corrupt an already-running download.

## Sync and performance

- A lightweight change check runs every five minutes for each enabled provider.
- Only catalog or install-state deltas queue shortcut and artwork work.
- Active downloads are observed more frequently only while their game page is visible.
- Active install and uninstall lifecycles use the lightweight two-second status path. Completion triggers one compact installed-state delta refresh; it never queues a catalog or artwork rebuild.
- Active managed downloads block system sleep but do not force the display to remain on.
- The native Steam action animates while work is active, and a compact phase panel explains preparation, queueing, transfer, reconnect, pause, finalization, required store action, or a confirmed failure.
- Network, processing, disk throughput, downloaded size, and estimated remaining time are shown when the provider exposes those values.
- `Retry Download` is reserved for confirmed terminal failures; transient interruptions reconnect automatically with the saved progress.
- Active Xbox, Epic, and GOG operations are recovered after Steam, TFS, or Windows restarts, even if the OmniLibrary UI is disabled afterward.
- Idle game pages and hidden Steam UI surfaces back off to longer refresh intervals.
- File mutations are serialized per provider so simultaneous Epic or GOG installs and uninstalls cannot corrupt helper state.
- A failed recovery or cleanup remains isolated to that game; it cannot prevent another store's transfer from resuming.
- The first preparation may require substantial artwork work; later syncs reuse cached assets.

### Artwork source order

Artwork is filled progressively without replacing a usable Steam slot. The source
order is fixed so SteamGridDB is never a single point of failure:

1. Existing Steam grid files, Steam's local `appcache\librarycache`, explicit
   `file://` images, ROM sidecars, bounded artwork folders inside an installed
   game, and the installed executable's icon.
2. Exact provider-owned images and free provider APIs, including Xbox/Epic/GOG
   catalog assets and RetroAchievements for content-hashed ROMs.
3. The public Steam Store search and CDN, which require no API key.
4. SteamGridDB only for slots still missing after every preceding source.

When real primary artwork exists locally, missing shapes, the small icon, and a
readable title logo are generated locally before any network request is attempted.

**Reload All Artwork** in OmniLibrary performs the same source chain again for
every enabled, managed title. It requires a second confirmation and builds all
five replacement slots in an isolated staging directory first. Existing artwork
is replaced only after that title has a complete valid set, so an unavailable
provider or fallback cannot turn a populated library into blank tiles. Native
Steam games and unrelated non-Steam shortcuts are never included.

## Xbox behavior

The Xbox library combines the public PC Game Pass PC catalog, optional cloud catalog data, and locally detected `XboxGames` manifests. A configured library path is scanned in addition to standard fixed-drive roots.

**Download** makes a best-effort request through Windows' Store installation broker. That broker is protected by a private Microsoft capability, so a normal public build can be denied. The safe fallback opens only `msxbox://game/?productId=<id>` for the exact product and never clicks or confirms anything on the user's behalf. Download percentages are best-effort observations of Windows/Xbox deployment events and are reconciled with the installed package scan at completion.

Xbox uninstall is deliberately user-controlled: OmniLibrary opens the exact Xbox product page after confirmation, the user finishes removal in the Xbox app, and TFS verifies only that game's known Content path and package registration twice before changing it back to Download. This targeted probe stops as soon as removal is confirmed and avoids rescanning the catalog, artwork, or every installed game. Microsoft does not expose a consumer API that enumerates every separately purchased Xbox entitlement for an arbitrary desktop client, so OmniLibrary does not claim a complete private purchase library.

## Epic behavior

Epic sign-in uses an isolated WebView2 profile and does not import Epic Games Launcher credentials. On first use, TFS downloads Legendary 0.20.43 from the official Heroic-Games-Launcher release and verifies the pinned SHA-256 before execution. Legendary uses an isolated configuration under `data\omnilibrary\legendary`.

Downloads and confirmed uninstalls run through Legendary. TFS combines
Legendary's checkpoint with completed on-disk files so resumed downloads retain
their absolute percentage instead of returning to zero. Steam's native action
also shows the current network rate. Transient exits retry up to five times with
progressive backoff, and an interrupted active transfer is recovered after the
next TFS start. The durable status cache uses
write-through replacement and a backup so an unexpected reboot cannot turn the
file into an unrecoverable empty state.

Epic download settings expose 1-32 workers and a 15-300 second connection
timeout; the defaults are 16 workers and 60 seconds. Before downloading, TFS
checks the final manifest size against available capacity plus a 15 GiB safety
reserve. A rotating `data\omnilibrary-epic-download.log` captures network,
decompression, disk-write, and disk-read throughput without recording account
credentials.

Removing a game deletes local game files but keeps the account library entry
available for another download. **Disconnect Epic Account** is separately
confirmed and removes the isolated Epic session, browser data, cached account
catalog, and managed Epic shortcuts; it does not uninstall game files.

See `ThirdParty\Legendary\NOTICE.txt` in an installed or portable build for the exact upstream version, hash, license, and corresponding source.

## GOG behavior

GOG sign-in uses the same isolated WebView2 contract as Epic and does not import
GOG Galaxy or Heroic credentials. On first use, TFS downloads heroic-gogdl 1.2.2
from its official release, verifies the pinned SHA-256, and stores its session
under `data\omnilibrary\gog`.

The first sync prepares the complete owned Windows catalog. Later five-minute
probes compare the owned product-ID set: only additions request new metadata and
artwork, while removals keep unchanged shortcuts and cached images intact.
Managed downloads and uninstalls use the configured GOG install root. An
installation owned by GOG Galaxy is never recursively deleted by TFS; its exact
Galaxy game page is opened instead.

GOG downloads keep their partial files and helper checkpoint across restarts,
retry transient exits up to five times with progressive backoff, and perform the
same capacity preflight used by Epic. A rotating
`data\omnilibrary-gog-download.log` records transfer diagnostics without account
credentials. If managed downloading is unavailable, OmniLibrary opens the exact
GOG Galaxy game page and displays **Action required** instead of presenting that
fallback as a failed download.

Every managed GOG install also has a small atomic transaction journal. It records
whether TFS is preparing, downloading, verifying files, completing Windows setup,
or ready, so a process or Windows restart resumes the correct phase instead of
starting the full installation again. Network, decompression, disk throughput,
and exact helper progress are sampled without recursively walking large install
directories. The local GOG registry is read once into a short-lived snapshot;
only active operations and installed products are probed, and two missing
observations are required before an uninstall is accepted.

GOG settings include DLC installation, worker count, connection timeout, and an
optional **Use GOG Galaxy for Play** mode for users who want Galaxy-owned cloud
saves or achievements. Installed build metadata is checked in small batches, so
available updates appear on the existing Steam action without refreshing the
full catalog. OmniLibrary-managed installations expose **Verify & Repair...** in
Steam's controller context menu. Galaxy-owned installations deliberately do not:
their repair and uninstall controls remain in Galaxy, and TFS never treats their
directory as its own.

See `ThirdParty\GogDl\NOTICE.txt` in an installed or portable build for the exact
upstream version, hash, license, and corresponding source.

## Local data and security

- Xbox passwords and tokens stay inside the official Xbox app.
- TFS checks only whether Microsoft's account/token cache has sign-in state; it never copies token contents.
- Epic and GOG session data is isolated from their official launchers and can be removed from OmniLibrary settings.
- Provider and game IDs are validated against the cached library before launch, install, or uninstall workers start.
- Worker arguments use `ProcessStartInfo.ArgumentList`; game IDs are never concatenated into a shell command.
- Partial-file deletion is restricted to a strict child of the configured provider install root and refuses filesystem reparse points.
- Xbox direct install and progress are capability-dependent, best effort, and always have a non-automated official-page fallback.

## Release limitations

OmniLibrary patches Steam Big Picture UI surfaces that Valve can change without notice. A Steam client update can temporarily affect tab layout, controller navigation, or detail-page discovery. TFS keeps store data and game files intact if the visual patch cannot attach, and disabling OmniLibrary stops its background checks.
