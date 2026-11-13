using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>Persists TFS Popups settings in data\popups.json (atomic write, cached read).</summary>
public sealed class PopupSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _settingsPath;
    private readonly object _gate = new();
    private PopupConfiguration? _cached;
    private DateTime _cachedWriteTimeUtc;

    public PopupSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public PopupConfiguration Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    var defaults = new PopupConfiguration();
                    Normalize(defaults);
                    return defaults;
                }

                var writeTime = File.GetLastWriteTimeUtc(_settingsPath);
                if (_cached is not null && writeTime == _cachedWriteTimeUtc)
                {
                    return Clone(_cached);
                }

                var configuration = JsonSerializer.Deserialize<PopupConfiguration>(
                    File.ReadAllText(_settingsPath),
                    JsonOptions) ?? new PopupConfiguration();
                Normalize(configuration);
                _cached = configuration;
                _cachedWriteTimeUtc = writeTime;
                return Clone(configuration);
            }
            catch
            {
                var defaults = new PopupConfiguration();
                Normalize(defaults);
                return defaults;
            }
        }
    }

    public PopupConfiguration Update(PopupSettingsUpdate update)
    {
        lock (_gate)
        {
            var configuration = Load();
            Apply(configuration, update);
            Normalize(configuration);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = $"{_settingsPath}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            _cached = Clone(configuration);
            _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(_settingsPath);
            return Clone(configuration);
        }
    }

    internal static void Apply(PopupConfiguration configuration, PopupSettingsUpdate update)
    {
        if (update.Theme is not null) configuration.Theme = update.Theme;
        if (update.SoundMode is not null) configuration.SoundMode = update.SoundMode;
        if (update.SoundVolumePercent is not null) configuration.SoundVolumePercent = update.SoundVolumePercent.Value;
        if (update.VolumeOverlay is not null) configuration.VolumeOverlay = update.VolumeOverlay.Value;
        if (update.VolumePosition is not null) configuration.VolumePosition = update.VolumePosition;
        if (update.SteamInExclusiveFullscreen is not null) configuration.SteamInExclusiveFullscreen = update.SteamInExclusiveFullscreen.Value;

        if (!PopupCategories.IsKnown(update.Category))
        {
            return;
        }

        var category = configuration.Categories.TryGetValue(update.Category!, out var existing)
            ? existing
            : PopupCategories.Defaults(update.Category!);
        if (update.Managed is not null) category.Managed = update.Managed.Value;
        if (update.Position is not null) category.Position = update.Position;
        if (update.DurationSeconds is not null) category.DurationSeconds = update.DurationSeconds.Value;
        configuration.Categories[update.Category!] = category;
    }

    internal static void Normalize(PopupConfiguration configuration)
    {
        configuration.Version = PopupConfiguration.CurrentVersion;
        configuration.Theme = PopupThemeIds.Normalize(configuration.Theme);
        configuration.SoundMode = PopupSoundModes.Normalize(configuration.SoundMode);
        configuration.SoundVolumePercent = Math.Clamp(configuration.SoundVolumePercent, 0, PopupConfiguration.MaximumSoundVolumePercent);
        configuration.VolumePosition = PopupPositions.Normalize(configuration.VolumePosition, PopupPositions.TopCenter);

        var saved = configuration.Categories ?? new Dictionary<string, PopupCategorySettings>();
        var categories = new Dictionary<string, PopupCategorySettings>(StringComparer.Ordinal);
        foreach (var id in PopupCategories.All)
        {
            var defaults = PopupCategories.Defaults(id);
            var value = saved.TryGetValue(id, out var stored) && stored is not null ? stored : defaults;
            categories[id] = new PopupCategorySettings
            {
                Managed = value.Managed,
                Position = PopupPositions.Normalize(value.Position, defaults.Position),
                DurationSeconds = Math.Clamp(
                    value.DurationSeconds <= 0 ? defaults.DurationSeconds : value.DurationSeconds,
                    PopupConfiguration.MinimumDurationSeconds,
                    PopupConfiguration.MaximumDurationSeconds),
            };
        }

        configuration.Categories = categories;
    }

    internal static PopupConfiguration Clone(PopupConfiguration configuration) =>
        JsonSerializer.Deserialize<PopupConfiguration>(
            JsonSerializer.Serialize(configuration, JsonOptions),
            JsonOptions) ?? new PopupConfiguration();
}
