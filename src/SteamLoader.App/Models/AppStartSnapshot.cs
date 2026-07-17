namespace SteamLoader.App.Models;

public sealed record AppStartSnapshot(
    IReadOnlyList<AppStartShortcutState> Shortcuts,
    string StatusText,
    DateTimeOffset? LastIndexedAtUtc = null);

public sealed record AppStartCatalogSnapshot(
    IReadOnlyList<AppStartCatalogEntry> Apps,
    string StatusText);

public sealed record AppStartShortcutState(
    string Id,
    string Name,
    string SourcePath,
    string? IconDataUri,
    bool Favorite = false,
    string SourceKind = "desktop");

public sealed record AppStartCatalogEntry(
    string Id,
    string Name,
    string SourcePath,
    string? IconDataUri,
    bool Added,
    bool Favorite = false,
    bool Hidden = false,
    string SourceKind = "desktop");
