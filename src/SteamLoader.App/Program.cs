using System.Windows;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Helpers;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Services;
using SteamLoader.App.UI;

namespace SteamLoader.App;

public static class Program
{
    private static Mutex? _installerMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        _installerMutex = new Mutex(false, SteamLoaderRuntime.InstallerMutexName);

        var legacyShellLaunch = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.LegacyShellBootstrapArgument, StringComparison.OrdinalIgnoreCase));
        var consoleStartRequested = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.ConsoleStartArgument, StringComparison.OrdinalIgnoreCase));
        if (legacyShellLaunch || consoleStartRequested)
        {
            // First line of every sign-in start, before anything that can fail,
            // so a start that dies early still leaves a trace.
            SteamStartupDiagnostics.Write($"sign-in start args=\"{string.Join(' ', args)}\"");
        }

        if (legacyShellLaunch)
        {
            // Shell Takeover was removed, but an old Winlogon "Shell" value still
            // made Windows start TFS instead of Explorer. Give the user the
            // desktop first; the settings below migrate to Console Mode and
            // put Explorer back as the shell for the next sign-in.
            SteamStartupDiagnostics.Write("legacy Shell Takeover launch; starting Windows Explorer and migrating to Console Mode");
            new WindowsShellService().StartWindowsShellIfNeeded();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RestoreXboxModeArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RestoreXboxMode();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.PrepareHandheldOemArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return HandheldReplacementRuntime.PrepareOemSoftware(
                Path.Combine(AppContext.BaseDirectory, "data"));
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.PrepareHandheldReplacementArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return HandheldReplacementRuntime.Prepare(
                Path.Combine(AppContext.BaseDirectory, "data"),
                args.Any(argument => string.Equals(argument, SteamLoaderRuntime.UsbIpOwnedByTfsArgument, StringComparison.OrdinalIgnoreCase)),
                args.Any(argument => string.Equals(argument, SteamLoaderRuntime.HidHideOwnedByTfsArgument, StringComparison.OrdinalIgnoreCase)));
        }

        if (args.Any(argument => string.Equals(
                argument,
                SteamLoaderRuntime.SuspendHandheldReplacementForUpdateArgument,
                StringComparison.OrdinalIgnoreCase)))
        {
            var dataDirectoryArgument = args.FirstOrDefault(argument => argument.StartsWith(
                SteamLoaderRuntime.HandheldDataDirectoryArgumentPrefix,
                StringComparison.OrdinalIgnoreCase));
            var dataDirectory = dataDirectoryArgument is null
                ? Path.Combine(AppContext.BaseDirectory, "data")
                : Path.GetFullPath(dataDirectoryArgument[SteamLoaderRuntime.HandheldDataDirectoryArgumentPrefix.Length..]);
            return HandheldReplacementRuntime.SuspendForUpdate(dataDirectory);
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RestoreHandheldReplacementArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return HandheldReplacementRuntime.RestoreForUninstall(Path.Combine(AppContext.BaseDirectory, "data"));
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RemoveOwnedHandheldDriversArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return HandheldReplacementRuntime.RemoveOwnedDrivers(Path.Combine(AppContext.BaseDirectory, "data"));
        }

        if (Infrastructure.StoreSync.XboxStoreLaunchHost.TryParseArguments(args, out var xboxLaunchPayload))
        {
            return Infrastructure.StoreSync.XboxStoreLaunchHost.Run(xboxLaunchPayload);
        }

        if (Infrastructure.StoreSync.OmniLibraryLoginRuntime.TryParseArguments(
                args,
                out var omniLibraryLoginStoreId))
        {
            return Infrastructure.StoreSync.OmniLibraryLoginRuntime.Run(omniLibraryLoginStoreId);
        }

        var gogPreparationIndex = Array.FindIndex(args, argument =>
            string.Equals(
                argument,
                Infrastructure.StoreSync.GogInstallPreparation.ElevatedArgument,
                StringComparison.OrdinalIgnoreCase));
        if (gogPreparationIndex >= 0)
        {
            if (gogPreparationIndex + 4 >= args.Length)
            {
                Console.Error.WriteLine("The elevated GOG setup request is incomplete.");
                return 1;
            }

            return Infrastructure.StoreSync.GogInstallPreparation.RunElevated(
                args[gogPreparationIndex + 1],
                args[gogPreparationIndex + 2],
                args[gogPreparationIndex + 3],
                args[gogPreparationIndex + 4]);
        }

        var lockScreenWallpaperIndex = Array.FindIndex(args, argument =>
            string.Equals(
                argument,
                Infrastructure.Themes.LockScreenWallpaperElevatedWorker.ElevatedArgument,
                StringComparison.OrdinalIgnoreCase));
        if (lockScreenWallpaperIndex >= 0)
        {
            if (lockScreenWallpaperIndex + 3 >= args.Length)
            {
                Console.Error.WriteLine("The elevated lock screen wallpaper request is incomplete.");
                return 1;
            }

            return Infrastructure.Themes.LockScreenWallpaperElevatedWorker.RunElevated(
                args[lockScreenWallpaperIndex + 1],
                args[lockScreenWallpaperIndex + 2],
                args[lockScreenWallpaperIndex + 3]);
        }

        var unifyInstallIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, Infrastructure.StoreSync.UnifySteamLauncher.InstallArgument, StringComparison.OrdinalIgnoreCase));
        if (unifyInstallIndex >= 0)
        {
            var installTarget = unifyInstallIndex + 1 < args.Length ? args[unifyInstallIndex + 1] : string.Empty;
            if (!Infrastructure.StoreSync.StorefrontFeatureFlags.Enabled)
            {
                Console.Error.WriteLine("OmniLibrary is disabled in this build.");
                return 1;
            }

            return Infrastructure.StoreSync.UnifySteamLauncher.Install(installTarget);
        }

        var unifyRepairIndex = Array.FindIndex(args, argument =>
            string.Equals(
                argument,
                Infrastructure.StoreSync.UnifySteamLauncher.RepairArgument,
                StringComparison.OrdinalIgnoreCase));
        if (unifyRepairIndex >= 0)
        {
            var repairTarget = unifyRepairIndex + 1 < args.Length
                ? args[unifyRepairIndex + 1]
                : string.Empty;
            if (!Infrastructure.StoreSync.StorefrontFeatureFlags.Enabled)
            {
                Console.Error.WriteLine("OmniLibrary is disabled in this build.");
                return 1;
            }

            return Infrastructure.StoreSync.UnifySteamLauncher.Repair(repairTarget);
        }

        var unifyUninstallIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, Infrastructure.StoreSync.UnifySteamLauncher.UninstallArgument, StringComparison.OrdinalIgnoreCase));
        if (unifyUninstallIndex >= 0)
        {
            var uninstallTarget = unifyUninstallIndex + 1 < args.Length ? args[unifyUninstallIndex + 1] : string.Empty;
            if (!Infrastructure.StoreSync.StorefrontFeatureFlags.Enabled)
            {
                Console.Error.WriteLine("OmniLibrary is disabled in this build.");
                return 1;
            }

            return Infrastructure.StoreSync.UnifySteamLauncher.Uninstall(uninstallTarget);
        }

        var unifyCancelDownloadIndex = Array.FindIndex(args, argument =>
            string.Equals(
                argument,
                Infrastructure.StoreSync.UnifySteamLauncher.CancelDownloadArgument,
                StringComparison.OrdinalIgnoreCase));
        if (unifyCancelDownloadIndex >= 0)
        {
            var cancelTarget = unifyCancelDownloadIndex + 1 < args.Length
                ? args[unifyCancelDownloadIndex + 1]
                : string.Empty;
            if (!Infrastructure.StoreSync.StorefrontFeatureFlags.Enabled)
            {
                Console.Error.WriteLine("OmniLibrary is disabled in this build.");
                return 1;
            }

            return Infrastructure.StoreSync.UnifySteamLauncher.CancelDownload(
                cancelTarget);
        }

        var unifyLaunchIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, "--unifysteam-launch", StringComparison.OrdinalIgnoreCase));
        if (unifyLaunchIndex >= 0)
        {
            var unifyTarget = unifyLaunchIndex + 1 < args.Length ? args[unifyLaunchIndex + 1] : string.Empty;
            if (!Infrastructure.StoreSync.StorefrontFeatureFlags.Enabled)
            {
                Console.Error.WriteLine("OmniLibrary is disabled in this build.");
                return 1;
            }

            return Infrastructure.StoreSync.UnifySteamLauncher.Run(unifyTarget);
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.BackgroundArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunBackgroundHostAsync().GetAwaiter().GetResult();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.HidDebugArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunHidDebugWindow();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.GamepadHelperArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunGamepadHelper();
        }

        // A pre-RTSS scheduled task may fire once before the installer removes it.
        // Never let that legacy argument start the normal app elevated.
        if (args.Any(argument => string.Equals(argument, "--fps-helper", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RegisterInstalledHelperTasksArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RegisterInstalledHelperTasks();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RegisterGamepadHelperTaskArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RegisterGamepadHelperTask();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.CheckGamepadHelperTaskArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return CheckGamepadHelperTask();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.SanitizeSteamAutostartArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return SanitizeSteamAutostart();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RequestSteamAttentionArgument, StringComparison.OrdinalIgnoreCase)))
        {
            TryRequestSteamAttention();
            return 0;
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RepairSteamStartupArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RepairSteamStartup();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.PreviewSplashArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunSplashPreview(args);
        }

        var startupModeArgument = args.FirstOrDefault(argument =>
            argument.StartsWith(SteamLoaderRuntime.SetStartupModeArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        if (startupModeArgument is not null)
        {
            return ConfigureStartupMode(startupModeArgument[SteamLoaderRuntime.SetStartupModeArgumentPrefix.Length..]);
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RemoveStartupIntegrationArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RemoveStartupIntegration();
        }

        using var appMutex = new Mutex(true, SteamLoaderRuntime.AppMutexName, out var ownsAppMutex);
        if (!ownsAppMutex)
        {
            TryRequestSteamAttention();
            return 0;
        }

        if (consoleStartRequested)
        {
            InstallConsoleStartSafetyNet();
        }

        var executablePath =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
        var autostartService = new WindowsAutostartService(
            SteamLoaderRuntime.AutostartValueName,
            "SteamLoader",
            "SteamTools");
        var disabledStartupEntries = autostartService.DisableSteamAutostartEntries(SteamStartupDiagnostics.Write);
        var shellService = new WindowsShellService();
        var settingsService = new SteamLoaderSettingsService(
            autostartService,
            shellService,
            executablePath,
            SteamLoaderRuntime.LegacyShellLaunchArguments,
            Path.Combine(AppContext.BaseDirectory, "data", "tfs.json"));
        var settingsSnapshot = settingsService.EnsureDefaultConsoleModeEnabled();
        var consoleStartMode = SteamLoaderRuntime.ShouldUseConsoleStart(
            consoleStartRequested,
            settingsSnapshot.StartupMode);
        var consoleBootstrapMode = consoleStartMode;
        SteamStartupDiagnostics.Write(
            $"main startup mode={settingsSnapshot.StartupMode} consoleStart={consoleStartMode} " +
            $"legacyShellLaunch={legacyShellLaunch} disabledConflictingAutostarts={disabledStartupEntries}");
        if (consoleStartRequested && !consoleStartMode)
        {
            // A sign-in task from an earlier Console Mode: start like eTray and
            // keep the desktop visible. Switching modes removes that task.
            SteamStartupDiagnostics.Write(
                $"console start task fired outside Console Mode (mode={settingsSnapshot.StartupMode}); starting without the console cover");
        }

        ConsoleStartChromeCover? consoleChromeCover = null;
        if (consoleStartMode)
        {
            EnsureNormalProcessPriority();
            // Explorer starts right next to TFS here. Keep its taskbar off the
            // splash from the very first frame it exists.
            consoleChromeCover = new ConsoleStartChromeCover(new WindowsShellVisibilityService());
            consoleChromeCover.Start();
        }

        if (consoleBootstrapMode)
        {
            // A desktop request from an earlier session must not keep the
            // taskbar over this console start.
            new ConsoleShellState().ClearDesktopRequest();
        }

        // Resolve Steam early and, in a console start, launch Big Picture as the
        // very first action - before settings and the launcher sync - so its
        // (slow, on handhelds) cold start begins immediately and everything
        // else happens while Steam is already loading.
        var steamInstallationService = new SteamInstallationService(
            new SteamInstallPathSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "steam-install-path.json")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        if (consoleBootstrapMode)
        {
            var existingSteamSession = SteamClientLaunchService.IsSteamRunningForStartup();
            var initialLaunchState = SteamClientLaunchService.RequestSteamStartForTools(
                steamInstallationService);
            SteamStartupDiagnostics.Write(
                $"console bootstrap initial request={initialLaunchState.Message}");

            // Steam's official shutdown can take several seconds when replacing
            // an incompatible startup. Keep that serialized recovery off the UI
            // startup path so the splash appears immediately and can explain the
            // current state instead of leaving a blank screen.
            if (existingSteamSession)
            {
                _ = Task.Run(async () =>
                {
                    // Let an already-running client react to the Gamepad UI
                    // request before checking whether a controlled restart is needed.
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    var launchState = SteamClientLaunchService.PrepareConsoleStartup(
                        steamInstallationService);
                    SteamStartupDiagnostics.Write($"console bootstrap result={launchState.Message}");
                });
            }
        }

        var runStartupSync = consoleBootstrapMode || args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.StartupSyncArgument, StringComparison.OrdinalIgnoreCase));
        // A normal tray/manager/debug launch never opens a splash.
        var consoleStartupMode = consoleBootstrapMode;

        var startHiddenInTray = true;

        var processManager = new SteamLoaderProcessManager(
            new Uri("http://127.0.0.1:47652/"),
            SteamLoaderRuntime.BackgroundArgument);
        if (consoleBootstrapMode && !settingsSnapshot.FirstRunCompleted)
        {
            settingsService.CompleteFirstRunSetup();
        }

        if (startHiddenInTray && !runStartupSync)
        {
            SteamClientLaunchService.RequestSteamStartForTools(steamInstallationService);
        }

        var releaseUpdateService = new ReleaseUpdateService();
        var supportBundleService = new SupportBundleService(shellService);

        var viewModel = new MainWindowViewModel(
            processManager,
            autostartService,
            shellService,
            settingsService,
            releaseUpdateService,
            supportBundleService,
            steamInstallationService,
            consoleStartupMode,
            runStartupSync,
            consoleChromeCover);

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        var window = new MainWindow
        {
            DataContext = viewModel,
            StartHiddenInTray = startHiddenInTray,
            StartupSplashMode = consoleStartupMode
        };

        using var trayIconController = new TrayIconController(application, window, viewModel);
        trayIconController.Initialize();

        application.Startup += async (_, _) =>
        {
            if (viewModel.ShowStartupSplash)
            {
                // Console Mode: Explorer is drawing the desktop right now. Cover
                // it at once with the splash background while the covers load.
                if (consoleChromeCover is not null)
                {
                    var startCover = CreateConsoleStartCover();
                    startCover.Show();
                    consoleChromeCover.AttachCoverWindow(
                        application.Dispatcher,
                        new System.Windows.Interop.WindowInteropHelper(startCover).Handle);
                    window.ContentRendered += (_, _) => startCover.Close();
                }

                // Wait for game covers to be ready before showing the window so
                // the mosaic appears immediately without a pop-in effect.
                // Falls back gracefully after 2.5 s if covers are unavailable.
                if (viewModel.ShowStartupSplash)
                    await viewModel.AwaitSplashCoversAsync();

                window.Show();
                consoleChromeCover?.AttachCoverWindow(
                    application.Dispatcher,
                    new System.Windows.Interop.WindowInteropHelper(window).Handle);
                return;
            }

            await window.InitializeHiddenAsync();
        };

        application.MainWindow = window;
        return application.Run();
    }

    private static int _consoleStartSafetyNetInstalled;

    private static void InstallConsoleStartSafetyNet()
    {
        if (Interlocked.Exchange(ref _consoleStartSafetyNetInstalled, 1) == 1)
        {
            return;
        }

        // Console Mode keeps Explorer running, so a crash already ends on the
        // desktop. Only the taskbar the start cover hid has to come back.
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            try
            {
                SteamStartupDiagnostics.Write(
                    $"console start crashed; showing the Windows taskbar again: {eventArgs.ExceptionObject}");
            }
            catch
            {
            }

            try
            {
                new WindowsShellVisibilityService().RestoreShellChrome();
            }
            catch
            {
            }
        };
    }

    private static Window CreateConsoleStartCover()
    {
        // Same color as the splash background, so the hand-over is invisible.
        return new Window
        {
            Title = SteamLoaderRuntime.ProductName,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowState = WindowState.Maximized,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x08, 0x10, 0x19))
        };
    }

    private static void EnsureNormalProcessPriority()
    {
        // Sign-in tasks can start below normal priority, and Steam and games
        // started by TFS inherit that class. The task asks for normal already;
        // this also covers a task edited by hand.
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            if (process.PriorityClass is System.Diagnostics.ProcessPriorityClass.BelowNormal
                or System.Diagnostics.ProcessPriorityClass.Idle)
            {
                process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                SteamStartupDiagnostics.Write("console start raised TFS to normal priority");
            }
        }
        catch
        {
        }
    }

    private static int RemoveStartupIntegration()
    {
        try
        {
            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
            var autostartService = new WindowsAutostartService(
                SteamLoaderRuntime.AutostartValueName,
                "SteamLoader",
                "SteamTools");
            var settingsService = new SteamLoaderSettingsService(
                autostartService,
                new WindowsShellService(),
                executablePath,
                SteamLoaderRuntime.LegacyShellLaunchArguments,
                Path.Combine(AppContext.BaseDirectory, "data", "tfs.json"));
            settingsService.RemoveStartupIntegration();
            new WindowsShellVisibilityService().RestoreShellChrome();
            var consoleShellState = new ConsoleShellState();
            consoleShellState.ClearDesktopRequest();
            consoleShellState.ClearStartCoverOwner();
            SteamStartupDiagnostics.Write("startup integration removed for uninstall");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RunSplashPreview(string[] args)
    {
        var durationSeconds = ParsePreviewDurationSeconds(args);
        var executablePath =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
        var processManager = new SteamLoaderProcessManager(
            new Uri("http://127.0.0.1:47652/"),
            SteamLoaderRuntime.BackgroundArgument);
        var autostartService = new WindowsAutostartService(
            SteamLoaderRuntime.AutostartValueName,
            "SteamLoader",
            "SteamTools");
        var shellService = new WindowsShellService();
        var settingsService = new SteamLoaderSettingsService(
            autostartService,
            shellService,
            executablePath,
            SteamLoaderRuntime.LegacyShellLaunchArguments,
            Path.Combine(AppContext.BaseDirectory, "data", "tfs.json"));
        var releaseUpdateService = new ReleaseUpdateService();
        var supportBundleService = new SupportBundleService(shellService);
        var previewSteamInstallationService = new SteamInstallationService(
            new SteamInstallPathSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "steam-install-path.json")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var viewModel = new MainWindowViewModel(
            processManager,
            autostartService,
            shellService,
            settingsService,
            releaseUpdateService,
            supportBundleService,
            previewSteamInstallationService,
            false,
            false);

        var duration = TimeSpan.FromSeconds(durationSeconds);
        viewModel.StartSplashPreview(duration);

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        var window = new MainWindow
        {
            DataContext = viewModel,
            PreviewSplashMode = true,
            PreviewSplashDuration = duration,
            StartHiddenInTray = false,
            StartupSplashMode = true
        };

        application.MainWindow = window;
        application.Startup += async (_, _) =>
        {
            await viewModel.AwaitSplashCoversAsync();
            window.Show();
        };
        return application.Run();
    }

    private static void TryRequestSteamAttention()
    {
        try
        {
            var steamInstallationService = new SteamInstallationService(
                new SteamInstallPathSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "steam-install-path.json")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            SteamClientLaunchService.RequestSteamAttention(steamInstallationService);
        }
        catch
        {
            // Best effort only: duplicate launches should exit quietly even if
            // Steam cannot be nudged into the foreground right now.
        }
    }

    private static int RepairSteamStartup()
    {
        try
        {
            var steamInstallationService = new SteamInstallationService(
                new SteamInstallPathSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "steam-install-path.json")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var service = new SteamClientLaunchService(
                httpClient,
                new Uri("http://127.0.0.1:8080"),
                steamInstallationService,
                isHandheld: true);
            var result = service.RestartSteamForSteamTools(
                clearWebCache: true,
                respectProtectedActivity: true);
            SteamStartupDiagnostics.Write($"external hard Steam startup repair result={result.Message}");
            return result.Message.StartsWith("Steam is restarting", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"external hard Steam startup repair failed: {exception}");
            return 1;
        }
    }

    private static int ParsePreviewDurationSeconds(string[] args)
    {
        var prefix = $"{SteamLoaderRuntime.PreviewSplashDurationArgument}=";
        var durationArgument = args.FirstOrDefault(argument =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (durationArgument is not null &&
            int.TryParse(durationArgument[prefix.Length..], out var parsed))
        {
            return Math.Clamp(parsed, 1, 30);
        }

        return 5;
    }

    private static int ConfigureStartupMode(string mode)
    {
        if (!SteamLoaderSettingsService.IsSupportedStartupMode(mode))
        {
            Console.Error.WriteLine($"Unsupported startup mode '{mode}'. Use console, shell or tray.");
            return 2;
        }

        try
        {
            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
            var autostartService = new WindowsAutostartService(
                SteamLoaderRuntime.AutostartValueName,
                "SteamLoader",
                "SteamTools");
            var shellService = new WindowsShellService();
            var settingsService = new SteamLoaderSettingsService(
                autostartService,
                shellService,
                executablePath,
                SteamLoaderRuntime.LegacyShellLaunchArguments,
                Path.Combine(AppContext.BaseDirectory, "data", "tfs.json"));

            settingsService.SetStartupMode(mode);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RestoreXboxMode()
    {
        try
        {
            new LegacyXboxModeCleanup().Run(leaveActiveSession: true);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RunGamepadHelper()
    {
        try
        {
            var helperHost = new TfsGamepadHelperHost();
            return helperHost.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunHidDebugWindow()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "data", "hid-debug.log");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, string.Empty);

            using var monitor = new HidMenuButtonMonitor();
            var logLock = new object();

            var instructionsText = new System.Windows.Controls.TextBlock
            {
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                Text =
                    "Press the controller button you want to inspect. " +
                    $"Tools for Steam currently expects HID button usage {HidMenuButtonMonitor.ExpectedMenuButtonUsage} " +
                    "for the Xbox Menu / Start button. This window logs the exact button usages reported by the device."
            };

            var stateText = new System.Windows.Controls.TextBlock
            {
                Margin = new Thickness(0, 0, 0, 12),
                Text = $"Waiting for HID input... Log: {logPath}"
            };

            var outputText = new System.Windows.Controls.TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                MinHeight = 420
            };

            void AppendLine(string line)
            {
                lock (logLock)
                {
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }

                if (outputText.Dispatcher.CheckAccess())
                {
                    outputText.AppendText(line + Environment.NewLine);
                    outputText.ScrollToEnd();
                    stateText.Text = $"Last update: {DateTime.Now:HH:mm:ss} | Log: {logPath}";
                    return;
                }

                _ = outputText.Dispatcher.BeginInvoke(() =>
                {
                    outputText.AppendText(line + Environment.NewLine);
                    outputText.ScrollToEnd();
                    stateText.Text = $"Last update: {DateTime.Now:HH:mm:ss} | Log: {logPath}";
                });
            }

            monitor.ReportObserved += report =>
            {
                var usagesText = report.ButtonUsages.Count == 0
                    ? "-"
                    : string.Join(", ", report.ButtonUsages);
                var deviceHandleText = $"0x{unchecked((ulong)report.DeviceHandle.ToInt64()):X}";
                var line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                    $"device={deviceHandleText} reportLength={report.ReportLength} " +
                    $"kind={report.InputKind} pressed={report.IsPressed} code={report.InputCode} " +
                    $"usages=[{usagesText}] expectedUsageDown={report.IsExpectedMenuUsagePressed} " +
                    $"detail=\"{report.Detail}\" path=\"{report.DeviceName}\"";
                AppendLine(line);
            };

            var copyPathButton = new System.Windows.Controls.Button
            {
                Content = "Copy Log Path",
                MinWidth = 120,
                Margin = new Thickness(0, 0, 8, 0)
            };
            copyPathButton.Click += (_, _) => System.Windows.Clipboard.SetText(logPath);

            var clearButton = new System.Windows.Controls.Button
            {
                Content = "Clear",
                MinWidth = 90,
                Margin = new Thickness(0, 0, 8, 0)
            };
            clearButton.Click += (_, _) =>
            {
                lock (logLock)
                {
                    File.WriteAllText(logPath, string.Empty);
                }

                outputText.Clear();
                stateText.Text = $"Log cleared at {DateTime.Now:HH:mm:ss} | Log: {logPath}";
            };

            var closeButton = new System.Windows.Controls.Button
            {
                Content = "Close",
                MinWidth = 90
            };

            var buttonBar = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };
            buttonBar.Children.Add(copyPathButton);
            buttonBar.Children.Add(clearButton);
            buttonBar.Children.Add(closeButton);

            var contentPanel = new System.Windows.Controls.DockPanel
            {
                Margin = new Thickness(16)
            };
            System.Windows.Controls.DockPanel.SetDock(instructionsText, System.Windows.Controls.Dock.Top);
            System.Windows.Controls.DockPanel.SetDock(stateText, System.Windows.Controls.Dock.Top);
            System.Windows.Controls.DockPanel.SetDock(buttonBar, System.Windows.Controls.Dock.Top);
            contentPanel.Children.Add(instructionsText);
            contentPanel.Children.Add(stateText);
            contentPanel.Children.Add(buttonBar);
            contentPanel.Children.Add(outputText);

            var window = new Window
            {
                Title = "Tools for Steam HID Debug",
                Width = 980,
                Height = 680,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = contentPanel
            };

            closeButton.Click += (_, _) => window.Close();

            AppendLine(
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ready expectedMenuUsage={HidMenuButtonMonitor.ExpectedMenuButtonUsage}");

            var application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose
            };
            application.MainWindow = window;
            return application.Run(window);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RegisterGamepadHelperTask()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "data", "gamepad-helper-task.log");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
            var taskService = new GamepadHelperScheduledTaskService(
                executablePath,
                AppContext.BaseDirectory);
            taskService.EnsureRegistered();

            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, exception.Message);
            }
            catch
            {
            }

            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RegisterInstalledHelperTasks()
    {
        try
        {
            if (!ElevatedHelperTaskService.IsCurrentProcessElevated())
            {
                throw new InvalidOperationException("Admin rights are required to register the elevated TFS helper tasks.");
            }

            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
            var gamepadTaskService = new GamepadHelperScheduledTaskService(
                executablePath,
                AppContext.BaseDirectory);

            gamepadTaskService.EnsureRegistered();

            var autostartService = new WindowsAutostartService(
                SteamLoaderRuntime.AutostartValueName,
                "SteamLoader",
                "SteamTools");
            var disabledCount = autostartService.DisableSteamAutostartEntries(SteamStartupDiagnostics.Write);
            SteamStartupDiagnostics.Write($"elevated helper registration sanitized conflicting autostarts disabled={disabledCount}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int CheckGamepadHelperTask()
    {
        try
        {
            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
            var taskService = new GamepadHelperScheduledTaskService(
                executablePath,
                AppContext.BaseDirectory);
            return taskService.IsRegistered() ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static int SanitizeSteamAutostart()
    {
        try
        {
            var autostartService = new WindowsAutostartService(
                SteamLoaderRuntime.AutostartValueName,
                "SteamLoader",
                "SteamTools");
            var disabledCount = autostartService.DisableSteamAutostartEntries(SteamStartupDiagnostics.Write);
            SteamStartupDiagnostics.Write($"installer autostart sanitation complete disabled={disabledCount}");
            return 0;
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"installer autostart sanitation failed: {exception}");
            return 1;
        }
    }

    private static async Task<int> RunBackgroundHostAsync()
    {
        using var backgroundHostMutex = new Mutex(true, SteamLoaderRuntime.BackgroundHostMutexName, out var ownsBackgroundHostMutex);
        if (!ownsBackgroundHostMutex)
        {
            return 0;
        }

        var cancellation = new CancellationTokenSource();

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;

        EventHandler processExitHandler = (_, _) =>
        {
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
            }
        };
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            var hostState = new SteamLoaderHostState();
            var host = new SteamLoaderBackgroundHost(hostState);
            await host.RunAsync(cancellation.Token, cancellation.Cancel);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            Console.CancelKeyPress -= cancelHandler;
            cancellation.Dispose();
        }
    }
}
