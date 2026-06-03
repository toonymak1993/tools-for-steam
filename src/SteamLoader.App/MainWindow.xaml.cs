using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using SteamLoader.App.UI;

namespace SteamLoader.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private bool _allowClose;
    private bool _shellBootstrapMode;

    public bool StartHiddenInTray { get; set; }

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
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
                await viewModel.InitializeAsync();
            }

            _refreshTimer.Start();

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
    }

    public void CloseFromTray()
    {
        _allowClose = true;
        Close();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
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

        if (StartHiddenInTray && !viewModel.ShowStartupSplash)
        {
            _ = Dispatcher.BeginInvoke(HideToTray, DispatcherPriority.ApplicationIdle);
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
