namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Known handheld families the "handheld" plugin can show device-specific
/// controls for. The first official integration targets MSI's AMD handhelds
/// (Claw A8 / Ryzen Z2 Extreme); MSI's control interface is shared across their
/// AMD line, so one integration is expected to cover them.
/// </summary>
public enum HandheldFamily
{
    Unknown = 0,
    MsiClawAmd,    // MSI Claw A8 and other MSI AMD (Ryzen Z2 / Z2 Extreme) handhelds
    MsiClawIntel,  // MSI Claw A1M / 7 AI+ / 8 AI+ (Intel)
    Generic
}

/// <summary>
/// Identity of the machine the app is running on, as detected from firmware
/// identifiers. Used to decide whether to surface handheld controls and which
/// device profile to load.
/// </summary>
public sealed record HandheldDeviceInfo(
    bool IsHandheld,
    HandheldFamily Family,
    string Manufacturer,
    string Model,
    string ProcessorName,
    bool IsAmd)
{
    public static HandheldDeviceInfo None { get; } =
        new(false, HandheldFamily.Unknown, string.Empty, string.Empty, string.Empty, false);

    public string DisplayName =>
        IsHandheld && !string.IsNullOrWhiteSpace(Model) ? Model.Trim() : "Unbekanntes Gerät";
}

/// <summary>
/// Power limits in watts. Mirrors the three values MSI Center M exposes:
/// SPL = STAPM / sustained, SPPT = PPT slow / average, FPPT = PPT fast / burst.
/// </summary>
public sealed record TdpLimits(int SplWatts, int SpptWatts, int FpptWatts)
{
    public static TdpLimits Empty { get; } = new(0, 0, 0);

    public bool IsValid => SplWatts > 0 && SpptWatts > 0 && FpptWatts > 0;
}

public sealed record HandheldTdpPreset(string Id, string Name, TdpLimits Limits);

public static class HandheldTdpPresets
{
    // First-cut presets for the MSI Claw A8 (Z2 Extreme). The ranges roughly
    // match MSI Center M's Manual mode (SPL up to ~35W, PPT up to ~48W). These
    // are safe starting points and will be refined once we read the device's
    // real limits on-device.
    public static readonly IReadOnlyList<HandheldTdpPreset> ClawA8 =
    [
        new("silent",   "Silent",   new TdpLimits(10, 12, 15)),
        new("balanced", "Balanced", new TdpLimits(15, 20, 25)),
        new("turbo",    "Turbo",    new TdpLimits(30, 40, 48)),
    ];

    public static IReadOnlyList<HandheldTdpPreset> ForFamily(HandheldFamily family) => family switch
    {
        HandheldFamily.MsiClawAmd => ClawA8,
        _ => [],
    };
}
