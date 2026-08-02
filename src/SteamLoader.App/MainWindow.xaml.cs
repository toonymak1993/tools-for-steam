using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using SteamLoader.App.UI;
using InputKey = System.Windows.Input.Key;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SteamLoader.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private bool _allowClose;
    private bool _shellBootstrapMode;
    private bool _runtimeInitialized;

    public bool StartHiddenInTray { get; set; }

    public bool PreviewSplashMode { get; set; }

    public TimeSpan PreviewSplashDuration { get; set; } = TimeSpan.FromSeconds(5);

    public bool ShellBootstrapMode
    {
        get => _shellBootstrapMode;
        set
        {
            _shellBootstrapMode = value;
            if (_shellBootstrapMode)
            {
                ApplyStartupSplashChrome();
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        _refreshTimer.Tick += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshAsync();
            }
        };

        Loaded += async (_, _) =>
        {
            if (PreviewSplashMode)
            {
                _ = CloseSplashPreviewAsync();
                return;
            }

            await InitializeRuntimeAsync();

            if (StartHiddenInTray && !ShellBootstrapMode)
            {
                _ = Dispatcher.BeginInvoke(HideToTray, DispatcherPriority.ApplicationIdle);
            }
        };

        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        };
        PreviewKeyDown += OnSplashRecoveryKeyDown;
        Closing += OnClosingToTray;
    }

    public void ShowManager()
    {
        RestoreManagerChromeIfNeeded();
        ShowInTaskbar = true;

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
        _refreshTimer.Start();

        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.RefreshAsync();
        }
    }

    public void CloseFromTray()
    {
        _allowClose = true;
        System.Windows.Application.Current?.Shutdown();
    }

    public async Task InitializeHiddenAsync()
    {
        await InitializeRuntimeAsync();
    }

    private void HideToTray()
    {
        _refreshTimer.Stop();
        ShowInTaskbar = false;
        Hide();

        if (DataContext is MainWindowViewModel viewModel &&
            !viewModel.ShowStartupSplash)
        {
            viewModel.ReleaseSplashArtwork();
        }
    }

    private async Task InitializeRuntimeAsync()
    {
        if (_runtimeInitialized)
        {
            return;
        }

        _runtimeInitialized = true;

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            await viewModel.InitializeAsync();
        }

        // The startup monitor already performs an adaptive, signal-driven status
        // check. Do not run the manager's periodic refresh alongside it.
        if (IsVisible &&
            !(DataContext is MainWindowViewModel activeViewModel && activeViewModel.ShowStartupSplash))
        {
            _refreshTimer.Start();
        }
    }

    private async Task CloseSplashPreviewAsync()
    {
        await Task.Delay(PreviewSplashDuration);
        _allowClose = true;
        System.Windows.Application.Current?.Shutdown();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MainWindowViewModel.ShowStartupSplash))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.ShowStartupSplash)
        {
            _refreshTimer.Stop();
            return;
        }

        if (StartHiddenInTray && !viewModel.ShowStartupSplash)
        {
            // Call directly (no dispatcher delay) so the window hides before
            // WPF renders the now-uncovered manager UI — eliminates the flicker.
            HideToTray();
            return;
        }

        if (IsVisible)
        {
            _refreshTimer.Start();
        }
    }

    private void OnClosingToTray(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        HideToTray();
    }

    private void OnSplashRecoveryKeyDown(object sender, InputKeyEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            !viewModel.ShowStartupSplash ||
            !viewModel.ShowSplashRecoveryActions)
        {
            return;
        }

        ICommand? command = eventArgs.Key switch
        {
            InputKey.A => viewModel.ContinueWaitingFromSplashCommand,
            InputKey.X => viewModel.RestartSteamFromSplashCommand,
            InputKey.Y => viewModel.OpenDesktopFromSplashCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true)
        {
            return;
        }

        command.Execute(null);
        eventArgs.Handled = true;
    }

    private void ApplyStartupSplashChrome()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#081019"));
    }

    private void RestoreManagerChromeIfNeeded()
    {
        if (!ShellBootstrapMode)
        {
            return;
        }

        Topmost = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
        Width = 980;
        Height = 660;
        MinWidth = 920;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10161F"));
        ShellBootstrapMode = false;
    }
}
