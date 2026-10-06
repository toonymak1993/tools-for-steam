using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SteamLoader.App.Infrastructure.Curtain;

internal sealed class CurtainOverlayHost
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private int _closing;

    private CurtainOverlayHost(Window window)
    {
        _window = window;
        _dispatcher = window.Dispatcher;
    }

    public static Task<CurtainOverlayHost> ShowAsync(
        string title,
        string backgroundPath,
        string logoPath)
    {
        var completion = new TaskCompletionSource<CurtainOverlayHost>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = BuildWindow(title, backgroundPath, logoPath);
                var host = new CurtainOverlayHost(window);
                window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
                    DispatcherPriority.Background);
                window.Show();
                completion.TrySetResult(host);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "TFS Curtain",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public Task CloseAsync(bool animate)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(() =>
        {
            if (!animate)
            {
                _window.Close();
                completion.TrySetResult();
                return;
            }

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) =>
            {
                _window.Close();
                completion.TrySetResult();
            };
            _window.BeginAnimation(UIElement.OpacityProperty, fade);
        });
        return completion.Task;
    }

    private static Window BuildWindow(string title, string backgroundPath, string logoPath)
    {
        var steamWindow = Services.SteamBigPictureForegroundDetector.Capture().PreferredWindowHandle;
        var screen = steamWindow != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(steamWindow)
            : System.Windows.Forms.Screen.PrimaryScreen;
        var bounds = screen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(5, 10, 17)),
            ClipToBounds = true
        };

        if (TryCreateImage(backgroundPath, out var background))
        {
            background.Stretch = Stretch.UniformToFill;
            background.Opacity = 0.82;
            background.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(1, 1);
            background.RenderTransform = scale;
            var zoom = new DoubleAnimation(1, 1.045, TimeSpan.FromSeconds(18))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
            root.Children.Add(background);
        }

        root.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(
                Color.FromArgb(34, 3, 9, 16),
                Color.FromArgb(178, 5, 11, 19),
                new Point(0.5, 0),
                new Point(0.5, 1))
        });

        var vignette = new Rectangle();
        var vignetteBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.44),
            GradientOrigin = new Point(0.5, 0.44),
            RadiusX = 0.72,
            RadiusY = 0.78
        };
        vignetteBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 4, 10, 18), 0));
        vignetteBrush.GradientStops.Add(new GradientStop(Color.FromArgb(145, 3, 7, 13), 1));
        vignette.Fill = vignetteBrush;
        root.Children.Add(vignette);

        var layout = new Grid { Margin = new Thickness(54, 42, 54, 42) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(91, 205, 245)),
            Child = new TextBlock
            {
                Text = "TFS",
                Foreground = new SolidColorBrush(Color.FromRgb(7, 26, 39)),
                FontSize = 14,
                FontWeight = FontWeights.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        brand.Children.Add(new TextBlock
        {
            Text = "TFS CURTAIN",
            Margin = new Thickness(16, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(122, 216, 250)),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(brand, 0);
        layout.Children.Add(brand);

        FrameworkElement centerpiece;
        if (TryCreateImage(logoPath, out var logo))
        {
            logo.Stretch = Stretch.Uniform;
            logo.MaxWidth = 760;
            logo.MaxHeight = 300;
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            logo.VerticalAlignment = VerticalAlignment.Center;
            logo.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28,
                Opacity = 0.75,
                ShadowDepth = 4,
                Color = Colors.Black
            };
            centerpiece = logo;
        }
        else
        {
            centerpiece = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(title) ? "Starting game" : title,
                MaxWidth = 1080,
                Foreground = Brushes.White,
                FontSize = 54,
                FontWeight = FontWeights.Black,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    Opacity = 0.75,
                    ShadowDepth = 4,
                    Color = Colors.Black
                }
            };
        }
        Grid.SetRow(centerpiece, 1);
        layout.Children.Add(centerpiece);

        var statusContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Orientation = Orientation.Horizontal
        };
        statusContent.Children.Add(new Ellipse
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(0, 0, 12, 0),
            Fill = new SolidColorBrush(Color.FromRgb(94, 230, 168))
        });
        statusContent.Children.Add(new TextBlock
        {
            Text = "PREPARING GAME",
            Foreground = new SolidColorBrush(Color.FromRgb(220, 230, 239)),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        statusContent.Children.Add(new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(18, 0, 18, 0),
            Background = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255))
        });
        statusContent.Children.Add(new TextBlock
        {
            Text = "B  CLOSE",
            Foreground = new SolidColorBrush(Color.FromRgb(169, 188, 204)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        var status = new Border
        {
            Padding = new Thickness(22, 13, 22, 13),
            CornerRadius = new CornerRadius(24),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Background = new SolidColorBrush(Color.FromArgb(170, 4, 11, 19)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = statusContent
        };
        Grid.SetRow(status, 2);
        layout.Children.Add(status);
        root.Children.Add(layout);

        var window = new Window
        {
            Title = "TFS Curtain",
            Content = root,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = true,
            Background = Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height
        };
        window.SourceInitialized += (_, _) => ApplyNativeWindowStyle(window, bounds);
        return window;
    }

    private static bool TryCreateImage(string path, out System.Windows.Controls.Image image)
    {
        image = new System.Windows.Controls.Image();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(path, UriKind.Absolute);
            source.EndInit();
            source.Freeze();
            image.Source = source;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyNativeWindowStyle(Window window, System.Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(
            handle,
            GwlExStyle,
            new IntPtr(style | WsExNoActivate | WsExToolWindow));

        var dpi = GetDpiForWindow(handle);
        if (dpi > 0)
        {
            var scale = 96d / dpi;
            window.Left = bounds.Left * scale;
            window.Top = bounds.Top * scale;
            window.Width = bounds.Width * scale;
            window.Height = bounds.Height * scale;
        }

        _ = SetWindowPos(
            handle,
            HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
