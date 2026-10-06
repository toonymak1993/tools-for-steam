using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Curtain;

public sealed class CurtainSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public CurtainSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public CurtainConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new CurtainConfiguration();
            }

            var configuration = JsonSerializer.Deserialize<CurtainConfiguration>(
                File.ReadAllText(_settingsPath),
                JsonOptions) ?? new CurtainConfiguration();
            Normalize(configuration);
            return configuration;
        }
        catch
        {
            return new CurtainConfiguration();
        }
    }

    public void Save(CurtainConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

        var temporaryPath = $"{_settingsPath}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private static void Normalize(CurtainConfiguration configuration)
    {
        configuration.DisabledAppIds = configuration.DisabledAppIds
            .Where(appId => appId > 0)
            .Distinct()
            .Order()
            .ToList();
    }
}

public sealed class CurtainConfiguration
{
    public List<uint> DisabledAppIds { get; set; } = [];
}
