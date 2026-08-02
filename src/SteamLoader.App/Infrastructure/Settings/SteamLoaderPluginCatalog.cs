namespace SteamLoader.App.Infrastructure.Settings;

public static class SteamLoaderPluginCatalog
{
    public static readonly IReadOnlyList<SteamLoaderPluginDefinition> Definitions =
        BuildDefinitions();

    internal static IReadOnlyList<SteamLoaderPluginDefinition> BuildDefinitions()
    {
        var definitions = new List<SteamLoaderPluginDefinition>
        {
        new("processes", "Processes", "Window switcher for visible app windows.", true),
        new("app-start", "Apps", "One-click launcher for installed Windows apps.", true),
        new("store-sync", "Store Sync", "Launcher sync, Steam shortcuts, artwork updates, and store collections.", true, false),
        new("omnilibrary", "OmniLibrary", "Bring Xbox and Epic libraries into dedicated Steam tabs with download, play, and safe uninstall actions.", true, false),
        new("tabhero", "Tabhero", "Rename, hide, reorder, and add filtered Steam Library tabs without changing tabs owned by other plugins.", true),
        new("auto-sisr", "Auto SISR", "Starts SISR marker mode for selected non-Steam games.", true, false),
        new("artwork", "SteamGridDB", "Context menu artwork picker and manual artwork settings.", true),
        new("audio", "Audio", "Output device switching and system volume controls.", true),
        new("display", "Display", "Display switching, resolution, and refresh rate controls.", true),
        new("performance", "Performance", "RTSS overlay modes, live metrics, and per-game FPS limiting.", true),
        new("hltb", "HLTB", "HowLongToBeat game page estimates.", true),
        new("discord", "Discord", "See online friends and browse servers with presence counts.", true, false),
        new("themes", "CSSLoader", "Controller for local CSSLoader themes, presets, and backend tools.", true),
        new("smart-home", "Homey", "Rooms, lights, moods, colors, and flows from Homey with a provider-neutral foundation.", true, false),
        new("power", "Power", "Recovery and power actions. This stays available for safety.", false),
        };

        return definitions;
    }

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
