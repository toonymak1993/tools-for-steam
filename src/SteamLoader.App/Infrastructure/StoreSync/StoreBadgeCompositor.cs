using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Downloads launcher/store icon PNGs from the internet, caches them locally, and composites
/// a small rounded-rectangle badge onto SteamGridDB artwork so users can see at a glance which
/// launcher a non-Steam shortcut belongs to.
///
/// Badges are only applied during the automatic StoreSync artwork pipeline —
/// the manual SteamGridDB plugin uses a completely separate service and is unaffected.
/// </summary>
internal static class StoreBadgeCompositor
{
    // ── Cache ─────────────────────────────────────────────────────────────────

    private static readonly string DiskCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ToolsForSteam",
        "badgecache");

    // Keyed by storeId.  null value = "download failed, use fallback text".
    private static readonly Dictionary<string, byte[]?> MemoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    // ── Store icon URL candidates ─────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string[]> StoreIconUrls =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["epic-games"] = [
                "https://www.epicgames.com/site/apple-touch-icon.png",
                "https://cdn2.epicgames.com/epic-icon.png",
            ],
            ["gog-galaxy"] = [
                "https://www.gog.com/apple-touch-icon.png",
                "https://www.gog.com/favicon-96x96.png",
            ],
            ["xbox-game-pass"] = [
                "https://www.xbox.com/apple-touch-icon.png",
            ],
            ["ubisoft-connect"] = [
                "https://www.ubisoft.com/apple-touch-icon.png",
                "https://www.ubisoft.com/favicon-96x96.png",
            ],
            ["ea-app"] = [
                "https://www.ea.com/apple-touch-icon.png",
                "https://www.ea.com/favicon-96x96.png",
            ],
            ["battle-net"] = [
                "https://www.blizzard.com/apple-touch-icon.png",
                "https://www.battle.net/apple-touch-icon.png",
            ],
            ["amazon-games"] = [
                "https://gaming.amazon.com/apple-touch-icon.png",
            ],
            ["itch-io"] = [
                "https://itch.io/apple-touch-icon.png",
                "https://itch.io/static/images/itchio-logo-black-new.png",
            ],
        };

    // ── Fallback colored badge per store (used when icon download fails) ──────

    private static readonly IReadOnlyDictionary<string, (Color Background, string Label)> StoreFallbacks =
        new Dictionary<string, (Color, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["epic-games"]      = (Color.FromArgb(30,  30,  30),  "E"),
            ["gog-galaxy"]      = (Color.FromArgb(134, 96,  212), "G"),
            ["xbox-game-pass"]  = (Color.FromArgb(16,  124, 16),  "X"),
            ["ubisoft-connect"] = (Color.FromArgb(28,  28,  191), "U"),
            ["ea-app"]          = (Color.FromArgb(145, 70,  255), "EA"),
            ["battle-net"]      = (Color.FromArgb(0,   112, 221), "B"),
            ["amazon-games"]    = (Color.FromArgb(255, 153, 0),   "A"),
            ["itch-io"]         = (Color.FromArgb(250, 91,  88),  "i"),
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Composites the store badge onto the artwork at <paramref name="imagePath"/> in-place.
    /// Safe to call from any thread.  Returns <c>false</c> silently on any failure.
    /// </summary>
    public static async Task<bool> ApplyBadgeAsync(
        string imagePath,
        string storeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(storeId))
        {
            return false;
        }

        if (!File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            var iconBytes = await GetOrFetchIconBytesAsync(storeId, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => CompositeOnto(imagePath, storeId, iconBytes), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Icon fetching and caching ─────────────────────────────────────────────

    private static async Task<byte[]?> GetOrFetchIconBytesAsync(
        string storeId,
        CancellationToken cancellationToken)
    {
        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (MemoryCache.TryGetValue(storeId, out var cached))
            {
                return cached;
            }

            // 1. Bundled embedded resource (highest priority — ships with the app).
            var embedded = TryLoadEmbeddedIcon(storeId);
            if (embedded != null)
            {
                MemoryCache[storeId] = embedded;
                return embedded;
            }

            // 2. Disk cache (previously downloaded icon).
            Directory.CreateDirectory(DiskCacheDirectory);
            var diskPath = Path.Combine(DiskCacheDirectory, $"{storeId}.png");
            if (File.Exists(diskPath))
            {
                try
                {
                    var diskBytes = await File.ReadAllBytesAsync(diskPath, cancellationToken).ConfigureAwait(false);
                    MemoryCache[storeId] = diskBytes;
                    return diskBytes;
                }
                catch
                {
                }
            }

            // 3. Download from web (stores without a bundled icon).
            if (StoreIconUrls.TryGetValue(storeId, out var urls))
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

                foreach (var url in urls)
                {
                    try
                    {
                        var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

                        // Validate that this is actually an image (not an HTML error page).
                        if (!IsValidImageBytes(bytes))
                        {
                            continue;
                        }

                        await File.WriteAllBytesAsync(diskPath, bytes, cancellationToken).ConfigureAwait(false);
                        MemoryCache[storeId] = bytes;
                        return bytes;
                    }
                    catch
                    {
                    }
                }
            }

            // Could not find any icon — remember so we don't retry on every sync.
            MemoryCache[storeId] = null;
            return null;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    /// <summary>
    /// Tries to load a store icon that was bundled as an embedded resource under
    /// Assets/StoreIcons/{storeId}.png (or .ico).  Returns null when not found.
    /// </summary>
    private static byte[]? TryLoadEmbeddedIcon(string storeId)
    {
        var assembly = typeof(StoreBadgeCompositor).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        // .NET embeds files as "{RootNamespace}.{RelativePath}" where path separators → dots.
        // Hyphens in file names survive unchanged, so we match by suffix only.
        foreach (var ext in new[] { ".png", ".ico" })
        {
            // Try both hyphenated (ea-app.png) and the rare underscore variant.
            var suffixHyphen = $".{storeId}{ext}";
            var suffixUnder  = $".{storeId.Replace('-', '_')}{ext}";

            var name = resourceNames.FirstOrDefault(r =>
                r.EndsWith(suffixHyphen, StringComparison.OrdinalIgnoreCase) ||
                r.EndsWith(suffixUnder,  StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    continue;
                }

                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes, 0, bytes.Length);

                if (IsValidImageBytes(bytes))
                {
                    return bytes;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool IsValidImageBytes(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        // PNG magic: 89 50 4E 47
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return true;
        }

        // JPEG magic: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        // ICO magic: 00 00 01 00
        if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
        {
            return true;
        }

        return false;
    }

    // ── Image compositing ─────────────────────────────────────────────────────

    private static void CompositeOnto(string imagePath, string storeId, byte[]? iconBytes)
    {
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();

        // Load the artwork into a fresh Bitmap.
        Bitmap artwork;
        using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var img = Image.FromStream(fs))
        {
            artwork = new Bitmap(img);
        }

        using (artwork)
        {
            // Badge height: ~7-8 % of artwork width, clamped to [44, 100] px.
            // Badge width: 30 % wider than height so logos have horizontal breathing room.
            var badgeH = Math.Max(44, Math.Min(100, artwork.Width / 13));
            var badgeW = (int)(badgeH * 1.3);
            var margin = Math.Max(10, badgeH / 4);
            var bx = artwork.Width  - badgeW - margin;
            var by = artwork.Height - badgeH - margin;

            using var g = Graphics.FromImage(artwork);
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            DrawBadge(g, storeId, iconBytes, bx, by, badgeW, badgeH);

            SaveArtwork(artwork, imagePath, extension);
        }
    }

    private static void DrawBadge(
        Graphics g,
        string storeId,
        byte[]? iconBytes,
        int x, int y, int w, int h)
    {
        const int cornerRadius = 8;
        const int padding = 7;

        // Background is always near-black so badges look consistent on any artwork.
        var bgColor = Color.FromArgb(18, 18, 18);

        // Drop shadow.
        using (var shadowBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
        {
            FillRoundRect(g, shadowBrush, x + 2, y + 2, w, h, cornerRadius);
        }

        // Badge background.
        using (var bgBrush = new SolidBrush(Color.FromArgb(215, bgColor.R, bgColor.G, bgColor.B)))
        {
            FillRoundRect(g, bgBrush, x, y, w, h, cornerRadius);
        }

        // Subtle white border so the badge reads on both light and dark artwork.
        using (var borderPen = new Pen(Color.FromArgb(60, 255, 255, 255), 1.2f))
        {
            DrawRoundRect(g, borderPen, x, y, w, h, cornerRadius);
        }

        if (iconBytes != null)
        {
            // Decode a fresh Bitmap from bytes (thread-safe: each call gets its own instance).
            try
            {
                using var ms   = new MemoryStream(iconBytes);
                using var icon = Image.FromStream(ms);

                // Keep the icon square, sized by the badge height, centred horizontally.
                var iconSize = h - padding * 2;
                var iconX    = x + (w - iconSize) / 2;
                var iconY    = y + padding;
                g.DrawImage(icon, iconX, iconY, iconSize, iconSize);
                return;
            }
            catch
            {
                // Fall through to text fallback.
            }
        }

        // Text fallback — store abbreviation centred in badge.
        StoreFallbacks.TryGetValue(storeId, out var fallback);
        var label = fallback == default
            ? storeId.Substring(0, Math.Min(2, storeId.Length)).ToUpperInvariant()
            : fallback.Label;

        var fontSize = h * 0.36f;
        using var font      = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var textRect = new RectangleF(x, y, w, h);
        var fmt = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(label, font, textBrush, textRect, fmt);
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    private static void FillRoundRect(Graphics g, Brush brush, int x, int y, int w, int h, int r)
    {
        using var path = BuildRoundRectPath(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    private static void DrawRoundRect(Graphics g, Pen pen, int x, int y, int w, int h, int r)
    {
        using var path = BuildRoundRectPath(x, y, w, h, r);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath BuildRoundRectPath(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x,         y,         d, d, 180, 90);
        path.AddArc(x + w - d, y,         d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d,   0, 90);
        path.AddArc(x,         y + h - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    // ── File saving ───────────────────────────────────────────────────────────

    private static void SaveArtwork(Bitmap bitmap, string path, string extension)
    {
        if (extension is ".jpg" or ".jpeg")
        {
            var jpegCodec = ImageCodecInfo
                .GetImageEncoders()
                .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
            if (jpegCodec != null)
            {
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
                bitmap.Save(path, jpegCodec, encoderParams);
                return;
            }
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
