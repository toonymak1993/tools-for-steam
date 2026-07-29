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

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RestoreXboxModeArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RestoreXboxMode();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.CheckXboxModeSupportArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return CheckXboxModeSupport();
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

        using var appMutex = new Mutex(true, SteamLoaderRuntime.AppMutexName, out var ownsAppMutex);
        if (!ownsAppMutex)
        {
            TryRequestSteamAttention();
            return 0;
        }

        var shellBootstrapRequested = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.ShellBootstrapArgument, StringComparison.OrdinalIgnoreCase));

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
            new XboxModeService(),
            executablePath,
            SteamLoaderRuntime.ShellLaunchArguments,
            Path.Combine(AppContext.BaseDirectory, "data", "tfs.json"));
        var settingsSnapshot = settingsService.EnsureDefaultConsoleModeEnabled();
        var shellBootstrapMode = SteamLoaderRuntime.ShouldUseShellBootstrap(
            shellBootstrapRequested,
            settingsSnapshot.StartupMode);
        var xboxBootstrapMode = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.XboxBootstrapArgument, StringComparison.OrdinalIgnoreCase));
        var xboxHostedSplash = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.XboxHostedSplashArgument, StringComparison.OrdinalIgnoreCase));
        var consoleBootstrapMode = shellBootstrapMode || xboxBootstrapMode;
        SteamStartupDiagnostics.Write(
            $"main startup mode={settingsSnapshot.StartupMode} shellBootstrap={shellBootstrapMode} " +
            $"xboxBootstrap={xboxBootstrapMode} disabledConflictingAutostarts={disabledStartupEntries}");

        // Resolve Steam early and, in console/shell startup, launch Big Picture as
        // the very first action - before shell setup, settings and the launcher
        // sync - so its (slow, on handhelds) cold start begins immediately and
        // everything else happens while Steam is already loading.
        var steamInstallationService = new SteamInstallationService(
            new SteamInstallPathSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "steam-install-path.json")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        if (consoleBootstrapMode)
        {
            var launchState = SteamClientLaunchService.PrepareConsoleStartup(steamInstallationService);
            SteamStartupDiagnostics.Write($"console bootstrap result={launchState.Message}");
        }

        if (shellBootstrapMode)
        {
            var bootstrapShellService = new WindowsShellService();
            bootstrapShellService.PrepareCurrentSession(executablePath, SteamLoaderRuntime.ShellLaunchArguments);
        }

        var runStartupSync = shellBootstrapMode || xboxBootstrapMode || args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.StartupSyncArgument, StringComparison.OrdinalIgnoreCase));
        var consoleStartupMode = !xboxHostedSplash;

        var startHiddenInTray = true;

        var processManager = new SteamLoaderProcessManager(
            new Uri("http://127.0.0.1:47652/"),
            SteamLoaderRuntime.BackgroundArgument);
        if (shellBootstrapMode && !settingsSnapshot.FirstRunCompleted)
        {
            settingsService.CompleteFirstRunSetup();
        }

        if (startHiddenInTray && !runStartupSync)
        {
            SteamClientLaunchService.RequestSteamStartForTools(steamInstallationService);
        }

        if (shellBootstrapMode && !runStartupSync)
        {
            shellService.StartWindowsShellIfNeeded();
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
            SteamLoaderRuntime.ShellLaunchArguments,
            shellBootstrapMode,
            consoleStartupMode,
            runStartupSync);

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        var window = new MainWindow
        {
            DataContext = viewModel,
            StartHiddenInTray = startHiddenInTray,
            ShellBootstrapMode = consoleStartupMode
        };

        using var trayIconController = new TrayIconController(application, window, viewModel);
        trayIconController.Initialize();

        application.Startup += async (_, _) =>
        {
            if (viewModel.ShowStartupSplash)
            {
                // Wait for game covers to be ready before showing the window so
                // the mosaic appears immediately without a pop-in effect.
                // Falls back gracefully after 2.5 s if covers are unavailable.
                if (viewModel.ShowStartupSplash)
                    await viewModel.AwaitSplashCoversAsync();

                window.Show();
                return;
            }

            await window.InitializeHiddenAsync();
        };

        application.MainWindow = window;
        return application.Run();
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
            SteamLoaderRuntime.ShellLaunchArguments,
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
            SteamLoaderRuntime.ShellLaunchArguments,
            false,
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
            ShellBootstrapMode = true
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
            var result = service.RestartSteamForSteamTools();
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
                new XboxModeService(),
                executablePath,
                SteamLoaderRuntime.ShellLaunchArguments,
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
            new XboxModeService().RestoreOnUninstall();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int CheckXboxModeSupport()
    {
        var support = new XboxModeService().GetSupportStatus();
        Console.WriteLine(support.Reason);
        return support.IsSupported ? 0 : 1;
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
