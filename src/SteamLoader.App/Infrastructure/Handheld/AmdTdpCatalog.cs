using System.Management;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// TDP range of one device. SPL is the sustained limit the user picks; SPPT (slow)
/// and FPPT (fast) follow it with a small boost headroom and never exceed their maximums.
/// </summary>
public sealed record HandheldTdpCapability(
    string CpuName,
    string Platform,
    int MinimumWatts,
    int MaximumWatts,
    int DefaultWatts,
    int MaximumSpptWatts,
    int MaximumFpptWatts,
    int SpptOffsetWatts,
    int FpptOffsetWatts,
    int MaximumBatteryWatts = 0,
    string LimitSource = HandheldTdpLimitSource.AmdChip)
{
    /// <summary>Highest SPL for the power source; on battery some vendors allow less.</summary>
    public int MaximumFor(string powerSource)
        => string.Equals(powerSource, "battery", StringComparison.OrdinalIgnoreCase) && MaximumBatteryWatts > 0
            ? Math.Clamp(MaximumBatteryWatts, MinimumWatts, MaximumWatts)
            : MaximumWatts;

    public int ResolveSppt(int splWatts, string powerSource = "ac")
    {
        var cap = MaximumSpptWatts - (MaximumWatts - MaximumFor(powerSource));
        return Math.Clamp(splWatts + SpptOffsetWatts, splWatts, Math.Max(splWatts, cap));
    }

    public int ResolveFppt(int splWatts, string powerSource = "ac")
    {
        var sppt = ResolveSppt(splWatts, powerSource);
        var cap = MaximumFpptWatts - (MaximumWatts - MaximumFor(powerSource));
        return Math.Clamp(splWatts + FpptOffsetWatts, sppt, Math.Max(sppt, cap));
    }
}

public static class HandheldTdpLimitSource
{
    /// <summary>Limits from the device maker (published or built into TFS).</summary>
    public const string Manufacturer = "manufacturer";

    /// <summary>Limits read from the maker's own control app on this device (e.g. AYASpace).</summary>
    public const string VendorApp = "vendor-app";

    /// <summary>No maker limits known, handheld chip: AMD's configurable TDP range of the chip.</summary>
    public const string AmdChip = "amd";

    /// <summary>No maker limits known, laptop chip: capped at AMD's default TDP to stay safe.</summary>
    public const string AmdDefault = "amd-default";
}

/// <summary>
/// TDP ranges for AMD mobile APUs, taken from AMD's published configurable TDP (cTDP)
/// per processor model. Device profiles may narrow these ranges further. A CPU that is
/// not listed here gets no TDP control, because a wrong range is worse than none.
/// </summary>
public static class AmdTdpCatalog
{
    private sealed record Entry(Regex Pattern, string Platform, int Minimum, int Maximum, int Default);

    // Order matters: more specific names first (Z1 Extreme before Z1, Z2 A before Z2).
    private static readonly Entry[] Entries =
    [
        // Strix Halo
        new(Pattern(@"Ryzen AI Max\+? (PRO )?\d{3}"), "StrixHalo", 45, 120, 55),
        // Strix Point
        new(Pattern(@"Ryzen AI (9 (HX )?(PRO )?3[67]\d|7 (PRO )?36\d)"), "StrixPoint", 15, 54, 28),
        // Krackan Point
        new(Pattern(@"Ryzen AI [57] (PRO )?3[345]\d"), "KrackanPoint", 15, 54, 28),
        // Handheld Z series
        // "Ryzen AI Z2 Extreme" is the Xbox Ally X name of the same chip.
        new(Pattern(@"Ryzen (AI )?Z2 Extreme"), "StrixPoint", 15, 35, 20),
        new(Pattern(@"Ryzen (AI )?Z2 A\b"), "VanGogh", 6, 20, 15),
        new(Pattern(@"Ryzen (AI )?Z2 Go"), "Rembrandt", 15, 30, 15),
        new(Pattern(@"Ryzen (AI )?Z2\b"), "HawkPoint", 15, 30, 15),
        new(Pattern(@"Ryzen Z1 Extreme"), "Phoenix", 9, 30, 15),
        new(Pattern(@"Ryzen Z1\b"), "Phoenix2", 9, 30, 15),
        // Steam Deck (Van Gogh custom APU 0405 / Sephiroth 0932)
        new(Pattern(@"Custom APU 0(405|932)"), "VanGogh", 3, 15, 15),
        // Phoenix / Hawk Point (7040 / 8040)
        new(Pattern(@"Ryzen [3579] (PRO )?[78][4-9]40U"), "Phoenix", 15, 30, 28),
        new(Pattern(@"Ryzen [3579] (PRO )?[78][4-9][4-9][05]HS?\b"), "Phoenix", 15, 54, 35),
        // Rembrandt / Rembrandt-R
        new(Pattern(@"Ryzen [3579] (PRO )?(6[6-9]\d0|7[3-7]35)U"), "Rembrandt", 15, 28, 28),
        new(Pattern(@"Ryzen [3579] (PRO )?(6[6-9]\d0|7[3-7]35)HS?\b"), "Rembrandt", 15, 45, 35),
        // Mendocino
        new(Pattern(@"Ryzen [3579] (PRO )?7[1-5]20U"), "Mendocino", 8, 15, 15),
        // Renoir / Lucienne / Cezanne / Barcelo
        new(Pattern(@"Ryzen [3579] (PRO )?([45]\d{2}[05]|7[3-7]30)U"), "Renoir", 10, 25, 15),
        new(Pattern(@"Ryzen [3579] (PRO )?[45]\d{2}[05]HS?\b"), "Renoir", 15, 45, 35),
        // Raven Ridge / Picasso / Dali
        new(Pattern(@"Ryzen [357] (PRO )?[23][2-7][05]0U"), "Raven", 12, 25, 15),
    ];

    private static Regex Pattern(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static HandheldTdpCapability? Resolve(string cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName) || !cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = Regex.Replace(cpuName.Trim(), @"\s+", " ");
        var entry = Entries.FirstOrDefault(candidate => candidate.Pattern.IsMatch(normalized));
        if (entry is null)
        {
            return null;
        }

        // Generic devices get boost headroom, but never above the chip's own cTDP maximum.
        return new HandheldTdpCapability(
            normalized,
            entry.Platform,
            entry.Minimum,
            entry.Maximum,
            Math.Clamp(entry.Default, entry.Minimum, entry.Maximum),
            entry.Maximum,
            entry.Maximum,
            SpptOffsetWatts: 5,
            FpptOffsetWatts: 10);
    }

    public static string ReadCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                return Convert.ToString(item["Name"])?.Trim() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }
}
