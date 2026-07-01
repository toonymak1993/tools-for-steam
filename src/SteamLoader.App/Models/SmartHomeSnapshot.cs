namespace SteamLoader.App.Models;

public static class SmartHomeProviderIds
{
    public const string Homey = "homey";
    public const string HomeAssistant = "home-assistant";
}

public static class SmartHomeControlKinds
{
    public const string Switch = "switch";
    public const string Slider = "slider";
}

public static class SmartHomeControlAccents
{
    public const string Neutral = "neutral";
    public const string Brightness = "brightness";
    public const string Hue = "hue";
    public const string Saturation = "saturation";
    public const string Temperature = "temperature";
}

public sealed record SmartHomeSnapshot(
    SmartHomeSettingsState Settings,
    SmartHomeOverviewState Overview,
    string StatusText,
    string ErrorText,
    DateTimeOffset? RefreshedAtUtc,
    IReadOnlyList<SmartHomeZoneState> Zones,
    IReadOnlyList<SmartHomeDeviceState> UnassignedDevices,
    IReadOnlyList<SmartHomeFlowState> Flows,
    IReadOnlyList<SmartHomeMoodState> Moods);

public sealed record SmartHomeSettingsState(
    string ActiveProviderId,
    IReadOnlyList<SmartHomeProviderOptionState> Providers,
    SmartHomeHomeySettingsState Homey);

public sealed record SmartHomeProviderOptionState(
    string Id,
    string Title,
    string Description,
    bool Supported,
    bool Selected);

public sealed record SmartHomeHomeySettingsState(
    string BaseUrl,
    string HomeyId,
    bool SessionTokenConfigured,
    bool IsConfigured,
    bool IsConnected,
    string StatusText);

public sealed record SmartHomeOverviewState(
    int ZoneCount,
    int DeviceCount,
    int ControllableDeviceCount,
    int AvailableDeviceCount,
    int LightCount,
    int FlowCount,
    int AdvancedFlowCount,
    int MoodCount);

public sealed record SmartHomeZoneState(
    string Id,
    string Name,
    string ParentId,
    string Path,
    int Depth,
    int DeviceCount,
    int LightCount,
    IReadOnlyList<SmartHomeDeviceState> Devices);

public sealed record SmartHomeDeviceState(
    string Id,
    string Name,
    string DeviceClass,
    string ZoneId,
    string ZoneName,
    bool Available,
    bool IsOn,
    bool IsLight,
    bool SupportsColor,
    string StatusText,
    string SwatchHex,
    string SwatchLabel,
    string IconUrl,
    string DriverId,
    string DriverUri,
    IReadOnlyList<SmartHomeControlState> Controls,
    IReadOnlyList<string> Tags);

public sealed record SmartHomeFlowState(
    string Id,
    string Name,
    bool IsAdvanced,
    bool Enabled,
    bool Broken,
    bool Triggerable,
    string FolderId,
    string Description,
    string BadgeText);

public sealed record SmartHomeMoodState(
    string Id,
    string Name,
    string ZoneId,
    string ZoneName,
    string ZonePath,
    int DeviceCount,
    string Preset,
    string Description,
    string BadgeText);

public sealed record SmartHomeControlState(
    string Id,
    string CapabilityId,
    string Kind,
    string Title,
    string Copy,
    bool Enabled,
    bool BooleanValue,
    double NumericValue,
    double Min,
    double Max,
    double Step,
    string ValueLabel,
    string Accent,
    string PreviewHex);
