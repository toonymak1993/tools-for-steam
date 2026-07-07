namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// MSI Claw (AMD, e.g. Claw A8 / Ryzen Z2 Extreme) power backend built on MSI's
/// own WMI interface (MSI_ACPI) - the same path MSI Center M and Handheld
/// Companion use, no kernel driver.
///
/// Writing goes through MSI's purpose-built <c>Set_Power</c> method (never the
/// raw <c>Set_EC</c> address poke that the kernel docs warn can damage hardware),
/// values are clamped to a safe range, and every write is confirmed by MSI's
/// returned status byte. The exact byte positions for SPL/SPPT/FPPT below are
/// still PROVISIONAL and are meant to be locked in from an on-device Get_Power
/// dump (see <see cref="ReadPowerRaw"/>); until confirmed, a wrong layout simply
/// makes Set_Power report failure rather than doing harm.
/// </summary>
public sealed class MsiClawDevice : IHandheldTdpBackend
{
    private const string GetPowerMethod = "Get_Power";
    private const string SetPowerMethod = "Set_Power";
    private const string GetEcMethod = "Get_EC";

    // Safety clamp for anything we ever write (watts).
    private const int MinWatt = 5;
    private const int MaxWatt = 54;

    // === PROVISIONAL PACKAGE RECIPE ==========================================
    // MSI_ACPI methods take a 32-byte package: [0] = subfeature selector, the
    // rest is data; the returned [0] is 0x00 on failure. The positions MSI uses
    // for SPL/SPPT/FPPT on the Claw A8 are confirmed on-device by decoding a
    // Get_Power dump at two known MSI settings. Adjust these once known.
    private const byte PowerSubFeature = 0x00;
    private const int SplOffset = 1;   // STAPM / sustained
    private const int SpptOffset = 2;  // PPT slow / average
    private const int FpptOffset = 3;  // PPT fast / burst
    // =========================================================================

    private readonly MsiWmiInterface _wmi;

    public MsiClawDevice(MsiWmiInterface wmi) => _wmi = wmi;

    public bool CanControlTdp => _wmi.IsAvailable();

    public bool TryGetLimits(out TdpLimits limits)
    {
        limits = TdpLimits.Empty;

        var raw = _wmi.Read(GetPowerMethod, PowerSubFeature);
        if (raw is null || raw.Length <= FpptOffset || raw[0] == 0x00)
        {
            return false;
        }

        var candidate = new TdpLimits(raw[SplOffset], raw[SpptOffset], raw[FpptOffset]);
        if (!IsPlausible(candidate))
        {
            // Offsets not yet calibrated for this device - report unreadable
            // rather than surfacing garbage. Use ReadPowerRaw() to decode.
            return false;
        }

        limits = candidate;
        return true;
    }

    public bool TrySetLimits(TdpLimits limits)
    {
        if (!_wmi.IsAvailable())
        {
            return false;
        }

        var package = new byte[32];
        package[0] = PowerSubFeature;
        package[SplOffset] = (byte)Clamp(limits.SplWatts);
        package[SpptOffset] = (byte)Clamp(limits.SpptWatts);
        package[FpptOffset] = (byte)Clamp(limits.FpptWatts);

        var result = _wmi.Invoke(SetPowerMethod, package);

        // Treat as applied only if MSI's method acknowledges (non-zero status).
        return result is { Length: > 0 } && result[0] != 0x00;
    }

    // --- Diagnostics used to lock in the recipe on the actual device ---

    /// <summary>Raw 32-byte Get_Power output (hex-dump this to decode the layout).</summary>
    public byte[]? ReadPowerRaw() => _wmi.Read(GetPowerMethod, PowerSubFeature);

    /// <summary>Raw 32-byte Get_EC output (EC firmware/info).</summary>
    public byte[]? ReadEcRaw() => _wmi.Read(GetEcMethod);

    /// <summary>Names of the MSI_ACPI methods this device exposes.</summary>
    public IReadOnlyList<string> ListWmiMethods() => _wmi.GetMethodNames();

    private static int Clamp(int watt) => Math.Clamp(watt, MinWatt, MaxWatt);

    private static bool IsPlausible(TdpLimits limits) =>
        limits.SplWatts is >= MinWatt and <= MaxWatt &&
        limits.SpptWatts is >= MinWatt and <= MaxWatt &&
        limits.FpptWatts is >= MinWatt and <= MaxWatt;
}
