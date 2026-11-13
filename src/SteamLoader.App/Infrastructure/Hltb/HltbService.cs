using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Hltb;

public sealed class HltbService
{
    private const string BaseUrl = "https://howlongtobeat.com";
    private const string DefaultSearchEndpoint = "/api/search/site";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Shared across requests; updated when the site moved its search path.
    private static string CurrentSearchEndpoint { get; set; } = DefaultSearchEndpoint;

    private readonly HltbSettingsStore _settingsStore;
    private readonly object _gate = new();

    public HltbService(HltbSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public HltbSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return BuildSnapshot(_settingsStore.Load());
        }
    }

    public HltbSnapshot ToggleSetting(string key)
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            var settings = configuration.Settings;

            switch (key.Trim().ToLowerInvariant())
            {
                case "enabled":
                    settings.Enabled = !settings.Enabled;
                    break;
                case "show-main-story":
                    settings.ShowMainStory = !settings.ShowMainStory;
                    break;
                case "show-main-plus":
                    settings.ShowMainPlus = !settings.ShowMainPlus;
                    break;
                case "show-completionist":
                    settings.ShowCompletionist = !settings.ShowCompletionist;
                    break;
                case "show-all-styles":
                    settings.ShowAllStyles = !settings.ShowAllStyles;
                    break;
                case "show-view-details":
                    settings.ShowViewDetails = !settings.ShowViewDetails;
                    break;
                default:
                    throw new InvalidOperationException("The requested HLTB setting could not be found.");
            }

            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration);
        }
    }

    public HltbSnapshot ClearCache()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.Cache.Clear();
            _settingsStore.Save(configuration);
            return BuildSnapshot(configuration);
        }
    }

    public async Task<HltbGameSnapshot> GetGameAsync(string title, int? appId, CancellationToken cancellationToken)
    {
        var requestedTitle = title?.Trim() ?? string.Empty;
        var normalizedTitle = NormalizeTitle(requestedTitle);
        HltbConfiguration configuration;

        lock (_gate)
        {
            configuration = _settingsStore.Load();
        }

        var settingsState = BuildSettingsState(configuration);
        if (!configuration.Settings.Enabled || (string.IsNullOrWhiteSpace(normalizedTitle) && !appId.HasValue))
        {
            return new HltbGameSnapshot(
                RequestedTitle: requestedTitle,
                MatchedTitle: string.Empty,
                AppId: appId,
                GameId: null,
                MainStory: "--",
                MainPlus: "--",
                Completionist: "--",
                AllStyles: "--",
                DetailUrl: string.Empty,
                Found: false,
                Cached: false,
                Settings: settingsState,
                ErrorMessage: null);
        }

        var cacheKey = BuildCacheKey(requestedTitle, appId);
        if (configuration.Cache.TryGetValue(cacheKey, out var cachedEntry) &&
            cachedEntry is not null &&
            !NeedsRefresh(cachedEntry.LastUpdatedAtUtc))
        {
            return BuildGameSnapshot(cachedEntry, settingsState, cached: true);
        }

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return new HltbGameSnapshot(
                RequestedTitle: requestedTitle,
                MatchedTitle: string.Empty,
                AppId: appId,
                GameId: null,
                MainStory: "--",
                MainPlus: "--",
                Completionist: "--",
                AllStyles: "--",
                DetailUrl: string.Empty,
                Found: false,
                Cached: false,
                Settings: settingsState,
                ErrorMessage: null);
        }

        try
        {
            var fetchedEntry = await FetchFromHltbAsync(requestedTitle, appId, cancellationToken);

            lock (_gate)
            {
                var latestConfiguration = _settingsStore.Load();
                latestConfiguration.Cache[cacheKey] = fetchedEntry;
                _settingsStore.Save(latestConfiguration);
                settingsState = BuildSettingsState(latestConfiguration);
            }

            return BuildGameSnapshot(fetchedEntry, settingsState, cached: false);
        }
        catch (Exception exception)
        {
            if (cachedEntry is not null)
            {
                return BuildGameSnapshot(cachedEntry, settingsState, cached: true) with
                {
                    ErrorMessage = exception.Message
                };
            }

            return new HltbGameSnapshot(
                RequestedTitle: requestedTitle,
                MatchedTitle: string.Empty,
                AppId: appId,
                GameId: null,
                MainStory: "--",
                MainPlus: "--",
                Completionist: "--",
                AllStyles: "--",
                DetailUrl: string.Empty,
                Found: false,
                Cached: false,
                Settings: settingsState,
                ErrorMessage: exception.Message);
        }
    }

    private static bool NeedsRefresh(DateTimeOffset lastUpdatedAtUtc)
    {
        return DateTimeOffset.UtcNow - lastUpdatedAtUtc > CacheLifetime;
    }

    private static HltbSnapshot BuildSnapshot(HltbConfiguration configuration)
    {
        var settingsState = BuildSettingsState(configuration);
        var enabledStatCount = 0;
        enabledStatCount += configuration.Settings.ShowMainStory ? 1 : 0;
        enabledStatCount += configuration.Settings.ShowMainPlus ? 1 : 0;
        enabledStatCount += configuration.Settings.ShowCompletionist ? 1 : 0;
        enabledStatCount += configuration.Settings.ShowAllStyles ? 1 : 0;

        var statusText = configuration.Settings.Enabled
            ? $"{enabledStatCount} stat categories enabled - {configuration.Cache.Count} cached game{(configuration.Cache.Count == 1 ? string.Empty : "s")}."
            : "Game page stats are currently disabled.";

        return new HltbSnapshot(settingsState, statusText);
    }

    private static HltbSettingsState BuildSettingsState(HltbConfiguration configuration)
    {
        return new HltbSettingsState(
            configuration.Settings.Enabled,
            configuration.Settings.ShowMainStory,
            configuration.Settings.ShowMainPlus,
            configuration.Settings.ShowCompletionist,
            configuration.Settings.ShowAllStyles,
            configuration.Settings.ShowViewDetails,
            configuration.Cache.Count);
    }

    private static HltbGameSnapshot BuildGameSnapshot(
        HltbCacheEntry entry,
        HltbSettingsState settings,
        bool cached)
    {
        return new HltbGameSnapshot(
            RequestedTitle: entry.RequestedTitle,
            MatchedTitle: string.IsNullOrWhiteSpace(entry.MatchedTitle) ? entry.RequestedTitle : entry.MatchedTitle,
            AppId: entry.AppId,
            GameId: entry.GameId,
            MainStory: FormatHours(entry.MainStoryHours),
            MainPlus: FormatHours(entry.MainPlusHours),
            Completionist: FormatHours(entry.CompletionistHours),
            AllStyles: FormatHours(entry.AllStylesHours),
            DetailUrl: entry.DetailUrl,
            Found: entry.Found,
            Cached: cached,
            Settings: settings,
            ErrorMessage: null);
    }

    private static async Task<HltbCacheEntry> FetchFromHltbAsync(
        string requestedTitle,
        int? appId,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(18)
        };

        // Exact name/alias matches of any title variant win over a fuzzy match
        // ("... Game of the Year Edition" must land on the base game, not on
        // a similar-looking remaster).
        HltbSearchResult? bestMatch = null;
        var searched = new List<(string Title, IReadOnlyList<HltbSearchResult> Results)>();
        foreach (var searchTitle in BuildSearchTitles(requestedTitle))
        {
            var results = await SearchAsync(httpClient, searchTitle, cancellationToken);
            searched.Add((searchTitle, results));
            bestMatch = SelectBestMatch(searchTitle, appId, results, allowFuzzy: false);
            if (bestMatch is not null)
            {
                break;
            }
        }

        if (bestMatch is null)
        {
            foreach (var (searchTitle, results) in searched)
            {
                bestMatch = SelectBestMatch(searchTitle, appId, results);
                if (bestMatch is not null)
                {
                    break;
                }
            }
        }

        if (bestMatch is null)
        {
            return new HltbCacheEntry
            {
                RequestedTitle = requestedTitle,
                MatchedTitle = requestedTitle,
                AppId = appId,
                Found = false,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        return new HltbCacheEntry
        {
            RequestedTitle = requestedTitle,
            MatchedTitle = bestMatch.GameName ?? requestedTitle,
            AppId = appId,
            GameId = bestMatch.GameId,
            MainStoryHours = ToHours(bestMatch.CompMain),
            MainPlusHours = ToHours(bestMatch.CompPlus),
            CompletionistHours = ToHours(bestMatch.Comp100),
            AllStylesHours = ToHours(bestMatch.CompAll),
            DetailUrl = $"https://howlongtobeat.com/game/{bestMatch.GameId}",
            Found = true,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// One search on howlongtobeat.com: fetch a search token from the init
    /// endpoint, then post the search. HLTB moves this endpoint every few
    /// months (/api/search, /api/find, /api/seek, /api/bleed, /api/search/site
    /// ...). A 404 makes us read the current path from the site's own
    /// JavaScript once and retry, so the next move does not break the stats.
    /// </summary>
    private static async Task<IReadOnlyList<HltbSearchResult>> SearchAsync(
        HttpClient httpClient,
        string title,
        CancellationToken cancellationToken)
    {
        var endpoint = CurrentSearchEndpoint;
        var rediscovered = false;
        var tokenRefreshed = false;

        while (true)
        {
            var authState = await FetchAuthTokenAsync(httpClient, endpoint, cancellationToken);
            if (authState is null && !rediscovered)
            {
                rediscovered = true;
                endpoint = await DiscoverSearchEndpointAsync(httpClient, endpoint, cancellationToken);
                continue;
            }

            if (authState is null || string.IsNullOrWhiteSpace(authState.Token))
            {
                throw new InvalidOperationException("HowLongToBeat did not return a search token.");
            }

            using var request = CreateHltbRequest(HttpMethod.Post, BaseUrl + endpoint);
            request.Headers.TryAddWithoutValidation("x-auth-token", authState.Token);
            if (!string.IsNullOrWhiteSpace(authState.HpKey))
            {
                request.Headers.TryAddWithoutValidation("x-hp-key", authState.HpKey);
                request.Headers.TryAddWithoutValidation("x-hp-val", authState.HpVal);
            }

            request.Content = new StringContent(
                JsonSerializer.Serialize(BuildSearchPayload(title, authState), JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if ((response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed) &&
                !rediscovered)
            {
                rediscovered = true;
                endpoint = await DiscoverSearchEndpointAsync(httpClient, endpoint, cancellationToken);
                continue;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && !tokenRefreshed)
            {
                // The site's own client refreshes an expired token once, too.
                tokenRefreshed = true;
                continue;
            }

            if ((int)response.StatusCode == 429)
            {
                throw new InvalidOperationException("HowLongToBeat is rate limiting searches right now.");
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<HltbSearchResponse>(stream, JsonOptions, cancellationToken)
                ?? new HltbSearchResponse();
            return payload.Data ?? [];
        }
    }

    private static HttpRequestMessage CreateHltbRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Origin", BaseUrl);
        request.Headers.Referrer = new Uri(BaseUrl + "/");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        return request;
    }

    /// <returns>The token, or null when the init endpoint does not exist (moved).</returns>
    private static async Task<HltbSearchInitResponse?> FetchAuthTokenAsync(
        HttpClient httpClient,
        string endpoint,
        CancellationToken cancellationToken)
    {
        using var request = CreateHltbRequest(
            HttpMethod.Get,
            $"{BaseUrl}{endpoint}/init?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<HltbSearchInitResponse>(stream, JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Reads the current search path from the site's JavaScript bundles
    /// (the page calls <c>fetch(`/api/.../init?t=...`)</c> before searching).
    /// Falls back to the previous path when nothing is found.
    /// </summary>
    private static async Task<string> DiscoverSearchEndpointAsync(
        HttpClient httpClient,
        string previousEndpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var pageRequest = CreateHltbRequest(HttpMethod.Get, BaseUrl + "/");
            pageRequest.Headers.Remove("Accept");
            pageRequest.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var pageResponse = await httpClient.SendAsync(pageRequest, cancellationToken);
            pageResponse.EnsureSuccessStatusCode();
            var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken);

            foreach (var scriptPath in ExtractScriptPaths(html).Take(40))
            {
                using var scriptRequest = CreateHltbRequest(HttpMethod.Get, BaseUrl + scriptPath);
                using var scriptResponse = await httpClient.SendAsync(scriptRequest, cancellationToken);
                if (!scriptResponse.IsSuccessStatusCode)
                {
                    continue;
                }

                var script = await scriptResponse.Content.ReadAsStringAsync(cancellationToken);
                var endpoint = ExtractSearchEndpoint(script);
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    CurrentSearchEndpoint = endpoint;
                    return endpoint;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the previous path; the caller reports the search error.
        }

        return previousEndpoint;
    }

    internal static IEnumerable<string> ExtractScriptPaths(string html)
    {
        return System.Text.RegularExpressions.Regex
            .Matches(html ?? string.Empty, "/_next/static/chunks/[A-Za-z0-9_\\-./]+?\\.js")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>"/api/search/site" from <c>fetch(`/api/search/site/init?t=${Date.now()}`)</c>.</summary>
    internal static string? ExtractSearchEndpoint(string script)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            script ?? string.Empty,
            "[\"'`](/api/[A-Za-z0-9_\\-/]+?)/init\\?t=");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Title variants tried in order: the cleaned title, then without edition
    /// suffixes ("Definitive Edition", "GOTY", "- Remastered" ...).
    /// </summary>
    internal static IReadOnlyList<string> BuildSearchTitles(string title)
    {
        var titles = new List<string>();
        var cleaned = System.Text.RegularExpressions.Regex
            .Replace(title ?? string.Empty, "[™®©]", string.Empty)
            .Replace('–', '-')
            .Replace('—', '-');
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            titles.Add(cleaned);
        }

        var withoutEdition = System.Text.RegularExpressions.Regex.Replace(
            cleaned,
            "\\s*[-:(]?\\s*(?:the\\s+)?(?:definitive|complete|ultimate|deluxe|enhanced|gold|goty|game of the year|anniversary|director'?s cut|remastered|special|standard|digital deluxe|premium)(?:\\s+edition)?\\s*\\)?\\s*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(' ', '-', ':');
        if (!string.IsNullOrWhiteSpace(withoutEdition) &&
            !titles.Contains(withoutEdition, StringComparer.OrdinalIgnoreCase))
        {
            titles.Add(withoutEdition);
        }

        return titles;
    }

    internal static Dictionary<string, object?> BuildSearchPayload(string title, HltbSearchInitResponse authState)
    {
        // Matches the payload of the site's own search (filters are
        // {mode, values} objects since the /api/search/site version).
        static Dictionary<string, object?> AnyFilter() => new()
        {
            ["mode"] = "include",
            ["values"] = Array.Empty<string>(),
        };

        var payload = new Dictionary<string, object?>
        {
            ["searchType"] = "games",
            ["searchTerms"] = title
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ["searchPage"] = 1,
            ["size"] = 20,
            ["searchOptions"] = new Dictionary<string, object?>
            {
                ["games"] = new Dictionary<string, object?>
                {
                    ["userId"] = 0,
                    ["platform"] = AnyFilter(),
                    ["sortCategory"] = "popular",
                    ["rangeCategory"] = "main",
                    ["rangeTime"] = new Dictionary<string, object?>
                    {
                        ["min"] = null,
                        ["max"] = null,
                    },
                    ["gameplay"] = new Dictionary<string, object?>
                    {
                        ["perspective"] = AnyFilter(),
                        ["flow"] = AnyFilter(),
                        ["genre"] = AnyFilter(),
                    },
                    ["year"] = AnyFilter(),
                    ["modifier"] = string.Empty,
                },
                ["users"] = new Dictionary<string, object?>
                {
                    ["sortCategory"] = "postcount",
                },
                ["lists"] = new Dictionary<string, object?>
                {
                    ["sortCategory"] = "follows",
                },
                ["filter"] = string.Empty,
                ["sort"] = 0,
                ["randomizer"] = 0,
            },
            ["useCache"] = true,
        };

        // Older endpoint versions wanted a honeypot key/value in the body.
        if (!string.IsNullOrWhiteSpace(authState.HpKey))
        {
            payload[authState.HpKey] = authState.HpVal;
        }

        return payload;
    }

    internal static HltbSearchResult? SelectBestMatch(
        string requestedTitle,
        int? appId,
        IReadOnlyList<HltbSearchResult> results,
        bool allowFuzzy = true)
    {
        if (results.Count == 0)
        {
            return null;
        }

        if (appId.HasValue)
        {
            var bySteamId = results.FirstOrDefault(result => result.ProfileSteam == appId.Value);
            if (bySteamId is not null)
            {
                return bySteamId;
            }
        }

        var normalizedRequestedTitle = NormalizeTitle(requestedTitle);
        var exactNameMatch = results.FirstOrDefault(result =>
            string.Equals(NormalizeTitle(result.GameName), normalizedRequestedTitle, StringComparison.OrdinalIgnoreCase));
        if (exactNameMatch is not null)
        {
            return exactNameMatch;
        }

        var aliasMatch = results.FirstOrDefault(result =>
            EnumerateAliases(result.GameAlias).Any(alias =>
                string.Equals(NormalizeTitle(alias), normalizedRequestedTitle, StringComparison.OrdinalIgnoreCase)));
        if (aliasMatch is not null)
        {
            return aliasMatch;
        }

        if (!allowFuzzy)
        {
            return null;
        }

        var best = results
            .Select(result => (Result: result, Score: ScoreResult(normalizedRequestedTitle, result)))
            .OrderBy(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Result.CompAllCount)
            .First();

        // A loose search can return unrelated games; showing their times on
        // the wrong page is worse than showing nothing.
        var tolerance = Math.Max(3, normalizedRequestedTitle.Length / 3);
        return best.Score <= tolerance ? best.Result : null;
    }

    private static int ScoreResult(string normalizedRequestedTitle, HltbSearchResult result)
    {
        var candidateNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.GameName))
        {
            candidateNames.Add(NormalizeTitle(result.GameName));
        }

        candidateNames.AddRange(EnumerateAliases(result.GameAlias).Select(NormalizeTitle));
        candidateNames = candidateNames
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateNames.Count == 0)
        {
            return int.MaxValue;
        }

        var bestScore = int.MaxValue;
        foreach (var candidateName in candidateNames)
        {
            var score = LevenshteinDistance(normalizedRequestedTitle, candidateName);
            if (candidateName.Contains(normalizedRequestedTitle, StringComparison.OrdinalIgnoreCase) ||
                normalizedRequestedTitle.Contains(candidateName, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(0, score - 2);
            }

            bestScore = Math.Min(bestScore, score);
        }

        return bestScore;
    }

    private static IEnumerable<string> EnumerateAliases(string? aliases)
    {
        if (string.IsNullOrWhiteSpace(aliases))
        {
            yield break;
        }

        foreach (var alias in aliases.Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return alias;
        }
    }

    private static string BuildCacheKey(string title, int? appId)
    {
        return appId.HasValue && appId.Value > 0
            ? $"app:{appId.Value}"
            : $"title:{NormalizeTitle(title)}";
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character) || character == ' ' || character == '-' || character == '/')
            {
                builder.Append(character);
            }
        }

        return string.Join(
            ' ',
            builder.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previousRow = new int[right.Length + 1];
        var currentRow = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previousRow[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            currentRow[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                currentRow[column] = Math.Min(
                    Math.Min(currentRow[column - 1] + 1, previousRow[column] + 1),
                    previousRow[column - 1] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[right.Length];
    }

    private static double? ToHours(int seconds)
    {
        if (seconds <= 0)
        {
            return null;
        }

        return Math.Round(seconds / 3600d, 1, MidpointRounding.AwayFromZero);
    }

    private static string FormatHours(double? value)
    {
        return value.HasValue && value.Value > 0
            ? value.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "--";
    }

    internal sealed class HltbSearchResponse
    {
        [JsonPropertyName("data")]
        public List<HltbSearchResult> Data { get; set; } = [];
    }

    internal sealed class HltbSearchInitResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("hpKey")]
        public string HpKey { get; set; } = string.Empty;

        [JsonPropertyName("hpVal")]
        public string HpVal { get; set; } = string.Empty;
    }

    internal sealed class HltbSearchResult
    {
        [JsonPropertyName("game_id")]
        public int GameId { get; set; }

        [JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonPropertyName("game_alias")]
        public string GameAlias { get; set; } = string.Empty;

        [JsonPropertyName("comp_main")]
        public int CompMain { get; set; }

        [JsonPropertyName("comp_plus")]
        public int CompPlus { get; set; }

        [JsonPropertyName("comp_100")]
        public int Comp100 { get; set; }

        [JsonPropertyName("comp_all")]
        public int CompAll { get; set; }

        [JsonPropertyName("comp_all_count")]
        public int CompAllCount { get; set; }

        [JsonPropertyName("profile_steam")]
        public int ProfileSteam { get; set; }
    }
}
