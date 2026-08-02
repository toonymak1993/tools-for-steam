using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

[Flags]
internal enum OmniLibraryStoreCapabilities
{
    None = 0,
    ManagedWebSignIn = 1 << 0,
    WindowsAccountSignIn = 1 << 1,
    ManualCodeSignIn = 1 << 2,
    InstallPath = 1 << 3,
    PcCatalogSource = 1 << 4,
    CloudCatalogSource = 1 << 5,
    ManagedInstall = 1 << 6,
    ProductPageInstallFallback = 1 << 7,
    ManagedUninstall = 1 << 8,
    ProductPageUninstall = 1 << 9,
    LocalLibrary = 1 << 10,
}

internal sealed record OmniLibraryStoreDescriptor(
    string Id,
    string Title,
    int Order,
    string ToolCommandName,
    string ToolExecutableName,
    OmniLibraryStoreCapabilities Capabilities,
    IReadOnlyList<OmniLibraryTabDescriptor> LibraryTabs)
{
    public bool Supports(OmniLibraryStoreCapabilities capability) =>
        (Capabilities & capability) == capability;

    public bool SupportsManualCodeAuth =>
        Supports(OmniLibraryStoreCapabilities.ManualCodeSignIn);
}

internal sealed record OmniLibraryTabDescriptor(
    string Id,
    string Title,
    string Filter,
    bool RequiresCloudSource = false);

/// <summary>
/// The single registry for OmniLibrary store behavior and Library surfaces.
/// Adding a store starts here; settings, summaries and tab order consume this
/// descriptor instead of maintaining separate store-id lists.
/// </summary>
internal static class OmniLibraryStoreRegistry
{
    private static readonly OmniLibraryStoreDescriptor[] Stores =
    [
        new(
            "xbox-game-pass",
            "Xbox",
            100,
            "Xbox app",
            "XboxPcApp.exe",
            OmniLibraryStoreCapabilities.WindowsAccountSignIn |
            OmniLibraryStoreCapabilities.InstallPath |
            OmniLibraryStoreCapabilities.PcCatalogSource |
            OmniLibraryStoreCapabilities.CloudCatalogSource |
            OmniLibraryStoreCapabilities.ProductPageInstallFallback |
            OmniLibraryStoreCapabilities.ProductPageUninstall,
            [
                new("tfs-xbox", "Xbox", "non-cloud"),
                new("tfs-xbox-cloud", "Xbox Cloud", "cloud", RequiresCloudSource: true),
            ]),
        new(
            "epic-games",
            "Epic Games",
            200,
            "legendary",
            "legendary.exe",
            OmniLibraryStoreCapabilities.ManagedWebSignIn |
            OmniLibraryStoreCapabilities.ManualCodeSignIn |
            OmniLibraryStoreCapabilities.InstallPath |
            OmniLibraryStoreCapabilities.ManagedInstall |
            OmniLibraryStoreCapabilities.ManagedUninstall,
            [
                new("tfs-epic", "Epic", "all"),
            ]),
        new(
            "gog-galaxy",
            "GOG",
            300,
            "gogdl",
            "gogdl.exe",
            OmniLibraryStoreCapabilities.ManagedWebSignIn |
            OmniLibraryStoreCapabilities.ManualCodeSignIn |
            OmniLibraryStoreCapabilities.InstallPath |
            OmniLibraryStoreCapabilities.ManagedInstall |
            OmniLibraryStoreCapabilities.ManagedUninstall |
            OmniLibraryStoreCapabilities.ProductPageInstallFallback |
            OmniLibraryStoreCapabilities.ProductPageUninstall,
            [
                new("tfs-gog", "GOG", "all"),
            ]),
        new(
            OmniLibraryRomSystemRegistry.StoreId,
            "Emulation",
            400,
            "PPSSPP",
            "PPSSPPWindows64.exe",
            OmniLibraryStoreCapabilities.InstallPath |
            OmniLibraryStoreCapabilities.PcCatalogSource |
            OmniLibraryStoreCapabilities.LocalLibrary,
            // ROM systems are data-driven. BuildLibraryTabSummaries emits one
            // native tab only for each platform that currently has games.
            []),
    ];

    private static readonly IReadOnlyDictionary<string, OmniLibraryStoreDescriptor> StoresById =
        Stores.ToDictionary(store => store.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<OmniLibraryStoreDescriptor> All { get; } =
        Stores.OrderBy(store => store.Order).ToArray();

    public static IReadOnlyList<string> Ids { get; } =
        All.Select(store => store.Id).ToArray();

    public static bool TryGet(string? storeId, out OmniLibraryStoreDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(storeId) &&
            StoresById.TryGetValue(storeId.Trim(), out var resolved))
        {
            descriptor = resolved;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static OmniLibraryStoreDescriptor GetRequired(string? storeId)
    {
        return TryGet(storeId, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException("Unknown OmniLibrary store.");
    }

    public static IReadOnlyList<UnifySteamLibraryTabSummary> BuildLibraryTabSummaries(
        OmniLibraryStoreDescriptor descriptor,
        IReadOnlyList<OmniLibraryRomSystemState>? romSystems = null)
    {
        if (descriptor.Id.Equals(
                OmniLibraryRomSystemRegistry.StoreId,
                StringComparison.OrdinalIgnoreCase))
        {
            return (romSystems ?? [])
                .Where(system =>
                    system.GameCount > 0 &&
                    system.AppIds.Any(appId => appId != 0))
                .OrderBy(system => system.Title, StringComparer.OrdinalIgnoreCase)
                .Select(system => new UnifySteamLibraryTabSummary(
                    OmniLibraryRomSystemRegistry.BuildLibraryTabId(system.Id),
                    system.Title,
                    $"platform:{system.Id}",
                    RequiresCloudSource: false))
                .ToArray();
        }

        return descriptor.LibraryTabs
            .Select(tab => new UnifySteamLibraryTabSummary(
                tab.Id,
                tab.Title,
                tab.Filter,
                tab.RequiresCloudSource))
            .ToArray();
    }

    public static IReadOnlyList<string> GetCapabilityIds(
        OmniLibraryStoreDescriptor descriptor)
    {
        return Enum.GetValues<OmniLibraryStoreCapabilities>()
            .Where(capability =>
                capability != OmniLibraryStoreCapabilities.None &&
                descriptor.Supports(capability))
            .Select(capability => capability switch
            {
                OmniLibraryStoreCapabilities.ManagedWebSignIn => "managed-web-sign-in",
                OmniLibraryStoreCapabilities.WindowsAccountSignIn => "windows-account-sign-in",
                OmniLibraryStoreCapabilities.ManualCodeSignIn => "manual-code-sign-in",
                OmniLibraryStoreCapabilities.InstallPath => "install-path",
                OmniLibraryStoreCapabilities.PcCatalogSource => "pc-catalog-source",
                OmniLibraryStoreCapabilities.CloudCatalogSource => "cloud-catalog-source",
                OmniLibraryStoreCapabilities.ManagedInstall => "managed-install",
                OmniLibraryStoreCapabilities.ProductPageInstallFallback => "product-page-install-fallback",
                OmniLibraryStoreCapabilities.ManagedUninstall => "managed-uninstall",
                OmniLibraryStoreCapabilities.ProductPageUninstall => "product-page-uninstall",
                OmniLibraryStoreCapabilities.LocalLibrary => "local-library",
                _ => capability.ToString().ToLowerInvariant(),
            })
            .ToArray();
    }
}
