using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Detects the current handheld from firmware/registry identifiers so the
/// handheld plugin can auto-select the right device profile. Detection is
/// registry-only: no elevated rights, no kernel driver, no WMI, no side effects -
/// safe to call at any time (also on a normal desktop PC, where it simply
/// reports "not a handheld").
/// </summary>
public sealed class HandheldDeviceDetection
{
    public HandheldDeviceInfo Detect()
    {
        var manufacturer = ReadBios("SystemManufacturer");
        var product = ReadBios("SystemProductName");
        if (string.IsNullOrWhiteSpace(product))
        {
            product = ReadBios("BaseBoardProduct");
        }

        var cpu = ReadProcessorName();
        var isAmd =
            cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);

        var isMsi =
            manufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) ||
            manufacturer.Contains("MSI", StringComparison.OrdinalIgnoreCase);
        var isClaw = product.Contains("Claw", StringComparison.OrdinalIgnoreCase);

        if (isMsi && isClaw)
        {
            var family = isAmd ? HandheldFamily.MsiClawAmd : HandheldFamily.MsiClawIntel;
            return new HandheldDeviceInfo(
                IsHandheld: true,
                Family: family,
                Manufacturer: manufacturer,
                Model: product,
                ProcessorName: cpu,
                IsAmd: isAmd);
        }

        // Not a recognised handheld (e.g. the dev PC): keep the identifiers for
        // diagnostics but mark it as non-handheld so no controls are shown.
        return HandheldDeviceInfo.None with
        {
            Manufacturer = manufacturer,
            Model = product,
            ProcessorName = cpu,
            IsAmd = isAmd,
        };
    }

    private static string ReadBios(string valueName) =>
        ReadString(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", valueName);

    private static string ReadProcessorName() =>
        ReadString(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");

    private static string ReadString(RegistryKey root, string path, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            return (key?.GetValue(valueName) as string)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
