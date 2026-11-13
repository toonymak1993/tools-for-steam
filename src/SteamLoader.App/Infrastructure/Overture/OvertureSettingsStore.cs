using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.Overture;

/// <summary>Persists TFS Overture in data\overture.json (atomic write, cached read).</summary>
public sealed partial class OvertureSettingsStore
{
    internal const int MaxTracksPerPlaylist = 50;
    internal const int MaxMusicFolders = 8;
    internal const int MaxRememberedMisses = 3000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _settingsPath;
    private readonly object _gate = new();
    private OvertureConfiguration? _cached;
    private DateTime _cachedWriteTimeUtc;

    public OvertureSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public string SettingsPath => _settingsPath;

    public OvertureConfiguration Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new OvertureConfiguration();
                }

                var writeTime = File.GetLastWriteTimeUtc(_settingsPath);
                if (_cached is not null && writeTime == _cachedWriteTimeUtc)
                {
                    return Clone(_cached);
                }

                var configuration = JsonSerializer.Deserialize<OvertureConfiguration>(
                    File.ReadAllText(_settingsPath),
                    JsonOptions) ?? new OvertureConfiguration();
                Normalize(configuration);
                _cached = configuration;
                _cachedWriteTimeUtc = writeTime;
                return Clone(configuration);
            }
            catch
            {
                // A damaged file must not take the assignments' audio files with it;
                // keep a copy next to it before the next save replaces it.
                TryBackupDamagedFile();
                return new OvertureConfiguration();
            }
        }
    }

    public void Save(OvertureConfiguration configuration)
    {
        lock (_gate)
        {
            Normalize(configuration);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = $"{_settingsPath}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            _cached = Clone(configuration);
            _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(_settingsPath);
        }
    }

    /// <summary>Applies <paramref name="change"/> to the stored configuration under one lock.</summary>
    public OvertureConfiguration Update(Action<OvertureConfiguration> change)
    {
        lock (_gate)
        {
            var configuration = Load();
            change(configuration);
            Save(configuration);
            return Clone(configuration);
        }
    }

    internal static OvertureConfiguration Clone(OvertureConfiguration configuration) =>
        JsonSerializer.Deserialize<OvertureConfiguration>(
            JsonSerializer.Serialize(configuration, JsonOptions),
            JsonOptions) ?? new OvertureConfiguration();

    internal static void Normalize(OvertureConfiguration configuration)
    {
        configuration.Version = OvertureConfiguration.CurrentVersion;
        configuration.MasterVolume = Math.Clamp(configuration.MasterVolume, 0, 100);
        configuration.FadeSeconds = ClampFinite(configuration.FadeSeconds, 0, 5, 1.5);
        configuration.DelaySeconds = ClampFinite(configuration.DelaySeconds, 0, 10, 0.5);
        configuration.MusicFolders = (configuration.MusicFolders ?? [])
            .Select(folder => folder?.Trim() ?? string.Empty)
            .Where(folder => folder.Length > 0 && Path.IsPathFullyQualified(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMusicFolders)
            .ToList();

        configuration.Tracks = new Dictionary<string, OvertureTrack>(
            (configuration.Tracks ?? new Dictionary<string, OvertureTrack>())
                .Where(entry => entry.Value is not null && IsValidTrack(entry.Key, entry.Value))
                .Select(entry => KeyValuePair.Create(entry.Key, entry.Value)),
            StringComparer.Ordinal);

        var tracks = configuration.Tracks;
        configuration.Playlists = new Dictionary<string, OverturePlaylist>(
            (configuration.Playlists ?? new Dictionary<string, OverturePlaylist>())
                .Where(entry => IsValidKey(entry.Key) && entry.Value is not null)
                .Select(entry => KeyValuePair.Create(entry.Key, NormalizePlaylist(entry.Value, tracks)))
                .Where(entry => !IsEmpty(entry.Value)),
            StringComparer.Ordinal);

        configuration.AutoFindMisses = new Dictionary<string, DateTimeOffset>(
            (configuration.AutoFindMisses ?? new Dictionary<string, DateTimeOffset>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .OrderByDescending(entry => entry.Value)
                .Take(MaxRememberedMisses),
            StringComparer.Ordinal);
    }

    /// <summary>"ambient" or a Steam app id (shortcuts included) in plain decimal.</summary>
    public static bool IsValidKey(string? key) =>
        key == OvertureConfiguration.AmbientKey ||
        (key is { Length: > 0 and <= 10 } &&
         key[0] != '0' &&
         uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) &&
         appId > 0);

    public static bool IsValidVideoId(string? videoId) =>
        videoId is not null && VideoIdPattern().IsMatch(videoId);

    public static string YoutubeTrackId(string videoId) => $"yt-{videoId}";

    internal static bool IsValidTrack(string key, OvertureTrack track)
    {
        if (!string.Equals(key, track.Id, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(track.Source))
        {
            return false;
        }

        return track.Kind switch
        {
            "youtube" => IsValidVideoId(track.Source) && key == YoutubeTrackId(track.Source),
            "local" => key.StartsWith("local-", StringComparison.Ordinal) && Path.IsPathFullyQualified(track.Source),
            _ => false
        };
    }

    internal static OverturePlaylist NormalizePlaylist(OverturePlaylist playlist, IReadOnlyDictionary<string, OvertureTrack> tracks)
    {
        var mode = playlist.Mode?.Trim().ToLowerInvariant();
        var source = playlist.Source?.Trim().ToLowerInvariant();
        return new OverturePlaylist
        {
            Mode = mode == "off" ? "off" : null,
            TrackIds = (playlist.TrackIds ?? [])
                .Where(id => id is not null && tracks.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxTracksPerPlaylist)
                .ToList(),
            Volume = playlist.Volume is { } volume && volume < 100 ? Math.Clamp(volume, 0, 100) : null,
            StartSeconds = playlist.StartSeconds is { } start && double.IsFinite(start) && start > 0
                ? Math.Round(Math.Min(start, 30), 1)
                : null,
            Loop = playlist.Loop == false ? false : null,
            Source = source is "auto" or "manual" ? source : null,
            AssignedAt = playlist.AssignedAt,
        };
    }

    internal static bool IsEmpty(OverturePlaylist playlist) =>
        playlist.Mode is null &&
        playlist.TrackIds.Count == 0 &&
        playlist.Volume is null &&
        playlist.StartSeconds is null &&
        playlist.Loop is null;

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, min, max), 1) : fallback;

    private void TryBackupDamagedFile()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                File.Copy(_settingsPath, $"{_settingsPath}.damaged", overwrite: true);
            }
        }
        catch
        {
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();
}
