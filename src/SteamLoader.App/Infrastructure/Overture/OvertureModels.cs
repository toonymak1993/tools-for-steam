namespace SteamLoader.App.Infrastructure.Overture;

/// <summary>TFS Overture settings and music assignments (data\overture.json).</summary>
public sealed class OvertureConfiguration
{
    public const int CurrentVersion = 1;
    public const string AmbientKey = "ambient";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Plays the assigned music when a game page opens.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Overall loudness of all TFS Overture music, 0 to 100.</summary>
    public int MasterVolume { get; set; } = 60;

    /// <summary>Seconds music takes to fade in and out.</summary>
    public double FadeSeconds { get; set; } = 1.5;

    /// <summary>Seconds after the game page opened before music starts.</summary>
    public double DelaySeconds { get; set; } = 0.5;

    /// <summary>Looks for a theme on YouTube when a game page has no music yet.</summary>
    public bool AutoFind { get; set; } = true;

    /// <summary>Briefly shows the playing track on the game page.</summary>
    public bool ShowNowPlaying { get; set; } = true;

    /// <summary>Lowers the music while a TFS trailer plays with sound.</summary>
    public bool YieldToTrailers { get; set; } = true;

    /// <summary>Plays the ambient playlist on Big Picture pages that are not game pages.</summary>
    public bool AmbientEnabled { get; set; }

    /// <summary>Folders offered for local music. Empty means the Windows Music folder.</summary>
    public List<string> MusicFolders { get; set; } = [];

    /// <summary>Playlists by Steam app id (decimal) or <see cref="AmbientKey"/>.</summary>
    public Dictionary<string, OverturePlaylist> Playlists { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Every known track by id; playlists refer to these.</summary>
    public Dictionary<string, OvertureTrack> Tracks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Titles where the automatic search found nothing (normalized title → when).</summary>
    public Dictionary<string, DateTimeOffset> AutoFindMisses { get; set; } = new(StringComparer.Ordinal);
}

public sealed class OverturePlaylist
{
    /// <summary>"off" keeps this game silent (and stops the automatic search); null plays.</summary>
    public string? Mode { get; set; }

    public List<string> TrackIds { get; set; } = [];

    /// <summary>Playlist loudness 0 to 100; null is 100.</summary>
    public int? Volume { get; set; }

    /// <summary>Seconds skipped at the start of every track (silent intros).</summary>
    public double? StartSeconds { get; set; }

    /// <summary>Repeats the playlist; null is on.</summary>
    public bool? Loop { get; set; }

    /// <summary>"auto" when the automatic search picked the music, otherwise "manual".</summary>
    public string? Source { get; set; }

    public DateTimeOffset? AssignedAt { get; set; }
}

public sealed class OvertureTrack
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"youtube" (downloaded into TFS' own folder) or "local" (a file of the user).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>YouTube video id, or the full path of a local file.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Downloaded file for YouTube tracks.</summary>
    public string? File { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Channel { get; set; }

    public double? DurationSeconds { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

public sealed record OvertureGlobalSnapshot(
    bool Enabled,
    int MasterVolume,
    double FadeSeconds,
    double DelaySeconds,
    bool AutoFind,
    bool ShowNowPlaying,
    bool YieldToTrailers,
    bool AmbientEnabled,
    IReadOnlyList<string> MusicFolders,
    IReadOnlyList<string> EffectiveMusicFolders,
    IReadOnlyList<uint> SilentAppIds,
    IReadOnlyList<uint> AppIdsWithMusic,
    OvertureYoutubeStatus Youtube,
    OverturePrefetchStatus Prefetch);

public sealed record OverturePrefetchStatus(
    bool Running,
    int Done,
    int Total,
    int Found,
    string? Message);

public sealed record OvertureTrackSnapshot(
    string Id,
    string Kind,
    string Title,
    string? Channel,
    double? DurationSeconds,
    bool Missing,
    string AudioPath);

public sealed record OverturePlaylistSnapshot(
    string Key,
    uint AppId,
    string? Title,
    bool Enabled,
    bool Silent,
    IReadOnlyList<OvertureTrackSnapshot> Tracks,
    int Volume,
    double StartSeconds,
    bool Loop,
    string? Source,
    bool CanAutoFind,
    string? Message,
    OvertureGlobalSnapshot Settings);

public sealed record OvertureGlobalUpdate(
    bool? Enabled,
    int? MasterVolume,
    double? FadeSeconds,
    double? DelaySeconds,
    bool? AutoFind,
    bool? ShowNowPlaying,
    bool? YieldToTrailers,
    bool? AmbientEnabled,
    List<string>? MusicFolders);

public sealed record OverturePlaylistUpdate(
    string? Mode,
    bool? ClearMode,
    int? Volume,
    double? StartSeconds,
    bool? Loop,
    List<string>? TrackIds);

public sealed record OvertureYoutubeStatus(
    bool Installed,
    bool HasJsRuntime,
    string State,
    string? Message,
    DateTimeOffset? InstalledAt);

public sealed record OvertureSearchResult(
    string VideoId,
    string Title,
    string? Channel,
    double? DurationSeconds);

public sealed record OvertureLocalFile(
    string Path,
    string Name,
    string Folder,
    long SizeBytes,
    bool Assigned);

public sealed record OvertureStorageEntry(
    string TrackId,
    string Title,
    long SizeBytes,
    int AssignedCount);

public sealed record OvertureStorageSnapshot(
    long DownloadBytes,
    int DownloadCount,
    long UnusedBytes,
    int UnusedCount,
    IReadOnlyList<OvertureStorageEntry> Largest);
