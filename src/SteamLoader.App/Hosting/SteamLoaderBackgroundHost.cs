using SteamLoader.App.Infrastructure.Assets;
using SteamLoader.App.Infrastructure.Artwork;
using SteamLoader.App.Infrastructure.AutoSisir;
using SteamLoader.App.Infrastructure.AppStart;
using SteamLoader.App.Infrastructure.Audio;
using SteamLoader.App.Infrastructure.Display;
using SteamLoader.App.Infrastructure.Discord;
using SteamLoader.App.Infrastructure.Helpers;
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
using SteamLoader.App.Services;

namespace SteamLoader.App.Hosting;

public sealed class SteamLoaderBackgroundHost
{
    private static readonly Uri DebugEndpoint = new("http://127.0.0.1:8080");
    private static readonly Uri ApiBaseUri = new("http://127.0.0.1:47652/");
    private static readonly Uri CssLoaderApiUri = new("http://127.0.0.1:35821/req");
    private static readonly TimeSpan OmniLibraryStartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OmniLibraryCatalogCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OmniLibraryMaximumFailureBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan OmniLibrarySchedulerWakeInterval = TimeSpan.FromMinutes(1);

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

        using var audioOutputDeviceService = new CoreAudioOutputDeviceService();
        var displaySwitchService = new DisplaySwitchService();
        var processWindowService = new ProcessWindowService();
        var steamWindowFocusService = new SteamWindowFocusService(processWindowService);
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var apiSessionToken = LocalApiSession.GetOrCreate(
            Path.Combine(dataDirectory, "local-api-session.token"));
        var hltbService = new HltbService(
            new HltbSettingsStore(Path.Combine(dataDirectory, "hltb.json")));
        await using var discordService = new DiscordService(
            httpClient,
            new DiscordSettingsStore(Path.Combine(dataDirectory, "discord.json")));
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
        using var omniLibraryMetadataService = new OmniLibraryGamePageMetadataService(
            storeSyncService.GetUnifySteamGame,
            Path.Combine(dataDirectory, "cache", "omnilibrary-game-pages.json"),
            settingsStore: storeSyncSettingsStore);
        var storeService = new StoreService(
            httpClient,
            new StoreSettingsStore(Path.Combine(dataDirectory, "store.json")),
            () => storeSyncService.GetSnapshot().SteamProfile,
            processWindowService,
            Path.Combine(dataDirectory, "cache", "store-artwork"));
        var artworkService = new SteamGridDbManualArtworkService(
            steamInstallationService,
            new ArtworkSettingsStore(Path.Combine(dataDirectory, "artwork.json")));
        var themesService = new ThemesService(httpClient, CssLoaderApiUri);
        using var performanceService = new TfsPerformanceService(
            new PerformanceSettingsStore(Path.Combine(dataDirectory, "performance.json")));
        performanceService.RestoreOverlayOnStartup();
        var handheldProfileNotificationService = new WindowsProfileNotificationService(dataDirectory);
        var handheldPerformanceService = new HandheldPerformanceService(
            dataDirectory,
            handheldProfileNotificationService);
        handheldPerformanceService.OemButtonPressed += binding =>
        {
            _ = ExecuteOemButtonActionAsync(
                binding,
                devToolsClient,
                steamWindowFocusService,
                dataDirectory,
                cancellationToken);
        };
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
        var pluginFullTrustRuntime = new PluginFullTrustRuntime(pluginStoreService, devToolsClient);
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
        var nvidiaDriverUpdateService = new NvidiaDriverUpdateService();
        var windowsSystemUpdateService = new WindowsSystemUpdateService();
        var hdrDisplayService = new HdrDisplayService();
        using var bluetoothDeviceService = new BluetoothDeviceService();
        var liveUpdateHub = new QuickAccessLiveUpdateHub();
        var storePriceNotificationService = new StorePriceNotificationService();
        storeService.PriceAlertReached += notification =>
        {
            if (liveUpdateHub.HasSubscribers)
            {
                liveUpdateHub.Publish(
                    "notifications.show",
                    new
                    {
                        title = notification.Title,
                        message = notification.Message,
                        level = "success",
                        durationMs = 9000,
                        actionUrl = notification.DealUrl,
                        actionLabel = "Open deal"
                    });
                return;
            }

            storePriceNotificationService.Show(notification, storeService.OpenDeal);
        };
        var externalGameQuickAccessService = new ExternalGameQuickAccessService(
            storeSyncService,
            processWindowService,
            steamWindowFocusService,
            devToolsClient,
            liveUpdateHub,
            Path.Combine(dataDirectory, "external-game-quick-access.log"));
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
            EmbeddedAssetReader.ReadText("Assets/omnilibrary-tab-topology.js"),
            EmbeddedAssetReader.ReadText("Assets/library-tabs.js"),
            EmbeddedAssetReader.ReadText("Assets/xbox-library-surface.js"),
            EmbeddedAssetReader.ReadText("Assets/omnilibrary-metadata-surface.js"),
            EmbeddedAssetReader.ReadText("Assets/artwork-surface.js"),
            EmbeddedAssetReader.ReadText("Assets/plugin-store-overlay.js"),
            EmbeddedAssetReader.ReadText("Assets/store-overlay.js"));

        var appStartService = new AppStartService(
            Path.Combine(dataDirectory, "app-start.json"),
            processWindowService);
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
            processWindowService,
            storeSyncService,
            () => steamLoaderSettingsService.IsPluginEnabled("store-sync"),
            smartHomeService,
            handheldPerformanceService,
            () => steamLoaderSettingsService.IsPluginEnabled("smart-home"),
            discordService,
            () => steamLoaderSettingsService.IsPluginEnabled("discord"));
        using var hidMenuButtonMonitor = new HidMenuButtonMonitor();
        hidMenuButtonMonitor.ReportObserved += handheldPerformanceService.ObserveOemInput;
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
            isHidBackButtonDown: () => hidMenuButtonMonitor.IsBackDown,
            tryOpenExternalGameQuickAccessAsync: () =>
                externalGameQuickAccessService.TryOpenForForegroundGameAsync(cancellationToken),
            settingsProvider: steamLoaderSettingsService.GetControllerShortcutSettings,
            hidControllerButtonMasksProvider: () => hidMenuButtonMonitor.ControllerButtonMasks,
            openInGameOverlayAsync: () =>
                devToolsClient.TryOpenInGameOverlayAsync(quickAccess: false, cancellationToken),
            openInGameQuickAccessAsync: () =>
                devToolsClient.TryOpenInGameOverlayAsync(quickAccess: true, cancellationToken));

        await using var apiServer = new SteamLoaderApiServer(
            audioOutputDeviceService,
            displaySwitchService,
            processWindowService,
            artworkService,
            autoSisirService,
            appStartService,
            hltbService,
            storeService,
            storeSyncService,
            omniLibraryMetadataService,
            themesService,
            performanceService,
            handheldPerformanceService,
            steamLoaderSettingsService,
            pluginStoreService,
            powerActionService,
            releaseUpdateService,
            nvidiaDriverUpdateService,
            windowsSystemUpdateService,
            hdrDisplayService,
            bluetoothDeviceService,
            frontendComponentService,
            devToolsClient,
            smartHomeService,
            discordService,
            pluginFullTrustRuntime,
            externalGameQuickAccessService,
            () => ControllerShortcutService.ReadPressedButtonIds(
                ControllerShortcutService.ReadConnectedControllerButtonMasks()
                    .Concat(hidMenuButtonMonitor.ControllerButtonMasks)
                    .Distinct()
                    .ToArray(),
                hidMenuButtonMonitor.IsBackDown,
                hidMenuButtonMonitor.IsMenuDown),
            ApiBaseUri,
            apiSessionToken,
            _hostState,
            liveUpdateHub,
            requestShutdown);

        var injector = new QuickAccessShellInjector(
            devToolsClient,
            ApiBaseUri,
            apiSessionToken,
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
        using var storeRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var storeRefreshTask = storeService.RunRefreshLoopAsync(
            static () => true,
            storeRefreshCts.Token);
        using var liveStatePublisherCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var liveStatePublisherTask = liveStatePublisher.RunAsync(liveStatePublisherCts.Token);
        using var gamepadHelperSupervisorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gamepadHelperSupervisorTask = gamepadHelperSupervisor.RunAsync(gamepadHelperSupervisorCts.Token);
        using var controllerShortcutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var controllerShortcutTask = controllerShortcutService.RunAsync(controllerShortcutCts.Token);
        using var handheldProfileCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handheldProfileTask = handheldProfileCoordinator.RunAsync(handheldProfileCts.Token);
        using var omniLibraryRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var omniLibraryRefreshTask = RunOmniLibraryRefreshLoopAsync(
            storeSyncService,
            () => steamLoaderSettingsService.IsPluginEnabled("omnilibrary"),
            dataDirectory,
            omniLibraryRefreshCts.Token);
        using var omniLibraryDownloadPowerCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Existing transfers are user operations, not UI state. They recover
        // even if OmniLibrary was disabled before TFS/Windows restarted.
        UnifySteamLauncher.ResumeInterruptedDownloads();
        var omniLibraryDownloadPowerTask =
            OmniLibraryDownloadSleepBlocker.RunStatusMonitorAsync(
                omniLibraryDownloadPowerCts.Token);
        using var gogInstallStateCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gogInstallStateTask =
            GogInstallStateTracker.RunAsync(gogInstallStateCts.Token);
        using var xboxInstallStateCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var xboxInstallStateTask =
            ProviderInstallStateTracker.RunAsync(
                "xbox-game-pass",
                UnifySteamService.LoadXboxInstalledGamesForReconciliation,
                xboxInstallStateCts.Token);
        using var epicInstallStateCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var epicInstallStateTask =
            ProviderInstallStateTracker.RunAsync(
                "epic-games",
                UnifySteamService.LoadEpicInstalledGamesForReconciliation,
                epicInstallStateCts.Token);

        try
        {
            await injector.RunAsync(cancellationToken);
        }
        finally
        {
            await gogInstallStateCts.CancelAsync();
            try
            {
                await gogInstallStateTask;
            }
            catch (OperationCanceledException)
            {
            }

            await xboxInstallStateCts.CancelAsync();
            try
            {
                await xboxInstallStateTask;
            }
            catch (OperationCanceledException)
            {
            }

            await epicInstallStateCts.CancelAsync();
            try
            {
                await epicInstallStateTask;
            }
            catch (OperationCanceledException)
            {
            }

            await omniLibraryDownloadPowerCts.CancelAsync();
            try
            {
                await omniLibraryDownloadPowerTask;
            }
            catch (OperationCanceledException)
            {
            }

            await omniLibraryRefreshCts.CancelAsync();
            try
            {
                await omniLibraryRefreshTask;
            }
            catch (OperationCanceledException)
            {
            }

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

            await storeRefreshCts.CancelAsync();
            try
            {
                await storeRefreshTask;
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

    private static async Task RunOmniLibraryRefreshLoopAsync(
        StoreSyncService storeSyncService,
        Func<bool> isEnabled,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        await Task.Delay(OmniLibraryStartupDelay, cancellationToken);
        var schedules = new Dictionary<string, OmniLibraryStoreCheckSchedule>(
            StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!isEnabled())
            {
                schedules.Clear();
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                continue;
            }

            var enabledStoreIds = storeSyncService.GetEnabledUnifySteamStoreIds();
            var enabledStoreSet = enabledStoreIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleStoreId in schedules.Keys
                         .Where(storeId => !enabledStoreSet.Contains(storeId))
                         .ToArray())
            {
                schedules.Remove(staleStoreId);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var storeId in enabledStoreIds)
            {
                schedules.TryGetValue(storeId, out var schedule);
                if (schedule is not null && now < schedule.NextCheckAtUtc)
                {
                    continue;
                }

                try
                {
                    var result = await Task.Run(
                        () => storeSyncService.CheckUnifySteamStoreForChanges(storeId),
                        cancellationToken);
                    if (!result.Checked)
                    {
                        schedules[storeId] = new OmniLibraryStoreCheckSchedule(
                            FailureCount: 0,
                            now.Add(OmniLibraryCatalogCheckInterval));
                        continue;
                    }

                    if (result.Succeeded)
                    {
                        schedules[storeId] = new OmniLibraryStoreCheckSchedule(
                            FailureCount: 0,
                            now.Add(OmniLibraryCatalogCheckInterval));
                        if (result.CatalogChanged || result.StateChanged)
                        {
                            AppendDiagnosticLog(
                                Path.Combine(dataDirectory, "omnilibrary-refresh.log"),
                                $"{storeId} five-minute check: {result.Detail}");
                        }
                        continue;
                    }

                    var failureCount = Math.Min((schedule?.FailureCount ?? 0) + 1, 8);
                    var backoff = ComputeOmniLibraryFailureBackoff(failureCount);
                    schedules[storeId] = new OmniLibraryStoreCheckSchedule(
                        failureCount,
                        now.Add(backoff));
                    AppendDiagnosticLog(
                        Path.Combine(dataDirectory, "omnilibrary-refresh.log"),
                        $"{storeId} five-minute check failed; retry in {backoff.TotalMinutes:0} min: {result.Detail}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var failureCount = Math.Min((schedule?.FailureCount ?? 0) + 1, 8);
                    var backoff = ComputeOmniLibraryFailureBackoff(failureCount);
                    schedules[storeId] = new OmniLibraryStoreCheckSchedule(
                        failureCount,
                        now.Add(backoff));
                    AppendDiagnosticLog(
                        Path.Combine(dataDirectory, "omnilibrary-refresh.log"),
                        $"{storeId} five-minute check failed; retry in {backoff.TotalMinutes:0} min: " +
                        $"{exception.GetType().Name}:{exception.Message}");
                }
            }

            var nextCheckAtUtc = schedules.Count == 0
                ? now.Add(OmniLibrarySchedulerWakeInterval)
                : schedules.Values.Min(schedule => schedule.NextCheckAtUtc);
            var delay = nextCheckAtUtc - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromSeconds(5))
            {
                delay = TimeSpan.FromSeconds(5);
            }
            if (delay > OmniLibrarySchedulerWakeInterval)
            {
                delay = OmniLibrarySchedulerWakeInterval;
            }
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static TimeSpan ComputeOmniLibraryFailureBackoff(int failureCount)
    {
        var multiplier = Math.Pow(2, Math.Max(0, failureCount - 1));
        return TimeSpan.FromTicks(Math.Min(
            OmniLibraryMaximumFailureBackoff.Ticks,
            (long)(OmniLibraryCatalogCheckInterval.Ticks * multiplier)));
    }

    private sealed record OmniLibraryStoreCheckSchedule(
        int FailureCount,
        DateTimeOffset NextCheckAtUtc);

    private static async Task ExecuteOemButtonActionAsync(
        HandheldOemButtonBinding binding,
        SteamDevToolsClient devToolsClient,
        SteamWindowFocusService steamWindowFocusService,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var delivery = "local";
            switch (binding.ActionId)
            {
                case "steam-menu":
                    var steamMenuDirect = await devToolsClient.TryOpenSteamMenuAsync(cancellationToken);
                    var steamMenuDelivered = steamMenuDirect ||
                        await devToolsClient.SendControlDigitShortcutAsync(1, cancellationToken);
                    if (!steamMenuDelivered)
                    {
                        throw new InvalidOperationException("Steam did not accept the Steam Menu command.");
                    }
                    delivery = steamMenuDirect ? "steam-direct" : "steam-ctrl-1";
                    break;
                case "quick-access":
                    var quickAccessDirect = await devToolsClient.TryOpenQuickAccessMenuAsync(cancellationToken);
                    var quickAccessDelivered = quickAccessDirect ||
                        await devToolsClient.SendControlDigitShortcutAsync(2, cancellationToken);
                    if (!quickAccessDelivered)
                    {
                        throw new InvalidOperationException("Steam did not accept the Quick Access command.");
                    }
                    delivery = quickAccessDirect ? "steam-direct" : "steam-ctrl-2";
                    break;
                case "focus-steam":
                    delivery = await steamWindowFocusService.FocusSteamWindowAsync(cancellationToken);
                    break;
                case "escape":
                    HandheldSystemControlService.SendOemKeyboardShortcut("ESC");
                    break;
                case "alt-tab":
                    HandheldSystemControlService.SendOemKeyboardShortcut("ALT+TAB");
                    break;
                case "xbox-game-bar":
                    HandheldSystemControlService.SendOemKeyboardShortcut("WIN+G");
                    break;
                case "task-manager":
                    HandheldSystemControlService.SendOemKeyboardShortcut("CTRL+SHIFT+ESC");
                    break;
                case "custom-shortcut":
                    HandheldSystemControlService.SendOemKeyboardShortcut(binding.CustomShortcut);
                    break;
            }

            AppendDiagnosticLog(
                Path.Combine(dataDirectory, "handheld-oem-buttons.log"),
                $"button={binding.ButtonId} action={binding.ActionId} delivery={delivery} input={binding.InputCode}");
        }
        catch (Exception exception)
        {
            AppendDiagnosticLog(
                Path.Combine(dataDirectory, "handheld-oem-buttons.log"),
                $"button={binding.ButtonId} action={binding.ActionId} failed={exception.GetType().Name}:{exception.Message}");
        }
    }

}
