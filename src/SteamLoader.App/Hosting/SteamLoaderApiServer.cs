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
using SteamLoader.App.Infrastructure.Hltb;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.PluginStore;
using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.SmartHome;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Infrastructure.Themes;
using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.Hosting;

public sealed class SteamLoaderApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan UpdateSnapshotCacheDuration = TimeSpan.FromMinutes(15);

    private readonly IAudioOutputDeviceService _audioOutputDeviceService;
    private readonly DisplaySwitchService _displaySwitchService;
    private readonly ProcessWindowService _processWindowService;
    private readonly SteamGridDbManualArtworkService _artworkService;
    private readonly AutoSisirService _autoSisirService;
    private readonly AppStartService _appStartService;
    private readonly HltbService _hltbService;
    private readonly StoreSyncService _storeSyncService;
    private readonly ThemesService _themesService;
    private readonly TfsPerformanceService _performanceService;
    private readonly HandheldPerformanceService _handheldPerformanceService;
    private readonly SteamLoaderSettingsService _steamLoaderSettingsService;
    private readonly PluginStoreService _pluginStoreService;
    private readonly PowerActionService _powerActionService;
    private readonly ReleaseUpdateService _releaseUpdateService;
    private readonly SteamFrontendComponentService _frontendComponentService;
    private readonly SteamDevToolsClient _devToolsClient;
    private readonly SmartHomeService _smartHomeService;
    private readonly DiscordService _discordService;
    private readonly PluginFullTrustRuntime _pluginFullTrustRuntime;
    private readonly ExternalGameQuickAccessService _externalGameQuickAccessService;
    private readonly SteamLoaderHostState _hostState;
    private readonly QuickAccessLiveUpdateHub _liveUpdateHub;
    private readonly HttpListener _listener;
    private readonly Action _requestShutdown;
    private readonly string _apiSessionToken;
    private readonly object _artworkOpenRequestLock = new();
    private readonly object _unifyStoreOverlayLock = new();
    private ArtworkOpenRequest? _latestArtworkOpenRequest;
    private DateTimeOffset _latestArtworkOpenRequestAt = DateTimeOffset.MinValue;
    private string _latestArtworkOpenRequestKey = string.Empty;
    private long _artworkOpenRequestNonce;
    private readonly SemaphoreSlim _updateSnapshotGate = new(1, 1);
    private readonly object _updateInstallGate = new();
    private UpdateCheckSnapshot? _cachedUpdateSnapshot;
    private Task? _activeUpdateInstallTask;
    private Task? _acceptLoopTask;
    private bool _unifyStoreOverlayOpen;

    public SteamLoaderApiServer(
        IAudioOutputDeviceService audioOutputDeviceService,
        DisplaySwitchService displaySwitchService,
        ProcessWindowService processWindowService,
        SteamGridDbManualArtworkService artworkService,
        AutoSisirService autoSisirService,
        AppStartService appStartService,
        HltbService hltbService,
        StoreSyncService storeSyncService,
        ThemesService themesService,
        TfsPerformanceService performanceService,
        HandheldPerformanceService handheldPerformanceService,
        SteamLoaderSettingsService steamLoaderSettingsService,
        PluginStoreService pluginStoreService,
        PowerActionService powerActionService,
        ReleaseUpdateService releaseUpdateService,
        SteamFrontendComponentService frontendComponentService,
        SteamDevToolsClient devToolsClient,
        SmartHomeService smartHomeService,
        DiscordService discordService,
        PluginFullTrustRuntime pluginFullTrustRuntime,
        ExternalGameQuickAccessService externalGameQuickAccessService,
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
        _autoSisirService = autoSisirService;
        _appStartService = appStartService;
        _hltbService = hltbService;
        _storeSyncService = storeSyncService;
        _themesService = themesService;
        _performanceService = performanceService;
        _handheldPerformanceService = handheldPerformanceService;
        _steamLoaderSettingsService = steamLoaderSettingsService;
        _pluginStoreService = pluginStoreService;
        _powerActionService = powerActionService;
        _releaseUpdateService = releaseUpdateService;
        _frontendComponentService = frontendComponentService;
        _devToolsClient = devToolsClient;
        _smartHomeService = smartHomeService;
        _discordService = discordService;
        _pluginFullTrustRuntime = pluginFullTrustRuntime;
        _externalGameQuickAccessService = externalGameQuickAccessService;
        _apiSessionToken = apiSessionToken;
        _hostState = hostState;
        _liveUpdateHub = liveUpdateHub;
        _requestShutdown = requestShutdown;
        _listener = new HttpListener();
        _listener.Prefixes.Add(baseUri.ToString());
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(cancellationToken), cancellationToken);
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
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _pluginFullTrustRuntime.DisposeAsync();
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
                    new { message = "Storefront is disabled in this build." },
                    cancellationToken);
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
                await WriteEventStreamAsync(response, cancellationToken);
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
                request.Url?.AbsolutePath == "/api/plugin-store/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _pluginStoreService.GetSnapshotAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.StartsWith("/api/plugin-store/images/built-in/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var imagePluginId = Uri.UnescapeDataString(
                    request.Url.AbsolutePath["/api/plugin-store/images/built-in/".Length..]);
                if (!_pluginStoreService.TryGetBuiltInImage(imagePluginId, out var imagePath, out var contentType))
                {
                    await WriteTextAsync(
                        response,
                        HttpStatusCode.NotFound,
                        "Plugin store image not found.",
                        cancellationToken);
                    return;
                }

                await WriteFileAsync(response, imagePath, contentType, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.StartsWith("/api/plugin-store/images/catalog/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var imageFileName = Uri.UnescapeDataString(
                    request.Url.AbsolutePath["/api/plugin-store/images/catalog/".Length..]);
                if (!_pluginStoreService.TryGetCatalogImage(imageFileName, out var imagePath, out var contentType))
                {
                    await WriteTextAsync(
                        response,
                        HttpStatusCode.NotFound,
                        "Plugin store catalog image not found.",
                        cancellationToken);
                    return;
                }

                await WriteFileAsync(response, imagePath, contentType, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/community/installed")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.GetCommunityRuntimeState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                TryParseCommunityPluginFilePath(
                    request.Url?.AbsolutePath,
                    out var communityPluginId,
                    out var communityRelativePath))
            {
                if (!_pluginStoreService.TryGetCommunityPluginFile(
                    communityPluginId,
                    communityRelativePath,
                    out var communityFilePath,
                    out var communityContentType))
                {
                    await WriteTextAsync(
                        response,
                        HttpStatusCode.NotFound,
                        "Community plugin file not found.",
                        cancellationToken);
                    return;
                }

                await WriteFileAsync(response, communityFilePath, communityContentType, cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/overlay/state")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.GetOverlayState(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/overlay/open")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.SetOverlayOpen(true),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/overlay/close")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _pluginStoreService.SetOverlayOpen(false),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/overlay/input")
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
                request.Url?.AbsolutePath == "/api/plugin-store/overlay/input")
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
                        new { message = "A store input action is required." },
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
                request.Url?.AbsolutePath == "/api/plugin-store/refresh")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await _pluginStoreService.RefreshAsync(cancellationToken),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/plugins/install")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetPluginStorePluginRequest>(
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

                var pluginStoreSnapshot = await _pluginStoreService.InstallCommunityPluginAsync(payload.PluginId, cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    pluginStoreSnapshot,
                    "plugin-store.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/plugins/uninstall")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetPluginStorePluginRequest>(
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

                _pluginFullTrustRuntime.StopAll(payload.PluginId);
                var pluginStoreSnapshot = await _pluginStoreService.UninstallCommunityPluginAsync(payload.PluginId, cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    pluginStoreSnapshot,
                    "plugin-store.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/plugin-store/plugins/update")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetPluginStorePluginRequest>(
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

                _pluginFullTrustRuntime.StopAll(payload.PluginId);
                var pluginStoreSnapshot = await _pluginStoreService.UpdateCommunityPluginAsync(payload.PluginId, cancellationToken);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    pluginStoreSnapshot,
                    "plugin-store.state",
                    cancellationToken);
                return;
            }

            if (TryParsePluginSdkPath(request.Url?.AbsolutePath, out var sdkPluginId, out var sdkPath))
            {
                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "state")
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.GetPluginSdkState(sdkPluginId),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath.StartsWith("capabilities/", StringComparison.OrdinalIgnoreCase))
                {
                    var capability = sdkPath["capabilities/".Length..].Trim('/');
                    if (capability.Length == 0 || capability.Contains('/'))
                    {
                        await WriteJsonAsync(
                            response,
                            HttpStatusCode.BadRequest,
                            new { message = "A valid SDK capability is required." },
                            cancellationToken);
                        return;
                    }

                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkCapabilityRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        await ExecutePluginSdkCapabilityAsync(
                            sdkPluginId,
                            capability,
                            payload,
                            cancellationToken),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "settings")
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.GetPluginSdkSettings(sdkPluginId),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "settings")
                {
                    var payload = await JsonSerializer.DeserializeAsync<JsonElement>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);

                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.SetPluginSdkSettings(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "secrets")
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.GetPluginSdkSecrets(sdkPluginId),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    TryParsePluginSdkSecretPath(sdkPath, out var sdkSecretKey, out var clearSecret) &&
                    clearSecret)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.ClearPluginSdkSecret(sdkPluginId, sdkSecretKey),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    TryParsePluginSdkSecretPath(sdkPath, out sdkSecretKey, out clearSecret) &&
                    !clearSecret)
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
                            new { message = "A secret value is required." },
                            cancellationToken);
                        return;
                    }

                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.SetPluginSdkSecret(sdkPluginId, sdkSecretKey, payload.Value),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "network/request")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkNetworkRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);

                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        await _pluginStoreService.SendPluginSdkNetworkRequestAsync(
                            sdkPluginId,
                            payload,
                            cancellationToken),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/list")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFileListRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.ListPluginSdkFiles(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/stat")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFilePathRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.GetPluginSdkFileInfo(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/read")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFileReadRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.ReadPluginSdkFile(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/write")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFileWriteRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.WritePluginSdkFile(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/directory")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFilePathRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.CreatePluginSdkDirectory(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/delete")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFilePathRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.DeletePluginSdkFile(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/move")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFileTransferRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.MovePluginSdkFile(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "files/copy")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkFileTransferRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.CopyPluginSdkFile(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "notifications/show")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkNotificationRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.CreatePluginSdkNotification(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    sdkPath == "logs/write")
                {
                    var payload = await JsonSerializer.DeserializeAsync<PluginSdkLogRequest>(
                        request.InputStream,
                        JsonOptions,
                        cancellationToken);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        _pluginStoreService.WritePluginSdkLog(sdkPluginId, payload),
                        cancellationToken);
                    return;
                }
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
                request.Url?.AbsolutePath == "/api/settings/splash/enabled")
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

                var settingsSnapshot = _steamLoaderSettingsService.SetSplashScreenEnabled(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/splash/show-text")
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

                var settingsSnapshot = _steamLoaderSettingsService.SetSplashScreenShowText(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/splash/wallpaper")
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
                        new { message = "A wallpaper path is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetSplashScreenWallpaperPath(payload.Value);
                await WriteJsonAndPublishAsync(
                    response,
                    HttpStatusCode.OK,
                    settingsSnapshot,
                    "settings.state",
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/settings/splash/icon")
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
                        new { message = "An icon path is required." },
                        cancellationToken);
                    return;
                }

                var settingsSnapshot = _steamLoaderSettingsService.SetSplashScreenIconPath(payload.Value);
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
                request.Url?.AbsolutePath == "/api/updates/channel")
            {
                var payload = await JsonSerializer.DeserializeAsync<SetUpdateChannelRequest>(
                    request.InputStream,
                    JsonOptions,
                    cancellationToken);

                if (payload is null || string.IsNullOrWhiteSpace(payload.Channel))
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.BadRequest,
                        new { message = "An update channel is required." },
                        cancellationToken);
                    return;
                }

                var normalizedChannel = _steamLoaderSettingsService.SetUpdateChannel(payload.Channel);
                InvalidateUpdateSnapshotCache();

                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    await GetUpdateSnapshotAsync(forceRefresh: true, cancellationToken, normalizedChannel),
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

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/display/internal")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _displaySwitchService.SwitchToInternalDisplay(),
                    cancellationToken);
                return;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath == "/api/display/external")
            {
                await WriteJsonAsync(
                    response,
                    HttpStatusCode.OK,
                    _displaySwitchService.SwitchToExternalDisplay(),
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
                        new { message = "A Storefront store ID is required." },
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
                        new { message = "A Storefront store ID and game ID are required." },
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
                        new { message = "A Storefront store ID is required." },
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
                        new { message = "A Storefront store ID is required." },
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
                        new { message = "A Storefront store ID is required." },
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

  const steamRequire = getSteamRequire();
  if (!steamRequire) {
    return {
      success: false,
      message: "Steam webpack runtime is not available."
    };
  }

  const gamepadStore = steamRequire(61236)?.oy;
  const windowInstance =
    gamepadStore?.ActiveWindowInstance ||
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

        return new SteamKeyboardOpenResult(
            true,
            "Steam virtual keyboard requested.",
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

    private static async Task WriteFileAsync(
        HttpListenerResponse response,
        string path,
        string contentType,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = contentType;
        response.ContentLength64 = fileInfo.Length;
        response.Headers["Cache-Control"] = "no-cache, no-store";

        await using var input = File.OpenRead(path);
        await using var output = response.OutputStream;
        await input.CopyToAsync(output, cancellationToken);
    }

    private async Task WriteEventStreamAsync(
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache, no-store";
        response.Headers["Connection"] = "keep-alive";
        response.SendChunked = true;
        response.KeepAlive = true;

        using var subscription = _liveUpdateHub.Subscribe();
        await using var output = response.OutputStream;
        using var writer = new StreamWriter(output, new UTF8Encoding(false));

        try
        {
            await writer.WriteAsync(": connected\n\n");
            await writer.FlushAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var readTask = subscription.Reader.ReadAsync(cancellationToken).AsTask();
                var keepAliveTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var completedTask = await Task.WhenAny(readTask, keepAliveTask);

                if (completedTask == keepAliveTask)
                {
                    await writer.WriteAsync(": keepalive\n\n");
                    await writer.FlushAsync(cancellationToken);
                    continue;
                }

                var payload = await readTask;
                await writer.WriteAsync($"data: {payload}\n\n");
                await writer.FlushAsync(cancellationToken);
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

    private PluginStoreOverlayState SetUnifyStoreOverlayOpen(bool open)
    {
        lock (_unifyStoreOverlayLock)
        {
            _unifyStoreOverlayOpen = open;
            return new PluginStoreOverlayState(_unifyStoreOverlayOpen);
        }
    }

    private bool TryStartUnifyStoreLaunch(string storeId, string gameId, out string message)
    {
        var normalizedStoreId = (storeId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedGameId = (gameId ?? string.Empty).Trim();

        if (normalizedStoreId is not ("epic-games" or "gog-galaxy"))
        {
            message = "Storefront can launch Epic and GOG games right now.";
            return false;
        }

        if (!IsSafeUnifyStoreLaunchId(normalizedGameId))
        {
            message = "The Storefront game ID is invalid.";
            return false;
        }

        var snapshot = _storeSyncService.GetSnapshot();
        var gameState = snapshot.UnifySteam.Stores
            .Where(store => string.Equals(store.Id, normalizedStoreId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(store => store.Games)
            .FirstOrDefault(game => string.Equals(game.Id, normalizedGameId, StringComparison.OrdinalIgnoreCase));
        if (gameState is null)
        {
            message = "The selected game is not in the cached Storefront library. Refresh Epic or GOG first.";
            return false;
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
            CreateNoWindow = false,
        };
        startInfo.ArgumentList.Add("--unifysteam-launch");
        startInfo.ArgumentList.Add($"{normalizedStoreId}:{normalizedGameId}");
        Process.Start(startInfo);

        message = normalizedStoreId.Equals("gog-galaxy", StringComparison.OrdinalIgnoreCase) && !gameState.Installed
            ? "Opening the GOG Galaxy game page. Install the game there, then refresh Storefront."
            : "Storefront started the launcher. If the game is not installed yet, the store tool will download it first.";
        return true;
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
            ["/api/display"] = "display",
            ["/api/discord"] = "discord",
            ["/api/performance"] = "performance",
            ["/api/processes"] = "processes",
            ["/api/artwork"] = "artwork",
            ["/api/hltb"] = "hltb",
            ["/api/smart-home"] = "smart-home",
            ["/api/store-sync"] = "store-sync",
            ["/api/themes"] = "themes",
            ["/api/power"] = "power"
        };

        foreach (var (prefix, id) in pluginPrefixes)
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

    private async Task<object> ExecutePluginSdkCapabilityAsync(
        string pluginId,
        string capability,
        PluginSdkCapabilityRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Operation))
        {
            throw new InvalidOperationException("An SDK capability operation is required.");
        }

        var normalizedCapability = capability.Trim().ToLowerInvariant();
        var permission = normalizedCapability switch
        {
            "audio" => "native.audio",
            "processes" => "native.processes",
            "display" => "native.display",
            "themes" => "native.themes",
            "artwork" => "native.artwork",
            "app-start" => "native.app-start",
            "store-sync" => "native.store-sync",
            "automation" => "native.automation",
            "performance" => "native.performance",
            "power" => "native.power",
            "system" or "filesystem" or "steam" => "native.full-trust",
            _ => throw new InvalidOperationException($"Unknown native SDK capability ({normalizedCapability}).")
        };
        _pluginStoreService.EnsurePluginSdkPermission(pluginId, permission);

        var operation = request.Operation.Trim().ToLowerInvariant();
        return normalizedCapability switch
        {
            "audio" => await ExecutePluginSdkAudioAsync(operation, request.Arguments, cancellationToken),
            "processes" => ExecutePluginSdkProcesses(operation, request.Arguments),
            "display" => ExecutePluginSdkDisplay(operation, request.Arguments),
            "themes" => await ExecutePluginSdkThemesAsync(operation, request.Arguments),
            "artwork" => await ExecutePluginSdkArtworkAsync(operation, request.Arguments, cancellationToken),
            "app-start" => ExecutePluginSdkAppStart(operation, request.Arguments),
            "store-sync" => await ExecutePluginSdkStoreSyncAsync(operation, request.Arguments, cancellationToken),
            "automation" => ExecutePluginSdkAutomation(operation, request.Arguments),
            "performance" => ExecutePluginSdkPerformance(operation, request.Arguments),
            "power" => ExecutePluginSdkPower(operation, request.Arguments),
            "system" => await _pluginFullTrustRuntime.ExecuteSystemAsync(pluginId, operation, request.Arguments, cancellationToken),
            "filesystem" => await _pluginFullTrustRuntime.ExecuteFileSystemAsync(pluginId, operation, request.Arguments, cancellationToken),
            "steam" => await _pluginFullTrustRuntime.ExecuteSteamAsync(pluginId, operation, request.Arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown native SDK capability ({normalizedCapability}).")
        };
    }

    private object ExecutePluginSdkPerformance(string operation, JsonElement? arguments)
    {
        return operation switch
        {
            "getstate" => _performanceService.GetSnapshot(),
            "setoverlaylevel" => _performanceService.SetOverlayLevel(GetCapabilityInt32(arguments, "level")),
            "toggleautotarget" => _performanceService.ToggleAutoTarget(),
            "setsettingvalue" => _performanceService.SetSettingValue(
                GetCapabilityString(arguments, "key"),
                GetCapabilityInt32(arguments, "value")),
            "startoverlay" => _performanceService.StartOverlay(),
            "stopoverlay" => _performanceService.StopOverlay(),
            "prepareelevatedhelper" => _performanceService.PrepareElevatedHelper(),
            _ => throw new InvalidOperationException($"Unknown native performance operation ({operation}).")
        };
    }

    private object ExecutePluginSdkPower(string operation, JsonElement? arguments)
    {
        if (operation == "getstate")
        {
            return new
            {
                actions = new[]
                {
                    new { id = "startWindowsDesktop", title = "Start Windows Desktop", disruptive = false },
                    new { id = "restartSteam", title = "Restart Steam", disruptive = true },
                    new { id = "sleepWindows", title = "Sleep Windows", disruptive = true },
                    new { id = "restartWindows", title = "Restart Windows", disruptive = true },
                    new { id = "shutdownWindows", title = "Shut Down Windows", disruptive = true }
                },
                confirmationRequired = true
            };
        }

        if (!GetCapabilityBoolean(arguments, "confirmed", false))
        {
            throw new InvalidOperationException("Native power actions require confirmed: true after an explicit user confirmation.");
        }

        return operation switch
        {
            "startwindowsdesktop" => _powerActionService.StartWindowsDesktop(),
            "restartsteam" => _powerActionService.RestartSteam(),
            "sleepwindows" => _powerActionService.SleepWindows(),
            "restartwindows" => _powerActionService.RestartWindows(),
            "shutdownwindows" => _powerActionService.ShutDownWindows(),
            _ => throw new InvalidOperationException($"Unknown native power operation ({operation}).")
        };
    }

    private async Task<object> ExecutePluginSdkAudioAsync(
        string operation,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        if (operation == "getstate")
        {
            return await GetAudioDashboardSnapshotAsync(cancellationToken);
        }

        await StaThread.RunAsync(
            () =>
            {
                switch (operation)
                {
                    case "setdefaultplayback":
                        _audioOutputDeviceService.SetDefaultPlaybackDevice(GetCapabilityString(arguments, "deviceId"));
                        break;
                    case "setdefaultcapture":
                        _audioOutputDeviceService.SetDefaultCaptureDevice(GetCapabilityString(arguments, "deviceId"));
                        break;
                    case "setplaybackvolume":
                        _audioOutputDeviceService.SetDefaultPlaybackVolume(GetCapabilityDouble(arguments, "volume"));
                        break;
                    case "setcapturevolume":
                        _audioOutputDeviceService.SetDefaultCaptureVolume(GetCapabilityDouble(arguments, "volume"));
                        break;
                    case "adjustplaybackvolume":
                        _audioOutputDeviceService.AdjustDefaultPlaybackVolume(GetCapabilityDouble(arguments, "delta"));
                        break;
                    case "adjustcapturevolume":
                        _audioOutputDeviceService.AdjustDefaultCaptureVolume(GetCapabilityDouble(arguments, "delta"));
                        break;
                    case "toggleplaybackmute":
                        _audioOutputDeviceService.ToggleDefaultPlaybackMute();
                        break;
                    case "togglecapturemute":
                        _audioOutputDeviceService.ToggleDefaultCaptureMute();
                        break;
                    case "setmixervolume":
                        _audioOutputDeviceService.SetMixerSessionVolume(
                            GetCapabilityString(arguments, "sessionId"),
                            GetCapabilityDouble(arguments, "volume"));
                        break;
                    case "togglemixermute":
                        _audioOutputDeviceService.ToggleMixerSessionMute(GetCapabilityString(arguments, "sessionId"));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown native audio operation ({operation}).");
                }

                return true;
            },
            cancellationToken);

        var snapshot = await GetAudioDashboardSnapshotAsync(cancellationToken);
        _liveUpdateHub.Publish("audio.dashboard", snapshot);
        return snapshot;
    }

    private object ExecutePluginSdkProcesses(string operation, JsonElement? arguments)
    {
        return operation switch
        {
            "getstate" => _processWindowService.GetSnapshot(),
            "activate" => _processWindowService.ActivateWindow(GetCapabilityString(arguments, "handle")),
            _ => throw new InvalidOperationException($"Unknown native processes operation ({operation}).")
        };
    }

    private object ExecutePluginSdkDisplay(string operation, JsonElement? arguments)
    {
        return operation switch
        {
            "getstate" => _displaySwitchService.GetModeSnapshot(),
            "switchinternal" => _displaySwitchService.SwitchToInternalDisplay(),
            "switchexternal" => _displaySwitchService.SwitchToExternalDisplay(),
            "setresolution" => _displaySwitchService.SetResolutionPreset(
                GetCapabilityString(arguments, "presetId")),
            "setrefreshrate" => _displaySwitchService.SetRefreshRatePreset(
                GetCapabilityInt32(arguments, "refreshRate")),
            _ => throw new InvalidOperationException($"Unknown native display operation ({operation}).")
        };
    }

    private async Task<object> ExecutePluginSdkThemesAsync(string operation, JsonElement? arguments)
    {
        return operation switch
        {
            "getstate" => await _themesService.GetSnapshotAsync(),
            "refreshcatalog" => await _themesService.RefreshCatalogAsync(),
            "getstorecatalog" => await _themesService.GetStoreCatalogAsync(
                GetCapabilityOptionalString(arguments, "search"),
                GetCapabilityOptionalString(arguments, "filter"),
                GetCapabilityOptionalString(arguments, "order"),
                GetCapabilityInt32(arguments, "page", 1),
                GetCapabilityInt32(arguments, "perPage", 12)),
            "getstoretheme" => await _themesService.GetStoreThemeAsync(
                GetCapabilityString(arguments, "storeThemeId")),
            "installstoretheme" => await _themesService.InstallStoreThemeAsync(
                GetCapabilityString(arguments, "storeThemeId")),
            "setenabled" => await _themesService.SetThemeEnabledAsync(
                GetCapabilityString(arguments, "themeId"),
                GetCapabilityBoolean(arguments, "enabled")),
            "toggleoption" => await _themesService.ToggleThemeOptionAsync(
                GetCapabilityString(arguments, "themeId"),
                GetCapabilityString(arguments, "optionId")),
            "setchoice" => await _themesService.SetThemeChoiceAsync(
                GetCapabilityString(arguments, "themeId"),
                GetCapabilityString(arguments, "optionId"),
                GetCapabilityString(arguments, "choiceId")),
            "adjustrange" => await _themesService.AdjustThemeRangeAsync(
                GetCapabilityString(arguments, "themeId"),
                GetCapabilityString(arguments, "optionId"),
                GetCapabilityInt32(arguments, "delta")),
            "resetrange" => await _themesService.ResetThemeRangeAsync(
                GetCapabilityString(arguments, "themeId"),
                GetCapabilityString(arguments, "optionId")),
            "createprofile" => await _themesService.CreateProfileAsync(
                GetCapabilityString(arguments, "title")),
            "applyprofile" => await _themesService.ApplyProfileAsync(
                GetCapabilityString(arguments, "profileId")),
            "updateprofile" => await _themesService.UpdateProfileAsync(
                GetCapabilityString(arguments, "profileId")),
            "removeprofile" => await _themesService.RemoveProfileAsync(
                GetCapabilityString(arguments, "profileId")),
            "setwatchenabled" => await _themesService.SetWatchEnabledAsync(
                GetCapabilityBoolean(arguments, "enabled")),
            _ => throw new InvalidOperationException($"Unknown native themes operation ({operation}).")
        };
    }

    private async Task<object> ExecutePluginSdkArtworkAsync(
        string operation,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            "getstate" => _artworkService.GetSnapshot(),
            "searchgames" => await _artworkService.SearchGamesAsync(
                GetCapabilityString(arguments, "term"),
                cancellationToken),
            "searchassets" => await _artworkService.SearchAssetsAsync(
                GetCapabilityInt32(arguments, "gameId"),
                GetCapabilityString(arguments, "assetType"),
                GetCapabilityInt32(arguments, "page", 0),
                cancellationToken),
            "apply" => await _artworkService.ApplyAssetAsync(
                GetCapabilityInt64(arguments, "appId"),
                GetCapabilityString(arguments, "assetType"),
                GetCapabilityString(arguments, "assetUrl"),
                cancellationToken),
            "togglesetting" => _artworkService.ToggleSetting(
                GetCapabilityString(arguments, "key")),
            "setresultlimit" => _artworkService.SetResultLimit(
                GetCapabilityInt32(arguments, "value")),
            _ => throw new InvalidOperationException($"Unknown native artwork operation ({operation}).")
        };
    }

    private object ExecutePluginSdkAppStart(string operation, JsonElement? arguments)
    {
        return operation switch
        {
            "getstate" => _appStartService.GetSnapshot(),
            "getcatalog" => _appStartService.GetCatalog(),
            "refreshcatalog" => _appStartService.RefreshCatalog(),
            "add" => _appStartService.AddShortcut(GetCapabilityString(arguments, "appId")),
            "remove" => _appStartService.RemoveShortcut(GetCapabilityString(arguments, "shortcutId")),
            "togglefavorite" => _appStartService.ToggleFavorite(GetCapabilityString(arguments, "shortcutId")),
            "launch" => _appStartService.LaunchShortcut(GetCapabilityString(arguments, "shortcutId")),
            _ => throw new InvalidOperationException($"Unknown native app-start operation ({operation}).")
        };
    }

    private async Task<object> ExecutePluginSdkStoreSyncAsync(
        string operation,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        object result = operation switch
        {
            "getstate" => _storeSyncService.GetSnapshot(),
            "gettitles" => string.IsNullOrWhiteSpace(GetCapabilityOptionalString(arguments, "storeId"))
                ? _storeSyncService.GetDetectedTitles()
                : _storeSyncService.GetDetectedTitlesByStore(GetCapabilityString(arguments, "storeId")),
            "getartworkpreview" => await _storeSyncService.GetArtworkPreviewAsync(
                GetCapabilityString(arguments, "titleId"),
                cancellationToken),
            "togglesetting" => _storeSyncService.ToggleSetting(GetCapabilityString(arguments, "key")),
            "setstoreenabled" => _storeSyncService.SetStoreEnabled(
                GetCapabilityString(arguments, "storeId"),
                GetCapabilityBoolean(arguments, "enabled")),
            "setstorepath" => _storeSyncService.SetStoreScanPath(
                GetCapabilityString(arguments, "storeId"),
                GetCapabilityString(arguments, "path")),
            "clearstorepath" => _storeSyncService.ClearStoreScanPath(
                GetCapabilityString(arguments, "storeId")),
            "setadditionalpaths" => _storeSyncService.SetStoreAdditionalScanPaths(
                GetCapabilityString(arguments, "storeId"),
                GetCapabilityStringArray(arguments, "paths")),
            "settitleoverride" => _storeSyncService.SetTitleOverride(
                GetCapabilityString(arguments, "titleId"),
                GetCapabilityOptionalString(arguments, "titleOverride") ?? string.Empty,
                GetCapabilityOptionalString(arguments, "artworkTitleOverride") ?? string.Empty,
                GetCapabilityBoolean(arguments, "excluded", false)),
            "cleartitleoverride" => _storeSyncService.ClearTitleOverride(
                GetCapabilityString(arguments, "titleId")),
            "sync" => _storeSyncService.RunSync(),
            "refreshstorefront" => _storeSyncService.RefreshUnifySteam(
                GetCapabilityOptionalString(arguments, "storeId")),
            "setstorefrontenabled" => _storeSyncService.SetUnifySteamStoreEnabled(
                GetCapabilityString(arguments, "storeId"),
                GetCapabilityBoolean(arguments, "enabled")),
            "startstorefrontlogin" => _storeSyncService.StartUnifySteamLogin(
                GetCapabilityString(arguments, "storeId")),
            "completestorefrontauth" => _storeSyncService.CompleteUnifySteamManualAuth(
                GetCapabilityString(arguments, "storeId"),
                GetCapabilityString(arguments, "value")),
            "launchstorefrontgame" => TryStartUnifyStoreLaunch(
                GetCapabilityString(arguments, "storeId"),
                GetCapabilityString(arguments, "gameId"),
                out var launchMessage)
                    ? new { success = true, message = launchMessage }
                    : new { success = false, message = launchMessage },
            _ => throw new InvalidOperationException($"Unknown native store-sync operation ({operation}).")
        };

        if (result is StoreSyncSnapshot snapshot && operation != "getstate")
        {
            _liveUpdateHub.Publish("store-sync.state", snapshot);
        }
        return result;
    }

    private object ExecutePluginSdkAutomation(string operation, JsonElement? arguments)
    {
        return operation switch
        {
            "getstate" => _autoSisirService.GetSnapshot(),
            "togglesetting" => _autoSisirService.ToggleSetting(
                GetCapabilityString(arguments, "key")),
            "setexecutablepath" => _autoSisirService.SetExecutablePath(
                GetCapabilityString(arguments, "path")),
            "resetexecutablepath" => _autoSisirService.ResetExecutablePath(),
            "togglewatchedtitle" => _autoSisirService.ToggleWatchedTitle(
                GetCapabilityString(arguments, "titleId")),
            _ => throw new InvalidOperationException($"Unknown native automation operation ({operation}).")
        };
    }

    private static JsonElement GetCapabilityArguments(JsonElement? arguments)
    {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }
        if (arguments.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("SDK capability arguments must be a JSON object.");
        }
        return arguments.Value;
    }

    private static string GetCapabilityString(JsonElement? arguments, string name)
    {
        var value = GetCapabilityOptionalString(arguments, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"SDK capability argument '{name}' is required.")
            : value;
    }

    private static string? GetCapabilityOptionalString(JsonElement? arguments, string name)
    {
        var value = GetCapabilityArguments(arguments);
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
    }

    private static double GetCapabilityDouble(JsonElement? arguments, string name)
    {
        var value = GetCapabilityArguments(arguments);
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var property) ||
            !property.TryGetDouble(out var result) ||
            double.IsNaN(result) ||
            double.IsInfinity(result))
        {
            throw new InvalidOperationException($"SDK capability argument '{name}' must be a number.");
        }
        return result;
    }

    private static int GetCapabilityInt32(JsonElement? arguments, string name, int? defaultValue = null)
    {
        var value = GetCapabilityArguments(arguments);
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.TryGetInt32(out var result))
        {
            return result;
        }
        return defaultValue ?? throw new InvalidOperationException(
            $"SDK capability argument '{name}' must be an integer.");
    }

    private static long GetCapabilityInt64(JsonElement? arguments, string name)
    {
        var value = GetCapabilityArguments(arguments);
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.TryGetInt64(out var result))
        {
            return result;
        }
        throw new InvalidOperationException($"SDK capability argument '{name}' must be an integer.");
    }

    private static bool GetCapabilityBoolean(JsonElement? arguments, string name, bool? defaultValue = null)
    {
        var value = GetCapabilityArguments(arguments);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (property.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }
        return defaultValue ?? throw new InvalidOperationException(
            $"SDK capability argument '{name}' must be a boolean.");
    }

    private static IReadOnlyList<string> GetCapabilityStringArray(JsonElement? arguments, string name)
    {
        var value = GetCapabilityArguments(arguments);
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"SDK capability argument '{name}' must be an array of strings.");
        }

        return property.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString()?.Trim() ?? string.Empty)
            .Where(entry => entry.Length > 0)
            .ToArray();
    }

    private static bool TryParsePluginSdkPath(string? path, out string pluginId, out string sdkPath)
    {
        pluginId = string.Empty;
        sdkPath = string.Empty;

        const string prefix = "/api/plugin-sdk/plugins/";
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = path[prefix.Length..].Trim('/');
        if (remainder.Length == 0)
        {
            return false;
        }

        var separatorIndex = remainder.IndexOf('/');
        pluginId = Uri.UnescapeDataString(separatorIndex < 0
            ? remainder
            : remainder[..separatorIndex]);
        sdkPath = separatorIndex < 0
            ? string.Empty
            : remainder[(separatorIndex + 1)..].Trim('/').ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(pluginId);
    }

    private static bool TryParseCommunityPluginFilePath(
        string? path,
        out string pluginId,
        out string relativePath)
    {
        pluginId = string.Empty;
        relativePath = string.Empty;

        const string prefix = "/api/plugin-store/community/";
        const string filesSegment = "/files/";
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = path[prefix.Length..];
        var filesSegmentIndex = remainder.IndexOf(filesSegment, StringComparison.OrdinalIgnoreCase);
        if (filesSegmentIndex <= 0)
        {
            return false;
        }

        pluginId = Uri.UnescapeDataString(remainder[..filesSegmentIndex]);
        relativePath = Uri.UnescapeDataString(remainder[(filesSegmentIndex + filesSegment.Length)..]);
        return !string.IsNullOrWhiteSpace(pluginId) && !string.IsNullOrWhiteSpace(relativePath);
    }

    private static bool TryParsePluginSdkSecretPath(
        string sdkPath,
        out string secretKey,
        out bool clear)
    {
        secretKey = string.Empty;
        clear = false;

        const string prefix = "secrets/";
        if (!sdkPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = sdkPath[prefix.Length..].Trim('/');
        if (remainder.EndsWith("/clear", StringComparison.OrdinalIgnoreCase))
        {
            clear = true;
            remainder = remainder[..^"/clear".Length].Trim('/');
        }

        if (remainder.Length == 0 || remainder.Contains('/'))
        {
            return false;
        }

        secretKey = Uri.UnescapeDataString(remainder);
        return !string.IsNullOrWhiteSpace(secretKey);
    }

    private async Task<UpdateCheckSnapshot> GetUpdateSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken,
        string? overrideChannel = null)
    {
        var channel = NormalizeUpdateChannel(overrideChannel ?? _steamLoaderSettingsService.GetUpdateChannel());

        await _updateSnapshotGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedUpdateSnapshot?.InstallInProgress == true)
            {
                return _cachedUpdateSnapshot;
            }

            if (!forceRefresh &&
                _cachedUpdateSnapshot is not null &&
                string.Equals(_cachedUpdateSnapshot.Channel, channel, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow - _cachedUpdateSnapshot.CheckedAtUtc <= UpdateSnapshotCacheDuration)
            {
                return _cachedUpdateSnapshot;
            }
        }
        finally
        {
            _updateSnapshotGate.Release();
        }

        var snapshot = await _releaseUpdateService.CheckAsync(channel, cancellationToken);
        await CacheUpdateSnapshotAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private async Task CacheUpdateSnapshotAsync(UpdateCheckSnapshot snapshot, CancellationToken cancellationToken)
    {
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
    }

    private void InvalidateUpdateSnapshotCache()
    {
        _cachedUpdateSnapshot = null;
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

        var channel = _steamLoaderSettingsService.GetUpdateChannel();
        var basisSnapshot = await GetUpdateSnapshotAsync(forceRefresh: true, cancellationToken, channel);
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
        await CacheUpdateSnapshotAsync(pendingSnapshot, cancellationToken);

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
                        channel,
                        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                        executablePath,
                        GetUpdateProcessIdsToWaitFor(executablePath),
                        async snapshot => await CacheUpdateSnapshotAsync(snapshot, CancellationToken.None),
                        CancellationToken.None);

                    await CacheUpdateSnapshotAsync(finalSnapshot, CancellationToken.None);
                    await Task.Delay(500);
                    _requestShutdown();
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

    private static string NormalizeUpdateChannel(string? channel)
    {
        return channel?.Trim().ToLowerInvariant() switch
        {
            SteamLoaderRuntime.UpdateChannelBeta => SteamLoaderRuntime.UpdateChannelBeta,
            _ => SteamLoaderRuntime.UpdateChannelStable
        };
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

    private sealed record SetDiscordSettingsRequest(string ApplicationId, string ServerId, string InviteUrl);

    private sealed record DiscordIdRequest(string Id);

    private sealed record SetIntegerValueRequest(int Value);
    private sealed record SetDisplayModeRequest(string Resolution, int RefreshRate);
    private sealed record SetHandheldCpuBoostRequest(string PowerSource, bool Enabled);

    private sealed record OemButtonRequest(string ButtonId);
    private sealed record UiHapticRequest(string Kind);

    private sealed record SetOemButtonBindingRequest(string ButtonId, string ActionId, string CustomShortcut);

    private sealed record SetUpdateChannelRequest(string Channel);

    private sealed record ApplyArtworkRequest(long AppId, string AssetType, string Url);

    private sealed record SetStartupModeRequest(string Mode);

    private sealed record SetPluginEnabledRequest(string PluginId, bool Enabled);

    private sealed record SetPluginOrderRequest(IReadOnlyList<string> PluginIds);

    private sealed record SetPluginStorePluginRequest(string PluginId);

    private sealed record PluginStoreInputRequest(string Action, string Source);

    private sealed record UnifyStoreLaunchRequest(string StoreId, string GameId);

    private sealed record SetStoreEnabledRequest(string StoreId, bool Enabled);

    private sealed record SetStorePathRequest(string StoreId, string? Value);

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
