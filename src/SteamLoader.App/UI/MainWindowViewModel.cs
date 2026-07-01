using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamLoader.App;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.UI;

public sealed class MainWindowViewModel : BindableBase
{
    private const int BigPictureVisibleWindowsShellHandOffDelaySeconds = 3;
    private const int RequiredStableShellHandOffPollsWithSteamSignal = 2;
    private const int RequiredStableShellHandOffPollsWithoutSteamSignal = 4;
    private const int ShowWindowRestore = 9;

    private readonly SteamLoaderProcessManager _processManager;
    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly SteamLoaderSettingsService _settingsService;
    private readonly ReleaseUpdateService _releaseUpdateService;
    private readonly SupportBundleService _supportBundleService;
    private readonly SteamInstallationService _steamInstallationService;
    private readonly string _shellLaunchArguments;
    private readonly bool _shellBootstrapMode;
    private readonly bool _consoleStartupMode;
    private readonly bool _runStartupSyncOnInitialize;
    private bool _isBusy;
    private bool _isRunning;
    private bool _autostartEnabled;
    private string _startupMode = SteamLoaderRuntime.StartupModeShell;
    private bool _initialized;
    private bool _startupSyncTriggered;
    private bool _showStartupSplash;
    private bool _showFirstRunSetup;
    private bool _windowsShellStarted;
    private Task? _shellBootstrapMonitorTask;
    private SteamLoaderHostStatus? _lastKnownStatus;
    private int _stableBigPictureVisiblePollCount;
    private int _stableSteamSignalPollCount;
    private string _serviceStateText = "Checking background host...";
    private string _serviceDetailText = "The manager is reading the current runtime status.";
    private string _steamStateText = "Waiting for status...";
    private string _apiStateText = "Waiting for status...";
    private string _autostartStateText = "Checking startup registration...";
    private string _setupChecklistText = "Setup checks have not run yet.";
    private string _recoveryHintText = "If Steam does not appear, start the Windows desktop and relaunch Tools for Steam.";
    private string _updateStateText = "Updates have not been checked yet.";
    private string _supportBundleText = "No support bundle has been exported yet.";
    private string _errorText = string.Empty;
    private bool _showStartupSplashText = true;
    private double _splashOverlayOpacity = 1.0;
    private string _splashWallpaperPath = string.Empty;
    private string _splashIconPath = string.Empty;
    private IReadOnlyList<BitmapSource> _splashGameCovers = [];
    private string _splashDebugText = string.Empty;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private Task _splashCoversTask = Task.CompletedTask;
    private int _windowsShellStartDelaySeconds;
    private UpdateCheckSnapshot? _updateSnapshot;

    public MainWindowViewModel(
        SteamLoaderProcessManager processManager,
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        SteamLoaderSettingsService settingsService,
        ReleaseUpdateService releaseUpdateService,
        SupportBundleService supportBundleService,
        SteamInstallationService steamInstallationService,
        string shellLaunchArguments,
        bool shellBootstrapMode,
        bool consoleStartupMode,
        bool runStartupSyncOnInitialize)
    {
        _processManager = processManager;
        _autostartService = autostartService;
        _shellService = shellService;
        _settingsService = settingsService;
        _releaseUpdateService = releaseUpdateService;
        _supportBundleService = supportBundleService;
        _steamInstallationService = steamInstallationService;
        _shellLaunchArguments = shellLaunchArguments;
        _shellBootstrapMode = shellBootstrapMode;
        _consoleStartupMode = consoleStartupMode;
        _runStartupSyncOnInitialize = runStartupSyncOnInitialize;
        ApplyGeneralSettingsSnapshot(_settingsService.GetSnapshot());
        _showStartupSplash = consoleStartupMode;
        if (consoleStartupMode)
        {
            _splashCoversTask = LoadSplashGameCoversAsync();
            _serviceStateText = "Preparing Tools for Steam";
            _serviceDetailText = "Starting the background service and preparing the fast Steam hand-off.";
            _steamStateText = "Startup sync runs first, then Steam opens as soon as shortcuts are ready.";
        }

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy && IsRunning);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => !IsBusy && IsRunning);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ToggleAutostartCommand = new RelayCommand(ToggleAutostart, () => !IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        StartDesktopCommand = new RelayCommand(StartDesktop);
        CompleteFirstRunCommand = new RelayCommand(CompleteFirstRunSetup, () => !IsBusy);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => !IsBusy && _updateSnapshot?.CanInstall == true);
        ExportSupportBundleCommand = new AsyncRelayCommand(ExportSupportBundleAsync, () => !IsBusy);
    }

    public string InstallPath => _processManager.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public string WindowTitle => "Tools for Steam";

    public string Subtitle => "Installed console shell and control panel for the Windows Quick Access toolkit.";

    public string ServiceStateText
    {
        get => _serviceStateText;
        private set => SetProperty(ref _serviceStateText, value);
    }

    public string ServiceDetailText
    {
        get => _serviceDetailText;
        private set => SetProperty(ref _serviceDetailText, value);
    }

    public string SteamStateText
    {
        get => _steamStateText;
        private set => SetProperty(ref _steamStateText, value);
    }

    public string ApiStateText
    {
        get => _apiStateText;
        private set => SetProperty(ref _apiStateText, value);
    }

    public string AutostartStateText
    {
        get => _autostartStateText;
        private set => SetProperty(ref _autostartStateText, value);
    }

    public string SetupChecklistText
    {
        get => _setupChecklistText;
        private set => SetProperty(ref _setupChecklistText, value);
    }

    public string RecoveryHintText
    {
        get => _recoveryHintText;
        private set => SetProperty(ref _recoveryHintText, value);
    }

    public string UpdateStateText
    {
        get => _updateStateText;
        private set => SetProperty(ref _updateStateText, value);
    }

    public string SupportBundleText
    {
        get => _supportBundleText;
        private set => SetProperty(ref _supportBundleText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaisePropertyChanged(nameof(StatusPillText));
                RefreshCommands();
            }
        }
    }

    public bool AutostartEnabled
    {
        get => _autostartEnabled;
        private set
        {
            if (SetProperty(ref _autostartEnabled, value))
            {
                RaisePropertyChanged(nameof(AutostartButtonText));
                RaisePropertyChanged(nameof(AutostartMenuText));
            }
        }
    }

    public string StartupMode
    {
        get => _startupMode;
        private set
        {
            if (SetProperty(ref _startupMode, value))
            {
                RaisePropertyChanged(nameof(AutostartButtonText));
                RaisePropertyChanged(nameof(AutostartMenuText));
            }
        }
    }

    public bool ShowStartupSplash
    {
        get => _showStartupSplash;
        private set => SetProperty(ref _showStartupSplash, value);
    }

    public bool ShowStartupSplashText
    {
        get => _showStartupSplashText;
        private set => SetProperty(ref _showStartupSplashText, value);
    }

    public double SplashOverlayOpacity
    {
        get => _splashOverlayOpacity;
        private set => SetProperty(ref _splashOverlayOpacity, value);
    }

    public string SplashWallpaperPath
    {
        get => _splashWallpaperPath;
        private set
        {
            if (SetProperty(ref _splashWallpaperPath, value))
            {
                RaisePropertyChanged(nameof(HasSplashWallpaper));
            }
        }
    }

    public bool HasSplashWallpaper => !string.IsNullOrWhiteSpace(SplashWallpaperPath);

    public string SplashIconPath
    {
        get => _splashIconPath;
        private set
        {
            if (SetProperty(ref _splashIconPath, value))
            {
                RaisePropertyChanged(nameof(HasCustomSplashIcon));
            }
        }
    }

    public bool HasCustomSplashIcon => !string.IsNullOrWhiteSpace(SplashIconPath);

    public IReadOnlyList<BitmapSource> SplashGameCovers
    {
        get => _splashGameCovers;
        private set => SetProperty(ref _splashGameCovers, value);
    }

    /// <summary>
    /// Awaits cover loading with a timeout. Returns when covers are ready OR when
    /// the timeout expires — whichever comes first. Never throws.
    /// </summary>
    public Task AwaitSplashCoversAsync(int timeoutMs = 2500) =>
        Task.WhenAny(_splashCoversTask, Task.Delay(timeoutMs));

    public string SplashDebugText
    {
        get => _splashDebugText;
        private set => SetProperty(ref _splashDebugText, value);
    }

    public int WindowsShellStartDelaySeconds
    {
        get => _windowsShellStartDelaySeconds;
        private set => SetProperty(ref _windowsShellStartDelaySeconds, value);
    }

    public bool ShowFirstRunSetup
    {
        get => _showFirstRunSetup;
        private set => SetProperty(ref _showFirstRunSetup, value);
    }

    public string StatusPillText => IsRunning ? "Running" : "Stopped";

    public string AutostartButtonText => StartupMode == SteamLoaderRuntime.StartupModeShell
        ? "Switch to Tray Startup"
        : "Switch to Shell Startup";

    public string AutostartMenuText => StartupMode switch
    {
        SteamLoaderRuntime.StartupModeShell => "Startup Mode: Shell takeover",
        SteamLoaderRuntime.StartupModeTray => "Startup Mode: Tray app",
        _ => "Startup Mode: Shell takeover"
    };

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ToggleAutostartCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand StartDesktopCommand { get; }

    public RelayCommand CompleteFirstRunCommand { get; }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public AsyncRelayCommand InstallUpdateCommand { get; }

    public AsyncRelayCommand ExportSupportBundleCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        await RefreshAsync();

        if (!IsRunning)
        {
            await StartAsync();
        }

        if (_runStartupSyncOnInitialize && !_startupSyncTriggered)
        {
            _startupSyncTriggered = true;
            await TriggerStartupSyncAsync();
        }

        if (_consoleStartupMode)
        {
            EnsureShellBootstrapMonitor();
        }
    }

    public void StartSplashPreview(TimeSpan duration)
    {
        ApplyGeneralSettingsSnapshot(_settingsService.GetSnapshot());
        _splashCoversTask = LoadSplashGameCoversAsync();
        ShowStartupSplash = true;
        ShowFirstRunSetup = false;
        ServiceStateText = "Splash preview";
        ServiceDetailText = $"Preview closes automatically in {Math.Ceiling(duration.TotalSeconds)} seconds.";
        SteamStateText = "No startup actions are running.";
        ApiStateText = "Preview mode";
        ErrorText = string.Empty;
    }

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            ErrorText = string.Empty;

            var status = await _processManager.GetStatusAsync();
            var settings = _settingsService.GetSnapshot();
            ApplyGeneralSettingsSnapshot(settings);
            _lastKnownStatus = status;
            IsRunning = status is not null;
            ShowFirstRunSetup = false;

            if (_consoleStartupMode && !_windowsShellStarted)
            {
                ApplyShellBootstrapStatus(status);
            }
            else if (status is null)
            {
                ServiceStateText = "Background host is offline.";
                ServiceDetailText = "Start the host to inject Tools for Steam into Steam Quick Access.";
                SteamStateText = "Steam connection is not available yet.";
                ApiStateText = _processManager.ApiBaseUri.ToString();
            }
            else
            {
                ServiceStateText = "Background host is running.";
                ServiceDetailText = status.ServiceMessage;
                SteamStateText = ResolveSteamState(status);
                ApiStateText = $"{_processManager.ApiBaseUri} ({FormatElapsed(status.StartedAtUtc)})";
                ErrorText = status.LastError ?? string.Empty;
            }

            SetupChecklistText = BuildSetupChecklistText(status);
            RecoveryHintText = BuildRecoveryHintText(status);

            StartupMode = settings.StartupMode;
            AutostartEnabled = string.Equals(settings.StartupMode, SteamLoaderRuntime.StartupModeShell, StringComparison.OrdinalIgnoreCase);
            AutostartStateText = BuildAutostartStateText(settings.StartupMode);
        }
        catch (Exception exception)
        {
            if (_consoleStartupMode && !_windowsShellStarted)
            {
                ApplyShellBootstrapStatus(null);
            }
            else
            {
                ErrorText = exception.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartAsync()
    {
        await RunManagedActionAsync(
            () => _processManager.StartAsync(),
            "Starting background host...");
    }

    private async Task StopAsync()
    {
        await RunManagedActionAsync(
            () => _processManager.StopAsync(),
            "Stopping background host...");
    }

    private async Task RestartAsync()
    {
        await RunManagedActionAsync(
            () => _processManager.RestartAsync(),
            "Restarting background host...");
    }

    private async Task RunManagedActionAsync(Func<Task> action, string pendingMessage)
    {
        IsBusy = true;
        ErrorText = string.Empty;
        ServiceDetailText = pendingMessage;

        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            var nextMode = string.Equals(StartupMode, SteamLoaderRuntime.StartupModeShell, StringComparison.OrdinalIgnoreCase)
                ? SteamLoaderRuntime.StartupModeTray
                : SteamLoaderRuntime.StartupModeShell;
            var settings = _settingsService.SetStartupMode(nextMode);
            StartupMode = settings.StartupMode;
            AutostartEnabled = string.Equals(settings.StartupMode, SteamLoaderRuntime.StartupModeShell, StringComparison.OrdinalIgnoreCase);
            AutostartStateText = BuildAutostartStateText(settings.StartupMode);
            ErrorText = string.Empty;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
        finally
        {
            RefreshCommands();
        }
    }

    private void OpenFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = InstallPath,
            UseShellExecute = true
        });
    }

    private void StartDesktop()
    {
        try
        {
            _shellService.StartWindowsShellIfNeeded();
            ErrorText = string.Empty;
            RecoveryHintText = "Windows desktop was requested. You can return to Steam after recovery.";
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
    }

    public void ToggleAutostartSetting()
    {
        ToggleAutostart();
    }

    public void OpenInstallFolder()
    {
        OpenFolder();
    }

    public void StartWindowsDesktop()
    {
        StartDesktop();
    }

    public async Task CheckForUpdatesAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        UpdateStateText = "Checking GitHub releases...";

        try
        {
            _updateSnapshot = await _releaseUpdateService.CheckAsync();
            UpdateStateText = _updateSnapshot.Message;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            UpdateStateText = "Update check failed.";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    public async Task InstallUpdateAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        UpdateStateText = "Preparing update...";

        try
        {
            _updateSnapshot ??= await _releaseUpdateService.CheckAsync();
            if (_updateSnapshot.CanInstall != true)
            {
                UpdateStateText = _updateSnapshot.Message;
                return;
            }

            await _processManager.StopAsync();
            var executableName = Path.GetFileNameWithoutExtension(_processManager.ExecutablePath);
            var processIds = Process.GetProcessesByName(executableName)
                .Select(process => process.Id)
                .ToArray();

            _updateSnapshot = await _releaseUpdateService.BeginInstallLatestAsync(
                InstallPath,
                _processManager.ExecutablePath,
                processIds);

            UpdateStateText = _updateSnapshot.Message;
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            UpdateStateText = "Update install failed.";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    public async Task ExportSupportBundleAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        SupportBundleText = "Collecting diagnostics...";

        try
        {
            var status = await _processManager.GetStatusAsync();
            var settings = _settingsService.GetSnapshot();
            var bundlePath = _supportBundleService.Export(status, settings);
            SupportBundleText = bundlePath;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            SupportBundleText = "Support bundle export failed.";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private void CompleteFirstRunSetup()
    {
        try
        {
            _settingsService.CompleteFirstRunSetup();
            ShowFirstRunSetup = false;
            ErrorText = string.Empty;
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
    }

    private async Task TriggerStartupSyncAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        ServiceDetailText = "Syncing launchers and writing Steam shortcuts...";

        try
        {
            await _processManager.RequestStartupSyncAsync();
            ServiceDetailText = "Startup sync triggered. Steam will open as soon as shortcuts are ready.";
        }
        catch (Exception exception)
        {
            ServiceDetailText = "Startup sync could not be confirmed. Steam launch will continue.";
            ErrorText = exception.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }

        if (_consoleStartupMode)
        {
            EnsureShellBootstrapMonitor();
        }
    }

    private string ResolveSteamState(SteamLoaderHostStatus status)
    {
        if (status.QuickAccessAttached)
        {
            return "Attached to Steam Quick Access.";
        }

        if (status.SharedContextAttached)
        {
            return "Shared Steam context is ready. Open Quick Access to attach the panel.";
        }

        return "Waiting for Steam GamepadUI and the SharedJSContext.";
    }

    private static string FormatElapsed(DateTimeOffset startedAtUtc)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAtUtc;

        if (elapsed.TotalMinutes < 1)
        {
            return $"up for {Math.Max(1, (int)elapsed.TotalSeconds)}s";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"up for {(int)elapsed.TotalMinutes}m";
        }

        return $"up for {(int)elapsed.TotalHours}h {(int)elapsed.Minutes}m";
    }

    private void RefreshCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        ToggleAutostartCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        StartDesktopCommand.RaiseCanExecuteChanged();
        CompleteFirstRunCommand.RaiseCanExecuteChanged();
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        InstallUpdateCommand.RaiseCanExecuteChanged();
        ExportSupportBundleCommand.RaiseCanExecuteChanged();
    }

    private static string BuildSetupChecklistText(SteamLoaderHostStatus? status)
    {
        if (status is null)
        {
            return "Host offline - Tools for Steam can start it from this manager.";
        }

        if (status.QuickAccessAttached)
        {
            return "Ready - host online, Steam DevTools reachable, and Quick Access attached.";
        }

        if (status.SharedContextAttached)
        {
            return "Almost ready - Steam DevTools is reachable. Open Quick Access once to attach the panel.";
        }

        return status.ServiceMessage.Contains("DevTools", StringComparison.OrdinalIgnoreCase)
            ? "Recovering - Steam is being started or restarted with DevTools enabled."
            : "Waiting - Steam Gamepad UI is not ready yet.";
    }

    private static string BuildRecoveryHintText(SteamLoaderHostStatus? status)
    {
        if (status is null)
        {
            return "Start the host first. If you are in shell mode and need Windows, press Start Windows Desktop.";
        }

        if (status.LastError is not null)
        {
            return "Use Restart Host first. If Steam still does not attach, start the Windows desktop and check Steam.";
        }

        if (status.QuickAccessAttached)
        {
            return "No recovery needed. Tools for Steam is attached and ready.";
        }

        return "If this state lasts too long, use Restart Host or Start Windows Desktop. Tools for Steam will keep trying safely.";
    }

    private void EnsureShellBootstrapMonitor()
    {
        if (!_consoleStartupMode || _shellBootstrapMonitorTask is not null)
        {
            return;
        }

        _shellBootstrapMonitorTask = MonitorShellBootstrapAsync();
    }

    private async Task MonitorShellBootstrapAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(180);
        ShellHandOffReadiness? readiness = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            readiness = CaptureShellHandOffReadiness();

            if (readiness.WindowsShellHandOffReady)
            {
                break;
            }

            await Task.Delay(900);
            await RefreshAsync();
        }

        readiness ??= CaptureShellHandOffReadiness();

        if (!readiness.BigPictureVisible)
        {
            ServiceStateText = "Steam needs more time";
            ServiceDetailText = _shellBootstrapMode
                ? "Starting Windows Desktop recovery while Tools for Steam keeps trying in the tray."
                : "Keeping Tools for Steam alive while Steam continues to start.";
            SteamStateText = "Steam was not ready before the console-mode timeout.";

            await DismissStartupSplashAsync();
            CompleteShellBootstrap();
            return;
        }

        if (ShowStartupSplash)
        {
            await DismissStartupSplashAsync();
            TryFocusSteamWindow();
        }

        await WaitBeforeWindowsShellHandoffAsync();
        CompleteShellBootstrap();
    }

    private ShellHandOffReadiness CaptureShellHandOffReadiness()
    {
        var bigPictureVisible = IsSteamBigPictureWindowVisible();
        var quickAccessAttached = _lastKnownStatus?.QuickAccessAttached == true;
        var sharedContextAttached = _lastKnownStatus?.SharedContextAttached == true;
        var steamSignalReady = quickAccessAttached || sharedContextAttached;

        if (!bigPictureVisible)
        {
            _stableBigPictureVisiblePollCount = 0;
            _stableSteamSignalPollCount = 0;
            return new ShellHandOffReadiness(
                BigPictureVisible: false,
                SteamSignalReady: false,
                WindowsShellHandOffReady: false);
        }

        _stableBigPictureVisiblePollCount += 1;
        _stableSteamSignalPollCount = steamSignalReady
            ? _stableSteamSignalPollCount + 1
            : 0;

        var windowsShellHandOffReady =
            _stableSteamSignalPollCount >= RequiredStableShellHandOffPollsWithSteamSignal ||
            _stableBigPictureVisiblePollCount >= RequiredStableShellHandOffPollsWithoutSteamSignal;

        return new ShellHandOffReadiness(
            BigPictureVisible: true,
            SteamSignalReady: steamSignalReady,
            WindowsShellHandOffReady: windowsShellHandOffReady);
    }

    private static bool IsSteamBigPictureWindowVisible()
    {
        var processes = Process.GetProcessesByName("steamwebhelper");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited &&
                        process.MainWindowHandle != IntPtr.Zero &&
                        process.MainWindowTitle.Contains("Big Picture", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void TryFocusSteamWindow()
    {
        var handle = FindSteamWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(handle, ShowWindowRestore);
        SetForegroundWindow(handle);
    }

    private static IntPtr FindSteamWindowHandle()
    {
        var preferredHandle = FindSteamWindowHandle("steamwebhelper", preferSteamTitle: true);
        if (preferredHandle != IntPtr.Zero)
        {
            return preferredHandle;
        }

        var steamHandle = FindSteamWindowHandle("steam", preferSteamTitle: false);
        if (steamHandle != IntPtr.Zero)
        {
            return steamHandle;
        }

        return FindSteamWindowHandle("steamwebhelper", preferSteamTitle: false);
    }

    private static IntPtr FindSteamWindowHandle(string processName, bool preferSteamTitle)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            var fallbackHandle = IntPtr.Zero;
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    fallbackHandle = process.MainWindowHandle;
                    if (!preferSteamTitle ||
                        process.MainWindowTitle.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
                        process.MainWindowTitle.Contains("Big Picture", StringComparison.OrdinalIgnoreCase))
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch
                {
                }
            }

            return fallbackHandle;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private void CompleteShellBootstrap()
    {
        if (_windowsShellStarted)
        {
            return;
        }

        _windowsShellStarted = true;
        if (_shellBootstrapMode)
        {
            _shellService.StartWindowsShellIfNeeded();
        }
    }

    private async Task DismissStartupSplashAsync()
    {
        const int steps = 20;
        const int stepDelayMs = 15; // 20 × 15ms = 300ms fade
        for (int i = steps - 1; i >= 0; i--)
        {
            SplashOverlayOpacity = (double)i / steps;
            await Task.Delay(stepDelayMs);
        }

        ShowStartupSplash = false;
        ShowFirstRunSetup = false;
        SplashOverlayOpacity = 1.0;
    }

    private async Task WaitBeforeWindowsShellHandoffAsync()
    {
        ApplyGeneralSettingsSnapshot(_settingsService.GetSnapshot());
        var holdSeconds = BigPictureVisibleWindowsShellHandOffDelaySeconds + WindowsShellStartDelaySeconds;

        if (holdSeconds <= 0)
        {
            return;
        }

        ServiceStateText = "Steam is ready";
        ServiceDetailText = $"Big Picture is visible. Starting the Windows desktop in {holdSeconds}s.";
        await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
    }

    private void ApplyShellBootstrapStatus(SteamLoaderHostStatus? status)
    {
        ErrorText = string.Empty;
        ApiStateText = _processManager.ApiBaseUri.ToString();

        if (status is null)
        {
            ServiceStateText = "Preparing Tools for Steam";
            ServiceDetailText = "Starting the background service and preparing the fast Steam hand-off.";
            SteamStateText = "Startup sync runs first, then Steam opens as soon as shortcuts are ready.";
            return;
        }

        ApiStateText = $"{_processManager.ApiBaseUri} ({FormatElapsed(status.StartedAtUtc)})";
        var bigPictureVisible = IsSteamBigPictureWindowVisible();

        if (bigPictureVisible)
        {
            ServiceStateText = "Steam is ready";
            ServiceDetailText = status.QuickAccessAttached || status.SharedContextAttached
                ? "Gamepad UI is live. Finishing the startup hand-off."
                : "Big Picture is visible. Confirming the startup hand-off before Windows starts.";
            SteamStateText = status.QuickAccessAttached
                ? "Quick Access is attached."
                : "Big Picture is visible.";
            return;
        }

        if (status.QuickAccessAttached)
        {
            ServiceStateText = "Steam is opening";
            ServiceDetailText = "Quick Access is attached. Waiting for the Big Picture window to appear.";
            SteamStateText = "Quick Access is attached.";
            return;
        }

        if (status.SharedContextAttached)
        {
            ServiceStateText = "Steam is starting";
            ServiceDetailText = "Steam is up and the shared UI context is ready.";
            SteamStateText = "Waiting for the final Gamepad UI surfaces.";
            return;
        }

        ServiceStateText = "Launching Steam";
        ServiceDetailText = "Writing shortcuts first, then handing off to Steam Gamepad UI.";
        SteamStateText = "Waiting for Steam to finish booting.";
    }

    private static string BuildAutostartStateText(string? startupMode)
    {
        return startupMode switch
        {
            SteamLoaderRuntime.StartupModeShell =>
                "Shell takeover is active. Tools for Steam starts before Explorer, syncs launchers, starts Steam in dev mode, and then hands the session back to Windows Explorer.",
            SteamLoaderRuntime.StartupModeTray =>
                "Tray app mode is active. Windows starts normally, then Tools for Steam runs from the tray, syncs launchers, and starts Steam in dev mode.",
            _ => "Shell takeover is active. Tools for Steam starts before Explorer, syncs launchers, starts Steam in dev mode, and then hands the session back to Windows Explorer."
        };
    }

    private async Task LoadSplashGameCoversAsync()
    {
        var steamRoot = _steamInstallationService.ResolveSteamRootPath();

        // Step 1: collect paths (fast I/O scan)
        var (paths, debugText) = await Task.Run(() => CollectSteamGameCoverPaths(steamRoot)).ConfigureAwait(false);

        // Step 2: decode thumbnails at 160 px width on the background thread.
        // BitmapImage with OnLoad + Freeze() is safe to create outside the UI thread.
        var thumbnails = await Task.Run(() => CreateThumbnails(paths)).ConfigureAwait(false);

        await _dispatcher.InvokeAsync(() =>
        {
            SplashGameCovers = thumbnails;
            SplashDebugText = $"{debugText} | loaded: {thumbnails.Count}";
        });
    }

    // Decode each image once at the exact cell size (1920/12 = 160 px) so WPF
    // receives frozen BitmapSources instead of loading full-res JPEGs on the UI thread.
    private static IReadOnlyList<BitmapSource> CreateThumbnails(IReadOnlyList<string> paths)
    {
        const int cellWidth = 160; // 1920 px ÷ 12 columns
        var results = new List<BitmapSource>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = cellWidth;
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // load fully before EndInit returns
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze(); // makes it cross-thread safe for WPF binding
                results.Add(bmp);
            }
            catch
            {
                // skip unreadable files silently
            }
        }
        return results;
    }

    private static (IReadOnlyList<string> Paths, string Debug) CollectSteamGameCoverPaths(string? steamRoot)
    {
        try
        {
            if (string.IsNullOrEmpty(steamRoot))
                return ([], "No Steam path found");

            // Primary source: userdata/<steamid>/config/grid
            // Portrait covers end with 'p' before the extension: e.g. 730p.jpg
            var gridDir = FindSteamGridDir(steamRoot);
            List<string> covers = [];
            string debugInfo;

            if (gridDir != null)
            {
                var portraitCovers = Directory.EnumerateFiles(gridDir, "*p.jpg")
                    .Concat(Directory.EnumerateFiles(gridDir, "*p.png"))
                    .Where(f =>
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        // Must end with 'p' and have only digits before it: e.g. "730p"
                        return name.Length >= 2 && name[^1] == 'p' &&
                               name[..^1].All(char.IsDigit);
                    })
                    .ToList();

                if (portraitCovers.Count >= 5)
                {
                    covers = portraitCovers;
                    debugInfo = $"grid: {gridDir} | portrait: {portraitCovers.Count}";
                }
                else
                {
                    // Not enough portrait covers — use all non-logo, non-hero images from grid
                    var allGrid = Directory.EnumerateFiles(gridDir, "*.jpg")
                        .Concat(Directory.EnumerateFiles(gridDir, "*.png"))
                        .Where(f =>
                        {
                            var name = Path.GetFileNameWithoutExtension(f);
                            return !name.EndsWith("_hero", StringComparison.OrdinalIgnoreCase) &&
                                   !name.EndsWith("_logo", StringComparison.OrdinalIgnoreCase) &&
                                   !name.EndsWith("_icon", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                    covers = allGrid;
                    debugInfo = $"grid: {gridDir} | portrait: {portraitCovers.Count} | all: {allGrid.Count}";
                }
            }
            else
            {
                // Fallback: appcache/librarycache (old Steam client behaviour)
                var cacheDir = Path.Combine(steamRoot, "appcache", "librarycache");
                if (Directory.Exists(cacheDir))
                {
                    covers = Directory.EnumerateFiles(cacheDir, "*_library_600x900.jpg")
                        .Concat(Directory.EnumerateFiles(cacheDir, "*_library_600x900.png"))
                        .ToList();

                    if (covers.Count < 5)
                    {
                        covers = Directory.EnumerateFiles(cacheDir, "*.jpg")
                            .Concat(Directory.EnumerateFiles(cacheDir, "*.png"))
                            .Where(f => !f.EndsWith("_logo.png", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                }

                debugInfo = $"librarycache: {covers.Count} files (no grid dir found)";
            }

            if (covers.Count == 0)
                return ([], debugInfo);

            // Shuffle so each launch shows a different arrangement.
            var rng = new Random();
            covers = [.. covers.OrderBy(_ => rng.Next())];

            // Fill a 12×7 UniformGrid — tile if fewer than 84 images.
            const int targetCount = 84;
            if (covers.Count < targetCount)
            {
                var repeated = new List<string>(targetCount);
                while (repeated.Count < targetCount)
                    repeated.AddRange(covers);
                covers = repeated;
            }

            return (covers.Take(targetCount).ToList(), debugInfo);
        }
        catch (Exception ex)
        {
            return ([], $"Error: {ex.Message}");
        }
    }

    private static string? FindSteamGridDir(string steamRoot)
    {
        var userdataDir = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdataDir))
            return null;

        // Find grid folder across all Steam user IDs; prefer the one with most files
        return Directory.EnumerateDirectories(userdataDir)
            .Select(d => Path.Combine(d, "config", "grid"))
            .Where(Directory.Exists)
            .OrderByDescending(d =>
            {
                try { return Directory.EnumerateFiles(d).Count(); }
                catch { return 0; }
            })
            .FirstOrDefault();
    }

    private void ApplyGeneralSettingsSnapshot(SteamLoaderGeneralSettingsSnapshot settings)
    {
        var splashScreen = settings.SplashScreen;
        ShowStartupSplashText = splashScreen.ShowText;
        SplashWallpaperPath = splashScreen.WallpaperExists ? splashScreen.WallpaperPath : string.Empty;
        SplashIconPath = splashScreen.IconExists ? splashScreen.IconPath : string.Empty;
        WindowsShellStartDelaySeconds = settings.WindowsShellStartDelaySeconds;
    }

    private sealed record ShellHandOffReadiness(
        bool BigPictureVisible,
        bool SteamSignalReady,
        bool WindowsShellHandOffReady);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
