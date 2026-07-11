namespace SteamLoader.App.Infrastructure.Handheld;

public sealed record HandheldPerformanceSnapshot(
    string PluginTitle,
    string DeviceId,
    string Manufacturer,
    string ProductCode,
    bool Supported,
    bool PawnIoInstalled,
    bool PawnIoReady,
    int MinimumTdpWatts,
    int MaximumTdpWatts,
    int SelectedTdpWatts,
    int GlobalTdpWatts,
    int GlobalAcTdpWatts,
    int GlobalBatteryTdpWatts,
    string PowerSource,
    HandheldPerformanceTelemetry Telemetry,
    string SelectedModeId,
    IReadOnlyList<HandheldTdpMode> Modes,
    bool AutoProfilesEnabled,
    bool ProfileNotificationsEnabled,
    HandheldRunningGame? CurrentGame,
    HandheldGameTdpProfile? ActiveProfile,
    IReadOnlyList<HandheldGameTdpProfile> Profiles,
    string StatusText,
    string ErrorText);

internal sealed record HandheldPerformanceSettings(
    int TdpWatts = 20,
    string ModeId = "balanced",
    DateTimeOffset UpdatedAt = default,
    int? AcTdpWatts = null,
    int? BatteryTdpWatts = null);

public sealed record HandheldRunningGame(
    string Key,
    string AppId,
    string Title,
    string ExecutablePath,
    int ProcessId);

public sealed record HandheldGameTdpProfile(
    string Key,
    string AppId,
    string Title,
    string ExecutablePath,
    int TdpWatts,
    DateTimeOffset UpdatedAt,
    int? AcTdpWatts = null,
    int? BatteryTdpWatts = null);

public sealed record HandheldPerformanceTelemetry(
    string PowerSource,
    bool IsPluggedIn,
    int BatteryPercent,
    int EstimatedMinutesRemaining,
    int AppliedTdpWatts,
    bool AppliedTdpConfirmed,
    DateTimeOffset UpdatedAt);

internal sealed record HandheldPowerState(
    string PowerSource = "ac",
    bool IsPluggedIn = true,
    int BatteryPercent = -1,
    int EstimatedMinutesRemaining = -1,
    DateTimeOffset UpdatedAt = default);

internal sealed record HandheldPerformanceProfileSettings(
    bool AutoProfilesEnabled = true,
    bool NotificationsEnabled = true,
    IReadOnlyList<HandheldGameTdpProfile>? Profiles = null);

internal sealed record HandheldAutomaticProfileResult(
    string Title,
    int TdpWatts,
    bool IsGameProfile,
    bool ShowNotification);

internal sealed record HandheldHardwareCommand(
    long Nonce,
    string DeviceId,
    string ProductCode,
    string Operation,
    int TdpWatts);

internal sealed record HandheldHardwareStatus(
    long Nonce = 0,
    bool Success = false,
    bool PawnIoReady = false,
    int AppliedTdpWatts = 0,
    string CpuCodeName = "",
    string Message = "Waiting for the elevated hardware helper.",
    DateTimeOffset UpdatedAt = default);
