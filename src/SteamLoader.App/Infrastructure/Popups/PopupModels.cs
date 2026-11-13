namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>Where a popup lane sits on the Big Picture monitor.</summary>
public static class PopupPositions
{
    public const string TopLeft = "top-left";
    public const string TopCenter = "top-center";
    public const string TopRight = "top-right";
    public const string BottomLeft = "bottom-left";
    public const string BottomCenter = "bottom-center";
    public const string BottomRight = "bottom-right";

    public static readonly IReadOnlyList<string> All =
        [TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight];

    public static string Normalize(string? value, string fallback) =>
        All.FirstOrDefault(position => string.Equals(position, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    public static bool IsTop(string position) => position.StartsWith("top", StringComparison.Ordinal);
}

/// <summary>
/// Popup categories. Steam's notification types are mapped to these in
/// popups-core.js; the backend only knows the category ids.
/// </summary>
public static class PopupCategories
{
    public const string Achievements = "achievements";
    public const string Messages = "messages";
    public const string Friends = "friends";
    public const string Downloads = "downloads";
    public const string System = "system";
    public const string Community = "community";

    /// <summary>Not a Steam category: the volume indicator in shell mode.</summary>
    public const string Volume = "volume";

    public static readonly IReadOnlyList<string> All =
        [Achievements, Messages, Friends, Downloads, System, Community];

    public static bool IsKnown(string? category) =>
        All.Contains(category ?? string.Empty, StringComparer.Ordinal);

    internal static PopupCategorySettings Defaults(string category) => category switch
    {
        Achievements => new PopupCategorySettings { Position = PopupPositions.TopCenter, DurationSeconds = 6 },
        Messages => new PopupCategorySettings { Position = PopupPositions.BottomRight, DurationSeconds = 5 },
        Friends => new PopupCategorySettings { Position = PopupPositions.BottomRight, DurationSeconds = 4 },
        Downloads => new PopupCategorySettings { Position = PopupPositions.BottomRight, DurationSeconds = 5 },
        System => new PopupCategorySettings { Position = PopupPositions.TopRight, DurationSeconds = 4 },
        _ => new PopupCategorySettings { Position = PopupPositions.BottomRight, DurationSeconds = 5 },
    };
}

/// <summary>Popup designs. Every theme is drawn by <see cref="PopupThemes"/>.</summary>
public static class PopupThemeIds
{
    public const string Tfs = "tfs";
    public const string Pill = "pill";
    public const string Tile = "tile";
    public const string Glow = "glow";
    public const string Light = "light";
    public const string Minimal = "minimal";

    public static readonly IReadOnlyList<string> All = [Tfs, Pill, Tile, Glow, Light, Minimal];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(theme => string.Equals(theme, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Tfs;
}

/// <summary>"theme" plays the theme's own sound, "steam" lets Steam play its sound, "off" is silent.</summary>
public static class PopupSoundModes
{
    public const string Theme = "theme";
    public const string Steam = "steam";
    public const string Off = "off";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Steam => Steam,
        Off => Off,
        _ => Theme,
    };
}

/// <summary>TFS Popups settings (data\popups.json).</summary>
public sealed class PopupConfiguration
{
    public const int CurrentVersion = 1;
    public const int MinimumDurationSeconds = 2;
    public const int MaximumDurationSeconds = 15;
    public const int MaximumSoundVolumePercent = 200;

    public int Version { get; set; } = CurrentVersion;

    public string Theme { get; set; } = PopupThemeIds.Tfs;

    public string SoundMode { get; set; } = PopupSoundModes.Theme;

    /// <summary>0..200 % of the theme sound's normal level.</summary>
    public int SoundVolumePercent { get; set; } = 100;

    /// <summary>Shows a volume indicator when Windows' own flyout is missing (no Explorer).</summary>
    public bool VolumeOverlay { get; set; } = true;

    public string VolumePosition { get; set; } = PopupPositions.TopCenter;

    /// <summary>Uses Steam's own popup instead while a game runs in exclusive fullscreen.</summary>
    public bool SteamInExclusiveFullscreen { get; set; } = true;

    public Dictionary<string, PopupCategorySettings> Categories { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PopupCategorySettings
{
    /// <summary>True: TFS draws this category. False: Steam keeps its own popup.</summary>
    public bool Managed { get; set; } = true;

    public string Position { get; set; } = PopupPositions.BottomRight;

    public int DurationSeconds { get; set; } = 5;
}

public sealed record PopupSettingsUpdate(
    string? Theme,
    string? SoundMode,
    int? SoundVolumePercent,
    bool? VolumeOverlay,
    string? VolumePosition,
    bool? SteamInExclusiveFullscreen,
    string? Category,
    bool? Managed,
    string? Position,
    int? DurationSeconds);

public sealed record PopupStateSnapshot(
    bool Ready,
    int ScalePercent,
    PopupConfiguration Settings,
    IReadOnlyList<string> Themes,
    IReadOnlyList<string> Positions);

/// <summary>One popup, sent by popups-core.js for a Steam notification.</summary>
public sealed class PopupRequest
{
    public string? Category { get; set; }

    /// <summary>Steam's notification type (EClientUINotificationType), for icons and logging.</summary>
    public int Type { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }

    /// <summary>Small extra line, e.g. achievement rarity.</summary>
    public string? Detail { get; set; }

    /// <summary>https image from Steam's CDN, cached locally.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>data:image/... URL when the image only exists inside Steam.</summary>
    public string? ImageData { get; set; }

    /// <summary>Round image (avatars).</summary>
    public bool RoundImage { get; set; }

    /// <summary>0..1 for progress achievements, null otherwise.</summary>
    public double? Progress { get; set; }

    /// <summary>Plays the theme sound (the JS asks Steam's own sound settings first).</summary>
    public bool Sound { get; set; } = true;

    /// <summary>Overrides the category duration (previews), in milliseconds.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Previews: draw with this theme instead of the saved one.</summary>
    public string? Theme { get; set; }

    /// <summary>Previews: draw at this position instead of the saved one.</summary>
    public string? Position { get; set; }

    /// <summary>Previews ignore the exclusive fullscreen fallback.</summary>
    public bool Preview { get; set; }
}

public sealed record PopupShowResult(bool Shown, string? Reason = null);
