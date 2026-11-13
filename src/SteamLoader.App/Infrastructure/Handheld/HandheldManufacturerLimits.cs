using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>TDP range a device maker allows (SPL), optionally lower on battery.</summary>
public sealed record HandheldManufacturerTdp(int MinimumWatts, int MaximumWatts, int MaximumBatteryWatts, string Source, string Note);

/// <summary>
/// Maker limits win over AMD's chip range: TFS never offers more than the vendor's own
/// software does. Order: the vendor app's config on this device (AYASpace), then the
/// built-in table of known handhelds. Unknown devices get a safe range (see <see cref="ApplyUnknown"/>).
/// Where sources disagree, the table keeps the lower maximum: too little power costs
/// frames, too much can damage a device.
/// </summary>
public static class HandheldManufacturerLimits
{
    /// <param name="Manufacturer">Board or system manufacturer contains this text.</param>
    /// <param name="Models">Board product or system model equals one of these (empty = any).</param>
    /// <param name="Cpu">Processor name contains this text (empty = any).</param>
    private sealed record Entry(string Manufacturer, string[] Models, string Cpu, int Minimum, int Maximum, int Battery, string Note);

    private static readonly Entry[] Known =
    [
        // Valve Steam Deck LCD (Jupiter) / OLED (Galileo): SteamOS TDP slider 3-15 W.
        new("Valve", ["Jupiter", "Galileo"], "", 3, 15, 0, "SteamOS TDP limit 3-15 W"),
        // ASUS ROG Ally / Ally X: Armoury Crate manual mode, Turbo 30 W plugged in, 25 W on battery.
        new("ASUSTeK", ["RC71L"], "", 7, 30, 25, "Armoury Crate: 30 W plugged in, 25 W on battery"),
        new("ASUSTeK", ["RC72LA"], "", 7, 30, 25, "Armoury Crate: 30 W plugged in, 25 W on battery"),
        // ROG Xbox Ally X (Z2 Extreme): Turbo 35 W plugged in, 25 W on battery.
        new("ASUSTeK", ["RC73XA"], "", 7, 35, 25, "Armoury Crate: 35 W plugged in, 25 W on battery"),
        // ROG Xbox Ally (Z2 A): Turbo 20 W.
        new("ASUSTeK", ["RC73YA"], "", 7, 20, 0, "Armoury Crate Turbo 20 W"),
        // Lenovo Legion Go (Z1 Extreme): Legion Space custom mode 5-30 W (Lenovo user guide).
        // Lenovo's board product (LNVNB161216) is shared by many models, so match the machine type.
        new("LENOVO", ["83E1"], "", 5, 30, 0, "Legion Space custom mode 5-30 W"),
        new("LENOVO", [], "Ryzen Z1", 5, 30, 0, "Legion Space custom mode 5-30 W"),
        // Legion Go 2 (Z2 Extreme / Z2): custom mode 5-35 W.
        new("LENOVO", [], "Ryzen Z2 Extreme", 5, 35, 0, "Legion Space custom mode 5-35 W"),
        // Legion Go S (Z2 Go): reports range from 30 to 40 W; the documented 30 W is used.
        new("LENOVO", [], "Ryzen Z2 Go", 5, 30, 0, "Legion Space, conservative 30 W"),
        new("LENOVO", [], "Ryzen Z2", 5, 35, 0, "Legion Space custom mode 5-35 W"),
        // AYANEO 3: AYASpace manual TDP 5-35 W (read from AYASpace on the device).
        new("AYANEO", ["AYANEO 3"], "", 5, 35, 0, "AYASpace manual TDP 5-35 W"),
    ];

    public static HandheldManufacturerTdp? Resolve(
        string manufacturer,
        string product,
        string cpuName,
        HandheldManufacturerTdp? vendorApp,
        string model = "")
    {
        if (vendorApp is not null)
        {
            return vendorApp;
        }

        var entry = Known.FirstOrDefault(candidate =>
            manufacturer.Contains(candidate.Manufacturer, StringComparison.OrdinalIgnoreCase) &&
            (candidate.Models.Length == 0 || candidate.Models.Any(name =>
                string.Equals(product.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Trim(), name, StringComparison.OrdinalIgnoreCase))) &&
            (candidate.Cpu.Length == 0 || ContainsWord(cpuName, candidate.Cpu)));
        return entry is null
            ? null
            : new HandheldManufacturerTdp(entry.Minimum, entry.Maximum, entry.Battery, HandheldTdpLimitSource.Manufacturer, entry.Note);
    }

    /// <summary>
    /// Whole-word match, so "Ryzen Z2" does not match "Ryzen Z2A". The table lists longer
    /// names first ("Ryzen Z2 Go" before "Ryzen Z2"), and the first match wins.
    /// </summary>
    private static bool ContainsWord(string text, string word)
    {
        text = NormalizeCpu(text);
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var end = index + word.Length;
        return end >= text.Length || !char.IsLetterOrDigit(text[end]);
    }

    /// <summary>"AMD Ryzen AI Z2 Extreme" (Xbox Ally X) and "AMD Ryzen Z2 Extreme" are the same chip.</summary>
    private static string NormalizeCpu(string cpu)
        => cpu.Replace("Ryzen AI Z", "Ryzen Z", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the vendor app's own limits on this device; only AYASpace so far.</summary>
    public static HandheldManufacturerTdp? ReadVendorApp(string manufacturer)
    {
        if (!manufacturer.Contains("AYANEO", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AYASpace", "Config", "PerformanceV2", "PerformanceCfg.json");
        try
        {
            return File.Exists(path) ? ParseAyaSpace(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>AYASpace stores one entry per power plan with tdp.minTDP / tdp.maxTDP.</summary>
    internal static HandheldManufacturerTdp? ParseAyaSpace(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var minimum = int.MaxValue;
            var maximum = 0;
            foreach (var plan in document.RootElement.EnumerateArray())
            {
                if (plan.ValueKind != JsonValueKind.Object ||
                    !plan.TryGetProperty("tdp", out var tdp) || tdp.ValueKind != JsonValueKind.Object ||
                    !tdp.TryGetProperty("minTDP", out var min) || !min.TryGetInt32(out var minWatts) ||
                    !tdp.TryGetProperty("maxTDP", out var max) || !max.TryGetInt32(out var maxWatts))
                {
                    continue;
                }

                minimum = Math.Min(minimum, minWatts);
                maximum = Math.Max(maximum, maxWatts);
            }

            return maximum is > 0 and <= 150 && minimum is > 0 && minimum < maximum
                ? new HandheldManufacturerTdp(minimum, maximum, 0, HandheldTdpLimitSource.VendorApp, $"AYASpace {minimum}-{maximum} W")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Narrows the AMD chip range to the maker's range (the maker may go below AMD's minimum).</summary>
    public static HandheldTdpCapability Apply(HandheldTdpCapability chip, HandheldManufacturerTdp? maker)
    {
        if (maker is null)
        {
            return ApplyUnknown(chip);
        }

        var maximum = Math.Clamp(maker.MaximumWatts, 1, chip.MaximumWatts);
        var minimum = Math.Clamp(maker.MinimumWatts, 1, maximum);
        var battery = maker.MaximumBatteryWatts > 0 ? Math.Clamp(maker.MaximumBatteryWatts, minimum, maximum) : 0;
        return chip with
        {
            MinimumWatts = minimum,
            MaximumWatts = maximum,
            DefaultWatts = Math.Clamp(chip.DefaultWatts, minimum, maximum),
            MaximumSpptWatts = maximum,
            MaximumFpptWatts = maximum,
            MaximumBatteryWatts = battery,
            LimitSource = maker.Source,
        };
    }

    /// <summary>
    /// No maker limits known. Handheld chips (Ryzen Z, Steam Deck APU) are designed for
    /// handheld cooling and keep their AMD range. Laptop chips in an unknown device stop at
    /// AMD's default TDP, which every device built around them must be able to cool.
    /// </summary>
    public static HandheldTdpCapability ApplyUnknown(HandheldTdpCapability chip)
    {
        var cpu = NormalizeCpu(chip.CpuName);
        var handheldChip = cpu.Contains("Ryzen Z", StringComparison.OrdinalIgnoreCase) ||
            cpu.Contains("Custom APU", StringComparison.OrdinalIgnoreCase);
        if (handheldChip)
        {
            return chip with { LimitSource = HandheldTdpLimitSource.AmdChip };
        }

        var maximum = Math.Clamp(chip.DefaultWatts, chip.MinimumWatts, chip.MaximumWatts);
        return chip with
        {
            MaximumWatts = maximum,
            DefaultWatts = maximum,
            MaximumSpptWatts = maximum,
            MaximumFpptWatts = maximum,
            LimitSource = HandheldTdpLimitSource.AmdDefault,
        };
    }
}
