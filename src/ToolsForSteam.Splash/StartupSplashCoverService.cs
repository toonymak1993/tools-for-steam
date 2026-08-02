using System.IO;
using System.Windows.Media.Imaging;

namespace ToolsForSteam.Splash;

public static class StartupSplashCoverService
{
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static string _cachedSteamRoot = string.Empty;
    private static DateTimeOffset _cachedAt;
    private static IReadOnlyList<BitmapSource> _cachedThumbnails = [];

    public static async Task<IReadOnlyList<BitmapSource>> LoadAsync(string? steamRoot)
    {
        var normalizedRoot = NormalizeRoot(steamRoot);
        await CacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedThumbnails.Count > 0 &&
                string.Equals(_cachedSteamRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cachedThumbnails;
            }

            var paths = await Task.Run(() => CollectCoverPaths(normalizedRoot)).ConfigureAwait(false);
            var thumbnails = await Task.Run(() => CreateThumbnails(paths)).ConfigureAwait(false);
            _cachedSteamRoot = normalizedRoot;
            _cachedAt = DateTimeOffset.UtcNow;
            _cachedThumbnails = thumbnails;
            return thumbnails;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static IReadOnlyList<BitmapSource> CreateThumbnails(IReadOnlyList<string> paths)
    {
        const int cellWidth = 160;
        var results = new List<BitmapSource>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = cellWidth;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();
                results.Add(bitmap);
            }
            catch
            {
            }
        }

        return results;
    }

    private static IReadOnlyList<string> CollectCoverPaths(string? steamRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(steamRoot))
            {
                return [];
            }

            var gridDirectory = FindSteamGridDirectory(steamRoot);
            List<string> covers = [];
            if (gridDirectory is not null)
            {
                var portraitCovers = Directory.EnumerateFiles(gridDirectory, "*p.jpg")
                    .Concat(Directory.EnumerateFiles(gridDirectory, "*p.png"))
                    .Where(path =>
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        return name.Length >= 2 && name[^1] == 'p' && name[..^1].All(char.IsDigit);
                    })
                    .ToList();

                covers = portraitCovers.Count >= 5
                    ? portraitCovers
                    : Directory.EnumerateFiles(gridDirectory, "*.jpg")
                        .Concat(Directory.EnumerateFiles(gridDirectory, "*.png"))
                        .Where(path =>
                        {
                            var name = Path.GetFileNameWithoutExtension(path);
                            return !name.EndsWith("_hero", StringComparison.OrdinalIgnoreCase) &&
                                !name.EndsWith("_logo", StringComparison.OrdinalIgnoreCase) &&
                                !name.EndsWith("_icon", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
            }
            else
            {
                var cacheDirectory = Path.Combine(steamRoot, "appcache", "librarycache");
                if (Directory.Exists(cacheDirectory))
                {
                    covers = Directory.EnumerateFiles(cacheDirectory, "*_library_600x900.jpg")
                        .Concat(Directory.EnumerateFiles(cacheDirectory, "*_library_600x900.png"))
                        .ToList();

                    if (covers.Count < 5)
                    {
                        covers = Directory.EnumerateFiles(cacheDirectory, "*.jpg")
                            .Concat(Directory.EnumerateFiles(cacheDirectory, "*.png"))
                            .Where(path => !path.EndsWith("_logo.png", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                }
            }

            if (covers.Count == 0)
            {
                return [];
            }

            Shuffle(covers, HashCode.Combine(DateTime.UtcNow.Date, steamRoot));
            const int targetCount = 84;
            if (covers.Count < targetCount)
            {
                var repeated = new List<string>(targetCount);
                while (repeated.Count < targetCount)
                {
                    repeated.AddRange(covers);
                }

                covers = repeated;
            }

            return covers.Take(targetCount).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void Shuffle<T>(IList<T> items, int seed)
    {
        var random = new Random(seed);
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private static string NormalizeRoot(string? steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(steamRoot);
        }
        catch
        {
            return steamRoot.Trim();
        }
    }

    private static string? FindSteamGridDirectory(string steamRoot)
    {
        var userdataDirectory = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdataDirectory))
        {
            return null;
        }

        return Directory.EnumerateDirectories(userdataDirectory)
            .Select(path => Path.Combine(path, "config", "grid"))
            .Where(Directory.Exists)
            .OrderByDescending(path =>
            {
                try
                {
                    return Directory.EnumerateFiles(path).Count();
                }
                catch
                {
                    return 0;
                }
            })
            .FirstOrDefault();
    }
}
