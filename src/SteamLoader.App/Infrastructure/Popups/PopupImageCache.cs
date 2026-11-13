using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>
/// Images for popups (achievement icons, friend avatars, game icons). Only
/// Steam CDN hosts are fetched; every image is kept on disk so the next popup
/// for the same friend or game shows instantly.
/// </summary>
internal sealed class PopupImageCache
{
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumFiles = 400;
    private const int DecodeWidth = 160;

    private static readonly string[] AllowedHostSuffixes =
    [
        ".steamstatic.com",
        ".steampowered.com",
        ".steamusercontent.com",
        ".steamcommunity.com",
        ".akamaihd.net",
    ];

    private readonly string _directory;
    private readonly HttpClient _httpClient;
    private int _writesSincePrune;

    public PopupImageCache(string directory, HttpClient httpClient)
    {
        _directory = directory;
        _httpClient = httpClient;
    }

    internal static bool IsAllowedUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !parsed.IsDefaultPort)
        {
            return false;
        }

        var host = parsed.Host.ToLowerInvariant();
        if (!AllowedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal) || host == suffix[1..]))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Decoded, frozen image or null. Never throws.</summary>
    public async Task<BitmapSource?> GetAsync(string? url, string? dataUrl, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = TryDecodeDataUrl(dataUrl) ?? await GetBytesAsync(url, cancellationToken);
            return bytes is null ? null : Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    internal static byte[]? TryDecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl) ||
            !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var payload = dataUrl[(comma + 1)..];
        if (payload.Length > MaximumBytes * 4 / 3 + 4)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<byte[]?> GetBytesAsync(string? url, CancellationToken cancellationToken)
    {
        if (!IsAllowedUrl(url, out var uri))
        {
            return null;
        }

        var path = Path.Combine(_directory, Hash(uri.AbsoluteUri) + ".img");
        if (File.Exists(path))
        {
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
            }

            return await File.ReadAllBytesAsync(path, cancellationToken);
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentLength is > MaximumBytes ||
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == false)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0 || bytes.Length > MaximumBytes)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            if (Interlocked.Increment(ref _writesSincePrune) >= 25)
            {
                Interlocked.Exchange(ref _writesSincePrune, 0);
                Prune();
            }
        }
        catch
        {
            // A cache write failure only costs a download next time.
        }

        return bytes;
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles("*.img")
                .OrderByDescending(file => file.LastAccessTimeUtc)
                .Skip(MaximumFiles)
                .ToList();
            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
