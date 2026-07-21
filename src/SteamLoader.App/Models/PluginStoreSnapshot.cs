namespace SteamLoader.App.Models;

public sealed record PluginStoreImageState(
    string Url,
    string AltText);

public sealed record PluginStorePluginState(
    string Id,
    string Title,
    string Description,
    string Source,
    string Author,
    string Category,
    string Version,
    string InstalledVersion,
    string SdkVersion,
    string EntryPoint,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> InstalledPermissions,
    IReadOnlyList<string> NetworkHosts,
    IReadOnlyList<string> InstalledNetworkHosts,
    string HomepageUrl,
    string RepositoryUrl,
    string Changelog,
    bool IsBuiltIn,
    bool IsAvailableOnline,
    bool IsLocalDevelopment,
    DateTimeOffset? PublishedAtUtc,
    bool IsNew,
    bool IsInstalled,
    bool IsEnabled,
    bool CanToggleVisibility,
    bool CanInstall,
    bool CanUninstall,
    bool HasUpdate,
    string StatusText,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PluginStoreImageState> Images);

public sealed record PluginStoreSnapshot(
    string StatusText,
    string ErrorText,
    string CatalogTitle,
    string CatalogDescription,
    bool IsCustomCatalog,
    string CatalogTrustText,
    bool CommunityCatalogAvailable,
    string CommunityCatalogStatusText,
    int BuiltInCount,
    int CommunityCount,
    int InstalledCommunityCount,
    int UpdateCount,
    IReadOnlyList<PluginStorePluginState> BuiltInPlugins,
    IReadOnlyList<PluginStorePluginState> CommunityPlugins);
