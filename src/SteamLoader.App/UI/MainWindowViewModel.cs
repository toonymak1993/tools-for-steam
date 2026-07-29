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
using ToolsForSteam.Splash;

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
    private bool _showStartupSplash;
    private bool _showFirstRunSetup;
    private bool _windowsShellStarted;
    // Fixed head start before the console-mode splash is taken down and the
    // session is handed back to Windows (Steam keeps loading behind it).
    private static readonly TimeSpan SplashHandOffDelay = TimeSpan.FromSeconds(10);

    private Task? _shellBootstrapMonitorTask;
    private SteamLoaderHostStatus? _lastKnownStatus;
    private int _stableBigPictureVisiblePollCount;
    private int _stableSteamSignalPollCount;
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
        // Simple, predictable hand-off: give Steam a fixed head start, then take
        // the splash down and hand the session back to Windows. No Big Picture
        // window/title detection - Steam keeps loading behind the scenes either
        // way, and the old detection was locale-fragile and could hang the splash.
        await Task.Delay(SplashHandOffDelay);

        // Clear the splash WITHOUT the fade: fading it out briefly reveals the
        // manager UI underneath it. Clearing the flag directly triggers the
        // window's hide-to-tray, so the manager UI never appears in console mode -
        // Tools for Steam just runs in the background.
        ShowStartupSplash = false;
        ShowFirstRunSetup = false;

        TryFocusSteamWindow();
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

    private static bool IsBigPictureWindowTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        // Steam titles the Gamepad UI window "Big-Picture" (with a hyphen), and
        // localized builds add suffixes (e.g. German "Big-Picture-Modus"). The
        // old space-only check ("Big Picture") never matched those, which left
        // startup stuck at "waiting for the Big Picture window" even though Big
        // Picture was already up. Normalise the separators so all variants match.
        return title.Replace('-', ' ').Contains("Big Picture", StringComparison.OrdinalIgnoreCase);
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
                        IsBigPictureWindowTitle(process.MainWindowTitle))
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

    private sealed record ShellHandOffReadiness(
        bool BigPictureVisible,
        bool SteamSignalReady,
        bool WindowsShellHandOffReady);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
