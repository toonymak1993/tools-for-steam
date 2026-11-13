using System.Management;

namespace SteamLoader.App.Infrastructure.Handheld;

public sealed record HandheldTdpMode(string Id, string Title, int Watts);

public sealed record HandheldLightingCapabilities(
    bool Supported,
    bool SeparateZones,
    int MinimumBrightness,
    int MaximumBrightness,
    IReadOnlyList<string> Effects);

public sealed record HandheldOemButtonDefinition(
    string Id,
    string Title,
    string Description);

public sealed record HandheldOemSoftwareCapabilities(
    bool Supported,
    string SoftwareName,
    IReadOnlyList<HandheldOemButtonDefinition> Buttons);

public sealed record HandheldControllerCapabilities(
    bool VibrationSupported,
    int MinimumVibrationStrengthPercent,
    int MaximumVibrationStrengthPercent,
    int DefaultVibrationStrengthPercent);

public sealed record HandheldDeviceProfile(
    string Id,
    string Manufacturer,
    string ProductCode,
    string DisplayName,
    int MinimumTdpWatts,
    int MaximumTdpWatts,
    IReadOnlyList<HandheldTdpMode> Modes,
    bool IsDetected,
    HandheldLightingCapabilities Lighting,
    HandheldControllerCapabilities Controller,
    HandheldOemSoftwareCapabilities OemSoftware,
    HandheldTdpCapability? Tdp = null);

public static class HandheldDeviceCatalog
{
    private const string MsiManufacturer = "MICRO-STAR INTERNATIONAL CO., LTD.";
    private const string MsiClawA8Product = "MS-1T8K";
    public const string GenericAmdDeviceId = "amd-apu";
    private static readonly Lazy<HandheldDeviceProfile> Detected = new(DetectCore, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Fully supported handheld (OEM buttons, RGB, vibration, replacement runtime).</summary>
    public static bool IsSupported(HandheldDeviceProfile profile)
        => profile.IsDetected && string.Equals(profile.Id, "msi-claw-a8", StringComparison.Ordinal);

    /// <summary>Any detected device with a known AMD TDP range (Claw A8 or a generic AMD APU).</summary>
    public static bool SupportsTdp(HandheldDeviceProfile profile)
        => profile.IsDetected && profile.Tdp is not null && profile.MaximumTdpWatts > 0;

    public static HandheldDeviceProfile Detect() => Detected.Value;

    private static HandheldDeviceProfile DetectCore()
    {
        var (manufacturer, product) = ReadComputerSystemIdentity();
        return Resolve(
            manufacturer,
            product,
            AmdTdpCatalog.ReadCpuName(),
            HandheldManufacturerLimits.ReadVendorApp(manufacturer),
            ReadSystemModel());
    }

    internal static HandheldDeviceProfile Resolve(
        string manufacturer,
        string product,
        string cpuName,
        HandheldManufacturerTdp? vendorAppLimits = null,
        string model = "")
    {
        if (string.Equals(manufacturer, MsiManufacturer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(product, MsiClawA8Product, StringComparison.OrdinalIgnoreCase))
        {
            return CreateMsiClawA8(true);
        }

        var tdp = AmdTdpCatalog.Resolve(cpuName);
        if (tdp is not null)
        {
            var maker = HandheldManufacturerLimits.Resolve(manufacturer, product, tdp.CpuName, vendorAppLimits, model);
            return CreateGenericAmd(manufacturer, product, HandheldManufacturerLimits.Apply(tdp, maker));
        }

        return new HandheldDeviceProfile(
            "unsupported",
            manufacturer,
            product,
            "Handheld Performance",
            0,
            0,
            [],
            false,
            new(false, false, 0, 0, []),
            new(false, 0, 0, 0),
            new(false, string.Empty, []));
    }

    internal static HandheldDeviceProfile CreateGenericAmd(string manufacturer, string product, HandheldTdpCapability tdp)
    {
        var displayName = string.IsNullOrWhiteSpace(product)
            ? tdp.CpuName
            : product.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(manufacturer)
                ? product
                : $"{manufacturer} {product}";
        var balanced = Math.Clamp(tdp.DefaultWatts, tdp.MinimumWatts, tdp.MaximumWatts);
        var performance = Math.Clamp((int)Math.Round(balanced + ((tdp.MaximumWatts - balanced) * 0.5)), balanced, tdp.MaximumWatts);
        return new HandheldDeviceProfile(
            GenericAmdDeviceId,
            manufacturer,
            product,
            displayName,
            tdp.MinimumWatts,
            tdp.MaximumWatts,
            [
                new HandheldTdpMode("battery", "Battery", tdp.MinimumWatts),
                new HandheldTdpMode("balanced", "Balanced", balanced),
                new HandheldTdpMode("performance", "Performance", performance),
            ],
            true,
            new(false, false, 0, 0, []),
            new(false, 0, 0, 0),
            new(false, string.Empty, []),
            tdp);
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
        detected,
        new(true, true, 0, 100, ["solid", "dual-zone"]),
        new(true, 0, 100, HandheldControllerSettingsStore.MsiClawA8DefaultVibrationStrengthPercent),
        new(
            true,
            "MSI Center M",
            [
                new("m1", "M1", "Left rear macro paddle."),
                new("m2", "M2", "Right rear macro paddle."),
                new("msi-center", "MSI Center Button", "Front button that normally opens MSI Center M."),
                new("quick-settings", "Quick Settings Button", "Front button that normally opens MSI Quick Settings."),
            ]),
        // Z2 Extreme with MSI's OEM limits (SPL 35, SPPT 40, FPPT 48 W).
        new HandheldTdpCapability("AMD Ryzen Z2 Extreme", "StrixPoint", 15, 35, 20, 40, 48, 5, 13,
            LimitSource: HandheldTdpLimitSource.Manufacturer));

    /// <summary>Win32_ComputerSystem.Model (Lenovo puts the machine type, e.g. 83E1, here).</summary>
    private static string ReadSystemModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                return Convert.ToString(item["Model"])?.Trim() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

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
