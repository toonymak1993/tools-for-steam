using System.Windows;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;
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

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.BackgroundArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunBackgroundHostAsync().GetAwaiter().GetResult();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.FpsHelperArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunFpsHelper();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.RegisterFpsHelperTaskArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RegisterFpsHelperTask();
        }

        if (args.Any(argument => string.Equals(argument, SteamLoaderRuntime.CheckFpsHelperTaskArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return CheckFpsHelperTask();
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

        var shellBootstrapMode = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.ShellBootstrapArgument, StringComparison.OrdinalIgnoreCase));
        if (shellBootstrapMode)
        {
            var bootstrapShellService = new WindowsShellService();
            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
            bootstrapShellService.PrepareCurrentSession(executablePath, SteamLoaderRuntime.ShellLaunchArguments);
        }

        var showManager = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.ManagerArgument, StringComparison.OrdinalIgnoreCase));
        var runStartupSync = shellBootstrapMode || args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.StartupSyncArgument, StringComparison.OrdinalIgnoreCase));
        var consoleStartupMode = shellBootstrapMode;

        var startHiddenInTray = !showManager;

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
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path."),
            SteamLoaderRuntime.ShellLaunchArguments,
            Path.Combine(AppContext.BaseDirectory, "data", "tfs.json"));
        var settingsSnapshot = settingsService.EnsureDefaultConsoleModeEnabled();

        if (shellBootstrapMode && !settingsSnapshot.FirstRunCompleted)
        {
            settingsService.CompleteFirstRunSetup();
        }

        var steamInstallationService = new SteamInstallationService(
            new SteamInstallPathSettingsStore(Path.Combine(AppContext.BaseDirectory, "data", "steam-install-path.json")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

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
            if (showManager || viewModel.ShowStartupSplash)
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

    private static int RunFpsHelper()
    {
        try
        {
            var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            var settingsStore = new PerformanceSettingsStore(Path.Combine(dataDirectory, "performance.json"));
            var statusStore = new PerformanceStatusStore(Path.Combine(dataDirectory, "performance-runtime.json"));
            var helperHost = new TfsFpsHelperHost(settingsStore, statusStore);
            return helperHost.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RegisterFpsHelperTask()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "data", "fps-helper-task.log");

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
            var taskService = new FpsHelperScheduledTaskService(
                executablePath,
                SteamLoaderRuntime.FpsHelperArgument,
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

    private static int CheckFpsHelperTask()
    {
        try
        {
            var executablePath =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the Tools for Steam executable path.");
            var taskService = new FpsHelperScheduledTaskService(
                executablePath,
                SteamLoaderRuntime.FpsHelperArgument,
                AppContext.BaseDirectory);
            return taskService.IsRegistered() ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static async Task<int> RunBackgroundHostAsync()
    {
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
