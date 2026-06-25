using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Steam;

public sealed class SteamInstallPathSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SteamInstallPathSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public SteamInstallPathConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultConfiguration();
            }

            var json = File.ReadAllText(_settingsPath);
            var configuration = JsonSerializer.Deserialize<SteamInstallPathConfiguration>(json, JsonOptions)
                ?? CreateDefaultConfiguration();

            Normalize(configuration);
            return configuration;
        }
        catch
        {
            return CreateDefaultConfiguration();
        }
    }

    public void Save(SteamInstallPathConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(configuration, JsonOptions));
    }

    private static SteamInstallPathConfiguration CreateDefaultConfiguration()
    {
        var configuration = new SteamInstallPathConfiguration();
        Normalize(configuration);
        return configuration;
    }

    private static void Normalize(SteamInstallPathConfiguration configuration)
    {
        configuration.ManualOverridePath ??= string.Empty;
    }
}

public sealed class SteamInstallPathConfiguration
{
    public string ManualOverridePath { get; set; } = string.Empty;
}
