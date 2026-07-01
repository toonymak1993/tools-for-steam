using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Themes;

public sealed class ThemesService
{
    private static readonly Uri DeckThemesApiBaseUri = new("https://api.deckthemes.com/");
    private static readonly Uri CssLoaderBackendLatestReleaseUri =
        new("https://api.github.com/repos/DeckThemes/SDH-CssLoader/releases/latest");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, object?> EmptyArgs =
        new Dictionary<string, object?>();
    private static readonly string[] DefaultStoreOrders =
    [
        "Most Downloaded",
        "Last Updated",
        "Most Stars",
        "Alphabetical (A to Z)"
    ];
    private const string BigPictureStoreType = "BPM-CSS";
    private const int DefaultStorePage = 1;
    private const int DefaultStorePerPage = 12;
    private const int MaximumStorePerPage = 24;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly Uri _backendUri;
    private readonly string _backendExecutablePath;
    private readonly string _legacyStartupBackendExecutablePath;
    private readonly string _fallbackThemePath;

    public ThemesService(HttpClient httpClient, Uri backendUri)
    {
        _httpClient = httpClient;
        _backendUri = backendUri;
        _backendExecutablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolsForSteam",
            "CSSLoader",
            "CssLoader-Standalone-Headless.exe");
        _legacyStartupBackendExecutablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "CssLoader-Standalone-Headless.exe");
        _fallbackThemePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "homebrew",
            "themes");
    }

    public async Task<ThemesSnapshot> GetSnapshotAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await BuildSnapshotCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThemesSnapshot> RefreshCatalogAsync()
    {
        return await ExecuteMutationAsync(async () =>
        {
            if (await IsBackendReachableAsync())
            {
                await InvokeMethodAsync<object>("reset");
            }
        });
    }

    public async Task<ThemeStoreCatalogState> GetStoreCatalogAsync(
        string? search,
        string? filter,
        string? order,
        int page,
        int perPage)
    {
        await _gate.WaitAsync();
        try
        {
            return await BuildStoreCatalogCoreAsync(search, filter, order, page, perPage);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThemeStoreThemeState> GetStoreThemeAsync(string storeThemeId)
    {
        await _gate.WaitAsync();
        try
        {
            var installedThemes = await TryGetInstalledThemesByNameAsync();
            var storeTheme = await GetStoreThemeCoreAsync(storeThemeId);
            return BuildStoreThemeState(storeTheme, installedThemes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThemesSnapshot> InstallStoreThemeAsync(string storeThemeId)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            await EnsureThemeFolderReadyAsync();
            var storeTheme = await GetStoreThemeCoreAsync(storeThemeId);
            var result = await InvokeMethodAsync<CssLoaderOperationResult>(
                "download_theme_from_url",
                new Dictionary<string, object?>
                {
                    ["id"] = storeTheme.Id,
                    ["url"] = DeckThemesApiBaseUri.ToString(),
                });

            EnsureOperationSucceeded(result, "The selected DeckThemes entry could not be installed.");

            await InvokeMethodAsync<CssLoaderLoadErrorsState>("reset");
        });
    }

    public Task<string> ResolveCssForTargetAsync(string? title, string? url)
    {
        return Task.FromResult(string.Empty);
    }

    public async Task<ThemesSnapshot> SetThemeInstalledAsync(string themeId, bool installed)
    {
        await _gate.WaitAsync();
        try
        {
            if (installed)
            {
                return await BuildSnapshotCoreAsync();
            }

            throw new InvalidOperationException(
                "CSSLoader themes are managed by the standalone backend or the themes folder.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThemesSnapshot> SetThemeEnabledAsync(string themeId, bool enabled)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var result = await InvokeMethodAsync<CssLoaderOperationResult>(
                "set_theme_state",
                new Dictionary<string, object?>
                {
                    ["name"] = themeId,
                    ["state"] = enabled,
                });

            EnsureOperationSucceeded(result, "The selected CSSLoader theme could not be updated.");
        });
    }

    public async Task<ThemesSnapshot> ToggleThemeOptionAsync(string themeId, string optionId)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var theme = await GetThemeByNameAsync(themeId);
            var patch = FindPatch(theme, optionId);

            if (!string.Equals(patch.Type, "checkbox", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This CSSLoader patch is not a toggle.");
            }

            var currentValue = NormalizePatchValue(patch.Value, patch.Options, patch.Default);
            var nextValue = string.Equals(currentValue, "Yes", StringComparison.OrdinalIgnoreCase)
                ? "No"
                : "Yes";

            await SetPatchValueAsync(themeId, optionId, nextValue);
        });
    }

    public async Task<ThemesSnapshot> SetThemeChoiceAsync(string themeId, string optionId, string choiceId)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var theme = await GetThemeByNameAsync(themeId);
            var patch = FindPatch(theme, optionId);
            var normalizedChoice = NormalizePatchValue(choiceId, patch.Options, patch.Default);

            if (!patch.Options.Any(option =>
                    string.Equals(option, normalizedChoice, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The selected CSSLoader option value is not available.");
            }

            await SetPatchValueAsync(themeId, optionId, normalizedChoice);
        });
    }

    public async Task<ThemesSnapshot> AdjustThemeRangeAsync(string themeId, string optionId, int delta)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var theme = await GetThemeByNameAsync(themeId);
            var patch = FindPatch(theme, optionId);

            if (!string.Equals(patch.Type, "slider", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(patch.Type, "dropdown", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This CSSLoader patch is not a stepped choice.");
            }

            var options = patch.Options ?? [];
            if (options.Count == 0)
            {
                throw new InvalidOperationException("The selected CSSLoader patch has no values.");
            }

            var currentValue = NormalizePatchValue(patch.Value, options, patch.Default);
            var currentIndex = options.FindIndex(option =>
                string.Equals(option, currentValue, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            var nextIndex = Math.Max(0, Math.Min(options.Count - 1, currentIndex + delta));
            await SetPatchValueAsync(themeId, optionId, options[nextIndex]);
        });
    }

    public async Task<ThemesSnapshot> ResetThemeRangeAsync(string themeId, string optionId)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var theme = await GetThemeByNameAsync(themeId);
            var patch = FindPatch(theme, optionId);
            await SetPatchValueAsync(themeId, optionId, NormalizePatchValue(patch.Default, patch.Options, patch.Default));
        });
    }

    public async Task<ThemesSnapshot> CreateProfileAsync(string title)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var normalizedTitle = (title ?? string.Empty).Trim();
            if (normalizedTitle.Length < 3)
            {
                throw new InvalidOperationException("Enter a preset name with at least 3 characters.");
            }

            var result = await InvokeMethodAsync<CssLoaderOperationResult>(
                "generate_preset_theme",
                new Dictionary<string, object?>
                {
                    ["name"] = normalizedTitle,
                });

            EnsureOperationSucceeded(result, "The CSSLoader preset could not be created.");
        });
    }

    public async Task<ThemesSnapshot> InstallProfileAsync(string profileId)
    {
        return await ApplyProfileAsync(profileId);
    }

    public async Task<ThemesSnapshot> ApplyProfileAsync(string profileId)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var result = await InvokeMethodAsync<CssLoaderOperationResult>(
                "set_theme_state",
                new Dictionary<string, object?>
                {
                    ["name"] = profileId,
                    ["state"] = true,
                });

            EnsureOperationSucceeded(result, "The CSSLoader preset could not be applied.");
        });
    }

    public async Task<ThemesSnapshot> UpdateProfileAsync(string profileId)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var backendThemes = await GetBackendThemesAsync();
            var themeNames = backendThemes
                .Where(theme => !IsPresetTheme(theme) && theme.Enabled)
                .Select(theme => theme.Name)
                .ToArray();

            var result = await InvokeMethodAsync<CssLoaderOperationResult>(
                "generate_preset_theme_from_theme_names",
                new Dictionary<string, object?>
                {
                    ["name"] = profileId,
                    ["themeNames"] = themeNames,
                });

            EnsureOperationSucceeded(result, "The CSSLoader preset could not be updated.");
        });
    }

    public async Task<ThemesSnapshot> RemoveProfileAsync(string profileId)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var result = await InvokeMethodAsync<CssLoaderOperationResult>(
                "delete_theme",
                new Dictionary<string, object?>
                {
                    ["themeName"] = profileId,
                });

            EnsureOperationSucceeded(result, "The CSSLoader preset could not be removed.");
        });
    }

    public async Task<ThemesSnapshot> OpenThemeFolderAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var snapshot = await BuildSnapshotCoreAsync();
            Directory.CreateDirectory(snapshot.LocalThemesFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = snapshot.LocalThemesFolder,
                UseShellExecute = true
            });

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThemesSnapshot> InstallBackendAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureThemeFolderExists();

            if (!File.Exists(_backendExecutablePath))
            {
                if (File.Exists(_legacyStartupBackendExecutablePath))
                {
                    CopyBackendToManagedPath(_legacyStartupBackendExecutablePath);
                }
                else
                {
                    var release = await GetLatestBackendReleaseAsync();
                    var asset = release.Assets.FirstOrDefault(IsHeadlessBackendAsset)
                                ?? throw new InvalidOperationException(
                                    "The latest CSSLoader release does not include the standalone headless backend.");
                    await DownloadBackendExecutableAsync(asset);
                }
            }

            if (!await IsBackendReachableAsync())
            {
                if (!IsBackendProcessRunning(_backendExecutablePath))
                {
                    StartBackendProcess(_backendExecutablePath);
                }

                await WaitForBackendAsync(24, 750);
            }

            return await BuildSnapshotCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThemesSnapshot> StartBackendAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StartInstalledBackendCoreAsync(
                throwIfMissing: true,
                attempts: 8,
                delayMs: 500);

            return await BuildSnapshotCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartBackendOnStartupAsync()
    {
        await _gate.WaitAsync();
        try
        {
            try
            {
                await StartInstalledBackendCoreAsync(
                    throwIfMissing: false,
                    attempts: 12,
                    delayMs: 500);
            }
            catch
            {
                // Startup must stay best-effort; the CSSLoader panel will show the exact state.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ThemesSnapshot> SetWatchEnabledAsync(bool enabled)
    {
        return await ExecuteMutationAsync(async () =>
        {
            await EnsureBackendAvailableAsync();
            var result = await InvokeMethodAsync<CssLoaderOperationResult>(
                "toggle_watch_state",
                new Dictionary<string, object?>
                {
                    ["enable"] = enabled,
                });

            EnsureOperationSucceeded(result, "CSSLoader folder watch could not be updated.");
        });
    }

    private async Task<ThemesSnapshot> ExecuteMutationAsync(Func<Task> mutation)
    {
        await _gate.WaitAsync();
        try
        {
            await mutation();
            return await BuildSnapshotCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ThemesSnapshot> BuildSnapshotCoreAsync()
    {
        var backendReachable = await IsBackendReachableAsync();
        var backendExecutablePath = ResolveBackendExecutablePath();
        var backendInstalled = backendReachable ||
                               File.Exists(backendExecutablePath) ||
                               IsProcessRunning("CssLoader-Standalone-Headless");

        var themePath = _fallbackThemePath;
        var backendVersion = (int?)null;
        var watchEnabled = false;
        var loadErrors = new List<ThemeLoadErrorState>();
        var backendThemes = new List<CssLoaderThemeState>();

        if (backendReachable)
        {
            var themesTask = GetBackendThemesAsync();
            var backendVersionTask = InvokeMethodAsync<int>("get_backend_version");
            var themePathTask = InvokeMethodAsync<string>("fetch_theme_path");
            var watchTask = InvokeMethodAsync<bool>("get_watch_state");
            var loadErrorsTask = InvokeMethodAsync<CssLoaderLoadErrorsState>("get_last_load_errors");

            await Task.WhenAll(themesTask, backendVersionTask, themePathTask, watchTask, loadErrorsTask);

            backendThemes = themesTask.Result ?? [];
            backendVersion = backendVersionTask.Result;
            themePath = NormalizeThemePath(themePathTask.Result);
            watchEnabled = watchTask.Result;
            loadErrors = MapLoadErrors(loadErrorsTask.Result);
        }
        else
        {
            themePath = NormalizeThemePath(themePath);
        }

        EnsureThemeFolderExists(themePath);

        var installedThemes = backendThemes
            .Where(theme => !IsPresetTheme(theme))
            .Select(BuildThemeState)
            .OrderBy(theme => theme.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profiles = BuildProfilesState(backendThemes);
        var activeThemeCount = installedThemes.Count(theme => theme.Enabled);
        var integration = new ThemeIntegrationState(
            backendReachable,
            backendInstalled,
            themePath,
            backendExecutablePath,
            backendVersion,
            watchEnabled,
            loadErrors);

        return new ThemesSnapshot(
            new ThemesSettingsState(
                backendReachable,
                false,
                false,
                false),
            installedThemes,
            [],
            profiles,
            string.Empty,
            BuildStatusText(backendReachable, backendInstalled, installedThemes.Count, activeThemeCount, profiles, loadErrors.Count),
            themePath,
            integration);
    }

    private async Task<ThemeStoreCatalogState> BuildStoreCatalogCoreAsync(
        string? search,
        string? filter,
        string? order,
        int page,
        int perPage)
    {
        var normalizedSearch = NormalizeStoreSearch(search);
        var normalizedPage = Math.Max(DefaultStorePage, page);
        var normalizedPerPage = Math.Max(1, Math.Min(MaximumStorePerPage, perPage <= 0 ? DefaultStorePerPage : perPage));

        var filtersTask = _httpClient.GetFromJsonAsync<DeckThemesFiltersResponse>(
            BuildStoreFiltersUri(),
            JsonOptions);
        var installedThemesTask = TryGetInstalledThemesByNameAsync();

        await Task.WhenAll(filtersTask, installedThemesTask);

        var filtersResponse = filtersTask.Result ?? new DeckThemesFiltersResponse();
        var availableFilters = BuildStoreFilterOptions(filtersResponse);
        var availableOrders = BuildStoreOrderOptions(filtersResponse);
        var normalizedFilter = NormalizeStoreFilter(filter, availableFilters);
        var normalizedOrder = NormalizeStoreOrder(order, availableOrders);

        var queryResponse = await _httpClient.GetFromJsonAsync<DeckThemesThemeQueryResponse>(
                                BuildStoreBrowseUri(normalizedSearch, normalizedFilter, normalizedOrder, normalizedPage, normalizedPerPage),
                                JsonOptions)
                            ?? new DeckThemesThemeQueryResponse();

        return new ThemeStoreCatalogState(
            normalizedSearch,
            normalizedFilter,
            normalizedOrder,
            normalizedPage,
            normalizedPerPage,
            Math.Max(0, queryResponse.Total),
            availableFilters,
            availableOrders,
            (queryResponse.Items ?? [])
                .Select(theme => BuildStoreThemeState(theme, installedThemesTask.Result))
                .ToList());
    }

    private async Task EnsureBackendAvailableAsync()
    {
        if (!await IsBackendReachableAsync())
        {
            await StartInstalledBackendCoreAsync(
                throwIfMissing: false,
                attempts: 8,
                delayMs: 500);
        }

        if (!await IsBackendReachableAsync())
        {
            throw new InvalidOperationException(
                "CSSLoader backend is not installed or running. Install CSSLoader from CSSLoader > Settings first.");
        }

        await EnsureThemeFolderReadyAsync();
    }

    private async Task<bool> IsBackendReachableAsync()
    {
        try
        {
            return await InvokeMethodAsync<bool>("dummy_function");
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForBackendAsync(int attempts = 8, int delayMs = 500)
    {
        for (var attempt = 0; attempt < attempts; attempt += 1)
        {
            if (await IsBackendReachableAsync())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new InvalidOperationException(
            "CSSLoader was launched, but its backend did not come online in time.");
    }

    private static void StartBackendProcess(string backendExecutablePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = backendExecutablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(backendExecutablePath)
        });
    }

    private static bool IsBackendProcessRunning(string backendExecutablePath)
    {
        var normalizedPath = Path.GetFullPath(backendExecutablePath);
        foreach (var process in Process.GetProcessesByName("CssLoader-Standalone-Headless"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Some process metadata can be inaccessible; ignore and keep checking.
            }
        }

        return false;
    }

    private async Task StartInstalledBackendCoreAsync(bool throwIfMissing, int attempts, int delayMs)
    {
        EnsureThemeFolderExists();

        if (await IsBackendReachableAsync())
        {
            await EnsureThemeFolderReadyAsync();
            return;
        }

        var backendExecutablePath = ResolveBackendExecutablePath();
        if (!File.Exists(backendExecutablePath))
        {
            if (throwIfMissing)
            {
                throw new InvalidOperationException(
                    "CSSLoader's standalone backend is not installed yet. Install CSSLoader from this plugin first.");
            }

            return;
        }

        if (!string.Equals(backendExecutablePath, _backendExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            CopyBackendToManagedPath(backendExecutablePath);
            backendExecutablePath = _backendExecutablePath;
        }

        if (!IsBackendProcessRunning(backendExecutablePath))
        {
            StartBackendProcess(backendExecutablePath);
        }

        await WaitForBackendAsync(attempts, delayMs);
        await EnsureThemeFolderReadyAsync();
    }

    private void CopyBackendToManagedPath(string sourcePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backendExecutablePath)!);
        File.Copy(sourcePath, _backendExecutablePath, true);
    }

    private async Task<string> EnsureThemeFolderReadyAsync()
    {
        var themePath = _fallbackThemePath;
        if (await IsBackendReachableAsync())
        {
            try
            {
                themePath = NormalizeThemePath(await InvokeMethodAsync<string>("fetch_theme_path"));
            }
            catch
            {
                themePath = _fallbackThemePath;
            }
        }

        EnsureThemeFolderExists(themePath);
        return themePath;
    }

    private void EnsureThemeFolderExists(string? themePath = null)
    {
        Directory.CreateDirectory(NormalizeThemePath(themePath ?? _fallbackThemePath));
    }

    private async Task<CssLoaderBackendReleaseState> GetLatestBackendReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CssLoaderBackendLatestReleaseUri);
        request.Headers.UserAgent.ParseAdd("ToolsForSteam");

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<CssLoaderBackendReleaseState>(JsonOptions);
        if (release is null || release.Assets.Count == 0)
        {
            throw new InvalidOperationException("CSSLoader backend release metadata could not be loaded.");
        }

        return release;
    }

    private async Task DownloadBackendExecutableAsync(CssLoaderBackendReleaseAssetState asset)
    {
        if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException("The CSSLoader backend has no download URL.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_backendExecutablePath)!);
        var tempPath = _backendExecutablePath + ".download";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using var response = await _httpClient.GetAsync(
            asset.BrowserDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync();
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination);
        }

        File.Move(tempPath, _backendExecutablePath, true);
    }

    private async Task<List<CssLoaderThemeState>> GetBackendThemesAsync()
    {
        return await InvokeMethodAsync<List<CssLoaderThemeState>>("get_themes") ?? [];
    }

    private async Task<IReadOnlyDictionary<string, CssLoaderThemeState>> TryGetInstalledThemesByNameAsync()
    {
        if (!await IsBackendReachableAsync())
        {
            return new Dictionary<string, CssLoaderThemeState>(StringComparer.OrdinalIgnoreCase);
        }

        return (await GetBackendThemesAsync())
            .ToDictionary(theme => theme.Name, theme => theme, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DeckThemesThemeApiState> GetStoreThemeCoreAsync(string storeThemeId)
    {
        var normalizedStoreThemeId = (storeThemeId ?? string.Empty).Trim();
        if (normalizedStoreThemeId.Length == 0)
        {
            throw new InvalidOperationException("A DeckThemes store entry ID is required.");
        }

        var storeTheme = await _httpClient.GetFromJsonAsync<DeckThemesThemeApiState>(
            new Uri(DeckThemesApiBaseUri, $"themes/{Uri.EscapeDataString(normalizedStoreThemeId)}"),
            JsonOptions);

        if (storeTheme is null || string.IsNullOrWhiteSpace(storeTheme.Id))
        {
            throw new InvalidOperationException("The requested DeckThemes entry could not be loaded.");
        }

        return storeTheme;
    }

    private async Task<CssLoaderThemeState> GetThemeByNameAsync(string themeName)
    {
        var backendThemes = await GetBackendThemesAsync();
        return backendThemes.FirstOrDefault(theme =>
                   string.Equals(theme.Name, themeName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("The selected CSSLoader theme could not be found.");
    }

    private async Task SetPatchValueAsync(string themeId, string optionId, string value)
    {
        var result = await InvokeMethodAsync<CssLoaderOperationResult>(
            "set_patch_of_theme",
            new Dictionary<string, object?>
            {
                ["themeName"] = themeId,
                ["patchName"] = optionId,
                ["value"] = value,
            });

        EnsureOperationSucceeded(result, "The selected CSSLoader patch could not be updated.");
    }

    private async Task<T> InvokeMethodAsync<T>(string method, IReadOnlyDictionary<string, object?>? args = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _backendUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new CssLoaderRequest(method, args ?? EmptyArgs), JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<CssLoaderEnvelope<T>>(JsonOptions);
        if (envelope is null)
        {
            throw new InvalidOperationException("CSSLoader returned an empty response.");
        }

        if (!envelope.Success)
        {
            throw new InvalidOperationException(envelope.Res?.ToString() ?? "CSSLoader returned an error.");
        }

        return envelope.Res!;
    }

    private static void EnsureOperationSucceeded(CssLoaderOperationResult? result, string fallbackMessage)
    {
        if (result is not null && result.Success)
        {
            return;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result?.Message)
                ? fallbackMessage
                : result!.Message);
    }

    private static ThemeState BuildThemeState(CssLoaderThemeState theme)
    {
        var options = (theme.Patches ?? [])
            .Select(BuildOptionState)
            .Where(option => option is not null)
            .Cast<ThemeOptionState>()
            .OrderBy(option => option.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dependencyCount = theme.Dependencies?.Count ?? 0;
        var advancedControlCount = (theme.Patches ?? []).Sum(patch => patch.Components?.Count ?? 0);

        var summaryParts = new List<string>();
        if (options.Count > 0)
        {
            summaryParts.Add($"{options.Count} controller-ready option{(options.Count == 1 ? string.Empty : "s")} in TFS");
        }

        if (advancedControlCount > 0)
        {
            summaryParts.Add($"{advancedControlCount} advanced control{(advancedControlCount == 1 ? string.Empty : "s")} not exposed in Quick Access yet");
        }

        if (dependencyCount > 0)
        {
            summaryParts.Add($"{dependencyCount} dependenc{(dependencyCount == 1 ? "y" : "ies")}");
        }

        var summary = summaryParts.Count > 0
            ? string.Join(" - ", summaryParts) + "."
            : "Managed directly by CSSLoader.";

        return new ThemeState(
            theme.Name,
            GetThemeTitle(theme),
            string.IsNullOrWhiteSpace(theme.Author) ? "CSSLoader Community" : theme.Author,
            string.IsNullOrWhiteSpace(theme.Version) ? "v1.0" : theme.Version,
            summary,
            summary,
            true,
            theme.Enabled,
            theme.Enabled ? "Active in CSSLoader" : "Ready in CSSLoader",
            "CSSLoader",
            0,
            ["Steam UI"],
            options,
            dependencyCount,
            advancedControlCount);
    }

    private static ThemeOptionState? BuildOptionState(CssLoaderPatchState patch)
    {
        var type = (patch.Type ?? string.Empty).Trim().ToLowerInvariant();
        var advancedControlCount = patch.Components?.Count ?? 0;
        var advancedNote = advancedControlCount > 0
            ? $" {advancedControlCount} advanced control{(advancedControlCount == 1 ? string.Empty : "s")} for this patch are not exposed in Quick Access yet."
            : string.Empty;

        switch (type)
        {
            case "checkbox":
                return new ThemeOptionState(
                    patch.Name,
                    patch.Name,
                    $"Toggle this CSSLoader patch on or off.{advancedNote}",
                    "toggle",
                    string.Equals(NormalizePatchValue(patch.Value, patch.Options, patch.Default), "Yes", StringComparison.OrdinalIgnoreCase),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    advancedControlCount);

            case "dropdown":
                var dropdownChoices = (patch.Options ?? [])
                    .Select(value => new ThemeChoiceState(value, value))
                    .ToList();
                var dropdownSelectedChoiceId = NormalizePatchValue(patch.Value, patch.Options, patch.Default);
                return new ThemeOptionState(
                    patch.Name,
                    patch.Name,
                    $"Choose the active CSSLoader value for this patch.{advancedNote}",
                    "choice",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    dropdownSelectedChoiceId,
                    dropdownChoices,
                    advancedControlCount);

            case "slider":
                var sliderChoices = (patch.Options ?? [])
                    .Select(value => new ThemeChoiceState(value, value))
                    .ToList();
                var sliderSelectedChoiceId = NormalizePatchValue(patch.Value, patch.Options, patch.Default);
                return new ThemeOptionState(
                    patch.Name,
                    patch.Name,
                    $"Adjust this CSSLoader slider patch with Left / Right.{advancedNote}",
                    "slider",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    sliderSelectedChoiceId,
                    sliderChoices,
                    advancedControlCount);

            default:
                return null;
        }
    }

    private static ThemeStoreThemeState BuildStoreThemeState(
        DeckThemesThemeApiState theme,
        IReadOnlyDictionary<string, CssLoaderThemeState> installedThemes)
    {
        var themeId = string.IsNullOrWhiteSpace(theme.Name)
            ? theme.Id
            : theme.Name;
        var themeTitle = string.IsNullOrWhiteSpace(theme.DisplayName)
            ? themeId
            : theme.DisplayName;

        installedThemes.TryGetValue(themeId, out var installedTheme);
        var installedVersionMatches = installedTheme is not null &&
                                      string.Equals(
                                          NormalizeVersion(installedTheme.Version),
                                          NormalizeVersion(theme.Version),
                                          StringComparison.OrdinalIgnoreCase);
        var targets = (theme.Targets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0 && !string.IsNullOrWhiteSpace(theme.Target))
        {
            targets.Add(theme.Target);
        }

        var author = !string.IsNullOrWhiteSpace(theme.SpecifiedAuthor)
            ? theme.SpecifiedAuthor
            : !string.IsNullOrWhiteSpace(theme.Author?.Username)
                ? theme.Author.Username
                : "DeckThemes Community";
        var statusText = installedTheme is null
            ? "Available in DeckThemes Store"
            : installedVersionMatches
                ? "Installed in CSSLoader"
                : "Update available in DeckThemes Store";
        var previewImageId = (theme.Images ?? []).FirstOrDefault()?.Id ?? string.Empty;

        return new ThemeStoreThemeState(
            theme.Id,
            themeId,
            themeTitle,
            author,
            string.IsNullOrWhiteSpace(theme.Version) ? "v1.0" : theme.Version,
            string.IsNullOrWhiteSpace(theme.Description) ? "Available from DeckThemes." : theme.Description.Trim(),
            string.IsNullOrWhiteSpace(theme.Source) ? "DeckThemes Store" : theme.Source,
            string.IsNullOrWhiteSpace(theme.Target) ? "Big Picture" : theme.Target,
            targets,
            Math.Max(0, theme.Download?.DownloadCount ?? 0),
            Math.Max(0, theme.StarCount),
            theme.Dependencies?.Count ?? 0,
            installedTheme is not null,
            installedVersionMatches,
            statusText,
            BuildStoreImageUrl(previewImageId),
            BuildStoreImageThumbnailUrl(previewImageId));
    }

    private static ThemesProfilesState BuildProfilesState(IReadOnlyList<CssLoaderThemeState> backendThemes)
    {
        var themesByName = backendThemes.ToDictionary(
            theme => theme.Name,
            theme => theme,
            StringComparer.OrdinalIgnoreCase);
        var installedProfiles = backendThemes
            .Where(IsPresetTheme)
            .OrderBy(profile => GetThemeTitle(profile), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedProfile = installedProfiles.FirstOrDefault(profile => profile.Enabled);
        var selectedProfileId = selectedProfile?.Name;

        return new ThemesProfilesState(
            selectedProfileId,
            !string.IsNullOrWhiteSpace(selectedProfileId),
            installedProfiles
                .Select(profile => BuildProfileState(profile, themesByName, selectedProfileId))
                .ToList(),
            []);
    }

    private static ThemeProfileState BuildProfileState(
        CssLoaderThemeState profile,
        IReadOnlyDictionary<string, CssLoaderThemeState> themesByName,
        string? selectedProfileId)
    {
        var profileThemes = (profile.Dependencies ?? [])
            .OrderBy(title => title, StringComparer.OrdinalIgnoreCase)
            .Select(themeName =>
            {
                themesByName.TryGetValue(themeName, out var theme);
                return new ThemeProfileThemeState(
                    themeName,
                    theme is null ? themeName : GetThemeTitle(theme),
                    theme is not null,
                    theme?.Enabled == true,
                    CountInteractiveOptions(theme));
            })
            .ToList();
        var isSelected = !string.IsNullOrWhiteSpace(selectedProfileId) &&
                         string.Equals(selectedProfileId, profile.Name, StringComparison.OrdinalIgnoreCase);
        var description = profileThemes.Count > 0
            ? $"Saved CSSLoader preset with {profileThemes.Count} theme{(profileThemes.Count == 1 ? string.Empty : "s")}."
            : "Saved CSSLoader preset.";

        return new ThemeProfileState(
            profile.Name,
            GetThemeTitle(profile),
            string.IsNullOrWhiteSpace(profile.Author) ? "CSSLoader Preset" : profile.Author,
            description,
            string.IsNullOrWhiteSpace(profile.Version) ? "v1.0" : profile.Version,
            isSelected ? "Preset active in CSSLoader" : "Preset saved in CSSLoader",
            "CSSLoader",
            0,
            true,
            isSelected,
            isSelected,
            profileThemes);
    }

    private static string BuildStatusText(
        bool backendReachable,
        bool backendInstalled,
        int themeCount,
        int activeThemeCount,
        ThemesProfilesState profiles,
        int loadErrorCount)
    {
        if (!backendReachable)
        {
            if (backendInstalled)
            {
                return "CSSLoader backend is installed but currently offline.";
            }

            return "CSSLoader standalone backend is not installed or running yet.";
        }

        var status = $"{themeCount} CSSLoader theme{(themeCount == 1 ? string.Empty : "s")} loaded - {activeThemeCount} active";
        if (profiles.InstalledProfiles.Count > 0)
        {
            status += $" - {profiles.InstalledProfiles.Count} preset{(profiles.InstalledProfiles.Count == 1 ? string.Empty : "s")}";
        }

        if (loadErrorCount > 0)
        {
            status += $" - {loadErrorCount} load error{(loadErrorCount == 1 ? string.Empty : "s")}";
        }

        return status + ".";
    }

    private static List<ThemeLoadErrorState> MapLoadErrors(CssLoaderLoadErrorsState? errors)
    {
        return (errors?.Fails ?? [])
            .Where(entry => entry.Count > 0)
            .Select(entry => new ThemeLoadErrorState(
                entry[0],
                entry.Count > 1 ? entry[1] : "Unknown CSSLoader load error."))
            .ToList();
    }

    private static CssLoaderPatchState FindPatch(CssLoaderThemeState theme, string optionId)
    {
        return (theme.Patches ?? [])
            .FirstOrDefault(patch => string.Equals(patch.Name, optionId, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("The selected CSSLoader patch could not be found.");
    }

    private static string NormalizePatchValue(string? value, IReadOnlyList<string>? options, string? fallbackValue)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!string.IsNullOrWhiteSpace(fallbackValue))
        {
            return fallbackValue;
        }

        return options?.FirstOrDefault() ?? string.Empty;
    }

    private static string GetThemeTitle(CssLoaderThemeState theme)
    {
        return string.IsNullOrWhiteSpace(theme.DisplayName)
            ? theme.Name.EndsWith(".profile", StringComparison.OrdinalIgnoreCase)
                ? theme.Name[..^8]
                : theme.Name
            : theme.DisplayName;
    }

    private static bool IsPresetTheme(CssLoaderThemeState theme)
    {
        return (theme.Flags ?? []).Any(flag =>
            string.Equals(flag, "PRESET", StringComparison.OrdinalIgnoreCase));
    }

    private static int CountInteractiveOptions(CssLoaderThemeState? theme)
    {
        return theme?.Patches?.Count(patch =>
                   string.Equals(patch.Type, "checkbox", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(patch.Type, "dropdown", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(patch.Type, "slider", StringComparison.OrdinalIgnoreCase))
               ?? 0;
    }

    private static string NormalizeThemePath(string? themePath)
    {
        return string.IsNullOrWhiteSpace(themePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "homebrew", "themes")
            : themePath.Trim();
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private string ResolveBackendExecutablePath()
    {
        var candidates = new[]
        {
            _backendExecutablePath,
            _legacyStartupBackendExecutablePath
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static Uri BuildStoreFiltersUri()
    {
        return new Uri(DeckThemesApiBaseUri, $"themes/filters?type={Uri.EscapeDataString(BigPictureStoreType)}");
    }

    private static Uri BuildStoreBrowseUri(string search, string filter, string order, int page, int perPage)
    {
        var queryParts = new List<string>
        {
            $"page={page}",
            $"perPage={perPage}",
            $"filters={Uri.EscapeDataString(BuildStoreFilterQuery(filter))}",
            $"order={Uri.EscapeDataString(order)}"
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            queryParts.Add($"search={Uri.EscapeDataString(search)}");
        }

        return new Uri(DeckThemesApiBaseUri, $"themes?{string.Join("&", queryParts)}");
    }

    private static IReadOnlyList<string> BuildStoreFilterOptions(DeckThemesFiltersResponse response)
    {
        var filters = new List<string> { "All" };
        foreach (var pair in response.Filters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Value > 0)
            {
                filters.Add(pair.Key);
            }
        }

        return filters;
    }

    private static IReadOnlyList<string> BuildStoreOrderOptions(DeckThemesFiltersResponse response)
    {
        var orders = (response.Order ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return orders.Count > 0 ? orders : DefaultStoreOrders;
    }

    private static string NormalizeStoreSearch(string? search)
    {
        return (search ?? string.Empty).Trim();
    }

    private static string NormalizeStoreFilter(string? filter, IReadOnlyList<string> availableFilters)
    {
        var normalizedFilter = (filter ?? string.Empty).Trim();
        if (normalizedFilter.Length == 0)
        {
            return "All";
        }

        return availableFilters.FirstOrDefault(option =>
                   string.Equals(option, normalizedFilter, StringComparison.OrdinalIgnoreCase))
               ?? "All";
    }

    private static string NormalizeStoreOrder(string? order, IReadOnlyList<string> availableOrders)
    {
        var normalizedOrder = (order ?? string.Empty).Trim();
        if (normalizedOrder.Length == 0)
        {
            return availableOrders.FirstOrDefault() ?? DefaultStoreOrders[0];
        }

        return availableOrders.FirstOrDefault(option =>
                   string.Equals(option, normalizedOrder, StringComparison.OrdinalIgnoreCase))
               ?? availableOrders.FirstOrDefault()
               ?? DefaultStoreOrders[0];
    }

    private static string BuildStoreFilterQuery(string filter)
    {
        return string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase)
            ? BigPictureStoreType
            : $"{BigPictureStoreType}.{filter}";
    }

    private static string BuildStoreImageUrl(string? imageId)
    {
        var normalizedImageId = (imageId ?? string.Empty).Trim();
        if (normalizedImageId.Length == 0)
        {
            return string.Empty;
        }

        return new Uri(DeckThemesApiBaseUri, $"blobs/{Uri.EscapeDataString(normalizedImageId)}").ToString();
    }

    private static string BuildStoreImageThumbnailUrl(string? imageId)
    {
        var normalizedImageId = (imageId ?? string.Empty).Trim();
        if (normalizedImageId.Length == 0)
        {
            return string.Empty;
        }

        return new Uri(DeckThemesApiBaseUri, $"blobs/{Uri.EscapeDataString(normalizedImageId)}/thumb?maxWidth=640").ToString();
    }

    private static string NormalizeVersion(string? version)
    {
        return (version ?? string.Empty).Trim();
    }

    private static bool IsHeadlessBackendAsset(CssLoaderBackendReleaseAssetState asset)
    {
        return asset.Name.Equals("CssLoader-Standalone-Headless.exe", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CssLoaderRequest(
        string Method,
        IReadOnlyDictionary<string, object?> Args);

    private sealed class CssLoaderEnvelope<T>
    {
        public T? Res { get; set; }

        public bool Success { get; set; }
    }

    private sealed class CssLoaderOperationResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;
    }

    private sealed class CssLoaderLoadErrorsState
    {
        public List<List<string>> Fails { get; set; } = [];
    }

    private sealed class CssLoaderThemeState
    {
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Author { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public List<CssLoaderPatchState> Patches { get; set; } = [];

        public List<string> Dependencies { get; set; } = [];

        public List<string> Flags { get; set; } = [];
    }

    private sealed class CssLoaderPatchState
    {
        public string Name { get; set; } = string.Empty;

        public string Default { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public List<string> Options { get; set; } = [];

        public List<CssLoaderPatchComponentState> Components { get; set; } = [];
    }

    private sealed class CssLoaderPatchComponentState
    {
        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string On { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }

    private sealed class DeckThemesFiltersResponse
    {
        public Dictionary<string, int> Filters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Order { get; set; } = [];
    }

    private sealed class DeckThemesThemeQueryResponse
    {
        public int Total { get; set; }

        public List<DeckThemesThemeApiState> Items { get; set; } = [];
    }

    private sealed class DeckThemesThemeApiState
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string Target { get; set; } = string.Empty;

        public List<string> Targets { get; set; } = [];

        public string SpecifiedAuthor { get; set; } = string.Empty;

        public int StarCount { get; set; }

        public DeckThemesUserState? Author { get; set; }

        public DeckThemesBlobState? Download { get; set; }

        public List<DeckThemesBlobState> Images { get; set; } = [];

        public List<DeckThemesMinimalThemeApiState> Dependencies { get; set; } = [];
    }

    private sealed class DeckThemesMinimalThemeApiState
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class DeckThemesUserState
    {
        public string Username { get; set; } = string.Empty;
    }

    private sealed class DeckThemesBlobState
    {
        public string Id { get; set; } = string.Empty;

        public int DownloadCount { get; set; }
    }

    private sealed class CssLoaderBackendReleaseState
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        public List<CssLoaderBackendReleaseAssetState> Assets { get; set; } = [];
    }

    private sealed class CssLoaderBackendReleaseAssetState
    {
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
