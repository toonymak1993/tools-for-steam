using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Steam library artwork roles written by OmniLibrary. Portrait, hero, logo and
/// icon use ORBIT's vertical, horizontal, logo and icon rules unchanged. The
/// wide library capsule (920x430) is a Steam-only slot without an ORBIT twin;
/// it follows ORBIT's title-bearing Store header rules at Steam's own size.
/// </summary>
internal enum OmniLibraryArtworkOrientation
{
    Capsule,
    Portrait,
    Hero,
    Logo,
    Icon,
}

internal enum OmniLibraryArtworkQuality
{
    Low,
    High,
}

internal enum LibretroArtworkFolder
{
    Named_Boxarts,
    Named_Snaps,
    Named_Titles,
}

/// <summary>
/// Title matching and quality rules ported from ORBIT
/// (src/shared/artworkMatching.ts, src/main/imageCache.ts validateImage,
/// src/main/steamGridDbSearch.ts). Keep both implementations in step: a
/// wrong cover is worse than no cover, so automatic sources only accept exact
/// normalized titles and every image is checked for size and aspect ratio.
/// </summary>
internal static class OmniLibraryArtworkRules
{
    private static readonly IReadOnlyDictionary<string, string> RomanTitleNumerals =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ii"] = "2",
            ["iii"] = "3",
            ["iv"] = "4",
            ["v"] = "5",
            ["vi"] = "6",
            ["vii"] = "7",
            ["viii"] = "8",
            ["ix"] = "9",
            ["x"] = "10",
        };

    private static readonly Regex TrademarkPattern = new("[™®©]", RegexOptions.Compiled);
    private static readonly Regex NonLetterOrDigitPattern = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex ApostrophePattern = new("['’‘`´]", RegexOptions.Compiled);
    private static readonly Regex EditionSuffixPattern = new(
        @"\s*(?:[-–—:]\s*)?(?:(?:complete|ultimate|gold)\s+edition|game\s+of\s+the\s+year(?:\s+edition)?|goty(?:\s+edition)?|edition)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string NormalizeTitle(string? value)
    {
        var withoutMarks = TrademarkPattern.Replace(value ?? string.Empty, string.Empty)
            .Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(withoutMarks.Length);
        foreach (var character in withoutMarks)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is not (UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark))
            {
                builder.Append(character);
            }
        }

        return NonLetterOrDigitPattern.Replace(builder.ToString(), " ")
            .Trim()
            .ToLowerInvariant();
    }

    /// <summary>
    /// Canonical exact-match key. Punctuation, trademarks and accents are
    /// ignored, while standalone series numerals such as "II" and "2" match.
    /// </summary>
    public static string CanonicalArtworkTitle(string? value) =>
        string.Join(
            ' ',
            NormalizeTitle(value)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => RomanTitleNumerals.TryGetValue(token, out var numeral) ? numeral : token));

    public static string SteamGridDbSearchKey(string? value) =>
        CanonicalArtworkTitle(ApostrophePattern.Replace(value ?? string.Empty, string.Empty));

    public static string StripSteamGridDbEditionWords(string? value) =>
        Regex.Replace(EditionSuffixPattern.Replace(value ?? string.Empty, " "), @"\s+", " ").Trim();

    /// <summary>
    /// Automatic SteamGridDB matches accept only one exact normalized title, or
    /// one exact match after removing an explicit edition suffix. Broader fuzzy
    /// search belongs in the manual TFS Artworks picker.
    /// </summary>
    public static T? SelectSteamGridDbGame<T>(
        string query,
        IEnumerable<T>? candidates,
        Func<T, string?> nameOf)
        where T : class
    {
        var list = candidates?.ToArray() ?? [];
        var rawKey = SteamGridDbSearchKey(query);
        if (list.Length == 0 || string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        var rawMatches = list.Where(candidate => SteamGridDbSearchKey(nameOf(candidate)) == rawKey).ToArray();
        if (rawMatches.Length == 1)
        {
            return rawMatches[0];
        }
        if (rawMatches.Length > 1)
        {
            return null;
        }

        var comparableKey = SteamGridDbSearchKey(StripSteamGridDbEditionWords(query));
        if (string.IsNullOrWhiteSpace(comparableKey))
        {
            return null;
        }

        var comparableMatches = list
            .Where(candidate =>
                SteamGridDbSearchKey(StripSteamGridDbEditionWords(nameOf(candidate))) == comparableKey)
            .ToArray();
        return comparableMatches.Length == 1 ? comparableMatches[0] : null;
    }

    private static double TargetRatio(OmniLibraryArtworkOrientation orientation) => orientation switch
    {
        OmniLibraryArtworkOrientation.Portrait => 2d / 3d,
        OmniLibraryArtworkOrientation.Hero => 16d / 9d,
        OmniLibraryArtworkOrientation.Capsule => 920d / 430d,
        OmniLibraryArtworkOrientation.Logo => 3d,
        _ => 1d,
    };

    private static (int Width, int Height) TargetDimensions(OmniLibraryArtworkOrientation orientation) =>
        orientation switch
        {
            OmniLibraryArtworkOrientation.Portrait => (600, 900),
            // Steam's 1920x620 library hero keeps ~1100 useful pixels after a
            // 16:9 fill crop and must outrank larger gameplay screenshots.
            OmniLibraryArtworkOrientation.Hero => (1000, 500),
            OmniLibraryArtworkOrientation.Capsule => (920, 430),
            OmniLibraryArtworkOrientation.Logo => (600, 200),
            _ => (128, 128),
        };

    private static double MinimumHighQualityAspectSuitability(OmniLibraryArtworkOrientation orientation) =>
        orientation switch
        {
            OmniLibraryArtworkOrientation.Portrait => 0.75,
            OmniLibraryArtworkOrientation.Hero => 0.55,
            OmniLibraryArtworkOrientation.Capsule => 0.75,
            OmniLibraryArtworkOrientation.Logo => 0.32,
            _ => 0.65,
        };

    private static double AspectSuitability(int width, int height, OmniLibraryArtworkOrientation orientation)
    {
        var ratio = (double)width / height;
        var targetRatio = TargetRatio(orientation);
        return Math.Min(ratio / targetRatio, targetRatio / ratio);
    }

    /// <summary>
    /// High quality only when the visible crop still reaches the target
    /// resolution and the aspect mismatch is not extreme.
    /// </summary>
    public static OmniLibraryArtworkQuality AutomaticQuality(
        int width,
        int height,
        OmniLibraryArtworkOrientation orientation)
    {
        if (width <= 0 || height <= 0)
        {
            return OmniLibraryArtworkQuality.Low;
        }

        var targetRatio = TargetRatio(orientation);
        var sourceRatio = (double)width / height;
        var visibleWidth = sourceRatio >= targetRatio ? height * targetRatio : width;
        var visibleHeight = sourceRatio >= targetRatio ? height : width / targetRatio;
        var target = TargetDimensions(orientation);
        // The epsilon absorbs float error for sources exactly at the target ratio.
        return visibleWidth + 1e-6 >= target.Width &&
               visibleHeight + 1e-6 >= target.Height &&
               AspectSuitability(width, height, orientation) >=
               MinimumHighQualityAspectSuitability(orientation)
            ? OmniLibraryArtworkQuality.High
            : OmniLibraryArtworkQuality.Low;
    }

    /// <summary>
    /// Ranks fallbacks by how naturally they fill the slot. Aspect suitability
    /// outweighs raw pixels: a square cover is a better poster than a 4K
    /// widescreen screenshot.
    /// </summary>
    public static double AutomaticScore(int width, int height, OmniLibraryArtworkOrientation orientation)
    {
        if (width <= 0 || height <= 0)
        {
            return double.NegativeInfinity;
        }

        var target = TargetDimensions(orientation);
        var resolutionSuitability = Math.Min(
            1,
            Math.Sqrt((double)width * height / ((double)target.Width * target.Height)));
        return AspectSuitability(width, height, orientation) * 1_000 + resolutionSuitability * 100;
    }

    /// <summary>
    /// ORBIT's strict per-orientation acceptance (imageCache.ts validateImage).
    /// Images outside these bounds are only usable as explicit provider
    /// fallbacks, never as a silent cross-orientation crop.
    /// </summary>
    public static bool MatchesOrientation(int width, int height, OmniLibraryArtworkOrientation orientation)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var ratio = (double)width / height;
        return orientation switch
        {
            OmniLibraryArtworkOrientation.Icon =>
                ratio is >= 0.65 and <= 1.45 && width >= 32 && height >= 32,
            OmniLibraryArtworkOrientation.Logo =>
                ratio is >= 0.65 and <= 12 && width >= 64 && height >= 16,
            OmniLibraryArtworkOrientation.Portrait =>
                ratio is >= 0.55 and <= 0.8 && width >= 300 && height >= 450,
            OmniLibraryArtworkOrientation.Capsule =>
                ratio is >= 1.6 and <= 2.8 && width >= 460 && height >= 215,
            _ => ratio is >= 1.45 and <= 3.6 && width >= 600 && height >= 250,
        };
    }

    /// <summary>Cross-orientation provider art must still be a real image, not a thumbnail.</summary>
    public static bool IsUsableFallback(int width, int height, OmniLibraryArtworkOrientation orientation) =>
        orientation is not (OmniLibraryArtworkOrientation.Icon or OmniLibraryArtworkOrientation.Logo) &&
        width >= 128 &&
        height >= 128;

    /// <summary>Largest persisted raster per role (ORBIT artworkResolution.ts).</summary>
    public static (int Width, int Height) DisplayCacheLimit(OmniLibraryArtworkOrientation orientation) =>
        orientation switch
        {
            OmniLibraryArtworkOrientation.Portrait => (768, 1152),
            OmniLibraryArtworkOrientation.Hero => (1920, 1080),
            OmniLibraryArtworkOrientation.Capsule => (1840, 860),
            OmniLibraryArtworkOrientation.Logo => (1200, 400),
            _ => (512, 512),
        };

    /// <summary>
    /// Libretro folders are semantic roles. A title screen or gameplay snap
    /// must never become a portrait cover.
    /// </summary>
    public static IReadOnlyList<LibretroArtworkFolder> LibretroFolders(OmniLibraryArtworkOrientation orientation) =>
        orientation switch
        {
            OmniLibraryArtworkOrientation.Portrait => [LibretroArtworkFolder.Named_Boxarts],
            OmniLibraryArtworkOrientation.Hero or OmniLibraryArtworkOrientation.Capsule =>
            [
                LibretroArtworkFolder.Named_Snaps,
                LibretroArtworkFolder.Named_Titles,
                LibretroArtworkFolder.Named_Boxarts,
            ],
            _ => [],
        };

    private static readonly HashSet<string> ReleaseLanguageCodes = new(StringComparer.Ordinal)
    {
        "ar", "cs", "da", "de", "en", "es", "fi", "fr", "he", "hu", "it", "ja", "jp", "ko", "nl", "no", "pl",
        "pt", "ru", "sv", "tr", "zh",
    };

    private static readonly IReadOnlyDictionary<string, string> RegionAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["usa"] = "usa",
            ["u"] = "usa",
            ["canada"] = "usa",
            ["europe"] = "europe",
            ["e"] = "europe",
            ["germany"] = "europe",
            ["france"] = "europe",
            ["italy"] = "europe",
            ["spain"] = "europe",
            ["netherlands"] = "europe",
            ["sweden"] = "europe",
            ["australia"] = "europe",
            ["pal"] = "europe",
            ["japan"] = "japan",
            ["j"] = "japan",
            ["world"] = "world",
        };

    private static readonly Regex RegionTokenSeparator = new(@"\s*(?:,|/|\+|-)\s*", RegexOptions.Compiled);

    private static HashSet<string> ArtworkRegions(string? value)
    {
        var regions = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(value ?? string.Empty, @"\(([^()]*)\)"))
        {
            var normalized = match.Groups[1].Value.Trim().ToLowerInvariant();
            if (Regex.IsMatch(normalized, "^[jue]{2,3}$"))
            {
                foreach (var code in normalized.Distinct())
                {
                    if (RegionAliases.TryGetValue(code.ToString(), out var compactRegion))
                    {
                        regions.Add(compactRegion);
                    }
                }
                if (normalized == "jue")
                {
                    regions.Add("world");
                }
                continue;
            }

            foreach (var token in RegionTokenSeparator.Split(normalized))
            {
                if (RegionAliases.TryGetValue(token, out var region))
                {
                    regions.Add(region);
                }
            }
        }
        return regions;
    }

    private static bool IsRegionMetadataTag(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (Regex.IsMatch(normalized, "^[jue]{2,3}$"))
        {
            return true;
        }
        var tokens = RegionTokenSeparator.Split(normalized);
        return tokens.Length > 0 && tokens.All(token => RegionAliases.ContainsKey(token) || token == "ntsc");
    }

    private static bool IsReleaseMetadataTag(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (IsRegionMetadataTag(normalized) ||
            Regex.IsMatch(
                normalized,
                @"^(?:rev(?:ision)?(?:\s+|[-_.])(?:[a-z]|\d[\w.-]*)|v(?:ersion)?\s*[-_.]?\s*\d[\w.-]*)$") ||
            Regex.IsMatch(normalized, @"^m\d+$") ||
            Regex.IsMatch(normalized, @"^(?:disc|disk|side)\s*[a-z0-9]+(?:\s+of\s+\d+)?$"))
        {
            return true;
        }
        var languageCodes = Regex.Split(normalized, @"\s*,\s*");
        return languageCodes.Length > 0 && languageCodes.All(ReleaseLanguageCodes.Contains);
    }

    private static string StripReleaseMetadata(string value)
    {
        var withoutTags = Regex.Replace(
            value,
            @"\s*\(([^()]*)\)",
            match => IsReleaseMetadataTag(match.Groups[1].Value) ? " " : match.Value);
        return Regex.Replace(withoutTags, @"\s*\[((?:!|[abho][0-9]*))\]", " ", RegexOptions.IgnoreCase);
    }

    private static string MoveTrailingArticle(string value)
    {
        var trimmed = value.Trim();
        var match = Regex.Match(trimmed, @"^(.+),\s*(the|a|an)(?=\s*(?:-|:)|$)(.*)$", RegexOptions.IgnoreCase);
        return match.Success
            ? $"{match.Groups[2].Value} {match.Groups[1].Value}{match.Groups[3].Value}"
            : trimmed;
    }

    private static string NormalizedThumbnailTitle(string value, bool stripTags) =>
        NormalizeTitle(MoveTrailingArticle(stripTags ? StripReleaseMetadata(value) : value));

    private static bool HasIdentityQualifier(string value)
    {
        var stripped = StripReleaseMetadata(value);
        return stripped.IndexOfAny(['(', ')', '[', ']']) >= 0;
    }

    private static int PreferredRegionBonus(string fileName, string? rawRomName, string language)
    {
        var candidateRegions = ArtworkRegions(fileName);
        var rawRegions = ArtworkRegions(rawRomName);
        var score = candidateRegions.Any(rawRegions.Contains) ? 60 : 0;
        if (language is "de" or "es" or "fr" or "ru" && candidateRegions.Contains("europe"))
        {
            score += 35;
        }
        else if (candidateRegions.Contains("usa"))
        {
            score += 30;
        }
        else if (candidateRegions.Contains("world"))
        {
            score += 25;
        }
        else if (candidateRegions.Contains("japan"))
        {
            score += 5;
        }
        else
        {
            score += 10;
        }
        return score;
    }

    /// <summary>Exact/short-name matching modeled after RetroArch's own thumbnail rules.</summary>
    public static string? MatchLibretroThumbnail(
        IEnumerable<string> files,
        string gameName,
        string? rawRomName,
        string language)
    {
        // A non-release qualifier (hack, beta, prototype) is identity evidence:
        // the cleaned display title must not route that ROM to retail artwork.
        var sourceNames = new[] { rawRomName }
            .Concat(!string.IsNullOrWhiteSpace(rawRomName) && HasIdentityQualifier(rawRomName)
                ? []
                : [gameName])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        var fullNames = sourceNames.Select(value => NormalizedThumbnailTitle(value, false)).ToHashSet(StringComparer.Ordinal);
        var shortNames = sourceNames.Select(value => NormalizedThumbnailTitle(value, true)).ToHashSet(StringComparer.Ordinal);
        string? bestFile = null;
        var bestScore = int.MinValue;
        foreach (var fileName in files)
        {
            var title = Regex.Replace(fileName, @"\.[a-z0-9]{1,8}$", string.Empty, RegexOptions.IgnoreCase);
            var full = NormalizedThumbnailTitle(title, false);
            var shortName = NormalizedThumbnailTitle(title, true);
            var exact = fullNames.Contains(full);
            var shortExact = shortNames.Contains(shortName);
            if (!exact && !shortExact)
            {
                continue;
            }

            var score = (exact ? 1_000 : 800) +
                PreferredRegionBonus(fileName, rawRomName, language) -
                Math.Min(10, Math.Max(0, full.Length - shortName.Length) / 10);
            if (score > bestScore)
            {
                bestScore = score;
                bestFile = fileName;
            }
        }
        return bestFile;
    }

    /// <summary>
    /// Compact key for catalogs such as GameTDB whose titles use display
    /// punctuation while ROM files use No-Intro or lowercase scene names.
    /// </summary>
    public static string CompactCatalogTitleKey(string? value)
    {
        var prepared = (value ?? string.Empty).Replace('_', ' ').Trim();
        if (prepared == prepared.ToLowerInvariant() && Regex.IsMatch(prepared, "^[a-z0-9]{1,4}-[a-z]"))
        {
            prepared = Regex.Replace(prepared, "^[a-z0-9]{1,4}-", string.Empty);
        }
        prepared = Regex.Replace(prepared, @"\s*\([^()]*\)|\s*\[[^\]]*\]", " ");
        return CanonicalArtworkTitle(MoveTrailingArticle(prepared)).Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public static bool IsTransientStatus(int status) =>
        status is 408 or 425 or 429 || status >= 500;
}

/// <summary>
/// Per-source circuit breaker (ORBIT artworkNetworkPolicy.ts). Two consecutive
/// transport or server failures open a source so one outage cannot occupy the
/// whole artwork queue; a success or a confirmed missing asset closes it.
/// </summary>
internal static class OmniLibraryArtworkNetworkPolicy
{
    private const int FailureThreshold = 2;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(30);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CircuitState> Circuits = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryBegin(string scope)
    {
        lock (Gate)
        {
            if (!Circuits.TryGetValue(scope, out var circuit) || circuit.ConsecutiveFailures < FailureThreshold)
            {
                return true;
            }
            if (circuit.RetryAtUtc > DateTime.UtcNow || circuit.ProbeInFlight)
            {
                return false;
            }
            circuit.ProbeInFlight = true;
            return true;
        }
    }

    public static void RecordAvailable(string scope)
    {
        lock (Gate)
        {
            Circuits.Remove(scope);
        }
    }

    public static void RecordUnavailable(string scope)
    {
        lock (Gate)
        {
            var failures = (Circuits.TryGetValue(scope, out var previous) ? previous.ConsecutiveFailures : 0) + 1;
            var exponent = Math.Max(0, failures - FailureThreshold);
            var backoff = TimeSpan.FromTicks(Math.Min(
                BaseBackoff.Ticks * (long)Math.Pow(2, Math.Min(exponent, 10)),
                MaximumBackoff.Ticks));
            Circuits[scope] = new CircuitState
            {
                ConsecutiveFailures = failures,
                RetryAtUtc = failures >= FailureThreshold ? DateTime.UtcNow + backoff : DateTime.MinValue,
            };
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Circuits.Clear();
        }
    }

    private sealed class CircuitState
    {
        public int ConsecutiveFailures { get; init; }
        public DateTime RetryAtUtc { get; init; }
        public bool ProbeInFlight { get; set; }
    }
}
