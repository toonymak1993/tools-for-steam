using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>
/// Draws TFS Popups in small, click-through, never-activating topmost windows
/// on the Big Picture monitor, one window per screen position ("lane"). A lane
/// window only exists while it shows something, so idle cost is zero.
/// Exclusive fullscreen games are not covered (Windows keeps them on top);
/// <see cref="PopupService"/> hands those popups back to Steam.
/// </summary>
internal sealed class PopupOverlayHost : IDisposable
{
    private const int MaxPerLane = 3;
    private const double LaneMargin = 28;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(220);

    private readonly object _gate = new();
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    private Dispatcher? _dispatcher;
    private VolumeItem? _volume;
    private bool _disposed;

    private sealed class Lane
    {
        public required string Position { get; init; }
        public required Window Window { get; init; }
        public required StackPanel Stack { get; init; }
        public required ScaleTransform Scale { get; init; }
        public IntPtr Handle { get; set; }
    }

    private sealed class Item
    {
        public required Border Holder { get; init; }
        public required Lane Lane { get; init; }
        public required DispatcherTimer Timer { get; init; }
        public bool Leaving { get; set; }
    }

    private sealed class VolumeItem
    {
        public required Item Item { get; init; }
        public required string Theme { get; set; }
    }

    public void Show(string theme, PopupView view, string position, double scale, TimeSpan duration)
    {
        Invoke(() =>
        {
            var visual = PopupThemes.Build(theme, view);
            var lane = GetLane(position, scale);
            AddItem(lane, visual, duration);
        });
    }

    public void ShowVolume(string theme, double level, bool muted, string position, double scale)
    {
        Invoke(() =>
        {
            var visual = PopupThemes.BuildVolume(theme, level, muted);
            var duration = TimeSpan.FromMilliseconds(1600);
            if (_volume is { Item.Leaving: false } current &&
                current.Item.Lane.Position == position &&
                current.Item.Lane.Stack.Children.Contains(current.Item.Holder))
            {
                // Same indicator: swap the content and keep it on screen.
                current.Item.Holder.Child = visual.Root;
                current.Theme = theme;
                current.Item.Timer.Stop();
                current.Item.Timer.Interval = duration;
                current.Item.Timer.Start();
                Reposition(current.Item.Lane);
                return;
            }

            var lane = GetLane(position, scale);
            var item = AddItem(lane, visual, duration);
            _volume = new VolumeItem { Item = item, Theme = theme };
        });
    }

    public void Dispose()
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dispatcher = _dispatcher;
            _dispatcher = null;
        }

        try
        {
            dispatcher?.InvokeShutdown();
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------
    // UI thread
    // ------------------------------------------------------------------

    private void Invoke(Action action)
    {
        var dispatcher = EnsureDispatcher();
        dispatcher?.BeginInvoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"TFS Popups overlay failed: {exception}");
            }
        });
    }

    private Dispatcher? EnsureDispatcher()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim();
            Dispatcher? created = null;
            var thread = new Thread(() =>
            {
                created = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "TFS Popups",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            _dispatcher = created;
            return _dispatcher;
        }
    }

    private Lane GetLane(string position, double scale)
    {
        if (!_lanes.TryGetValue(position, out var lane))
        {
            var scaleTransform = new ScaleTransform(scale, scale);
            var stack = new StackPanel
            {
                LayoutTransform = scaleTransform,
                HorizontalAlignment = position.EndsWith("left", StringComparison.Ordinal)
                    ? HorizontalAlignment.Left
                    : position.EndsWith("right", StringComparison.Ordinal)
                        ? HorizontalAlignment.Right
                        : HorizontalAlignment.Center,
            };
            var window = new Window
            {
                Title = "TFS Popups",
                Content = new Border { Padding = new Thickness(LaneMargin), Child = stack, Background = Brushes.Transparent },
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
            };
            lane = new Lane { Position = position, Window = window, Stack = stack, Scale = scaleTransform };
            var captured = lane;
            window.SourceInitialized += (_, _) =>
            {
                captured.Handle = new WindowInteropHelper(window).Handle;
                var style = GetWindowLongPtr(captured.Handle, GwlExStyle).ToInt64();
                _ = SetWindowLongPtr(captured.Handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow | WsExTransparent));
            };
            window.SizeChanged += (_, _) => Reposition(captured);
            _lanes[position] = lane;
        }

        lane.Scale.ScaleX = scale;
        lane.Scale.ScaleY = scale;
        return lane;
    }

    private Item AddItem(Lane lane, PopupVisual visual, TimeSpan duration)
    {
        var translate = new TranslateTransform();
        var scaleTransform = new ScaleTransform(1, 1);
        var holder = new Border
        {
            Child = visual.Root,
            Margin = new Thickness(0, 6, 0, 6),
            Opacity = 0,
            RenderTransformOrigin = Origin(lane.Position),
            RenderTransform = new TransformGroup { Children = { scaleTransform, translate } },
        };

        var timer = new DispatcherTimer(DispatcherPriority.Normal, lane.Window.Dispatcher) { Interval = duration };
        var item = new Item { Holder = holder, Lane = lane, Timer = timer };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Leave(item);
        };

        // The newest popup always sits nearest the screen edge.
        var children = lane.Stack.Children;
        var top = PopupPositions.IsTop(lane.Position);
        if (top)
        {
            children.Insert(0, holder);
        }
        else
        {
            children.Add(holder);
        }

        while (children.Count > MaxPerLane)
        {
            var oldestIndex = top ? children.Count - 1 : 0;
            if (children[oldestIndex] is Border { Tag: Item old })
            {
                old.Timer.Stop();
                if (_volume?.Item == old)
                {
                    _volume = null;
                }
            }

            children.RemoveAt(oldestIndex);
        }

        holder.Tag = item;
        if (!lane.Window.IsVisible)
        {
            lane.Window.Show();
        }

        Reposition(lane);
        Enter(holder, translate, scaleTransform, visual, lane.Position);
        timer.Start();
        return item;
    }

    private void Leave(Item item)
    {
        if (item.Leaving)
        {
            return;
        }

        item.Leaving = true;
        var fade = new DoubleAnimation(item.Holder.Opacity, 0, ExitDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.Completed += (_, _) =>
        {
            var lane = item.Lane;
            lane.Stack.Children.Remove(item.Holder);
            if (_volume?.Item == item)
            {
                _volume = null;
            }

            if (lane.Stack.Children.Count == 0)
            {
                lane.Window.Hide();
            }
        };
        item.Holder.BeginAnimation(UIElement.OpacityProperty, fade);
        if (item.Holder.RenderTransform is TransformGroup group && group.Children[1] is TranslateTransform translate)
        {
            var offset = PopupPositions.IsTop(item.Lane.Position) ? -12d : 12d;
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, offset, ExitDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }
    }

    private static void Enter(Border holder, TranslateTransform translate, ScaleTransform scale, PopupVisual visual, string position)
    {
        var duration = visual.EnterDuration;
        var reduced = !SystemParameters.ClientAreaAnimation;
        var motion = reduced ? PopupMotion.Fade : visual.Motion;
        var fromTop = PopupPositions.IsTop(position);
        var side = position.EndsWith("left", StringComparison.Ordinal) ? -1 : position.EndsWith("right", StringComparison.Ordinal) ? 1 : 0;
        IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        holder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Math.Min(200, duration.TotalMilliseconds))));
        switch (motion)
        {
            case PopupMotion.Slide when side != 0:
                translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(side * 64, 0, duration) { EasingFunction = ease });
                break;
            case PopupMotion.Slide:
            case PopupMotion.Drop:
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromTop ? -48 : 48, 0, duration)
                {
                    EasingFunction = motion == PopupMotion.Drop ? new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut } : ease,
                });
                break;
            case PopupMotion.Wipe:
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.02, 1, duration) { EasingFunction = ease });
                break;
            case PopupMotion.Rise:
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, duration) { EasingFunction = ease });
                break;
            case PopupMotion.Pop:
                var pop = new DoubleAnimation(0.86, 1, duration) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                break;
        }
    }

    private static Point Origin(string position) =>
        new(position.EndsWith("left", StringComparison.Ordinal) ? 0 : position.EndsWith("right", StringComparison.Ordinal) ? 1 : 0.5,
            PopupPositions.IsTop(position) ? 0 : 1);

    private static void Reposition(Lane lane)
    {
        if (lane.Handle == IntPtr.Zero || !lane.Window.IsVisible)
        {
            return;
        }

        var bounds = TargetBounds();
        var dpi = GetDpiForWindow(lane.Handle);
        var factor = dpi > 0 ? dpi / 96d : 1d;
        var width = (int)Math.Ceiling(lane.Window.ActualWidth * factor);
        var height = (int)Math.Ceiling(lane.Window.ActualHeight * factor);
        var position = lane.Position;
        var x = position.EndsWith("left", StringComparison.Ordinal)
            ? bounds.Left
            : position.EndsWith("right", StringComparison.Ordinal)
                ? bounds.Right - width
                : bounds.Left + (bounds.Width - width) / 2;
        var y = PopupPositions.IsTop(position) ? bounds.Top : bounds.Bottom - height;
        _ = SetWindowPos(lane.Handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private static System.Drawing.Rectangle TargetBounds()
    {
        try
        {
            var steamWindow = Services.SteamBigPictureForegroundDetector.Capture().PreferredWindowHandle;
            var screen = steamWindow != IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(steamWindow)
                : System.Windows.Forms.Screen.PrimaryScreen;
            return screen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        }
        catch
        {
            return System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
