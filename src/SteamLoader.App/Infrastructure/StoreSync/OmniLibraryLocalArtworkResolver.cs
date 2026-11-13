using System.Drawing;
using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal enum OmniLibraryArtworkSlotKind
{
    LibraryCapsule,
    Portrait,
    Hero,
    Logo,
    Icon,
}

/// <summary>
/// Resolves artwork that is already present on the PC. The resolver is deliberately
/// read-only and bounded: it checks explicit file references, Steam's local library
/// cache, ROM sidecars, and a small set of artwork-oriented install subdirectories.
/// It never recursively walks an entire game installation.
/// </summary>
internal sealed class OmniLibraryLocalArtworkResolver
{
    private const int MaximumInstallArtworkCandidates = 256;
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".webp", ".ico"];
    private static readonly string[] ArtworkDirectoryNames =
        ["art", "artwork", "assets", "images", "icons", "resources", "support"];

    private readonly string _steamRoot;
    private Dictionary<string, int?>? _steamAppIdsByTitle;

    public OmniLibraryLocalArtworkResolver(string? steamRoot = null)
    {
        _steamRoot = string.IsNullOrWhiteSpace(steamRoot)
            ? ResolveSteamRoot()
            : NormalizeDirectory(steamRoot);
    }

    public IReadOnlyDictionary<OmniLibraryArtworkSlotKind, string> Resolve(
        StoreSyncArtworkTarget target)
    {
        var resolved = new Dictionary<OmniLibraryArtworkSlotKind, string>();

        RunBestEffort(() => AddExplicitLocalReference(
            resolved,
            OmniLibraryArtworkSlotKind.Portrait,
            target.FallbackPortraitUrl));
        RunBestEffort(() => AddExplicitLocalReference(
            resolved,
            OmniLibraryArtworkSlotKind.Hero,
            target.FallbackHeroUrl));
        RunBestEffort(() => AddExplicitLocalReference(
            resolved,
            OmniLibraryArtworkSlotKind.LibraryCapsule,
            target.FallbackHeroUrl));

        RunBestEffort(() => AddLocalSteamCacheArtwork(resolved, target));
        RunBestEffort(() => AddRomSidecarArtwork(resolved, target.RomPath));
        RunBestEffort(() => AddInstallArtwork(
            resolved,
            target.LocalInstallPath,
            target.LocalExecutablePath));
        return resolved;
    }

    /// <summary>
    /// Provider-owned files on this PC (ORBIT's local provider metadata):
    /// explicit catalog file references, ROM sidecars and bounded install
    /// artwork. These precede every network source.
    /// </summary>
    public IReadOnlyDictionary<OmniLibraryArtworkSlotKind, string> ResolveProviderLocal(
        StoreSyncArtworkTarget target)
    {
        var resolved = new Dictionary<OmniLibraryArtworkSlotKind, string>();
        RunBestEffort(() => AddExplicitLocalReference(
            resolved,
            OmniLibraryArtworkSlotKind.Portrait,
            target.FallbackPortraitUrl));
        RunBestEffort(() => AddExplicitLocalReference(
            resolved,
            OmniLibraryArtworkSlotKind.Hero,
            target.FallbackHeroUrl));
        RunBestEffort(() => AddExplicitLocalReference(
            resolved,
            OmniLibraryArtworkSlotKind.LibraryCapsule,
            target.FallbackHeroUrl));
        RunBestEffort(() => AddRomSidecarArtwork(resolved, target.RomPath));
        RunBestEffort(() => AddInstallArtwork(
            resolved,
            target.LocalInstallPath,
            target.LocalExecutablePath));
        return resolved;
    }

    /// <summary>
    /// Official Steam library files of an installed Steam copy with the same
    /// canonical title (ORBIT's library match). Steam keeps them flat, below
    /// the app folder, or below content-hashed subfolders; all are checked in
    /// ORBIT's order, best first. Ambiguous titles never borrow artwork.
    /// </summary>
    public IReadOnlyDictionary<OmniLibraryArtworkSlotKind, IReadOnlyList<string>> ResolveSteamLibraryMatch(
        StoreSyncArtworkTarget target)
    {
        var resolved = new Dictionary<OmniLibraryArtworkSlotKind, IReadOnlyList<string>>();
        RunBestEffort(() =>
        {
            if (string.IsNullOrWhiteSpace(_steamRoot) || ResolveLocalSteamAppId(target) is not { } appId)
            {
                return;
            }

            foreach (var (slot, names) in SteamLibraryCacheNames)
            {
                var paths = SteamLibraryCachePaths(appId, names)
                    .Where(path => TryResolveLocalReference(path, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (paths.Length > 0)
                {
                    resolved[slot] = paths;
                }
            }
        });
        return resolved;
    }

    private static readonly (OmniLibraryArtworkSlotKind Slot, string[] Names)[] SteamLibraryCacheNames =
    [
        (OmniLibraryArtworkSlotKind.LibraryCapsule, ["header.jpg", "library_header.jpg"]),
        (OmniLibraryArtworkSlotKind.Portrait, ["library_600x900_2x.jpg", "library_600x900.jpg", "library_capsule.jpg"]),
        // The library hero is the purpose-built background; store-page
        // backgrounds are only a later tier.
        (OmniLibraryArtworkSlotKind.Hero, ["library_hero.jpg", "page_bg_generated_v6b.jpg"]),
        (OmniLibraryArtworkSlotKind.Logo, ["logo.png", "library_logo.png", "logo_2x.png"]),
        (OmniLibraryArtworkSlotKind.Icon, ["icon.jpg", "icon.png"]),
    ];

    private IEnumerable<string> SteamLibraryCachePaths(int appId, IReadOnlyList<string> names)
    {
        var cacheRoot = Path.Combine(_steamRoot, "appcache", "librarycache");
        var appRoot = Path.Combine(cacheRoot, appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string[] hashedRoots = [];
        if (Directory.Exists(appRoot))
        {
            try
            {
                hashedRoots = Directory.GetDirectories(appRoot);
            }
            catch
            {
            }
        }

        foreach (var name in names)
        {
            yield return Path.Combine(appRoot, name);
        }
        foreach (var root in hashedRoots)
        {
            foreach (var name in names)
            {
                yield return Path.Combine(root, name);
            }
        }
        foreach (var name in names)
        {
            yield return Path.Combine(cacheRoot, $"{appId}_{name}");
        }
    }

    internal static bool TryResolveLocalReference(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var candidate = value.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                candidate = uri.LocalPath;
            }
            else if (!Path.IsPathRooted(candidate))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (!File.Exists(fullPath) || !IsSupportedImage(fullPath))
            {
                return false;
            }

            var length = new FileInfo(fullPath).Length;
            if (length is < 128 or > 32L * 1024 * 1024)
            {
                return false;
            }

            path = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AddLocalSteamCacheArtwork(
        IDictionary<OmniLibraryArtworkSlotKind, string> resolved,
        StoreSyncArtworkTarget target)
    {
        if (string.IsNullOrWhiteSpace(_steamRoot))
        {
            return;
        }

        var appId = ResolveLocalSteamAppId(target);
        if (!appId.HasValue)
        {
            return;
        }

        foreach (var (slot, names) in SteamLibraryCacheNames)
        {
            if (resolved.ContainsKey(slot))
            {
                continue;
            }
            foreach (var path in SteamLibraryCachePaths(appId.Value, names))
            {
                if (TryResolveLocalReference(path, out var resolvedPath))
                {
                    resolved[slot] = resolvedPath;
                    break;
                }
            }
        }
    }

    private int? ResolveLocalSteamAppId(StoreSyncArtworkTarget target)
    {
        _steamAppIdsByTitle ??= BuildLocalSteamTitleIndex();
        foreach (var title in new[] { target.Title }.Concat(target.SearchHints ?? []))
        {
            var key = NormalizeTitle(title);
            if (!string.IsNullOrWhiteSpace(key) &&
                _steamAppIdsByTitle.TryGetValue(key, out var appId) &&
                appId.HasValue)
            {
                return appId;
            }
        }

        return null;
    }

    private Dictionary<string, int?> BuildLocalSteamTitleIndex()
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamAppsPath in EnumerateSteamAppsPaths())
        {
            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(
                    steamAppsPath,
                    "appmanifest_*.acf",
                    SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var manifest in manifests)
            {
                if (!TryReadSteamManifest(manifest, out var appId, out var title))
                {
                    continue;
                }

                var key = NormalizeTitle(title);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!result.TryAdd(key, appId) && result[key] != appId)
                {
                    // A title shared by multiple installed Steam products is
                    // ambiguous. Do not borrow artwork from either one.
                    result[key] = null;
                }
            }
        }

        return result;
    }

    private IEnumerable<string> EnumerateSteamAppsPaths()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primary = Path.Combine(_steamRoot, "steamapps");
        if (Directory.Exists(primary) && yielded.Add(primary))
        {
            yield return primary;
        }

        var libraryFoldersPath = Path.Combine(primary, "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            yield break;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(libraryFoldersPath);
        }
        catch
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                line,
                "\"path\"\\s+\"(?<path>[^\"]+)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var libraryPath = match.Groups["path"].Value.Replace("\\\\", "\\");
            var steamAppsPath = NormalizeDirectory(Path.Combine(libraryPath, "steamapps"));
            if (Directory.Exists(steamAppsPath) && yielded.Add(steamAppsPath))
            {
                yield return steamAppsPath;
            }
        }
    }

    private static bool TryReadSteamManifest(
        string manifestPath,
        out int appId,
        out string title)
    {
        appId = 0;
        title = string.Empty;
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(manifestPath);
            var idText = fileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase)
                ? fileName["appmanifest_".Length..]
                : string.Empty;
            if (!int.TryParse(
                    idText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out appId) ||
                appId <= 0)
            {
                return false;
            }

            foreach (var line in File.ReadLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"name\"", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var values = System.Text.RegularExpressions.Regex.Matches(
                    trimmed,
                    "\"(?<value>[^\"]*)\"");
                if (values.Count >= 2)
                {
                    title = values[1].Groups["value"].Value.Trim();
                    return !string.IsNullOrWhiteSpace(title);
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static void AddRomSidecarArtwork(
        IDictionary<OmniLibraryArtworkSlotKind, string> resolved,
        string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(romPath);
        var baseName = Path.GetFileNameWithoutExtension(romPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
        {
            return;
        }

        AddFirstExistingStem(
            resolved,
            OmniLibraryArtworkSlotKind.Portrait,
            directory,
            baseName,
            $"{baseName}-cover",
            $"{baseName}_cover",
            $"{baseName}-boxart");
        AddFirstExistingStem(
            resolved,
            OmniLibraryArtworkSlotKind.Hero,
            directory,
            $"{baseName}-hero",
            $"{baseName}_hero",
            $"{baseName}-background");
        AddFirstExistingStem(
            resolved,
            OmniLibraryArtworkSlotKind.Logo,
            directory,
            $"{baseName}-logo",
            $"{baseName}_logo");
    }

    private static void AddInstallArtwork(
        IDictionary<OmniLibraryArtworkSlotKind, string> resolved,
        string? installPath,
        string? executablePath)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSafeDirectory(roots, installPath);
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                AddSafeDirectory(roots, Path.GetDirectoryName(executablePath));
            }
            catch
            {
            }
        }

        var candidates = new List<LocalImageCandidate>();
        foreach (var root in roots)
        {
            foreach (var directory in new[] { root }.Concat(
                         ArtworkDirectoryNames.Select(name => Path.Combine(root, name))))
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(
                                 directory,
                                 "*.*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        if (candidates.Count >= MaximumInstallArtworkCandidates)
                        {
                            break;
                        }
                        if (IsSupportedImage(file) && TryCreateCandidate(file, out var candidate))
                        {
                            candidates.Add(candidate);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        AddBestCandidate(resolved, OmniLibraryArtworkSlotKind.LibraryCapsule, candidates);
        AddBestCandidate(resolved, OmniLibraryArtworkSlotKind.Portrait, candidates);
        AddBestCandidate(resolved, OmniLibraryArtworkSlotKind.Hero, candidates);
        AddBestCandidate(resolved, OmniLibraryArtworkSlotKind.Logo, candidates);
        AddBestCandidate(resolved, OmniLibraryArtworkSlotKind.Icon, candidates);
    }

    private static void AddBestCandidate(
        IDictionary<OmniLibraryArtworkSlotKind, string> resolved,
        OmniLibraryArtworkSlotKind slot,
        IReadOnlyList<LocalImageCandidate> candidates)
    {
        if (resolved.ContainsKey(slot))
        {
            return;
        }

        var selected = candidates
            .Select(candidate => new { Candidate = candidate, Score = ScoreCandidate(candidate, slot) })
            .Where(item => item.Score >= 40)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.Area)
            .Select(item => item.Candidate.Path)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            resolved[slot] = selected;
        }
    }

    private static int ScoreCandidate(
        LocalImageCandidate candidate,
        OmniLibraryArtworkSlotKind slot)
    {
        var name = candidate.Name;
        var ratio = candidate.Height > 0 ? (double)candidate.Width / candidate.Height : 0;
        var score = 0;
        switch (slot)
        {
            case OmniLibraryArtworkSlotKind.Portrait:
                score += ContainsAny(name, "portrait", "poster", "boxart", "box-art", "vertical", "600x900") ? 85 : 0;
                score += name.Contains("cover", StringComparison.OrdinalIgnoreCase) ? 55 : 0;
                score += ratio is >= 0.52 and <= 0.82 ? 35 : -40;
                break;
            case OmniLibraryArtworkSlotKind.LibraryCapsule:
                score += ContainsAny(name, "capsule", "header", "banner", "keyart", "key-art") ? 75 : 0;
                score += ratio is >= 1.6 and <= 2.7 ? 35 : -35;
                break;
            case OmniLibraryArtworkSlotKind.Hero:
                score += ContainsAny(name, "hero", "background", "backdrop", "splash", "keyart", "key-art") ? 75 : 0;
                score += ratio is >= 2.2 and <= 4.2 ? 35 : -35;
                break;
            case OmniLibraryArtworkSlotKind.Logo:
                score += ContainsAny(name, "logo", "clearlogo", "wordmark") ? 100 : -100;
                break;
            case OmniLibraryArtworkSlotKind.Icon:
                score += ContainsAny(name, "icon", "appicon", "gameicon") ? 100 : -100;
                score += ratio is >= 0.85 and <= 1.15 ? 25 : -25;
                break;
        }

        if (ContainsAny(name, "achievement", "avatar", "button", "controller", "screenshot", "thumbnail", "ui-", "ui_"))
        {
            score -= 120;
        }
        if (candidate.Width >= 256 && candidate.Height >= 256)
        {
            score += 10;
        }
        return score;
    }

    private static bool TryCreateCandidate(string path, out LocalImageCandidate candidate)
    {
        candidate = default!;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is < 128 or > 32L * 1024 * 1024)
            {
                return false;
            }

            using var image = Image.FromFile(path);
            candidate = new(
                path,
                Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                image.Width,
                image.Height);
            return image.Width > 0 && image.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void AddExplicitLocalReference(
        IDictionary<OmniLibraryArtworkSlotKind, string> resolved,
        OmniLibraryArtworkSlotKind slot,
        string? value)
    {
        if (!resolved.ContainsKey(slot) && TryResolveLocalReference(value, out var path))
        {
            resolved[slot] = path;
        }
    }

    private static void AddFirstExistingStem(
        IDictionary<OmniLibraryArtworkSlotKind, string> resolved,
        OmniLibraryArtworkSlotKind slot,
        string directory,
        params string[] stems)
    {
        if (resolved.ContainsKey(slot))
        {
            return;
        }

        foreach (var stem in stems)
        {
            foreach (var extension in ImageExtensions)
            {
                var path = Path.Combine(directory, stem + extension);
                if (TryResolveLocalReference(path, out var resolvedPath))
                {
                    resolved[slot] = resolvedPath;
                    return;
                }
            }
        }
    }

    private static void AddSafeDirectory(ISet<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = NormalizeDirectory(path);
            var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(fullPath) ||
                string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            roots.Add(fullPath);
        }
        catch
        {
        }
    }

    private static string ResolveSteamRoot()
    {
        try
        {
            foreach (var value in new[]
                     {
                         Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString(),
                         Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null)?.ToString() is { } steamExe
                             ? Path.GetDirectoryName(steamExe)
                             : null,
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     })
            {
                var normalized = NormalizeDirectory(value);
                if (Directory.Exists(normalized))
                {
                    return normalized;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static void RunBestEffort(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A malformed or locked local cache must never prevent the remote
            // stages from filling the remaining artwork slots.
        }
    }

    private static string NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        try
        {
            return Path.GetFullPath(value.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeTitle(string? value) =>
        OmniLibraryArtworkRules.CanonicalArtworkTitle(value);

    private static bool IsSupportedImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private sealed record LocalImageCandidate(
        string Path,
        string Name,
        int Width,
        int Height)
    {
        public long Area => (long)Width * Height;
    }
}
