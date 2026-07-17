using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace SteamLoader.App.Infrastructure.Discord;

public sealed class DiscordSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public DiscordSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public DiscordConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new DiscordConfiguration();
            }

            var json = File.ReadAllText(_settingsPath);
            var configuration = JsonSerializer.Deserialize<DiscordConfiguration>(json, JsonOptions)
                ?? new DiscordConfiguration();
            configuration.AccessToken = UnprotectToken(configuration.AccessToken);
            configuration.RefreshToken = UnprotectToken(configuration.RefreshToken);
            Normalize(configuration);
            return configuration;
        }
        catch
        {
            return new DiscordConfiguration();
        }
    }

    public void Save(DiscordConfiguration configuration)
    {
        Normalize(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var persisted = new DiscordConfiguration
        {
            ApplicationId = configuration.ApplicationId,
            ServerId = configuration.ServerId,
            InviteUrl = configuration.InviteUrl,
            AccessToken = ProtectToken(configuration.AccessToken),
            RefreshToken = ProtectToken(configuration.RefreshToken),
            TokenExpiresAtUtc = configuration.TokenExpiresAtUtc,
            SelectedGuildId = configuration.SelectedGuildId,
            TokenProvider = configuration.TokenProvider,
            TokenScopes = configuration.TokenScopes
        };
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(persisted, JsonOptions));
    }

    public void Clear()
    {
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }
    }

    private static void Normalize(DiscordConfiguration configuration)
    {
        configuration.ApplicationId = (configuration.ApplicationId ?? string.Empty).Trim();
        configuration.ServerId = (configuration.ServerId ?? string.Empty).Trim();
        configuration.InviteUrl = (configuration.InviteUrl ?? string.Empty).Trim();
        configuration.AccessToken = (configuration.AccessToken ?? string.Empty).Trim();
        configuration.RefreshToken = (configuration.RefreshToken ?? string.Empty).Trim();
        configuration.SelectedGuildId = (configuration.SelectedGuildId ?? string.Empty).Trim();
        configuration.TokenProvider = (configuration.TokenProvider ?? string.Empty).Trim().ToLowerInvariant();
        configuration.TokenScopes = (configuration.TokenScopes ?? string.Empty).Trim();
    }

    private static string ProtectToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return $"dpapi:{Convert.ToBase64String(protectedBytes)}";
    }

    private static string UnprotectToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!value.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[6..]);
            var clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}

public sealed class DiscordConfiguration
{
    public string ApplicationId { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public string InviteUrl { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTimeOffset? TokenExpiresAtUtc { get; set; }

    public string SelectedGuildId { get; set; } = string.Empty;

    public string TokenProvider { get; set; } = string.Empty;

    public string TokenScopes { get; set; } = string.Empty;
}
