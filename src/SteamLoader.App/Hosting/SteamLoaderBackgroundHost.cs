using SteamLoader.App.Infrastructure.Assets;
using SteamLoader.App.Infrastructure.Artwork;
using SteamLoader.App.Infrastructure.AutoSisir;
using SteamLoader.App.Infrastructure.AppStart;
using SteamLoader.App.Infrastructure.Audio;
using SteamLoader.App.Infrastructure.Display;
using SteamLoader.App.Infrastructure.Helpers;
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
        var handheldProfileNotificationService = new WindowsProfileNotificationService(dataDirectory);
        var handheldPerformanceService = new HandheldPerformanceService(
            dataDirectory,
            handheldProfileNotificationService);
        var handheldProfileCoordinator = new HandheldPerformanceProfileCoordinator(
            handheldPerformanceService,
            steamInstallationService.ResolveSteamRootPath(),
            handheldProfileNotificationService);
        var steamLoaderSettingsService = new SteamLoaderSettingsService(
            autostartService,
            shellService,
            new XboxModeService(),
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
        var steamClientLaunchService = new SteamClientLaunchService(
            httpClient,
            DebugEndpoint,
            steamInstallationService,
            isHandheld: HandheldDeviceCatalog.IsSupported(HandheldDeviceCatalog.Detect()));
        var powerActionService = new PowerActionService(
            steamClientLaunchService,
            shellService,
            executablePath,
            SteamLoaderRuntime.BackgroundArgument);
        var gamepadHelperTaskService = new GamepadHelperScheduledTaskService(
            executablePath,
            AppContext.BaseDirectory);
        var gamepadHelperSupervisor = new GamepadHelperSupervisor(
            gamepadHelperTaskService,
            Path.Combine(dataDirectory, "gamepad-helper-watchdog.log"));
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
            handheldPerformanceService,
            () => steamLoaderSettingsService.IsPluginEnabled("smart-home"));
        using var hidMenuButtonMonitor = new HidMenuButtonMonitor();
        var controllerShortcutService = new ControllerShortcutService(
            isEnabled: () => !gamepadHelperSupervisor.IsHelperRunning,
            isBigPictureForeground: SteamBigPictureForegroundDetector.IsBigPictureForeground,
            isGameInForeground: () =>
                !gamepadHelperSupervisor.IsHelperRunning &&
                PerformanceForegroundTargetResolver.TryResolve() is not null,
            isHidMenuButtonDown: () => hidMenuButtonMonitor.IsMenuDown,
            openSteamMenuAsync: () => devToolsClient.TryOpenSteamMenuAsync(cancellationToken),
            openQuickAccessMenuAsync: () => devToolsClient.TryOpenQuickAccessMenuAsync(cancellationToken),
            sendControlDigitAsync: digit => devToolsClient.SendControlDigitShortcutAsync(digit, cancellationToken),
            diagnosticLog: message => AppendDiagnosticLog(
                Path.Combine(dataDirectory, "controller-shortcuts.log"),
                message),
            isHidBackButtonDown: () => hidMenuButtonMonitor.IsBackDown);

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
            handheldPerformanceService,
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
        using var gamepadHelperSupervisorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gamepadHelperSupervisorTask = gamepadHelperSupervisor.RunAsync(gamepadHelperSupervisorCts.Token);
        using var controllerShortcutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var controllerShortcutTask = controllerShortcutService.RunAsync(controllerShortcutCts.Token);
        using var handheldProfileCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handheldProfileTask = handheldProfileCoordinator.RunAsync(handheldProfileCts.Token);

        try
        {
            await injector.RunAsync(cancellationToken);
        }
        finally
        {
            await handheldProfileCts.CancelAsync();
            try
            {
                await handheldProfileTask;
            }
            catch (OperationCanceledException)
            {
            }

            await liveStatePublisherCts.CancelAsync();
            try
            {
                await liveStatePublisherTask;
            }
            catch (OperationCanceledException)
            {
            }

            await gamepadHelperSupervisorCts.CancelAsync();
            try
            {
                await gamepadHelperSupervisorTask;
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

    private static void AppendDiagnosticLog(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
