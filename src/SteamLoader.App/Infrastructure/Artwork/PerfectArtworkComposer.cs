using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace SteamLoader.App.Infrastructure.Artwork;

/// <summary>Logo placement for a Perfect Hero / Perfect Banner, in percent of the canvas.</summary>
public sealed record PerfectArtworkLayout(
    double LogoX = 50,
    double LogoY = 50,
    double LogoWidth = 40,
    double Opacity = 100,
    double Shadow = 40,
    double BackgroundY = 50);

/// <summary>
/// Bakes a game logo into a background ("Perfect Hero" for the game page
/// header, "Perfect Banner" for the wide capsule) with WPF imaging, at the
/// background's own resolution within Steam's slot shape.
/// </summary>
/// <remarks>Idea from Playhub Artworks (MIT, LoZazaMastro); independent code.</remarks>
public static class PerfectArtworkComposer
{
    public static (int Width, int Height) TargetSize(string kind, int backgroundWidth) => kind switch
    {
        // Steam's hero is 96:31; keep the source resolution between 1920 and 3840 wide.
        "hero" => Shape(Math.Clamp(backgroundWidth, 1920, 3840), 96, 31),
        // Wide capsule 920x430 (2x when the background is large enough).
        "grid_l" => backgroundWidth >= 1840 ? (1840, 860) : (920, 430),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only hero and grid_l can be composed."),
    };

    /// <summary>Logo rectangle on the canvas, clamped so the logo stays fully visible.</summary>
    public static Rect LogoRect(int canvasWidth, int canvasHeight, double logoPixelWidth, double logoPixelHeight, PerfectArtworkLayout layout)
    {
        var width = canvasWidth * Math.Clamp(layout.LogoWidth, 5, 90) / 100.0;
        var height = logoPixelWidth > 0 ? width * logoPixelHeight / logoPixelWidth : 0;
        if (height > canvasHeight * 0.9)
        {
            height = canvasHeight * 0.9;
            width = logoPixelHeight > 0 ? height * logoPixelWidth / logoPixelHeight : width;
        }

        var centerX = canvasWidth * Math.Clamp(layout.LogoX, 0, 100) / 100.0;
        var centerY = canvasHeight * Math.Clamp(layout.LogoY, 0, 100) / 100.0;
        var left = Math.Clamp(centerX - width / 2, 0, Math.Max(0, canvasWidth - width));
        var top = Math.Clamp(centerY - height / 2, 0, Math.Max(0, canvasHeight - height));
        return new Rect(left, top, width, height);
    }

    /// <summary>Must run on an STA thread (WPF).</summary>
    public static byte[] Compose(string kind, byte[] background, byte[] logo, PerfectArtworkLayout layout)
    {
        var backgroundImage = Decode(background);
        var logoImage = Decode(logo);
        var (width, height) = TargetSize(kind, backgroundImage.PixelWidth);

        // Cover the canvas with the background (like CSS object-fit: cover).
        var scale = Math.Max(width / (double)backgroundImage.PixelWidth, height / (double)backgroundImage.PixelHeight);
        var drawWidth = backgroundImage.PixelWidth * scale;
        var drawHeight = backgroundImage.PixelHeight * scale;
        var offsetX = (width - drawWidth) / 2;
        var offsetY = (height - drawHeight) * Math.Clamp(layout.BackgroundY, 0, 100) / 100.0;

        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true, Background = Brushes.Black };
        var backgroundElement = new Image { Source = backgroundImage, Width = drawWidth, Height = drawHeight, Stretch = Stretch.Fill };
        Canvas.SetLeft(backgroundElement, offsetX);
        Canvas.SetTop(backgroundElement, offsetY);
        canvas.Children.Add(backgroundElement);

        var rect = LogoRect(width, height, logoImage.PixelWidth, logoImage.PixelHeight, layout);
        var logoElement = new Image
        {
            Source = logoImage,
            Width = rect.Width,
            Height = rect.Height,
            Stretch = Stretch.Uniform,
            Opacity = Math.Clamp(layout.Opacity, 0, 100) / 100.0,
        };
        var shadow = Math.Clamp(layout.Shadow, 0, 100);
        if (shadow > 0)
        {
            logoElement.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = height * 0.03 * (0.4 + shadow / 100.0),
                ShadowDepth = height * 0.006,
                Direction = 270,
                Opacity = Math.Min(0.95, 0.25 + shadow / 120.0),
            };
        }

        RenderOptions.SetBitmapScalingMode(backgroundElement, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(logoElement, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(logoElement, rect.Left);
        Canvas.SetTop(logoElement, rect.Top);
        canvas.Children.Add(logoElement);

        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(canvas);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.DpiX is > 0 and not 96 || frame.DpiY is > 0 and not 96)
        {
            // Normalise DPI so WPF does not scale the image by its file metadata.
            var stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * frame.PixelHeight];
            frame.CopyPixels(pixels, stride, 0);
            frame = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, frame.Format, frame.Palette, pixels, stride);
        }

        frame.Freeze();
        return frame;
    }

    private static (int Width, int Height) Shape(int width, int ratioWidth, int ratioHeight) =>
        (width, (int)Math.Round(width * ratioHeight / (double)ratioWidth));
}
