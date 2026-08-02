using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamLoader.App;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using ToolsForSteam.Splash;

namespace SteamLoader.App.UI;

public sealed class MainWindowViewModel : BindableBase
{
    private const int ShowWindowRestore = 9;
    private static readonly TimeSpan StartupFallbackObservationInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupActionControllerPollInterval = TimeSpan.FromMilliseconds(150);

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
    private readonly SteamStartupEnvironmentProbe _steamEnvironmentProbe;
    private readonly SteamStartupTimingPolicy _startupTimingPolicy;
    private readonly SteamSplashStartupCoordinator? _splashStartupCoordinator;
    private readonly DateTimeOffset _startupTimelineStartedAt = DateTimeOffset.UtcNow;
    private bool _isBusy;
    private bool _isRunning;
    private bool _autostartEnabled;
    private string _startupMode = SteamLoaderRuntime.StartupModeShell;
    private bool _initialized;
    private bool _showStartupSplash;
    private bool _showFirstRunSetup;
    private bool _windowsShellStarted;
    private Task? _shellBootstrapMonitorTask;
    private SteamLoaderHostStatus? _lastKnownStatus;
    private string _lastStartupTimelineState = string.Empty;
    private ushort _previousSplashControllerButtons;
    private bool _manualSplashRecoveryUsed;
    private bool _showSplashRecoveryActions;
    private bool _canRestartSteamFromSplash;
    private string _serviceStateText = "Checking background host...";
    private string _serviceDetailText = "Tools for Steam is reading the current runtime status.";
    private string _steamStateText = "Waiting for status...";
    private string _apiStateText = "Waiting for status...";
    private string _autostartStateText = "Checking startup registration...";
    private string _setupChecklistText = "Setup checks have not run yet.";
    private string _recoveryHintText = "If Steam does not appear, start the Windows desktop and relaunch Tools for Steam.";
    private string _updateStateText = "Updates have not been checked yet.";
    private string _supportBundleText = "No support bundle has been exported yet.";
    private string _errorText = string.Empty;
    private double _splashOverlayOpacity = 1.0;
    private string _splashCustomImagePath = string.Empty;
    private IReadOnlyList<BitmapSource> _splashGameCovers = [];
    private string _splashDebugText = string.Empty;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private Task _splashCoversTask = Task.CompletedTask;
    private bool _splashArtworkReleased;
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
        _steamEnvironmentProbe = new SteamStartupEnvironmentProbe(
            steamInstallationService.ResolveSteamRootPath());
        var isHandheld = HandheldDeviceCatalog.IsSupported(HandheldDeviceCatalog.Detect());
        _startupTimingPolicy = new SteamStartupHistoryStore().GetTimingPolicy(isHandheld);
        _splashStartupCoordinator = consoleStartupMode
            ? new SteamSplashStartupCoordinator(_startupTimingPolicy, DateTimeOffset.UtcNow)
            : null;
        ApplyGeneralSettingsSnapshot(_settingsService.GetSnapshot());
        _showStartupSplash = consoleStartupMode;
        if (consoleStartupMode)
        {
            _splashCoversTask = string.IsNullOrWhiteSpace(SplashCustomImagePath)
                ? LoadSplashGameCoversAsync()
                : Task.CompletedTask;
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
        ContinueWaitingFromSplashCommand = new RelayCommand(ContinueWaitingFromSplash);
        RestartSteamFromSplashCommand = new AsyncRelayCommand(
            RestartSteamFromSplashAsync,
            () => CanRestartSteamFromSplash && !_manualSplashRecoveryUsed);
        OpenDesktopFromSplashCommand = new RelayCommand(OpenDesktopFromSplash);
    }

    public string InstallPath => _processManager.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public string WindowTitle => "Tools for Steam";

    public string Subtitle => "Installed console runtime and Quick Access bridge for Windows.";

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

    public double SplashOverlayOpacity
    {
        get => _splashOverlayOpacity;
        private set => SetProperty(ref _splashOverlayOpacity, value);
    }

    public string SplashCustomImagePath
    {
        get => _splashCustomImagePath;
        private set => SetProperty(ref _splashCustomImagePath, value);
    }

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

    public void ReleaseSplashArtwork()
    {
        if (_splashArtworkReleased)
        {
            return;
        }

        _splashArtworkReleased = true;
        _splashCoversTask = Task.CompletedTask;
        SplashGameCovers = [];
        SplashCustomImagePath = string.Empty;
        SplashDebugText = string.Empty;
    }

    public string SplashDebugText
    {
        get => _splashDebugText;
        private set => SetProperty(ref _splashDebugText, value);
    }

    public bool ShowSplashRecoveryActions
    {
        get => _showSplashRecoveryActions;
        private set => SetProperty(ref _showSplashRecoveryActions, value);
    }

    public bool CanRestartSteamFromSplash
    {
        get => _canRestartSteamFromSplash;
        private set
        {
            if (SetProperty(ref _canRestartSteamFromSplash, value))
            {
                RestartSteamFromSplashCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SplashHeadlineText => IsGermanUi
        ? "Steam wird vorbereitet"
        : "Preparing Steam";

    public string SplashContinueLabel => IsGermanUi ? "A  Weiter warten" : "A  Keep waiting";

    public string SplashRestartLabel => IsGermanUi ? "X  Steam neu starten" : "X  Restart Steam";

    public string SplashDesktopLabel => IsGermanUi ? "Y  Desktop öffnen" : "Y  Open desktop";

    public string SplashRecoveryHeadline => IsGermanUi
        ? "Steam braucht länger als gewöhnlich"
        : "Steam is taking longer than usual";

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
        SteamLoaderRuntime.StartupModeTray => "Startup Mode: eTray",
        SteamLoaderRuntime.StartupModeXbox => "Startup Mode: Xbox Mode",
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

    public RelayCommand ContinueWaitingFromSplashCommand { get; }

    public AsyncRelayCommand RestartSteamFromSplashCommand { get; }

    public RelayCommand OpenDesktopFromSplashCommand { get; }

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

        // Startup sync intentionally disabled: the background Store Sync
        // automation keeps the library in sync during runtime, so there is no
        // need to run - and wait on - a sync at boot. This keeps console-mode
        // startup fast (Steam is already launched first).
        _ = _runStartupSyncOnInitialize;

        EnsureShellBootstrapMonitor();
    }

    public void StartSplashPreview(TimeSpan duration)
    {
        _splashArtworkReleased = false;
        ApplyGeneralSettingsSnapshot(_settingsService.GetSnapshot());
        _splashCoversTask = string.IsNullOrWhiteSpace(SplashCustomImagePath)
            ? LoadSplashGameCoversAsync()
            : Task.CompletedTask;
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

        EnsureShellBootstrapMonitor();
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
        RestartSteamFromSplashCommand.RaiseCanExecuteChanged();
    }

    private static string BuildSetupChecklistText(SteamLoaderHostStatus? status)
    {
        if (status is null)
        {
            return "Host offline - Tools for Steam can start it from the tray app.";
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
        if (!SteamLoaderRuntime.ShouldStartShellHandOffMonitor(
                _shellBootstrapMode,
                _consoleStartupMode) ||
            _shellBootstrapMonitorTask is not null)
        {
            return;
        }

        _shellBootstrapMonitorTask = MonitorShellBootstrapAsync();
    }

    private async Task MonitorShellBootstrapAsync()
    {
        try
        {
            await MonitorShellBootstrapCoreAsync();
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write(
                $"startup splash monitor failed safely: {exception.GetType().Name}: {exception.Message}");
            ErrorText = exception.Message;
            ServiceStateText = IsGermanUi
                ? "Steam-Start konnte nicht weiter geprüft werden"
                : "Steam startup could not be monitored further";
            ServiceDetailText = IsGermanUi
                ? "Der Windows-Desktop wird sicher gestartet. Steam wird nicht automatisch beendet."
                : "The Windows desktop will open safely. Steam will not be terminated automatically.";
            CompleteStartupSplashHandOff(steamReady: false);
        }
    }

    private async Task MonitorShellBootstrapCoreAsync()
    {
        if (_splashStartupCoordinator is null)
        {
            CompleteShellBootstrap();
            return;
        }

        using var signalWatcher = new SteamStartupSignalWatcher();
        while (!_windowsShellStarted)
        {
            var status = await _processManager.GetStatusAsync();
            var runtime = _steamEnvironmentProbe.Capture();
            _lastKnownStatus = status;
            ApplyShellBootstrapStatus(status, runtime);

            var stage = status?.SteamStartupStage ?? SteamClientStartupStage.Starting;
            var uiState = status?.SteamUiState ?? SteamUiState.Starting;
            var decision = _splashStartupCoordinator.Observe(
                DateTimeOffset.UtcNow,
                runtime,
                stage,
                uiState,
                hostAvailable: status is not null,
                hostSteamSignalReady: status?.QuickAccessAttached == true ||
                    status?.SharedContextAttached == true);
            ApplySplashDecision(decision, status, runtime, uiState);

            switch (decision.Action)
            {
                case SteamSplashNextAction.FocusSteam:
                    TryFocusSteamWindow(runtime.Windows.PreferredWindowHandle);
                    break;

                case SteamSplashNextAction.CompleteWithSteam:
                    SteamStartupDiagnostics.Write("splash hand-off confirmed from stable Steam UI and foreground signals");
                    if (!await WaitBeforeWindowsShellHandoffAsync())
                    {
                        break;
                    }

                    CompleteStartupSplashHandOff(steamReady: true);
                    return;

                case SteamSplashNextAction.CompleteWithDesktop:
                    SteamStartupDiagnostics.Write($"splash hand-off selected safe desktop fallback: {decision.Detail}");
                    CompleteStartupSplashHandOff(steamReady: false);
                    return;
            }

            await WaitForStartupSignalOrControllerAsync(signalWatcher);
        }
    }

    private void ApplySplashDecision(
        SteamSplashDecision decision,
        SteamLoaderHostStatus? status,
        SteamRuntimeObservation runtime,
        SteamUiState uiState)
    {
        ShowSplashRecoveryActions = decision.ShowRecoveryActions;
        CanRestartSteamFromSplash = decision.CanRestartSteam && !_manualSplashRecoveryUsed;

        ServiceStateText = status?.SteamStartupStage switch
        {
            SteamClientStartupStage.Updating => IsGermanUi ? "Steam wird aktualisiert" : "Steam is updating",
            SteamClientStartupStage.Protected => IsGermanUi ? "Steam-Aktivität geschützt" : "Steam activity protected",
            SteamClientStartupStage.Recovering => IsGermanUi ? "Steam wird einmal neu gestartet" : "Restarting Steam once",
            SteamClientStartupStage.Failed => IsGermanUi ? "Steam benötigt Aufmerksamkeit" : "Steam needs attention",
            SteamClientStartupStage.Ready => IsGermanUi ? "Steam ist bereit" : "Steam is ready",
            _ => IsGermanUi ? "Steam wird gestartet" : "Starting Steam"
        };
        ServiceDetailText = decision.ShowRecoveryActions
            ? decision.Detail
            : status?.ServiceMessage ?? decision.Detail;
        SteamStateText = BuildSplashRuntimeState(runtime, uiState, decision.Detail);

        var transitionState = string.Join(
            ':',
            status?.SteamStartupStage ?? SteamClientStartupStage.Starting,
            uiState,
            runtime.SteamRunning,
            runtime.WebHelperRunning,
            runtime.Windows.HasVisibleSteamWindow,
            runtime.Windows.IsSteamForeground,
            decision.Action);
        if (!string.Equals(transitionState, _lastStartupTimelineState, StringComparison.Ordinal))
        {
            _lastStartupTimelineState = transitionState;
            var elapsed = DateTimeOffset.UtcNow - _startupTimelineStartedAt;
            SteamStartupDiagnostics.Write(
                $"timeline +{elapsed.TotalSeconds:F1}s state={transitionState} detail={decision.Detail}");
        }
    }

    private static string BuildSplashRuntimeState(
        SteamRuntimeObservation runtime,
        SteamUiState uiState,
        string fallback)
    {
        if (uiState == SteamUiState.Login)
        {
            return IsGermanUi
                ? "Steam wartet auf eine Anmeldung. Öffne bei Bedarf den Desktop."
                : "Steam is waiting for sign-in. Open the desktop if needed.";
        }

        if (uiState == SteamUiState.Offline)
        {
            return IsGermanUi
                ? "Steam zeigt einen Offline- oder Netzwerkdialog."
                : "Steam is showing an offline or network dialog.";
        }

        if (uiState == SteamUiState.Error || runtime.ErrorReporterRunning)
        {
            return IsGermanUi
                ? "Steam hat einen Fehler gemeldet; automatische Neustarts bleiben begrenzt."
                : "Steam reported an error; automatic restarts remain limited.";
        }

        if (runtime.UpdateInProgress || uiState == SteamUiState.Updating)
        {
            return IsGermanUi
                ? "Ein Steam-Update läuft; ein automatischer Neustart ist gesperrt."
                : "A Steam update is active; automatic restart is blocked.";
        }

        if (runtime.GameOrOverlayRunning)
        {
            return IsGermanUi
                ? "Ein laufendes Spiel wird nicht durch die Start-Recovery beendet."
                : "A running game is protected from startup recovery.";
        }

        if (!runtime.SteamRunning)
        {
            return IsGermanUi ? "Warte auf steam.exe." : "Waiting for steam.exe.";
        }

        if (!runtime.WebHelperRunning)
        {
            return IsGermanUi ? "Steam läuft; die Web-Oberfläche startet." : "Steam is running; its web UI is starting.";
        }

        if (!runtime.Windows.HasVisibleSteamWindow)
        {
            return IsGermanUi ? "Steam-Oberfläche wird aufgebaut." : "Steam UI is being created.";
        }

        if (!runtime.Windows.IsSteamForeground)
        {
            return IsGermanUi ? "Steam wurde erkannt und wird fokussiert." : "Steam was detected and is being focused.";
        }

        return fallback;
    }

    private async Task WaitForStartupSignalOrControllerAsync(SteamStartupSignalWatcher signalWatcher)
    {
        if (!ShowSplashRecoveryActions)
        {
            await signalWatcher.WaitForSignalAsync(
                StartupFallbackObservationInterval,
                CancellationToken.None);
            return;
        }

        var fallbackAt = DateTimeOffset.UtcNow + StartupFallbackObservationInterval;
        while (!_windowsShellStarted && DateTimeOffset.UtcNow < fallbackAt)
        {
            if (await HandleSplashControllerInputAsync())
            {
                return;
            }

            if (await signalWatcher.WaitForSignalAsync(
                    StartupActionControllerPollInterval,
                    CancellationToken.None))
            {
                return;
            }
        }
    }

    private async Task<bool> HandleSplashControllerInputAsync()
    {
        var mask = ControllerShortcutService.ReadConnectedControllerButtonMasks()
            .Aggregate((ushort)0, (combined, current) => (ushort)(combined | current));
        var newlyPressed = (ushort)(mask & ~_previousSplashControllerButtons);
        _previousSplashControllerButtons = mask;
        if (!ShowSplashRecoveryActions || newlyPressed == 0)
        {
            return false;
        }

        if ((newlyPressed & 0x8000) != 0)
        {
            OpenDesktopFromSplash();
            return true;
        }
        else if ((newlyPressed & 0x4000) != 0 && CanRestartSteamFromSplash)
        {
            await RestartSteamFromSplashAsync();
            return true;
        }
        else if ((newlyPressed & 0x1000) != 0)
        {
            ContinueWaitingFromSplash();
            return true;
        }

        return false;
    }

    private void ContinueWaitingFromSplash()
    {
        _splashStartupCoordinator?.ExtendWait(DateTimeOffset.UtcNow);
        ShowSplashRecoveryActions = false;
        ServiceDetailText = IsGermanUi
            ? "Die Wartezeit wurde um fünf Minuten verlängert."
            : "Waiting was extended by five minutes.";
        SteamStartupDiagnostics.Write("user extended Steam splash wait by five minutes");
    }

    private async Task RestartSteamFromSplashAsync()
    {
        if (_manualSplashRecoveryUsed || !CanRestartSteamFromSplash)
        {
            return;
        }

        _manualSplashRecoveryUsed = true;
        CanRestartSteamFromSplash = false;
        _splashStartupCoordinator?.ExtendWait(DateTimeOffset.UtcNow);
        ServiceStateText = IsGermanUi ? "Steam-Reparatur wird gestartet" : "Starting Steam repair";
        ServiceDetailText = IsGermanUi
            ? "Steam wird höchstens einmal kontrolliert neu gestartet."
            : "Steam will be restarted at most once in a controlled repair.";
        SteamStartupDiagnostics.Write("user requested the one splash Steam repair");

        var repaired = await _processManager.RequestSteamStartupRepairAsync();
        if (!repaired)
        {
            ShowSplashRecoveryActions = true;
            ServiceStateText = IsGermanUi ? "Steam-Reparatur wurde nicht ausgeführt" : "Steam repair was not performed";
            ServiceDetailText = IsGermanUi
                ? "Ein Update, Download oder Spiel kann den Neustart geschützt haben."
                : "An update, download, or running game may have protected Steam from restart.";
        }
    }

    private void OpenDesktopFromSplash()
    {
        SteamStartupDiagnostics.Write("user opened the Windows desktop from the startup splash");
        CompleteStartupSplashHandOff(steamReady: false);
    }

    private static bool TryFocusSteamWindow(IntPtr preferredHandle = default)
    {
        var handle = preferredHandle != IntPtr.Zero
            ? preferredHandle
            : SteamBigPictureForegroundDetector.Capture().PreferredWindowHandle;
        if (handle == IntPtr.Zero)
        {
            handle = FindSteamWindowHandle();
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var steamProcessId);
        if (steamProcessId != 0)
        {
            AllowSetForegroundWindow(steamProcessId);
        }

        ShowWindow(handle, ShowWindowRestore);
        return SetForegroundWindow(handle);
    }

    private static bool IsSteamBigPictureWindowVisible()
    {
        var snapshot = SteamBigPictureForegroundDetector.Capture();
        return snapshot.HasVisibleSteamWindow && snapshot.HasLikelyGamepadWindow;
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

    private async Task<bool> WaitBeforeWindowsShellHandoffAsync()
    {
        ApplyGeneralSettingsSnapshot(_settingsService.GetSnapshot());
        var holdSeconds = WindowsShellStartDelaySeconds;

        if (holdSeconds <= 0)
        {
            return true;
        }

        ServiceStateText = IsGermanUi ? "Steam ist bereit" : "Steam is ready";
        ServiceDetailText = IsGermanUi
            ? $"Steam ist im Vordergrund bestätigt. Der Windows-Desktop startet in {holdSeconds} s."
            : $"Steam is confirmed in the foreground. Starting the Windows desktop in {holdSeconds}s.";
        await Task.Delay(TimeSpan.FromSeconds(holdSeconds));

        // The configurable hold can be several seconds long. Steam may close,
        // switch to a login/error page, or lose foreground during that time.
        // Revalidate once before uncovering the desktop instead of trusting the
        // earlier observation indefinitely.
        var status = await _processManager.GetStatusAsync();
        var runtime = _steamEnvironmentProbe.Capture();
        _lastKnownStatus = status;
        var uiState = status?.SteamUiState ?? SteamUiState.Unknown;
        var stillReady = runtime.SteamRunning &&
            runtime.Windows.IsSteamForeground &&
            !runtime.ErrorReporterRunning &&
            uiState is not (SteamUiState.Login or SteamUiState.Offline or SteamUiState.Error) &&
            (runtime.Windows.HasLikelyGamepadWindow ||
             (runtime.Windows.HasVisibleSteamWindow && uiState == SteamUiState.Gamepad));

        if (stillReady)
        {
            return true;
        }

        SteamStartupDiagnostics.Write(
            $"Steam readiness changed during shell hand-off hold; continuing monitor " +
            $"(running={runtime.SteamRunning}, foreground={runtime.Windows.IsSteamForeground}, ui={uiState})");
        ApplyShellBootstrapStatus(status, runtime);
        return false;
    }

    private void CompleteStartupSplashHandOff(bool steamReady)
    {
        // Clear the splash without a fade so the manager UI cannot flash between
        // the overlay and Steam/the desktop. PropertyChanged hides this window.
        ShowStartupSplash = false;
        ShowFirstRunSetup = false;
        ShowSplashRecoveryActions = false;

        if (steamReady)
        {
            TryFocusSteamWindow();
        }

        CompleteShellBootstrap();
    }

    private void ApplyShellBootstrapStatus(
        SteamLoaderHostStatus? status,
        SteamRuntimeObservation? runtime = null)
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
        var bigPictureVisible = runtime?.Windows.HasLikelyGamepadWindow ??
            SteamBigPictureForegroundDetector.Capture(
                _steamInstallationService.ResolveSteamRootPath()).HasLikelyGamepadWindow;

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
                "eTray mode is active. Windows starts normally, then Tools for Steam runs from the tray, syncs launchers, and starts Steam in dev mode.",
            SteamLoaderRuntime.StartupModeXbox =>
                "Xbox Mode is active. Windows launches Tools for Steam as the gaming Home app, then TFS starts Steam and injects the Quick Access panel.",
            _ => "Shell takeover is active. Tools for Steam starts before Explorer, syncs launchers, starts Steam in dev mode, and then hands the session back to Windows Explorer."
        };
    }

    private async Task LoadSplashGameCoversAsync()
    {
        var steamRoot = _steamInstallationService.ResolveSteamRootPath();
        var thumbnails = await StartupSplashCoverService.LoadAsync(steamRoot).ConfigureAwait(false);

        await _dispatcher.InvokeAsync(() =>
        {
            if (_splashArtworkReleased)
            {
                return;
            }

            SplashGameCovers = thumbnails;
            SplashDebugText = $"loaded: {thumbnails.Count}";
        });
    }

    private void ApplyGeneralSettingsSnapshot(SteamLoaderGeneralSettingsSnapshot settings)
    {
        var splashScreen = settings.SplashScreen;
        if (!_splashArtworkReleased)
        {
            SplashCustomImagePath =
                splashScreen.ArtworkMode == StartupSplashArtworkMode.Custom && splashScreen.CustomImageExists
                    ? splashScreen.CustomImagePath
                    : string.Empty;
        }

        WindowsShellStartDelaySeconds = settings.WindowsShellStartDelaySeconds;
    }

    private static bool IsGermanUi =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
            "de",
            StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
