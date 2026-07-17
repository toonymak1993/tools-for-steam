using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Persists controller-replacement settings by handheld profile so future
/// devices can supply their own defaults without sharing MSI-specific state.
/// </summary>
internal static class HandheldControllerSettingsStore
{
    internal const int MsiClawA8DefaultVibrationStrengthPercent = 70;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int ReadVibrationStrengthPercent(
        string dataDirectory,
        string deviceId,
        int defaultValue)
    {
        try
        {
            var settings = Load(dataDirectory);
            return settings.Devices is not null &&
                   settings.Devices.TryGetValue(deviceId, out var device)
                ? Math.Clamp(device.VibrationStrengthPercent, 0, 100)
                : Math.Clamp(defaultValue, 0, 100);
        }
        catch
        {
            return Math.Clamp(defaultValue, 0, 100);
        }
    }

    public static int WriteVibrationStrengthPercent(
        string dataDirectory,
        string deviceId,
        int value)
    {
        var normalized = Math.Clamp(value, 0, 100);
        var settings = Load(dataDirectory);
        var devices = new Dictionary<string, HandheldControllerDeviceSettings>(
            settings.Devices ?? new Dictionary<string, HandheldControllerDeviceSettings>(),
            StringComparer.OrdinalIgnoreCase);
        devices.TryGetValue(deviceId, out var existing);
        devices[deviceId] = new(normalized, existing?.UiHapticsEnabled ?? true);

        Save(dataDirectory, devices);
        return normalized;
    }

    public static bool ReadUiHapticsEnabled(string dataDirectory, string deviceId, bool defaultValue = true)
    {
        try
        {
            var settings = Load(dataDirectory);
            return settings.Devices is not null && settings.Devices.TryGetValue(deviceId, out var device)
                ? device.UiHapticsEnabled
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public static bool WriteUiHapticsEnabled(
        string dataDirectory,
        string deviceId,
        bool enabled,
        int defaultVibrationStrengthPercent)
    {
        var settings = Load(dataDirectory);
        var devices = new Dictionary<string, HandheldControllerDeviceSettings>(
            settings.Devices ?? new Dictionary<string, HandheldControllerDeviceSettings>(),
            StringComparer.OrdinalIgnoreCase);
        devices.TryGetValue(deviceId, out var existing);
        devices[deviceId] = new(
            existing?.VibrationStrengthPercent ?? Math.Clamp(defaultVibrationStrengthPercent, 0, 100),
            enabled);

        Save(dataDirectory, devices);
        return enabled;
    }

    private static void Save(
        string dataDirectory,
        IReadOnlyDictionary<string, HandheldControllerDeviceSettings> devices)
    {

        Directory.CreateDirectory(dataDirectory);
        var path = GetPath(dataDirectory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new HandheldControllerSettings(devices), JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static HandheldControllerSettings Load(string dataDirectory)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<HandheldControllerSettings>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static string GetPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "handheld-controller-settings.json");

    private sealed record HandheldControllerSettings(
        IReadOnlyDictionary<string, HandheldControllerDeviceSettings>? Devices = null);

    private sealed record HandheldControllerDeviceSettings(
        int VibrationStrengthPercent,
        bool UiHapticsEnabled = true);
}

internal sealed record HandheldUiHapticCommand(
    long Nonce,
    string DeviceId,
    byte LargeMotor,
    byte SmallMotor,
    int DurationMilliseconds);

internal static class HandheldUiHapticPatternCatalog
{
    public static HandheldUiHapticCommand Create(string deviceId, string kind, long nonce)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        var pattern = normalized switch
        {
            // The MSI motors need a brief startup impulse to be perceptible.
            // A short high-frequency pulse feels lighter and more reliable
            // than values below the motor's physical startup threshold.
            "move" => (Large: (byte)0, Small: (byte)150, Duration: 38),
            "confirm" => (Large: (byte)150, Small: (byte)190, Duration: 72),
            "back" => (Large: (byte)100, Small: (byte)165, Duration: 55),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "The requested UI haptic pattern is not supported."),
        };

        return new(nonce, deviceId, pattern.Large, pattern.Small, pattern.Duration);
    }
}
