namespace SteamLoader.App.Infrastructure.Settings;

public static class SteamLoaderPluginCatalog
{
    public static readonly IReadOnlyList<SteamLoaderPluginDefinition> Definitions =
    [
        new("processes", "Processes", "Window switcher for visible app windows.", true),
        new("app-start", "App Start", "Controller launcher for selected Windows apps.", true),
        new("store-sync", "Store Sync", "Launcher sync, Steam shortcuts, artwork updates, and store collections.", true),
        new("unifystore", "Storefront", "Fullscreen launcher for connected Epic and GOG account libraries.", true),
        new("auto-sisr", "Auto SISR", "Starts SISR marker mode for selected non-Steam games.", true, false),
        new("artwork", "SteamGridDB", "Context menu artwork picker and manual artwork settings.", true),
        new("audio", "Audio", "Output device switching and system volume controls.", true),
        new("display", "Display", "Display switching, resolution, and refresh rate controls.", true),
        new("performance", "Performance", "Built-in TFS FPS meter and Steam-style overlay controls.", true),
        new("hltb", "HLTB", "HowLongToBeat game page estimates.", true),
        new("themes", "CSSLoader", "Controller for local CSSLoader themes, presets, and backend tools.", true),
        new("smart-home", "Homey", "Rooms, lights, moods, colors, and flows from Homey with a provider-neutral foundation.", true, false),
        new("power", "Power", "Recovery and power actions. This stays available for safety.", false),
    ];

    public static SteamLoaderPluginDefinition? Find(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        return Definitions.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record SteamLoaderPluginDefinition(
    string Id,
    string Title,
    string Description,
    bool CanDisable,
    bool DefaultEnabled = true);
