using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Application = System.Windows.Application;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SteamLoader.App.UI;

public sealed class TrayIconController : IDisposable
{
    private readonly Application _application;
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _headerItem;
    private readonly Forms.ToolStripMenuItem _serviceStateItem;
    private readonly Forms.ToolStripMenuItem _steamStateItem;
    private readonly Forms.ToolStripMenuItem _errorItem;
    private readonly Forms.ToolStripMenuItem _startHostItem;
    private readonly Forms.ToolStripMenuItem _restartHostItem;
    private readonly Forms.ToolStripMenuItem _stopHostItem;
    private readonly Forms.ToolStripMenuItem _autostartItem;
    private readonly Forms.ToolStripMenuItem _startDesktopItem;
    private readonly Forms.ToolStripMenuItem _openFolderItem;
    private readonly Forms.ToolStripMenuItem _refreshItem;
    private readonly Forms.ToolStripMenuItem _quitItem;
    private readonly Drawing.Icon _runningIcon;
    private readonly Drawing.Icon _stoppedIcon;
    private readonly Drawing.Icon _busyIcon;
    private bool _disposed;

    public TrayIconController(Application application, MainWindow window, MainWindowViewModel viewModel)
    {
        _application = application;
        _window = window;
        _viewModel = viewModel;

        _runningIcon = CreateTrayIcon(Drawing.Color.FromArgb(84, 190, 120));
        _stoppedIcon = CreateTrayIcon(Drawing.Color.FromArgb(130, 143, 158));
        _busyIcon = CreateTrayIcon(Drawing.Color.FromArgb(83, 150, 255));

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true
        };

        _headerItem = CreateLabelItem("Steam Tools");
        _serviceStateItem = CreateLabelItem("Service: Checking...");
        _steamStateItem = CreateLabelItem("Steam: Waiting for status...");
        _errorItem = CreateLabelItem(string.Empty);
        _errorItem.Visible = false;

        _startHostItem = new Forms.ToolStripMenuItem("Start Background Host", null, (_, _) => _viewModel.StartCommand.Execute(null));
        _restartHostItem = new Forms.ToolStripMenuItem("Restart Background Host", null, (_, _) => _viewModel.RestartCommand.Execute(null));
        _stopHostItem = new Forms.ToolStripMenuItem("Stop Background Host", null, (_, _) => _viewModel.StopCommand.Execute(null));
        _autostartItem = new Forms.ToolStripMenuItem("Run on Windows Sign-In (shell + sync + Steam)", null, (_, _) => _viewModel.ToggleAutostartSetting())
        {
            CheckOnClick = false
        };
        _startDesktopItem = new Forms.ToolStripMenuItem("Start Windows Desktop", null, (_, _) => _viewModel.StartWindowsDesktop());
        _openFolderItem = new Forms.ToolStripMenuItem("Open Portable Folder", null, (_, _) => _viewModel.OpenInstallFolder());
        _refreshItem = new Forms.ToolStripMenuItem("Refresh Status", null, async (_, _) => await _viewModel.RefreshAsync());
        _quitItem = new Forms.ToolStripMenuItem("Quit Tray App", null, (_, _) => QuitTrayApp());

        _menu.Items.AddRange(
        [
            _headerItem,
            _serviceStateItem,
            _steamStateItem,
            _errorItem,
            new Forms.ToolStripSeparator(),
            _startHostItem,
            _restartHostItem,
            _stopHostItem,
            new Forms.ToolStripSeparator(),
            _autostartItem,
            _startDesktopItem,
            _openFolderItem,
            _refreshItem,
            new Forms.ToolStripSeparator(),
            _quitItem
        ]);

        _menu.Opening += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            UpdateTrayState();
        };

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = false
        };

        _notifyIcon.DoubleClick += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            UpdateTrayState();
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public void Initialize()
    {
        ThrowIfDisposed();
        UpdateTrayState();
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _runningIcon.Dispose();
        _stoppedIcon.Dispose();
        _busyIcon.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(MainWindowViewModel.IsBusy):
            case nameof(MainWindowViewModel.IsRunning):
            case nameof(MainWindowViewModel.SteamStateText):
            case nameof(MainWindowViewModel.AutostartEnabled):
            case nameof(MainWindowViewModel.ErrorText):
            case nameof(MainWindowViewModel.ServiceDetailText):
                UpdateTrayState();
                break;
        }
    }

    private void UpdateTrayState()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Icon = _viewModel.IsBusy
            ? _busyIcon
            : _viewModel.IsRunning
                ? _runningIcon
                : _stoppedIcon;
        _notifyIcon.Text = BuildTooltipText();

        _serviceStateItem.Text = _viewModel.IsBusy
            ? "Service: Updating..."
            : _viewModel.IsRunning
                ? "Service: Running"
                : "Service: Stopped";
        _steamStateItem.Text = $"Steam: {TrimText(_viewModel.SteamStateText, 72)}";

        _errorItem.Visible = _viewModel.HasError;
        _errorItem.Text = $"Error: {TrimText(_viewModel.ErrorText, 72)}";

        _startHostItem.Enabled = _viewModel.StartCommand.CanExecute(null);
        _restartHostItem.Enabled = _viewModel.RestartCommand.CanExecute(null);
        _stopHostItem.Enabled = _viewModel.StopCommand.CanExecute(null);
        _refreshItem.Enabled = _viewModel.RefreshCommand.CanExecute(null);
        _autostartItem.Enabled = _viewModel.ToggleAutostartCommand.CanExecute(null);
        _autostartItem.Checked = _viewModel.AutostartEnabled;
        _startDesktopItem.Enabled = _viewModel.StartDesktopCommand.CanExecute(null);
    }

    private void QuitTrayApp()
    {
        _window.CloseFromTray();
        _application.Shutdown();
    }

    private string BuildTooltipText()
    {
        var status = _viewModel.IsBusy
            ? "Updating"
            : _viewModel.IsRunning
                ? "Running"
                : "Stopped";

        return TrimText($"Steam Tools - {status}", 63);
    }

    private static Forms.ToolStripMenuItem CreateLabelItem(string text)
    {
        return new Forms.ToolStripMenuItem(text)
        {
            Enabled = false
        };
    }

    private static string TrimText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : $"{trimmed[..Math.Max(0, maximumLength - 1)]}\u2026";
    }

    private static Drawing.Icon CreateTrayIcon(Drawing.Color accentColor)
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);

        using var shellPath = CreateRoundedRectanglePath(new Drawing.RectangleF(2, 2, 28, 28), 8f);
        using var shellBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(28, 36, 48));
        using var outlinePen = new Drawing.Pen(Drawing.Color.FromArgb(54, 68, 84), 1.4f);
        using var glyphBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(224, 231, 239));
        using var accentBrush = new Drawing.SolidBrush(accentColor);

        graphics.FillPath(shellBrush, shellPath);
        graphics.DrawPath(outlinePen, shellPath);

        graphics.FillRectangle(glyphBrush, 9, 9, 7, 7);
        graphics.FillRectangle(glyphBrush, 18, 9, 5, 5);
        graphics.FillRectangle(glyphBrush, 9, 18, 5, 5);
        graphics.FillRectangle(glyphBrush, 16, 18, 8, 3);
        graphics.FillEllipse(accentBrush, 20, 20, 7, 7);

        var iconHandle = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = Drawing.Icon.FromHandle(iconHandle);
            return (Drawing.Icon)temporaryIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static GraphicsPath CreateRoundedRectanglePath(Drawing.RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
