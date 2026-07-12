namespace SteamLoader.App.Models;

public sealed record PluginStoreCommunityRuntimePluginState(
    string Id,
    string Title,
    string Description,
    string Version,
    string SdkVersion,
    string EntryPoint,
    string ScriptUrl,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> NetworkHosts);

public sealed record PluginStoreCommunityRuntimeState(
    IReadOnlyList<PluginStoreCommunityRuntimePluginState> Plugins);
