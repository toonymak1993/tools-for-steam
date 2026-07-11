using System.Management;

namespace SteamLoader.App.Infrastructure.Handheld;

public sealed record HandheldTdpMode(string Id, string Title, int Watts);

public sealed record HandheldDeviceProfile(
    string Id,
    string Manufacturer,
    string ProductCode,
    string DisplayName,
    int MinimumTdpWatts,
    int MaximumTdpWatts,
    IReadOnlyList<HandheldTdpMode> Modes,
    bool IsDetected);

public static class HandheldDeviceCatalog
{
    private const string MsiManufacturer = "MICRO-STAR INTERNATIONAL CO., LTD.";
    private const string MsiClawA8Product = "MS-1T8K";

    public static bool IsSupported(HandheldDeviceProfile profile)
        => profile.IsDetected && string.Equals(profile.Id, "msi-claw-a8", StringComparison.Ordinal);

    public static HandheldDeviceProfile Detect()
    {
        var (manufacturer, product) = ReadComputerSystemIdentity();
        if (string.Equals(manufacturer, MsiManufacturer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(product, MsiClawA8Product, StringComparison.OrdinalIgnoreCase))
        {
            return CreateMsiClawA8(true);
        }

        return new HandheldDeviceProfile(
            "unsupported",
            manufacturer,
            product,
            "Handheld Performance",
            0,
            0,
            [],
            false);
    }

    public static HandheldDeviceProfile CreateMsiClawA8(bool detected = false) => new(
        "msi-claw-a8",
        MsiManufacturer,
        MsiClawA8Product,
        "MSI Claw A8",
        15,
        35,
        [
            new HandheldTdpMode("battery", "Battery", 15),
            new HandheldTdpMode("balanced", "Balanced", 20),
            new HandheldTdpMode("performance", "Performance", 28),
        ],
        detected);

    private static (string Manufacturer, string Product) ReadComputerSystemIdentity()
    {
        try
        {
            // Handheld Companion identifies supported models from Win32_BaseBoard.
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                return (
                    Convert.ToString(item["Manufacturer"])?.Trim() ?? string.Empty,
                    Convert.ToString(item["Product"])?.Trim() ?? string.Empty);
            }
        }
        catch
        {
        }

        return (string.Empty, string.Empty);
    }
}
