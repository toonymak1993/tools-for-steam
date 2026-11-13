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

/// <summary>
/// Classic TFS Curtain: a non-activating topmost window over the whole
/// monitor. Used when Big Picture is not available (desktop Steam) or when the
/// user picks Classic. It draws the same per-game look as the Modern curtain.
/// </summary>
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
    private readonly Rectangle _blackout;
    private int _closing;

    private CurtainOverlayHost(Window window, Rectangle blackout)
    {
        _window = window;
        _blackout = blackout;
        _dispatcher = window.Dispatcher;
    }

    public static Task<CurtainOverlayHost> ShowAsync(CurtainVisual visual)
    {
        var completion = new TaskCompletionSource<CurtainOverlayHost>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = BuildWindow(visual, out var blackout);
                var host = new CurtainOverlayHost(window, blackout);
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

    /// <summary>Fades the artwork to black before the game takes over.</summary>
    public Task FadeToBlackAsync(TimeSpan duration)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(() =>
        {
            var fade = new DoubleAnimation(_blackout.Opacity, 1, duration) { FillBehavior = FillBehavior.HoldEnd };
            fade.Completed += (_, _) => completion.TrySetResult();
            _blackout.BeginAnimation(UIElement.OpacityProperty, fade);
        });
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

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
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

    private static Window BuildWindow(CurtainVisual visual, out Rectangle blackout)
    {
        var steamWindow = Services.SteamBigPictureForegroundDetector.Capture().PreferredWindowHandle;
        var screen = steamWindow != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(steamWindow)
            : System.Windows.Forms.Screen.PrimaryScreen;
        var bounds = screen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var root = new Grid
        {
            Background = Brushes.Black,
            ClipToBounds = true
        };

        if (TryLoadBitmap(visual.BackgroundPath, out var backgroundBitmap))
        {
            var brush = new ImageBrush(backgroundBitmap)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = visual.BackgroundX < 0.34 ? AlignmentX.Left : visual.BackgroundX > 0.66 ? AlignmentX.Right : AlignmentX.Center,
                AlignmentY = visual.BackgroundY < 0.34 ? AlignmentY.Top : visual.BackgroundY > 0.66 ? AlignmentY.Bottom : AlignmentY.Center
            };
            var background = new Rectangle
            {
                Fill = brush,
                Opacity = visual.BackgroundOpacity,
                RenderTransformOrigin = new Point(visual.BackgroundX, visual.BackgroundY)
            };
            var scale = new ScaleTransform(visual.BackgroundScale, visual.BackgroundScale);
            background.RenderTransform = scale;
            if (visual.BackgroundZoom)
            {
                var zoom = new DoubleAnimation(visual.BackgroundScale, visual.BackgroundScale * 1.045, TimeSpan.FromSeconds(18))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
            }

            root.Children.Add(background);
        }

        var vignette = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.8
        };
        vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
        vignette.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0, 0, 0), 1));
        root.Children.Add(new Rectangle { Fill = vignette });

        var stage = new Canvas();
        root.Children.Add(stage);
        FrameworkElement centerpiece;
        if (TryLoadBitmap(visual.LogoPath, out var logoBitmap))
        {
            centerpiece = new System.Windows.Controls.Image
            {
                Source = logoBitmap,
                Stretch = Stretch.Uniform,
                Width = bounds.Width * visual.LogoWidth,
                MaxHeight = bounds.Height * 0.45
            };
        }
        else
        {
            centerpiece = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(visual.Title) ? "Starting game" : visual.Title,
                MaxWidth = bounds.Width * 0.8,
                Foreground = Brushes.White,
                FontSize = 54,
                FontWeight = FontWeights.Black,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
        }

        if (visual.LogoShadow)
        {
            centerpiece.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28,
                Opacity = 0.8,
                ShadowDepth = 4,
                Color = Colors.Black
            };
        }

        if (visual.LogoZoom)
        {
            var logoScale = new ScaleTransform(1, 1);
            centerpiece.RenderTransformOrigin = new Point(0.5, 0.5);
            centerpiece.RenderTransform = logoScale;
            var pulse = new DoubleAnimation(1, 1.06, TimeSpan.FromSeconds(6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            logoScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            logoScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }

        // Logo position is its centre in percent of the screen, like the Modern curtain.
        centerpiece.SizeChanged += (_, _) =>
        {
            Canvas.SetLeft(centerpiece, stage.ActualWidth * visual.LogoX - centerpiece.ActualWidth / 2);
            Canvas.SetTop(centerpiece, stage.ActualHeight * visual.LogoY - centerpiece.ActualHeight / 2);
        };
        stage.SizeChanged += (_, _) =>
        {
            Canvas.SetLeft(centerpiece, stage.ActualWidth * visual.LogoX - centerpiece.ActualWidth / 2);
            Canvas.SetTop(centerpiece, stage.ActualHeight * visual.LogoY - centerpiece.ActualHeight / 2);
        };
        stage.Children.Add(centerpiece);

        if (visual.ShowStatus)
        {
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
                Text = visual.StatusText.ToUpperInvariant(),
                Foreground = new SolidColorBrush(Color.FromRgb(225, 232, 240)),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            });
            statusContent.Children.Add(new TextBlock
            {
                Text = "B  CLOSE",
                Margin = new Thickness(22, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(160, 172, 184)),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            });
            root.Children.Add(new Border
            {
                Padding = new Thickness(22, 12, 22, 12),
                Margin = new Thickness(0, 0, 0, 48),
                CornerRadius = new CornerRadius(24),
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = statusContent
            });
        }

        blackout = new Rectangle { Fill = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
        root.Children.Add(blackout);

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

    private static bool TryLoadBitmap(string path, out BitmapImage bitmap)
    {
        bitmap = new BitmapImage();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
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

/// <summary>Everything the curtain draws, resolved for one game (fractions 0..1).</summary>
internal sealed record CurtainVisual(
    string Title,
    string BackgroundPath,
    double BackgroundOpacity,
    double BackgroundScale,
    double BackgroundX,
    double BackgroundY,
    bool BackgroundZoom,
    string LogoPath,
    double LogoX,
    double LogoY,
    double LogoWidth,
    bool LogoShadow,
    bool LogoZoom,
    bool ShowStatus,
    string StatusText);
