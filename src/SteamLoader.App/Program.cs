using System.Windows;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Settings;
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
        var runStartupSync = args.Any(argument =>
            string.Equals(argument, SteamLoaderRuntime.StartupSyncArgument, StringComparison.OrdinalIgnoreCase));
        if (shellBootstrapMode && !runStartupSync)
        {
            var bootstrapShellService = new WindowsShellService();
            bootstrapShellService.StartWindowsShellIfNeeded();
        }

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
        var releaseUpdateService = new ReleaseUpdateService();
        var supportBundleService = new SupportBundleService(shellService);

        var viewModel = new MainWindowViewModel(
            processManager,
            autostartService,
            shellService,
            settingsService,
            releaseUpdateService,
            supportBundleService,
            SteamLoaderRuntime.ShellLaunchArguments,
            shellBootstrapMode,
            runStartupSync);

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        var window = new MainWindow
        {
            DataContext = viewModel,
            StartHiddenInTray = startHiddenInTray,
            ShellBootstrapMode = shellBootstrapMode
        };

        if (startHiddenInTray && !shellBootstrapMode)
        {
            window.ShowInTaskbar = false;
            window.WindowState = WindowState.Minimized;
        }

        using var trayIconController = new TrayIconController(application, window, viewModel);
        trayIconController.Initialize();

        return application.Run(window);
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
