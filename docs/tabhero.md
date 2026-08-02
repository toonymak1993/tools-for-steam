# Tabhero

Tabhero is Tools for Steam's controller-native Library tab manager. It can rename, hide, reorder, and reset Steam's native tabs, and it can create removable custom tabs from filters. Layout profiles save and restore the editable part of the tab row.

Open **Quick Access > TFS > Tabhero** to manage tabs, create filtered tabs, or switch profiles. Changes are stored locally and are broadcast to every injected Steam surface, so the Library updates without requiring two competing tab patchers. The Tabs page can undo the latest editable change and reveal every hidden Steam tab without affecting custom or protected tabs.

## Ownership and OmniLibrary compatibility

Steam, Tabhero, and other Tools for Steam features share one compositor in `library-tabs.js`. Tabhero never patches the Library row independently.

Every tab whose identifier begins with `tfs-`, or which explicitly belongs to OmniLibrary, OmniConsole, Tools for Steam, or another non-Steam plugin, is protected. Protected tabs are discovered and shown in Tabhero, but cannot be renamed, hidden, deleted, or moved. Their owning plugin remains the only authority for their lifecycle and relative order. The complete protected group is inserted as one stable block after Steam's Non-Steam tab, or after Installed when Non-Steam is hidden. This rule is the same whether OmniLibrary is enabled, disabled, loading, or changes providers at runtime.

Tabhero's own custom tabs use the `tabhero-` namespace. Deleting one only removes that custom definition; it does not delete a Steam collection, shortcut, game, or protected plugin tab.

## Custom tabs and filters

The New Tab page includes presets for all games, installed and Non-Steam apps, recently played games, Favorites, Deck Verified games, family-shared games, demos, and coming-soon titles. Choosing a preset fills both its useful name and filter. The advanced JSON editor remains available for combinations. An empty array includes every game. Multiple filters normally use AND; enable **Match Any Filter** for OR. Every filter accepts `"inverted": true`.

```json
[
  { "type": "installed", "params": { "installed": true } },
  { "type": "last played", "params": { "condition": "above", "daysAgo": 30 } }
]
```

Supported filters cover Steam collections, installed state, regular expressions, friends, tags, whitelist and blacklist, nested merge groups, platform, Steam Deck and SteamOS compatibility, review score, time played, size on disk, release/purchase/last-played dates, family sharing, demos, coming-soon games, streamability, Steam features, achievements, SD cards, and install folders. Regex patterns are compiled once and bounded; patterns with known catastrophic backtracking shapes are rejected before they can block Steam's render thread.

Tabhero evaluates filters against Steam's live app overview objects and existing Steam caches; it does not add a network poll for filter tabs. Collection app lookups are shared within the compositor, unchanged filter results are reused briefly, and virtual collections are updated only when their resulting app-id set changes. Settings changes invalidate these caches immediately.

Custom tabs can be duplicated as independent copies and moved directly to the start or end. Generated identifiers are collision-safe, including after tabs have been deleted and recreated.

## Profiles and recovery

A profile stores native-tab titles and visibility, editable ordering, and the enabled state of Tabhero custom tabs. It deliberately excludes protected tabs. If every editable native tab is hidden and no protected tab is available, Tabhero keeps one Steam tab visible as a recovery route without discarding the saved layout.

Disabling Tabhero stops applying its layout while preserving the settings. OmniLibrary continues to own and render its tabs independently.

Steam renders Quick Access and Library content in separate webviews. Every Tabhero write first merges with the newest saved revision, so catalog discovery in one surface cannot overwrite a rename, filter, profile, or visibility change made at nearly the same time in another.

## Inspiration

Tabhero is a separate Tools for Steam implementation inspired by [Tormak9970/TabMaster](https://github.com/Tormak9970/TabMaster), including its broad filter vocabulary and focus on controller-friendly Library customization. Credit goes to Tormak9970 and TabMaster's contributors for the original concept and prior art.
