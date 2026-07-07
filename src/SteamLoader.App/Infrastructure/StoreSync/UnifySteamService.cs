using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed class UnifySteamService
{
    private const string GogClientId = "46899977096215655";
    private const string GogClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    private const string GogRedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    // Same public client legendary uses for its own webview-based login.
    private const string EpicClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string EpicClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string EpicRedirectPrefix = "https://www.epicgames.com/id/api/redirect";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly ConcurrentDictionary<string, string> GogArtworkCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> SteamGridDbPortraitCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly UnifySteamStoreDefinition[] Definitions =
    [
        new(
            "epic-games",
            "Epic Games",
            "legendary",
            "legendary.exe",
            SupportsManualCodeAuth: true),
        new(
            "gog-galaxy",
            "GOG",
            "gogdl",
            "gogdl.exe",
            SupportsManualCodeAuth: true),
    ];

    private readonly StoreSyncJournal _journal;

    public UnifySteamService(StoreSyncJournal journal)
    {
        _journal = journal;
    }

    public UnifySteamSnapshot BuildSnapshot(
        StoreSyncConfiguration configuration,
        IReadOnlyList<StoreSyncStoreState> storeSyncStores)
    {
        var detectedTitles = storeSyncStores
            .SelectMany(store => store.DetectedTitles)
            .ToArray();

        var stores = Definitions
            .Select(definition => BuildStoreState(definition, configuration, detectedTitles))
            .ToArray();

        var lastRefreshedAtUtc = stores
            .Where(store => store.RefreshedAtUtc.HasValue)
            .Select(store => store.RefreshedAtUtc)
            .Max();

        var readyCount = stores.Count(store => store.AuthReady || store.ToolDetected);
        var totalAvailable = stores.Sum(store => store.AvailableCount);
        var totalInstalled = stores.Sum(store => store.InstalledCount);

        var statusText = readyCount > 0
            ? "Ready"
            : "Setup";
        var detailText = totalAvailable > 0 || totalInstalled > 0
            ? $"{totalInstalled} installed / {totalAvailable} in library across Epic and GOG."
            : "Sign in and refresh a store to build your unified launcher library.";

        return new UnifySteamSnapshot(
            statusText,
            detailText,
            lastRefreshedAtUtc,
            stores);
    }

    public void RefreshLibraries(StoreSyncConfiguration configuration, string? storeId = null, bool skipUnconfigured = false)
    {
        foreach (var definition in ResolveDefinitions(storeId))
        {
            var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
            if (!storeConfiguration.Enabled)
            {
                continue;
            }

            if (skipUnconfigured && !IsStoreConfigured(definition, storeConfiguration))
            {
                continue;
            }

            RefreshStore(definition, storeConfiguration);
        }
    }

    private static bool IsStoreConfigured(UnifySteamStoreDefinition definition, UnifySteamStoreConfiguration storeConfiguration)
    {
        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
        {
            var epicAuthPath = ResolveReadableEpicAuthPath(storeConfiguration.AuthPath);
            return !string.IsNullOrWhiteSpace(ResolveToolPath(definition, storeConfiguration.ToolPath)) ||
                   !string.IsNullOrWhiteSpace(ResolveEpicLauncherPath()) ||
                   (!string.IsNullOrWhiteSpace(epicAuthPath) && File.Exists(epicAuthPath));
        }

        var authPath = ResolveReadableGogAuthPath(storeConfiguration.AuthPath);
        return !string.IsNullOrWhiteSpace(authPath) && File.Exists(authPath);
    }

    public void StartLogin(StoreSyncConfiguration configuration, string storeId)
    {
        var definition = ResolveDefinition(storeId);
        var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);

        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
        {
            storeConfiguration.AuthPath = ResolveWritableEpicAuthPath(storeConfiguration.AuthPath);
            OpenInDefaultBrowser(BuildEpicLoginUrl());
            _journal.Append(
                "info",
                "unifysteam",
                "Opened Epic login page in the browser.",
                "Sign in there; the final page shows \"authorizationCode\". Copy it and paste it into the Login Code field.");
            return;
        }

        var authPath = ResolveWritableGogAuthPath(storeConfiguration.AuthPath);
        storeConfiguration.AuthPath = authPath;
        OpenInDefaultBrowser(BuildGogAuthUrl());
        _journal.Append(
            "info",
            "unifysteam",
            "Opened GOG login page in the browser.",
            "Sign in there, then copy the final page URL (it contains code=...) and paste it into the Login Code field.");
    }

    private static void OpenInDefaultBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        })?.Dispose();
    }

    private static string BuildEpicLoginUrl()
    {
        var redirect = $"{EpicRedirectPrefix}?clientId={EpicClientId}&responseType=code";
        return $"https://www.epicgames.com/id/login?redirectUrl={Uri.EscapeDataString(redirect)}";
    }

    public void CompleteManualCodeAuth(StoreSyncConfiguration configuration, string storeId, string value)
    {
        var definition = ResolveDefinition(storeId);
        if (!definition.SupportsManualCodeAuth)
        {
            throw new InvalidOperationException("This store uses an external login flow.");
        }

        var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
        var authCode = ExtractAuthorizationCode(value);
        if (string.IsNullOrWhiteSpace(authCode))
        {
            throw new InvalidOperationException("Paste the code, the full page URL, or the whole text from the login page.");
        }

        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase))
        {
            var epicAuthPath = ResolveWritableEpicAuthPath(storeConfiguration.AuthPath);
            var epicToken = ExchangeEpicAuthorizationCode(authCode);
            SaveEpicCredentials(epicAuthPath, epicToken);
            storeConfiguration.AuthPath = epicAuthPath;
            _journal.Append("info", "unifysteam", "Saved Epic sign-in.", $"Auth data was stored at {epicAuthPath}.");
            return;
        }

        var authPath = ResolveWritableGogAuthPath(storeConfiguration.AuthPath);
        var token = ExchangeGogAuthorizationCode(authCode);
        SaveGogCredentials(authPath, token);
        storeConfiguration.AuthPath = authPath;
        _journal.Append("info", "unifysteam", "Saved GOG sign-in.", $"Auth data was stored at {authPath}.");
    }

    private UnifySteamStoreState BuildStoreState(
        UnifySteamStoreDefinition definition,
        StoreSyncConfiguration configuration,
        IReadOnlyList<StoreSyncDetectedTitleState> detectedTitles)
    {
        var storeConfiguration = GetStoreConfiguration(configuration, definition.Id);
        var effectiveToolPath = !string.IsNullOrWhiteSpace(storeConfiguration.ToolPath)
            ? storeConfiguration.ToolPath
            : ResolveToolPath(definition, storeConfiguration.ToolPath);
        var effectiveEpicLauncherPath = definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
            ? ResolveEpicLauncherPath()
            : string.Empty;
        var effectiveAuthPath = definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
            ? ResolveReadableGogAuthPath(storeConfiguration.AuthPath)
            : definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                ? ResolveReadableEpicAuthPath(storeConfiguration.AuthPath)
                : string.Empty;
        var cache = storeConfiguration.Cache ?? new UnifySteamLibraryCache();
        var cacheLastError = cache.LastError;
        if (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(effectiveEpicLauncherPath) &&
            cacheLastError.Contains("legendary was not found", StringComparison.OrdinalIgnoreCase))
        {
            cacheLastError = string.Empty;
        }
        var detectedByStore = detectedTitles
            .Where(title => string.Equals(title.StoreId, definition.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var games = cache.Games
            .Select(game => ToGameState(definition, game, detectedByStore))
            .OrderByDescending(game => game.Installed)
            .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var installedCount = games.Count(game => game.Installed);
        var availableCount = games.Length;
        var toolDetected = definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(effectiveToolPath) || !string.IsNullOrWhiteSpace(effectiveEpicLauncherPath)
            : !string.IsNullOrWhiteSpace(effectiveToolPath);
        var authConfigured = definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(effectiveAuthPath)
            : definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                ? !string.IsNullOrWhiteSpace(effectiveAuthPath) || toolDetected
                : toolDetected;
        var authReady = !string.IsNullOrWhiteSpace(cache.AccountName);

        var statusText = !storeConfiguration.Enabled
            ? "Disabled"
            : !string.IsNullOrWhiteSpace(cacheLastError)
                ? "Attention"
                : !authReady && definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                    ? authConfigured
                        ? "Login required"
                        : "Setup required"
                    : !authReady && definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
                        ? authConfigured
                            ? "Login required"
                            : "Setup required"
                        : availableCount > 0
                            ? "Ready"
                            : "Not loaded";

        var detailText = !string.IsNullOrWhiteSpace(cacheLastError)
            ? cacheLastError
            : !authReady && definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase)
                ? authConfigured
                    ? !string.IsNullOrWhiteSpace(cache.DetailText)
                        ? cache.DetailText
                        : "Sign in with Epic, paste the login code, then refresh the library."
                    : "Sign in with Epic or install Epic Games Launcher, Heroic, or legendary, then refresh Storefront."
                : !authReady && definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
                    ? authConfigured
                        ? "Sign in with GOG, then refresh the library."
                        : "Sign in with GOG or refresh after Heroic has saved GOG auth data."
                    : !string.IsNullOrWhiteSpace(cache.DetailText)
                        ? cache.DetailText
                        : availableCount > 0
                            ? $"{installedCount} installed / {availableCount} total."
                            : "Refresh this store to build the library snapshot.";

        return new UnifySteamStoreState(
            definition.Id,
            definition.Title,
            storeConfiguration.Enabled,
            toolDetected,
            authConfigured,
            authReady,
            CanRefresh: storeConfiguration.Enabled && (definition.Id.Equals("epic-games", StringComparison.OrdinalIgnoreCase) ? authConfigured : authConfigured),
            definition.SupportsManualCodeAuth,
            statusText,
            detailText,
            cache.AccountName,
            cache.RefreshedAtUtc,
            installedCount,
            availableCount,
            games);
    }

    private UnifySteamGameState ToGameState(
        UnifySteamStoreDefinition definition,
        UnifySteamGameCacheEntry game,
        IReadOnlyList<StoreSyncDetectedTitleState> detectedTitles)
    {
        var matchedDetectedTitle = detectedTitles.FirstOrDefault(title =>
        {
            if (!string.IsNullOrWhiteSpace(game.ExecutablePath) &&
                string.Equals(title.ExecutablePath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(game.InstallPath) &&
                title.ExecutablePath.StartsWith(game.InstallPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(title.Title, game.Title, StringComparison.OrdinalIgnoreCase);
        });

        var installed = definition.Id.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase)
            ? matchedDetectedTitle is not null || game.Installed
            : game.Installed || matchedDetectedTitle is not null;
        var syncedToSteam = matchedDetectedTitle is not null;
        var executablePath = !string.IsNullOrWhiteSpace(game.ExecutablePath)
            ? game.ExecutablePath
            : matchedDetectedTitle?.ExecutablePath ?? string.Empty;
        var installPath = !string.IsNullOrWhiteSpace(game.InstallPath)
            ? game.InstallPath
            : matchedDetectedTitle?.StartDirectory ?? string.Empty;
        var statusText = !installed
            ? "Available"
            : syncedToSteam
                ? "Installed + Synced"
                : "Installed";
        var detailText = !installed
            ? "In your account library."
            : !string.IsNullOrWhiteSpace(installPath)
                ? installPath
                : "Installed locally.";

        return new UnifySteamGameState(
            game.Id,
            game.Title,
            installed,
            syncedToSteam,
            statusText,
            detailText,
            NormalizeImageUrl(game.ImageUrl),
            installPath,
            executablePath,
            game.Version);
    }

    private void RefreshStore(UnifySteamStoreDefinition definition, UnifySteamStoreConfiguration storeConfiguration)
    {
        try
        {
            switch (definition.Id)
            {
                case "epic-games":
                    RefreshEpicStore(definition, storeConfiguration);
                    break;
                case "gog-galaxy":
                    RefreshGogStore(storeConfiguration);
                    break;
                default:
                    throw new InvalidOperationException("Unknown Storefront store.");
            }
        }
        catch (Exception exception)
        {
            storeConfiguration.Cache ??= new UnifySteamLibraryCache();
            storeConfiguration.Cache.LastError = exception.Message.Trim();
            storeConfiguration.Cache.StatusText = "Attention";
            storeConfiguration.Cache.DetailText = exception.Message.Trim();
            storeConfiguration.Cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            _journal.Append("warning", "unifysteam", $"Failed to refresh {definition.Title}.", exception.Message);
        }
    }

    private void RefreshEpicStore(UnifySteamStoreDefinition definition, UnifySteamStoreConfiguration storeConfiguration)
    {
        var toolPath = ResolveToolPath(definition, storeConfiguration.ToolPath);
        var launcherInstalledGames = LoadEpicLauncherInstalledGames();
        var authPath = ResolveReadableEpicAuthPath(storeConfiguration.AuthPath);
        if (!string.IsNullOrWhiteSpace(authPath))
        {
            var credentials = EnsureEpicCredentials(authPath);
            var epicInstalledMap = !string.IsNullOrWhiteSpace(toolPath)
                ? MergeEpicInstalledGames(LoadEpicInstalledGames(toolPath), launcherInstalledGames)
                : launcherInstalledGames;
            var epicGames = LoadEpicAccountLibraryGames(credentials.AccessToken, epicInstalledMap);
            var epicCache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
            epicCache.AccountName = FirstNonEmpty(credentials.DisplayName, credentials.AccountId, "Epic Account");
            epicCache.Games = epicGames;
            epicCache.LastError = string.Empty;
            epicCache.StatusText = "Ready";
            epicCache.DetailText = $"Loaded {epicGames.Count} Epic title{(epicGames.Count == 1 ? string.Empty : "s")} for {epicCache.AccountName}.";
            epicCache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            _journal.Append("info", "unifysteam", "Refreshed Epic library.", epicCache.DetailText);
            return;
        }

        if (string.IsNullOrWhiteSpace(toolPath))
        {
            var launcherPath = ResolveEpicLauncherPath();
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new InvalidOperationException("Epic Games Launcher or legendary was not found. Install Epic Games Launcher, Heroic, or legendary, then refresh Storefront.");
            }

            var launcherCache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
            launcherCache.AccountName = string.Empty;
            launcherCache.Games = launcherInstalledGames.Values
                .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            launcherCache.LastError = string.Empty;
            launcherCache.StatusText = "Ready";
            launcherCache.DetailText = launcherInstalledGames.Count > 0
                ? $"Epic Games Launcher detected. Showing {launcherInstalledGames.Count} installed title{(launcherInstalledGames.Count == 1 ? string.Empty : "s")} from the local launcher manifest."
                : "Epic Games Launcher detected. Use Epic Login, paste the code, then refresh to import the full account library.";
            launcherCache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            _journal.Append("info", "unifysteam", "Refreshed Epic launcher fallback.", launcherCache.DetailText);
            return;
        }

        var installedMap = LoadEpicInstalledGames(toolPath);
        var status = LoadEpicStatus(toolPath);
        var cache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();

        if (!status.Authenticated)
        {
            cache.AccountName = string.Empty;
            cache.LastError = string.Empty;
            cache.StatusText = "Login required";
            cache.DetailText = installedMap.Count > 0
                ? "Installed titles were found locally, but Epic sign-in is still required for the full library."
                : "Run Epic sign-in, then refresh the library.";
            cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
            if (cache.Games.Count == 0 && installedMap.Count > 0)
            {
                cache.Games = installedMap.Values
                    .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            _journal.Append("info", "unifysteam", "Epic refresh needs sign-in.", cache.DetailText);
            return;
        }

        var games = LoadEpicLibraryGames(toolPath, installedMap);
        cache.AccountName = status.AccountName;
        cache.Games = games;
        cache.LastError = string.Empty;
        cache.StatusText = "Ready";
        cache.DetailText = $"Loaded {games.Count} Epic title{(games.Count == 1 ? string.Empty : "s")} for {status.AccountName}.";
        cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
        _journal.Append("info", "unifysteam", "Refreshed Epic library.", cache.DetailText);
    }

    private void RefreshGogStore(UnifySteamStoreConfiguration storeConfiguration)
    {
        var cache = storeConfiguration.Cache ??= new UnifySteamLibraryCache();
        var authPath = ResolveReadableGogAuthPath(storeConfiguration.AuthPath);
        if (string.IsNullOrWhiteSpace(authPath))
        {
            throw new InvalidOperationException("GOG auth data was not found. Open the GOG login flow first.");
        }

        var credentials = EnsureGogCredentials(authPath);
        var response = LoadGogLibrary(credentials.AccessToken);
        cache.AccountName = string.IsNullOrWhiteSpace(response.AccountName)
            ? "GOG Account"
            : response.AccountName;
        cache.Games = response.Games;
        cache.LastError = string.Empty;
        cache.StatusText = "Ready";
        cache.DetailText = $"Loaded {response.Games.Count} GOG title{(response.Games.Count == 1 ? string.Empty : "s")}.";
        cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
        _journal.Append("info", "unifysteam", "Refreshed GOG library.", cache.DetailText);
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> LoadEpicLauncherInstalledGames()
    {
        var installed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var manifestPath = GetEpicLauncherInstalledManifestPath();
        if (!File.Exists(manifestPath))
        {
            return installed;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("InstallationList", out var installationList) ||
                installationList.ValueKind != JsonValueKind.Array)
            {
                return installed;
            }

            foreach (var item in installationList.EnumerateArray())
            {
                var appName = FirstNonEmpty(
                    GetJsonString(item, "AppName"),
                    GetJsonString(item, "MainGameAppName"),
                    GetJsonString(item, "ArtifactId"),
                    GetJsonString(item, "ItemId"));
                if (string.IsNullOrWhiteSpace(appName))
                {
                    continue;
                }

                var installLocation = GetJsonString(item, "InstallLocation");
                var launchExecutable = GetJsonString(item, "LaunchExecutable");
                var executablePath = ResolveExecutablePath(installLocation, launchExecutable);
                installed[appName] = new UnifySteamGameCacheEntry
                {
                    Id = appName,
                    Title = FirstNonEmpty(GetJsonString(item, "DisplayName"), appName),
                    Installed = true,
                    InstallPath = installLocation,
                    ExecutablePath = executablePath,
                };
            }
        }
        catch
        {
        }

        return installed;
    }

    private static string GetEpicLauncherInstalledManifestPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "UnrealEngineLauncher",
            "LauncherInstalled.dat");
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> MergeEpicInstalledGames(
        Dictionary<string, UnifySteamGameCacheEntry> primary,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> fallback)
    {
        foreach (var pair in fallback)
        {
            primary.TryAdd(pair.Key, pair.Value);
        }

        return primary;
    }

    private static List<UnifySteamGameCacheEntry> LoadEpicAccountLibraryGames(
        string accessToken,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedMap)
    {
        var games = new List<UnifySteamGameCacheEntry>();
        var cursor = string.Empty;

        do
        {
            var url =
                "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true&platform=Windows&limit=100";
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", accessToken);
            using var response = HttpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Epic library could not be loaded (HTTP {(int)response.StatusCode}). Sign in again and retry.");
            }

            var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Epic library returned an unexpected response.");
            }

            if (document.RootElement.TryGetProperty("records", out var records) &&
                records.ValueKind == JsonValueKind.Array)
            {
                AppendEpicLibraryRecords(records, installedMap, games);
            }

            cursor = string.Empty;
            if (document.RootElement.TryGetProperty("responseMetadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object)
            {
                cursor = GetJsonString(metadata, "nextCursor");
            }
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return DedupeLibraryGames(games);
    }

    private static void AppendEpicLibraryRecords(
        JsonElement records,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedMap,
        ICollection<UnifySteamGameCacheEntry> target)
    {
        foreach (var record in records.EnumerateArray())
        {
            var recordType = GetJsonString(record, "recordType");
            if (!string.Equals(recordType, "APPLICATION", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var appName = FirstNonEmpty(
                GetJsonString(record, "appName"),
                GetJsonString(record, "artifactId"),
                GetJsonString(record, "catalogItemId"));
            if (string.IsNullOrWhiteSpace(appName))
            {
                continue;
            }

            var title = FirstNonEmpty(
                GetJsonString(record, "sandboxName"),
                GetJsonString(record, "title"),
                appName);
            installedMap.TryGetValue(appName, out var installed);
            target.Add(new UnifySteamGameCacheEntry
            {
                Id = appName,
                Title = title,
                Installed = installed?.Installed == true,
                InstallPath = installed?.InstallPath ?? string.Empty,
                ExecutablePath = installed?.ExecutablePath ?? string.Empty,
                Version = installed?.Version ?? string.Empty,
                ImageUrl = ResolveSteamGridDbPortraitArtworkUrl(title),
            });
        }
    }

    private static EpicStatus LoadEpicStatus(string toolPath)
    {
        var result = RunTool(toolPath, "status", "--json");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstNonEmpty(result.StandardError, result.StandardOutput, "legendary status failed."));
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var accountName = GetJsonString(root, "account");
        return new EpicStatus(
            !string.IsNullOrWhiteSpace(accountName) && !string.Equals(accountName, "<not logged in>", StringComparison.OrdinalIgnoreCase),
            string.Equals(accountName, "<not logged in>", StringComparison.OrdinalIgnoreCase) ? string.Empty : accountName);
    }

    private static Dictionary<string, UnifySteamGameCacheEntry> LoadEpicInstalledGames(string toolPath)
    {
        var result = RunTool(toolPath, "list-installed", "--json");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var installed = new Dictionary<string, UnifySteamGameCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var appName = GetJsonString(item, "app_name");
            if (string.IsNullOrWhiteSpace(appName))
            {
                continue;
            }

            installed[appName] = new UnifySteamGameCacheEntry
            {
                Id = appName,
                Title = FirstNonEmpty(GetJsonString(item, "title"), appName),
                Installed = true,
                InstallPath = GetJsonString(item, "install_path"),
                ExecutablePath = ResolveInstalledExecutablePath(item),
                Version = GetJsonString(item, "version"),
            };
        }

        return installed;
    }

    private static List<UnifySteamGameCacheEntry> LoadEpicLibraryGames(
        string toolPath,
        IReadOnlyDictionary<string, UnifySteamGameCacheEntry> installedMap)
    {
        var result = RunTool(toolPath, "list", "--json");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstNonEmpty(result.StandardError, result.StandardOutput, "legendary list failed."));
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("legendary did not return a valid library list.");
        }

        var games = new List<UnifySteamGameCacheEntry>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var appName = GetJsonString(item, "app_name");
            if (string.IsNullOrWhiteSpace(appName))
            {
                continue;
            }

            var metadata = item.TryGetProperty("metadata", out var metadataNode)
                ? metadataNode
                : default;
            var isDlc = metadata.ValueKind == JsonValueKind.Object &&
                        metadata.TryGetProperty("mainGameItem", out _);
            if (isDlc)
            {
                continue;
            }

            installedMap.TryGetValue(appName, out var installed);
            games.Add(new UnifySteamGameCacheEntry
            {
                Id = appName,
                Title = FirstNonEmpty(GetJsonString(item, "app_title"), appName),
                Installed = installed?.Installed == true,
                InstallPath = installed?.InstallPath ?? string.Empty,
                ExecutablePath = installed?.ExecutablePath ?? string.Empty,
                Version = installed?.Version ?? string.Empty,
                ImageUrl = ResolveEpicImageUrl(metadata),
            });
        }

        return DedupeLibraryGames(games);
    }

    private static string ResolveEpicImageUrl(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("keyImages", out var keyImages) ||
            keyImages.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var bestUrl = string.Empty;
        var bestScore = double.NegativeInfinity;
        foreach (var image in keyImages.EnumerateArray())
        {
            var url = GetJsonString(image, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var score = ScoreEpicImage(image, url);
            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
            }
        }

        return NormalizeImageUrl(bestUrl);
    }

    private static double ScoreEpicImage(JsonElement image, string url)
    {
        var type = GetJsonString(image, "type");
        var width = GetJsonInt(image, "width");
        var height = GetJsonInt(image, "height");
        var areaScore = width > 0 && height > 0 ? Math.Min(width * height / 1000d, 3000d) : 0d;
        var aspect = width > 0 && height > 0 ? height / (double)width : 1.5d;
        var portraitScore = height >= width ? 1500d : -500d;
        var aspectScore = Math.Max(0d, 1000d - Math.Abs(aspect - 1.5d) * 900d);
        var combined = $"{type} {url}";
        var typeScore = type.ToLowerInvariant() switch
        {
            "dieselgameboxtall" => 6000d,
            "offerimagetall" => 5600d,
            "dieselgamebox" => 5200d,
            "vaultopened" => 4400d,
            "vaultclosed" => 4200d,
            "offerimagewide" => 2400d,
            "thumbnail" => 400d,
            "dieselgameboxlogo" => -600d,
            _ => 0d,
        };
        var nameScore = combined.Contains("portrait", StringComparison.OrdinalIgnoreCase) ||
                        combined.Contains("tall", StringComparison.OrdinalIgnoreCase) ||
                        combined.Contains("box", StringComparison.OrdinalIgnoreCase)
            ? 800d
            : 0d;

        return typeScore + portraitScore + aspectScore + areaScore + nameScore;
    }

    private GogCredential EnsureGogCredentials(string authPath)
    {
        var current = LoadGogCredential(authPath)
            ?? throw new InvalidOperationException("GOG auth data could not be read.");

        if (DateTimeOffset.UtcNow < current.ExpiresAtUtc.AddMinutes(-2))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            throw new InvalidOperationException("The saved GOG login has expired. Sign in again to refresh the library.");
        }

        var requestUri = $"{BuildGogTokenBaseUri()}&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(current.RefreshToken)}";
        using var response = HttpClient.GetAsync(requestUri).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The saved GOG login could not be refreshed. Sign in again.");
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        var token = ParseGogToken(document.RootElement);
        SaveGogCredentials(authPath, token);
        return token;
    }

    private EpicCredential EnsureEpicCredentials(string authPath)
    {
        var current = LoadEpicCredential(authPath)
            ?? throw new InvalidOperationException("Epic auth data could not be read.");

        if (DateTimeOffset.UtcNow < current.ExpiresAtUtc.AddMinutes(-2))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            throw new InvalidOperationException("The saved Epic login has expired. Sign in again to refresh the library.");
        }

        var token = ExchangeEpicRefreshToken(current.RefreshToken);
        SaveEpicCredentials(authPath, token);
        return token;
    }

    private GogLibraryResponse LoadGogLibrary(string accessToken)
    {
        // 1) Authoritative ownership list; works reliably with the Galaxy bearer token.
        var ownedIds = LoadGogOwnedIds(accessToken);

        // 2) Try the account pages for rich data (may return nothing for bearer sessions).
        var games = new List<UnifySteamGameCacheEntry>();
        var fromAccountPages = 0;
        try
        {
            LoadGogAccountPages(accessToken, games);
            fromAccountPages = games.Count;
        }
        catch (Exception exception)
        {
            _journal.Append(
                "warning",
                "unifysteam",
                "GOG account pages could not be loaded; using the products API instead.",
                exception.Message);
        }

        // Safety net: never keep the same product twice, even if GOG exposes
        // the same title under separate product IDs or editions.
        games = DedupeLibraryGames(games);
        fromAccountPages = games.Count;

        // 3) Always enrich with the public products API. Account pages often only expose
        // small cover thumbnails, while product details include higher resolution cards.
        var beforeDetails = games.Count;
        var productDetails = new List<UnifySteamGameCacheEntry>();
        if (ownedIds.Count > 0)
        {
            AppendGogProductDetails(ownedIds, productDetails);
            games = MergeGogProductDetails(games, productDetails);
        }

        _journal.Append(
            "info",
            "unifysteam",
            "GOG library assembled.",
            $"Owned product IDs: {ownedIds.Count}; from account pages: {fromAccountPages}; enriched from products API: {productDetails.Count}; added from products API: {Math.Max(0, games.Count - beforeDetails)}.");

        return new GogLibraryResponse(string.Empty, DedupeLibraryGames(games));
    }

    private List<string> LoadGogOwnedIds(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://embed.gog.com/user/data/games");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The GOG ownership list could not be loaded (HTTP {(int)response.StatusCode}). Sign in again and retry.");
        }

        var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(payload);
        var ids = new List<string>();
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("owned", out var owned) &&
            owned.ValueKind == JsonValueKind.Array)
        {
            foreach (var idNode in owned.EnumerateArray())
            {
                var id = idNode.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private void AppendGogProductDetails(IReadOnlyList<string> ids, ICollection<UnifySteamGameCacheEntry> target)
    {
        const int batchSize = 50;
        for (var offset = 0; offset < ids.Count; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            try
            {
                using var response = HttpClient.GetAsync(
                    $"https://api.gog.com/products?ids={string.Join(',', batch)}").GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    _journal.Append(
                        "warning",
                        "unifysteam",
                        "A GOG products API batch failed.",
                        $"HTTP {(int)response.StatusCode} for {batch.Length} products; these titles are skipped for now.");
                    continue;
                }

                var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var entries = new List<UnifySteamGameCacheEntry>();
                foreach (var product in document.RootElement.EnumerateArray())
                {
                    // Skip DLC entries; packs and games stay.
                    var gameType = GetJsonString(product, "game_type");
                    if (string.Equals(gameType, "dlc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var worksOnWindows = !product.TryGetProperty("content_system_compatibility", out var compatibility) ||
                                         compatibility.ValueKind != JsonValueKind.Object ||
                                         !compatibility.TryGetProperty("windows", out var windowsNode) ||
                                         windowsNode.ValueKind != JsonValueKind.False;
                    if (!worksOnWindows)
                    {
                        continue;
                    }

                    var id = GetJsonString(product, "id");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var imageUrl = string.Empty;
                    if (product.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object)
                    {
                        imageUrl = FirstNonEmpty(
                            GetJsonString(images, "productCard2x"),
                            GetJsonString(images, "productCard"),
                            GetJsonString(images, "verticalCover"),
                            GetJsonString(images, "cover"),
                            GetJsonString(images, "boxArtImage"),
                            GetJsonString(images, "featuredVertical"),
                            GetJsonString(images, "background"),
                            GetJsonString(images, "icon"));
                    }

                    entries.Add(new UnifySteamGameCacheEntry
                    {
                        Id = id,
                        Title = FirstNonEmpty(GetJsonString(product, "title"), id),
                        Installed = false,
                        ImageUrl = NormalizeImageUrl(imageUrl),
                    });
                }

                var steamGridPortraitByTitle = LoadSteamGridDbPortraitArtworkBatch(entries.Select(entry => entry.Title).ToArray());
                var artworkById = LoadGogV2ArtworkBatch(entries.Select(entry => entry.Id).ToArray());
                foreach (var entry in entries)
                {
                    if (steamGridPortraitByTitle.TryGetValue(entry.Title, out var steamGridArtworkUrl) &&
                        !string.IsNullOrWhiteSpace(steamGridArtworkUrl))
                    {
                        entry.ImageUrl = steamGridArtworkUrl;
                    }
                    else if (artworkById.TryGetValue(entry.Id, out var artworkUrl) && !string.IsNullOrWhiteSpace(artworkUrl))
                    {
                        entry.ImageUrl = artworkUrl;
                    }

                    target.Add(entry);
                }
            }
            catch (Exception exception)
            {
                _journal.Append(
                    "warning",
                    "unifysteam",
                    "A GOG products API batch could not be processed.",
                    exception.Message);
            }
        }
    }

    private static Dictionary<string, string> LoadSteamGridDbPortraitArtworkBatch(IReadOnlyList<string> titles)
    {
        var uniqueTitles = titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (uniqueTitles.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var artworkByTitle = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(
            uniqueTitles,
            new ParallelOptions { MaxDegreeOfParallelism = 3 },
            title =>
            {
                var artworkUrl = ResolveSteamGridDbPortraitArtworkUrl(title);
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                {
                    artworkByTitle[title] = artworkUrl;
                }
            });

        return artworkByTitle.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveSteamGridDbPortraitArtworkUrl(string title)
    {
        var cacheKey = NormalizeArtworkLookupTitle(title);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return string.Empty;
        }

        if (SteamGridDbPortraitCache.TryGetValue(cacheKey, out var cachedArtworkUrl))
        {
            return cachedArtworkUrl;
        }

        var artworkUrl = string.Empty;
        try
        {
            var gameId = ResolveSteamGridDbGameId(title, cacheKey);
            if (gameId > 0)
            {
                artworkUrl = ResolveSteamGridDbPortraitAssetUrl(gameId);
            }
        }
        catch (Exception)
        {
            artworkUrl = string.Empty;
        }

        artworkUrl = NormalizeImageUrl(artworkUrl);
        SteamGridDbPortraitCache[cacheKey] = artworkUrl;
        return artworkUrl;
    }

    private static int ResolveSteamGridDbGameId(string title, string normalizedTitle)
    {
        using var document = LoadSteamGridDbJson($"search/autocomplete/{Uri.EscapeDataString(title.Trim())}");
        if (document is null ||
            document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var bestGameId = 0;
        var bestScore = int.MinValue;
        foreach (var match in data.EnumerateArray())
        {
            var gameId = GetJsonInt(match, "id");
            var matchName = GetJsonString(match, "name");
            if (gameId <= 0 || string.IsNullOrWhiteSpace(matchName))
            {
                continue;
            }

            var score = ScoreSteamGridDbMatch(normalizedTitle, matchName);
            if (score > bestScore)
            {
                bestScore = score;
                bestGameId = gameId;
            }
        }

        return bestGameId;
    }

    private static string ResolveSteamGridDbPortraitAssetUrl(int gameId)
    {
        var requestPaths = new[]
        {
            $"grids/game/{gameId}?types=static&dimensions=600x900&mimes=image/png,image/jpeg",
            $"grids/game/{gameId}?types=static&mimes=image/png,image/jpeg",
        };

        foreach (var requestPath in requestPaths)
        {
            using var document = LoadSteamGridDbJson(requestPath);
            if (document is null ||
                document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var artworkUrl = SelectSteamGridDbPortraitUrl(data);
            if (!string.IsNullOrWhiteSpace(artworkUrl))
            {
                return artworkUrl;
            }
        }

        return string.Empty;
    }

    private static JsonDocument? LoadSteamGridDbJson(string requestPath)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/{requestPath}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            SteamGridDbArtworkDownloader.BuiltInApiKey);

        using var response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonDocument.Parse(payload);
    }

    private static string SelectSteamGridDbPortraitUrl(JsonElement data)
    {
        var bestUrl = string.Empty;
        var bestScore = int.MinValue;
        foreach (var asset in data.EnumerateArray())
        {
            var url = GetJsonString(asset, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var width = GetJsonInt(asset, "width");
            var height = GetJsonInt(asset, "height");
            var score = 0;
            if (width == 600 && height == 900)
            {
                score += 10000;
            }

            if (height > width)
            {
                score += 5000;
            }

            score += Math.Min(width, 1600) + Math.Min(height, 2400);
            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
            }
        }

        return bestUrl;
    }

    private static int ScoreSteamGridDbMatch(string normalizedTitle, string matchName)
    {
        var normalizedMatch = NormalizeArtworkLookupTitle(matchName);
        if (string.IsNullOrWhiteSpace(normalizedMatch))
        {
            return 0;
        }

        if (string.Equals(normalizedMatch, normalizedTitle, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (normalizedMatch.StartsWith(normalizedTitle, StringComparison.Ordinal) ||
            normalizedTitle.StartsWith(normalizedMatch, StringComparison.Ordinal))
        {
            return 800;
        }

        if (normalizedMatch.Contains(normalizedTitle, StringComparison.Ordinal) ||
            normalizedTitle.Contains(normalizedMatch, StringComparison.Ordinal))
        {
            return 600;
        }

        return 100;
    }

    private static string NormalizeArtworkLookupTitle(string title)
    {
        var normalized = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static Dictionary<string, string> LoadGogV2ArtworkBatch(IReadOnlyList<string> ids)
    {
        var uniqueIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (uniqueIds.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var artworkById = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(
            uniqueIds,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            id =>
            {
                var artworkUrl = ResolveGogV2ArtworkUrl(id);
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                {
                    artworkById[id] = artworkUrl;
                }
            });

        return artworkById.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveGogV2ArtworkUrl(string id)
    {
        id = id.Trim();
        if (GogArtworkCache.TryGetValue(id, out var cachedArtworkUrl))
        {
            return cachedArtworkUrl;
        }

        var imageUrl = string.Empty;
        try
        {
            using var response = HttpClient.GetAsync(
                $"https://api.gog.com/v2/games/{Uri.EscapeDataString(id)}?locale=en-US").GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("_links", out var links))
                {
                    imageUrl = FirstNonEmpty(
                        GetGogLinkHref(links, "boxArtImage"),
                        GetGogLinkHref(links, "productCardImage"),
                        GetGogLinkHref(links, "coverArtImage"),
                        GetGogLinkHref(links, "backgroundImage"),
                        GetGogLinkHref(links, "galaxyBackgroundImage"),
                        GetGogLinkHref(links, "iconSquare"),
                        GetGogLinkHref(links, "icon"));
                }
            }
        }
        catch (Exception)
        {
            imageUrl = string.Empty;
        }

        var normalizedArtworkUrl = NormalizeImageUrl(imageUrl);
        GogArtworkCache[id] = normalizedArtworkUrl;
        return normalizedArtworkUrl;
    }

    private static List<UnifySteamGameCacheEntry> MergeGogProductDetails(
        IReadOnlyList<UnifySteamGameCacheEntry> games,
        IReadOnlyList<UnifySteamGameCacheEntry> productDetails)
    {
        var detailsById = productDetails
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var merged = new List<UnifySteamGameCacheEntry>(games.Count + productDetails.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            seen.Add(game.Id);
            if (!detailsById.TryGetValue(game.Id, out var details))
            {
                merged.Add(game);
                continue;
            }

            merged.Add(new UnifySteamGameCacheEntry
            {
                Id = game.Id,
                Title = FirstNonEmpty(game.Title, details.Title, game.Id),
                Installed = game.Installed,
                InstallPath = game.InstallPath,
                ExecutablePath = game.ExecutablePath,
                Version = game.Version,
                ImageUrl = FirstNonEmpty(details.ImageUrl, game.ImageUrl),
            });
        }

        foreach (var details in productDetails)
        {
            if (seen.Add(details.Id))
            {
                merged.Add(details);
            }
        }

        return DedupeLibraryGames(merged);
    }

    private void LoadGogAccountPages(string accessToken, List<UnifySteamGameCacheEntry> games)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://embed.gog.com/account/getFilteredProducts?mediaType=1&page=1");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = HttpClient.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GOG library could not be loaded (HTTP {(int)response.StatusCode}). Sign in again and retry.");
        }

        var firstPayload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        JsonDocument firstDocument;
        try
        {
            firstDocument = JsonDocument.Parse(firstPayload);
        }
        catch (JsonException)
        {
            var preview = firstPayload.Length > 160 ? firstPayload[..160] : firstPayload;
            throw new InvalidOperationException($"GOG returned an unexpected (non-JSON) response. It usually means the session is not valid - sign in again. Response started with: {preview}");
        }

        using var _ = firstDocument;
        var root = firstDocument.RootElement;
        var totalPages = root.TryGetProperty("totalPages", out var totalPagesNode) && totalPagesNode.TryGetInt32(out var parsedPages)
            ? Math.Max(parsedPages, 1)
            : 1;
        var totalProducts = root.TryGetProperty("totalProducts", out var totalProductsNode) && totalProductsNode.TryGetInt32(out var parsedProducts)
            ? parsedProducts
            : -1;
        var moviesCount = root.TryGetProperty("moviesCount", out var moviesCountNode) && moviesCountNode.TryGetInt32(out var parsedMovies)
            ? parsedMovies
            : -1;
        var pageProductCount = root.TryGetProperty("products", out var productsNode) && productsNode.ValueKind == JsonValueKind.Array
            ? productsNode.GetArrayLength()
            : -1;

        _journal.Append(
            "info",
            "unifysteam",
            "GOG library response received.",
            $"Page 1: {pageProductCount} products, totalPages={totalPages}, totalProducts={totalProducts}, moviesCount={moviesCount}.");

        AppendGogGames(root, games);

        for (var page = 2; page <= totalPages; page += 1)
        {
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, $"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}");
            pageRequest.Headers.Authorization = new("Bearer", accessToken);
            using var pageResponse = HttpClient.Send(pageRequest);
            if (!pageResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GOG library page {page} could not be loaded.");
            }

            var pagePayload = pageResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var pageDocument = JsonDocument.Parse(pagePayload);
            AppendGogGames(pageDocument.RootElement, games);
        }
    }

    private static void AppendGogGames(JsonElement root, ICollection<UnifySteamGameCacheEntry> target)
    {
        if (!root.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var product in products.EnumerateArray())
        {
            if (product.TryGetProperty("isGame", out var isGameNode) && isGameNode.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            var worksOnWindows = !product.TryGetProperty("worksOn", out var worksOn) ||
                                 worksOn.ValueKind != JsonValueKind.Object ||
                                 !worksOn.TryGetProperty("Windows", out var windowsNode) ||
                                 windowsNode.ValueKind != JsonValueKind.False;
            if (!worksOnWindows)
            {
                continue;
            }

            var id = GetJsonString(product, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            target.Add(new UnifySteamGameCacheEntry
            {
                Id = id,
                Title = FirstNonEmpty(GetJsonString(product, "title"), id),
                Installed = false,
                ImageUrl = NormalizeImageUrl(GetJsonString(product, "image")),
            });
        }
    }

    private static List<UnifySteamGameCacheEntry> DedupeLibraryGames(IEnumerable<UnifySteamGameCacheEntry> games)
    {
        return games
            .Where(game => game is not null &&
                           (!string.IsNullOrWhiteSpace(game.Id) || !string.IsNullOrWhiteSpace(game.Title)))
            .GroupBy(game => NormalizeGameTitleKey(FirstNonEmpty(game.Title, game.Id)), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(game => game.Installed)
                .ThenByDescending(game => !string.IsNullOrWhiteSpace(game.ExecutablePath))
                .ThenByDescending(game => !string.IsNullOrWhiteSpace(game.InstallPath))
                .ThenByDescending(game => !string.IsNullOrWhiteSpace(game.ImageUrl))
                .ThenBy(game => (game.Id ?? string.Empty).Length)
                .ThenBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeGameTitleKey(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private GogCredential ExchangeGogAuthorizationCode(string authCode)
    {
        var requestUri =
            $"{BuildGogTokenBaseUri()}&grant_type=authorization_code&code={Uri.EscapeDataString(authCode)}&redirect_uri={Uri.EscapeDataString(GogRedirectUri)}";
        using var response = HttpClient.GetAsync(requestUri).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("GOG rejected the code. Open the login page again and paste a fresh code.");
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        return ParseGogToken(document.RootElement);
    }

    private static string BuildGogTokenBaseUri()
    {
        return
            $"https://auth.gog.com/token?client_id={GogClientId}&client_secret={GogClientSecret}";
    }

    private static string BuildGogAuthUrl()
    {
        return
            $"https://auth.gog.com/auth?client_id={GogClientId}&redirect_uri={Uri.EscapeDataString(GogRedirectUri)}&response_type=code&layout=client2";
    }

    private static EpicCredential ExchangeEpicAuthorizationCode(string authCode)
    {
        using var document = PostEpicTokenRequest(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authCode,
        }, "Epic rejected the code. Open the login page again and paste a fresh code.");

        return ParseEpicToken(document.RootElement);
    }

    private static EpicCredential ExchangeEpicRefreshToken(string refreshToken)
    {
        using var document = PostEpicTokenRequest(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, "The saved Epic login could not be refreshed. Sign in again.");

        return ParseEpicToken(document.RootElement);
    }

    private static JsonDocument PostEpicTokenRequest(
        IReadOnlyDictionary<string, string> formValues,
        string failureMessage)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
        {
            Content = new FormUrlEncodedContent(formValues),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{EpicClientId}:{EpicClientSecret}")));

        using var response = HttpClient.Send(request);
        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(failureMessage);
        }

        return JsonDocument.Parse(json);
    }

    private static GogCredential ParseGogToken(JsonElement root)
    {
        var expiresIn = root.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 3600;
        var accessToken = GetJsonString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The GOG token response did not include an access token.");
        }

        var loginTime = DateTimeOffset.UtcNow;
        return new GogCredential(
            accessToken,
            GetJsonString(root, "refresh_token"),
            GetJsonString(root, "user_id"),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static EpicCredential ParseEpicToken(JsonElement root)
    {
        var expiresIn = root.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 7200;
        var accessToken = GetJsonString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The Epic token response did not include an access token.");
        }

        var loginTime = DateTimeOffset.UtcNow;
        return new EpicCredential(
            accessToken,
            GetJsonString(root, "refresh_token"),
            FirstNonEmpty(GetJsonString(root, "account_id"), GetJsonString(root, "accountId")),
            FirstNonEmpty(GetJsonString(root, "displayName"), GetJsonString(root, "display_name")),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static GogCredential? LoadGogCredential(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(authPath));
        JsonElement credentialNode;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(GogClientId, out var gogCredentialNode))
        {
            credentialNode = gogCredentialNode;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var firstProperty = document.RootElement.EnumerateObject().FirstOrDefault();
            credentialNode = firstProperty.Value;
        }
        else
        {
            return null;
        }

        var accessToken = GetJsonString(credentialNode, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var expiresIn = credentialNode.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 3600;
        var loginTimeSeconds = credentialNode.TryGetProperty("loginTime", out var loginTimeNode) && loginTimeNode.TryGetDouble(out var parsedLoginTime)
            ? parsedLoginTime
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var loginTime = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Math.Floor(loginTimeSeconds)));

        return new GogCredential(
            accessToken,
            GetJsonString(credentialNode, "refresh_token"),
            GetJsonString(credentialNode, "user_id"),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static EpicCredential? LoadEpicCredential(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(authPath));
        JsonElement credentialNode;
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(EpicClientId, out var epicCredentialNode))
        {
            credentialNode = epicCredentialNode;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            credentialNode = document.RootElement;
        }
        else
        {
            return null;
        }

        var accessToken = GetJsonString(credentialNode, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var expiresIn = credentialNode.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 7200;
        var loginTimeSeconds = credentialNode.TryGetProperty("loginTime", out var loginTimeNode) && loginTimeNode.TryGetDouble(out var parsedLoginTime)
            ? parsedLoginTime
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var loginTime = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Math.Floor(loginTimeSeconds)));

        return new EpicCredential(
            accessToken,
            GetJsonString(credentialNode, "refresh_token"),
            FirstNonEmpty(GetJsonString(credentialNode, "account_id"), GetJsonString(credentialNode, "accountId")),
            FirstNonEmpty(GetJsonString(credentialNode, "displayName"), GetJsonString(credentialNode, "display_name")),
            loginTime,
            loginTime.AddSeconds(Math.Max(expiresIn, 1)));
    }

    private static void SaveGogCredentials(string authPath, GogCredential credential)
    {
        var directory = Path.GetDirectoryName(authPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [GogClientId] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["access_token"] = credential.AccessToken,
                ["refresh_token"] = credential.RefreshToken,
                ["user_id"] = credential.UserId,
                ["expires_in"] = (int)Math.Max(1, (credential.ExpiresAtUtc - credential.LoginTimeUtc).TotalSeconds),
                ["loginTime"] = credential.LoginTimeUtc.ToUnixTimeSeconds(),
                ["token_type"] = "bearer",
            }
        };

        File.WriteAllText(authPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static void SaveEpicCredentials(string authPath, EpicCredential credential)
    {
        var directory = Path.GetDirectoryName(authPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [EpicClientId] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["access_token"] = credential.AccessToken,
                ["refresh_token"] = credential.RefreshToken,
                ["account_id"] = credential.AccountId,
                ["displayName"] = credential.DisplayName,
                ["expires_in"] = (int)Math.Max(1, (credential.ExpiresAtUtc - credential.LoginTimeUtc).TotalSeconds),
                ["loginTime"] = credential.LoginTimeUtc.ToUnixTimeSeconds(),
                ["token_type"] = "bearer",
            }
        };

        File.WriteAllText(authPath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static CommandResult RunTool(string toolPath, params string[] arguments)
    {
        var startInfo = CreateHiddenStartInfo(toolPath, arguments);
        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120000);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new InvalidOperationException("The external launcher tool timed out.");
        }

        Task.WaitAll(outputTask, errorTask);
        return new CommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static void StartDetachedProcess(string toolPath, string arguments, bool visible)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = true,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };

        if (IsBatchLike(toolPath))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{toolPath}\" {arguments}\"";
        }
        else
        {
            startInfo.FileName = toolPath;
            startInfo.Arguments = arguments;
        }

        Process.Start(startInfo)?.Dispose();
    }

    private static ProcessStartInfo CreateHiddenStartInfo(string toolPath, IReadOnlyList<string> arguments)
    {
        if (IsBatchLike(toolPath))
        {
            var batchStartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            batchStartInfo.ArgumentList.Add("/d");
            batchStartInfo.ArgumentList.Add("/s");
            batchStartInfo.ArgumentList.Add("/c");
            batchStartInfo.ArgumentList.Add($"\"{toolPath}\" {JoinCommandLine(arguments)}");
            return batchStartInfo;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string JoinCommandLine(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
    }

    private static bool IsBatchLike(string toolPath)
    {
        var extension = Path.GetExtension(toolPath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveToolPath(UnifySteamStoreDefinition definition, string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in GetCandidateToolPaths(definition))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string ResolveEpicLauncherPath()
    {
        foreach (var candidate in GetEpicLauncherCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetEpicLauncherCandidates()
    {
        foreach (var candidate in FindOnPath("EpicGamesLauncher.exe"))
        {
            yield return candidate;
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
                 })
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe");
        }
    }

    private static IEnumerable<string> GetCandidateToolPaths(UnifySteamStoreDefinition definition)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in FindOnPath(definition.ToolExecutableName, definition.ToolCommandName))
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        var heroicBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "heroic",
            "resources",
            "app.asar.unpacked",
            "build",
            "bin",
            "win32");

        foreach (var candidate in new[]
                 {
                     Path.Combine(heroicBase, definition.ToolExecutableName),
                     Path.Combine(heroicBase, definition.ToolCommandName),
                 })
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> FindOnPath(params string[] names)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists);

        foreach (var directory in directories)
        {
            foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }

                if (Path.HasExtension(name))
                {
                    continue;
                }

                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    var extendedCandidate = candidate + extension;
                    if (File.Exists(extendedCandidate))
                    {
                        yield return extendedCandidate;
                    }
                }
            }
        }
    }

    private static string ResolveReadableGogAuthPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in GetCandidateGogAuthPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return configuredPath.Trim();
    }

    private static string ResolveReadableEpicAuthPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in GetCandidateEpicAuthPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string ResolveWritableGogAuthPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return NormalizePath(configuredPath);
        }

        return NormalizePath(GetCandidateGogAuthPaths().First());
    }

    private static string ResolveWritableEpicAuthPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return NormalizePath(configuredPath);
        }

        return NormalizePath(GetCandidateEpicAuthPaths().First());
    }

    private static IEnumerable<string> GetCandidateGogAuthPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "heroic", "gog_store", "auth.json");
        yield return Path.Combine(appData, "heroic_gogdl", "auth.json");
    }

    private static IEnumerable<string> GetCandidateEpicAuthPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "ToolsForSteam", "unifystore", "epic-auth.json");
    }

    private static string ResolveInstalledExecutablePath(JsonElement item)
    {
        var executable = GetJsonString(item, "executable");
        var installPath = GetJsonString(item, "install_path");
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(executable))
        {
            return NormalizePath(executable);
        }

        if (!string.IsNullOrWhiteSpace(installPath))
        {
            return NormalizePath(Path.Combine(installPath, executable));
        }

        return executable;
    }

    private static string ResolveExecutablePath(string installPath, string executableHint)
    {
        if (string.IsNullOrWhiteSpace(executableHint))
        {
            return string.Empty;
        }

        var cleanedHint = executableHint.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(cleanedHint)
            ? NormalizePath(cleanedHint)
            : NormalizePath(Path.Combine(installPath ?? string.Empty, cleanedHint));
    }

    private static string ExtractAuthorizationCode(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?');
            foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = segment.Split('=', 2);
                if (parts.Length == 2 && string.Equals(parts[0], "code", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
        }

        // Epic's login page ends on a JSON document containing "authorizationCode".
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var propertyName in new[] { "authorizationCode", "code" })
                    {
                        if (document.RootElement.TryGetProperty(propertyName, out var codeNode) &&
                            codeNode.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(codeNode.GetString()))
                        {
                            return codeNode.GetString()!.Trim();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not valid JSON after all; fall through to the raw value.
            }
        }

        return trimmed;
    }

    private static string NormalizeImageUrl(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return string.Empty;
        }

        var normalized = imageUrl.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = $"https:{normalized}";
        }

        return UpgradeArtworkUrl(normalized);
    }

    private static string UpgradeArtworkUrl(string imageUrl)
    {
        if (imageUrl.Contains("gog-statics.com", StringComparison.OrdinalIgnoreCase))
        {
            var upgraded = Regex.Replace(
                imageUrl,
                @"_(196|392)(\.[a-z0-9]+)(\?.*)?$",
                "_784$2$3",
                RegexOptions.IgnoreCase);
            upgraded = Regex.Replace(
                upgraded,
                @"_product_card_v2_mobile_slider_\d+(\.[a-z0-9]+)(\?.*)?$",
                "_product_card_v2_mobile_slider_1280$1$2",
                RegexOptions.IgnoreCase);
            upgraded = Regex.Replace(
                upgraded,
                @"_glx_vertical_cover_\d+(\.[a-z0-9]+)(\?.*)?$",
                "_glx_vertical_cover_1200$1$2",
                RegexOptions.IgnoreCase);
            return upgraded;
        }

        if (imageUrl.Contains("epicgames", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("unrealengine", StringComparison.OrdinalIgnoreCase))
        {
            var upgraded = Regex.Replace(imageUrl, @"([?&])w=\d+", "$1w=1200", RegexOptions.IgnoreCase);
            upgraded = Regex.Replace(upgraded, @"([?&])h=\d+", "$1h=1600", RegexOptions.IgnoreCase);
            return upgraded;
        }

        return imageUrl;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static string GetGogLinkHref(JsonElement links, string propertyName)
    {
        if (links.ValueKind != JsonValueKind.Object ||
            !links.TryGetProperty(propertyName, out var link))
        {
            return string.Empty;
        }

        return link.ValueKind switch
        {
            JsonValueKind.String => link.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Object => GetJsonString(link, "href"),
            _ => string.Empty,
        };
    }

    private static int GetJsonInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => 0,
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static UnifySteamStoreConfiguration GetStoreConfiguration(StoreSyncConfiguration configuration, string storeId)
    {
        if (!configuration.UnifySteam.Stores.TryGetValue(storeId, out var storeConfiguration) || storeConfiguration is null)
        {
            storeConfiguration = new UnifySteamStoreConfiguration();
            configuration.UnifySteam.Stores[storeId] = storeConfiguration;
        }

        storeConfiguration.Cache ??= new UnifySteamLibraryCache();
        storeConfiguration.Cache.Games ??= [];
        return storeConfiguration;
    }

    private static UnifySteamStoreDefinition ResolveDefinition(string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new InvalidOperationException("A store ID is required.");
        }

        return Definitions.FirstOrDefault(definition =>
                   string.Equals(definition.Id, storeId.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("Unknown Storefront store.");
    }

    private static IEnumerable<UnifySteamStoreDefinition> ResolveDefinitions(string? storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            return Definitions;
        }

        return [ResolveDefinition(storeId)];
    }

    private sealed record UnifySteamStoreDefinition(
        string Id,
        string Title,
        string ToolCommandName,
        string ToolExecutableName,
        bool SupportsManualCodeAuth);

    private sealed record GogCredential(
        string AccessToken,
        string RefreshToken,
        string UserId,
        DateTimeOffset LoginTimeUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record EpicCredential(
        string AccessToken,
        string RefreshToken,
        string AccountId,
        string DisplayName,
        DateTimeOffset LoginTimeUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record GogLibraryResponse(
        string AccountName,
        List<UnifySteamGameCacheEntry> Games);

    private sealed record EpicStatus(
        bool Authenticated,
        string AccountName);

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
