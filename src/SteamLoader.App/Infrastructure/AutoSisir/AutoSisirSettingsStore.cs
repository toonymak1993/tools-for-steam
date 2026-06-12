using System.Text.Json;

namespace SteamLoader.App.Infrastructure.AutoSisir;

public sealed class AutoSisirSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AutoSisirSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AutoSisirConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultConfiguration();
            }

            var json = File.ReadAllText(_settingsPath);
            var configuration = JsonSerializer.Deserialize<AutoSisirConfiguration>(json, JsonOptions)
                ?? CreateDefaultConfiguration();

            Normalize(configuration);
            return configuration;
        }
        catch
        {
            return CreateDefaultConfiguration();
        }
    }

    public void Save(AutoSisirConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(configuration, JsonOptions));
    }

    public static string GetDefaultExecutablePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SISR",
            "SISR.exe");
    }

    private static AutoSisirConfiguration CreateDefaultConfiguration()
    {
        var configuration = new AutoSisirConfiguration();
        Normalize(configuration);
        return configuration;
    }

    private static void Normalize(AutoSisirConfiguration configuration)
    {
        configuration.ExecutablePath ??= string.Empty;
        configuration.LaunchArguments = string.IsNullOrWhiteSpace(configuration.LaunchArguments)
            ? "--marker"
            : configuration.LaunchArguments.Trim();
        configuration.WatchedTitleIds = configuration.WatchedTitleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AutoSisirConfiguration
{
    public bool Enabled { get; set; }

    public bool AutoStartForGamePass { get; set; } = true;

    public string ExecutablePath { get; set; } = string.Empty;

    public string LaunchArguments { get; set; } = "--marker";

    public List<string> WatchedTitleIds { get; set; } = [];
}
