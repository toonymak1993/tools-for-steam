using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>What one popup shows, already resolved (image decoded, texts final).</summary>
internal sealed record PopupView(
    string Category,
    string Title,
    string Body,
    string Detail,
    BitmapSource? Image,
    bool RoundImage,
    double? Progress);

/// <summary>How a theme moves a popup in and out.</summary>
internal enum PopupMotion
{
    Slide,
    Drop,
    Wipe,
    Rise,
    Pop,
    Fade,
}

internal sealed record PopupVisual(FrameworkElement Root, PopupMotion Motion, TimeSpan EnterDuration);

/// <summary>
/// The TFS Popups designs. Each theme has its own shape, material, motion and
/// sound (see <see cref="PopupSoundPlayer"/>). Sizes are DIPs at 100 %; the
/// lane applies the notification size on top.
/// </summary>
internal static class PopupThemes
{
    private static readonly FontFamily TextFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Color Accent(string category) => category switch
    {
        PopupCategories.Achievements => Color.FromRgb(0xF5, 0xC0, 0x4A),
        PopupCategories.Messages => Color.FromRgb(0x4A, 0xA3, 0xFF),
        PopupCategories.Friends => Color.FromRgb(0x5B, 0xD6, 0x8A),
        PopupCategories.Downloads => Color.FromRgb(0x3D, 0xD6, 0xC6),
        PopupCategories.System => Color.FromRgb(0xFF, 0x9E, 0x45),
        PopupCategories.Community => Color.FromRgb(0xB0, 0x7C, 0xFF),
        _ => Color.FromRgb(0xE6, 0xEA, 0xF0),
    };

    public static string Glyph(string category) => category switch
    {
        PopupCategories.Achievements => "",
        PopupCategories.Messages => "",
        PopupCategories.Friends => "",
        PopupCategories.Downloads => "",
        PopupCategories.System => "",
        PopupCategories.Community => "",
        _ => "",
    };

    public static PopupVisual Build(string theme, PopupView view) => theme switch
    {
        PopupThemeIds.Pill => Pill(view),
        PopupThemeIds.Tile => Tile(view),
        PopupThemeIds.Glow => Glow(view),
        PopupThemeIds.Light => Light(view),
        PopupThemeIds.Minimal => Minimal(view),
        _ => Tfs(view),
    };

    /// <summary>Volume indicator in the same design language as the theme.</summary>
    public static PopupVisual BuildVolume(string theme, double level, bool muted)
    {
        var percent = (int)Math.Round(Math.Clamp(level, 0, 1) * 100);
        var glyph = muted || percent == 0 ? "" : percent < 34 ? "" : percent < 67 ? "" : "";
        var (background, foreground, track, fill, radius, border) = theme switch
        {
            PopupThemeIds.Light => (Solid(0xF4, 0xF6, 0xF9, 0xF5), Solid(0x15, 0x19, 0x20), Solid(0x15, 0x19, 0x20, 0x26), Solid(0x15, 0x19, 0x20), 22d, (Brush?)null),
            PopupThemeIds.Tile => (Solid(0x12, 0x15, 0x1B, 0xF2), Brushes.White, Solid(0xFF, 0xFF, 0xFF, 0x26), Solid(0x3D, 0xD6, 0xC6), 0d, null),
            PopupThemeIds.Glow => (Solid(0x0E, 0x14, 0x24, 0xF0), Brushes.White, Solid(0xFF, 0xFF, 0xFF, 0x22), Solid(0x6C, 0xB4, 0xFF), 14d, Solid(0x6C, 0xB4, 0xFF, 0x80)),
            PopupThemeIds.Minimal => (Solid(0x0A, 0x0C, 0x10, 0xE0), Brushes.White, Solid(0xFF, 0xFF, 0xFF, 0x26), Brushes.White, 8d, null),
            PopupThemeIds.Pill => (Solid(0x0B, 0x0D, 0x12, 0xE6), Brushes.White, Solid(0xFF, 0xFF, 0xFF, 0x26), Brushes.White, 30d, Solid(0xFF, 0xFF, 0xFF, 0x1F)),
            _ => (Solid(0x10, 0x14, 0x1C, 0xEE), Brushes.White, Solid(0xFF, 0xFF, 0xFF, 0x22), Solid(0x4A, 0xA3, 0xFF), 16d, Solid(0xFF, 0xFF, 0xFF, 0x18)),
        };

        var trackWidth = 200d;
        var bar = new Grid { Width = trackWidth, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
        bar.Children.Add(new Border { Background = track, CornerRadius = new CornerRadius(3) });
        bar.Children.Add(new Border
        {
            Background = muted ? track : fill,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(muted ? 0 : 6, trackWidth * percent / 100d),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 20, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center, Width = 24, TextAlignment = TextAlignment.Center });
        row.Children.Add(bar);
        row.Children.Add(new TextBlock
        {
            Text = muted ? "—" : percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontFamily = TextFont,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            Width = 34,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var card = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(radius),
            BorderBrush = border,
            BorderThickness = border is null ? new Thickness(0) : new Thickness(1),
            Padding = new Thickness(18, 12, 18, 12),
            Child = row,
            Effect = Shadow(theme == PopupThemeIds.Glow ? Color.FromRgb(0x6C, 0xB4, 0xFF) : Colors.Black, theme == PopupThemeIds.Glow ? 0.55 : 0.45, 22),
        };
        return new PopupVisual(card, PopupMotion.Fade, TimeSpan.FromMilliseconds(160));
    }

    // ------------------------------------------------------------------
    // Themes
    // ------------------------------------------------------------------

    /// <summary>TFS: dark glass card, accent stripe, rounded image.</summary>
    private static PopupVisual Tfs(PopupView view)
    {
        var accent = Accent(view.Category);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stripe = new Border { Width = 4, Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 14, 4) };
        grid.Children.Add(stripe);

        var image = ImageOrGlyph(view, 56, view.RoundImage ? 28 : 10, accent, Solid(0xFF, 0xFF, 0xFF, 0x14), Brushes.White);
        image.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(image, 1);
        grid.Children.Add(image);

        var text = TextStack(view, Brushes.White, Solid(0xC9, 0xD2, 0xDE), new SolidColorBrush(accent), titleSize: 17, bodySize: 14.5);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var card = new Border
        {
            Width = 380,
            Padding = new Thickness(12, 12, 16, 12),
            CornerRadius = new CornerRadius(16),
            Background = new LinearGradientBrush(Color.FromArgb(0xF0, 0x1A, 0x20, 0x2B), Color.FromArgb(0xF0, 0x10, 0x14, 0x1C), 90),
            BorderBrush = Solid(0xFF, 0xFF, 0xFF, 0x18),
            BorderThickness = new Thickness(1),
            Child = WithProgress(grid, view, accent, Solid(0xFF, 0xFF, 0xFF, 0x1E)),
            Effect = Shadow(Colors.Black, 0.5, 24),
        };
        return new PopupVisual(card, PopupMotion.Slide, TimeSpan.FromMilliseconds(260));
    }

    /// <summary>Pill: compact capsule with a round image, drops in with a small bounce.</summary>
    private static PopupVisual Pill(PopupView view)
    {
        var accent = Accent(view.Category);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var image = ImageOrGlyph(view, 44, 22, accent, new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)), new SolidColorBrush(accent));
        image.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(image);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
        text.Children.Add(Line(view.Title, Brushes.White, 15.5, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(view.Body))
        {
            text.Children.Add(Line(view.Body, Solid(0xB8, 0xC0, 0xCC), 13.5, FontWeights.Normal));
        }

        row.Children.Add(text);
        var card = new Border
        {
            Padding = new Thickness(6, 6, 22, 6),
            CornerRadius = new CornerRadius(30),
            Background = Solid(0x0B, 0x0D, 0x12, 0xE8),
            BorderBrush = Solid(0xFF, 0xFF, 0xFF, 0x1F),
            BorderThickness = new Thickness(1),
            Child = row,
            Effect = Shadow(Colors.Black, 0.45, 18),
        };
        return new PopupVisual(card, PopupMotion.Drop, TimeSpan.FromMilliseconds(380));
    }

    /// <summary>Tile: square corners, a big coloured image block and caps title.</summary>
    private static PopupVisual Tile(PopupView view)
    {
        var accent = Accent(view.Category);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var block = new Grid { Background = new SolidColorBrush(accent), MinHeight = 84 };
        var image = ImageOrGlyph(view, 64, view.RoundImage ? 32 : 0, accent, Brushes.Transparent, Solid(0x10, 0x12, 0x16));
        image.HorizontalAlignment = HorizontalAlignment.Center;
        image.VerticalAlignment = VerticalAlignment.Center;
        block.Children.Add(image);
        grid.Children.Add(block);

        var text = new StackPanel { Margin = new Thickness(16, 12, 16, 12), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = view.Title.ToUpper(System.Globalization.CultureInfo.CurrentCulture),
            FontFamily = TextFont,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(Wrapped(view.Body, Brushes.White, 16, FontWeights.SemiBold, 2));
        if (!string.IsNullOrWhiteSpace(view.Detail))
        {
            text.Children.Add(Line(view.Detail, Solid(0x9A, 0xA4, 0xB2), 12.5, FontWeights.Normal));
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var card = new Border
        {
            Width = 400,
            Background = Solid(0x12, 0x15, 0x1B, 0xF4),
            Child = WithProgress(grid, view, accent, Solid(0xFF, 0xFF, 0xFF, 0x1E)),
            Effect = Shadow(Colors.Black, 0.5, 16),
        };
        return new PopupVisual(card, PopupMotion.Wipe, TimeSpan.FromMilliseconds(240));
    }

    /// <summary>Glow: deep blue card with a soft coloured halo.</summary>
    private static PopupVisual Glow(PopupView view)
    {
        var accent = Accent(view.Category);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var image = ImageOrGlyph(view, 52, 26, accent, new SolidColorBrush(Color.FromArgb(0x2A, accent.R, accent.G, accent.B)), new SolidColorBrush(accent));
        image.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(image);
        var text = TextStack(view, Brushes.White, Solid(0xB9, 0xC6, 0xE0), new SolidColorBrush(accent), titleSize: 16.5, bodySize: 14);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var card = new Border
        {
            Width = 370,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush(Color.FromArgb(0xF2, 0x14, 0x1C, 0x33), Color.FromArgb(0xF2, 0x0B, 0x10, 0x1E), 45),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B), Color.FromArgb(0x20, accent.R, accent.G, accent.B), 0),
            BorderThickness = new Thickness(1.5),
            Child = WithProgress(grid, view, accent, Solid(0xFF, 0xFF, 0xFF, 0x1A)),
            Effect = Shadow(accent, 0.6, 26),
        };
        return new PopupVisual(card, PopupMotion.Rise, TimeSpan.FromMilliseconds(320));
    }

    /// <summary>Light: bright frosted card with dark text.</summary>
    private static PopupVisual Light(PopupView view)
    {
        var accent = Accent(view.Category);
        var darkAccent = Color.FromRgb((byte)(accent.R * 0.62), (byte)(accent.G * 0.62), (byte)(accent.B * 0.62));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var image = ImageOrGlyph(view, 54, view.RoundImage ? 27 : 14, accent, new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)), new SolidColorBrush(darkAccent));
        image.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(image);
        var text = TextStack(view, Solid(0x14, 0x18, 0x1F), Solid(0x4A, 0x52, 0x5E), new SolidColorBrush(darkAccent), titleSize: 16.5, bodySize: 14);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var card = new Border
        {
            Width = 370,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(20),
            Background = Solid(0xF4, 0xF6, 0xF9, 0xF6),
            Child = WithProgress(grid, view, darkAccent, Solid(0x14, 0x18, 0x1F, 0x1E)),
            Effect = Shadow(Colors.Black, 0.3, 22),
        };
        return new PopupVisual(card, PopupMotion.Pop, TimeSpan.FromMilliseconds(260));
    }

    /// <summary>Minimal: a small chip with an icon and one line, no artwork.</summary>
    private static PopupVisual Minimal(PopupView view)
    {
        var accent = Accent(view.Category);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = Glyph(view.Category),
            FontFamily = IconFont,
            FontSize = 15,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        var line = string.IsNullOrWhiteSpace(view.Body) ? view.Title : $"{view.Title}  ·  {view.Body}";
        row.Children.Add(new TextBlock
        {
            Text = line,
            FontFamily = TextFont,
            FontSize = 14,
            Foreground = Brushes.White,
            MaxWidth = 380,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var card = new Border
        {
            Padding = new Thickness(14, 9, 16, 9),
            CornerRadius = new CornerRadius(8),
            Background = Solid(0x0A, 0x0C, 0x10, 0xE0),
            Child = row,
        };
        return new PopupVisual(card, PopupMotion.Fade, TimeSpan.FromMilliseconds(180));
    }

    // ------------------------------------------------------------------
    // Parts
    // ------------------------------------------------------------------

    private static StackPanel TextStack(PopupView view, Brush title, Brush body, Brush detail, double titleSize, double bodySize)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Line(view.Title, title, titleSize, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(view.Body))
        {
            stack.Children.Add(Wrapped(view.Body, body, bodySize, FontWeights.Normal, 2));
        }

        if (!string.IsNullOrWhiteSpace(view.Detail))
        {
            var detailBlock = Line(view.Detail, detail, bodySize - 1.5, FontWeights.SemiBold);
            detailBlock.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(detailBlock);
        }

        return stack;
    }

    private static TextBlock Line(string text, Brush foreground, double size, FontWeight weight) => new()
    {
        Text = text,
        FontFamily = TextFont,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock Wrapped(string text, Brush foreground, double size, FontWeight weight, int maxLines) => new()
    {
        Text = text,
        FontFamily = TextFont,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = size * 1.42 * maxLines,
    };

    private static FrameworkElement ImageOrGlyph(PopupView view, double size, double radius, Color accent, Brush glyphBackground, Brush glyphForeground)
    {
        if (view.Image is not null)
        {
            var image = new Image { Source = view.Image, Width = size, Height = size, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (radius > 0)
            {
                image.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
            }

            return image;
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = glyphBackground,
            Child = new TextBlock
            {
                Text = Glyph(view.Category),
                FontFamily = IconFont,
                FontSize = size * 0.42,
                Foreground = glyphForeground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static FrameworkElement WithProgress(FrameworkElement content, PopupView view, Color accent, Brush track)
    {
        if (view.Progress is not { } progress)
        {
            return content;
        }

        var stack = new StackPanel();
        stack.Children.Add(content);
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(new Border { Background = track, CornerRadius = new CornerRadius(2) });
        var fill = new Border { Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        bar.SizeChanged += (_, _) => fill.Width = Math.Max(4, bar.ActualWidth * Math.Clamp(progress, 0, 1));
        bar.Children.Add(fill);
        stack.Children.Add(bar);
        return stack;
    }

    private static DropShadowEffect Shadow(Color color, double opacity, double blur) => new()
    {
        Color = color,
        Opacity = opacity,
        BlurRadius = blur,
        ShadowDepth = 0,
        RenderingBias = RenderingBias.Performance,
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
