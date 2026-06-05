using System.Diagnostics;
using SteamLoader.App;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.UI;

public sealed class MainWindowViewModel : BindableBase
{
    private readonly SteamLoaderProcessManager _processManager;
    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly SteamLoaderSettingsService _settingsService;
    private readonly ReleaseUpdateService _releaseUpdateService;
    private readonly SupportBundleService _supportBundleService;
    private readonly string _shellLaunchArguments;
    private readonly bool _shellBootstrapMode;
    private readonly bool _runStartupSyncOnInitialize;
    private bool _isBusy;
    private bool _isRunning;
    private bool _autostartEnabled;
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
        _runStartupSyncOnInitialize = runStartupSyncOnInitialize;
        _showStartupSplash = shellBootstrapMode;
        if (shellBootstrapMode)
        {
            _serviceStateText = "Preparing Tools for Steam";
            _serviceDetailText = "Starting the background service.";
            _steamStateText = "Waiting to begin the Steam startup flow.";
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
            }
        }
    }

    public bool ShowStartupSplash
    {
        get => _showStartupSplash;
        private set => SetProperty(ref _showStartupSplash, value);
    }

    public bool ShowFirstRunSetup
    {
        get => _showFirstRunSetup;
        private set => SetProperty(ref _showFirstRunSetup, value);
    }

    public string StatusPillText => IsRunning ? "Running" : "Stopped";

    public string AutostartButtonText => AutostartEnabled ? "Disable Autostart" : "Enable Autostart";

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

        if (_shellBootstrapMode)
        {
            EnsureShellBootstrapMonitor();
        }
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
            _lastKnownStatus = status;
            IsRunning = status is not null;
            ShowFirstRunSetup = false;

            if (_shellBootstrapMode && !_windowsShellStarted)
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

            AutostartEnabled = settings.RunOnWindowsSignIn;
            AutostartStateText = BuildAutostartStateText(AutostartEnabled);
        }
        catch (Exception exception)
        {
            if (_shellBootstrapMode && !_windowsShellStarted)
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
            var nextState = !AutostartEnabled;
            if (nextState)
            {
                _autostartService.DisableSteamAutostartEntries();
            }

            var settings = _settingsService.SetRunOnWindowsSignIn(nextState);
            AutostartEnabled = settings.RunOnWindowsSignIn;
            AutostartStateText = BuildAutostartStateText(settings.RunOnWindowsSignIn);
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
        ServiceDetailText = "Running startup sync before launching Steam...";

        try
        {
            await _processManager.RequestStartupSyncAsync();
            ServiceDetailText = "Startup sync requested. Tools for Steam will finish the sync and launch Steam.";
            if (_shellBootstrapMode)
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
        if (!_shellBootstrapMode || _shellBootstrapMonitorTask is not null)
        {
            return;
        }

        _shellBootstrapMonitorTask = MonitorShellBootstrapAsync();
    }

    private async Task MonitorShellBootstrapAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);

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
            ServiceDetailText = "Starting Windows Desktop recovery while Tools for Steam keeps trying in the tray.";
            SteamStateText = "Steam was not ready before the console-mode timeout.";
        }

        CompleteShellBootstrap();
    }

    private bool SteamReadyForShellHandOff()
    {
        return _lastKnownStatus?.QuickAccessAttached == true
            || _lastKnownStatus?.SharedContextAttached == true;
    }

    private void CompleteShellBootstrap()
    {
        if (_windowsShellStarted)
        {
            return;
        }

        _windowsShellStarted = true;
        _shellService.StartWindowsShellIfNeeded();
        ShowStartupSplash = false;
        ShowFirstRunSetup = false;
    }

    private void ApplyShellBootstrapStatus(SteamLoaderHostStatus? status)
    {
        ErrorText = string.Empty;
        ApiStateText = _processManager.ApiBaseUri.ToString();

        if (status is null)
        {
            ServiceStateText = "Preparing Tools for Steam";
            ServiceDetailText = "Starting the background service.";
            SteamStateText = "Waiting to begin the Steam startup flow.";
            return;
        }

        ApiStateText = $"{_processManager.ApiBaseUri} ({FormatElapsed(status.StartedAtUtc)})";

        if (status.QuickAccessAttached)
        {
            ServiceStateText = "Steam is ready";
            ServiceDetailText = "Gamepad UI is live. Finishing the Windows hand-off.";
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
        ServiceDetailText = "Syncing launchers and starting Steam in Gamepad UI.";
        SteamStateText = "Waiting for Steam to finish booting.";
    }

    private static string BuildAutostartStateText(bool enabled)
    {
        return enabled
            ? "Tools for Steam takes over the sign-in shell, syncs your launchers, starts Steam in dev mode, and then hands the session back to Windows Explorer."
            : "Tools for Steam only starts when you launch it manually.";
    }
}
