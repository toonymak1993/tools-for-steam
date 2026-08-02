using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

[Flags]
internal enum OmniLibraryGameDataCapabilities
{
    None = 0,
    Metadata = 1 << 0,
    AchievementDefinitions = 1 << 1,
    UserAchievementProgress = 1 << 2,
    StoreAccount = 1 << 3,
    ApiCredential = 1 << 4,
    ExternalProfile = 1 << 5,
    LocalFiles = 1 << 6,
    ManualGameMapping = 1 << 7,
}

internal sealed record OmniLibraryGameDataProviderDescriptor(
    string Id,
    string Title,
    int Order,
    string Description,
    OmniLibraryGameDataCapabilities Capabilities,
    IReadOnlyList<string> StoreIds,
    string SetupKind,
    bool EnabledByDefault = false,
    bool RuntimeAvailable = false)
{
    public bool Supports(OmniLibraryGameDataCapabilities capability) =>
        (Capabilities & capability) == capability;
}

/// <summary>
/// Central registry for game-page data providers. Providers are deliberately
/// independent from download stores: PSN, RetroAchievements and emulator
/// sources can enrich a game without creating a Library tab or background
/// ownership sync. Adding a provider must not require changes to tab topology.
/// </summary>
internal static class OmniLibraryGameDataProviderRegistry
{
    private const OmniLibraryGameDataCapabilities RemoteUserProvider =
        OmniLibraryGameDataCapabilities.Metadata |
        OmniLibraryGameDataCapabilities.AchievementDefinitions |
        OmniLibraryGameDataCapabilities.UserAchievementProgress |
        OmniLibraryGameDataCapabilities.ManualGameMapping;

    private static readonly OmniLibraryGameDataProviderDescriptor[] Providers =
    [
        new(
            "xbox-live",
            "Xbox Network",
            100,
            "Xbox and Windows achievements with account-scoped progress.",
            RemoteUserProvider |
            OmniLibraryGameDataCapabilities.ApiCredential |
            OmniLibraryGameDataCapabilities.StoreAccount |
            OmniLibraryGameDataCapabilities.LocalFiles,
            ["xbox-game-pass", "xbox", "microsoft-store", "windows-store"],
            "openxbl",
            EnabledByDefault: true,
            RuntimeAvailable: true),
        new(
            "epic-games",
            "Epic Games",
            200,
            "Epic achievement definitions and progress from the connected Epic account.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.StoreAccount,
            ["epic-games", "epic"],
            "store-account",
            EnabledByDefault: true,
            RuntimeAvailable: true),
        new(
            "gog",
            "GOG",
            300,
            "GOG Galaxy achievements using OmniLibrary's isolated GOG session.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.StoreAccount,
            ["gog-galaxy", "gog"],
            "store-account",
            EnabledByDefault: true,
            RuntimeAvailable: true),
        new(
            "steam",
            "Steam",
            400,
            "Steam metadata and achievement definitions. Native Steam games remain untouched.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.StoreAccount,
            ["steam"],
            "steam-account",
            RuntimeAvailable: true),
        new(
            "ea",
            "EA",
            500,
            "EA achievement data for games linked to an EA profile.",
            RemoteUserProvider |
            OmniLibraryGameDataCapabilities.ApiCredential |
            OmniLibraryGameDataCapabilities.ExternalProfile,
            ["ea-app", "origin", "electronic-arts"],
            "bearer-account",
            RuntimeAvailable: true),
        new(
            "battle-net",
            "Battle.net",
            600,
            "Battle.net and supported World of Warcraft achievement data.",
            RemoteUserProvider |
            OmniLibraryGameDataCapabilities.ApiCredential |
            OmniLibraryGameDataCapabilities.ExternalProfile,
            ["battle-net", "battlenet", "blizzard"],
            "api-and-account",
            RuntimeAvailable: true),
        new(
            "ubisoft-connect",
            "Ubisoft Connect",
            700,
            "Ubisoft achievement metadata with an optional public-profile fallback.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.ExternalProfile,
            ["ubisoft-connect", "ubisoft", "uplay"],
            "account"),
        new(
            "playstation-network",
            "PlayStation Network",
            800,
            "PSN trophy definitions and account-scoped progress.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.ApiCredential,
            ["playstation-network", "playstation", "psn"],
            "npsso",
            RuntimeAvailable: true),
        new(
            "retroachievements",
            "RetroAchievements",
            900,
            "RetroAchievements definitions and progress using a personal web API key.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.ApiCredential,
            ["retroachievements", "retro-achievements", OmniLibraryRomSystemRegistry.StoreId],
            "username-api-key",
            RuntimeAvailable: true),
        new(
            "rpcs3",
            "RPCS3",
            1000,
            "Local RPCS3 trophy definitions and progress.",
            OmniLibraryGameDataCapabilities.Metadata |
            OmniLibraryGameDataCapabilities.AchievementDefinitions |
            OmniLibraryGameDataCapabilities.UserAchievementProgress |
            OmniLibraryGameDataCapabilities.LocalFiles |
            OmniLibraryGameDataCapabilities.ManualGameMapping,
            ["rpcs3"],
            "local-path"),
        new(
            "shadps4",
            "shadPS4",
            1100,
            "Local shadPS4 trophy definitions and progress.",
            OmniLibraryGameDataCapabilities.Metadata |
            OmniLibraryGameDataCapabilities.AchievementDefinitions |
            OmniLibraryGameDataCapabilities.UserAchievementProgress |
            OmniLibraryGameDataCapabilities.LocalFiles |
            OmniLibraryGameDataCapabilities.ManualGameMapping,
            ["shadps4"],
            "local-path"),
        new(
            "xenia",
            "Xenia",
            1200,
            "Local Xenia Xbox 360 achievement data from the selected account folder.",
            OmniLibraryGameDataCapabilities.Metadata |
            OmniLibraryGameDataCapabilities.AchievementDefinitions |
            OmniLibraryGameDataCapabilities.UserAchievementProgress |
            OmniLibraryGameDataCapabilities.LocalFiles |
            OmniLibraryGameDataCapabilities.ManualGameMapping,
            ["xenia"],
            "local-path"),
        new(
            "ffxiv",
            "Final Fantasy XIV",
            1300,
            "Character achievements from a public Lodestone profile.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.ExternalProfile,
            ["ffxiv"],
            "character-id",
            RuntimeAvailable: true),
        new(
            "hoyoverse",
            "HoYoverse",
            1400,
            "Achievement data for supported HoYoverse games.",
            RemoteUserProvider |
            OmniLibraryGameDataCapabilities.ApiCredential |
            OmniLibraryGameDataCapabilities.ExternalProfile,
            ["hoyoverse", "mihoyo", "hoyoplay"],
            "cookie-account"),
        new(
            "exophase",
            "Exophase",
            1500,
            "Optional cross-platform fallback and rarity enrichment from a public Exophase profile.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.ExternalProfile,
            [],
            "profile"),
        new(
            "apple-game-center",
            "Apple Game Center",
            1600,
            "Provider slot for Game Center compatible metadata sources.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.ExternalProfile,
            ["apple-game-center", "apple", "ios", "app-store"],
            "account"),
        new(
            "google-play-games",
            "Google Play Games",
            1700,
            "Provider slot for Google Play Games compatible metadata sources.",
            RemoteUserProvider | OmniLibraryGameDataCapabilities.ExternalProfile,
            ["google-play-games", "google-play", "android"],
            "account"),
        new(
            "manual",
            "Manual Overrides",
            1800,
            "Per-game provider IDs for titles that cannot be resolved automatically.",
            OmniLibraryGameDataCapabilities.Metadata |
            OmniLibraryGameDataCapabilities.AchievementDefinitions |
            OmniLibraryGameDataCapabilities.ManualGameMapping,
            [],
            "manual"),
    ];

    private static readonly IReadOnlyDictionary<string, OmniLibraryGameDataProviderDescriptor>
        ProvidersById = Providers.ToDictionary(
            provider => provider.Id,
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<OmniLibraryGameDataProviderDescriptor> All { get; } =
        Providers.OrderBy(provider => provider.Order).ToArray();

    public static IReadOnlyList<string> Ids { get; } =
        All.Select(provider => provider.Id).ToArray();

    public static bool TryGet(
        string? providerId,
        out OmniLibraryGameDataProviderDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(providerId) &&
            ProvidersById.TryGetValue(providerId.Trim(), out var resolved))
        {
            descriptor = resolved;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static OmniLibraryGameDataProviderDescriptor GetRequired(string? providerId) =>
        TryGet(providerId, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException("Unknown OmniLibrary game-data provider.");

    public static OmniLibraryGameDataProviderDescriptor? ResolveForStore(
        string? storeId,
        string? deliveryProvider = null)
    {
        // A catalog can hand a title off to another launcher (for example an
        // Epic-owned EA game). Achievement identity follows the launcher that
        // actually owns the account progress, not the outer catalog entry.
        var candidates = new[] { deliveryProvider, storeId }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        foreach (var candidate in candidates)
        {
            var match = All.FirstOrDefault(provider =>
                provider.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                provider.StoreIds.Contains(candidate, StringComparer.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> GetCapabilityIds(
        OmniLibraryGameDataProviderDescriptor descriptor) =>
        Enum.GetValues<OmniLibraryGameDataCapabilities>()
            .Where(capability =>
                capability != OmniLibraryGameDataCapabilities.None &&
                descriptor.Supports(capability))
            .Select(capability => capability switch
            {
                OmniLibraryGameDataCapabilities.Metadata => "metadata",
                OmniLibraryGameDataCapabilities.AchievementDefinitions => "achievement-definitions",
                OmniLibraryGameDataCapabilities.UserAchievementProgress => "user-progress",
                OmniLibraryGameDataCapabilities.StoreAccount => "store-account",
                OmniLibraryGameDataCapabilities.ApiCredential => "api-credential",
                OmniLibraryGameDataCapabilities.ExternalProfile => "external-profile",
                OmniLibraryGameDataCapabilities.LocalFiles => "local-files",
                OmniLibraryGameDataCapabilities.ManualGameMapping => "manual-game-mapping",
                _ => capability.ToString().ToLowerInvariant(),
            })
            .ToArray();

    public static OmniLibraryGameDataState BuildState(
        StoreSyncConfiguration configuration,
        IReadOnlyList<UnifySteamStoreState> stores)
    {
        var gameData = configuration.UnifySteam.GameData;
        var states = All.Select(descriptor =>
        {
            var provider = gameData.Providers[descriptor.Id];
            var relatedStores = stores
                .Where(store => descriptor.StoreIds.Contains(
                    store.Id,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var storeConnected = relatedStores.Any(store => store.AuthReady);
            var configured = descriptor.SetupKind switch
            {
                "openxbl" => !string.IsNullOrWhiteSpace(provider.Credential),
                "store-account" => storeConnected,
                "steam-account" => true,
                "local-path" => !string.IsNullOrWhiteSpace(provider.DataPath),
                "username-api-key" =>
                    !string.IsNullOrWhiteSpace(provider.AccountName) &&
                    !string.IsNullOrWhiteSpace(provider.Credential),
                "npsso" => !string.IsNullOrWhiteSpace(provider.Credential),
                "bearer-account" => !string.IsNullOrWhiteSpace(provider.Credential),
                "character-id" => !string.IsNullOrWhiteSpace(provider.AccountId),
                "cookie-account" =>
                    !string.IsNullOrWhiteSpace(provider.AccountId) &&
                    !string.IsNullOrWhiteSpace(provider.Credential),
                "api-and-account" =>
                    !string.IsNullOrWhiteSpace(provider.AccountName) &&
                    !string.IsNullOrWhiteSpace(provider.Credential) &&
                    !string.IsNullOrWhiteSpace(provider.SecondaryCredential),
                "account" or "profile" =>
                    !string.IsNullOrWhiteSpace(provider.AccountName),
                "manual" => true,
                _ => storeConnected ||
                     !string.IsNullOrWhiteSpace(provider.Credential) ||
                     !string.IsNullOrWhiteSpace(provider.SecondaryCredential) ||
                     !string.IsNullOrWhiteSpace(provider.AccountId) ||
                     !string.IsNullOrWhiteSpace(provider.AccountName) ||
                     !string.IsNullOrWhiteSpace(provider.DataPath),
            };
            var status = !descriptor.RuntimeAvailable
                ? "adapter-pending"
                : !gameData.Enabled || !provider.Enabled
                ? "disabled"
                : configured
                    ? "ready"
                    : "setup-required";
            var detail = status switch
            {
                "adapter-pending" =>
                    "The provider is known, but its safe OmniLibrary runtime is not included in this build yet.",
                "disabled" => "Provider is disabled and performs no background work.",
                "ready" when storeConnected =>
                    $"Uses the connected {relatedStores.First(store => store.AuthReady).Title} account.",
                "ready" => "Provider configuration is stored locally and loaded on demand.",
                _ => descriptor.SetupKind switch
                {
                    "openxbl" => "Add a personal OpenXBL API key.",
                    "store-account" => "Connect the matching OmniLibrary store first.",
                    "local-path" => "Choose the provider's local data folder.",
                    "username-api-key" => "Enter the account name and personal API key.",
                    "npsso" => "Connect a PlayStation account with an NPSSO token.",
                    "bearer-account" => "Add the provider access token. The account identity is resolved automatically.",
                    _ => "Open this provider to complete its account or profile setup.",
                },
            };

            return new OmniLibraryGameDataProviderState(
                descriptor.Id,
                descriptor.Title,
                descriptor.Description,
                descriptor.RuntimeAvailable,
                provider.Enabled,
                configured,
                status,
                detail,
                provider.ConnectionStatus,
                provider.ConnectionDetail,
                provider.ConnectionCheckedAtUtc,
                descriptor.SetupKind,
                PreviewSecret(provider.Credential),
                provider.AccountName,
                provider.AccountId,
                provider.Region,
                provider.Locale,
                provider.DataPath,
                GetCapabilityIds(descriptor),
                descriptor.StoreIds);
        }).ToArray();

        return new OmniLibraryGameDataState(
            gameData.Enabled,
            states.Count(provider => provider.RuntimeAvailable && provider.Enabled),
            states.Count(provider =>
                provider.RuntimeAvailable &&
                provider.Enabled &&
                provider.Configured),
            states);
    }

    private static string PreviewSecret(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.Length <= 8
            ? "configured"
            : $"{trimmed[..4]}...{trimmed[^4..]}";
    }
}
