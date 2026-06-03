using System.Diagnostics;
using SteamLoader.App;
using SteamLoader.App.Hosting;
using SteamLoader.App.Services;

namespace SteamLoader.App.UI;

public sealed class MainWindowViewModel : BindableBase
{
    private readonly SteamLoaderProcessManager _processManager;
    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly string _shellLaunchArguments;
    private readonly bool _shellBootstrapMode;
    private readonly bool _runStartupSyncOnInitialize;
    private bool _isBusy;
    private bool _isRunning;
    private bool _autostartEnabled;
    private bool _initialized;
    private bool _startupSyncTriggered;
    private bool _showStartupSplash;
    private bool _windowsShellStarted;
    private Task? _shellBootstrapMonitorTask;
    private SteamLoaderHostStatus? _lastKnownStatus;
    private string _serviceStateText = "Checking background host...";
    private string _serviceDetailText = "The manager is reading the current runtime status.";
    private string _steamStateText = "Waiting for status...";
    private string _apiStateText = "Waiting for status...";
    private string _autostartStateText = "Checking startup registration...";
    private string _errorText = string.Empty;

    public MainWindowViewModel(
        SteamLoaderProcessManager processManager,
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        string shellLaunchArguments,
        bool shellBootstrapMode,
        bool runStartupSyncOnInitialize)
    {
        _processManager = processManager;
        _autostartService = autostartService;
        _shellService = shellService;
        _shellLaunchArguments = shellLaunchArguments;
        _shellBootstrapMode = shellBootstrapMode;
        _runStartupSyncOnInitialize = runStartupSyncOnInitialize;
        _showStartupSplash = shellBootstrapMode;
        if (shellBootstrapMode)
        {
            _serviceStateText = "Preparing Steam Tools";
            _serviceDetailText = "Starting the background service.";
            _steamStateText = "Waiting to begin the Steam startup flow.";
        }

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy && IsRunning);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => !IsBusy && IsRunning);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ToggleAutostartCommand = new RelayCommand(ToggleAutostart, () => !IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public string InstallPath => _processManager.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public string WindowTitle => "Steam Tools";

    public string Subtitle => "Portable tray shell and control panel for the Windows Quick Access toolkit.";

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

    public string StatusPillText => IsRunning ? "Running" : "Stopped";

    public string AutostartButtonText => AutostartEnabled ? "Disable Autostart" : "Enable Autostart";

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ToggleAutostartCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

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
            _lastKnownStatus = status;
            IsRunning = status is not null;

            if (_shellBootstrapMode && !_windowsShellStarted)
            {
                ApplyShellBootstrapStatus(status);
            }
            else if (status is null)
            {
                ServiceStateText = "Background host is offline.";
                ServiceDetailText = "Start the host to inject Steam Tools into Steam Quick Access.";
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

            AutostartEnabled = _shellService.IsEnabled(_processManager.ExecutablePath, _shellLaunchArguments);
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

            _autostartService.SetEnabled(_processManager.ExecutablePath, SteamLoaderRuntime.AutostartArguments, false);
            _shellService.SetEnabled(_processManager.ExecutablePath, _shellLaunchArguments, nextState);
            AutostartEnabled = nextState;
            AutostartStateText = BuildAutostartStateText(nextState);
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

    public void ToggleAutostartSetting()
    {
        ToggleAutostart();
    }

    public void OpenInstallFolder()
    {
        OpenFolder();
    }

    private async Task TriggerStartupSyncAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        ServiceDetailText = "Running startup sync before launching Steam...";

        try
        {
            await _processManager.RequestStartupSyncAsync();
            ServiceDetailText = "Startup sync requested. Steam Tools will finish the sync and launch Steam.";
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
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (SteamReadyForShellHandOff())
            {
                break;
            }

            await Task.Delay(900);
            await RefreshAsync();
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
    }

    private void ApplyShellBootstrapStatus(SteamLoaderHostStatus? status)
    {
        ErrorText = string.Empty;
        ApiStateText = _processManager.ApiBaseUri.ToString();

        if (status is null)
        {
            ServiceStateText = "Preparing Steam Tools";
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
            ? "Steam Tools takes over the sign-in shell, syncs your launchers, starts Steam in dev mode, and then hands the session back to Windows Explorer."
            : "Steam Tools only starts when you launch it manually.";
    }
}
