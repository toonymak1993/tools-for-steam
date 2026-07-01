namespace SteamLoader.App.Models;

public sealed record ThemeChoiceState(
    string Id,
    string Title);

public sealed record ThemeOptionState(
    string Id,
    string Title,
    string Description,
    string Type,
    bool? BoolValue,
    int? NumberValue,
    int? Min,
    int? Max,
    int? Step,
    string? Unit,
    string? SelectedChoiceId,
    IReadOnlyList<ThemeChoiceState> Choices,
    int AdvancedControlCount);

public sealed record ThemeState(
    string Id,
    string Title,
    string Author,
    string Version,
    string Description,
    string StoreDescription,
    bool Installed,
    bool Enabled,
    string StatusText,
    string SourceLabel,
    int DownloadCount,
    IReadOnlyList<string> Targets,
    IReadOnlyList<ThemeOptionState> Options,
    int DependencyCount,
    int AdvancedControlCount);

public sealed record ThemeProfileThemeState(
    string ThemeId,
    string ThemeTitle,
    bool Installed,
    bool Enabled,
    int OptionCount);

public sealed record ThemeProfileState(
    string Id,
    string Title,
    string Author,
    string Description,
    string Version,
    string StatusText,
    string SourceLabel,
    int DownloadCount,
    bool Installed,
    bool Selected,
    bool MatchesCurrentSetup,
    IReadOnlyList<ThemeProfileThemeState> Themes);

public sealed record ThemesProfilesState(
    string? SelectedProfileId,
    bool CurrentSetupMatchesSelectedProfile,
    IReadOnlyList<ThemeProfileState> InstalledProfiles,
    IReadOnlyList<ThemeProfileState> BrowseProfiles);

public sealed record ThemesSettingsState(
    bool ThemeEngineEnabled,
    bool ShowCommunityThemes,
    bool SingleThemeMode,
    bool AutoEnableOnInstall);

public sealed record ThemeLoadErrorState(
    string Title,
    string Message);

public sealed record ThemeStoreThemeState(
    string StoreId,
    string ThemeId,
    string Title,
    string Author,
    string Version,
    string Description,
    string Source,
    string Target,
    IReadOnlyList<string> Targets,
    int DownloadCount,
    int StarCount,
    int DependencyCount,
    bool Installed,
    bool InstalledVersionMatches,
    string StatusText,
    string PreviewImageUrl,
    string PreviewThumbnailUrl);

public sealed record ThemeStoreCatalogState(
    string Search,
    string Filter,
    string Order,
    int Page,
    int PerPage,
    int Total,
    IReadOnlyList<string> AvailableFilters,
    IReadOnlyList<string> AvailableOrders,
    IReadOnlyList<ThemeStoreThemeState> Items);

public sealed record ThemeIntegrationState(
    bool BackendReachable,
    bool BackendInstalled,
    string ThemePath,
    string BackendPath,
    int? BackendVersion,
    bool WatchEnabled,
    IReadOnlyList<ThemeLoadErrorState> LoadErrors);

public sealed record ThemesSnapshot(
    ThemesSettingsState Settings,
    IReadOnlyList<ThemeState> InstalledThemes,
    IReadOnlyList<ThemeState> BrowseThemes,
    ThemesProfilesState Profiles,
    string ActiveCss,
    string StatusText,
    string LocalThemesFolder,
    ThemeIntegrationState Integration);
