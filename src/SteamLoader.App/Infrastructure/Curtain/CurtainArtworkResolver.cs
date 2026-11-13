namespace SteamLoader.App.Infrastructure.Curtain;

/// <summary>
/// Finds the images TFS Curtain can show for a game. "Auto" follows Steam:
/// custom grid artwork first (that is where TFS Artworks applies its picks),
/// then Steam's own library cache. Shortcut (non-Steam) games use their
/// 32-bit shortcut app id in the grid folder, exactly like Steam.
/// </summary>
internal static class CurtainArtworkResolver
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    internal static readonly HashSet<string> AllowedCustomExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    // Order = "auto" preference.
    private static readonly (string Id, string Label)[] BackgroundCandidates =
    [
        ("grid-hero", "Hero (custom)"),
        ("cache-hero", "Hero (Steam)"),
        ("grid-portrait", "Cover (custom)"),
        ("cache-portrait", "Cover (Steam)"),
        ("grid-wide", "Banner (custom)"),
        ("cache-header", "Banner (Steam)")
    ];

    private static readonly (string Id, string Label)[] LogoCandidates =
    [
        ("grid-logo", "Logo (custom)"),
        ("cache-logo", "Logo (Steam)")
    ];

    public static IReadOnlyList<CurtainImageCandidate> FindCandidates(string? steamRoot, uint appId)
    {
        var results = new List<CurtainImageCandidate>();
        if (string.IsNullOrWhiteSpace(steamRoot) || appId == 0)
        {
            return results;
        }

        foreach (var (id, label) in BackgroundCandidates)
        {
            var path = FindPath(steamRoot, appId, id);
            if (path is not null)
            {
                results.Add(new CurtainImageCandidate(id, "background", label, path));
            }
        }

        foreach (var (id, label) in LogoCandidates)
        {
            var path = FindPath(steamRoot, appId, id);
            if (path is not null)
            {
                results.Add(new CurtainImageCandidate(id, "logo", label, path));
            }
        }

        return results;
    }

    public static string ResolveBackground(string? steamRoot, uint appId, string? source, string? customPath) =>
        Resolve(steamRoot, appId, source, customPath, BackgroundCandidates.Select(candidate => candidate.Id));

    public static string ResolveLogo(string? steamRoot, uint appId, string? source, string? customPath) =>
        Resolve(steamRoot, appId, source, customPath, LogoCandidates.Select(candidate => candidate.Id));

    public static bool IsUsableCustomImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return AllowedCustomExtensions.Contains(Path.GetExtension(fullPath)) &&
                File.Exists(fullPath) &&
                new FileInfo(fullPath).Length is > 0 and <= 64L * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    private static string Resolve(
        string? steamRoot,
        uint appId,
        string? source,
        string? customPath,
        IEnumerable<string> autoOrder)
    {
        switch (source ?? CurtainImageSources.Auto)
        {
            case CurtainImageSources.None:
                return string.Empty;
            case CurtainImageSources.Custom:
                // A custom file that went missing falls back to Steam's artwork
                // instead of showing an empty curtain.
                if (IsUsableCustomImage(customPath))
                {
                    return Path.GetFullPath(customPath!);
                }

                goto case CurtainImageSources.Auto;
            case CurtainImageSources.Auto:
                if (string.IsNullOrWhiteSpace(steamRoot) || appId == 0)
                {
                    return string.Empty;
                }

                foreach (var id in autoOrder)
                {
                    var path = FindPath(steamRoot, appId, id);
                    if (path is not null)
                    {
                        return path;
                    }
                }

                return string.Empty;
            default:
                return string.IsNullOrWhiteSpace(steamRoot) || appId == 0
                    ? string.Empty
                    : FindPath(steamRoot, appId, source!) ??
                      Resolve(steamRoot, appId, CurtainImageSources.Auto, null, autoOrder);
        }
    }

    internal static string? FindPath(string steamRoot, uint appId, string candidateId)
    {
        var id = appId.ToString();
        return candidateId switch
        {
            "grid-hero" => FirstGridFile(steamRoot, $"{id}_hero"),
            "grid-portrait" => FirstGridFile(steamRoot, $"{id}p"),
            "grid-wide" => FirstGridFile(steamRoot, id),
            "grid-logo" => FirstGridFile(steamRoot, $"{id}_logo"),
            "cache-hero" => FirstLibraryCacheFile(steamRoot, id, "library_hero"),
            "cache-portrait" => FirstLibraryCacheFile(steamRoot, id, "library_600x900", "library_capsule"),
            "cache-header" => FirstLibraryCacheFile(steamRoot, id, "library_header", "header"),
            "cache-logo" => FirstLibraryCacheFile(steamRoot, id, "logo"),
            _ => null
        };
    }

    private static string? FirstGridFile(string steamRoot, string stem)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
        {
            return null;
        }

        try
        {
            // Several Steam accounts can exist; the most recently changed file wins.
            return Directory.EnumerateDirectories(userdata)
                .Select(profile => Path.Combine(profile, "config", "grid"))
                .Where(Directory.Exists)
                .SelectMany(grid => ImageExtensions.Select(extension => Path.Combine(grid, stem + extension)))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstLibraryCacheFile(string steamRoot, string appId, params string[] stems)
    {
        var cacheRoot = Path.Combine(steamRoot, "appcache", "librarycache");
        var appDirectory = Path.Combine(cacheRoot, appId);
        foreach (var stem in stems)
        {
            try
            {
                if (Directory.Exists(appDirectory))
                {
                    // Current Steam keeps each asset in a hashed sub folder
                    // (librarycache\<appid>\<hash>\library_hero.jpg); older builds
                    // put it directly into the app folder.
                    var match = Directory.EnumerateFiles(appDirectory, stem + ".*", SearchOption.AllDirectories)
                        .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetFileNameWithoutExtension(path), stem, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (match is not null)
                    {
                        return match;
                    }
                }

                foreach (var extension in ImageExtensions)
                {
                    var flat = Path.Combine(cacheRoot, $"{appId}_{stem}{extension}");
                    if (File.Exists(flat))
                    {
                        return flat;
                    }
                }
            }
            catch
            {
                // An unreadable cache folder only means "no artwork".
            }
        }

        return null;
    }
}

public sealed record CurtainImageCandidate(string Id, string Kind, string Label, string Path);
