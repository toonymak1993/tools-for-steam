namespace SteamLoader.App.Infrastructure.Trailers;

/// <summary>Global TFS Trailers settings (data\trailers.json).</summary>
public sealed class TrailerConfiguration
{
    public const int CurrentVersion = 1;
    public const int DefaultDelaySeconds = 3;
    public const string DefaultQuality = "1080";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Plays a trailer in the game page header when a game page opens.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds Steam's own header artwork stays before the trailer fades in.</summary>
    public int DelaySeconds { get; set; } = DefaultDelaySeconds;

    /// <summary>Highest video height to stream: 1080, 720 or 480.</summary>
    public string Quality { get; set; } = DefaultQuality;

    /// <summary>Moves the game logo to the lower left while the trailer plays.</summary>
    public bool MoveLogo { get; set; } = true;

    /// <summary>Starts trailers with sound instead of muted (X toggles either way).</summary>
    public bool StartWithSound { get; set; }

    /// <summary>Also looks up trailers for non-Steam games through a Steam store match.</summary>
    public bool MatchShortcuts { get; set; } = true;

    public Dictionary<string, TrailerGameSettings> Games { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Remembered Steam store matches for non-Steam titles (normalized title → app id, 0 = none).</summary>
    public Dictionary<string, TrailerTitleMatch> TitleMatches { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Per-game overrides. Null means "use the default".</summary>
public sealed class TrailerGameSettings
{
    /// <summary>"off" hides trailers for this game; null or "auto" plays them.</summary>
    public string? Mode { get; set; }

    /// <summary>A specific Steam video instead of the first highlighted one.</summary>
    public long? MovieId { get; set; }

    /// <summary>Steam app whose trailers a non-Steam game uses (overrides the automatic match).</summary>
    public uint? SteamAppId { get; set; }

    /// <summary>Seconds cut from the start of the video (intro).</summary>
    public double? TrimStartSeconds { get; set; }

    /// <summary>Seconds cut from the end of the video (outro).</summary>
    public double? TrimEndSeconds { get; set; }
}

public sealed class TrailerTitleMatch
{
    public uint SteamAppId { get; set; }

    public string? MatchedName { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}

public sealed record TrailerMovie(
    long Id,
    string Name,
    string Thumbnail,
    string DashH264,
    string HlsH264,
    bool Highlight);

public sealed record TrailerGlobalSnapshot(
    bool Enabled,
    int DelaySeconds,
    string Quality,
    bool MoveLogo,
    bool StartWithSound,
    bool MatchShortcuts,
    IReadOnlyList<uint> DisabledAppIds);

public sealed record TrailerGameSnapshot(
    uint AppId,
    string Title,
    bool IsShortcut,
    bool Enabled,
    uint SteamAppId,
    string MatchSource,
    string? MatchedName,
    IReadOnlyList<TrailerMovie> Movies,
    long? SelectedMovieId,
    TrailerGameSettings Overrides,
    TrailerGlobalSnapshot Settings,
    string? Message);

public sealed record TrailerGlobalUpdate(
    bool? Enabled,
    int? DelaySeconds,
    string? Quality,
    bool? MoveLogo,
    bool? StartWithSound,
    bool? MatchShortcuts);

public sealed record TrailerStoreSearchResult(uint AppId, string Name);
