using SteamLoader.App.Infrastructure.Assets;
using SteamLoader.App.Infrastructure.Audio;
using SteamLoader.App.Infrastructure.Display;
using SteamLoader.App.Infrastructure.Hltb;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Infrastructure.Themes;
using SteamLoader.App.Services;

namespace SteamLoader.App.Hosting;

public sealed class SteamLoaderBackgroundHost
{
    private static readonly Uri DebugEndpoint = new("http://127.0.0.1:8080");
    private static readonly Uri ApiBaseUri = new("http://127.0.0.1:47652/");

    private readonly SteamLoaderHostState _hostState;

    public SteamLoaderBackgroundHost(SteamLoaderHostState hostState)
    {
        _hostState = hostState;
    }

    public async Task RunAsync(CancellationToken cancellationToken, Action requestShutdown)
    {
        _hostState.UpdateMessage("Background host is running.");

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var audioOutputDeviceService = new CoreAudioOutputDeviceService();
        var displaySwitchService = new DisplaySwitchService();
        var hltbService = new HltbService(
            new HltbSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "hltb.json")));
        var autostartService = new WindowsAutostartService(SteamLoaderRuntime.AutostartValueName);
        var storeSyncSettingsStore = new StoreSyncSettingsStore(
            Path.Combine(AppContext.BaseDirectory, "data", "store-sync.json"));
        var storeSyncService = new StoreSyncService(
            storeSyncSettingsStore,
            new SteamShortcutFile(),
            new SteamGridDbArtworkDownloader(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var themesService = new ThemesService(
            new ThemesSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "themes.json")),
            "Assets/themes-catalog.json",
            "Assets/themes-profiles-catalog.json",
            Path.Combine(AppContext.BaseDirectory, "data", "themes"));
        var steamLoaderSettingsService = new SteamLoaderSettingsService(
            autostartService,
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the SteamLoader executable path."),
            SteamLoaderRuntime.AutostartArguments);
        var sharedScript = EmbeddedAssetReader.ReadText("Assets/quickaccess-shell.js");
        var popupScript = EmbeddedAssetReader.ReadText("Assets/quickaccess-popup.js");
        var themeSurfaceScript = string.Join(
            Environment.NewLine,
            EmbeddedAssetReader.ReadText("Assets/theme-surface.js"),
            EmbeddedAssetReader.ReadText("Assets/hltb-surface.js"));

        await using var apiServer = new SteamLoaderApiServer(
            audioOutputDeviceService,
            displaySwitchService,
            hltbService,
            storeSyncService,
            themesService,
            steamLoaderSettingsService,
            ApiBaseUri,
            _hostState,
            requestShutdown);

        var devToolsClient = new SteamDevToolsClient(httpClient, DebugEndpoint);
        var injector = new QuickAccessShellInjector(
            devToolsClient,
            ApiBaseUri,
            sharedScript,
            popupScript,
            themeSurfaceScript,
            _hostState);

        await apiServer.StartAsync(cancellationToken);

        try
        {
            await injector.RunAsync(cancellationToken);
        }
        finally
        {
            _hostState.UpdateMessage("Background host stopped.");
            await apiServer.StopAsync();
        }
    }
}
