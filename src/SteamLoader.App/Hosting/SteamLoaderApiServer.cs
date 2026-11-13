using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Artwork;
using SteamLoader.App.Infrastructure.AppStart;
using SteamLoader.App.Infrastructure.AutoSisir;
using SteamLoader.App.Infrastructure.Audio;
using SteamLoader.App.Infrastructure.Display;
using SteamLoader.App.Infrastructure.Discord;
using SteamLoader.App.Infrastructure.Curtain;
using SteamLoader.App.Infrastructure.Hltb;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.PluginStore;
using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.SmartHome;
using SteamLoader.App.Infrastructure.Store;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Infrastructure.SystemTools;
using SteamLoader.App.Infrastructure.Themes;
using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.Hosting;

public sealed class SteamLoaderApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan UpdateSnapshotCacheDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AutomaticUpdateStartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AutomaticUpdateCheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan AutomaticUpdateIdlePollInterval = TimeSpan.FromMinutes(2);
    private readonly IAudioOutputDeviceService _audioOutputDeviceService;
    private readonly DisplaySwitchService _displaySwitchService;
    private readonly ProcessWindowService _processWindowService;
    private readonly SteamGridDbManualArtworkService _artworkService;
    private readonly CurtainService _curtainService;
    private readonly AutoSisirService _autoSisirService;
    private readonly AppStartService _appStartService;
    private readonly HltbService _hltbService;
    private readonly StoreService _storeService;
    private readonly StoreSyncService _storeSyncService;
    private readonly OmniLibraryGamePageMetadataService _omniLibraryMetadataService;
    private readonly ThemesService _themesService;
    private readonly TfsPerformanceService _performanceService;
    private readonly HandheldPerformanceService _handheldPerformanceService;
    private readonly SteamLoaderSettingsService _steamLoaderSettingsService;
    private readonly PluginStoreService _pluginStoreService;
    private readonly PowerActionService _powerActionService;
    private readonly ReleaseUpdateService _releaseUpdateService;
    private readonly NvidiaDriverUpdateService _nvidiaDriverUpdateService;
    private readonly WindowsSystemUpdateService _windowsSystemUpdateService;
    private readonly HdrDisplayService _hdrDisplayService;
    private readonly BluetoothDeviceService _bluetoothDeviceService;
    private readonly SteamFrontendComponentService _frontendComponentService;
    private readonly SteamDevToolsClient _devToolsClient;
    private readonly SmartHomeService _smartHomeService;
    private readonly DiscordService _discordService;
    private readonly ExternalGameQuickAccessService _externalGameQuickAccessService;
    private readonly Func<IReadOnlyList<string>> _pressedControllerButtonsProvider;
    private readonly SteamLoaderHostState _hostState;
    private readonly QuickAccessLiveUpdateHub _liveUpdateHub;
    private readonly HttpListener _listener;
    private readonly Action _requestShutdown;
    private readonly string _apiSessionToken;
    private readonly object _artworkOpenRequestLock = new();
    private readonly object _unifyStoreOverlayLock = new();
    private readonly object _storeOverlayLock = new();
    private readonly object _omniLibraryLoginProcessLock = new();
    private readonly Dictionary<string, Process> _omniLibraryLoginProcesses =
        new(StringComparer.OrdinalIgnoreCase);
    private ArtworkOpenRequest? _latestArtworkOpenRequest;
    private DateTimeOffset _latestArtworkOpenRequestAt = DateTimeOffset.MinValue;
    private string _latestArtworkOpenRequestKey = string.Empty;
    private long _artworkOpenRequestNonce;
    private readonly SemaphoreSlim _updateSnapshotGate = new(1, 1);
    private readonly object _updateInstallGate = new();
    private UpdateCheckSnapshot? _cachedUpdateSnapshot;
    private Task? _activeUpdateInstallTask;
    private Task? _acceptLoopTask;
    private Task? _automaticUpdateLoopTask;
    private CancellationTokenSource? _automaticUpdateCts;
    private bool _unifyStoreOverlayOpen;
    private bool _storeOverlayOpen;

    public SteamLoaderApiServer(
        IAudioOutputDeviceService audioOutputDeviceService,
        DisplaySwitchService displaySwitchService,
        ProcessWindowService processWindowService,
        SteamGridDbManualArtworkService artworkService,
        CurtainService curtainService,
        AutoSisirService autoSisirService,
        AppStartService appStartService,
        HltbService hltbService,
        StoreService storeService,
        StoreSyncService storeSyncService,
        OmniLibraryGamePageMetadataService omniLibraryMetadataService,
        ThemesService themesService,
        TfsPerformanceService performanceService,
        HandheldPerformanceService handheldPerformanceService,
        SteamLoaderSettingsService steamLoaderSettingsService,
        PluginStoreService pluginStoreService,
        PowerActionService powerActionService,
        ReleaseUpdateService releaseUpdateService,
        NvidiaDriverUpdateService nvidiaDriverUpdateService,
        WindowsSystemUpdateService windowsSystemUpdateService,
        HdrDisplayService hdrDisplayService,
        BluetoothDeviceService bluetoothDeviceService,
        SteamFrontendComponentService frontendComponentService,
        SteamDevToolsClient devToolsClient,
        SmartHomeService smartHomeService,
        DiscordService discordService,
        ExternalGameQuickAccessService externalGameQuickAccessService,
        Func<IReadOnlyList<string>> pressedControllerButtonsProvider,
        Uri baseUri,
        string apiSessionToken,
        SteamLoaderHostState hostState,
        QuickAccessLiveUpdateHub liveUpdateHub,
        Action requestShutdown)
    {
        _audioOutputDeviceService = audioOutputDeviceService;
        _displaySwitchService = displaySwitchService;
        _processWindowService = processWindowService;
        _artworkService = artworkService;
        _curtainService = curtainService;
        _autoSisirService = autoSisirService;
        _appStartService = appStartService;
        _hltbService = hltbService;
        _storeService = storeService;
        _storeSyncService = storeSyncService;
        _omniLibraryMetadataService = omniLibraryMetadataService;
        _themesService = themesService;
        _performanceService = performanceService;
        _handheldPerformanceService = handheldPerformanceService;
        _steamLoaderSettingsService = steamLoaderSettingsService;
        _pluginStoreService = pluginStoreService;
        _powerActionService = powerActionService;
        _releaseUpdateService = releaseUpdateService;
        _nvidiaDriverUpdateService = nvidiaDriverUpdateService;
        _windowsSystemUpdateService = windowsSystemUpdateService;
        _hdrDisplayService = hdrDisplayService;
        _bluetoothDeviceService = bluetoothDeviceService;
        _frontendComponentService = frontendComponentService;
        _devToolsClient = devToolsClient;
        _smartHomeService = smartHomeService;
        _discordService = discordService;
        _externalGameQuickAccessService = externalGameQuickAccessService;
        _pressedControllerButtonsProvider = pressedControllerButtonsProvider;
        _apiSessionToken = apiSessionToken;
        _hostState = hostState;
        _liveUpdateHub = liveUpdateHub;
        _requestShutdown = requestShutdown;
        _listener = new HttpListener();
        _listener.Prefixes.Add(baseUri.ToString());
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    /// <summary>Frontend bundles served under <c>/frontend/</c> for the Decky-style loader.</summary>
    public FrontendBundleRegistry? FrontendBundles { get; init; }

    /// <summary>TFS Trailers backend, served under <c>/api/trailers/</c>.</summary>
    public Infrastructure.Trailers.TrailerService? Trailers { get; init; }

    /// <summary>TFS Overture backend (game page theme music), served under <c>/api/overture/</c>.</summary>
    public Infrastructure.Overture.OvertureService? Overture { get; init; }

    /// <summary>TFS Artworks page backend, served under <c>/api/artwork/picker/</c>.</summary>
    public Infrastructure.Artwork.ArtworkPickerService? ArtworkPicker { get; init; }

    /// <summary>TFS section in Steam's Performance tab, served under <c>/api/perf-tab/</c>.</summary>
    public Infrastructure.PerfTab.PerfTabService? PerfTab { get; init; }

    /// <summary>TFS rows in Steam's Quick Settings tab, served under <c>/api/quick-settings/</c>.</summary>
    public Infrastructure.PerfTab.QuickSettingsService? QuickSettings { get; init; }

    /// <summary>TFS Popups backend, served under <c>/api/popups/</c>.</summary>
    public Infrastructure.Popups.PopupService? Popups { get; init; }

    /// <summary>Discord Web input bridge, served under <c>/api/discord/web/</c>.</summary>
    public DiscordWebBridge? DiscordWeb { get; init; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(cancellationToken), cancellationToken);
        _automaticUpdateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var automaticUpdateToken = _automaticUpdateCts.Token;
        _automaticUpdateLoopTask = Task.Run(() => AutomaticUpdateLoopAsync(automaticUpdateToken));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_acceptLoopTask is not null)
        {
            await _acceptLoopTask;
        }

        if (_automaticUpdateCts is not null)
        {
            _automaticUpdateCts.Cancel();
            if (_automaticUpdateLoopTask is not null)
            {
                await _automaticUpdateLoopTask;
            }

            _automaticUpdateCts.Dispose();
            _automaticUpdateCts = null;
            _automaticUpdateLoopTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _listener.Close();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        response.Headers["Cache-Control"] = "no-store";

        var origin = request.Headers["Origin"];
        if (!LocalApiSession.IsTrustedOrigin(origin))
        {
            await WriteJsonAsync(
                response,
                HttpStatusCode.Forbidden,
                new { message = "This origin is not allowed to access the local Tools for Steam API." },
                cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = $"Content-Type, {LocalApiSession.HeaderName}";
        }

        if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.Close();
            return;
        }

        var requestPath = request.Url?.AbsolutePath;
        if (!LocalApiSession.IsPublicResourceRequest(request.HttpMethod, requestPath) &&
            !LocalApiSession.IsAuthorized(
                _apiSessionToken,
                request.Headers[LocalApiSession.HeaderName],
                request.QueryString[LocalApiSession.QueryName],
                request.HttpMethod))
        {
            await WriteJsonAsync(
                response,
                HttpStatusCode.Unauthorized,
                new { message = "A valid local Tools for Steam session is required." },
                cancellationToken);
            return;
        }

        try
        {
            if (StorefrontFeatureFlags.IsDisabledRequestPath(request.Url?.AbsolutePath))
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.NotFound,
                    new { message = "OmniLibrary is disabled in this build." },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                FrontendBundleRegistry.TryParseRequestPath(request.Url?.AbsolutePath, out var frontendFileName))
            {
                if (FrontendBundles?.TryGet(frontendFileName, out var frontendBundle) != true)
                {
                    await WriteTextAsync(response, HttpStatusCode.NotFound, "Unknown frontend bundle.", cancellationToken);
                    return;
                }

                // The URL carries the content hash, so a matching request can be
                // cached for good; anything else must revalidate.
                var cacheControl = string.Equals(
                    request.QueryString["v"],
                    frontendBundle!.Version,
                    StringComparison.OrdinalIgnoreCase)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
                await WriteBytesAsync(
                    response,
                    HttpStatusCode.OK,
                    Encoding.UTF8.GetBytes(frontendBundle.Body),
                    "text/javascript; charset=utf-8",
                    cancellationToken,
                    cacheControl);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/health")
            {
                await WriteTextAsync(response, HttpStatusCode.OK, "ok", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/control/status")
            {
                await WriteJsonAsync(response, HttpStatusCode.OK, _hostState.Snapshot(), cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/events")
            {
                await WriteEventStreamAsync(
                    response,
                    ParseLiveUpdateTopics(request.QueryString["topics"]),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/frontend/components")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _frontendComponentService.GetSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/control/shutdown")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { message = "Shutdown requested." },
                    cancellationToken);

                _requestShutdown();
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath is "/api/control/steam-menu" or "/api/control/quick-access")
            {
                var quickAccess = request.Url.AbsolutePath.EndsWith(
                    "/quick-access",
                    StringComparison.OrdinalIgnoreCase);
                var openedDirectly = quickAccess
                    ? await _devToolsClient.TryOpenQuickAccessMenuAsync(cancellationToken)
                    : await _devToolsClient.TryOpenSteamMenuAsync(cancellationToken);
                if (!openedDirectly)
                {
                    await _devToolsClient.SendControlDigitShortcutAsync(
                        quickAccess ? 2 : 1,
                        cancellationToken);
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { handled = true, openedDirectly },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath is "/api/control/game-overlay" or "/api/control/game-quick-access")
            {
                var quickAccess = request.Url.AbsolutePath.EndsWith(
                    "/game-quick-access",
                    StringComparison.OrdinalIgnoreCase);
                var openedDirectly = await _devToolsClient.TryOpenInGameOverlayAsync(
                    quickAccess,
                    cancellationToken);

                await WriteJsonAsync(
                    response,
                    openedDirectly ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                    new { handled = openedDirectly, openedDirectly },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/steam/keyboard/show")
            {
                var payload = await JsonSerializer.DeserializeAsync<ShowSteamKeyboardRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                var result = await ShowSteamKeyboardAsync(payload, cancellationToken);
                await WriteJsonAsync(
                    response,
                    result.Success ? HttpStatusCode.OK : HttpStatusCode.BadGateway,
                    result,
                    cancellationToken);
                return;
            }

            if (TryResolvePluginId(request.Url?.AbsolutePath, out var pluginId) &&
                !_steamLoaderSettingsService.IsPluginEnabled(pluginId))
            {
                await WriteDisabledPluginResponseAsync(
                    response,
                    request.Url?.AbsolutePath,
                    pluginId,
                    cancellationToken);
                return;
            }

            if (await TrailerApiEndpoints.TryHandleAsync(request, response, Trailers, cancellationToken))
            {
                return;
            }

            if (await OvertureApiEndpoints.TryHandleAsync(request, response, Overture, cancellationToken))
            {
                return;
            }

            if (await IntroVideoApiEndpoints.TryHandleAsync(request, response, _steamLoaderSettingsService, cancellationToken))
            {
                return;
            }

            if (await GameMetadataApiEndpoints.TryHandleAsync(
                    request,
                    response,
                    _omniLibraryMetadataService,
                    cancellationToken))
            {
                return;
            }

            if (await ArtworkPickerApiEndpoints.TryHandleAsync(request, response, ArtworkPicker, cancellationToken))
            {
                return;
            }

            if (await PerfTabApiEndpoints.TryHandleAsync(request, response, PerfTab, cancellationToken))
            {
                return;
            }

            if (await PerfTabApiEndpoints.TryHandleQuickSettingsAsync(request, response, QuickSettings, cancellationToken))
            {
                return;
            }

            if (await PopupApiEndpoints.TryHandleAsync(request, response, Popups, cancellationToken))
            {
                return;
            }

            if (await DiscordWebApiEndpoints.TryHandleAsync(request, response, DiscordWeb, cancellationToken))
            {
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _curtainService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/game-enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetCurtainGameEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null || payload.AppId == 0)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Steam app id and enabled state are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _curtainService.SetGameEnabled(payload.AppId, payload.Enabled),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/launch")
            {
                var payload = await JsonSerializer.DeserializeAsync<ShowCurtainRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null || payload.AppId == 0)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Steam app id is required." },
                        cancellationToken);
                    return;
                }

                // Play on an OmniLibrary game that is not installed starts
                // nothing (downloads run from the game page Download row), so
                // there is no launch to cover.
                if (IsUninstalledOmniLibraryShortcut(payload.AppId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.Conflict,
                        new CurtainLaunchResult(
                            false,
                            "This OmniLibrary game is not installed.",
                            _curtainService.GetSnapshot()),
                        cancellationToken);
                    return;
                }

                // The launch must not die with the request: Steam's frontend
                // fires it and moves on.
                var result = await _curtainService.ShowAsync(
                    payload.AppId,
                    payload.Title,
                    payload.Reason,
                    payload.BigPicture ?? true,
                    CancellationToken.None);
                await WriteJsonAsync(
                    response,
                    result.Shown ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                    result,
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/confirm")
            {
                var payload = await JsonSerializer.DeserializeAsync<CurtainAppRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _curtainService.Confirm(payload?.AppId ?? 0),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/dismiss")
            {
                CurtainDismissRequest? payload = null;
                if (request.HasEntityBody)
                {
                    try
                    {
                        payload = await JsonSerializer.DeserializeAsync<CurtainDismissRequest>(
                            request.InputStream,
                            JsonOptions,
                            cancellationToken);
                    }
                    catch (JsonException)
                    {
                        payload = null;
                    }
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _curtainService.DismissAsync(payload?.Reason),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/settings")
            {
                var payload = await JsonSerializer.DeserializeAsync<CurtainGlobalUpdate>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "Curtain settings are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(response, HttpStatusCode.OK, _curtainService.SaveGlobal(payload), cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/game")
            {
                if (!uint.TryParse(request.QueryString["appId"], out var curtainAppId) || curtainAppId == 0)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Steam app id is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(response, HttpStatusCode.OK, _curtainService.GetGame(curtainAppId), cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                (request.Url?.AbsolutePath == "/api/curtain/game" ||
                 request.Url?.AbsolutePath == "/api/curtain/game/reset"))
            {
                var payload = await JsonSerializer.DeserializeAsync<SaveCurtainGameRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null || payload.AppId == 0)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Steam app id is required." },
                        cancellationToken);
                    return;
                }

                var details = request.Url.AbsolutePath.EndsWith("/reset", StringComparison.Ordinal)
                    ? _curtainService.ResetGame(payload.AppId)
                    : _curtainService.SaveGame(payload.AppId, payload.Settings ?? new CurtainGameSettings(), payload.Enabled);
                await WriteJsonAsync(response, HttpStatusCode.OK, details, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/curtain/image")
            {
                _ = uint.TryParse(request.QueryString["appId"], out var imageAppId);
                var curtainImagePath = _curtainService.ResolveImagePath(
                    imageAppId,
                    request.QueryString["kind"] ?? "background",
                    request.QueryString["source"]);
                if (string.IsNullOrWhiteSpace(curtainImagePath) || !File.Exists(curtainImagePath))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.NotFound,
                        new { message = "No curtain image is available." },
                        cancellationToken);
                    return;
                }

                // The URL carries a content version (v=), so the image may be cached.
                await WriteFileAsync(
                    response,
                    curtainImagePath,
                    Path.GetExtension(curtainImagePath).ToLowerInvariant() switch
                    {
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        ".gif" => "image/gif",
                        ".bmp" => "image/bmp",
                        _ => "image/jpeg"
                    },
                    cancellationToken,
                    string.IsNullOrEmpty(request.QueryString["v"]) ? "no-cache, no-store" : "private, max-age=86400");
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _artworkService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.GetSnapshotAsync(forceRefresh: false, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/refresh")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.GetSnapshotAsync(forceRefresh: true, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.GetSnapshotAsync(forceRefresh: false, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/control/external-game-quick-access")
            {
                var target = request.HasEntityBody
                    ? await JsonSerializer.DeserializeAsync<ExternalGameQuickAccessTarget>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken)
                    : null;
                var handled = await _externalGameQuickAccessService.TryOpenForForegroundGameAsync(
                    cancellationToken,
                    target);
                await WriteJsonAsync(
                    response,
                    handled ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                    new { handled, state = _externalGameQuickAccessService.GetState() },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/external-game-quick-access/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _externalGameQuickAccessService.GetState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/external-game-quick-access/close-game")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _externalGameQuickAccessService.CloseCurrentGameAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/external-game-quick-access/return-game")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _externalGameQuickAccessService.ReturnToGame(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/refresh")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.GetSnapshotAsync(forceRefresh: true, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/widget/refresh")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.GetWidgetFallbackSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/connect")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.ConnectAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/disconnect")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.DisconnectAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/guild/select")
            {
                var payload = await JsonSerializer.DeserializeAsync<DiscordIdRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.SelectGuildAsync(payload?.Id, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/guild/favorite")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetDiscordGuildFavoriteRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Discord server favorite setting is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.SetGuildFavoriteAsync(
                        payload.Id,
                        payload.Favorite,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/settings/friend-online-notifications")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A friend-online notification setting is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.SetFriendOnlineNotificationsAsync(
                        payload.Value,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/voice/join")
            {
                var payload = await JsonSerializer.DeserializeAsync<DiscordIdRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.JoinVoiceChannelAsync(payload?.Id, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/guild/open")
            {
                var payload = await JsonSerializer.DeserializeAsync<DiscordIdRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var target = await _discordService.OpenGuildAsync(payload?.Id, cancellationToken);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { message = "Opened the Discord server.", target },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/settings")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetDiscordSettingsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "Discord settings are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.SaveSettingsAsync(
                        payload.ApplicationId,
                        payload.ServerId,
                        payload.InviteUrl,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/settings/clear")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _discordService.ClearSettingsAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/discord/open")
            {
                var inviteUrl = await _discordService.OpenServerAsync(cancellationToken);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { message = "Opened the Discord server invite.", inviteUrl },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/settings/homey/base-url")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.SetHomeyBaseUrlAsync(payload?.Value, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/settings/homey/homey-id")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.SetHomeyHomeyIdAsync(payload?.Value, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/settings/homey/session-token")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.SetHomeySessionTokenAsync(payload?.Value, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/settings/homey/session-token/clear")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.ClearHomeySessionTokenAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/devices/capability")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetSmartHomeCapabilityRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.DeviceId) ||
                    string.IsNullOrWhiteSpace(payload.CapabilityId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A device id and capability id are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.SetDeviceCapabilityAsync(
                        payload.DeviceId,
                        payload.CapabilityId,
                        payload.Value,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/flows/run")
            {
                var payload = await JsonSerializer.DeserializeAsync<RunSmartHomeFlowRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.FlowId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Homey flow id is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.TriggerFlowAsync(
                        payload.FlowId,
                        payload.IsAdvanced,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/smart-home/moods/apply")
            {
                var payload = await JsonSerializer.DeserializeAsync<RunSmartHomeMoodRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.MoodId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Homey mood id is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _smartHomeService.ApplyMoodAsync(
                        payload.MoodId,
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/settings/toggle")
            {
                var payload = await JsonSerializer.DeserializeAsync<ToggleSettingRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Key))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A setting key is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _artworkService.ToggleSetting(payload.Key),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/settings/api-key")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An API key value is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _artworkService.SetApiKey(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/settings/api-key/clear")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _artworkService.ClearApiKey(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/settings/steam-path")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Steam path value is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _artworkService.SetSteamPath(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/settings/steam-path/clear")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _artworkService.ClearSteamPath(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/settings/result-limit")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetIntegerValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A result limit is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _artworkService.SetResultLimit(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/open-request")
            {
                var payload = await JsonSerializer.DeserializeAsync<RequestArtworkOpenRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                var normalizedAppId = payload is null ? 0 : NormalizeSteamAppId(payload.AppId);
                if (payload is null || normalizedAppId <= 0)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Steam app id is required." },
                        cancellationToken);
                    return;
                }

                if (!_artworkService.IsContextMenuEnabled())
                {
                    await WriteJsonAsync(response, HttpStatusCode.OK, new ArtworkOpenRequest(0, 0, string.Empty), cancellationToken);
                    return;
                }

                var title = string.IsNullOrWhiteSpace(payload.Title) ? "Selected Game" : payload.Title.Trim();
                var requestKey = $"{normalizedAppId}:{title.ToLowerInvariant()}";
                var now = DateTimeOffset.UtcNow;
                ArtworkOpenRequest openRequest;

                lock (_artworkOpenRequestLock)
                {
                    if (
                        _latestArtworkOpenRequest is not null &&
                        string.Equals(_latestArtworkOpenRequestKey, requestKey, StringComparison.Ordinal) &&
                        now - _latestArtworkOpenRequestAt < TimeSpan.FromMilliseconds(1600))
                    {
                        openRequest = _latestArtworkOpenRequest;
                    }
                    else
                    {
                        openRequest = new ArtworkOpenRequest(
                            Interlocked.Increment(ref _artworkOpenRequestNonce),
                            normalizedAppId,
                            title);

                        _latestArtworkOpenRequest = openRequest;
                        _latestArtworkOpenRequestKey = requestKey;
                        _latestArtworkOpenRequestAt = now;
                    }
                }

                await WriteJsonAsync(response, HttpStatusCode.OK, openRequest, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/open-request")
            {
                _ = long.TryParse(request.QueryString["after"], out var afterNonce);
                var openRequest = _latestArtworkOpenRequest;
                var requestIsFresh = DateTimeOffset.UtcNow - _latestArtworkOpenRequestAt < TimeSpan.FromSeconds(8);

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    requestIsFresh && openRequest is not null && openRequest.Nonce > afterNonce
                        ? openRequest
                        : new ArtworkOpenRequest(0, 0, string.Empty),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/search")
            {
                var term = request.QueryString["term"] ?? string.Empty;
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _artworkService.SearchGamesAsync(term, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/assets")
            {
                var gameIdValue = request.QueryString["gameId"];
                var assetType = request.QueryString["type"] ?? "grid_p";
                var pageValue = request.QueryString["page"];
                _ = int.TryParse(pageValue, out var page);

                if (!int.TryParse(gameIdValue, out var gameId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid SteamGridDB game id is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _artworkService.SearchAssetsAsync(gameId, assetType, page, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/artwork/apply")
            {
                var payload = await JsonSerializer.DeserializeAsync<ApplyArtworkRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                var normalizedAppId = payload is null ? 0 : NormalizeSteamAppId(payload.AppId);
                if (payload is null ||
                    normalizedAppId <= 0 ||
                    string.IsNullOrWhiteSpace(payload.AssetType) ||
                    string.IsNullOrWhiteSpace(payload.Url))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Steam app id, artwork type, and URL are required." },
                        cancellationToken);
                    return;
                }

                var result = await _artworkService.ApplyAssetAsync(
                    normalizedAppId,
                    payload.AssetType,
                    payload.Url,
                    cancellationToken);

                await WriteJsonAsync(
                    response,
                    result.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                    result,
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/state")
            {
                var snapshot = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.GetDashboardSnapshot(),
                    cancellationToken);

                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/devices")
            {
                var snapshot = await GetAudioDashboardSnapshotAsync(cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    snapshot.PlaybackDevices,
                    "audio.dashboard",
                    snapshot,
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/default")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetDefaultDeviceRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "Device ID is required." },
                        cancellationToken);
                    return;
                }

                await StaThread.RunAsync(
                    () =>
                    {
                        _audioOutputDeviceService.SetDefaultPlaybackDevice(payload.DeviceId);
                        return true;
                    },
                    cancellationToken);

                var devices = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.GetPlaybackDevices(),
                    cancellationToken);

                await WriteJsonAsync(response, HttpStatusCode.OK, devices, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/default-capture")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetDefaultDeviceRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "Device ID is required." },
                        cancellationToken);
                    return;
                }

                await StaThread.RunAsync(
                    () =>
                    {
                        _audioOutputDeviceService.SetDefaultCaptureDevice(payload.DeviceId);
                        return true;
                    },
                    cancellationToken);

                var snapshot = await GetAudioDashboardSnapshotAsync(cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    snapshot.CaptureDevices,
                    "audio.dashboard",
                    snapshot,
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/volume")
            {
                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.GetDefaultPlaybackVolume(),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    volumeInfo,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/capture/volume")
            {
                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.GetDefaultCaptureVolume(),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    volumeInfo,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/volume")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetVolumeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || double.IsNaN(payload.Volume) || double.IsInfinity(payload.Volume))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid volume value is required." },
                        cancellationToken);
                    return;
                }

                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.SetDefaultPlaybackVolume(payload.Volume),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    volumeInfo,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/capture/volume")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetVolumeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || double.IsNaN(payload.Volume) || double.IsInfinity(payload.Volume))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid volume value is required." },
                        cancellationToken);
                    return;
                }

                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.SetDefaultCaptureVolume(payload.Volume),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    volumeInfo,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/volume/adjust")
            {
                var payload = await JsonSerializer.DeserializeAsync<AdjustVolumeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || double.IsNaN(payload.Delta) || double.IsInfinity(payload.Delta))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid volume delta is required." },
                        cancellationToken);
                    return;
                }

                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.AdjustDefaultPlaybackVolume(payload.Delta),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    volumeInfo,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/capture/volume/adjust")
            {
                var payload = await JsonSerializer.DeserializeAsync<AdjustVolumeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || double.IsNaN(payload.Delta) || double.IsInfinity(payload.Delta))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid volume delta is required." },
                        cancellationToken);
                    return;
                }

                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.AdjustDefaultCaptureVolume(payload.Delta),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    volumeInfo,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/volume/toggle-mute")
            {
                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.ToggleDefaultPlaybackMute(),
                    cancellationToken);

                await WriteJsonAsync(response, HttpStatusCode.OK, volumeInfo, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/capture/volume/toggle-mute")
            {
                var volumeInfo = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.ToggleDefaultCaptureMute(),
                    cancellationToken);

                await WriteJsonAsync(response, HttpStatusCode.OK, volumeInfo, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/mixer")
            {
                var sessions = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.GetActiveMixerSessions(),
                    cancellationToken);

                await WriteJsonAsync(response, HttpStatusCode.OK, sessions, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/mixer/session/volume")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetAudioMixerSessionVolumeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.SessionId) ||
                    double.IsNaN(payload.Volume) ||
                    double.IsInfinity(payload.Volume))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid session ID and volume value are required." },
                        cancellationToken);
                    return;
                }

                var session = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.SetMixerSessionVolume(payload.SessionId, payload.Volume),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    session,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/audio/mixer/session/toggle-mute")
            {
                var payload = await JsonSerializer.DeserializeAsync<ToggleAudioMixerSessionRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid session ID is required." },
                        cancellationToken);
                    return;
                }

                var session = await StaThread.RunAsync(
                    () => _audioOutputDeviceService.ToggleMixerSessionMute(payload.SessionId),
                    cancellationToken);

                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    session,
                    "audio.dashboard",
                    await GetAudioDashboardSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _steamLoaderSettingsService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/driver/nvidia/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _nvidiaDriverUpdateService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/driver/nvidia/update")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _nvidiaDriverUpdateService.LaunchAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/driver/nvidia/game-ready/check")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _nvidiaDriverUpdateService.CheckGameReadyAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/driver/nvidia/game-ready/install")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _nvidiaDriverUpdateService.StartGameReadyInstallAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/driver/nvidia/restart-steam")
            {
                if (!_nvidiaDriverUpdateService.GetSnapshot().SteamRestartRequired)
                {
                    throw new InvalidOperationException(
                        "Steam restart is available only after a completed NVIDIA driver update.");
                }

                _powerActionService.RestartSteamAfterDriverUpdate();
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _nvidiaDriverUpdateService.AcknowledgeSteamRestart(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/driver/nvidia/game-ready/silent")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _nvidiaDriverUpdateService.LaunchSilentGameReadyAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/windows-update/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _windowsSystemUpdateService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/windows-update/scan")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _windowsSystemUpdateService.ScanAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/windows-update/run")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _windowsSystemUpdateService.RunWindowsUpdate(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/hdr/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _hdrDisplayService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/hdr/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken)
                    ?? throw new InvalidOperationException("The HDR state is missing.");
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _hdrDisplayService.SetEnabled(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/bluetooth/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _bluetoothDeviceService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/bluetooth/refresh")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _bluetoothDeviceService.RefreshAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/bluetooth/scan")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _bluetoothDeviceService.StartScanAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/bluetooth/settings")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _bluetoothDeviceService.OpenSettings(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/system/bluetooth/pair")
            {
                var payload = await JsonSerializer.DeserializeAsync<BluetoothPairRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken)
                    ?? throw new InvalidOperationException("The Bluetooth device is missing.");
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _bluetoothDeviceService.PairAsync(payload.DeviceId, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-activator/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _pluginStoreService.GetSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.StartsWith("/api/plugin-activator/images/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var imagePluginId = Uri.UnescapeDataString(
                    request.Url.AbsolutePath["/api/plugin-activator/images/".Length..]);
                if (!_pluginStoreService.TryGetBuiltInImage(imagePluginId, out var imagePath, out var contentType))
                {
                    await WriteTextAsync(
                        response,
                        HttpStatusCode.NotFound,
                        "Plugin Activator image not found.",
                        cancellationToken);
                    return;
                }

                await WriteFileAsync(response, imagePath, contentType, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-activator/overlay/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.GetOverlayState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-activator/overlay/open")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.SetOverlayOpen(true),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-activator/overlay/close")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.SetOverlayOpen(false),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-activator/overlay/input")
            {
                _ = long.TryParse(request.QueryString["after"], out var afterNonce);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.GetOverlayInputs(afterNonce),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-activator/overlay/input")
            {
                var payload = await JsonSerializer.DeserializeAsync<PluginStoreInputRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Action))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A Plugin Activator input action is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.AddOverlayInput(payload.Action, payload.Source),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/autostart")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A boolean value is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetRunOnWindowsSignIn(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/startup-mode")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStartupModeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Mode))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A startup mode is required." },
                        cancellationToken);
                    return;
                }

                if (!SteamLoaderSettingsService.IsSupportedStartupMode(payload.Mode))
                {
                    // Xbox Mode was removed; an overlay script from an older
                    // build may still offer it until Steam restarts.
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "Tools for Steam supports Console Mode and eTray startup only. Shell Takeover and Xbox Mode were removed." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetStartupMode(payload.Mode);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/hide-windows-shell")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A boolean value is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetHideWindowsShellInConsoleMode(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/developer-debug")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A boolean value is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetDeveloperDebugEnabled(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/gamebar-automation")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A boolean value is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetGameBarControllerAutomation(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/theme/wallpaper")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A boolean value is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetWallpaperEnabled(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/theme/animations")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A boolean value is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetAnimationsEnabled(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/theme/accent-color")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An accent color is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetAccentColor(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/splash/artwork-mode")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A splash artwork mode is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetSplashScreenArtworkMode(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/splash/custom-image")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A custom image path is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetSplashScreenCustomImagePath(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/overlay/input-state")
            {
                // Only the Overlay recorder polls this: keep the shortcut
                // pollers quiet while it listens.
                ControllerShortcutCaptureGate.MarkActive();
                var pressedButtons = _pressedControllerButtonsProvider();
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { buttons = pressedButtons },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/overlay/button")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetOverlayButtonRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Key) || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An overlay context and controller button are required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetControllerShortcutButton(
                    payload.Key,
                    payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/overlay/combination")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetOverlayCombinationRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Action) || payload.Buttons is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An overlay action and its controller-button combination are required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetControllerShortcutCombination(
                    payload.Action,
                    payload.Buttons);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/overlay/hold-time")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetOverlayHoldTimeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Key))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An overlay action and hold time are required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetControllerShortcutHoldMilliseconds(
                    payload.Key,
                    payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/overlay/reset")
            {
                var settingsSnapshot = _steamLoaderSettingsService.ResetControllerShortcutSettings();
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/splash/select-custom-image")
            {
                var selectedPath = await StaThread.RunAsync(
                    () =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "Choose a custom splash image",
                            Filter = "Image files (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
                            CheckFileExists = true,
                            DereferenceLinks = true,
                            Multiselect = false
                        };

                        return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
                    },
                    cancellationToken);

                var settingsSnapshot = string.IsNullOrWhiteSpace(selectedPath)
                    ? _steamLoaderSettingsService.GetSnapshot()
                    : _steamLoaderSettingsService.SetSplashScreenCustomImagePath(selectedPath);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                (request.Url?.AbsolutePath == "/api/settings/windows-shell-start-delay" ||
                 request.Url?.AbsolutePath == "/api/settings/splash/extra-delay"))
            {
                var payload = await JsonSerializer.DeserializeAsync<SetIntegerValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A delay value is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetWindowsShellStartDelaySeconds(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            // TFS Popups size (Quick Access plugin "TFS Popups"), kept as the
            // general notification size setting.
            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/notification-scale")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetIntegerValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A notification size in percent is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetNotificationScalePercent(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/plugins/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetPluginEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.PluginId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A plugin ID is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetPluginEnabled(payload.PluginId, payload.Enabled);
                if (payload.PluginId.Equals("omnilibrary", StringComparison.OrdinalIgnoreCase) ||
                    payload.PluginId.Equals("store-sync", StringComparison.OrdinalIgnoreCase))
                {
                    _storeSyncService.SetOmniLibraryPluginRuntimeEnabled(
                        _steamLoaderSettingsService.IsPluginEnabled("omnilibrary"));
                    _storeSyncService.SetStoreSyncPluginRuntimeEnabled(
                        _steamLoaderSettingsService.IsPluginEnabled("store-sync"));
                }
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/plugins/order")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetPluginOrderRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload?.PluginIds is null || payload.PluginIds.Count == 0)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "At least one plugin ID is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetPluginOrder(payload.PluginIds);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/open-manager")
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    throw new InvalidOperationException("Tools for Steam manager path could not be resolved.");
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = SteamLoaderRuntime.ManagerArgument,
                    UseShellExecute = true,
                })?.Dispose();

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _steamLoaderSettingsService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/updates/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await GetUpdateSnapshotAsync(forceRefresh: false, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/updates/check")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await GetUpdateSnapshotAsync(forceRefresh: true, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/updates/install")
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    throw new InvalidOperationException("Tools for Steam path could not be resolved for the update.");
                }

                var updateSnapshot = await BeginBackgroundUpdateInstallAsync(
                    executablePath,
                    cancellationToken);

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    updateSnapshot,
                    cancellationToken);

                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/splash/preview")
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    throw new InvalidOperationException("Tools for Steam preview path could not be resolved.");
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = $"{SteamLoaderRuntime.PreviewSplashArgument} {SteamLoaderRuntime.PreviewSplashDurationArgument}=5",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.Dispose();

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _steamLoaderSettingsService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/auto-sisr/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _autoSisirService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/auto-sisr/settings/toggle")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A setting key is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _autoSisirService.ToggleSetting(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/auto-sisr/path")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A path value is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _autoSisirService.SetExecutablePath(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/auto-sisr/path/reset")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _autoSisirService.ResetExecutablePath(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/auto-sisr/titles/toggle")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A title id is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _autoSisirService.ToggleWatchedTitle(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/display/modes")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _displaySwitchService.GetModeSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/display/resolution")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A resolution preset is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _displaySwitchService.SetResolutionPreset(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/display/refresh-rate")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetIntegerValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A refresh rate is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _displaySwitchService.SetRefreshRatePreset(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/processes/windows")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _processWindowService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/processes/activate")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A window handle is required." },
                        cancellationToken);
                    return;
                }

                var processesSnapshot = _processWindowService.ActivateWindow(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    processesSnapshot,
                    "processes.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/app-start/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _appStartService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/app-start/catalog")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _appStartService.GetCatalog(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/app-start/catalog/refresh")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _appStartService.RefreshCatalog(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/app-start/apps/add")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An app ID is required." },
                        cancellationToken);
                    return;
                }

                var appStartSnapshot = _appStartService.AddShortcut(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    appStartSnapshot,
                    "app-start.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/app-start/apps/launch")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An app shortcut ID is required." },
                        cancellationToken);
                    return;
                }

                var appStartSnapshot = _appStartService.LaunchShortcut(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    appStartSnapshot,
                    "app-start.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/app-start/apps/remove")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An app shortcut ID is required." },
                        cancellationToken);
                    return;
                }

                var appStartSnapshot = _appStartService.RemoveShortcut(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    appStartSnapshot,
                    "app-start.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/app-start/apps/favorite")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An app ID is required." },
                        cancellationToken);
                    return;
                }

                var appStartSnapshot = _appStartService.ToggleFavorite(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    appStartSnapshot,
                    "app-start.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/hltb/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _hltbService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/hltb/game")
            {
                var title = request.QueryString["title"] ?? string.Empty;
                var appIdValue = request.QueryString["appId"];
                int? appId = int.TryParse(appIdValue, out var parsedAppId) && parsedAppId > 0
                    ? parsedAppId
                    : null;

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _hltbService.GetGameAsync(title, appId, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/artwork")
            {
                var source = request.QueryString["source"] ?? string.Empty;
                var artwork = await _storeService.GetCachedArtworkAsync(source, cancellationToken);
                if (artwork is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.NotFound,
                        new { message = "Artwork is unavailable from the trusted image sources." },
                        cancellationToken);
                    return;
                }

                await WriteFileAsync(
                    response,
                    artwork.Path,
                    artwork.ContentType,
                    cancellationToken,
                    "public, max-age=31536000, immutable");
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/state")
            {
                var forceRefresh = bool.TryParse(request.QueryString["refresh"], out var parsedRefresh) && parsedRefresh;
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _storeService.GetSnapshotAsync(forceRefresh, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/refresh")
            {
                var snapshot = await _storeService.RefreshAsync(cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    snapshot,
                    "store.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/offers")
            {
                var gameId = request.QueryString["gameId"] ?? string.Empty;
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _storeService.GetOffersAsync(gameId, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/search")
            {
                var query = request.QueryString["q"] ?? string.Empty;
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _storeService.SearchAsync(query, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/wishlist")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreWishlistRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload?.Game is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A store game is required." },
                        cancellationToken);
                    return;
                }

                var snapshot = _storeService.SetLocalWishlist(payload.Game, payload.Enabled);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    snapshot,
                    "store.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/wishlist/metadata")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreWishlistMetadataRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { message = "Wishlist metadata is required." }, cancellationToken);
                    return;
                }

                var snapshot = _storeService.SetWishlistMetadata(payload.GameId, payload.IsPinned, payload.Tags);
                await WriteJsonAndPublishAsync(response, HttpStatusCode.OK, snapshot, "store.state", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/wishlist/bulk")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreWishlistBulkRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { message = "Wishlist bulk action details are required." }, cancellationToken);
                    return;
                }

                var snapshot = _storeService.ApplyWishlistBulkAction(
                    payload.GameIds,
                    payload.IsPinned,
                    payload.AddTag,
                    payload.RemoveLocal,
                    payload.AlertMultiplier,
                    payload.AlertCurrencyCode);
                await WriteJsonAndPublishAsync(response, HttpStatusCode.OK, snapshot, "store.state", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/wishlist/seen")
            {
                var snapshot = _storeService.MarkWishlistChangesSeen();
                await WriteJsonAndPublishAsync(response, HttpStatusCode.OK, snapshot, "store.state", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/backup")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { json = _storeService.ExportBackup() },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/backup/import")
            {
                var payload = await JsonSerializer.DeserializeAsync<ImportStoreBackupRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _storeService.ImportBackup(payload?.Json ?? string.Empty);
                await WriteJsonAndPublishAsync(response, HttpStatusCode.OK, snapshot, "store.state", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/alerts")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePriceAlertRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "Price alert details are required." },
                        cancellationToken);
                    return;
                }

                var snapshot = _storeService.SetAlert(
                    payload.SteamAppId,
                    payload.GameId,
                    payload.Title,
                    payload.TargetPrice,
                    payload.CurrencyCode,
                    payload.Enabled,
                    payload.Mode,
                    payload.TargetDiscountPercent,
                    payload.SnoozedUntilUtc);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    snapshot,
                    "store.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/settings/preferences")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePreferencesRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _storeService.SetStorePreferences(
                    payload?.IncludeKeyshops,
                    payload?.RefreshIntervalMinutes,
                    payload?.NotificationsEnabled);
                await WriteJsonAndPublishAsync(response, HttpStatusCode.OK, snapshot, "store.state", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/settings/cache")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreArtworkCacheRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { message = "Artwork cache settings are required." }, cancellationToken);
                    return;
                }
                var snapshot = _storeService.SetArtworkCachePolicy(payload.MaximumMegabytes, payload.RetentionDays);
                await WriteJsonAndPublishAsync(response, HttpStatusCode.OK, snapshot, "store.state", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/cache/clear")
            {
                var snapshot = _storeService.ClearArtworkCache();
                await WriteJsonAndPublishAsync(response, HttpStatusCode.OK, snapshot, "store.state", cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/settings/currency")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _storeService.SetDisplayCurrency(payload?.Value ?? "USD");
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    snapshot,
                    "store.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/settings/region")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                _storeService.SetStoreRegion(payload?.Value ?? "US");
                var snapshot = await _storeService.RefreshAsync(cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    snapshot,
                    "store.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/offers/open")
            {
                var payload = await JsonSerializer.DeserializeAsync<OpenStoreDealRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                _storeService.OpenDeal(payload?.DealUrl ?? string.Empty);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { message = "Offer opened in the default browser." },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store/overlay/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    GetStoreOverlayState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath is "/api/store/overlay/open" or "/api/store/overlay/close")
            {
                var open = request.Url.AbsolutePath.EndsWith("/open", StringComparison.OrdinalIgnoreCase);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    SetStoreOverlayOpen(open),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _storeSyncService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/settings-state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _storeSyncService.GetUnifySteamSettingsState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/downloads")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _storeSyncService.GetOmniLibraryDownloadCenter(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _storeSyncService.GetUnifySteamState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/summary")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _storeSyncService.GetUnifySteamLibrarySummary(),
                    cancellationToken);
                return;
            }

            const string unifySteamGamePathPrefix = "/api/unifystore/games/";
            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath is { } unifySteamGamePath &&
                unifySteamGamePath.StartsWith(unifySteamGamePathPrefix, StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(unifySteamGamePath[unifySteamGamePathPrefix.Length..], out var unifySteamGameAppId))
            {
                var game = _storeSyncService.GetUnifySteamGame(unifySteamGameAppId);
                await WriteJsonAsync(
                    response,
                    game.Game is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    game,
                    cancellationToken);
                return;
            }

            const string omniLibraryMetadataPathPrefix = "/api/unifystore/metadata/games/";
            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/metadata/search" &&
                uint.TryParse(request.QueryString["appId"], out var metadataSearchAppId))
            {
                var matches = await _omniLibraryMetadataService.SearchSteamMatchesAsync(
                    metadataSearchAppId,
                    request.QueryString["term"],
                    cancellationToken);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    matches,
                    cancellationToken);
                return;
            }

            if (request.Url?.AbsolutePath is { } metadataPath &&
                metadataPath.StartsWith(
                    omniLibraryMetadataPathPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(
                    metadataPath[omniLibraryMetadataPathPrefix.Length..],
                    out var metadataShortcutAppId) &&
                (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                 request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)))
            {
                var forceRefresh = request.HttpMethod.Equals(
                    "POST",
                    StringComparison.OrdinalIgnoreCase);
                var refreshOnOpen =
                    !forceRefresh &&
                    bool.TryParse(request.QueryString["open"], out var parsedOpen) &&
                    parsedOpen;
                var metadata = await _omniLibraryMetadataService.GetAsync(
                    metadataShortcutAppId,
                    forceRefresh,
                    refreshOnOpen,
                    cancellationToken);
                await WriteJsonAsync(
                    response,
                    metadata is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    metadata is null
                        ? (object)new
                        {
                            message = "The current game is not managed by OmniLibrary.",
                        }
                        : metadata,
                    cancellationToken);
                return;
            }

            const string omniLibraryMetadataMatchSuffix = "/match";
            if (request.Url?.AbsolutePath is { } metadataMatchPath &&
                metadataMatchPath.StartsWith(
                    omniLibraryMetadataPathPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                metadataMatchPath.EndsWith(
                    omniLibraryMetadataMatchSuffix,
                    StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(
                    metadataMatchPath[
                        omniLibraryMetadataPathPrefix.Length..
                        ^omniLibraryMetadataMatchSuffix.Length],
                    out var metadataMatchShortcutAppId) &&
                (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                 request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase)))
            {
                OmniLibraryGamePageMetadataSnapshot? metadata;
                if (request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    metadata = await _omniLibraryMetadataService.ClearSteamMatchAsync(
                        metadataMatchShortcutAppId,
                        cancellationToken);
                }
                else
                {
                    var payload = await JsonSerializer.DeserializeAsync<
                        SetOmniLibraryMetadataMatchRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    if (payload is null || payload.SteamAppId <= 0)
                    {
                        await WriteJsonAsync(
                            response,
                            HttpStatusCode.BadRequest,
                            new { message = "A positive Steam app ID is required." },
                            cancellationToken);
                        return;
                    }
                    metadata = await _omniLibraryMetadataService.SetSteamMatchAsync(
                        metadataMatchShortcutAppId,
                        payload.SteamAppId,
                        cancellationToken);
                }

                await WriteJsonAsync(
                    response,
                    metadata is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    metadata is null
                        ? (object)new
                        {
                            message = "The current game is not managed by OmniLibrary.",
                        }
                        : metadata,
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/games/artwork/repair")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetSteamAppIdRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null || payload.SteamAppId == 0)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A managed Steam app ID is required." },
                        cancellationToken);
                    return;
                }

                var result = _storeSyncService.RepairOmniLibraryGameArtwork(
                    payload.SteamAppId);
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.Accepted,
                    result,
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/artwork/reload-all")
            {
                var payload = await JsonSerializer.DeserializeAsync<ConfirmedRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload?.Confirmed != true)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new
                        {
                            message = "Reloading every managed artwork set requires explicit confirmation.",
                        },
                        cancellationToken);
                    return;
                }

                var result = _storeSyncService.ReloadAllOmniLibraryArtwork();
                await WriteJsonAsync(
                    response,
                    result.Queued ? HttpStatusCode.Accepted : HttpStatusCode.OK,
                    result,
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/metadata/open")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (!TryNormalizeMetadataUrl(payload?.Value, out var metadataUrl))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "This metadata link is not allowed." },
                        cancellationToken);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = metadataUrl,
                    UseShellExecute = true,
                })?.Dispose();
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { message = "Opened metadata link." },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/overlay/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    GetUnifyStoreOverlayState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/overlay/open")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    SetUnifyStoreOverlayOpen(true),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/overlay/close")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    SetUnifyStoreOverlayOpen(false),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/refresh")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                var storeSyncSnapshot = _storeSyncService.RefreshUnifySteam(payload?.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamStoreEnabled(
                    payload.StoreId,
                    payload.Enabled);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/xbox-source")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetXboxSourceEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.SourceId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An Xbox library source ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamXboxSourceEnabled(
                    payload.SourceId,
                    payload.Enabled);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/achievements")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreAchievementOptionsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamAchievementOptions(
                    payload.StoreId,
                    payload.Enabled,
                    payload.Credential);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/game-data/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetGameDataEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var storeSyncSnapshot =
                    _storeSyncService.SetOmniLibraryGameDataEnabled(payload?.Enabled == true);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/game-data/providers")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetGameDataProviderOptionsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ProviderId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary game-data provider ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot =
                    _storeSyncService.SetOmniLibraryGameDataProviderOptions(
                        payload.ProviderId,
                        payload.Enabled,
                        payload.Credential,
                        payload.SecondaryCredential,
                        payload.AccountId,
                        payload.AccountName,
                        payload.Region,
                        payload.Locale,
                        payload.DataPath);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/game-data/providers/test")
            {
                var payload = await JsonSerializer.DeserializeAsync<TestGameDataProviderRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null || string.IsNullOrWhiteSpace(payload.ProviderId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary game-data provider ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = await _storeSyncService
                    .TestOmniLibraryGameDataProviderAsync(
                        payload.ProviderId,
                        cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/login")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = StartUnifySteamLogin(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/disconnect")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.DisconnectUnifySteamStore(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/auth-code")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePathRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.StoreId) ||
                    string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID and authorization code are required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.CompleteUnifySteamManualAuth(
                    payload.StoreId,
                    payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/install-path")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePathRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamInstallPath(
                    payload.StoreId,
                    payload.Value ?? string.Empty);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/open-folder")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.OpenUnifySteamLibraryFolder(
                    payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/tool-path")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePathRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamToolPath(
                    payload.StoreId,
                    payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/rom-system/settings")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetRomSystemSettingsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.SystemId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A ROM system ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetOmniLibraryRomSystemSettings(
                    payload.SystemId,
                    payload.EmulatorPath,
                    payload.Fullscreen,
                    payload.EmulatorId);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/rom-system/install-emulator")
            {
                var payload = await JsonSerializer.DeserializeAsync<InstallRomEmulatorRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.SystemId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A ROM system ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.InstallOmniLibraryEmulator(
                    payload.SystemId,
                    payload.EmulatorId);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/emulation/options")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetEmulationOptionsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "Emulation options are required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetOmniLibraryEmulationOptions(
                    payload.AutoInstallEmulators,
                    payload.ImportOrbitRoms,
                    payload.ImportEmulatorLibraries);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/rom-system/open-folder")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A ROM system ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.OpenOmniLibraryRomSystemFolder(
                    payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/download-options")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreDownloadOptionsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamDownloadOptions(
                    payload.StoreId,
                    payload.DownloadWorkers,
                    payload.DownloadTimeoutSeconds);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/stores/gog-options")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetGogOptionsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "GOG options are required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamGogOptions(
                    payload.IncludeDlc,
                    payload.PreferGalaxyForLaunch);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/restart-steam")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _powerActionService.RestartSteam(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/games/launch")
            {
                var payload = await JsonSerializer.DeserializeAsync<UnifyStoreLaunchRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.StoreId) ||
                    string.IsNullOrWhiteSpace(payload.GameId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID and game ID are required." },
                        cancellationToken);
                    return;
                }

                if (!TryStartUnifyStoreLaunch(payload.StoreId, payload.GameId, out var launchMessage))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = launchMessage },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new
                    {
                        message = launchMessage,
                        snapshot = _storeSyncService.GetSnapshot()
                    },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/games/install")
            {
                var payload = await JsonSerializer.DeserializeAsync<UnifyStoreLaunchRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.StoreId) ||
                    string.IsNullOrWhiteSpace(payload.GameId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID and game ID are required." },
                        cancellationToken);
                    return;
                }

                if (!TryStartUnifyStoreInstall(payload.StoreId, payload.GameId, out var installMessage))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = installMessage },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.Accepted,
                    new { message = installMessage },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/games/repair")
            {
                var payload = await JsonSerializer.DeserializeAsync<UnifyStoreLaunchRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.StoreId) ||
                    string.IsNullOrWhiteSpace(payload.GameId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A GOG store ID and game ID are required." },
                        cancellationToken);
                    return;
                }

                if (!TryStartGogRepair(
                        payload.StoreId,
                        payload.GameId,
                        out var repairMessage))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = repairMessage },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.Accepted,
                    new { message = repairMessage },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/downloads/action")
            {
                var payload =
                    await JsonSerializer.DeserializeAsync<UnifyStoreDownloadActionRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.StoreId) ||
                    string.IsNullOrWhiteSpace(payload.GameId) ||
                    string.IsNullOrWhiteSpace(payload.Action))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A download, store, and action are required." },
                        cancellationToken);
                    return;
                }

                if (!TryControlUnifyStoreDownload(
                        payload.StoreId,
                        payload.GameId,
                        payload.Action,
                        out var controlMessage))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new
                        {
                            message = controlMessage,
                            downloads =
                                _storeSyncService.GetOmniLibraryDownloadCenter(),
                        },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.Accepted,
                    new
                    {
                        message = controlMessage,
                        downloads =
                            _storeSyncService.GetOmniLibraryDownloadCenter(),
                    },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/unifystore/games/uninstall")
            {
                var payload = await JsonSerializer.DeserializeAsync<UnifyStoreLaunchRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.StoreId) ||
                    string.IsNullOrWhiteSpace(payload.GameId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID and game ID are required." },
                        cancellationToken);
                    return;
                }

                if (!TryStartUnifyStoreUninstall(payload.StoreId, payload.GameId, out var uninstallMessage))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = uninstallMessage },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.Accepted,
                    new { message = uninstallMessage },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/performance/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _performanceService.GetSnapshot(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/state")
            {
                await WriteJsonAsync(response, HttpStatusCode.OK, _handheldPerformanceService.GetSnapshot(), cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/tdp")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetHandheldTdpRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _handheldPerformanceService.SetTdp(payload?.Watts ?? 0);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/display/brightness")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetIntegerValueRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                if (payload is null)
                {
                    await WriteJsonAsync(response, HttpStatusCode.BadRequest,
                        new { message = "A brightness value is required." }, cancellationToken);
                    return;
                }
                await WriteJsonAsync(response, HttpStatusCode.OK,
                    _displaySwitchService.SetBrightness(payload.Value), cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/display/mode")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetDisplayModeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                if (payload is null || string.IsNullOrWhiteSpace(payload.Resolution))
                {
                    await WriteJsonAsync(response, HttpStatusCode.BadRequest,
                        new { message = "A resolution and refresh rate are required." }, cancellationToken);
                    return;
                }

                await WriteJsonAsync(response, HttpStatusCode.OK,
                    _displaySwitchService.SetModePreset(payload.Resolution, payload.RefreshRate), cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/lighting")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetHandheldLightingRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.SetLighting(
                    payload?.Enabled ?? false,
                    payload?.Effect ?? "solid",
                    payload?.LeftColor ?? "#000000",
                    payload?.RightColor ?? "#000000",
                    payload?.ButtonColor ?? payload?.LeftColor ?? "#000000",
                    payload?.Brightness ?? 0);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/cpu-boost")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetHandheldCpuBoostRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.SetCpuBoost(
                    payload?.PowerSource ?? "ac", payload?.Enabled ?? false);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/afmf")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.SetAfmf(payload?.Value ?? false);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/oem-software/disable")
            {
                var snapshot = _handheldPerformanceService.SetOemSoftwareEnabled(false);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/oem-software/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.SetOemSoftwareEnabled(payload?.Value ?? false);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/oem-software/vibration-strength")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetIntegerValueRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.SetOemVibrationStrength(payload?.Value ?? 0);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/oem-software/ui-haptics-enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.SetOemUiHapticsEnabled(payload?.Value ?? false);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/controller/ui-haptic")
            {
                var payload = await JsonSerializer.DeserializeAsync<UiHapticRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var accepted = _handheldPerformanceService.RequestUiHaptic(payload?.Kind ?? string.Empty);
                await WriteJsonAsync(response, HttpStatusCode.OK, new { accepted }, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/oem-software/buttons/capture")
            {
                var payload = await JsonSerializer.DeserializeAsync<OemButtonRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.StartOemButtonCapture(payload?.ButtonId ?? string.Empty);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/oem-software/buttons/capture/cancel")
            {
                var snapshot = _handheldPerformanceService.CancelOemButtonCapture();
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/oem-software/buttons/binding")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetOemButtonBindingRequest>(
                    request.InputStream, JsonOptions, cancellationToken);
                var snapshot = _handheldPerformanceService.SetOemButtonBinding(
                    payload?.ButtonId ?? string.Empty,
                    payload?.ActionId ?? "none",
                    payload?.CustomShortcut ?? string.Empty);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/mode")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetHandheldModeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _handheldPerformanceService.SetMode(payload?.ModeId ?? string.Empty);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/profiles/global")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetHandheldPowerTdpRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _handheldPerformanceService.SetGlobalTdp(
                    payload?.Watts ?? 0,
                    payload?.PowerSource ?? string.Empty);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/profiles/game")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetHandheldGameProfileRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _handheldPerformanceService.SetGameProfileTdp(
                    payload?.Key ?? string.Empty,
                    payload?.Watts ?? 0,
                    payload?.PowerSource ?? string.Empty);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/profiles/auto-enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _handheldPerformanceService.SetAutoProfilesEnabled(payload?.Value ?? false);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/profiles/notifications-enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _handheldPerformanceService.SetProfileNotificationsEnabled(payload?.Value ?? false);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/profiles/notifications/test")
            {
                var snapshot = _handheldPerformanceService.ShowTestNotification();
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/profiles/delete")
            {
                var payload = await JsonSerializer.DeserializeAsync<DeleteHandheldProfileRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);
                var snapshot = _handheldPerformanceService.DeleteProfile(payload?.Key ?? string.Empty);
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/handheld-performance/pawnio/install")
            {
                var snapshot = _handheldPerformanceService.InstallOrRepairPawnIo();
                await WriteJsonAsync(response, HttpStatusCode.OK, snapshot, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.GetSnapshotAsync(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/store")
            {
                var page = int.TryParse(request.QueryString["page"], out var parsedPage)
                    ? parsedPage
                    : 1;
                var perPage = int.TryParse(request.QueryString["perPage"], out var parsedPerPage)
                    ? parsedPerPage
                    : 12;

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.GetStoreCatalogAsync(
                        request.QueryString["search"],
                        request.QueryString["filter"],
                        request.QueryString["order"],
                        page,
                        perPage),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/store/theme")
            {
                var storeThemeId = request.QueryString["storeThemeId"];
                if (string.IsNullOrWhiteSpace(storeThemeId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A store theme ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.GetStoreThemeAsync(storeThemeId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/resolve-css")
            {
                var title = request.QueryString["title"];
                var url = request.QueryString["url"];

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new
                    {
                        css = await _themesService.ResolveCssForTargetAsync(title, url)
                    },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/catalog/refresh")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.RefreshCatalogAsync(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/store/install")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreThemeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreThemeId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A store theme ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.InstallStoreThemeAsync(payload.StoreThemeId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/performance/overlay/start")
            {
                var performanceSnapshot = _performanceService.StartOverlay();
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    performanceSnapshot,
                    "performance.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/performance/elevated-helper/prepare")
            {
                var performanceSnapshot = _performanceService.PrepareElevatedHelper();
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    performanceSnapshot,
                    "performance.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/performance/overlay/stop")
            {
                var performanceSnapshot = _performanceService.StopOverlay();
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    performanceSnapshot,
                    "performance.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/performance/settings/overlay-level")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetPerformanceOverlayLevelRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid TFS Overlay preset is required." },
                        cancellationToken);
                    return;
                }

                var performanceSnapshot = _performanceService.SetOverlayLevel(payload.Level);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    performanceSnapshot,
                    "performance.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/performance/settings/auto-target")
            {
                var performanceSnapshot = _performanceService.ToggleAutoTarget();
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    performanceSnapshot,
                    "performance.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/performance/settings/value")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetPerformanceIntegerSettingRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Key))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A valid TFS Overlay setting key and value are required." },
                        cancellationToken);
                    return;
                }

                var performanceSnapshot = _performanceService.SetSettingValue(payload.Key, payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    performanceSnapshot,
                    "performance.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/settings/toggle")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.GetSnapshotAsync(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/themes/install")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetThemeInstalledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ThemeId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A theme ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.SetThemeInstalledAsync(payload.ThemeId, payload.Installed),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/themes/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetThemeEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ThemeId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A theme ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.SetThemeEnabledAsync(payload.ThemeId, payload.Enabled),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/themes/option/toggle")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetThemeOptionRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ThemeId) || string.IsNullOrWhiteSpace(payload.OptionId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A theme ID and option ID are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.ToggleThemeOptionAsync(payload.ThemeId, payload.OptionId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/themes/option/choice")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetThemeChoiceRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.ThemeId) ||
                    string.IsNullOrWhiteSpace(payload.OptionId) ||
                    string.IsNullOrWhiteSpace(payload.ChoiceId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A theme ID, option ID, and choice ID are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.SetThemeChoiceAsync(payload.ThemeId, payload.OptionId, payload.ChoiceId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/themes/option/range/adjust")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetThemeRangeRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ThemeId) || string.IsNullOrWhiteSpace(payload.OptionId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A theme ID and option ID are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.AdjustThemeRangeAsync(payload.ThemeId, payload.OptionId, payload.Delta),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/themes/option/range/reset")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetThemeOptionRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ThemeId) || string.IsNullOrWhiteSpace(payload.OptionId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A theme ID and option ID are required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.ResetThemeRangeAsync(payload.ThemeId, payload.OptionId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/profiles/create")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.CreateProfileAsync(payload?.Value ?? string.Empty),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/profiles/install")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetProfileRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A profile ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.InstallProfileAsync(payload.ProfileId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/profiles/apply")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetProfileRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A profile ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.ApplyProfileAsync(payload.ProfileId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/profiles/update")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetProfileRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A profile ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.UpdateProfileAsync(payload.ProfileId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/profiles/remove")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetProfileRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.ProfileId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A profile ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.RemoveProfileAsync(payload.ProfileId),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/folder/open")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.OpenThemeFolderAsync(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/backend/install")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.InstallBackendAsync(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/backend/start")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.StartBackendAsync(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/themes/watch/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetBooleanValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A watch enabled value is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _themesService.SetWatchEnabledAsync(payload.Value),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/hltb/settings/toggle")
            {
                var payload = await JsonSerializer.DeserializeAsync<ToggleSettingRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Key))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A setting key is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _hltbService.ToggleSetting(payload.Key),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/hltb/cache/clear")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _hltbService.ClearCache(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/hltb/open-details")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A detail URL is required." },
                        cancellationToken);
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = payload.Value,
                    UseShellExecute = true,
                });

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { message = "Opened the HowLongToBeat detail page." },
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/power/start-desktop")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _powerActionService.StartWindowsDesktop(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/power/restart-steam")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _powerActionService.RestartSteam(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/power/restart-steam-tools")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _powerActionService.RestartSteamTools(),
                    cancellationToken);

                _requestShutdown();
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/power/sleep")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _powerActionService.SleepWindows(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/power/restart-windows")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _powerActionService.RestartWindows(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/power/shutdown-windows")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _powerActionService.ShutDownWindows(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/settings/toggle")
            {
                var payload = await JsonSerializer.DeserializeAsync<ToggleSettingRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Key))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A setting key is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.ToggleSetting(payload.Key);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/settings/api-key")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                var storeSyncSnapshot = _storeSyncService.SetSteamGridDbApiKey(payload?.Value ?? string.Empty);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/stores/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetStoreEnabled(payload.StoreId, payload.Enabled);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/stores/path")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePathRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetStoreScanPath(payload.StoreId, payload.Value ?? string.Empty);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/stores/path/clear")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.ClearStoreScanPath(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/stores/additional-paths")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePathsRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetStoreAdditionalScanPaths(payload.StoreId, payload.Values);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/stores/custom-path")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                var storeSyncSnapshot = _storeSyncService.SetCustomScanPath(payload?.Value ?? string.Empty);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/stores/custom-path/clear")
            {
                var storeSyncSnapshot = _storeSyncService.ClearCustomScanPath();
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/unifysteam/refresh")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                var storeSyncSnapshot = _storeSyncService.RefreshUnifySteam(payload?.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/unifysteam/stores/enabled")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreEnabledRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetUnifySteamStoreEnabled(payload.StoreId, payload.Enabled);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/unifysteam/stores/login")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.StartUnifySteamLogin(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/unifysteam/stores/auth-code")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStorePathRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.StoreId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An OmniLibrary store ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.CompleteUnifySteamManualAuth(payload.StoreId, payload.Value ?? string.Empty);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/titles/artwork-preview")
            {
                var titleId = request.QueryString["titleId"] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(titleId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A title ID is required." },
                        cancellationToken);
                    return;
                }

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _storeSyncService.GetArtworkPreviewAsync(titleId, cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/titles/override")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetStoreSyncTitleOverrideRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.TitleId))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A title ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.SetTitleOverride(
                    payload.TitleId,
                    payload.TitleOverride ?? string.Empty,
                    payload.ArtworkTitleOverride ?? string.Empty,
                    payload.Excluded);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/titles/override/clear")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetTextValueRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Value))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "A title ID is required." },
                        cancellationToken);
                    return;
                }

                var storeSyncSnapshot = _storeSyncService.ClearTitleOverride(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/sync")
            {
                var storeSyncSnapshot = _storeSyncService.RunSync();
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    storeSyncSnapshot,
                    "store-sync.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/store-sync/startup-sync")
            {
                _ = Task.Run(() => _storeSyncService.RunStartupSync());
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    new { triggered = true },
                    cancellationToken);
                return;
            }

            await WriteJsonAsync(
                response,
                HttpStatusCode.NotFound,
                new { message = "Route not found." },
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await WriteJsonAsync(
                response,
                HttpStatusCode.BadRequest,
                new { message = exception.Message },
                cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteJsonAsync(
                response,
                HttpStatusCode.InternalServerError,
                new { message = exception.Message },
                cancellationToken);
        }
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        object payload,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";

        await using var output = response.OutputStream;
        await JsonSerializer.SerializeAsync(output, payload, JsonOptions, cancellationToken);
    }

    private async Task<SteamKeyboardOpenResult> ShowSteamKeyboardAsync(
        ShowSteamKeyboardRequest? request,
        CancellationToken cancellationToken)
    {
        var sharedTarget = await _devToolsClient.GetSharedJsContextTargetAsync(cancellationToken);
        if (sharedTarget is null)
        {
            return new SteamKeyboardOpenResult(
                false,
                "Steam SharedJSContext is not available.",
                null);
        }

        var label = string.IsNullOrWhiteSpace(request?.Label)
            ? "Text"
            : request.Label.Trim();
        var value = request?.Value ?? string.Empty;
        var rect = new
        {
            x = request?.X,
            y = request?.Y,
            width = request?.Width,
            height = request?.Height
        };

        var expression = $$"""
(() => {
  const label = {{JsonSerializer.Serialize(label, JsonOptions)}};
  const value = {{JsonSerializer.Serialize(value, JsonOptions)}};
  const rect = {{JsonSerializer.Serialize(rect, JsonOptions)}};

  function getSteamRequire() {
    if (typeof window.__tfsSteamRequire === "function") {
      return window.__tfsSteamRequire;
    }

    const chunk = window.webpackChunksteamui;
    if (!chunk || typeof chunk.push !== "function") {
      return null;
    }

    let steamRequire = null;
    chunk.push([[Math.floor(Math.random() * 1000000000)], {}, (require) => {
      steamRequire = require;
    }]);

    if (typeof steamRequire === "function") {
      window.__tfsSteamRequire = steamRequire;
    }

    return steamRequire;
  }

  void getSteamRequire;

  // Steam's window store is a stable global. The old hard-coded webpack
  // module id (61236) vanished with a Steam client update.
  const gamepadStore = window.SteamUIStore?.m_WindowStore || window.SteamUIStore?.WindowStore;
  const windowInstance =
    gamepadStore?.GamepadUIMainWindowInstance ||
    gamepadStore?.MainWindowInstance;
  const keyboardManager = windowInstance?.VirtualKeyboardManager;

  if (!keyboardManager || typeof keyboardManager.CreateVirtualKeyboardRef !== "function") {
    return {
      success: false,
      message: "Steam VirtualKeyboardManager is not available.",
      hasStore: Boolean(gamepadStore),
      hasWindowInstance: Boolean(windowInstance)
    };
  }

  try {
    keyboardManager.AddVirtualKeyboardOwner?.("ToolsForSteam");
  } catch {}

  try {
    keyboardManager.SetDismissOnEnterKey?.(false);
  } catch {}

  try {
    if (
      Number.isFinite(rect.x) &&
      Number.isFinite(rect.y) &&
      Number.isFinite(rect.width) &&
      Number.isFinite(rect.height)
    ) {
      keyboardManager.SetTextFieldLocation(rect.x, rect.y, rect.width, rect.height);
    }
  } catch {}

  const keyboardRef =
    window.__tfsQuickAccessKeyboardRef ||
    keyboardManager.CreateVirtualKeyboardRef({
      BIsElementValidForInput: () => true,
      strEnterKeyLabel: "Done",
      onKeyboardShow: () => {},
      onKeyboardFullyVisible: () => {}
    });

  window.__tfsQuickAccessKeyboardRef = keyboardRef;
  keyboardRef.ShowVirtualKeyboard();

  return {
    success: true,
    message: "Steam virtual keyboard requested.",
    label,
    valueLength: value.length,
    showing: Boolean(keyboardManager.IsShowingVirtualKeyboard?.Value),
    modal: Boolean(keyboardManager.IsVirtualKeyboardModal?.Value),
    owners: keyboardManager.m_KeyboardOwners?.size ?? null
  };
})()
""";

        var evaluation = await _devToolsClient.EvaluateAsync(
            sharedTarget.WebSocketDebuggerUrl,
            expression,
            cancellationToken);

        if (!evaluation.Success)
        {
            return new SteamKeyboardOpenResult(
                false,
                evaluation.ErrorMessage ?? "Steam virtual keyboard request failed.",
                evaluation.Value);
        }

        // The script reports its own outcome; a missing keyboard manager is a
        // failure, not "requested" (that hid the breakage after a Steam update).
        var opened = evaluation.Value is JsonElement { ValueKind: JsonValueKind.Object } details &&
            details.TryGetProperty("success", out var successElement) &&
            successElement.ValueKind == JsonValueKind.True;
        return new SteamKeyboardOpenResult(
            opened,
            opened ? "Steam virtual keyboard requested." : "Steam's virtual keyboard is not available.",
            evaluation.Value);
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        string text,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/plain; charset=utf-8";

        var bytes = Encoding.UTF8.GetBytes(text);
        await using var output = response.OutputStream;
        await output.WriteAsync(bytes, cancellationToken);
    }

    private static async Task WriteBytesAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken,
        string cacheControl = "no-cache, no-store")
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.LongLength;
        response.Headers["Cache-Control"] = cacheControl;

        await using var output = response.OutputStream;
        await output.WriteAsync(bytes, cancellationToken);
    }

    private static async Task WriteFileAsync(
        HttpListenerResponse response,
        string path,
        string contentType,
        CancellationToken cancellationToken,
        string cacheControl = "no-cache, no-store")
    {
        var fileInfo = new FileInfo(path);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = contentType;
        response.ContentLength64 = fileInfo.Length;
        response.Headers["Cache-Control"] = cacheControl;

        await using var input = File.OpenRead(path);
        await using var output = response.OutputStream;
        await input.CopyToAsync(output, cancellationToken);
    }

    private async Task WriteEventStreamAsync(
        HttpListenerResponse response,
        IReadOnlyCollection<string>? topics,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache, no-store";
        response.Headers["Connection"] = "keep-alive";
        response.SendChunked = true;
        response.KeepAlive = true;

        using var subscription = _liveUpdateHub.Subscribe(topics);
        await using var output = response.OutputStream;
        using var writer = new StreamWriter(output, new UTF8Encoding(false));

        try
        {
            await writer.WriteAsync(": connected\n\n");
            await writer.FlushAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                keepAliveCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var payload = await subscription.Reader.ReadAsync(keepAliveCts.Token);
                    await writer.WriteAsync($"data: {payload}\n\n");
                    await writer.FlushAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await writer.WriteAsync(": keepalive\n\n");
                    await writer.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpListenerException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static IReadOnlyCollection<string>? ParseLiveUpdateTopics(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var topics = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(topic => topic.Length is > 0 and <= 80)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        return topics.Length > 0 ? topics : null;
    }

    private async Task<AudioDashboardSnapshot> GetAudioDashboardSnapshotAsync(CancellationToken cancellationToken)
    {
        return await StaThread.RunAsync(
            () => _audioOutputDeviceService.GetDashboardSnapshot(),
            cancellationToken);
    }

    private async Task WriteJsonAndPublishAsync<TPayload>(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        TPayload payload,
        string liveTopic,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(response, statusCode, payload!, cancellationToken);

        if (statusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(liveTopic))
        {
            _liveUpdateHub.Publish(liveTopic, payload);
        }
    }

    private async Task WriteJsonAndPublishAsync<TResponse, TLivePayload>(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        TResponse responsePayload,
        string liveTopic,
        TLivePayload livePayload,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(response, statusCode, responsePayload!, cancellationToken);

        if (statusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(liveTopic))
        {
            _liveUpdateHub.Publish(liveTopic, livePayload);
        }
    }

    private async Task WriteDisabledPluginResponseAsync(
        HttpListenerResponse response,
        string? path,
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(path, "/api/themes/resolve-css", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(response, HttpStatusCode.OK, new { css = string.Empty }, cancellationToken);
            return;
        }

        if (string.Equals(path, "/api/metadata/state", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(response, HttpStatusCode.OK, new { enabled = false }, cancellationToken);
            return;
        }

        if (string.Equals(path, "/api/hltb/game", StringComparison.OrdinalIgnoreCase))
        {
            var settings = new HltbSettingsState(false, false, false, false, false, false, 0);
            await WriteJsonAsync(
                response,
                HttpStatusCode.OK,
                new HltbGameSnapshot(
                    RequestedTitle: string.Empty,
                    MatchedTitle: string.Empty,
                    AppId: null,
                    GameId: null,
                    MainStory: string.Empty,
                    MainPlus: string.Empty,
                    Completionist: string.Empty,
                    AllStyles: string.Empty,
                    DetailUrl: string.Empty,
                    Found: false,
                    Cached: false,
                    Settings: settings,
                    ErrorMessage: "HLTB is disabled in Tools for Steam settings."),
                cancellationToken);
            return;
        }

        if (string.Equals(path, "/api/unifystore/summary", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(
                response,
                HttpStatusCode.OK,
                new UnifySteamLibrarySummarySnapshot(
                    0,
                    false,
                    []),
                cancellationToken);
            return;
        }

        await WriteJsonAsync(
            response,
            HttpStatusCode.Forbidden,
            new { message = $"{pluginId} is disabled in Tools for Steam settings." },
            cancellationToken);
    }

    private static long NormalizeSteamAppId(long appId)
    {
        return appId < 0
            ? unchecked((uint)appId)
            : appId;
    }

    private PluginStoreOverlayState GetUnifyStoreOverlayState()
    {
        lock (_unifyStoreOverlayLock)
        {
            return new PluginStoreOverlayState(_unifyStoreOverlayOpen);
        }
    }

    private PluginStoreOverlayState GetStoreOverlayState()
    {
        lock (_storeOverlayLock)
        {
            return new PluginStoreOverlayState(_storeOverlayOpen);
        }
    }

    private PluginStoreOverlayState SetStoreOverlayOpen(bool open)
    {
        lock (_storeOverlayLock)
        {
            _storeOverlayOpen = open;
            return new PluginStoreOverlayState(_storeOverlayOpen);
        }
    }

    private PluginStoreOverlayState SetUnifyStoreOverlayOpen(bool open)
    {
        lock (_unifyStoreOverlayLock)
        {
            _unifyStoreOverlayOpen = open;
            return new PluginStoreOverlayState(_unifyStoreOverlayOpen);
        }
    }

    private StoreSyncSnapshot StartUnifySteamLogin(string storeId)
    {
        var snapshot = _storeSyncService.StartUnifySteamLogin(storeId);
        if (!OmniLibraryStoreRegistry.TryGet(storeId, out var descriptor) ||
            (!descriptor.Supports(OmniLibraryStoreCapabilities.ManagedWebSignIn) &&
             !descriptor.Id.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase)))
        {
            return snapshot;
        }

        lock (_omniLibraryLoginProcessLock)
        {
            if (_omniLibraryLoginProcesses.TryGetValue(descriptor.Id, out var existing) &&
                !existing.HasExited)
            {
                OmniLibraryLoginRuntime.TryActivate(existing);
                return snapshot;
            }

            existing?.Dispose();
            var process = OmniLibraryLoginRuntime.StartProcess(descriptor.Id);
            _omniLibraryLoginProcesses[descriptor.Id] = process;
            _ = MonitorOmniLibraryLoginAsync(descriptor.Id, process);
        }

        return snapshot;
    }

    private async Task MonitorOmniLibraryLoginAsync(string storeId, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                var snapshot = _storeSyncService.RefreshUnifySteam(storeId);
                _liveUpdateHub.Publish("store-sync.state", snapshot);
            }
        }
        catch
        {
            // The sign-in window owns user-facing errors. A retry starts a fresh process.
        }
        finally
        {
            lock (_omniLibraryLoginProcessLock)
            {
                if (_omniLibraryLoginProcesses.TryGetValue(storeId, out var current) &&
                    ReferenceEquals(current, process))
                {
                    _omniLibraryLoginProcesses.Remove(storeId);
                }
            }

            process.Dispose();
        }
    }

    private bool TryStartUnifyStoreLaunch(string storeId, string gameId, out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();

        if (!OmniLibraryStoreRegistry.TryGet(normalizedStoreId, out _))
        {
            message = "This OmniLibrary store is not supported.";
            return false;
        }

        if (!IsSafeUnifyStoreLaunchId(normalizedGameId))
        {
            message = "The OmniLibrary game ID is invalid.";
            return false;
        }

        // The cached OmniLibrary state is enough to validate an action. The full
        // Store Sync snapshot rescanned every store folder and shortcuts.vdf
        // under the service lock on each Play or Download press.
        var snapshot = _storeSyncService.GetUnifySteamState();
        var storeState = snapshot.UnifySteam.Stores
            .FirstOrDefault(store => string.Equals(store.Id, normalizedStoreId, StringComparison.OrdinalIgnoreCase));
        if (storeState is null || !storeState.Enabled)
        {
            message = "Enable this OmniLibrary store before launching its games.";
            return false;
        }

        var gameState = storeState.Games
            .FirstOrDefault(game => string.Equals(game.Id, normalizedGameId, StringComparison.OrdinalIgnoreCase));
        if (gameState is null)
        {
            message = "The selected game is not in the cached OmniLibrary catalog. Refresh its store first.";
            return false;
        }

        if (normalizedStoreId.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase))
        {
            if (!gameState.Installed && gameState.CloudPlayable)
            {
                if (!UnifySteamLauncher.TryOpenXboxCloudGame(gameState.Title, normalizedGameId))
                {
                    message = "Xbox Cloud Gaming could not be opened for this game.";
                    return false;
                }

                message = $"Opening {gameState.Title} in Xbox Cloud Gaming.";
                return true;
            }

            if (!gameState.Installed)
            {
                message = "Download this Xbox game before launching it.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(gameState.ExecutablePath) && File.Exists(gameState.ExecutablePath))
            {
                var xboxHostPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(xboxHostPath) || !File.Exists(xboxHostPath))
                {
                    message = "Tools for Steam could not resolve its Xbox launch host.";
                    return false;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = xboxHostPath,
                    Arguments = XboxStoreLaunchHost.BuildLaunchArguments(gameState.ExecutablePath, gameState.InstallPath),
                    WorkingDirectory = Path.GetDirectoryName(xboxHostPath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.Dispose();
                message = $"Starting {gameState.Title} through the Xbox launch host.";
                return true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = $"msxbox://game/?productId={Uri.EscapeDataString(normalizedGameId)}",
                UseShellExecute = true,
            })?.Dispose();
            message = "The local Xbox executable is protected or unavailable, so the Xbox game page was opened.";
            return true;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            message = "Tools for Steam could not resolve its launcher executable.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--unifysteam-launch");
        startInfo.ArgumentList.Add($"{normalizedStoreId}:{normalizedGameId}");
        Process.Start(startInfo)?.Dispose();

        message = normalizedStoreId.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase) && !gameState.Installed
            ? "Opening the GOG Galaxy game page. Install the game there, then refresh OmniLibrary."
            : "OmniLibrary started the store launcher.";
        return true;
    }

    private bool TryStartUnifyStoreInstall(
        string storeId,
        string gameId,
        out string message,
        bool allowDisabledExistingDownload = false)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        if (!OmniLibraryStoreRegistry.TryGet(normalizedStoreId, out var descriptor) ||
            (!descriptor.Supports(OmniLibraryStoreCapabilities.ManagedInstall) &&
             !descriptor.Supports(OmniLibraryStoreCapabilities.ProductPageInstallFallback)))
        {
            message = "This OmniLibrary store is not supported.";
            return false;
        }

        if (!IsSafeUnifyStoreLaunchId(normalizedGameId))
        {
            message = "The OmniLibrary game ID is invalid.";
            return false;
        }

        // The cached OmniLibrary state is enough to validate an action. The full
        // Store Sync snapshot rescanned every store folder and shortcuts.vdf
        // under the service lock on each Play or Download press.
        var snapshot = _storeSyncService.GetUnifySteamState();
        var storeState = snapshot.UnifySteam.Stores
            .FirstOrDefault(store => string.Equals(store.Id, normalizedStoreId, StringComparison.OrdinalIgnoreCase));
        if (storeState is null ||
            (!storeState.Enabled && !allowDisabledExistingDownload))
        {
            message = "Enable this OmniLibrary store before downloading its games.";
            return false;
        }

        var gameState = storeState.Games
            .FirstOrDefault(game => string.Equals(game.Id, normalizedGameId, StringComparison.OrdinalIgnoreCase));
        if (gameState is null)
        {
            message = "The selected game is not in the cached OmniLibrary catalog. Refresh its store first.";
            return false;
        }

        var gogTransaction = normalizedStoreId.Equals(
                "gog-galaxy",
                StringComparison.OrdinalIgnoreCase)
            ? GogOperationJournal.Get(normalizedGameId)
            : null;
        var resumeGogRepair =
            gogTransaction?.IsRepair == true &&
            gogTransaction.IsRecoverableInstall &&
            gameState.Download.Status is
                "failed" or
                "paused" or
                "reconnecting" or
                "queued";
        if (gameState.Installed &&
            !gameState.UpdateAvailable &&
            !resumeGogRepair)
        {
            message = $"{gameState.Title} is already installed.";
            return true;
        }

        if (UnifySteamDownloadStatusStore.IsActivelyTransferring(
                gameState.Download.Status))
        {
            message = string.IsNullOrWhiteSpace(gameState.Download.DetailText)
                ? $"{gameState.Title} is already being downloaded."
                : gameState.Download.DetailText;
            return true;
        }

        if (normalizedStoreId.Equals("xbox-game-pass", StringComparison.OrdinalIgnoreCase) &&
            gameState.CloudPlayable)
        {
            if (!UnifySteamLauncher.TryOpenXboxCloudGame(gameState.Title, normalizedGameId))
            {
                message = "Xbox Cloud Gaming could not be opened for this game.";
                return false;
            }

            message = $"Opening {gameState.Title} in Xbox Cloud Gaming.";
            return true;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            message = "Tools for Steam could not resolve its download worker.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(resumeGogRepair
            ? UnifySteamLauncher.RepairArgument
            : UnifySteamLauncher.InstallArgument);
        startInfo.ArgumentList.Add($"{normalizedStoreId}:{normalizedGameId}");
        UnifySteamDownloadStatusStore.Update(
            normalizedStoreId,
            normalizedGameId,
            "preparing",
            gameState.Download.ProgressPercent,
            normalizedStoreId switch
            {
                "epic-games" => "Preparing the Epic download worker.",
                "gog-galaxy" => resumeGogRepair
                    ? "Preparing the GOG repair worker."
                    : "Preparing the GOG download worker.",
                "xbox-game-pass" => "Preparing the Xbox installation request.",
                _ => "Preparing download.",
            },
            workerProcessId: 0,
            downloadedBytes: gameState.Download.DownloadedBytes,
            totalBytes: gameState.Download.TotalBytes,
            attempt: gameState.Download.Attempt,
            gameTitle: gameState.Title,
            steamAppId: gameState.SteamAppId);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException(
                    "Windows did not start the OmniLibrary download worker.");
            }

            UnifySteamLauncher.AssignDownloadWorkerIfUnclaimed(
                normalizedStoreId,
                normalizedGameId,
                process.Id);
        }
        catch (Exception exception)
        {
            UnifySteamDownloadStatusStore.Update(
                normalizedStoreId,
                normalizedGameId,
                "failed",
                gameState.Download.ProgressPercent,
                $"The download worker could not start: {exception.Message}",
                workerProcessId: 0,
                downloadedBytes: gameState.Download.DownloadedBytes,
                totalBytes: gameState.Download.TotalBytes,
                attempt: gameState.Download.Attempt);
            message = "The OmniLibrary download worker could not be started.";
            return false;
        }

        message = normalizedStoreId switch
        {
            "epic-games" => $"Epic download started for {gameState.Title}.",
            "gog-galaxy" => resumeGogRepair
                ? $"GOG repair resumed for {gameState.Title}."
                : gameState.UpdateAvailable
                ? $"GOG update started for {gameState.Title}."
                : $"GOG download started for {gameState.Title}.",
            "xbox-game-pass" =>
                $"Requesting the Xbox installation for {gameState.Title}. " +
                "If Windows blocks direct installation, its Xbox product page will open.",
            _ => "Download started.",
        };
        return true;
    }

    private bool TryStartGogRepair(
        string storeId,
        string gameId,
        out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        if (!normalizedStoreId.Equals(
                "gog-galaxy",
                StringComparison.OrdinalIgnoreCase) ||
            !IsSafeUnifyStoreLaunchId(normalizedGameId))
        {
            message = "Verify & Repair is available only for a valid GOG game.";
            return false;
        }

        var store = _storeSyncService.GetUnifySteamState().UnifySteam.Stores
            .FirstOrDefault(candidate =>
                candidate.Id.Equals(
                    normalizedStoreId,
                    StringComparison.OrdinalIgnoreCase));
        var game = store?.Games.FirstOrDefault(candidate =>
            candidate.Id.Equals(
                normalizedGameId,
                StringComparison.OrdinalIgnoreCase));
        if (store?.Enabled != true ||
            game?.Installed != true ||
            !UnifySteamLauncher.IsManagedGogInstall(
                game.InstallPath,
                normalizedGameId))
        {
            message =
                "Verify & Repair is available only for GOG games installed by OmniLibrary.";
            return false;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath))
        {
            message = "Tools for Steam could not resolve its GOG repair worker.";
            return false;
        }

        var current = UnifySteamDownloadStatusStore.Get(
            normalizedStoreId,
            normalizedGameId);
        if (UnifySteamDownloadStatusStore.IsBusyOperation(current.Status))
        {
            message = string.IsNullOrWhiteSpace(current.DetailText)
                ? "A GOG operation is already active for this game."
                : current.DetailText;
            return true;
        }

        UnifySteamDownloadStatusStore.Update(
            normalizedStoreId,
            normalizedGameId,
            "preparing",
            0,
            $"Verifying {game.Title} and repairing changed or missing files.",
            workerProcessId: 0,
            gameTitle: game.Title,
            steamAppId: game.SteamAppId);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory =
                Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(UnifySteamLauncher.RepairArgument);
        startInfo.ArgumentList.Add(
            $"{normalizedStoreId}:{normalizedGameId}");
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException(
                    "Windows did not start the GOG repair worker.");
            }

            UnifySteamLauncher.AssignDownloadWorkerIfUnclaimed(
                normalizedStoreId,
                normalizedGameId,
                process.Id);
            message = $"Verify & Repair started for {game.Title}.";
            return true;
        }
        catch (Exception exception)
        {
            GogOperationJournal.Fail(normalizedGameId, exception.Message);
            UnifySteamDownloadStatusStore.Update(
                normalizedStoreId,
                normalizedGameId,
                "failed",
                0,
                $"The GOG repair worker could not start: {exception.Message}",
                workerProcessId: 0,
                gameTitle: game.Title,
                steamAppId: game.SteamAppId);
            message = "The GOG repair worker could not be started.";
            return false;
        }
    }

    private bool TryStartUnifyStoreUninstall(string storeId, string gameId, out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        if (!OmniLibraryStoreRegistry.TryGet(normalizedStoreId, out var descriptor) ||
            (!descriptor.Supports(OmniLibraryStoreCapabilities.ManagedUninstall) &&
             !descriptor.Supports(OmniLibraryStoreCapabilities.ProductPageUninstall)))
        {
            message = "This OmniLibrary store is not supported.";
            return false;
        }

        if (!IsSafeUnifyStoreLaunchId(normalizedGameId))
        {
            message = "The OmniLibrary game ID is invalid.";
            return false;
        }

        // The cached OmniLibrary state is enough to validate an action. The full
        // Store Sync snapshot rescanned every store folder and shortcuts.vdf
        // under the service lock on each Play or Download press.
        var snapshot = _storeSyncService.GetUnifySteamState();
        var storeState = snapshot.UnifySteam.Stores
            .FirstOrDefault(store => string.Equals(store.Id, normalizedStoreId, StringComparison.OrdinalIgnoreCase));
        if (storeState is null || !storeState.Enabled)
        {
            message = "Enable this OmniLibrary store before managing its games.";
            return false;
        }

        var gameState = storeState.Games
            .FirstOrDefault(game => string.Equals(game.Id, normalizedGameId, StringComparison.OrdinalIgnoreCase));
        if (gameState is null)
        {
            message = "The selected game is not in the cached OmniLibrary catalog. Refresh its store first.";
            return false;
        }

        if (!gameState.Installed)
        {
            message = $"{gameState.Title} is not installed.";
            return false;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            message = "Tools for Steam could not resolve its uninstall worker.";
            return false;
        }

        var windowsBeforeLaunch = normalizedStoreId == "xbox-game-pass"
            ? _processWindowService.GetSnapshot().Windows
            : null;
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(UnifySteamLauncher.UninstallArgument);
        startInfo.ArgumentList.Add($"{normalizedStoreId}:{normalizedGameId}");
        var currentStatus = UnifySteamDownloadStatusStore.Get(
            normalizedStoreId,
            normalizedGameId);
        UnifySteamDownloadStatusStore.Update(
            normalizedStoreId,
            normalizedGameId,
            "uninstalling",
            0,
            $"Preparing to uninstall {gameState.Title}.",
            workerProcessId: 0,
            downloadedBytes: currentStatus.DownloadedBytes,
            totalBytes: currentStatus.TotalBytes,
            attempt: currentStatus.Attempt,
            gameTitle: gameState.Title,
            steamAppId: gameState.SteamAppId);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException(
                    "Windows did not start the OmniLibrary uninstall worker.");
            }

            UnifySteamLauncher.AssignDownloadWorkerIfUnclaimed(
                normalizedStoreId,
                normalizedGameId,
                process.Id);
        }
        catch (Exception exception)
        {
            UnifySteamDownloadStatusStore.Update(
                normalizedStoreId,
                normalizedGameId,
                "uninstall-failed",
                0,
                $"The uninstall worker could not start: {exception.Message}",
                workerProcessId: 0,
                gameTitle: gameState.Title,
                steamAppId: gameState.SteamAppId);
            message = "The OmniLibrary uninstall worker could not be started.";
            return false;
        }

        if (windowsBeforeLaunch is not null)
        {
            _processWindowService.ActivateLaunchedAppWhenReady(
                "Xbox",
                "XboxPcApp",
                launchedProcessId: null,
                windowsBeforeLaunch: windowsBeforeLaunch);
        }

        message = normalizedStoreId switch
        {
            "epic-games" =>
                $"Uninstalling {gameState.Title}. OmniLibrary will keep the library entry available for download.",
            "gog-galaxy" =>
                $"Uninstalling {gameState.Title}. OmniLibrary removes managed installs automatically and opens GOG Galaxy only for externally managed files.",
            _ =>
                $"Opening {gameState.Title} in the Xbox app. Finish the uninstall there; OmniLibrary will detect the removal.",
        };
        return true;
    }

    // True for an OmniLibrary shortcut whose game is neither installed nor
    // cloud playable: Steam's Play button starts nothing for it.
    private bool IsUninstalledOmniLibraryShortcut(uint appId)
    {
        if (appId < 0x80000000u)
        {
            return false;
        }

        try
        {
            var detail = _storeSyncService.GetUnifySteamGame(appId);
            return detail.Game is { Installed: false, CloudPlayable: false } &&
                !string.Equals(detail.StoreId, "rom-library", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool TryControlUnifyStoreDownload(
        string storeId,
        string gameId,
        string action,
        out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();
        var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!OmniLibraryStoreRegistry.TryGet(normalizedStoreId, out _) ||
            !IsSafeUnifyStoreLaunchId(normalizedGameId))
        {
            message = "This Download Center entry is invalid.";
            return false;
        }

        var entry = _storeSyncService.GetOmniLibraryDownloadCenter().Entries
            .FirstOrDefault(candidate =>
                candidate.StoreId.Equals(
                    normalizedStoreId,
                    StringComparison.OrdinalIgnoreCase) &&
                candidate.GameId.Equals(
                    normalizedGameId,
                    StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            message = "This download is no longer present. Refresh Download Center.";
            return false;
        }

        switch (normalizedAction)
        {
            case "pause" when entry.CanPause:
                return UnifySteamLauncher.TryPauseDownload(
                    normalizedStoreId,
                    normalizedGameId,
                    out message);
            case "resume" when entry.CanResume:
                return TryStartUnifyStoreInstall(
                    normalizedStoreId,
                    normalizedGameId,
                    out message,
                    allowDisabledExistingDownload: true);
            case "cancel" when entry.CanCancel:
                if (!UnifySteamLauncher.TryPrepareManagedDownloadCancellation(
                        normalizedStoreId,
                        normalizedGameId,
                        out message))
                {
                    return false;
                }

                return TryStartUnifyDownloadCancellation(
                    entry,
                    out message);
            case "stop-tracking" when entry.CanStopTracking:
                if (!UnifySteamLauncher.TryStopTrackingDownload(
                        normalizedStoreId,
                        normalizedGameId,
                        out message))
                {
                    return false;
                }

                message =
                    $"{entry.GameTitle} was removed from Download Center. " +
                    $"This does not cancel work already owned by {entry.TransferOwner}.";
                return true;
            case "dismiss" when entry.CanDismiss:
                UnifySteamDownloadStatusStore.Clear(
                    normalizedStoreId,
                    normalizedGameId);
                message = $"{entry.GameTitle} was removed from Download Center.";
                return true;
            case "play" when entry.CanPlay:
                return TryStartUnifyStoreLaunch(
                    normalizedStoreId,
                    normalizedGameId,
                    out message);
            case "manage" when entry.CanManageExternally:
                return UnifySteamLauncher.TryOpenExternalDownloadManager(
                    normalizedStoreId,
                    normalizedGameId,
                    out message);
            case "retry-uninstall" when entry.CanRetryUninstall:
                return TryStartUnifyStoreUninstall(
                    normalizedStoreId,
                    normalizedGameId,
                    out message);
            default:
                message =
                    "That action is no longer available for the current download state.";
                return false;
        }
    }

    private static bool TryStartUnifyDownloadCancellation(
        OmniLibraryDownloadCenterEntry entry,
        out string message)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath))
        {
            message = "Tools for Steam could not resolve its cleanup worker.";
            return false;
        }

        var current = UnifySteamDownloadStatusStore.Get(
            entry.StoreId,
            entry.GameId);
        UnifySteamDownloadStatusStore.Update(
            entry.StoreId,
            entry.GameId,
            "canceling",
            current.ProgressPercent,
            "Stopping the transfer and removing its partial files.",
            workerProcessId: 0,
            downloadedBytes: current.DownloadedBytes,
            totalBytes: current.TotalBytes,
            attempt: current.Attempt,
            gameTitle: entry.GameTitle,
            steamAppId: entry.SteamAppId);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory =
                    Path.GetDirectoryName(executablePath) ??
                    AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(
                UnifySteamLauncher.CancelDownloadArgument);
            startInfo.ArgumentList.Add($"{entry.StoreId}:{entry.GameId}");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException(
                    "Windows did not start the cleanup worker.");
            }

            UnifySteamLauncher.AssignDownloadWorkerIfUnclaimed(
                entry.StoreId,
                entry.GameId,
                process.Id);
            message =
                $"Canceling {entry.GameTitle} and removing its partial files.";
            return true;
        }
        catch (Exception exception)
        {
            UnifySteamDownloadStatusStore.Update(
                entry.StoreId,
                entry.GameId,
                "cancel-failed",
                current.ProgressPercent,
                $"The cleanup worker could not start: {exception.Message}",
                workerProcessId: 0,
                downloadedBytes: current.DownloadedBytes,
                totalBytes: current.TotalBytes,
                attempt: current.Attempt);
            message = "The download cleanup worker could not be started.";
            return false;
        }
    }

    private static bool IsSafeUnifyStoreLaunchId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(character =>
                   char.IsLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
    }

    private static bool TryResolvePluginId(string? path, out string pluginId)
    {
        pluginId = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path.TrimEnd('/');
        var pluginPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/api/audio"] = "audio",
            ["/api/app-start"] = "app-start",
            ["/api/auto-sisr"] = "auto-sisr",
            ["/api/discord"] = "discord",
            ["/api/performance"] = "performance",
            ["/api/processes"] = "processes",
            ["/api/curtain"] = "curtain",
            ["/api/trailers"] = "trailers",
            ["/api/popups"] = "popups",
            ["/api/overture"] = "overture",
            ["/api/metadata"] = "metadata",
            ["/api/artwork"] = "artwork",
            ["/api/hltb"] = "hltb",
            ["/api/smart-home"] = "smart-home",
            // OmniLibrary shares the /api/store-sync/* path prefix with the
            // classic Store Sync plugin but is a separate, independently
            // toggleable plugin ("OmniLibrary is an alternative to Store
            // Sync"). This more specific prefix must win so disabling classic
            // Store Sync never blocks OmniLibrary's own sync/refresh calls.
            ["/api/store-sync/unifysteam"] = "omnilibrary",
            ["/api/store-sync"] = "store-sync",
            ["/api/unifystore"] = "omnilibrary",
            ["/api/themes"] = "themes",
            ["/api/power"] = "power"
        };

        foreach (var (prefix, id) in pluginPrefixes.OrderByDescending(pair => pair.Key.Length))
        {
            if (normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                pluginId = id;
                return true;
            }
        }

        return false;
    }

    internal static bool TryNormalizeMetadataUrl(string? value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var allowedHosts = new[]
        {
            "store.steampowered.com",
            "steamcommunity.com",
            "www.youtube.com",
            "youtube.com",
            "youtu.be",
            "www.ign.com",
            "ign.com",
            "rawg.io",
            "www.rawg.io",
            "xbox.com",
            "www.xbox.com",
            "store-images.s-microsoft.com",
            "cdn.akamai.steamstatic.com",
            "cdn.cloudflare.steamstatic.com",
            "shared.fastly.steamstatic.com",
            "retroachievements.org",
            "www.retroachievements.org",
            "media.retroachievements.org",
        };
        if (!allowedHosts.Any(host =>
                uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private async Task<UpdateCheckSnapshot> GetUpdateSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await _updateSnapshotGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedUpdateSnapshot?.InstallInProgress == true)
            {
                return _cachedUpdateSnapshot;
            }

            if (!forceRefresh &&
                _cachedUpdateSnapshot is not null &&
                DateTimeOffset.UtcNow - _cachedUpdateSnapshot.CheckedAtUtc <= UpdateSnapshotCacheDuration)
            {
                return _cachedUpdateSnapshot;
            }
        }
        finally
        {
            _updateSnapshotGate.Release();
        }

        var snapshot = await _releaseUpdateService.CheckAsync(cancellationToken);
        return await CacheUpdateSnapshotAsync(snapshot, cancellationToken);
    }

    private async Task<UpdateCheckSnapshot> CacheUpdateSnapshotAsync(
        UpdateCheckSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        snapshot = snapshot with { AutomaticInstall = AutomaticUpdatesEnabled };
        await _updateSnapshotGate.WaitAsync(cancellationToken);
        try
        {
            _cachedUpdateSnapshot = snapshot;
        }
        finally
        {
            _updateSnapshotGate.Release();
        }

        _liveUpdateHub.Publish("updates.state", snapshot);
        return snapshot;
    }

    private static bool AutomaticUpdatesEnabled =>
        AutomaticUpdatePolicy.IsInstalledBuild(AppContext.BaseDirectory);

    /// <summary>
    /// Keeps an installed TFS on the newest stable release without any user
    /// action: checks GitHub a few minutes after start and then every few
    /// hours, and installs only while no game is running. A development build
    /// never updates itself.
    /// </summary>
    private async Task AutomaticUpdateLoopAsync(CancellationToken cancellationToken)
    {
        if (!AutomaticUpdatesEnabled)
        {
            return;
        }

        var attemptPath = Path.Combine(AppContext.BaseDirectory, "data", AutomaticUpdatePolicy.AttemptFileName);
        try
        {
            await Task.Delay(AutomaticUpdateStartupDelay, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var nextCheckAt = DateTimeOffset.UtcNow + AutomaticUpdateCheckInterval;
                var snapshot = await GetUpdateSnapshotAsync(forceRefresh: true, cancellationToken);
                if (AutomaticUpdatePolicy.ShouldInstall(
                        snapshot,
                        AutomaticUpdatePolicy.ReadAttempt(attemptPath),
                        DateTimeOffset.UtcNow))
                {
                    // Wait for a quiet moment; the GitHub answer stays valid
                    // until the next regular check.
                    while (AutomaticUpdatePolicy.IsUserBusy() && DateTimeOffset.UtcNow < nextCheckAt)
                    {
                        await Task.Delay(AutomaticUpdateIdlePollInterval, cancellationToken);
                    }

                    var executablePath = Environment.ProcessPath;
                    if (!AutomaticUpdatePolicy.IsUserBusy() && !string.IsNullOrWhiteSpace(executablePath))
                    {
                        AutomaticUpdatePolicy.WriteAttempt(
                            attemptPath,
                            new AutomaticUpdateAttempt(snapshot.LatestVersion!, DateTimeOffset.UtcNow));
                        await BeginBackgroundUpdateInstallAsync(executablePath, cancellationToken);
                    }
                }

                var remaining = nextCheckAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The automatic path must never take the host down; the manual
            // update button keeps working.
            Debug.WriteLine($"Automatic update loop stopped: {exception}");
        }
    }

    private async Task<UpdateCheckSnapshot> BeginBackgroundUpdateInstallAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        Task? activeTask;
        lock (_updateInstallGate)
        {
            activeTask = _activeUpdateInstallTask is { IsCompleted: false }
                ? _activeUpdateInstallTask
                : null;
        }

        if (activeTask is not null)
        {
            return await GetUpdateSnapshotAsync(forceRefresh: false, cancellationToken);
        }

        var basisSnapshot = await GetUpdateSnapshotAsync(forceRefresh: true, cancellationToken);
        if (!basisSnapshot.UpdateAvailable || !basisSnapshot.CanInstall)
        {
            return basisSnapshot;
        }

        var pendingSnapshot = basisSnapshot with
        {
            Message = $"Please wait. Downloading {basisSnapshot.LatestVersion ?? "the update"} in the background...",
            CheckedAtUtc = DateTimeOffset.UtcNow,
            InstallInProgress = true,
            InstallState = "starting",
            InstallProgressPercent = 0
        };
        pendingSnapshot = await CacheUpdateSnapshotAsync(pendingSnapshot, cancellationToken);

        Task installTask;
        lock (_updateInstallGate)
        {
            if (_activeUpdateInstallTask is { IsCompleted: false })
            {
                return _cachedUpdateSnapshot ?? pendingSnapshot;
            }

            installTask = Task.Run(async () =>
            {
                try
                {
                    var finalSnapshot = await _releaseUpdateService.BeginInstallLatestAsync(
                        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                        executablePath,
                        GetUpdateProcessIdsToWaitFor(executablePath),
                        async snapshot => await CacheUpdateSnapshotAsync(snapshot, CancellationToken.None),
                        CancellationToken.None);

                    await CacheUpdateSnapshotAsync(finalSnapshot, CancellationToken.None);
                    if (finalSnapshot.InstallInProgress)
                    {
                        // The handoff script waits for this process to exit.
                        await Task.Delay(500);
                        _requestShutdown();
                    }
                }
                catch (Exception exception)
                {
                    var failedSnapshot = basisSnapshot with
                    {
                        Message = $"Update install failed: {exception.Message}",
                        CheckedAtUtc = DateTimeOffset.UtcNow,
                        InstallInProgress = false,
                        InstallState = "failed",
                        InstallProgressPercent = null
                    };

                    await CacheUpdateSnapshotAsync(failedSnapshot, CancellationToken.None);
                }
                finally
                {
                    lock (_updateInstallGate)
                    {
                        _activeUpdateInstallTask = null;
                    }
                }
            });

            _activeUpdateInstallTask = installTask;
        }

        return pendingSnapshot;
    }

    private static IReadOnlyList<int> GetUpdateProcessIdsToWaitFor(string executablePath)
    {
        var normalizedExecutablePath = NormalizeExecutablePath(executablePath);
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        var processIds = new HashSet<int> { Environment.ProcessId };

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        processIds.Add(process.Id);
                        continue;
                    }

                    string? processPath = null;
                    try
                    {
                        processPath = process.MainModule?.FileName;
                    }
                    catch
                    {
                    }

                    if (!string.IsNullOrWhiteSpace(processPath) &&
                        string.Equals(NormalizeExecutablePath(processPath), normalizedExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                    }
                }
            }
        }
        catch
        {
        }

        return processIds
            .Where(id => id > 0)
            .OrderBy(id => id)
            .ToArray();
    }

    private static string NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private sealed record SetDefaultDeviceRequest(string DeviceId);

    private sealed record SetVolumeRequest(double Volume);

    private sealed record AdjustVolumeRequest(double Delta);

    private sealed record SetAudioMixerSessionVolumeRequest(string SessionId, double Volume);

    private sealed record ToggleAudioMixerSessionRequest(string SessionId);

    private sealed record ToggleSettingRequest(string Key);

    private sealed record SetTextValueRequest(string Value);

    private sealed record SetPerformanceOverlayLevelRequest(int Level);

    private sealed record SetPerformanceVendorOverlayRequest(string VendorId);

    private sealed record SetPerformanceIntegerSettingRequest(string Key, int Value);

    private sealed record SetHandheldTdpRequest(int Watts);

    private sealed record SetHandheldLightingRequest(
        bool Enabled, string Effect, string LeftColor, string RightColor, string ButtonColor, int Brightness);

    private sealed record SetHandheldPowerTdpRequest(int Watts, string PowerSource);

    private sealed record SetHandheldGameProfileRequest(string Key, int Watts, string PowerSource);

    private sealed record SetHandheldModeRequest(string ModeId);

    private sealed record DeleteHandheldProfileRequest(string Key);

    private sealed record SetBooleanValueRequest(bool Value);

    private sealed record BluetoothPairRequest(string DeviceId);

    private sealed record SetDiscordSettingsRequest(string ApplicationId, string ServerId, string InviteUrl);

    private sealed record DiscordIdRequest(string Id);

    private sealed record SetDiscordGuildFavoriteRequest(string Id, bool Favorite);

    private sealed record SetIntegerValueRequest(int Value);
    private sealed record SetOverlayButtonRequest(string Key, string Value);
    private sealed record SetOverlayCombinationRequest(string Action, IReadOnlyList<string>? Buttons);
    private sealed record SetOverlayHoldTimeRequest(string Key, int Value);
    private sealed record SetDisplayModeRequest(string Resolution, int RefreshRate);
    private sealed record SetHandheldCpuBoostRequest(string PowerSource, bool Enabled);

    private sealed record OemButtonRequest(string ButtonId);
    private sealed record UiHapticRequest(string Kind);

    private sealed record SetOemButtonBindingRequest(string ButtonId, string ActionId, string CustomShortcut);


    private sealed record ApplyArtworkRequest(long AppId, string AssetType, string Url);

    private sealed record SetCurtainGameEnabledRequest(uint AppId, bool Enabled);

    private sealed record ShowCurtainRequest(uint AppId, string? Title, string? Reason = null, bool? BigPicture = null);

    private sealed record CurtainAppRequest(uint AppId);

    private sealed record CurtainDismissRequest(string? Reason);

    private sealed record SaveCurtainGameRequest(uint AppId, CurtainGameSettings? Settings, bool? Enabled);

    private sealed record SetStartupModeRequest(string Mode);

    private sealed record SetPluginEnabledRequest(string PluginId, bool Enabled);

    private sealed record SetPluginOrderRequest(IReadOnlyList<string> PluginIds);

    private sealed record SetPluginStorePluginRequest(string PluginId);

    private sealed record PluginStoreInputRequest(string Action, string Source);

    private sealed record UnifyStoreLaunchRequest(string StoreId, string GameId);

    private sealed record UnifyStoreDownloadActionRequest(
        string StoreId,
        string GameId,
        string Action);

    private sealed record SetStorePriceAlertRequest(
        long? SteamAppId,
        string? GameId,
        string Title,
        decimal TargetPrice,
        string CurrencyCode,
        bool Enabled,
        string? Mode,
        int TargetDiscountPercent,
        DateTimeOffset? SnoozedUntilUtc);

    private sealed record SetStoreWishlistMetadataRequest(
        string GameId,
        bool? IsPinned,
        IReadOnlyList<string>? Tags);

    private sealed record SetStoreWishlistBulkRequest(
        IReadOnlyList<string>? GameIds,
        bool? IsPinned,
        string? AddTag,
        bool RemoveLocal,
        decimal? AlertMultiplier,
        string? AlertCurrencyCode);

    private sealed record SetStorePreferencesRequest(
        bool? IncludeKeyshops,
        int? RefreshIntervalMinutes,
        bool? NotificationsEnabled);

    private sealed record SetStoreArtworkCacheRequest(
        int MaximumMegabytes,
        int RetentionDays);

    private sealed record ImportStoreBackupRequest(string Json);

    private sealed record OpenStoreDealRequest(string DealUrl);

    private sealed record SetStoreWishlistRequest(StoreGameState Game, bool Enabled);

    private sealed record SetStoreEnabledRequest(string StoreId, bool Enabled);

    private sealed record SetXboxSourceEnabledRequest(string SourceId, bool Enabled);

    private sealed record SetStorePathRequest(string StoreId, string? Value);

    private sealed record SetRomSystemSettingsRequest(
        string SystemId,
        string? EmulatorPath,
        bool Fullscreen,
        string? EmulatorId = null);

    private sealed record InstallRomEmulatorRequest(
        string SystemId,
        string? EmulatorId);

    private sealed record SetEmulationOptionsRequest(
        bool AutoInstallEmulators,
        bool ImportOrbitRoms,
        bool? ImportEmulatorLibraries = null);

    private sealed record SetStoreDownloadOptionsRequest(
        string StoreId,
        int DownloadWorkers,
        int DownloadTimeoutSeconds);

    private sealed record SetStoreAchievementOptionsRequest(
        string StoreId,
        bool Enabled,
        string? Credential);

    private sealed record SetGameDataProviderOptionsRequest(
        string ProviderId,
        bool Enabled,
        string? Credential,
        string? SecondaryCredential,
        string? AccountId,
        string? AccountName,
        string? Region,
        string? Locale,
        string? DataPath);

    private sealed record SetGameDataEnabledRequest(bool Enabled);

    private sealed record TestGameDataProviderRequest(string ProviderId);

    private sealed record SetSteamAppIdRequest(uint SteamAppId);

    private sealed record ConfirmedRequest(bool Confirmed);

    private sealed record SetGogOptionsRequest(
        bool IncludeDlc,
        bool PreferGalaxyForLaunch);

    private sealed record SetStorePathsRequest(string StoreId, IReadOnlyList<string>? Values);

    private sealed record SetStoreSyncTitleOverrideRequest(
        string TitleId,
        string? TitleOverride,
        string? ArtworkTitleOverride,
        bool Excluded);

    private sealed record SetSmartHomeCapabilityRequest(
        string DeviceId,
        string CapabilityId,
        JsonElement Value);

    private sealed record RunSmartHomeFlowRequest(
        string FlowId,
        bool IsAdvanced);

    private sealed record RunSmartHomeMoodRequest(
        string MoodId);

    private sealed record ShowSteamKeyboardRequest(
        string? Label,
        string? Value,
        double? X,
        double? Y,
        double? Width,
        double? Height);

    private sealed record SteamKeyboardOpenResult(
        bool Success,
        string Message,
        object? Details);

    private sealed record SetThemeInstalledRequest(string ThemeId, bool Installed);

    private sealed record SetThemeEnabledRequest(string ThemeId, bool Enabled);

    private sealed record SetStoreThemeRequest(string StoreThemeId);

    private sealed record SetThemeOptionRequest(string ThemeId, string OptionId);

    private sealed record SetThemeChoiceRequest(string ThemeId, string OptionId, string ChoiceId);

    private sealed record SetThemeRangeRequest(string ThemeId, string OptionId, int Delta);

    private sealed record SetProfileRequest(string ProfileId);
}
