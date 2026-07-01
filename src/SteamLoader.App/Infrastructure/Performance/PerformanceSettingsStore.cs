using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Performance;

public sealed class PerformanceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly object _gate = new();

    public PerformanceSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    internal PerformanceSettingsConfiguration Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return Normalize(new PerformanceSettingsConfiguration());
                }

                var json = File.ReadAllText(_settingsPath);
                var configuration = JsonSerializer.Deserialize<PerformanceSettingsConfiguration>(json, JsonOptions);
                return Normalize(configuration ?? new PerformanceSettingsConfiguration());
            }
            catch
            {
                return Normalize(new PerformanceSettingsConfiguration());
            }
        }
    }

    internal void Save(PerformanceSettingsConfiguration configuration)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Normalize(configuration), JsonOptions));
        }
    }

    private static PerformanceSettingsConfiguration Normalize(PerformanceSettingsConfiguration configuration)
    {
        configuration.OverlayLevel = Math.Clamp(configuration.OverlayLevel, 0, 2);
        configuration.OverlayPosition = Math.Clamp(configuration.OverlayPosition, 0, 3);
        configuration.OverlayWidth = RoundToStep(Math.Clamp(configuration.OverlayWidth, 200, 1920), 40);
        configuration.OverlayScale = RoundToStep(Math.Clamp(configuration.OverlayScale, 80, 160), 10);
        configuration.GraphMode = Math.Clamp(configuration.GraphMode, 0, 2);
        configuration.BackgroundTheme = Math.Clamp(configuration.BackgroundTheme, 0, 4);
        configuration.BackgroundOpacity = RoundToStep(Math.Clamp(configuration.BackgroundOpacity, 0, 100), 10);
        configuration.MetricPollRate = RoundToStep(Math.Clamp(configuration.MetricPollRate, 10, 120), 10);
        configuration.TelemetrySamplingPeriodMs = RoundToStep(Math.Clamp(configuration.TelemetrySamplingPeriodMs, 10, 500), 10);
        configuration.MetricsWindow = RoundToStep(Math.Clamp(configuration.MetricsWindow, 100, 5000), 100);
        configuration.OverlayDrawRate = RoundToStep(Math.Clamp(configuration.OverlayDrawRate, 1, 120), 5);
        return configuration;
    }

    private static int RoundToStep(int value, int step)
    {
        if (step <= 1)
        {
            return value;
        }

        return (int)Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step;
    }
}

internal sealed class PerformanceSettingsConfiguration
{
    public bool OverlayEnabled { get; set; }

    public int OverlayLevel { get; set; } = 0;

    public bool AutoTargetEnabled { get; set; } = true;

    public int OverlayPosition { get; set; } = 0;

    public int OverlayWidth { get; set; } = 400;

    public int OverlayScale { get; set; } = 100;

    public int GraphMode { get; set; } = 2;

    public int BackgroundTheme { get; set; } = 0;

    public int BackgroundOpacity { get; set; } = 90;

    public int MetricPollRate { get; set; } = 40;

    public int TelemetrySamplingPeriodMs { get; set; } = 100;

    public int MetricsWindow { get; set; } = 1000;

    public int OverlayDrawRate { get; set; } = 10;
}
