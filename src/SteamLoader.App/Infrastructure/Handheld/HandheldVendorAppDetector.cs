using System.Diagnostics;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Finds running vendor tools that write their own power limits (and would overwrite
/// the TDP TFS sets). Matching is by process-name prefix; the result is cached briefly
/// because the Performance tab asks for it on every refresh.
/// </summary>
internal static class HandheldVendorAppDetector
{
    private static readonly (string Prefix, string Title)[] KnownApps =
    [
        ("AYASpace", "AYASpace"),
        ("ArmouryCrate", "Armoury Crate"),
        ("LegionSpace", "Legion Space"),
        ("MSI Center M", "MSI Center M"),
        ("MSI_Center_M", "MSI Center M"),
        ("OneXConsole", "OneXConsole"),
        ("HandheldCompanion", "Handheld Companion"),
        ("RyzenController", "Ryzen Controller"),
        ("AutoTDP", "AutoTDP"),
    ];

    private static readonly object Sync = new();
    private static IReadOnlyList<string> _cached = [];
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public static IReadOnlyList<string> FindRunning()
    {
        lock (Sync)
        {
            if (DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(10))
            {
                return _cached;
            }

            var found = new SortedSet<string>(StringComparer.Ordinal);
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                processes = [];
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    string name;
                    try
                    {
                        name = process.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    var match = Match(name);
                    if (match is not null)
                    {
                        found.Add(match);
                    }
                }
            }

            _cached = found.ToArray();
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
    }

    internal static string? Match(string processName)
    {
        foreach (var (prefix, title) in KnownApps)
        {
            if (processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return title;
            }
        }

        return null;
    }
}
