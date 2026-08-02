using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Title-scoped, user-scoped achievement providers for OmniLibrary.
///
/// This service deliberately does not participate in the ownership catalog
/// loop. It is called only for an opened title, a manual refresh, or the
/// post-play refresh hook. Provider failures never delete the last good data.
/// </summary>
internal sealed class OmniLibraryAchievementProvider
{
    private const string EpicClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string EpicClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string EpicGraphQlUrl = "https://launcher.store.epicgames.com/graphql";
    private const string EpicLibraryAssetsUrl =
        "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true&platform=Windows";
    private const string EpicLauncherUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) EpicGamesLauncher";
    private static readonly TimeSpan DefaultFailureBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AuthenticationFailureBackoff = TimeSpan.FromHours(1);

    private readonly StoreSyncSettingsStore? _settingsStore;
    private readonly HttpClient _httpClient;
    private readonly string _epicCredentialPath;
    private readonly IReadOnlyDictionary<string, IOmniLibraryAchievementSource> _sources;
    private readonly object _configurationGate = new();
    private readonly object _epicAccountGate = new();
    private readonly SemaphoreSlim _epicAssetGate = new(1, 1);
    private StoreSyncConfiguration? _configuration;
    private long _configurationRevision = long.MinValue;
    private long _epicCredentialRevision = long.MinValue;
    private string _epicAccountId = string.Empty;
    private string _epicAssetTokenFingerprint = string.Empty;
    private IReadOnlyList<EpicLibraryAsset> _epicAssets = [];

    public OmniLibraryAchievementProvider(
        StoreSyncSettingsStore? settingsStore,
        HttpClient httpClient,
        string? epicCredentialPath = null)
    {
        _settingsStore = settingsStore;
        _httpClient = httpClient;
        _epicCredentialPath = string.IsNullOrWhiteSpace(epicCredentialPath)
            ? ManagedLegendaryHelper.UserDataPath
            : Path.GetFullPath(epicCredentialPath);
        _sources = new IOmniLibraryAchievementSource[]
        {
            new DelegatingOmniLibraryAchievementSource(
                "xbox-live",
                async (context, cancellationToken) =>
                {
                    ApplyXboxProviderConfiguration(
                        context.Store,
                        context.Provider);
                    return await RefreshXboxAsync(
                        context.GameDetail,
                        context.Store,
                        cancellationToken).ConfigureAwait(false);
                }),
            new DelegatingOmniLibraryAchievementSource(
                "epic-games",
                (context, cancellationToken) => RefreshEpicAsync(
                    context.GameDetail,
                    context.Provider,
                    context.Previous,
                    context.PreviousProviderState,
                    context.RefreshDefinitions,
                    context.RefreshProgress,
                    cancellationToken)),
            new OmniLibraryGogAchievementSource(httpClient, settingsStore),
            new OmniLibraryBattleNetAchievementSource(httpClient),
            new OmniLibraryEaAchievementSource(httpClient, settingsStore),
            new OmniLibraryFfxivAchievementSource(httpClient),
            new OmniLibraryPsnAchievementSource(httpClient, settingsStore),
            new OmniLibraryRetroAchievementsSource(
                httpClient,
                PersistProviderGameId),
        }.ToDictionary(source => source.ProviderId, StringComparer.OrdinalIgnoreCase);
    }

    public string GetConfigurationFingerprint(
        UnifySteamGameDetailSnapshot gameDetail)
    {
        var configuration = LoadConfiguration();
        if (configuration is null ||
            !configuration.UnifySteam.Stores.TryGetValue(
                gameDetail.StoreId,
                out var store) ||
            store is null)
        {
            return $"{gameDetail.StoreId}:unconfigured";
        }

        var descriptor = ResolveProviderForGame(configuration, gameDetail);
        if (descriptor is null ||
            !configuration.UnifySteam.GameData.Providers.TryGetValue(
                descriptor.Id,
                out var provider) ||
            provider is null)
        {
            return $"{gameDetail.StoreId}:provider-unconfigured";
        }

        var game = gameDetail.Game;
        var identity = descriptor.Id.Equals(
                "xbox-live",
                StringComparison.OrdinalIgnoreCase)
                ? $"{game?.StoreTitleId}|{ResolvedStoredXboxTitleId(provider, store, game)}|{HashSecret(provider.Credential)}|{provider.AccountId}"
            : descriptor.Id.Equals(
                "epic-games",
                StringComparison.OrdinalIgnoreCase)
                ? $"{game?.StoreNamespace}|{game?.StoreTitleId}|{game?.Title}|{GetEpicAccountId()}"
            : descriptor.Id.Equals(
                "retroachievements",
                StringComparison.OrdinalIgnoreCase)
                ? $"rahash-v1|{game?.Id}|{ResolvedProviderGameId(provider, game)}|{provider.AccountName}|{HashSecret(provider.Credential)}|{RomContentFingerprint(game?.RomPath)}"
                : $"{game?.Id}|{ResolvedProviderGameId(provider, game)}|{provider.AccountId}|{provider.AccountName}|{HashSecret(provider.Credential)}|{HashSecret(provider.SecondaryCredential)}|{provider.DataPath}";
        return $"{descriptor.Id}|{configuration.UnifySteam.GameData.Enabled}|{provider.Enabled}|{identity}";
    }

    public bool CanRefreshUserScoped(
        UnifySteamGameDetailSnapshot gameDetail)
    {
        var configuration = LoadConfiguration();
        if (configuration is null)
        {
            var fallback = OmniLibraryGameDataProviderRegistry.ResolveForStore(
                gameDetail.StoreId,
                gameDetail.Game?.DeliveryProvider);
            return fallback is not null && _sources.ContainsKey(fallback.Id);
        }
        if (!configuration.UnifySteam.GameData.Enabled)
        {
            return false;
        }
        var descriptor = ResolveProviderForGame(configuration, gameDetail);
        return descriptor is not null &&
               _sources.ContainsKey(descriptor.Id) &&
               configuration.UnifySteam.GameData.Providers.TryGetValue(
                   descriptor.Id,
                   out var provider) &&
               provider?.Enabled == true;
    }

    public async Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        UnifySteamGameDetailSnapshot gameDetail,
        OmniLibraryAchievementMetadata previous,
        string previousProviderState,
        bool refreshDefinitions,
        bool refreshProgress,
        CancellationToken cancellationToken)
    {
        var configuration = LoadConfiguration();
        if (configuration is null ||
            !configuration.UnifySteam.Stores.TryGetValue(
                gameDetail.StoreId,
                out var store) ||
            store is null)
        {
            return Unavailable(
                ProviderName(gameDetail.StoreId),
                "not-configured",
                "Achievement settings are not available.",
                retryAfterUtc: DateTimeOffset.UtcNow.Add(AuthenticationFailureBackoff));
        }

        var descriptor = ResolveProviderForGame(configuration, gameDetail);
        if (descriptor is null ||
            !configuration.UnifySteam.GameData.Providers.TryGetValue(
                descriptor.Id,
                out var provider) ||
            provider is null)
        {
            return Unavailable(
                ProviderName(gameDetail.StoreId),
                "provider-required",
                "No game-data provider is registered for this title.",
                retryAfterUtc: DateTimeOffset.UtcNow.AddDays(1));
        }

        if (!configuration.UnifySteam.GameData.Enabled || !provider.Enabled)
        {
            return new OmniLibraryAchievementRefreshResult(
                new OmniLibraryAchievementMetadata(
                    descriptor.Title,
                    "disabled",
                    "Achievement progress is disabled for this store.",
                    0,
                    0,
                    []),
                DefinitionsRefreshed: true,
                ProgressRefreshed: true,
                previousProviderState,
                RetryAfterUtc: null,
                Error: string.Empty);
        }

        try
        {
            if (_sources.TryGetValue(descriptor.Id, out var source))
            {
                return await source.RefreshAsync(
                    new OmniLibraryAchievementSourceContext(
                        gameDetail,
                        store,
                        provider,
                        previous,
                        previousProviderState,
                        refreshDefinitions,
                        refreshProgress),
                    cancellationToken).ConfigureAwait(false);
            }

            return Unavailable(
                descriptor.Title,
                "provider-required",
                "This provider is registered, but its user-scoped runtime is not available for this title yet.",
                retryAfterUtc: DateTimeOffset.UtcNow.AddDays(1));
        }
        catch (AchievementProviderException error)
        {
            return new OmniLibraryAchievementRefreshResult(
                Metadata: null,
                DefinitionsRefreshed: false,
                ProgressRefreshed: false,
                previousProviderState,
                error.RetryAfterUtc,
                error.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new OmniLibraryAchievementRefreshResult(
                Metadata: null,
                DefinitionsRefreshed: false,
                ProgressRefreshed: false,
                previousProviderState,
                DateTimeOffset.UtcNow.Add(DefaultFailureBackoff),
                error is TaskCanceledException
                    ? "Achievement sync timed out. The last good progress is kept."
                    : "Achievement sync is temporarily unavailable. The last good progress is kept.");
        }
    }

    private async Task<OmniLibraryAchievementRefreshResult> RefreshXboxAsync(
        UnifySteamGameDetailSnapshot gameDetail,
        UnifySteamStoreConfiguration store,
        CancellationToken cancellationToken)
    {
        var apiKey = store.OpenXblApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Unavailable(
                "OpenXBL",
                "not-configured",
                "Add your personal OpenXBL API key in Xbox settings to show verified achievement progress.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        var accountId = store.OpenXblAccountId?.Trim() ?? string.Empty;
        var accountName = store.OpenXblAccountName?.Trim() ?? string.Empty;
        var titleId = KnownXboxTitleId(gameDetail.Game, store);
        if (!IsNumericXboxTitleId(titleId) &&
            string.IsNullOrWhiteSpace(accountId))
        {
            using var accountDocument = await SendOpenXblAsync(
                "/api/v2/account",
                apiKey,
                cancellationToken).ConfigureAwait(false);
            (accountId, accountName) = ParseOpenXblAccount(accountDocument.RootElement);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new AchievementProviderException(
                    "OpenXBL returned no Xbox account identity. Reconnect the API key.",
                    DateTimeOffset.UtcNow.Add(AuthenticationFailureBackoff));
            }
            PersistOpenXblAccount(apiKey, accountId, accountName);
        }

        if (!IsNumericXboxTitleId(titleId))
        {
            titleId = await ResolveXboxTitleIdAsync(
                gameDetail.Game,
                store,
                apiKey,
                accountId,
                cancellationToken).ConfigureAwait(false);
        }
        if (!IsNumericXboxTitleId(titleId))
        {
            return Unavailable(
                "OpenXBL",
                "mapping-unavailable",
                "Xbox did not publish a Title ID in the Store catalog or the connected player's title history. Play the title once on this Xbox account, then retry.",
                DateTimeOffset.UtcNow.AddDays(1));
        }

        // OpenXBL already scopes an API key to its Xbox account. Prefer the
        // account-scoped endpoint when the Microsoft catalog gives us a Title
        // ID, so a harmless /account response-shape change cannot hide every
        // achievement. Account lookup remains available only for the older
        // title-history fallback used when the catalog has no Title ID.
        var path =
            $"/api/v2/achievements/title/{Uri.EscapeDataString(titleId)}";
        using var document = await SendOpenXblAsync(
            path,
            apiKey,
            cancellationToken).ConfigureAwait(false);
        var achievements = ParseXboxAchievements(document.RootElement);
        var metadata = achievements.Count == 0
            ? new OmniLibraryAchievementMetadata(
                "OpenXBL",
                "no-achievements",
                "Xbox returned no achievements for this title.",
                0,
                0,
                [])
            : new OmniLibraryAchievementMetadata(
                "OpenXBL",
                "ready",
                string.IsNullOrWhiteSpace(accountName)
                    ? "Verified Xbox achievement progress."
                    : $"Verified Xbox achievement progress for {accountName}.",
                achievements.Count(item => item.Unlocked),
                achievements.Count,
                achievements);
        return new OmniLibraryAchievementRefreshResult(
            metadata,
            DefinitionsRefreshed: true,
            ProgressRefreshed: true,
            ProviderState: JsonSerializer.Serialize(new
            {
                titleId,
                accountId,
            }),
            RetryAfterUtc: null,
            Error: string.Empty);
    }

    private async Task<OmniLibraryAchievementRefreshResult> RefreshEpicAsync(
        UnifySteamGameDetailSnapshot gameDetail,
        OmniLibraryGameDataProviderConfiguration provider,
        OmniLibraryAchievementMetadata previous,
        string previousProviderState,
        bool refreshDefinitions,
        bool refreshProgress,
        CancellationToken cancellationToken)
    {
        if (IsEpicTrialLikeTitle(gameDetail.Game?.Title))
        {
            return new OmniLibraryAchievementRefreshResult(
                new OmniLibraryAchievementMetadata(
                    "Epic Games",
                    "no-achievements",
                    "Epic exposes achievements for the shared full-game sandbox, not a verified achievement set for this demo or trial.",
                    0,
                    0,
                    []),
                DefinitionsRefreshed: true,
                ProgressRefreshed: true,
                ProviderState: JsonSerializer.Serialize(new EpicProviderState(
                    gameDetail.Game?.StoreNamespace?.Trim() ?? string.Empty,
                    string.Empty)),
                RetryAfterUtc: null,
                Error: string.Empty);
        }

        var session = await LoadEpicSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Unavailable(
                "Epic Games",
                "not-connected",
                "Connect Epic Games in OmniLibrary to show verified achievement progress.",
                DateTimeOffset.UtcNow.Add(AuthenticationFailureBackoff));
        }

        var sandboxId = await ResolveEpicSandboxIdAsync(
            gameDetail.Game,
            provider,
            session,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sandboxId))
        {
            return Unavailable(
                "Epic Games",
                "mapping-unavailable",
                "Epic did not expose an exact library asset mapping for this title. No title-name guess is used.",
                DateTimeOffset.UtcNow.AddDays(7));
        }

        var state = ParseEpicProviderState(previousProviderState);
        var definitions = previous.Items.ToList();
        var productId = state.ProductId;
        var definitionsUpdated = false;
        var progressUpdated = false;

        if (refreshDefinitions ||
            definitions.Count == 0 ||
            string.IsNullOrWhiteSpace(productId) ||
            !state.SandboxId.Equals(sandboxId, StringComparison.OrdinalIgnoreCase))
        {
            var definitionDocument = await SendEpicGraphQlAsync(
                EpicAchievementDefinitionQuery,
                new
                {
                    SandboxId = sandboxId,
                    Locale = "en",
                },
                accessToken: null,
                cancellationToken).ConfigureAwait(false);
            (productId, definitions) = ParseEpicDefinitions(definitionDocument.RootElement);
            definitionsUpdated = true;
        }

        if (string.IsNullOrWhiteSpace(productId) || definitions.Count == 0)
        {
            return new OmniLibraryAchievementRefreshResult(
                new OmniLibraryAchievementMetadata(
                    "Epic Games",
                    "no-achievements",
                    "Epic returned no achievement set for this title.",
                    0,
                    0,
                    []),
                DefinitionsRefreshed: true,
                ProgressRefreshed: true,
                ProviderState: JsonSerializer.Serialize(new EpicProviderState(
                    sandboxId,
                    productId)),
                RetryAfterUtc: null,
                Error: string.Empty);
        }

        if (refreshProgress || definitionsUpdated)
        {
            var progressDocument = await SendEpicGraphQlAsync(
                EpicAchievementProgressQuery,
                new
                {
                    EpicAccountId = session.AccountId,
                    ProductId = productId,
                },
                session.AccessToken,
                cancellationToken).ConfigureAwait(false);
            definitions = MergeEpicProgress(
                definitions,
                progressDocument.RootElement);
            progressUpdated = true;
        }

        var unlocked = definitions.Count(item => item.Unlocked);
        return new OmniLibraryAchievementRefreshResult(
            new OmniLibraryAchievementMetadata(
                "Epic Games",
                "ready",
                $"Verified Epic achievement progress for {session.DisplayName}.",
                unlocked,
                definitions.Count,
                definitions),
            DefinitionsRefreshed: definitionsUpdated,
            ProgressRefreshed: progressUpdated,
            ProviderState: JsonSerializer.Serialize(new EpicProviderState(
                sandboxId,
                productId)),
            RetryAfterUtc: null,
            Error: string.Empty);
    }

    private async Task<string> ResolveEpicSandboxIdAsync(
        UnifySteamGameState? game,
        OmniLibraryGameDataProviderConfiguration provider,
        EpicSession session,
        CancellationToken cancellationToken)
    {
        if (game is null)
        {
            return string.Empty;
        }
        var gameId = game.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(gameId) &&
            provider.GameIdOverrides.TryGetValue(gameId, out var mappedSandbox) &&
            !string.IsNullOrWhiteSpace(mappedSandbox))
        {
            return mappedSandbox.Trim();
        }

        var candidates = new[]
        {
            gameId,
            game.StoreNamespace?.Trim() ?? string.Empty,
            game.StoreTitleId?.Trim() ?? string.Empty,
        }.Where(value => !string.IsNullOrWhiteSpace(value))
         .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogSandbox = game.StoreNamespace?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(catalogSandbox))
        {
            if (!string.IsNullOrWhiteSpace(gameId))
            {
                PersistProviderGameId("epic-games", gameId, catalogSandbox);
            }
            return catalogSandbox;
        }
        var assets = await GetEpicLibraryAssetsAsync(
            session.AccessToken,
            cancellationToken).ConfigureAwait(false);
        var matches = assets
            .Where(asset =>
                candidates.Contains(asset.AppName) ||
                candidates.Contains(asset.Namespace))
            .Select(asset => asset.Namespace)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sandboxId = matches.Length == 1
            ? matches[0]
            : game.StoreNamespace?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(gameId) &&
            !string.IsNullOrWhiteSpace(sandboxId))
        {
            PersistProviderGameId("epic-games", gameId, sandboxId);
        }
        return sandboxId;
    }

    private async Task<IReadOnlyList<EpicLibraryAsset>> GetEpicLibraryAssetsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var fingerprint = HashSecret(accessToken);
        if (_epicAssets.Count > 0 &&
            _epicAssetTokenFingerprint.Equals(
                fingerprint,
                StringComparison.Ordinal))
        {
            return _epicAssets;
        }

        await _epicAssetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_epicAssets.Count > 0 &&
                _epicAssetTokenFingerprint.Equals(
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return _epicAssets;
            }

            var result = new List<EpicLibraryAsset>();
            var nextUrl = EpicLibraryAssetsUrl;
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    accessToken);
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    EpicLauncherUserAgent);
                using var response = await _httpClient.SendAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden)
                {
                    throw new AchievementProviderException(
                        "The Epic session expired. Reconnect Epic in OmniLibrary.",
                        DateTimeOffset.UtcNow.Add(AuthenticationFailureBackoff));
                }
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("records", out var records) &&
                    records.ValueKind == JsonValueKind.Array)
                {
                    foreach (var record in records.EnumerateArray())
                    {
                        var asset = new EpicLibraryAsset(
                            FirstJsonString(record, "namespace"),
                            FirstJsonString(record, "appName"));
                        if (!string.IsNullOrWhiteSpace(asset.Namespace))
                        {
                            result.Add(asset);
                        }
                    }
                }
                var cursor = document.RootElement.TryGetProperty(
                                 "responseMetadata",
                                 out var metadata)
                    ? FirstJsonString(metadata, "nextCursor")
                    : string.Empty;
                nextUrl = string.IsNullOrWhiteSpace(cursor)
                    ? string.Empty
                    : EpicLibraryAssetsUrl + "&cursor=" +
                      Uri.EscapeDataString(cursor);
            }
            _epicAssets = result;
            _epicAssetTokenFingerprint = fingerprint;
            return _epicAssets;
        }
        finally
        {
            _epicAssetGate.Release();
        }
    }

    private void PersistProviderGameId(
        string providerId,
        string gameId,
        string providerGameId)
    {
        if (_settingsStore is null ||
            string.IsNullOrWhiteSpace(gameId) ||
            string.IsNullOrWhiteSpace(providerGameId))
        {
            return;
        }
        _settingsStore.Update(configuration =>
        {
            if (!configuration.UnifySteam.GameData.Providers.TryGetValue(
                    providerId,
                    out var provider) || provider is null)
            {
                return;
            }
            provider.GameIdOverrides[gameId.Trim()] = providerGameId.Trim();
            provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
        });
    }

    private static bool IsEpicTrialLikeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalized = " " + string.Join(
            ' ',
            new string(
                title
                    .ToLowerInvariant()
                    .Select(character =>
                        char.IsLetterOrDigit(character) ? character : ' ')
                    .ToArray())
                .Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)) + " ";
        return normalized.Contains(" demo ", StringComparison.Ordinal) ||
               normalized.Contains(" trial ", StringComparison.Ordinal) ||
               normalized.Contains(" playtest ", StringComparison.Ordinal) ||
               normalized.Contains(" test server ", StringComparison.Ordinal) ||
               normalized.Contains(" public test ", StringComparison.Ordinal) ||
               normalized.Contains(" open beta ", StringComparison.Ordinal) ||
               normalized.Contains(" closed beta ", StringComparison.Ordinal);
    }

    private async Task<JsonDocument> SendOpenXblAsync(
        string path,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://xbl.io" + path);
        request.Headers.TryAddWithoutValidation("X-Authorization", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound &&
            (path.StartsWith("/api/v2/achievements/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/v2/player/titleHistory", StringComparison.OrdinalIgnoreCase)))
        {
            return JsonDocument.Parse("""{"achievements":[],"titles":[]}""");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw BuildProviderHttpError("OpenXBL", response);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendEpicGraphQlAsync(
        string query,
        object variables,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query,
            variables,
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            EpicGraphQlUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", EpicLauncherUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw BuildProviderHttpError("Epic Games", response);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            document.Dispose();
            throw new AchievementProviderException(
                string.IsNullOrWhiteSpace(accessToken)
                    ? "Epic achievement definitions are temporarily unavailable."
                    : "Epic rejected the achievement session. Reconnect Epic Games and retry.",
                DateTimeOffset.UtcNow.Add(
                    string.IsNullOrWhiteSpace(accessToken)
                        ? DefaultFailureBackoff
                        : AuthenticationFailureBackoff));
        }
        return document;
    }

    private static AchievementProviderException BuildProviderHttpError(
        string provider,
        HttpResponseMessage response)
    {
        var now = DateTimeOffset.UtcNow;
        var retryAfter = response.Headers.RetryAfter?.Date ??
                         (response.Headers.RetryAfter?.Delta is { } delta
                             ? now.Add(delta)
                             : now.Add(response.StatusCode == HttpStatusCode.TooManyRequests
                                 ? TimeSpan.FromHours(1)
                                 : DefaultFailureBackoff));
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"{provider} rejected the saved credentials. Reconnect the provider and retry.",
            HttpStatusCode.TooManyRequests =>
                $"{provider} rate-limited achievement sync. The last good progress is kept until retry.",
            HttpStatusCode.NotFound =>
                $"{provider} has no achievement data for this title.",
            _ when (int)response.StatusCode >= 500 =>
                $"{provider} is temporarily unavailable. The last good progress is kept.",
            _ =>
                $"{provider} achievement sync failed (HTTP {(int)response.StatusCode}). The last good progress is kept.",
        };
        return new AchievementProviderException(message, retryAfter);
    }

    private static (string AccountId, string AccountName) ParseOpenXblAccount(
        JsonElement root)
    {
        var profile = root;
        if (TryGetProperty(root, "profileUsers", out var users) &&
            users.ValueKind == JsonValueKind.Array)
        {
            profile = users.EnumerateArray().FirstOrDefault();
        }
        var accountId = FirstJsonString(profile, "id", "xuid", "accountId");
        var accountName = FirstJsonString(
            profile,
            "gamertag",
            "displayName",
            "name");
        if (TryGetProperty(profile, "settings", out var settings) &&
            settings.ValueKind == JsonValueKind.Array)
        {
            foreach (var setting in settings.EnumerateArray())
            {
                if (FirstJsonString(setting, "id", "name")
                        .Equals("Gamertag", StringComparison.OrdinalIgnoreCase))
                {
                    accountName = FirstJsonString(setting, "value");
                    break;
                }
            }
        }
        return (accountId, accountName);
    }

    private async Task<string> ResolveXboxTitleIdAsync(
        UnifySteamGameState? game,
        UnifySteamStoreConfiguration store,
        string apiKey,
        string accountId,
        CancellationToken cancellationToken)
    {
        var knownTitleId = KnownXboxTitleId(game, store);
        if (IsNumericXboxTitleId(knownTitleId))
        {
            return knownTitleId;
        }

        var productId = game?.Id?.Trim() ?? string.Empty;
        if (TryResolveXboxTitleIdFromInstall(game, out var localTitleId))
        {
            if (!string.IsNullOrWhiteSpace(productId))
            {
                PersistOpenXblTitleId(apiKey, productId, localTitleId);
            }
            return localTitleId;
        }

        var candidates = new List<XboxTitleCandidate>();
        using (var history = await SendOpenXblAsync(
                   $"/api/v2/player/titleHistory/{Uri.EscapeDataString(accountId)}",
                   apiKey,
                   cancellationToken).ConfigureAwait(false))
        {
            candidates.AddRange(ParseXboxTitleCandidates(history.RootElement));
        }

        var match = MatchXboxTitleCandidate(game, candidates);
        if (match is null)
        {
            // Older OpenXBL/Xbox profiles occasionally omit a title-history
            // row while still returning it from the account achievement list.
            // Spend this second request only after the primary exact match
            // failed; never scan broad Game Pass or marketplace catalogs.
            using var achievements = await SendOpenXblAsync(
                $"/api/v2/achievements/player/{Uri.EscapeDataString(accountId)}",
                apiKey,
                cancellationToken).ConfigureAwait(false);
            candidates.AddRange(ParseXboxTitleCandidates(
                achievements.RootElement));
            match = MatchXboxTitleCandidate(game, candidates);
        }

        if (match is null || !IsNumericXboxTitleId(match.TitleId))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(productId))
        {
            PersistOpenXblTitleId(apiKey, productId, match.TitleId);
        }
        return match.TitleId;
    }

    private static string ResolvedStoredXboxTitleId(
        OmniLibraryGameDataProviderConfiguration provider,
        UnifySteamStoreConfiguration store,
        UnifySteamGameState? game)
    {
        var productId = game?.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(productId) &&
            provider.GameIdOverrides.TryGetValue(productId, out var providerTitleId) &&
            IsNumericXboxTitleId(providerTitleId))
        {
            return providerTitleId.Trim();
        }

        return ResolvedStoredXboxTitleId(store, game);
    }

    private static string ResolvedProviderGameId(
        OmniLibraryGameDataProviderConfiguration provider,
        UnifySteamGameState? game)
    {
        var gameId = game?.Id?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(gameId) &&
               provider.GameIdOverrides.TryGetValue(gameId, out var mappedId)
            ? mappedId?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string RomContentFingerprint(string? romPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(romPath))
            {
                return string.Empty;
            }
            var file = new FileInfo(romPath);
            return file.Exists
                ? $"{file.FullName}|{file.Length:x16}|{file.LastWriteTimeUtc.Ticks:x16}"
                : file.FullName;
        }
        catch
        {
            return romPath?.Trim() ?? string.Empty;
        }
    }

    private static OmniLibraryGameDataProviderDescriptor? ResolveProviderForGame(
        StoreSyncConfiguration configuration,
        UnifySteamGameDetailSnapshot gameDetail)
    {
        var primary = OmniLibraryGameDataProviderRegistry.ResolveForStore(
            gameDetail.StoreId,
            gameDetail.Game?.DeliveryProvider);
        var gameId = gameDetail.Game?.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            var mappedProviders = OmniLibraryGameDataProviderRegistry.All
                .Where(descriptor =>
                    !descriptor.Id.Equals(
                        primary?.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    configuration.UnifySteam.GameData.Providers.TryGetValue(
                        descriptor.Id,
                        out var provider) &&
                    provider?.Enabled == true &&
                    provider.GameIdOverrides.ContainsKey(gameId))
                .ToArray();
            if (mappedProviders.Length == 1)
            {
                return mappedProviders[0];
            }
        }

        return primary;
    }

    private static string ResolvedStoredXboxTitleId(
        UnifySteamStoreConfiguration store,
        UnifySteamGameState? game)
    {
        var productId = game?.Id?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(productId) &&
               store.OpenXblTitleIds.TryGetValue(productId, out var titleId)
            ? titleId?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string KnownXboxTitleId(
        UnifySteamGameState? game,
        UnifySteamStoreConfiguration store)
    {
        var catalogTitleId = game?.StoreTitleId?.Trim() ?? string.Empty;
        return IsNumericXboxTitleId(catalogTitleId)
            ? catalogTitleId
            : ResolvedStoredXboxTitleId(store, game);
    }

    private static bool IsNumericXboxTitleId(string? value) =>
        ulong.TryParse(
            value?.Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _);

    private static bool TryResolveXboxTitleIdFromInstall(
        UnifySteamGameState? game,
        out string titleId)
    {
        titleId = string.Empty;
        var installPath = game?.InstallPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }

        foreach (var configPath in new[]
                 {
                     Path.Combine(installPath, "MicrosoftGame.config"),
                     Path.Combine(installPath, "Content", "MicrosoftGame.config"),
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(configPath))
            {
                continue;
            }
            try
            {
                var value = XDocument.Load(configPath)
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName.Equals(
                        "TitleId",
                        StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                if (TryNormalizeXboxTitleId(value, out titleId))
                {
                    return true;
                }
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // A malformed or temporarily locked config must never block
                // the history-based resolver or invalidate cached data.
            }
        }
        return false;
    }

    private static bool TryNormalizeXboxTitleId(
        string? value,
        out string titleId)
    {
        titleId = string.Empty;
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 8 &&
            trimmed.All(Uri.IsHexDigit) &&
            uint.TryParse(
                trimmed,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var hexadecimal))
        {
            titleId = hexadecimal.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (trimmed.All(char.IsDigit) && trimmed.Length > 0)
        {
            titleId = trimmed;
            return true;
        }
        return false;
    }

    private static IReadOnlyList<XboxTitleCandidate> ParseXboxTitleCandidates(
        JsonElement root)
    {
        var objects = new List<JsonElement>();
        CollectJsonObjects(root, objects);
        var candidates = new Dictionary<string, XboxTitleCandidate>(
            StringComparer.Ordinal);
        foreach (var node in objects)
        {
            var titleId = FirstJsonString(
                node,
                "titleId",
                "titleID",
                "xboxTitleId",
                "XboxTitleId",
                "xbox_title_id",
                "title_id",
                "titleid");
            var title = FirstJsonString(
                node,
                "titleName",
                "name",
                "title",
                "localizedTitleName",
                "localizedTitle",
                "productTitle",
                "productName",
                "displayTitle",
                "displayName");
            if (!IsNumericXboxTitleId(titleId) &&
                !string.IsNullOrWhiteSpace(title))
            {
                var genericId = FirstJsonString(node, "id");
                if (IsNumericXboxTitleId(genericId))
                {
                    titleId = genericId;
                }
            }
            if (!IsNumericXboxTitleId(titleId))
            {
                continue;
            }

            if (!candidates.TryGetValue(titleId, out var candidate))
            {
                candidate = new XboxTitleCandidate(
                    titleId,
                    new HashSet<string>(StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                candidates[titleId] = candidate;
            }
            AddXboxTitleName(candidate.Names, title);
            foreach (var field in new[]
                     {
                         "titleName",
                         "name",
                         "title",
                         "localizedTitleName",
                         "localizedTitle",
                         "productTitle",
                         "productName",
                         "displayTitle",
                         "displayName",
                         "shortTitle",
                         "sortTitle",
                     })
            {
                AddXboxTitleName(
                    candidate.Names,
                    FirstJsonString(node, field));
            }
            foreach (var field in new[]
                     {
                         "productId",
                         "ProductId",
                         "storeId",
                         "StoreId",
                         "msStoreProductId",
                         "bigId",
                         "BigId",
                         "packageFamilyName",
                         "PackageFamilyName",
                         "pfn",
                         "PFN",
                         "aumid",
                         "AUMID",
                         "scid",
                         "SCID",
                         "serviceConfigId",
                         "serviceConfigurationId",
                         "contentId",
                         "ContentId",
                     })
            {
                var value = FirstJsonString(node, field).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    candidate.RawIds.Add(value);
                }
            }
        }
        return candidates.Values.ToList();
    }

    private static void CollectJsonObjects(
        JsonElement node,
        ICollection<JsonElement> output)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            output.Add(node);
            foreach (var property in node.EnumerateObject())
            {
                CollectJsonObjects(property.Value, output);
            }
            return;
        }
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
            {
                CollectJsonObjects(child, output);
            }
        }
    }

    private static void AddXboxTitleName(
        ISet<string> names,
        string? value)
    {
        var normalized = NormalizeXboxTitle(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            names.Add(normalized);
        }
    }

    private static XboxTitleCandidate? MatchXboxTitleCandidate(
        UnifySteamGameState? game,
        IEnumerable<XboxTitleCandidate> candidates)
    {
        if (game is null)
        {
            return null;
        }
        var distinct = candidates
            .GroupBy(candidate => candidate.TitleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var productId = game.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(productId))
        {
            var productMatches = distinct
                .Where(candidate => candidate.RawIds.Contains(productId))
                .ToList();
            if (productMatches.Count == 1)
            {
                return productMatches[0];
            }
        }

        var title = NormalizeXboxTitle(game.Title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        var titleMatches = distinct
            .Where(candidate => candidate.Names.Contains(title))
            .ToList();
        return titleMatches.Count == 1
            ? titleMatches[0]
            : null;
    }

    private static string NormalizeXboxTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) ==
                UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            builder.Append(char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : ' ');
        }
        var normalized = string.Join(
            ' ',
            builder.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var suffix in new[]
                 {
                     " standard edition",
                     " deluxe edition",
                     " ultimate edition",
                     " complete edition",
                     " game of the year edition",
                     " windows edition",
                     " for windows",
                     " pc edition",
                 })
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^suffix.Length].TrimEnd();
                break;
            }
        }
        return normalized;
    }

    private static List<OmniLibraryAchievementItemMetadata> ParseXboxAchievements(
        JsonElement root)
    {
        if (!TryFindNamedArray(root, "achievements", out var achievements))
        {
            return [];
        }

        var result = new List<OmniLibraryAchievementItemMetadata>();
        foreach (var item in achievements.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var id = FirstJsonString(item, "id", "achievementId", "name");
            var name = FirstJsonString(item, "name", "displayName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var state = FirstJsonString(item, "progressState", "state");
            var unlocked = state.Equals("Achieved", StringComparison.OrdinalIgnoreCase) ||
                           state.Equals("Unlocked", StringComparison.OrdinalIgnoreCase);
            var hidden = GetJsonBoolean(item, "isSecret") ||
                         GetJsonBoolean(item, "hidden");
            var description = unlocked
                ? FirstJsonString(item, "description", "unlockedDescription")
                : FirstJsonString(item, "lockedDescription", "description");
            var iconUrl = string.Empty;
            if (TryGetProperty(item, "mediaAssets", out var mediaAssets) &&
                mediaAssets.ValueKind == JsonValueKind.Array)
            {
                var icon = mediaAssets.EnumerateArray().FirstOrDefault(asset =>
                    FirstJsonString(asset, "type", "name")
                        .Contains("icon", StringComparison.OrdinalIgnoreCase));
                if (icon.ValueKind == JsonValueKind.Undefined)
                {
                    icon = mediaAssets.EnumerateArray().FirstOrDefault();
                }
                iconUrl = NormalizeHttpsUrl(FirstJsonString(icon, "url", "uri"));
            }

            DateTimeOffset? unlockedAt = null;
            var current = unlocked ? 1 : 0;
            var target = 1;
            if (TryGetProperty(item, "progression", out var progression))
            {
                unlockedAt = ParseDateTime(FirstJsonString(
                    progression,
                    "timeUnlocked",
                    "unlockDate"));
                if (TryGetProperty(progression, "requirements", out var requirements) &&
                    requirements.ValueKind == JsonValueKind.Array)
                {
                    var requirement = requirements.EnumerateArray().FirstOrDefault();
                    current = Math.Max(
                        0,
                        ParseInt(
                            FirstJsonString(requirement, "current", "currentProgress"),
                            current));
                    target = Math.Max(
                        1,
                        ParseInt(
                            FirstJsonString(requirement, "target", "targetProgress"),
                            target));
                }
            }

            result.Add(new OmniLibraryAchievementItemMetadata(
                string.IsNullOrWhiteSpace(id) ? $"xbox-{result.Count}" : id,
                name,
                description,
                unlocked,
                hidden,
                unlockedAt,
                iconUrl,
                current,
                target));
        }
        return result
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static (string ProductId, List<OmniLibraryAchievementItemMetadata> Items)
        ParseEpicDefinitions(JsonElement root)
    {
        if (!TryNavigate(
                root,
                out var record,
                "data",
                "Achievement",
                "productAchievementsRecordBySandbox") ||
            record.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (string.Empty, []);
        }
        var productId = FirstJsonString(record, "productId");
        if (!TryGetProperty(record, "achievements", out var achievements) ||
            achievements.ValueKind != JsonValueKind.Array)
        {
            return (productId, []);
        }

        var items = new List<OmniLibraryAchievementItemMetadata>();
        foreach (var wrapper in achievements.EnumerateArray())
        {
            var item = TryGetProperty(wrapper, "achievement", out var nested)
                ? nested
                : wrapper;
            var id = FirstJsonString(item, "name", "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            var hidden = GetJsonBoolean(item, "hidden");
            var name = FirstJsonString(
                item,
                "unlockedDisplayName",
                "lockedDisplayName",
                "name");
            var description = FirstJsonString(
                item,
                "unlockedDescription",
                "lockedDescription",
                "flavorText");
            items.Add(new OmniLibraryAchievementItemMetadata(
                id,
                name,
                description,
                Unlocked: false,
                hidden,
                UnlockedAtUtc: null,
                NormalizeHttpsUrl(FirstJsonString(
                    item,
                    "unlockedIconLink",
                    "lockedIconLink")),
                CurrentProgress: 0,
                TargetProgress: 1));
        }
        return (productId, items);
    }

    private static List<OmniLibraryAchievementItemMetadata> MergeEpicProgress(
        IReadOnlyList<OmniLibraryAchievementItemMetadata> definitions,
        JsonElement root)
    {
        var progressById =
            new Dictionary<string, (bool Unlocked, double Progress, DateTimeOffset? Date)>(
                StringComparer.OrdinalIgnoreCase);
        if (TryNavigate(
                root,
                out var productAchievements,
                "data",
                "PlayerProfile",
                "playerProfile",
                "productAchievements",
                "data") &&
            TryGetProperty(
                productAchievements,
                "playerAchievements",
                out var playerAchievements) &&
            playerAchievements.ValueKind == JsonValueKind.Array)
        {
            foreach (var wrapper in playerAchievements.EnumerateArray())
            {
                var item = TryGetProperty(wrapper, "playerAchievement", out var nested)
                    ? nested
                    : wrapper;
                var id = FirstJsonString(item, "achievementName", "name");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                progressById[id] = (
                    GetJsonBoolean(item, "unlocked"),
                    GetJsonDouble(item, "progress"),
                    ParseDateTime(FirstJsonString(item, "unlockDate")));
            }
        }

        return definitions.Select(definition =>
        {
            if (!progressById.TryGetValue(definition.Id, out var progress))
            {
                return definition with
                {
                    Unlocked = false,
                    UnlockedAtUtc = null,
                    CurrentProgress = 0,
                };
            }
            return definition with
            {
                Unlocked = progress.Unlocked,
                UnlockedAtUtc = progress.Unlocked ? progress.Date : null,
                CurrentProgress = progress.Unlocked
                    ? 1
                    : Math.Clamp(
                        (int)Math.Round(
                            progress.Progress <= 1
                                ? progress.Progress * 100
                                : progress.Progress),
                        0,
                        100),
                TargetProgress = progress.Unlocked || progress.Progress <= 0
                    ? 1
                    : 100,
            };
        }).ToList();
    }

    private async Task<EpicSession?> LoadEpicSessionAsync(
        CancellationToken cancellationToken)
    {
        var path = _epicCredentialPath;

        await ManagedLegendaryHelper.CredentialGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var root = JsonNode.Parse(await File.ReadAllTextAsync(
                path,
                cancellationToken).ConfigureAwait(false)) as JsonObject;
            if (root is null)
            {
                return null;
            }
            var credentials = ResolveEpicCredentialObject(root);
            var session = ParseEpicSession(credentials);
            if (session is null)
            {
                return null;
            }
            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return session;
            }
            if (string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                throw new AchievementProviderException(
                    "The Epic session expired. Reconnect Epic Games.",
                    DateTimeOffset.UtcNow.Add(AuthenticationFailureBackoff));
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = session.RefreshToken,
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{EpicClientId}:{EpicClientSecret}")));
            request.Headers.TryAddWithoutValidation("User-Agent", EpicLauncherUserAgent);
            using var response = await _httpClient.SendAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw BuildProviderHttpError("Epic Games", response);
            }
            var refreshed = JsonNode.Parse(await response.Content.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false)) as JsonObject;
            if (refreshed is null)
            {
                throw new AchievementProviderException(
                    "Epic returned an invalid refreshed session. Reconnect Epic Games.",
                    DateTimeOffset.UtcNow.Add(AuthenticationFailureBackoff));
            }
            foreach (var pair in refreshed)
            {
                credentials[pair.Key] = pair.Value?.DeepClone();
            }
            credentials["loginTime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await SaveJsonAtomicallyAsync(path, root, cancellationToken)
                .ConfigureAwait(false);
            return ParseEpicSession(credentials);
        }
        catch (JsonException)
        {
            throw new AchievementProviderException(
                "Epic sign-in data is damaged. Reconnect Epic Games.",
                DateTimeOffset.UtcNow.Add(AuthenticationFailureBackoff));
        }
        catch (IOException error)
        {
            throw new AchievementProviderException(
                $"Epic sign-in data could not be read: {error.Message}",
                DateTimeOffset.UtcNow.Add(DefaultFailureBackoff));
        }
        finally
        {
            ManagedLegendaryHelper.CredentialGate.Release();
        }
    }

    private static JsonObject ResolveEpicCredentialObject(JsonObject root)
    {
        if (root[EpicClientId] is JsonObject nested)
        {
            return nested;
        }
        return root;
    }

    private static EpicSession? ParseEpicSession(JsonObject credentials)
    {
        var accessToken = NodeString(credentials, "access_token");
        var accountId = FirstNonEmpty(
            NodeString(credentials, "account_id"),
            NodeString(credentials, "accountId"));
        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(accountId))
        {
            return null;
        }
        var expiresAt = ParseDateTime(NodeString(credentials, "expires_at"));
        if (!expiresAt.HasValue)
        {
            var loginTime = NodeDouble(credentials, "loginTime");
            var expiresIn = Math.Max(1, NodeDouble(credentials, "expires_in", 7200));
            expiresAt = loginTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(loginTime))
                    .AddSeconds(expiresIn)
                : DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        }
        return new EpicSession(
            accessToken,
            NodeString(credentials, "refresh_token"),
            accountId,
            FirstNonEmpty(
                NodeString(credentials, "displayName"),
                NodeString(credentials, "display_name"),
                accountId),
            expiresAt.Value);
    }

    private string GetEpicAccountId()
    {
        var revision = GetFileRevision(_epicCredentialPath);
        lock (_epicAccountGate)
        {
            if (revision == _epicCredentialRevision)
            {
                return _epicAccountId;
            }
        }

        var accountId = ReadEpicAccountId(_epicCredentialPath);
        lock (_epicAccountGate)
        {
            _epicCredentialRevision = revision;
            _epicAccountId = accountId;
            return _epicAccountId;
        }
    }

    private static string ReadEpicAccountId(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return root is null
                ? string.Empty
                : ParseEpicSession(ResolveEpicCredentialObject(root))?.AccountId ??
                  string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long GetFileRevision(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists
                ? HashCode.Combine(file.LastWriteTimeUtc.Ticks, file.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task SaveJsonAtomicallyAsync(
        string path,
        JsonObject root,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Epic auth path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                }),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, path + ".bak", true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    private void PersistOpenXblAccount(
        string apiKey,
        string accountId,
        string accountName)
    {
        if (_settingsStore is null)
        {
            return;
        }
        _settingsStore.Update(configuration =>
        {
            if (!configuration.UnifySteam.Stores.TryGetValue(
                    "xbox-game-pass",
                    out var xbox) ||
                xbox is null ||
                !string.Equals(xbox.OpenXblApiKey, apiKey, StringComparison.Ordinal))
            {
                return;
            }
            xbox.OpenXblAccountId = accountId;
            xbox.OpenXblAccountName = accountName;
            xbox.OpenXblAccountCheckedAtUtc = DateTimeOffset.UtcNow;

            if (configuration.UnifySteam.GameData.Providers.TryGetValue(
                    "xbox-live",
                    out var provider) &&
                provider is not null &&
                string.Equals(provider.Credential, apiKey, StringComparison.Ordinal))
            {
                provider.AccountId = accountId;
                provider.AccountName = accountName;
                provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        });
        lock (_configurationGate)
        {
            _configurationRevision = long.MinValue;
            _configuration = null;
        }
    }

    private void PersistOpenXblTitleId(
        string apiKey,
        string productId,
        string titleId)
    {
        if (_settingsStore is null ||
            string.IsNullOrWhiteSpace(productId) ||
            !IsNumericXboxTitleId(titleId))
        {
            return;
        }
        _settingsStore.Update(configuration =>
        {
            if (!configuration.UnifySteam.Stores.TryGetValue(
                    "xbox-game-pass",
                    out var xbox) ||
                xbox is null ||
                !string.Equals(xbox.OpenXblApiKey, apiKey, StringComparison.Ordinal))
            {
                return;
            }
            xbox.OpenXblTitleIds[productId.Trim()] = titleId.Trim();
            if (configuration.UnifySteam.GameData.Providers.TryGetValue(
                    "xbox-live",
                    out var provider) &&
                provider is not null &&
                string.Equals(provider.Credential, apiKey, StringComparison.Ordinal))
            {
                provider.GameIdOverrides[productId.Trim()] = titleId.Trim();
                provider.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        });
        lock (_configurationGate)
        {
            _configurationRevision = long.MinValue;
            _configuration = null;
        }
    }

    private StoreSyncConfiguration? LoadConfiguration()
    {
        if (_settingsStore is null)
        {
            return null;
        }
        var revision = _settingsStore.GetRevision();
        lock (_configurationGate)
        {
            if (_configuration is null || revision != _configurationRevision)
            {
                _configuration = _settingsStore.Load();
                _configurationRevision = revision;
            }
            return _configuration;
        }
    }

    private static void ApplyXboxProviderConfiguration(
        UnifySteamStoreConfiguration store,
        OmniLibraryGameDataProviderConfiguration provider)
    {
        store.AchievementsEnabled = provider.Enabled;
        store.OpenXblApiKey = provider.Credential?.Trim() ?? string.Empty;
        store.OpenXblAccountId = provider.AccountId?.Trim() ?? string.Empty;
        store.OpenXblAccountName = provider.AccountName?.Trim() ?? string.Empty;
        store.OpenXblTitleIds = provider.GameIdOverrides;
    }

    private static OmniLibraryAchievementRefreshResult Unavailable(
        string provider,
        string status,
        string detail,
        DateTimeOffset? retryAfterUtc)
    {
        return new OmniLibraryAchievementRefreshResult(
            new OmniLibraryAchievementMetadata(
                provider,
                status,
                detail,
                0,
                0,
                []),
            DefinitionsRefreshed: true,
            ProgressRefreshed: true,
            ProviderState: string.Empty,
            retryAfterUtc,
            Error: string.Empty);
    }

    private static string ProviderName(string storeId) => storeId switch
    {
        "xbox-game-pass" => "OpenXBL",
        "epic-games" => "Epic Games",
        "gog-galaxy" => "GOG",
        _ => "OmniLibrary",
    };

    private static EpicProviderState ParseEpicProviderState(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<EpicProviderState>(value) ??
                   new EpicProviderState(string.Empty, string.Empty);
        }
        catch
        {
            return new EpicProviderState(string.Empty, string.Empty);
        }
    }

    private static string HashSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value.Trim())))[..12];
    }

    private static bool TryFindNamedArray(
        JsonElement element,
        string propertyName,
        out JsonElement result,
        int depth = 0)
    {
        result = default;
        if (depth > 8)
        {
            return false;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    result = property.Value;
                    return true;
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                if (TryFindNamedArray(
                        property.Value,
                        propertyName,
                        out result,
                        depth + 1))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray().Take(20))
            {
                if (TryFindNamedArray(item, propertyName, out result, depth + 1))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryNavigate(
        JsonElement root,
        out JsonElement result,
        params string[] path)
    {
        result = root;
        foreach (var segment in path)
        {
            if (!TryGetProperty(result, segment, out result))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        return false;
    }

    private static string FirstJsonString(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }
            var result = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                _ => string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }
        return string.Empty;
    }

    private static bool GetJsonBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return false;
        }
        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed) &&
                parsed);
    }

    private static double GetJsonDouble(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) &&
               value.TryGetDouble(out var parsed)
            ? parsed
            : ParseDouble(FirstJsonString(element, name), 0);
    }

    private static double ParseDouble(string value, double fallback)
    {
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static int ParseInt(string value, int fallback)
    {
        if (int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var floating)
            ? (int)Math.Round(floating)
            : fallback;
    }

    private static DateTimeOffset? ParseDateTime(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string NormalizeHttpsUrl(string value)
    {
        var url = value?.Trim() ?? string.Empty;
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? parsed.AbsoluteUri
            : string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ??
        string.Empty;

    private static string NodeString(JsonObject node, string name) =>
        node[name]?.GetValue<string?>()?.Trim() ?? string.Empty;

    private static double NodeDouble(
        JsonObject node,
        string name,
        double fallback = 0)
    {
        try
        {
            return node[name]?.GetValue<double>() ?? fallback;
        }
        catch
        {
            return double.TryParse(
                node[name]?.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback;
        }
    }

    private const string EpicAchievementDefinitionQuery =
        """
        query Achievement($SandboxId: String!, $Locale: String!) {
          Achievement {
            productAchievementsRecordBySandbox(sandboxId: $SandboxId, locale: $Locale) {
              productId
              sandboxId
              achievements {
                achievement {
                  name
                  hidden
                  unlockedDisplayName
                  lockedDisplayName
                  unlockedDescription
                  lockedDescription
                  flavorText
                  unlockedIconLink
                  lockedIconLink
                  XP
                }
              }
            }
          }
        }
        """;

    private const string EpicAchievementProgressQuery =
        """
        query playerProfileAchievementsByProductId($EpicAccountId: String!, $ProductId: String!) {
          PlayerProfile {
            playerProfile(epicAccountId: $EpicAccountId) {
              productAchievements(productId: $ProductId) {
                ... on PlayerProductAchievementsResponseSuccess {
                  data {
                    totalXP
                    totalUnlocked
                    playerAchievements {
                      playerAchievement {
                        achievementName
                        progress
                        unlocked
                        unlockDate
                        XP
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private sealed record EpicSession(
        string AccessToken,
        string RefreshToken,
        string AccountId,
        string DisplayName,
        DateTimeOffset ExpiresAtUtc);

    private sealed record EpicLibraryAsset(
        string Namespace,
        string AppName);

    private sealed record EpicProviderState(
        string SandboxId,
        string ProductId);

    private sealed record XboxTitleCandidate(
        string TitleId,
        HashSet<string> Names,
        HashSet<string> RawIds);
}

internal sealed record OmniLibraryAchievementRefreshResult(
    OmniLibraryAchievementMetadata? Metadata,
    bool DefinitionsRefreshed,
    bool ProgressRefreshed,
    string ProviderState,
    DateTimeOffset? RetryAfterUtc,
    string Error);

internal sealed class AchievementProviderException : Exception
{
    public AchievementProviderException(
        string message,
        DateTimeOffset retryAfterUtc)
        : base(message)
    {
        RetryAfterUtc = retryAfterUtc;
    }

    public DateTimeOffset RetryAfterUtc { get; }
}
