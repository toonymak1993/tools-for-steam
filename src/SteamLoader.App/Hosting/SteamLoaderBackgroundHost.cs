using SteamLoader.App.Infrastructure.Assets;
using SteamLoader.App.Infrastructure.Artwork;
using SteamLoader.App.Infrastructure.AutoSisir;
using SteamLoader.App.Infrastructure.AppStart;
using SteamLoader.App.Infrastructure.Audio;
using SteamLoader.App.Infrastructure.Display;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Hltb;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.PluginStore;
using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.SmartHome;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Infrastructure.Themes;
using SteamLoader.App.Services;

namespace SteamLoader.App.Hosting;

public sealed class SteamLoaderBackgroundHost
{
    private static readonly Uri DebugEndpoint = new("http://127.0.0.1:8080");
    private static readonly Uri ApiBaseUri = new("http://127.0.0.1:47652/");
    private static readonly Uri CssLoaderApiUri = new("http://127.0.0.1:35821/req");

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
        var processWindowService = new ProcessWindowService();
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var hltbService = new HltbService(
            new HltbSettingsStore(Path.Combine(dataDirectory, "hltb.json")));
        var autostartService = new WindowsAutostartService(
            SteamLoaderRuntime.AutostartValueName,
            "SteamLoader",
            "SteamTools");
        var shellService = new WindowsShellService();
        var devToolsClient = new SteamDevToolsClient(httpClient, DebugEndpoint);
        var steamInstallationService = new SteamInstallationService(
            new SteamInstallPathSettingsStore(Path.Combine(dataDirectory, "steam-install-path.json")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var storeSyncSettingsStore = new StoreSyncSettingsStore(
            Path.Combine(dataDirectory, "store-sync.json"));
        var storeSyncJournal = new StoreSyncJournal(
            Path.Combine(dataDirectory, "store-sync-journal.jsonl"));
        var storeSyncService = new StoreSyncService(
            storeSyncSettingsStore,
            new SteamShortcutFile(),
            new SteamGridDbArtworkDownloader(),
            shellService,
            steamInstallationService,
            devToolsClient,
            storeSyncJournal);
        var artworkService = new SteamGridDbManualArtworkService(
            steamInstallationService,
            new ArtworkSettingsStore(Path.Combine(dataDirectory, "artwork.json")));
        var themesService = new ThemesService(httpClient, CssLoaderApiUri);
        var performanceService = new TfsPerformanceService(
            new PerformanceSettingsStore(Path.Combine(dataDirectory, "performance.json")),
            new PerformanceStatusStore(Path.Combine(dataDirectory, "performance-runtime.json")));
        performanceService.RestoreOverlayOnStartup();
        var steamLoaderSettingsService = new SteamLoaderSettingsService(
            autostartService,
            shellService,
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path."),
            SteamLoaderRuntime.ShellLaunchArguments,
            Path.Combine(dataDirectory, "tfs.json"));
        steamLoaderSettingsService.EnsureDefaultConsoleModeEnabled();
        var pluginStoreService = new PluginStoreService(
            httpClient,
            steamLoaderSettingsService,
            Path.Combine(dataDirectory, "plugin-store"));
        var storeSyncAutomationService = new StoreSyncAutomationService(
            storeSyncService,
            () => steamLoaderSettingsService.IsPluginEnabled("store-sync"));
        var frontendComponentService = new SteamFrontendComponentService(devToolsClient);
        var shellVisibilityService = new WindowsShellVisibilityService();
        var shellGuardService = new ConsoleModeShellGuardService(
            devToolsClient,
            steamLoaderSettingsService,
            shellVisibilityService);
        var executablePath =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
        var handheldDevice = new HandheldDeviceDetection().Detect();
        var steamClientLaunchService = new SteamClientLaunchService(
            httpClient,
            DebugEndpoint,
            steamInstallationService,
            isHandheld: handheldDevice.IsHandheld);
        var powerActionService = new PowerActionService(
            steamClientLaunchService,
            shellService,
            executablePath,
            SteamLoaderRuntime.BackgroundArgument);
        var releaseUpdateService = new ReleaseUpdateService();
        var liveUpdateHub = new QuickAccessLiveUpdateHub();
        var sharedScript = EmbeddedAssetReader.ReadText("Assets/quickaccess-shell.js");
        var popupScript = string.Join(
            Environment.NewLine,
            EmbeddedAssetReader.ReadText("Assets/st-frontend-lib.js"),
            EmbeddedAssetReader.ReadText("Assets/quickaccess-popup.js"),
            EmbeddedAssetReader.ReadText("Assets/plugin-store-overlay.js"));
        var themeSurfaceScript = string.Join(
            Environment.NewLine,
            EmbeddedAssetReader.ReadText("Assets/theme-surface.js"),
            EmbeddedAssetReader.ReadText("Assets/hltb-surface.js"),
            EmbeddedAssetReader.ReadText("Assets/artwork-surface.js"),
            EmbeddedAssetReader.ReadText("Assets/plugin-store-overlay.js"),
            EmbeddedAssetReader.ReadText("Assets/unifystore-overlay.js"));

        var appStartService = new AppStartService(Path.Combine(dataDirectory, "app-start.json"));
        var autoSisirService = new AutoSisirService(
            new AutoSisirSettingsStore(Path.Combine(dataDirectory, "auto-sisr.json")),
            storeSyncService,
            Path.Combine(dataDirectory, "auto-sisr.log"),
            () => steamLoaderSettingsService.IsPluginEnabled("auto-sisr"));
        var smartHomeService = new SmartHomeService(
            httpClient,
            new SmartHomeSettingsStore(Path.Combine(dataDirectory, "smart-home.json")));
        var liveStatePublisher = new QuickAccessLiveStatePublisher(
            liveUpdateHub,
            audioOutputDeviceService,
            processWindowService,
            storeSyncService,
            smartHomeService,
            () => steamLoaderSettingsService.IsPluginEnabled("smart-home"));
        var controllerShortcutService = new ControllerShortcutService(
            isEnabled: () => true,
            sendControlDigitAsync: digit => devToolsClient.SendControlDigitShortcutAsync(digit, cancellationToken));

        await using var apiServer = new SteamLoaderApiServer(
            audioOutputDeviceService,
            displaySwitchService,
            processWindowService,
            artworkService,
            autoSisirService,
            appStartService,
            hltbService,
            storeSyncService,
            themesService,
            performanceService,
            steamLoaderSettingsService,
            pluginStoreService,
            powerActionService,
            releaseUpdateService,
            frontendComponentService,
            devToolsClient,
            smartHomeService,
            ApiBaseUri,
            _hostState,
            liveUpdateHub,
            requestShutdown);

        var injector = new QuickAccessShellInjector(
            devToolsClient,
            ApiBaseUri,
            steamClientLaunchService,
            sharedScript,
            popupScript,
            themeSurfaceScript,
            _hostState);

        await apiServer.StartAsync(cancellationToken);
        _ = themesService.StartBackendOnStartupAsync();
        using var shellGuardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var shellGuardTask = shellGuardService.RunAsync(shellGuardCts.Token);
        using var autoSisirCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var autoSisirTask = autoSisirService.RunAsync(autoSisirCts.Token);
        using var storeSyncAutomationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var storeSyncAutomationTask = storeSyncAutomationService.RunAsync(storeSyncAutomationCts.Token);
        using var liveStatePublisherCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var liveStatePublisherTask = liveStatePublisher.RunAsync(liveStatePublisherCts.Token);
        using var controllerShortcutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var controllerShortcutTask = controllerShortcutService.RunAsync(controllerShortcutCts.Token);

        try
        {
            await injector.RunAsync(cancellationToken);
        }
        finally
        {
            await liveStatePublisherCts.CancelAsync();
            try
            {
                await liveStatePublisherTask;
            }
            catch (OperationCanceledException)
            {
            }

            await controllerShortcutCts.CancelAsync();
            try
            {
                await controllerShortcutTask;
            }
            catch (OperationCanceledException)
            {
            }

            await storeSyncAutomationCts.CancelAsync();
            try
            {
                await storeSyncAutomationTask;
            }
            catch (OperationCanceledException)
            {
            }

            await autoSisirCts.CancelAsync();
            try
            {
                await autoSisirTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                autoSisirService.Stop();
            }

            await shellGuardCts.CancelAsync();
            try
            {
                await shellGuardTask;
            }
            catch (OperationCanceledException)
            {
            }

            _hostState.UpdateMessage("Background host stopped.");
            await apiServer.StopAsync();
        }
    }
}
