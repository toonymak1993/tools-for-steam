namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Device-specific power-limit backend. Each handheld family plugs in its own
/// mechanism to read and apply SPL/SPPT/FPPT. For MSI's AMD handhelds this will
/// be an implementation over MSI's own WMI/EC interface (the MSI_ACPI class),
/// i.e. the same path MSI Center M and Handheld Companion use - no kernel driver.
///
/// Writes are intentionally kept behind this contract (and out of this first
/// cut) until the exact EC byte layout for the target device is confirmed
/// on-device, because writing wrong values to the embedded controller can harm
/// the machine.
/// </summary>
public interface IHandheldTdpBackend
{
    /// <summary>True if this backend can read/apply power limits on this device.</summary>
    bool CanControlTdp { get; }

    /// <summary>Reads the current SPL/SPPT/FPPT limits, if supported.</summary>
    bool TryGetLimits(out TdpLimits limits);

    /// <summary>Applies the given SPL/SPPT/FPPT limits, if supported.</summary>
    bool TrySetLimits(TdpLimits limits);
}

/// <summary>
/// Fallback backend used on unrecognised hardware or before a device-specific
/// backend is wired in. Reports no capability and performs no actions.
/// </summary>
public sealed class NullHandheldTdpBackend : IHandheldTdpBackend
{
    public bool CanControlTdp => false;

    public bool TryGetLimits(out TdpLimits limits)
    {
        limits = TdpLimits.Empty;
        return false;
    }

    public bool TrySetLimits(TdpLimits limits) => false;
}
