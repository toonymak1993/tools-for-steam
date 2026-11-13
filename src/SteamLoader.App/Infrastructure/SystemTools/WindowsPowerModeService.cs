using System.Runtime.InteropServices;

namespace SteamLoader.App.Infrastructure.SystemTools;

/// <summary>
/// Windows 11 power mode (Settings › System › Power › Power mode): the
/// "overlay" on top of the Balanced plan. Switching needs no administrator.
/// </summary>
public static class WindowsPowerModeService
{
    public const string Efficiency = "efficiency";
    public const string Balanced = "balanced";
    public const string Performance = "performance";

    private static readonly Guid EfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedOverlay = Guid.Empty;
    private static readonly Guid PerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static IReadOnlyList<string> Modes { get; } = [Efficiency, Balanced, Performance];

    public static string? GetMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) != 0)
            {
                return null;
            }

            return ToMode(overlay);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static string? SetMode(string mode)
    {
        var overlay = mode switch
        {
            Efficiency => EfficiencyOverlay,
            Balanced => BalancedOverlay,
            Performance => PerformanceOverlay,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown power mode."),
        };
        var result = PowerSetActiveOverlayScheme(overlay);
        if (result != 0)
        {
            throw new InvalidOperationException($"Windows did not change the power mode (error {result}).");
        }

        return GetMode();
    }

    internal static string ToMode(Guid overlay) =>
        overlay == EfficiencyOverlay ? Efficiency
        : overlay == PerformanceOverlay ? Performance
        : Balanced;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
}
