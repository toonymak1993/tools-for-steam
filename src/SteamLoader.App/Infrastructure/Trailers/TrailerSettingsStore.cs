using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLoader.App.Infrastructure.Trailers;

/// <summary>Persists TFS Trailers settings in data\trailers.json (atomic write, cached read).</summary>
public sealed class TrailerSettingsStore
{
    internal const int MaxRememberedTitleMatches = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] Qualities = ["1080", "720", "480"];

    private readonly string _settingsPath;
    private readonly object _gate = new();
    private TrailerConfiguration? _cached;
    private DateTime _cachedWriteTimeUtc;

    public TrailerSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public string SettingsPath => _settingsPath;

    public TrailerConfiguration Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new TrailerConfiguration();
                }

                var writeTime = File.GetLastWriteTimeUtc(_settingsPath);
                if (_cached is not null && writeTime == _cachedWriteTimeUtc)
                {
                    return Clone(_cached);
                }

                var configuration = JsonSerializer.Deserialize<TrailerConfiguration>(
                    File.ReadAllText(_settingsPath),
                    JsonOptions) ?? new TrailerConfiguration();
                Normalize(configuration);
                _cached = configuration;
                _cachedWriteTimeUtc = writeTime;
                return Clone(configuration);
            }
            catch
            {
                return new TrailerConfiguration();
            }
        }
    }

    public void Save(TrailerConfiguration configuration)
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
    public TrailerConfiguration Update(Action<TrailerConfiguration> change)
    {
        lock (_gate)
        {
            var configuration = Load();
            change(configuration);
            Save(configuration);
            return Clone(configuration);
        }
    }

    internal static TrailerConfiguration Clone(TrailerConfiguration configuration) =>
        JsonSerializer.Deserialize<TrailerConfiguration>(
            JsonSerializer.Serialize(configuration, JsonOptions),
            JsonOptions) ?? new TrailerConfiguration();

    internal static void Normalize(TrailerConfiguration configuration)
    {
        configuration.Version = TrailerConfiguration.CurrentVersion;
        configuration.DelaySeconds = Math.Clamp(configuration.DelaySeconds, 0, 30);
        configuration.Quality = NormalizeQuality(configuration.Quality);
        configuration.Games = new Dictionary<string, TrailerGameSettings>(
            (configuration.Games ?? new Dictionary<string, TrailerGameSettings>())
                .Where(entry => uint.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) && appId > 0 && entry.Value is not null)
                .Select(entry => KeyValuePair.Create(entry.Key, NormalizeGame(entry.Value)))
                .Where(entry => !IsEmpty(entry.Value)),
            StringComparer.Ordinal);
        configuration.TitleMatches = new Dictionary<string, TrailerTitleMatch>(
            (configuration.TitleMatches ?? new Dictionary<string, TrailerTitleMatch>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null)
                .OrderByDescending(entry => entry.Value.CheckedAt)
                .Take(MaxRememberedTitleMatches),
            StringComparer.Ordinal);
    }

    internal static string NormalizeQuality(string? quality) =>
        Qualities.Contains(quality?.Trim() ?? string.Empty, StringComparer.Ordinal)
            ? quality!.Trim()
            : TrailerConfiguration.DefaultQuality;

    internal static TrailerGameSettings NormalizeGame(TrailerGameSettings settings)
    {
        var mode = settings.Mode?.Trim().ToLowerInvariant();
        return new TrailerGameSettings
        {
            Mode = mode == "off" ? "off" : null,
            MovieId = settings.MovieId is > 0 ? settings.MovieId : null,
            SteamAppId = settings.SteamAppId is > 0 ? settings.SteamAppId : null,
            TrimStartSeconds = NormalizeTrim(settings.TrimStartSeconds),
            TrimEndSeconds = NormalizeTrim(settings.TrimEndSeconds),
        };
    }

    internal static bool IsEmpty(TrailerGameSettings settings) =>
        settings.Mode is null &&
        settings.MovieId is null &&
        settings.SteamAppId is null &&
        settings.TrimStartSeconds is null &&
        settings.TrimEndSeconds is null;

    private static double? NormalizeTrim(double? value) =>
        value is null || double.IsNaN(value.Value) || value.Value <= 0
            ? null
            : Math.Round(Math.Min(value.Value, 120), 1);
}
