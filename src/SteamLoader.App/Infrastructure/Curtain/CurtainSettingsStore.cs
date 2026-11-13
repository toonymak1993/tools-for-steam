using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLoader.App.Infrastructure.Curtain;

/// <summary>
/// Persists TFS Curtain settings in data\curtain.json: global launch behaviour
/// plus optional per-game overrides (look, timing and mode). Files written by
/// the first TFS Curtain (only <c>disabledAppIds</c>) load unchanged.
/// </summary>
public sealed class CurtainSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _settingsPath;
    private readonly object _gate = new();
    private CurtainConfiguration? _cached;
    private DateTime _cachedWriteTimeUtc;

    public CurtainSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public string SettingsPath => _settingsPath;

    public CurtainConfiguration Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new CurtainConfiguration();
                }

                // The launch hook reads settings on every start; reuse the parsed
                // file until it changes on disk.
                var writeTime = File.GetLastWriteTimeUtc(_settingsPath);
                if (_cached is not null && writeTime == _cachedWriteTimeUtc)
                {
                    return Clone(_cached);
                }

                var configuration = JsonSerializer.Deserialize<CurtainConfiguration>(
                    File.ReadAllText(_settingsPath),
                    JsonOptions) ?? new CurtainConfiguration();
                Normalize(configuration);
                _cached = configuration;
                _cachedWriteTimeUtc = writeTime;
                return Clone(configuration);
            }
            catch
            {
                return new CurtainConfiguration();
            }
        }
    }

    public void Save(CurtainConfiguration configuration)
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

    internal static CurtainConfiguration Clone(CurtainConfiguration configuration) =>
        JsonSerializer.Deserialize<CurtainConfiguration>(
            JsonSerializer.Serialize(configuration, JsonOptions),
            JsonOptions) ?? new CurtainConfiguration();

    internal static void Normalize(CurtainConfiguration configuration)
    {
        configuration.Version = CurtainConfiguration.CurrentVersion;
        configuration.Mode = CurtainModes.NormalizeGlobal(configuration.Mode);
        configuration.TimeoutSeconds = NormalizeTimeout(configuration.TimeoutSeconds) ?? CurtainConfiguration.DefaultTimeoutSeconds;
        configuration.ExitDelaySeconds = NormalizeExitDelay(configuration.ExitDelaySeconds) ?? CurtainConfiguration.DefaultExitDelaySeconds;
        configuration.StatusText = NormalizeText(configuration.StatusText, 60) ?? string.Empty;
        configuration.FallbackLogoPath = NormalizeText(configuration.FallbackLogoPath, 1024) ?? string.Empty;
        configuration.DisabledAppIds = (configuration.DisabledAppIds ?? [])
            .Where(appId => appId > 0)
            .Distinct()
            .Order()
            .ToList();

        var games = new Dictionary<string, CurtainGameSettings>(StringComparer.Ordinal);
        foreach (var (key, settings) in configuration.Games ?? [])
        {
            if (!uint.TryParse(key, out var appId) || appId == 0 || settings is null)
            {
                continue;
            }

            NormalizeGame(settings);
            if (!settings.IsEmpty)
            {
                games[appId.ToString()] = settings;
            }
        }

        configuration.Games = games;
    }

    internal static void NormalizeGame(CurtainGameSettings settings)
    {
        settings.Mode = CurtainModes.NormalizeOverride(settings.Mode);
        settings.TimeoutSeconds = NormalizeTimeout(settings.TimeoutSeconds);
        settings.ExitDelaySeconds = NormalizeExitDelay(settings.ExitDelaySeconds);
        settings.StatusText = NormalizeText(settings.StatusText, 60);
        settings.BackgroundSource = CurtainImageSources.Normalize(settings.BackgroundSource, CurtainImageSources.BackgroundIds);
        settings.BackgroundPath = NormalizeText(settings.BackgroundPath, 1024);
        settings.BackgroundOpacity = Clamp(settings.BackgroundOpacity, 0, 100);
        settings.BackgroundScale = Clamp(settings.BackgroundScale, 100, 200);
        settings.BackgroundX = Clamp(settings.BackgroundX, 0, 100);
        settings.BackgroundY = Clamp(settings.BackgroundY, 0, 100);
        settings.LogoSource = CurtainImageSources.Normalize(settings.LogoSource, CurtainImageSources.LogoIds);
        settings.LogoPath = NormalizeText(settings.LogoPath, 1024);
        settings.LogoX = Clamp(settings.LogoX, 0, 100);
        settings.LogoY = Clamp(settings.LogoY, 0, 100);
        settings.LogoWidth = Clamp(settings.LogoWidth, 10, 90);
    }

    private static int? NormalizeTimeout(int? value)
    {
        if (value is null)
        {
            return null;
        }

        // 0 = no timeout (only "game ready" or a manual close end the curtain).
        return value.Value <= 0 ? 0 : Math.Clamp(value.Value, 10, 300);
    }

    private static double? NormalizeExitDelay(double? value) =>
        value is null || double.IsNaN(value.Value)
            ? null
            : Math.Round(Math.Clamp(value.Value, 0, 10), 1);

    private static int? Clamp(int? value, int minimum, int maximum) =>
        value is null ? null : Math.Clamp(value.Value, minimum, maximum);

    private static string? NormalizeText(string? value, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maximumLength ? trimmed[..maximumLength] : trimmed;
    }
}

public static class CurtainModes
{
    public const string Modern = "modern";
    public const string Classic = "classic";
    public const string Off = "off";

    public static string NormalizeGlobal(string? value) => NormalizeOverride(value) ?? Modern;

    public static string? NormalizeOverride(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Modern => Modern,
        Classic => Classic,
        Off => Off,
        _ => null
    };
}

/// <summary>
/// Image source ids. "auto" follows Steam's artwork (and therefore everything
/// TFS Artworks applies), "custom" uses a file path, "none" hides the layer.
/// </summary>
public static class CurtainImageSources
{
    public const string Auto = "auto";
    public const string None = "none";
    public const string Custom = "custom";

    public static readonly string[] BackgroundIds =
    [
        Auto, None, Custom, "grid-hero", "cache-hero", "grid-portrait", "cache-portrait", "grid-wide", "cache-header"
    ];

    public static readonly string[] LogoIds = [Auto, None, Custom, "grid-logo", "cache-logo"];

    public static string? Normalize(string? value, IReadOnlyCollection<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized) ? normalized : null;
    }
}

public sealed class CurtainConfiguration
{
    public const int CurrentVersion = 2;
    public const int DefaultTimeoutSeconds = 45;
    public const double DefaultExitDelaySeconds = 1.0;

    public int Version { get; set; } = CurrentVersion;
    public string Mode { get; set; } = CurtainModes.Modern;
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    public double ExitDelaySeconds { get; set; } = DefaultExitDelaySeconds;
    public bool HideLaunchers { get; set; } = true;
    public bool ReturnToSteam { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public string StatusText { get; set; } = string.Empty;
    public string FallbackLogoPath { get; set; } = string.Empty;
    public List<uint> DisabledAppIds { get; set; } = [];
    public Dictionary<string, CurtainGameSettings> Games { get; set; } = new(StringComparer.Ordinal);

    public CurtainGameSettings? FindGame(uint appId) =>
        Games.TryGetValue(appId.ToString(), out var settings) ? settings : null;
}

/// <summary>Per-game overrides; null means "use the global value / default".</summary>
public sealed class CurtainGameSettings
{
    public string? Mode { get; set; }
    public int? TimeoutSeconds { get; set; }
    public double? ExitDelaySeconds { get; set; }
    public string? StatusText { get; set; }
    public string? BackgroundSource { get; set; }
    public string? BackgroundPath { get; set; }
    public int? BackgroundOpacity { get; set; }
    public int? BackgroundScale { get; set; }
    public int? BackgroundX { get; set; }
    public int? BackgroundY { get; set; }
    public bool? BackgroundZoom { get; set; }
    public string? LogoSource { get; set; }
    public string? LogoPath { get; set; }
    public int? LogoX { get; set; }
    public int? LogoY { get; set; }
    public int? LogoWidth { get; set; }
    public bool? LogoShadow { get; set; }
    public bool? LogoZoom { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        Mode is null && TimeoutSeconds is null && ExitDelaySeconds is null && StatusText is null &&
        BackgroundSource is null && string.IsNullOrEmpty(BackgroundPath) && BackgroundOpacity is null &&
        BackgroundScale is null && BackgroundX is null && BackgroundY is null && BackgroundZoom is null &&
        LogoSource is null && string.IsNullOrEmpty(LogoPath) && LogoX is null && LogoY is null &&
        LogoWidth is null && LogoShadow is null && LogoZoom is null;
}
