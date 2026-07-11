using System.IO;
using System.Windows.Media.Imaging;

namespace ToolsForSteam.Splash;

public static class StartupSplashCoverService
{
    public static async Task<IReadOnlyList<BitmapSource>> LoadAsync(string? steamRoot)
    {
        var paths = await Task.Run(() => CollectCoverPaths(steamRoot)).ConfigureAwait(false);
        return await Task.Run(() => CreateThumbnails(paths)).ConfigureAwait(false);
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

            covers = [.. covers.OrderBy(_ => Random.Shared.Next())];
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
