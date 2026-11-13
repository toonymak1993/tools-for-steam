using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLoader.App.Infrastructure.Metadata;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Builds the enhanced Big Picture game page for OmniLibrary shortcuts.
///
/// The cache is deliberately independent from store catalog and artwork state:
/// opening one game never causes a store-wide refresh, and a metadata-source
/// outage can never invalidate a working shortcut or its last good metadata.
/// </summary>
public sealed partial class OmniLibraryGamePageMetadataService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly TimeSpan GameInfoLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan ActivityLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan AchievementDefinitionLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan AchievementProgressLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan SourceMatchLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan UnmatchedSourceMatchLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan UnusedEntryLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan OpenRefreshThrottle = TimeSpan.FromSeconds(5);
    private static readonly Regex HtmlTagPattern = HtmlTagRegex();
    private static readonly Regex BbCodePattern = BbCodeRegex();
    private static readonly Regex ImageUrlPattern = ImageUrlRegex();
    private static readonly Regex NonAlphaNumericPattern = NonAlphaNumericRegex();
    private static readonly Regex EditionSuffixPattern = EditionSuffixRegex();
    private static readonly Regex WindowsSuffixPattern = WindowsSuffixRegex();
    private static readonly Regex NumberPattern = NumberRegex();
    private static readonly Regex AchievementRowPattern = AchievementRowRegex();

    private readonly Func<uint, UnifySteamGameDetailSnapshot> _gameProvider;
    private readonly string _cachePath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OmniLibraryAchievementProvider _achievementProvider;
    private readonly IgnMetadataClient _ign;
    private readonly MetadataTranslator _translator;
    private const string IgnSourceLabel = "IGN";
    // Metadata is requested per visible title. Keep the fan-out bounded, but
    // allow several page refreshes to overlap so background work for a game
    // the user just left cannot delay the next game page.
    private readonly SemaphoreSlim _networkGate = new(4, 4);
    private readonly object _cacheGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _refreshes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _openRefreshes =
        new(StringComparer.OrdinalIgnoreCase);
    private CacheDocument? _cache;
    private long _revision;
    private bool _disposed;

    public OmniLibraryGamePageMetadataService(
        Func<uint, UnifySteamGameDetailSnapshot> gameProvider,
        string cachePath,
        HttpClient? httpClient = null,
        StoreSyncSettingsStore? settingsStore = null)
    {
        _gameProvider = gameProvider ?? throw new ArgumentNullException(nameof(gameProvider));
        _cachePath = cachePath ?? throw new ArgumentNullException(nameof(cachePath));
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        _ownsHttpClient = httpClient is null;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ToolsForSteam-OmniLibrary-Metadata/1.0");
        }
        _ign = new IgnMetadataClient(_httpClient);
        _translator = new MetadataTranslator(_httpClient);
        _achievementProvider = new OmniLibraryAchievementProvider(
            settingsStore,
            _httpClient);
    }

    public async Task<OmniLibraryGamePageMetadataSnapshot?> GetAsync(
        uint shortcutAppId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        return await GetAsync(
            shortcutAppId,
            forceRefresh,
            refreshOnOpen: false,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<OmniLibraryGamePageMetadataSnapshot?> GetAsync(
        uint shortcutAppId,
        bool forceRefresh,
        bool refreshOnOpen,
        CancellationToken cancellationToken) =>
        GetAsync(
            shortcutAppId,
            shortcutTitle: null,
            forceRefresh,
            refreshOnOpen,
            cancellationToken);

    /// <summary>
    /// Game page metadata for any non-Steam shortcut. OmniLibrary games keep
    /// their own store identity and achievement providers; every other
    /// shortcut is identified by its Steam shortcut app ID and the title Steam
    /// shows for it.
    /// </summary>
    public async Task<OmniLibraryGamePageMetadataSnapshot?> GetAsync(
        uint shortcutAppId,
        string? shortcutTitle,
        bool forceRefresh,
        bool refreshOnOpen,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ResolveGameDetail(shortcutAppId, shortcutTitle) is not { } gameDetail)
        {
            return null;
        }

        var identity = BuildIdentity(gameDetail.StoreId, gameDetail.Game!.Id);
        var snapshot = await GetCoreAsync(
                shortcutAppId,
                gameDetail,
                identity,
                forceRefresh,
                refreshOnOpen,
                cancellationToken)
            .ConfigureAwait(false);
        return ApplyUserEdits(snapshot, GetUserEdits(identity));
    }

    private async Task<OmniLibraryGamePageMetadataSnapshot> GetCoreAsync(
        uint shortcutAppId,
        UnifySteamGameDetailSnapshot gameDetail,
        string identity,
        bool forceRefresh,
        bool refreshOnOpen,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = GetEntry(identity);
        var validIdentity = entry is not null &&
            entry.ShortcutAppId == shortcutAppId &&
            entry.StoreId.Equals(gameDetail.StoreId, StringComparison.OrdinalIgnoreCase) &&
            entry.GameId.Equals(gameDetail.Game.Id, StringComparison.OrdinalIgnoreCase) &&
            NormalizeTitle(entry.Title).Equals(
                NormalizeTitle(gameDetail.Game.Title),
                StringComparison.Ordinal);
        if (!validIdentity)
        {
            entry = null;
        }

        if (!forceRefresh && refreshOnOpen)
        {
            var hadCachedEntry = entry is not null;
            entry ??= CreateInitialEntry(identity, gameDetail, now);
            var lastOpenRefresh = _openRefreshes.GetOrAdd(identity, now);
            var refreshAllowed = !hadCachedEntry ||
                                 lastOpenRefresh == now ||
                                 now - lastOpenRefresh >= OpenRefreshThrottle;
            if (refreshAllowed)
            {
                _openRefreshes[identity] = now;
                _ = StartRefreshAsync(
                    identity,
                    gameDetail,
                    entry,
                    forceRefresh: true);
            }
            TouchEntry(identity, now);
            return BuildSnapshot(
                entry,
                refreshing: refreshAllowed || _refreshes.ContainsKey(identity),
                cached: hadCachedEntry);
        }

        var achievementFingerprint = GetAchievementFingerprint(gameDetail);
        var needsRefresh = forceRefresh ||
                           entry is null ||
                           IsRefreshDue(
                               entry,
                               now,
                               achievementFingerprint,
                               CanRefreshUserScopedAchievements(gameDetail));
        if (!needsRefresh)
        {
            TouchEntry(identity, now);
            return BuildSnapshot(entry!, refreshing: false, cached: true);
        }

        if (entry is not null && !forceRefresh)
        {
            _ = StartRefreshAsync(identity, gameDetail, entry, forceRefresh: false);
            TouchEntry(identity, now);
            return BuildSnapshot(entry, refreshing: true, cached: true);
        }

        try
        {
            var refreshed = await StartRefreshAsync(
                identity,
                gameDetail,
                entry,
                forceRefresh).WaitAsync(cancellationToken).ConfigureAwait(false);
            return BuildSnapshot(refreshed, refreshing: false, cached: entry is not null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            if (entry is not null)
            {
                return BuildSnapshot(
                    entry with
                    {
                        LastError = FriendlyError(error),
                        RetryAfterUtc = now.Add(FailureBackoff),
                    },
                    refreshing: false,
                    cached: true);
            }

            return BuildUnavailableSnapshot(gameDetail, FriendlyError(error));
        }
    }

    private CacheEntry CreateInitialEntry(
        string identity,
        UnifySteamGameDetailSnapshot gameDetail,
        DateTimeOffset now)
    {
        var game = gameDetail.Game!;
        var manualSteamAppId = GetSteamMatchOverride(identity);
        var entry = new CacheEntry
        {
            Revision = Interlocked.Increment(ref _revision),
            ShortcutAppId = game.SteamAppId,
            StoreId = gameDetail.StoreId,
            GameId = game.Id,
            Title = game.Title.Trim(),
            SourceSteamAppId = manualSteamAppId,
            MetadataSource = manualSteamAppId.HasValue
                ? "Steam (selected)"
                : string.Empty,
            MatchMode = manualSteamAppId.HasValue ? "manual" : "automatic",
            SourceMatchedAtUtc = manualSteamAppId.HasValue ? now : null,
            AchievementConfigurationFingerprint =
                GetAchievementFingerprint(gameDetail),
            LastAccessedAtUtc = now,
            GameInfo = EmptyGameInfo(game.Title, manualSteamAppId),
            Achievements = StoreAchievementPlaceholder(gameDetail.StoreId),
        };
        entry.ContentHash = ComputeContentHash(entry);
        SaveEntry(identity, entry);
        return entry;
    }

    public Task<IReadOnlyList<OmniLibraryMetadataSearchResult>> SearchSteamMatchesAsync(
        uint shortcutAppId,
        string? query,
        CancellationToken cancellationToken) =>
        SearchSteamMatchesAsync(shortcutAppId, shortcutTitle: null, query, cancellationToken);

    public async Task<IReadOnlyList<OmniLibraryMetadataSearchResult>> SearchSteamMatchesAsync(
        uint shortcutAppId,
        string? shortcutTitle,
        string? query,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ResolveGameDetail(shortcutAppId, shortcutTitle) is not { } gameDetail)
        {
            return [];
        }

        var term = string.IsNullOrWhiteSpace(query)
            ? gameDetail.Game.Title.Trim()
            : query.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        await _networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (language, market) = GetSteamLocale();
            var url =
                $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}" +
                $"&l={Uri.EscapeDataString(language)}&cc={Uri.EscapeDataString(market)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var requested = NormalizeTitle(term);
            return items.EnumerateArray()
                .Take(30)
                .Select(item =>
                {
                    var appId = item.TryGetProperty("id", out var idNode) &&
                                idNode.TryGetInt32(out var parsedId)
                        ? parsedId
                        : 0;
                    var title = GetString(item, "name");
                    var imageUrl = GetString(item, "tiny_image");
                    var price = item.TryGetProperty("price", out var priceNode)
                        ? GetString(priceNode, "final_formatted")
                        : string.Empty;
                    return new OmniLibraryMetadataSearchResult(
                        appId,
                        title,
                        imageUrl,
                        price,
                        ScoreSearchResult(requested, NormalizeTitle(title)));
                })
                .Where(item => item.SteamAppId > 0 && !string.IsNullOrWhiteSpace(item.Title))
                .GroupBy(item => item.SteamAppId)
                .Select(group => group.First())
                .OrderBy(item => item.MatchScore)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(12)
                .ToArray();
        }
        finally
        {
            _networkGate.Release();
        }
    }

    public Task<OmniLibraryGamePageMetadataSnapshot?> SetSteamMatchAsync(
        uint shortcutAppId,
        int steamAppId,
        CancellationToken cancellationToken) =>
        SetSteamMatchAsync(shortcutAppId, shortcutTitle: null, steamAppId, cancellationToken);

    public async Task<OmniLibraryGamePageMetadataSnapshot?> SetSteamMatchAsync(
        uint shortcutAppId,
        string? shortcutTitle,
        int steamAppId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (steamAppId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steamAppId),
                "A positive Steam app ID is required.");
        }

        if (ResolveGameDetail(shortcutAppId, shortcutTitle) is not { } gameDetail)
        {
            return null;
        }

        var identity = BuildIdentity(gameDetail.StoreId, gameDetail.Game!.Id);
        await AwaitActiveRefreshBeforeMatchChangeAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        SaveSteamMatchOverride(identity, steamAppId);
        return await GetAsync(
                shortcutAppId,
                shortcutTitle,
                forceRefresh: true,
                refreshOnOpen: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<OmniLibraryGamePageMetadataSnapshot?> ClearSteamMatchAsync(
        uint shortcutAppId,
        CancellationToken cancellationToken) =>
        ClearSteamMatchAsync(shortcutAppId, shortcutTitle: null, cancellationToken);

    public async Task<OmniLibraryGamePageMetadataSnapshot?> ClearSteamMatchAsync(
        uint shortcutAppId,
        string? shortcutTitle,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ResolveGameDetail(shortcutAppId, shortcutTitle) is not { } gameDetail)
        {
            return null;
        }

        var identity = BuildIdentity(gameDetail.StoreId, gameDetail.Game!.Id);
        await AwaitActiveRefreshBeforeMatchChangeAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        SaveSteamMatchOverride(identity, null);
        return await GetAsync(
                shortcutAppId,
                shortcutTitle,
                forceRefresh: true,
                refreshOnOpen: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Store id used for shortcuts that OmniLibrary does not manage (games
    /// added by hand, Store Sync, Steam ROM Manager, UWPHook and so on).
    /// </summary>
    public const string ShortcutStoreId = "tfs-shortcut";

    private const int MaxShortcutTitleLength = 200;

    public static bool IsShortcutStore(string? storeId) =>
        string.Equals(storeId, ShortcutStoreId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OmniLibrary keeps ownership of its own games. Any other non-Steam
    /// shortcut (Steam gives those app IDs with the top bit set) becomes a
    /// plain title-only game so Steam store matching can fill its page.
    /// </summary>
    private UnifySteamGameDetailSnapshot? ResolveGameDetail(
        uint shortcutAppId,
        string? shortcutTitle)
    {
        var gameDetail = _gameProvider(shortcutAppId);
        if (gameDetail.Game is not null &&
            !string.IsNullOrWhiteSpace(gameDetail.StoreId) &&
            gameDetail.Game.SteamAppId == shortcutAppId)
        {
            return gameDetail;
        }

        var title = CleanShortcutTitle(shortcutTitle);
        if (shortcutAppId < 0x80000000u || title.Length == 0)
        {
            return null;
        }

        return new UnifySteamGameDetailSnapshot(
            0,
            ShortcutStoreId,
            new UnifySteamGameState(
                shortcutAppId.ToString(CultureInfo.InvariantCulture),
                title,
                Installed: true,
                CloudPlayable: false,
                SyncedToSteam: true,
                StatusText: string.Empty,
                DetailText: string.Empty,
                ImageUrl: string.Empty,
                InstallPath: string.Empty,
                ExecutablePath: string.Empty,
                Version: string.Empty,
                SteamAppId: shortcutAppId,
                Download: new UnifySteamDownloadState(
                    "idle",
                    0,
                    string.Empty,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0)));
    }

    internal static string CleanShortcutTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxShortcutTitleLength));
        foreach (var character in value)
        {
            if (builder.Length >= MaxShortcutTitleLength)
            {
                break;
            }
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    // Shortcuts outside OmniLibrary have no store account, so no achievement
    // provider is consulted for them; their achievement section stays empty
    // instead of showing someone else's Steam unlock list.
    private string GetAchievementFingerprint(UnifySteamGameDetailSnapshot gameDetail) =>
        IsShortcutStore(gameDetail.StoreId)
            ? $"{ShortcutStoreId}:none"
            : _achievementProvider.GetConfigurationFingerprint(gameDetail);

    private bool CanRefreshUserScopedAchievements(UnifySteamGameDetailSnapshot gameDetail) =>
        !IsShortcutStore(gameDetail.StoreId) &&
        _achievementProvider.CanRefreshUserScoped(gameDetail);

    private const int MaxDescriptionLength = 8000;
    private const int MaxListItems = 8;
    private const int MaxListItemLength = 120;
    private const int MaxReleaseDateLength = 40;

    /// <summary>
    /// Editor view for one game: the page as shown (edits applied), the
    /// automatic values underneath and the edits themselves.
    /// </summary>
    public async Task<GameMetadataEditorSnapshot?> GetEditorAsync(
        uint shortcutAppId,
        string? shortcutTitle,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ResolveGameDetail(shortcutAppId, shortcutTitle) is not { } gameDetail)
        {
            return null;
        }

        var identity = BuildIdentity(gameDetail.StoreId, gameDetail.Game!.Id);
        var automatic = await GetCoreAsync(
                shortcutAppId,
                gameDetail,
                identity,
                forceRefresh: false,
                refreshOnOpen: false,
                cancellationToken)
            .ConfigureAwait(false);
        var edits = GetUserEdits(identity) ?? new GameMetadataEdits();
        var ignSlug = GetIgnOverride(identity);
        return new GameMetadataEditorSnapshot(
            ApplyUserEdits(automatic, edits),
            automatic.GameInfo,
            edits,
            ManagedByOmniLibrary: !IsShortcutStore(gameDetail.StoreId),
            IgnPage: ignSlug is null ? string.Empty : IgnMetadataClient.PageBaseUrl + ignSlug);
    }

    /// <summary>
    /// Picks an IGN page (address or slug) as the game's source; blank goes
    /// back to automatic. Throws ArgumentException for anything that is not
    /// an IGN game page.
    /// </summary>
    public async Task<GameMetadataEditorSnapshot?> SetIgnPageAsync(
        uint shortcutAppId,
        string? shortcutTitle,
        string? page,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string? slug = null;
        if (!string.IsNullOrWhiteSpace(page))
        {
            slug = IgnMetadataClient.ParseSlug(page)
                ?? throw new ArgumentException("Enter an IGN game page address, for example https://www.ign.com/games/hollow-knight.");
        }

        if (ResolveGameDetail(shortcutAppId, shortcutTitle) is not { } gameDetail)
        {
            return null;
        }

        var identity = BuildIdentity(gameDetail.StoreId, gameDetail.Game!.Id);
        await AwaitActiveRefreshBeforeMatchChangeAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        lock (_cacheGate)
        {
            var cache = LoadCacheLocked();
            if (slug is null)
            {
                cache.IgnSlugOverrides.Remove(identity);
            }
            else
            {
                cache.IgnSlugOverrides[identity] = slug;
            }
            cache.Entries.Remove(identity);
            SaveCacheLocked(cache);
        }

        return await GetEditorAsync(shortcutAppId, shortcutTitle, cancellationToken)
            .ConfigureAwait(false);
    }

    private string? GetIgnOverride(string identity)
    {
        lock (_cacheGate)
        {
            return LoadCacheLocked().IgnSlugOverrides.TryGetValue(identity, out var slug)
                ? slug
                : null;
        }
    }

    /// <summary>Saves hand-made game info; null clears every edit.</summary>
    public async Task<GameMetadataEditorSnapshot?> SaveEditsAsync(
        uint shortcutAppId,
        string? shortcutTitle,
        GameMetadataEdits? edits,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ResolveGameDetail(shortcutAppId, shortcutTitle) is not { } gameDetail)
        {
            return null;
        }

        var identity = BuildIdentity(gameDetail.StoreId, gameDetail.Game!.Id);
        var clean = CleanEdits(edits);
        lock (_cacheGate)
        {
            var cache = LoadCacheLocked();
            if (clean is null)
            {
                cache.UserEdits.Remove(identity);
            }
            else
            {
                cache.UserEdits[identity] = clean;
            }

            // A new revision makes every open game page re-apply the snapshot.
            if (cache.Entries.TryGetValue(identity, out var entry))
            {
                entry.Revision = Interlocked.Increment(ref _revision);
            }
            SaveCacheLocked(cache);
        }

        return await GetEditorAsync(shortcutAppId, shortcutTitle, cancellationToken)
            .ConfigureAwait(false);
    }

    private GameMetadataEdits? GetUserEdits(string identity)
    {
        lock (_cacheGate)
        {
            return LoadCacheLocked().UserEdits.TryGetValue(identity, out var edits)
                ? edits
                : null;
        }
    }

    /// <summary>
    /// Trims and caps every field. Blank fields mean "automatic"; returns
    /// null when nothing is left to keep.
    /// </summary>
    internal static GameMetadataEdits? CleanEdits(GameMetadataEdits? edits)
    {
        if (edits is null)
        {
            return null;
        }

        static string? Text(string? value, int maxLength)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
        }

        static IReadOnlyList<string>? List(IReadOnlyList<string>? values)
        {
            var items = (values ?? [])
                .Select(value => CleanShortcutTitle(value))
                .Select(value => value.Length <= MaxListItemLength ? value : value[..MaxListItemLength].TrimEnd())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxListItems)
                .ToArray();
            return items.Length > 0 ? items : null;
        }

        var clean = new GameMetadataEdits
        {
            Description = Text(edits.Description, MaxDescriptionLength),
            Developers = List(edits.Developers),
            Publishers = List(edits.Publishers),
            Genres = List(edits.Genres),
            ReleaseDate = Text(CleanShortcutTitle(edits.ReleaseDate), MaxReleaseDateLength),
            Rating = edits.Rating is { } rating ? Math.Clamp(rating, 0, 100) : null,
        };
        return clean.Description is null &&
               clean.Developers is null &&
               clean.Publishers is null &&
               clean.Genres is null &&
               clean.ReleaseDate is null &&
               clean.Rating is null
            ? null
            : clean;
    }

    internal static OmniLibraryGamePageMetadataSnapshot ApplyUserEdits(
        OmniLibraryGamePageMetadataSnapshot snapshot,
        GameMetadataEdits? edits)
    {
        if (edits is null)
        {
            return snapshot;
        }

        var info = snapshot.GameInfo;
        var edited = new List<string>();
        if (edits.Description is { } description)
        {
            var shortDescription = description.Length <= 300
                ? description
                : description[..300].TrimEnd() + "…";
            info = info with { Description = description, ShortDescription = shortDescription };
            edited.Add("description");
        }
        if (edits.Developers is { } developers)
        {
            info = info with { Developers = developers };
            edited.Add("developers");
        }
        if (edits.Publishers is { } publishers)
        {
            info = info with { Publishers = publishers };
            edited.Add("publishers");
        }
        if (edits.Genres is { } genres)
        {
            info = info with { Genres = genres };
            edited.Add("genres");
        }
        if (edits.ReleaseDate is { } releaseDate)
        {
            info = info with { ReleaseDate = releaseDate };
            edited.Add("releaseDate");
        }
        if (edits.Rating is { } rating)
        {
            info = info with { Rating = rating, RatingLabel = "Your rating" };
            edited.Add("rating");
        }

        if (edited.Count == 0)
        {
            return snapshot;
        }

        // Hand-made info counts as real metadata even without a source match.
        var status = snapshot.Status == "unmatched" ? "ready" : snapshot.Status;
        return snapshot with
        {
            Status = status,
            GameInfo = info,
            EditedFields = edited,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _networkGate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private Task<CacheEntry> StartRefreshAsync(
        string identity,
        UnifySteamGameDetailSnapshot gameDetail,
        CacheEntry? previous,
        bool forceRefresh)
    {
        var candidate = new Lazy<Task<CacheEntry>>(
            () => RefreshCoreAsync(
                identity,
                gameDetail,
                previous,
                forceRefresh,
                CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var active = _refreshes.GetOrAdd(identity, candidate);
        return AwaitRefreshAndReleaseAsync(identity, active);
    }

    private async Task<CacheEntry> AwaitRefreshAndReleaseAsync(
        string identity,
        Lazy<Task<CacheEntry>> refresh)
    {
        try
        {
            return await refresh.Value.ConfigureAwait(false);
        }
        finally
        {
            _refreshes.TryRemove(
                new KeyValuePair<string, Lazy<Task<CacheEntry>>>(
                    identity,
                    refresh));
        }
    }

    private async Task<CacheEntry> RefreshCoreAsync(
        string identity,
        UnifySteamGameDetailSnapshot gameDetail,
        CacheEntry? previous,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await _networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var currentAchievementFingerprint = GetAchievementFingerprint(gameDetail);
            if (!forceRefresh &&
                previous?.RetryAfterUtc is { } retryAfter &&
                now < retryAfter &&
                string.Equals(
                    previous.AchievementConfigurationFingerprint,
                    currentAchievementFingerprint,
                    StringComparison.Ordinal))
            {
                return previous;
            }

            var game = gameDetail.Game!;
            var title = game.Title.Trim();
            var manualSteamAppId = GetSteamMatchOverride(identity);
            var sourceSteamAppId = manualSteamAppId ?? previous?.SourceSteamAppId;
            var sourceMatchedAtUtc = previous?.SourceMatchedAtUtc;
            if (manualSteamAppId.HasValue)
            {
                sourceMatchedAtUtc = now;
            }
            var sourceMatchLifetime = sourceSteamAppId.HasValue
                ? SourceMatchLifetime
                : UnmatchedSourceMatchLifetime;
            var needsSourceMatch =
                !manualSteamAppId.HasValue &&
                (forceRefresh ||
                !sourceMatchedAtUtc.HasValue ||
                now - sourceMatchedAtUtc.Value >= sourceMatchLifetime ||
                !NormalizeTitle(previous?.Title ?? string.Empty).Equals(
                    NormalizeTitle(title),
                    StringComparison.Ordinal));
            if (needsSourceMatch)
            {
                sourceSteamAppId = await ResolveSteamAppIdAsync(
                    title,
                    cancellationToken).ConfigureAwait(false);
                sourceMatchedAtUtc = now;
            }

            // A new (or lost) Steam match makes the cached game info stale at once.
            var sourceChanged = previous is not null &&
                                previous.SourceSteamAppId != sourceSteamAppId;
            var translatedLanguage = previous?.TranslatedLanguage ?? string.Empty;
            var gameInfo = previous?.GameInfo ?? EmptyGameInfo(title, sourceSteamAppId);
            var activity = previous?.Activity ?? [];
            var community = previous?.Community ?? [];
            var metadataSource = previous?.MetadataSource ?? string.Empty;
            var infoRefreshedAtUtc = previous?.InfoRefreshedAtUtc;
            var activityRefreshedAtUtc = previous?.ActivityRefreshedAtUtc;
            var achievements = previous?.Achievements ??
                StoreAchievementPlaceholder(gameDetail.StoreId);
            var achievementDefinitionsRefreshedAtUtc =
                previous?.AchievementDefinitionsRefreshedAtUtc ??
                previous?.AchievementsRefreshedAtUtc;
            var achievementProgressRefreshedAtUtc =
                previous?.AchievementProgressRefreshedAtUtc;
            var achievementRetryAfterUtc = previous?.AchievementRetryAfterUtc;
            var achievementProviderState =
                previous?.AchievementProviderState ?? string.Empty;
            var achievementConfigurationFingerprint = GetAchievementFingerprint(gameDetail);
            var achievementConfigurationChanged =
                !string.Equals(
                    previous?.AchievementConfigurationFingerprint,
                    achievementConfigurationFingerprint,
                    StringComparison.Ordinal);
            var achievementLastError = previous?.AchievementLastError ?? string.Empty;
            var refreshedAnySection = false;
            var failures = new List<string>();

            CacheEntry PersistProgress()
            {
                var next = new CacheEntry
                {
                    ShortcutAppId = game.SteamAppId,
                    StoreId = gameDetail.StoreId,
                    GameId = game.Id,
                    Title = title,
                    SourceSteamAppId = sourceSteamAppId,
                    MetadataSource = metadataSource,
                    TranslatedLanguage = translatedLanguage,
                    MatchMode = manualSteamAppId.HasValue ? "manual" : "automatic",
                    SourceMatchedAtUtc = sourceMatchedAtUtc,
                    InfoRefreshedAtUtc = infoRefreshedAtUtc,
                    ActivityRefreshedAtUtc = activityRefreshedAtUtc,
                    AchievementsRefreshedAtUtc = achievementDefinitionsRefreshedAtUtc,
                    AchievementDefinitionsRefreshedAtUtc =
                        achievementDefinitionsRefreshedAtUtc,
                    AchievementProgressRefreshedAtUtc =
                        achievementProgressRefreshedAtUtc,
                    AchievementRetryAfterUtc = achievementRetryAfterUtc,
                    AchievementLastError = achievementLastError,
                    AchievementProviderState = achievementProviderState,
                    AchievementConfigurationFingerprint =
                        achievementConfigurationFingerprint,
                    LastAccessedAtUtc = now,
                    RefreshedAtUtc = refreshedAnySection
                        ? now
                        : previous?.RefreshedAtUtc,
                    RetryAfterUtc = failures.Count > 0
                        ? now.Add(FailureBackoff)
                        : null,
                    LastError = string.Join(
                        " ",
                        failures.Append(achievementLastError)
                            .Where(value => !string.IsNullOrWhiteSpace(value))),
                    GameInfo = gameInfo,
                    Activity = activity,
                    Community = community,
                    Achievements = achievements,
                };
                next.ContentHash = ComputeContentHash(next);

                if (previous is not null &&
                    previous.ContentHash.Equals(next.ContentHash, StringComparison.Ordinal) &&
                    previous.ShortcutAppId == next.ShortcutAppId)
                {
                    next.Revision = previous.Revision;
                }
                else
                {
                    next.Revision = Interlocked.Increment(ref _revision);
                }

                SaveEntry(identity, next);
                previous = next;
                return next;
            }

            if (needsSourceMatch)
            {
                PersistProgress();
            }

            var ignOverride = GetIgnOverride(identity);
            if (ignOverride is not null)
            {
                // An IGN page picked by hand wins over Steam, Xbox and search.
                if (forceRefresh ||
                    !metadataSource.Equals(IgnSourceLabel, StringComparison.Ordinal) ||
                    !infoRefreshedAtUtc.HasValue ||
                    now - infoRefreshedAtUtc.Value >= GameInfoLifetime ||
                    IsTranslationOutdated(translatedLanguage))
                {
                    try
                    {
                        var ign = await _ign.FetchAsync(ignOverride, cancellationToken)
                            .ConfigureAwait(false);
                        if (ign is null)
                        {
                            failures.Add("IGN Game Info: The chosen IGN page was not found.");
                        }
                        else
                        {
                            (gameInfo, translatedLanguage) = await BuildIgnGameInfoAsync(
                                ign,
                                cancellationToken).ConfigureAwait(false);
                            metadataSource = IgnSourceLabel;
                            community = [];
                            infoRefreshedAtUtc = now;
                            refreshedAnySection = true;
                            PersistProgress();
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        failures.Add($"IGN Game Info: {FriendlyError(error)}");
                    }
                }
            }
            else if (!manualSteamAppId.HasValue &&
                gameDetail.StoreId.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase) &&
                (forceRefresh ||
                 !metadataSource.Equals("Xbox", StringComparison.OrdinalIgnoreCase) ||
                 !infoRefreshedAtUtc.HasValue ||
                 now - infoRefreshedAtUtc.Value >= GameInfoLifetime))
            {
                try
                {
                    var details = await FetchXboxCatalogDetailsAsync(
                        game.Id,
                        title,
                        cancellationToken).ConfigureAwait(false);
                    gameInfo = details.GameInfo;
                    community = details.Community;
                    metadataSource = "Xbox";
                    infoRefreshedAtUtc = now;
                    refreshedAnySection = true;
                    PersistProgress();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures.Add($"Xbox Game Info: {FriendlyError(error)}");
                }
            }
            else if (sourceSteamAppId.HasValue &&
                     (forceRefresh ||
                      sourceChanged ||
                      metadataSource.Equals(IgnSourceLabel, StringComparison.Ordinal) ||
                      !infoRefreshedAtUtc.HasValue ||
                      now - infoRefreshedAtUtc.Value >= GameInfoLifetime))
            {
                try
                {
                    var details = await FetchAppDetailsAsync(
                        sourceSteamAppId.Value,
                        title,
                        cancellationToken).ConfigureAwait(false);
                    gameInfo = details.GameInfo;
                    community = details.Community;
                    metadataSource = manualSteamAppId.HasValue
                        ? "Steam (selected)"
                        : "Steam";
                    infoRefreshedAtUtc = now;
                    refreshedAnySection = true;
                    PersistProgress();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures.Add($"Game Info: {FriendlyError(error)}");
                }
            }
            else if (!sourceSteamAppId.HasValue &&
                     !gameDetail.StoreId.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase) &&
                     (forceRefresh ||
                      sourceChanged ||
                      !infoRefreshedAtUtc.HasValue ||
                      now - infoRefreshedAtUtc.Value >= GameInfoLifetime ||
                      (metadataSource.Equals(IgnSourceLabel, StringComparison.Ordinal) &&
                       IsTranslationOutdated(translatedLanguage))))
            {
                // No Steam store page: IGN's public game page is the fallback.
                try
                {
                    var ign = await _ign.FindAsync(
                        IgnTitleVariants(title),
                        cancellationToken).ConfigureAwait(false);
                    // Without a matching page the last good info stays.
                    if (ign is not null)
                    {
                        (gameInfo, translatedLanguage) = await BuildIgnGameInfoAsync(
                            ign,
                            cancellationToken).ConfigureAwait(false);
                        metadataSource = IgnSourceLabel;
                        community = [];
                    }
                    infoRefreshedAtUtc = now;
                    refreshedAnySection = true;
                    PersistProgress();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures.Add($"IGN Game Info: {FriendlyError(error)}");
                }
            }

            if (sourceSteamAppId.HasValue &&
                (forceRefresh ||
                 !activityRefreshedAtUtc.HasValue ||
                 now - activityRefreshedAtUtc.Value >= ActivityLifetime))
            {
                try
                {
                    activity = await FetchActivityAsync(
                        sourceSteamAppId.Value,
                        gameInfo.HeaderImageUrl,
                        cancellationToken).ConfigureAwait(false);
                    activityRefreshedAtUtc = now;
                    refreshedAnySection = true;
                    PersistProgress();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failures.Add($"Activity: {FriendlyError(error)}");
                }
            }

            var isShortcut = IsShortcutStore(gameDetail.StoreId);
            var hasUserScopedProvider = CanRefreshUserScopedAchievements(gameDetail);
            var definitionsDue =
                forceRefresh ||
                achievementConfigurationChanged ||
                !achievementDefinitionsRefreshedAtUtc.HasValue ||
                now - achievementDefinitionsRefreshedAtUtc.Value >=
                AchievementDefinitionLifetime;
            var progressDue =
                forceRefresh ||
                achievementConfigurationChanged ||
                !achievementProgressRefreshedAtUtc.HasValue ||
                now - achievementProgressRefreshedAtUtc.Value >=
                AchievementProgressLifetime;
            var achievementBackoffActive =
                !forceRefresh &&
                !achievementConfigurationChanged &&
                achievementRetryAfterUtc.HasValue &&
                now < achievementRetryAfterUtc.Value;
            if (isShortcut)
            {
                if (definitionsDue)
                {
                    achievements = StoreAchievementPlaceholder(gameDetail.StoreId);
                    achievementDefinitionsRefreshedAtUtc = now;
                    achievementLastError = string.Empty;
                    achievementRetryAfterUtc = null;
                }
            }
            else if (hasUserScopedProvider &&
                (definitionsDue || progressDue) &&
                !achievementBackoffActive)
            {
                var achievementResult = await _achievementProvider.RefreshAsync(
                    gameDetail,
                    achievements,
                    achievementProviderState,
                    definitionsDue,
                    progressDue,
                    cancellationToken).ConfigureAwait(false);
                if (achievementResult.Metadata is not null)
                {
                    achievements = achievementResult.Metadata;
                    achievementProviderState = achievementResult.ProviderState;
                    if (achievementResult.DefinitionsRefreshed)
                    {
                        achievementDefinitionsRefreshedAtUtc = now;
                    }
                    if (achievementResult.ProgressRefreshed)
                    {
                        achievementProgressRefreshedAtUtc = now;
                    }
                    achievementLastError = string.Empty;
                    achievementRetryAfterUtc = achievementResult.RetryAfterUtc;
                    achievementConfigurationFingerprint = GetAchievementFingerprint(gameDetail);
                    refreshedAnySection = true;
                    PersistProgress();
                }
                else
                {
                    achievementLastError = achievementResult.Error;
                    achievementRetryAfterUtc = achievementResult.RetryAfterUtc;
                }
            }
            else if (!hasUserScopedProvider &&
                     sourceSteamAppId.HasValue &&
                     definitionsDue)
            {
                try
                {
                    var definitions = await FetchSteamAchievementDefinitionsAsync(
                        sourceSteamAppId.Value,
                        cancellationToken).ConfigureAwait(false);
                    if (definitions is not null)
                    {
                        achievements = definitions;
                    }
                    achievementDefinitionsRefreshedAtUtc = now;
                    refreshedAnySection = true;
                    PersistProgress();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    achievementLastError =
                        $"Achievements: {FriendlyError(error)}";
                    achievementRetryAfterUtc = now.Add(FailureBackoff);
                }
            }

            if (!sourceSteamAppId.HasValue &&
                string.IsNullOrWhiteSpace(gameInfo.Description) &&
                string.IsNullOrWhiteSpace(gameInfo.ShortDescription))
            {
                failures.Add(
                    "No high-confidence Steam match was found. The store artwork and shortcut remain unchanged.");
            }

            return PersistProgress();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (previous is not null)
            {
                var fallback = previous with
                {
                    LastError = FriendlyError(error),
                    RetryAfterUtc = DateTimeOffset.UtcNow.Add(FailureBackoff),
                    LastAccessedAtUtc = DateTimeOffset.UtcNow,
                };
                SaveEntry(identity, fallback);
                return fallback;
            }

            throw;
        }
        finally
        {
            _networkGate.Release();
        }
    }

    // IGN text needs a translation when the UI is not English and the cached
    // text is not in the UI language yet (e.g. the last attempt failed).
    private static bool IsTranslationOutdated(string translatedLanguage)
    {
        var target = MetadataTranslator.CurrentTargetLanguage();
        return !target.Equals("en", StringComparison.OrdinalIgnoreCase) &&
               !target.Equals(translatedLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> IgnTitleVariants(string title) =>
        new[]
            {
                title,
                WindowsSuffixPattern.Replace(title, string.Empty).Trim(),
                EditionSuffixPattern.Replace(title, string.Empty).Trim(),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// IGN writes English. The descriptions are translated into the Windows UI
    /// language (Google, MyMemory as fallback); on failure the English text
    /// stays and the translation is retried with the next refresh.
    /// </summary>
    private async Task<(OmniLibraryGameInfoMetadata GameInfo, string TranslatedLanguage)>
        BuildIgnGameInfoAsync(IgnGameInfo ign, CancellationToken cancellationToken)
    {
        var description = ign.Description;
        var shortDescription = ign.ShortDescription;
        var translatedLanguage = string.Empty;
        var target = MetadataTranslator.CurrentTargetLanguage();
        if (!target.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            var translated = await _translator.TranslateAsync(description, target, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                description = translated;
                translatedLanguage = target;
                if (!string.IsNullOrWhiteSpace(shortDescription))
                {
                    shortDescription = shortDescription.Equals(ign.Description, StringComparison.Ordinal)
                        ? translated
                        : await _translator.TranslateAsync(shortDescription, target, cancellationToken)
                              .ConfigureAwait(false) ?? shortDescription;
                }
            }
        }

        var info = new OmniLibraryGameInfoMetadata(
            shortDescription,
            description,
            ign.Developers,
            ign.Publishers,
            ign.Genres,
            [],
            ign.ReleaseDate,
            ign.Rating,
            ign.Rating.HasValue ? "IGN" : string.Empty,
            ign.ImageUrl,
            ign.PageUrl,
            []);
        return (info, translatedLanguage);
    }

    private async Task<int?> ResolveSteamAppIdAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var searchTerms = new[]
            {
                title,
                WindowsSuffixPattern.Replace(title, string.Empty).Trim(),
                EditionSuffixPattern.Replace(
                    NormalizeTitle(title),
                    string.Empty).Trim(),
                EditionSuffixPattern.Replace(title, string.Empty).Trim(),
            }
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3);
        var requested = NormalizeTitle(title);
        var requestedNumbers = ExtractNumbers(requested);

        foreach (var term in searchTerms)
        {
            var (language, market) = GetSteamLocale();
            var url =
                $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}" +
                $"&l={Uri.EscapeDataString(language)}&cc={Uri.EscapeDataString(market)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var matches = new List<(int AppId, int Score)>();
            foreach (var item in items.EnumerateArray().Take(20))
            {
                var appId = item.TryGetProperty("id", out var idNode) &&
                            idNode.TryGetInt32(out var parsedId)
                    ? parsedId
                    : 0;
                var candidateTitle = item.TryGetProperty("name", out var nameNode)
                    ? nameNode.GetString() ?? string.Empty
                    : string.Empty;
                if (appId <= 0 || string.IsNullOrWhiteSpace(candidateTitle))
                {
                    continue;
                }

                var candidate = NormalizeTitle(candidateTitle);
                if (!requestedNumbers.SetEquals(ExtractNumbers(candidate)))
                {
                    continue;
                }

                var score = ScoreTitleMatch(requested, candidate);
                if (score >= 0)
                {
                    matches.Add((appId, score));
                }
            }

            var best = matches
                .OrderBy(match => match.Score)
                .ThenBy(match => match.AppId)
                .FirstOrDefault();
            if (best.AppId > 0 && best.Score <= 1)
            {
                return best.AppId;
            }
        }

        return null;
    }

    private async Task<(OmniLibraryGameInfoMetadata GameInfo, List<OmniLibraryCommunityMetadata> Community)>
        FetchAppDetailsAsync(
            int steamAppId,
            string fallbackTitle,
            CancellationToken cancellationToken)
    {
        var (language, market) = GetSteamLocale();
        var url =
            $"https://store.steampowered.com/api/appdetails?appids={steamAppId}" +
            $"&l={Uri.EscapeDataString(language)}&cc={Uri.EscapeDataString(market)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty(
                steamAppId.ToString(CultureInfo.InvariantCulture),
                out var wrapper) ||
            !wrapper.TryGetProperty("success", out var success) ||
            !success.GetBoolean() ||
            !wrapper.TryGetProperty("data", out var data))
        {
            throw new InvalidDataException("Steam did not return metadata for this match.");
        }

        var shortDescription = GetString(data, "short_description");
        var description = CleanMarkup(GetString(data, "detailed_description"));
        if (string.IsNullOrWhiteSpace(description))
        {
            description = CleanMarkup(GetString(data, "about_the_game"));
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            description = CleanMarkup(shortDescription);
        }

        var screenshots = ParseScreenshots(data);
        var headerImage = GetString(data, "header_image");
        var developers = ReadStringArray(data, "developers");
        var publishers = ReadStringArray(data, "publishers");
        var genres = ReadDescriptionArray(data, "genres");
        var features = ReadDescriptionArray(data, "categories");
        var releaseDate = data.TryGetProperty("release_date", out var releaseNode)
            ? GetString(releaseNode, "date")
            : string.Empty;
        int? rating = null;
        if (data.TryGetProperty("metacritic", out var metacriticNode) &&
            metacriticNode.TryGetProperty("score", out var scoreNode) &&
            scoreNode.TryGetInt32(out var score))
        {
            rating = score;
        }

        var gameInfo = new OmniLibraryGameInfoMetadata(
            CleanMarkup(shortDescription),
            description,
            developers,
            publishers,
            genres,
            features,
            releaseDate,
            rating,
            rating.HasValue ? "Metacritic" : string.Empty,
            headerImage,
            $"https://store.steampowered.com/app/{steamAppId}/",
            screenshots);
        var community = new List<OmniLibraryCommunityMetadata>();
        community.AddRange(screenshots.Take(12).Select((screenshot, index) =>
            new OmniLibraryCommunityMetadata(
                $"steam-screenshot-{steamAppId}-{index}",
                "image",
                string.IsNullOrWhiteSpace(screenshot.Caption)
                    ? $"{fallbackTitle} screenshot"
                    : screenshot.Caption,
                screenshot.FullImageUrl,
                screenshot.ThumbnailUrl,
                "Steam")));
        community.AddRange(ParseMovies(data, steamAppId));
        return (gameInfo, community);
    }

    private async Task<(OmniLibraryGameInfoMetadata GameInfo, List<OmniLibraryCommunityMetadata> Community)>
        FetchXboxCatalogDetailsAsync(
            string productId,
            string fallbackTitle,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(productId) ||
            !productId.All(character => char.IsLetterOrDigit(character)))
        {
            throw new InvalidDataException("The Xbox product ID is invalid.");
        }

        var culture = CultureInfo.CurrentUICulture;
        var language = string.IsNullOrWhiteSpace(culture.Name)
            ? "en-us"
            : culture.Name.ToLowerInvariant();
        var market = "US";
        try
        {
            market = new RegionInfo(culture.Name).TwoLetterISORegionName.ToUpperInvariant();
        }
        catch
        {
        }

        var url =
            $"https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={Uri.EscapeDataString(productId)}" +
            $"&market={Uri.EscapeDataString(market)}&languages={Uri.EscapeDataString(language)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("Products", out var products) ||
            products.ValueKind != JsonValueKind.Array ||
            products.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Xbox did not return metadata for this product.");
        }

        var product = products.EnumerateArray().First();
        var localized = product.TryGetProperty("LocalizedProperties", out var localizedValues) &&
                        localizedValues.ValueKind == JsonValueKind.Array
            ? localizedValues.EnumerateArray().FirstOrDefault()
            : default;
        if (localized.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Xbox did not return localized product metadata.");
        }

        var title = GetString(localized, "ProductTitle");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = fallbackTitle;
        }
        var shortDescription = CleanMarkup(GetString(localized, "ShortDescription"));
        var description = CleanMarkup(GetString(localized, "ProductDescription"));
        if (string.IsNullOrWhiteSpace(description))
        {
            description = shortDescription;
        }
        if (string.IsNullOrWhiteSpace(shortDescription) && !string.IsNullOrWhiteSpace(description))
        {
            shortDescription = description.Length <= 260
                ? description
                : description[..257].TrimEnd() + "...";
        }

        var screenshots = ParseXboxImages(localized, "Screenshot", take: 16)
            .Select((image, index) => new OmniLibraryScreenshotMetadata(
                $"xbox-screenshot-{productId}-{index}",
                image.Url,
                image.Url,
                string.IsNullOrWhiteSpace(image.Caption)
                    ? $"{title} screenshot"
                    : image.Caption))
            .ToList();
        var headerImage = ParseXboxImages(localized, string.Empty, take: 40)
            .OrderByDescending(image => XboxImagePurposeScore(image.Purpose))
            .ThenByDescending(image => image.Width * image.Height)
            .FirstOrDefault(image => image.Width > image.Height)?.Url ?? string.Empty;
        var releaseDate = string.Empty;
        int? userRating = null;
        if (product.TryGetProperty("MarketProperties", out var marketValues) &&
            marketValues.ValueKind == JsonValueKind.Array)
        {
            var marketProperty = marketValues.EnumerateArray().FirstOrDefault();
            var originalReleaseDate = GetString(marketProperty, "OriginalReleaseDate");
            if (DateTimeOffset.TryParse(
                    originalReleaseDate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsedRelease))
            {
                releaseDate = parsedRelease.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            }
            if (marketProperty.TryGetProperty("UsageData", out var usageData) &&
                usageData.ValueKind == JsonValueKind.Array)
            {
                var allTime = usageData.EnumerateArray().FirstOrDefault(item =>
                    GetString(item, "AggregateTimeSpan")
                        .Equals("AllTime", StringComparison.OrdinalIgnoreCase));
                if (allTime.ValueKind == JsonValueKind.Object &&
                    allTime.TryGetProperty("AverageRating", out var ratingNode) &&
                    ratingNode.TryGetDouble(out var rating))
                {
                    userRating = Math.Clamp((int)Math.Round(rating * 20), 0, 100);
                }
            }
        }

        var genres = new List<string>();
        var features = new List<string>();
        if (product.TryGetProperty("Properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            var category = GetString(properties, "Category");
            if (!string.IsNullOrWhiteSpace(category))
            {
                genres.Add(category);
            }
            if (properties.TryGetProperty("Attributes", out var attributes) &&
                attributes.ValueKind == JsonValueKind.Array)
            {
                features.AddRange(attributes.EnumerateArray()
                    .Select(attribute => FriendlyXboxFeature(GetString(attribute, "Name")))
                    .Where(feature => !string.IsNullOrWhiteSpace(feature))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12));
            }
        }

        var gameInfo = new OmniLibraryGameInfoMetadata(
            shortDescription,
            description,
            SingleValueList(GetString(localized, "DeveloperName")),
            SingleValueList(GetString(localized, "PublisherName")),
            genres,
            features,
            releaseDate,
            userRating,
            userRating.HasValue ? "Xbox user rating" : string.Empty,
            headerImage,
            $"https://www.xbox.com/games/store/{Uri.EscapeDataString(productId)}",
            screenshots);
        var community = screenshots
            .Take(12)
            .Select((screenshot, index) => new OmniLibraryCommunityMetadata(
                $"xbox-community-{productId}-{index}",
                "image",
                screenshot.Caption,
                screenshot.FullImageUrl,
                screenshot.ThumbnailUrl,
                "Xbox"))
            .ToList();
        return (gameInfo, community);
    }

    /// <summary>
    /// Steam's news API mixes in external feeds in other scripts (for example
    /// Russian gaming sites). Drop titles written mostly in Cyrillic or CJK
    /// script unless the Windows UI uses such a language (CJK = Chinese,
    /// Japanese, Korean).
    /// </summary>
    internal static bool IsReadableNewsTitle(string title, string uiLanguage)
    {
        int letters = 0, cyrillic = 0, cjk = 0;
        foreach (var character in title)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }
            letters++;
            if (character is >= 'Ѐ' and <= 'ӿ')
            {
                cyrillic++;
            }
            else if (character is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯')
            {
                cjk++;
            }
        }

        if (letters == 0)
        {
            return true;
        }

        // Game names stay Latin inside foreign titles, so a quarter of the
        // letters in another script already marks a foreign-language article.
        var language = uiLanguage.ToLowerInvariant();
        if (cyrillic >= 3 && cyrillic * 4 > letters &&
            language is not ("ru" or "uk" or "bg" or "be" or "sr" or "kk" or "mk"))
        {
            return false;
        }

        return !(cjk >= 2 && cjk * 4 > letters && language is not ("zh" or "ja" or "ko"));
    }

    private async Task<List<OmniLibraryActivityMetadata>> FetchActivityAsync(
        int steamAppId,
        string fallbackImageUrl,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={steamAppId}&count=20&maxlength=700&format=json";
        using var response = await _httpClient.GetAsync(url, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("appnews", out var appNews) ||
            !appNews.TryGetProperty("newsitems", out var newsItems) ||
            newsItems.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<OmniLibraryActivityMetadata>();
        foreach (var item in newsItems.EnumerateArray())
        {
            var title = CleanMarkup(GetString(item, "title"));
            var contents = GetString(item, "contents");
            var summary = CleanMarkup(contents);
            if (summary.Length > 520)
            {
                summary = summary[..517].TrimEnd() + "...";
            }
            var id = GetString(item, "gid");
            var publishedAt = item.TryGetProperty("date", out var dateNode) &&
                              dateNode.TryGetInt64(out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : (DateTimeOffset?)null;
            var image = ExtractImageUrl(contents);
            if (string.IsNullOrWhiteSpace(image))
            {
                image = fallbackImageUrl;
            }

            items.Add(new OmniLibraryActivityMetadata(
                string.IsNullOrWhiteSpace(id)
                    ? $"steam-news-{steamAppId}-{items.Count}"
                    : id,
                title,
                summary,
                GetString(item, "url"),
                image,
                GetString(item, "author"),
                GetString(item, "feedlabel"),
                publishedAt));
        }

        var uiLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Where(item => IsReadableNewsTitle(item.Title, uiLanguage))
            .GroupBy(
                item => $"{NormalizeTitle(item.Title)}|{item.PublishedAtUtc?.ToUnixTimeSeconds() ?? 0}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => item.PublishedAtUtc)
            .Take(8)
            .ToList();
    }

    private async Task<OmniLibraryAchievementMetadata?> FetchSteamAchievementDefinitionsAsync(
        int steamAppId,
        CancellationToken cancellationToken)
    {
        var url = $"https://steamcommunity.com/stats/{steamAppId}/achievements/?l=english";
        using var response = await _httpClient.GetAsync(url, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = new List<OmniLibraryAchievementItemMetadata>();
        foreach (Match match in AchievementRowPattern.Matches(html))
        {
            var name = CleanMarkup(match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var description = CleanMarkup(match.Groups["description"].Value);
            var iconUrl = WebUtility.HtmlDecode(match.Groups["icon"].Value).Trim();
            if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var parsedIcon) ||
                parsedIcon.Scheme != Uri.UriSchemeHttps)
            {
                iconUrl = string.Empty;
            }
            items.Add(new OmniLibraryAchievementItemMetadata(
                $"steam-definition-{steamAppId}-{items.Count}",
                name,
                description,
                Unlocked: false,
                Hidden: false,
                UnlockedAtUtc: null,
                iconUrl,
                CurrentProgress: 0,
                TargetProgress: 1));
        }
        if (items.Count == 0)
        {
            return null;
        }
        return new OmniLibraryAchievementMetadata(
            "Steam metadata",
            "definitions-only",
            "Achievement names and icons are matched from public Steam metadata. Personal unlock progress is shown only when the connected store exposes a verified user-scoped source.",
            0,
            items.Count,
            items);
    }

    private static List<OmniLibraryScreenshotMetadata> ParseScreenshots(JsonElement data)
    {
        if (!data.TryGetProperty("screenshots", out var screenshots) ||
            screenshots.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<OmniLibraryScreenshotMetadata>();
        foreach (var screenshot in screenshots.EnumerateArray().Take(20))
        {
            var id = screenshot.TryGetProperty("id", out var idNode)
                ? idNode.ToString()
                : $"screenshot-{result.Count}";
            var thumbnail = GetString(screenshot, "path_thumbnail");
            var full = GetString(screenshot, "path_full");
            if (string.IsNullOrWhiteSpace(full))
            {
                continue;
            }
            result.Add(new OmniLibraryScreenshotMetadata(
                id,
                string.IsNullOrWhiteSpace(thumbnail) ? full : thumbnail,
                full,
                string.Empty));
        }
        return result;
    }

    private static IEnumerable<OmniLibraryCommunityMetadata> ParseMovies(
        JsonElement data,
        int steamAppId)
    {
        if (!data.TryGetProperty("movies", out var movies) ||
            movies.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 0;
        foreach (var movie in movies.EnumerateArray().Take(6))
        {
            var title = GetString(movie, "name");
            var thumbnail = GetString(movie, "thumbnail");
            var movieUrl = string.Empty;
            if (movie.TryGetProperty("mp4", out var mp4))
            {
                movieUrl = GetString(mp4, "max");
                if (string.IsNullOrWhiteSpace(movieUrl))
                {
                    movieUrl = GetString(mp4, "480");
                }
            }
            if (string.IsNullOrWhiteSpace(movieUrl))
            {
                continue;
            }
            yield return new OmniLibraryCommunityMetadata(
                $"steam-movie-{steamAppId}-{index++}",
                "video",
                title,
                movieUrl,
                thumbnail,
                "Steam");
        }
    }

    private static List<XboxImageMetadata> ParseXboxImages(
        JsonElement localized,
        string requiredPurpose,
        int take)
    {
        if (!localized.TryGetProperty("Images", out var images) ||
            images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return images.EnumerateArray()
            .Select(image =>
            {
                var url = GetString(image, "Uri").Trim();
                if (url.StartsWith("//", StringComparison.Ordinal))
                {
                    url = "https:" + url;
                }
                else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url["http://".Length..];
                }
                return new XboxImageMetadata(
                    url,
                    GetString(image, "ImagePurpose"),
                    GetString(image, "Caption"),
                    GetInt(image, "Width"),
                    GetInt(image, "Height"));
            })
            .Where(image =>
                Uri.TryCreate(image.Url, UriKind.Absolute, out var uri) &&
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(requiredPurpose) ||
                 image.Purpose.Equals(requiredPurpose, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(image => image.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(image => image.Width * image.Height)
            .Take(Math.Max(1, take))
            .ToList();
    }

    private static int XboxImagePurposeScore(string purpose) => purpose switch
    {
        "TitledHeroArt" => 100,
        "SuperHeroArt" => 95,
        "FeaturePromotionalWideArt" => 90,
        "Hero" => 85,
        "Screenshot" => 20,
        _ => 0,
    };

    private static string FriendlyXboxFeature(string value) => value switch
    {
        "SinglePlayer" => "Single-player",
        "XblLocalCoop" => "Local co-op",
        "XblOnlineCoop" => "Online co-op",
        "XblCrossPlatformCoop" => "Cross-platform co-op",
        "SharedSplitScreen" => "Split screen",
        "Capability4k" => "4K",
        "CapabilityHDR" => "HDR",
        "CapabilityVRR" => "Variable refresh rate",
        "RayTracing" => "Ray tracing",
        "60fps" => "60 FPS",
        "120fps" => "120 FPS",
        "SpatialSound" => "Spatial sound",
        "DolbyAtmos" => "Dolby Atmos",
        "DTSX" => "DTS:X",
        "PcGamePad" => "Controller support",
        "XboxLive" => "Xbox network",
        _ => string.Empty,
    };

    private static List<string> SingleValueList(string value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];

    private CacheEntry? GetEntry(string identity)
    {
        lock (_cacheGate)
        {
            var cache = LoadCacheLocked();
            return cache.Entries.GetValueOrDefault(identity);
        }
    }

    private void TouchEntry(string identity, DateTimeOffset now)
    {
        lock (_cacheGate)
        {
            var cache = LoadCacheLocked();
            if (cache.Entries.TryGetValue(identity, out var entry))
            {
                cache.Entries[identity] = entry with { LastAccessedAtUtc = now };
            }
        }
    }

    private void SaveEntry(string identity, CacheEntry entry)
    {
        lock (_cacheGate)
        {
            var cache = LoadCacheLocked();
            var now = DateTimeOffset.UtcNow;
            foreach (var expiredKey in cache.Entries
                         .Where(pair =>
                             pair.Value.LastAccessedAtUtc.HasValue &&
                             now - pair.Value.LastAccessedAtUtc.Value > UnusedEntryLifetime)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                cache.Entries.Remove(expiredKey);
            }

            var existing = cache.Entries.GetValueOrDefault(identity);
            cache.Entries[identity] = entry;
            if (existing is not null &&
                existing.ContentHash.Equals(entry.ContentHash, StringComparison.Ordinal) &&
                existing.ShortcutAppId == entry.ShortcutAppId &&
                existing.LastError.Equals(entry.LastError, StringComparison.Ordinal) &&
                existing.RetryAfterUtc == entry.RetryAfterUtc &&
                existing.SourceMatchedAtUtc == entry.SourceMatchedAtUtc &&
                existing.InfoRefreshedAtUtc == entry.InfoRefreshedAtUtc &&
                existing.ActivityRefreshedAtUtc == entry.ActivityRefreshedAtUtc &&
                existing.RefreshedAtUtc == entry.RefreshedAtUtc &&
                existing.AchievementDefinitionsRefreshedAtUtc ==
                entry.AchievementDefinitionsRefreshedAtUtc &&
                existing.AchievementProgressRefreshedAtUtc ==
                entry.AchievementProgressRefreshedAtUtc &&
                existing.AchievementRetryAfterUtc ==
                entry.AchievementRetryAfterUtc &&
                string.Equals(
                    existing.AchievementLastError,
                    entry.AchievementLastError,
                    StringComparison.Ordinal) &&
                string.Equals(
                    existing.AchievementConfigurationFingerprint,
                    entry.AchievementConfigurationFingerprint,
                    StringComparison.Ordinal))
            {
                return;
            }

            SaveCacheLocked(cache);
        }
    }

    private int? GetSteamMatchOverride(string identity)
    {
        lock (_cacheGate)
        {
            var cache = LoadCacheLocked();
            return cache.SteamAppIdOverrides.TryGetValue(identity, out var appId) && appId > 0
                ? appId
                : null;
        }
    }

    private async Task AwaitActiveRefreshBeforeMatchChangeAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        if (!_refreshes.TryGetValue(identity, out var refresh))
        {
            return;
        }
        try
        {
            await refresh.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A failed automatic refresh must not prevent an explicit user fix.
        }
    }

    private void SaveSteamMatchOverride(string identity, int? steamAppId)
    {
        lock (_cacheGate)
        {
            var cache = LoadCacheLocked();
            if (steamAppId is > 0)
            {
                cache.SteamAppIdOverrides[identity] = steamAppId.Value;
            }
            else
            {
                cache.SteamAppIdOverrides.Remove(identity);
            }

            // Metadata, activity and achievement definitions can all be tied to
            // the previous match. Rebuild the game-scoped entry without touching
            // any other title or provider cache.
            cache.Entries.Remove(identity);
            SaveCacheLocked(cache);
        }
    }

    private CacheDocument LoadCacheLocked()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                _cache = JsonSerializer.Deserialize<CacheDocument>(json, JsonOptions);
            }
        }
        catch
        {
            _cache = null;
        }

        _cache ??= new CacheDocument();
        _cache.SchemaVersion = 2;
        _cache.Entries ??=
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        _cache.SteamAppIdOverrides ??=
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _cache.IgnSlugOverrides = _cache.IgnSlugOverrides is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                _cache.IgnSlugOverrides,
                StringComparer.OrdinalIgnoreCase);
        _cache.UserEdits = _cache.UserEdits is null
            ? new Dictionary<string, GameMetadataEdits>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, GameMetadataEdits>(
                _cache.UserEdits,
                StringComparer.OrdinalIgnoreCase);
        if (_cache.Entries.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            _cache.Entries = new Dictionary<string, CacheEntry>(
                _cache.Entries,
                StringComparer.OrdinalIgnoreCase);
        }
        if (_cache.SteamAppIdOverrides.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            _cache.SteamAppIdOverrides = new Dictionary<string, int>(
                _cache.SteamAppIdOverrides,
                StringComparer.OrdinalIgnoreCase);
        }

        _revision = Math.Max(
            _revision,
            _cache.Entries.Values.Select(entry => entry.Revision).DefaultIfEmpty(0).Max());
        return _cache;
    }

    private void SaveCacheLocked(CacheDocument cache)
    {
        var directory = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException("Metadata cache path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_cachePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(cache, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(_cachePath))
            {
                File.Replace(
                    temporaryPath,
                    _cachePath,
                    _cachePath + ".bak",
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _cachePath);
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

    private static bool IsRefreshDue(
        CacheEntry entry,
        DateTimeOffset now,
        string achievementFingerprint,
        bool userScopedAchievements)
    {
        if (entry.RetryAfterUtc is { } retryAfter && now < retryAfter)
        {
            return false;
        }
        var achievementBackoffActive =
            entry.AchievementRetryAfterUtc is { } achievementRetryAfter &&
            now < achievementRetryAfter;
        var achievementDue =
            !string.Equals(
                entry.AchievementConfigurationFingerprint,
                achievementFingerprint,
                StringComparison.Ordinal) ||
            (!achievementBackoffActive &&
             (!entry.AchievementDefinitionsRefreshedAtUtc.HasValue ||
              now - entry.AchievementDefinitionsRefreshedAtUtc.Value >=
              AchievementDefinitionLifetime ||
              (userScopedAchievements &&
               (!entry.AchievementProgressRefreshedAtUtc.HasValue ||
                now - entry.AchievementProgressRefreshedAtUtc.Value >=
                AchievementProgressLifetime))));
        return !entry.InfoRefreshedAtUtc.HasValue ||
               now - entry.InfoRefreshedAtUtc.Value >= GameInfoLifetime ||
               (entry.SourceSteamAppId.HasValue &&
                 (!entry.ActivityRefreshedAtUtc.HasValue ||
                  now - entry.ActivityRefreshedAtUtc.Value >= ActivityLifetime)) ||
               achievementDue;
    }

    private static OmniLibraryGamePageMetadataSnapshot BuildSnapshot(
        CacheEntry entry,
        bool refreshing,
        bool cached)
    {
        var nextRefresh = new[]
            {
                entry.InfoRefreshedAtUtc?.Add(GameInfoLifetime),
                entry.ActivityRefreshedAtUtc?.Add(ActivityLifetime),
                entry.AchievementDefinitionsRefreshedAtUtc?.Add(
                    AchievementDefinitionLifetime),
                entry.AchievementProgressRefreshedAtUtc?.Add(
                    AchievementProgressLifetime),
                entry.AchievementRetryAfterUtc,
            }
            .Where(value => value.HasValue)
            .Min();
        var status = refreshing
            ? "updating"
            : !entry.SourceSteamAppId.HasValue &&
              string.IsNullOrWhiteSpace(entry.MetadataSource)
                ? "unmatched"
                : !string.IsNullOrWhiteSpace(entry.LastError)
                    ? "degraded"
                    : "ready";
        return new OmniLibraryGamePageMetadataSnapshot(
            entry.Revision,
            entry.ShortcutAppId,
            entry.StoreId,
            entry.GameId,
            entry.Title,
            status,
            cached,
            refreshing,
            entry.RefreshedAtUtc,
            entry.RetryAfterUtc ?? nextRefresh,
            entry.SourceSteamAppId,
            !string.IsNullOrWhiteSpace(entry.MetadataSource)
                ? entry.MetadataSource
                : entry.SourceSteamAppId.HasValue
                    ? "Steam"
                    : entry.StoreId,
            entry.MatchMode,
            entry.LastError,
            entry.GameInfo,
            entry.Activity,
            entry.Achievements,
            entry.Community);
    }

    private static OmniLibraryGamePageMetadataSnapshot BuildUnavailableSnapshot(
        UnifySteamGameDetailSnapshot gameDetail,
        string warning)
    {
        var game = gameDetail.Game!;
        return new OmniLibraryGamePageMetadataSnapshot(
            0,
            game.SteamAppId,
            gameDetail.StoreId,
            game.Id,
            game.Title,
            "unavailable",
            false,
            false,
            null,
            DateTimeOffset.UtcNow.Add(FailureBackoff),
            null,
            gameDetail.StoreId,
            "automatic",
            warning,
            EmptyGameInfo(game.Title, null),
            [],
            StoreAchievementPlaceholder(gameDetail.StoreId),
            []);
    }

    private static OmniLibraryGameInfoMetadata EmptyGameInfo(
        string title,
        int? sourceSteamAppId)
    {
        return new OmniLibraryGameInfoMetadata(
            string.Empty,
            string.Empty,
            [],
            [],
            [],
            [],
            string.Empty,
            null,
            string.Empty,
            string.Empty,
            sourceSteamAppId.HasValue
                ? $"https://store.steampowered.com/app/{sourceSteamAppId.Value}/"
                : string.Empty,
            []);
    }

    private static OmniLibraryAchievementMetadata StoreAchievementPlaceholder(string storeId)
    {
        if (IsShortcutStore(storeId))
        {
            return new OmniLibraryAchievementMetadata(
                string.Empty,
                "none",
                string.Empty,
                0,
                0,
                []);
        }

        var storeName = storeId switch
        {
            "xbox-game-pass" => "Xbox",
            "epic-games" => "Epic Games",
            "gog-galaxy" => "GOG",
            _ => "the connected store",
        };
        return new OmniLibraryAchievementMetadata(
            storeName,
            "provider-required",
            $"Achievement progress will appear when {storeName} exposes a verified user-scoped achievement source. TFS never presents Steam achievements as store unlocks.",
            0,
            0,
            []);
    }

    private static string ComputeContentHash(CacheEntry entry)
    {
        var payload = JsonSerializer.Serialize(new
        {
            entry.ShortcutAppId,
            entry.StoreId,
            entry.GameId,
            entry.Title,
            entry.SourceSteamAppId,
            entry.MetadataSource,
            entry.GameInfo,
            entry.Activity,
            entry.Achievements,
            entry.Community,
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string BuildIdentity(string storeId, string gameId) =>
        $"{storeId.Trim().ToLowerInvariant()}:{gameId.Trim().ToLowerInvariant()}";

    private static int ScoreTitleMatch(string requested, string candidate)
    {
        if (requested.Equals(candidate, StringComparison.Ordinal))
        {
            return 0;
        }
        var requestedWithoutEdition = EditionSuffixPattern.Replace(requested, string.Empty).Trim();
        var candidateWithoutEdition = EditionSuffixPattern.Replace(candidate, string.Empty).Trim();
        if (requestedWithoutEdition.Equals(candidateWithoutEdition, StringComparison.Ordinal))
        {
            return 1;
        }
        return -1;
    }

    private static int ScoreSearchResult(string requested, string candidate)
    {
        var exactScore = ScoreTitleMatch(requested, candidate);
        if (exactScore >= 0)
        {
            return exactScore;
        }
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(candidate))
        {
            return 1000;
        }

        var requestedNumbers = ExtractNumbers(requested);
        var candidateNumbers = ExtractNumbers(candidate);
        var numberPenalty = requestedNumbers.SetEquals(candidateNumbers) ? 0 : 100;
        var containmentScore = requested.Contains(candidate, StringComparison.Ordinal) ||
                               candidate.Contains(requested, StringComparison.Ordinal)
            ? 10
            : 30;
        return numberPenalty + containmentScore + Math.Abs(requested.Length - candidate.Length);
    }

    private static (string Language, string Market) GetSteamLocale()
    {
        var culture = CultureInfo.CurrentUICulture;
        var language = culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "de" => "german",
            "fr" => "french",
            "it" => "italian",
            "es" => "spanish",
            "pt" when culture.Name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) => "brazilian",
            "pt" => "portuguese",
            "pl" => "polish",
            "ru" => "russian",
            "uk" => "ukrainian",
            "tr" => "turkish",
            "cs" => "czech",
            "hu" => "hungarian",
            "nl" => "dutch",
            "da" => "danish",
            "fi" => "finnish",
            "no" => "norwegian",
            "sv" => "swedish",
            "ja" => "japanese",
            "ko" => "koreana",
            "zh" when culture.Name.Contains("Hans", StringComparison.OrdinalIgnoreCase) ||
                      culture.Name.EndsWith("CN", StringComparison.OrdinalIgnoreCase) => "schinese",
            "zh" => "tchinese",
            _ => "english",
        };
        var market = "US";
        try
        {
            market = new RegionInfo(culture.Name).TwoLetterISORegionName.ToUpperInvariant();
        }
        catch
        {
        }
        return (language, market);
    }

    private static HashSet<string> ExtractNumbers(string title) =>
        NumberPattern.Matches(title)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string NormalizeTitle(string value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty)
            .Replace("™", string.Empty, StringComparison.Ordinal)
            .Replace("®", string.Empty, StringComparison.Ordinal)
            .Replace("©", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        decoded = WindowsSuffixPattern.Replace(decoded, string.Empty);
        return NonAlphaNumericPattern.Replace(decoded, " ").Trim();
    }

    private static string CleanMarkup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var clean = HtmlTagPattern.Replace(value, " ");
        clean = BbCodePattern.Replace(clean, " ");
        clean = WebUtility.HtmlDecode(clean);
        return Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private static string ExtractImageUrl(string contents)
    {
        var match = ImageUrlPattern.Match(WebUtility.HtmlDecode(contents ?? string.Empty));
        return match.Success ? match.Value : string.Empty;
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var node) &&
               node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var node) &&
        node.TryGetInt32(out var value)
            ? value
            : 0;

    private static List<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ReadDescriptionArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return values.EnumerateArray()
            .Select(value => GetString(value, "description"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FriendlyError(Exception error) => error switch
    {
        HttpRequestException => "The metadata source is temporarily unavailable. Cached data is kept.",
        TaskCanceledException => "The metadata source timed out. Cached data is kept.",
        InvalidDataException invalidData => invalidData.Message,
        _ => "Metadata could not be refreshed. Cached data is kept.",
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\[(?:/?[a-z]+)(?:=[^\]]+)?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BbCodeRegex();

    [GeneratedRegex(@"https://[^\s""'\]]+\.(?:jpg|jpeg|png|webp)(?:\?[^\s""'\]]*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ImageUrlRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\b(?:standard|complete|ultimate|deluxe|definitive|enhanced|remastered|goty|game of the year)\s+edition\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EditionSuffixRegex();

    [GeneratedRegex(@"\s*(?:-|–|—)\s*(?:windows|win10|pc)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsSuffixRegex();

    [GeneratedRegex(@"\d+", RegexOptions.Compiled)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"<div\s+class=[""']achieveRow[^""']*[""'][\s\S]*?<img[^>]+src=[""'](?<icon>[^""']+)[""'][^>]*>[\s\S]*?<h3[^>]*>(?<name>[\s\S]*?)</h3>\s*<h5[^>]*>(?<description>[\s\S]*?)</h5>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AchievementRowRegex();

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; } = 2;

        public Dictionary<string, CacheEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> SteamAppIdOverrides { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Hand-made game info. Kept apart from Entries so a refresh, a new
        // Steam match or cache pruning never loses what the user typed.
        public Dictionary<string, GameMetadataEdits> UserEdits { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // IGN page (slug) the user picked by hand; wins over every other source.
        public Dictionary<string, string> IgnSlugOverrides { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CacheEntry
    {
        public long Revision { get; set; }

        public uint ShortcutAppId { get; init; }

        public string StoreId { get; init; } = string.Empty;

        public string GameId { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public int? SourceSteamAppId { get; init; }

        public string MetadataSource { get; init; } = string.Empty;

        public string MatchMode { get; init; } = "automatic";

        /// <summary>Language the IGN description was translated into ("" = original).</summary>
        public string TranslatedLanguage { get; init; } = string.Empty;

        public DateTimeOffset? SourceMatchedAtUtc { get; init; }

        public DateTimeOffset? InfoRefreshedAtUtc { get; init; }

        public DateTimeOffset? ActivityRefreshedAtUtc { get; init; }

        public DateTimeOffset? AchievementsRefreshedAtUtc { get; init; }

        public DateTimeOffset? AchievementDefinitionsRefreshedAtUtc { get; init; }

        public DateTimeOffset? AchievementProgressRefreshedAtUtc { get; init; }

        public DateTimeOffset? AchievementRetryAfterUtc { get; init; }

        public string AchievementLastError { get; init; } = string.Empty;

        public string AchievementProviderState { get; init; } = string.Empty;

        public string AchievementConfigurationFingerprint { get; init; } = string.Empty;

        public DateTimeOffset? RefreshedAtUtc { get; init; }

        public DateTimeOffset? LastAccessedAtUtc { get; init; }

        public DateTimeOffset? RetryAfterUtc { get; init; }

        public string LastError { get; init; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public OmniLibraryGameInfoMetadata GameInfo { get; init; } =
            EmptyGameInfo(string.Empty, null);

        public List<OmniLibraryActivityMetadata> Activity { get; init; } = [];

        public OmniLibraryAchievementMetadata Achievements { get; init; } =
            StoreAchievementPlaceholder(string.Empty);

        public List<OmniLibraryCommunityMetadata> Community { get; init; } = [];
    }

    private sealed record XboxImageMetadata(
        string Url,
        string Purpose,
        string Caption,
        int Width,
        int Height);
}
