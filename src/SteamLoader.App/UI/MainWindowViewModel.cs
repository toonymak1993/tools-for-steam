using System.Diagnostics;
using System.Runtime.InteropServices;
using SteamLoader.App;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.UI;

public sealed class MainWindowViewModel : BindableBase
{
    private const int BigPictureReadySplashHoldSeconds = 3;
    private const int ShowWindowRestore = 9;

    private readonly SteamLoaderProcessManager _processManager;
    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly SteamLoaderSettingsService _settingsService;
    private readonly ReleaseUpdateService _releaseUpdateService;
    private readonly SupportBundleService _supportBundleService;
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
    private bool _splashScreenEnabled = true;
    private bool _showStartupSplashText = true;
    private string _splashWallpaperPath = string.Empty;
    private string _splashIconPath = string.Empty;
    private int _splashExtraCloseDelaySeconds;
    private UpdateCheckSnapshot? _updateSnapshot;

    public MainWindowViewModel(
        SteamLoaderProcessManager processManager,
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        SteamLoaderSettingsService settingsService,
        ReleaseUpdateService releaseUpdateService,
        SupportBundleService supportBundleService,
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
        _shellLaunchArguments = shellLaunchArguments;
        _shellBootstrapMode = shellBootstrapMode;
        _consoleStartupMode = consoleStartupMode;
        _runStartupSyncOnInitialize = runStartupSyncOnInitialize;
        ApplySplashScreenSettings(_settingsService.GetSplashScreenSettings());
        _showStartupSplash = consoleStartupMode && _splashScreenEnabled;
        if (consoleStartupMode)
        {
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

    public int SplashExtraCloseDelaySeconds
    {
        get => _splashExtraCloseDelaySeconds;
        private set => SetProperty(ref _splashExtraCloseDelaySeconds, value);
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
        ApplySplashScreenSettings(_settingsService.GetSplashScreenSettings());
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
            ApplySplashScreenSettings(settings.SplashScreen);
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
            ServiceDetailText = "Startup sync requested. Steam will open as soon as shortcuts are ready.";
            if (_consoleStartupMode)
            {
                EnsureShellBootstrapMonitor();
            }
        }
        catch (Exception exception)
        {
            CompleteShellBootstrap();
            ErrorText = exception.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
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

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (SteamReadyForShellHandOff())
            {
                break;
            }

            await Task.Delay(900);
            await RefreshAsync();
        }

        if (!SteamReadyForShellHandOff())
        {
            ServiceStateText = "Steam needs more time";
            ServiceDetailText = _shellBootstrapMode
                ? "Starting Windows Desktop recovery while Tools for Steam keeps trying in the tray."
                : "Keeping Tools for Steam alive while Steam continues to start.";
            SteamStateText = "Steam was not ready before the console-mode timeout.";
        }

        await HoldSplashBeforeShellHandoffAsync();
        CompleteShellBootstrap();
    }

    private bool SteamReadyForShellHandOff()
    {
        return IsSteamBigPictureWindowVisible();
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

        ShowStartupSplash = false;
        ShowFirstRunSetup = false;
    }

    private async Task HoldSplashBeforeShellHandoffAsync()
    {
        ApplySplashScreenSettings(_settingsService.GetSplashScreenSettings());
        var holdSeconds = _consoleStartupMode
            ? Math.Max(BigPictureReadySplashHoldSeconds, SplashExtraCloseDelaySeconds)
            : SplashExtraCloseDelaySeconds;

        if (!ShowStartupSplash || holdSeconds <= 0)
        {
            return;
        }

        ServiceStateText = "Steam is ready";
        ServiceDetailText = $"Big Picture is visible. Closing the splash screen in {holdSeconds}s.";
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

        if (status.QuickAccessAttached)
        {
            ServiceStateText = IsSteamBigPictureWindowVisible()
                ? "Steam is ready"
                : "Steam is opening";
            ServiceDetailText = IsSteamBigPictureWindowVisible()
                ? "Gamepad UI is live. Finishing the startup hand-off."
                : "Quick Access is attached. Waiting for the Big Picture window to appear.";
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

    private void ApplySplashScreenSettings(SteamLoaderSplashScreenSettingsSnapshot settings)
    {
        _splashScreenEnabled = settings.Enabled;
        ShowStartupSplashText = settings.ShowText;
        SplashWallpaperPath = settings.WallpaperExists ? settings.WallpaperPath : string.Empty;
        SplashIconPath = settings.IconExists ? settings.IconPath : string.Empty;
        SplashExtraCloseDelaySeconds = settings.ExtraCloseDelaySeconds;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
