using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Artwork;

public sealed class ArtworkSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public ArtworkSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public ArtworkConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultConfiguration();
            }

            var json = File.ReadAllText(_settingsPath);
            var configuration = JsonSerializer.Deserialize<ArtworkConfiguration>(json, JsonOptions)
                ?? CreateDefaultConfiguration();

            Normalize(configuration);
            return configuration;
        }
        catch
        {
            return CreateDefaultConfiguration();
        }
    }

    public void Save(ArtworkConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(configuration, JsonOptions));
    }

    private static ArtworkConfiguration CreateDefaultConfiguration()
    {
        var configuration = new ArtworkConfiguration();
        Normalize(configuration);
        return configuration;
    }

    private static void Normalize(ArtworkConfiguration configuration)
    {
        configuration.SteamGridDbApiKey ??= string.Empty;
        configuration.ResultLimit = Math.Clamp(configuration.ResultLimit <= 0 ? 36 : configuration.ResultLimit, 12, 72);
    }
}

public sealed class ArtworkConfiguration
{
    public bool ContextMenuEnabled { get; set; } = true;

    public string SteamGridDbApiKey { get; set; } = string.Empty;

    public bool PreferVerifiedMatches { get; set; } = true;

    public int ResultLimit { get; set; } = 36;
}
