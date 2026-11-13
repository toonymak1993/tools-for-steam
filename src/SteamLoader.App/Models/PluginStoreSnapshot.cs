namespace SteamLoader.App.Models;

public sealed record PluginStoreImageState(
    string Url,
    string AltText);

/// <summary>An integration that ships with Tools for Steam (Plugin Activator entry).</summary>
public sealed record PluginStorePluginState(
    string Id,
    string Title,
    string Description,
    string Source,
    string Author,
    string Category,
    string Version,
    bool IsBuiltIn,
    bool IsInstalled,
    bool IsEnabled,
    bool CanToggleVisibility,
    string StatusText,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PluginStoreImageState> Images);

public sealed record PluginStoreSnapshot(
    string StatusText,
    string ErrorText,
    string CatalogTitle,
    string CatalogDescription,
    int BuiltInCount,
    IReadOnlyList<PluginStorePluginState> BuiltInPlugins);
