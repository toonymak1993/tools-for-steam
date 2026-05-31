using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SteamLoader.App.UI;

namespace SteamLoader.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private bool _allowClose;

    public bool StartHiddenInTray { get; set; }

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
                await viewModel.InitializeAsync();
            }

            _refreshTimer.Start();

            if (StartHiddenInTray)
            {
                _ = Dispatcher.BeginInvoke(HideToTray, DispatcherPriority.ApplicationIdle);
            }
        };

        Closed += (_, _) => _refreshTimer.Stop();
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

    private void OnClosingToTray(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        HideToTray();
    }
}
