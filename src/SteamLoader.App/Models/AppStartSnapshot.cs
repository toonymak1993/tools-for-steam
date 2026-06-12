namespace SteamLoader.App.Models;

public sealed record AppStartSnapshot(
    IReadOnlyList<AppStartShortcutState> Shortcuts,
    string StatusText);

public sealed record AppStartCatalogSnapshot(
    IReadOnlyList<AppStartCatalogEntry> Apps,
    string StatusText);

public sealed record AppStartShortcutState(
    string Id,
    string Name,
    string SourcePath,
    string? IconDataUri);

public sealed record AppStartCatalogEntry(
    string Id,
    string Name,
    string SourcePath,
    string? IconDataUri,
    bool Added);
