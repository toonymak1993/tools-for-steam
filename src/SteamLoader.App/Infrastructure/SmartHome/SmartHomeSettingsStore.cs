using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.SmartHome;

public sealed class SmartHomeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SmartHomeSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public SmartHomeConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultConfiguration();
            }

            var json = File.ReadAllText(_settingsPath);
            var configuration = JsonSerializer.Deserialize<SmartHomeConfiguration>(json, JsonOptions)
                ?? CreateDefaultConfiguration();

            Normalize(configuration);
            return configuration;
        }
        catch
        {
            return CreateDefaultConfiguration();
        }
    }

    public void Save(SmartHomeConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(configuration, JsonOptions));
    }

    public static string NormalizeProviderId(string? providerId)
    {
        var normalized = (providerId ?? string.Empty).Trim();
        return normalized.Equals(SmartHomeProviderIds.HomeAssistant, StringComparison.OrdinalIgnoreCase)
            ? SmartHomeProviderIds.HomeAssistant
            : SmartHomeProviderIds.Homey;
    }

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var candidate = trimmed;
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri))
        {
            return candidate.TrimEnd('/');
        }

        var builder = new UriBuilder(absoluteUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        var path = builder.Path?.Trim() ?? string.Empty;
        var apiMarkerIndex = path.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
        if (apiMarkerIndex >= 0)
        {
            path = path[..apiMarkerIndex];
        }

        path = path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path) ? "/" : $"{path}/";
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static SmartHomeConfiguration CreateDefaultConfiguration()
    {
        var configuration = new SmartHomeConfiguration();
        Normalize(configuration);
        return configuration;
    }

    private static void Normalize(SmartHomeConfiguration configuration)
    {
        configuration.ProviderId = NormalizeProviderId(configuration.ProviderId);
        configuration.Homey ??= new SmartHomeHomeyConfiguration();
        configuration.Homey.BaseUrl = NormalizeBaseUrl(configuration.Homey.BaseUrl);
        configuration.Homey.HomeyId = NormalizeText(configuration.Homey.HomeyId);
        configuration.Homey.SessionToken = NormalizeText(configuration.Homey.SessionToken);
    }

    private static string NormalizeText(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}

public sealed class SmartHomeConfiguration
{
    public string ProviderId { get; set; } = SmartHomeProviderIds.Homey;

    public SmartHomeHomeyConfiguration Homey { get; set; } = new();
}

public sealed class SmartHomeHomeyConfiguration
{
    public string BaseUrl { get; set; } = string.Empty;

    public string HomeyId { get; set; } = string.Empty;

    public string SessionToken { get; set; } = string.Empty;
}
