namespace SteamLoader.App.Models;

public sealed record ControllerShortcutSettingsSnapshot(
    IReadOnlyList<string> SteamMenuButtons,
    IReadOnlyList<string> SteamQuickAccessButtons,
    IReadOnlyList<string> InGameOverlayButtons,
    IReadOnlyList<string> InGameQuickAccessButtons,
    int SteamHoldMilliseconds,
    int InGameOverlayHoldMilliseconds,
    int InGameQuickAccessHoldMilliseconds)
{
    public const string DefaultSteamButton = "back";
    public const string DefaultInGameButton = "start";
    public const int DefaultSteamHoldMilliseconds = 1050;
    public const int DefaultInGameOverlayHoldMilliseconds = 1050;
    public const int DefaultInGameQuickAccessHoldMilliseconds = 3300;
    public const int MinimumHoldMilliseconds = 250;
    public const int MaximumPrimaryHoldMilliseconds = 5000;
    public const int MaximumQuickAccessHoldMilliseconds = 8000;
    public const int MinimumExtendedHoldGapMilliseconds = 250;
    public const int MaximumButtonsPerCombination = 3;

    private static readonly HashSet<string> SupportedButtonIds = new(
        [
            "back",
            "start",
            "left-bumper",
            "right-bumper",
            "left-stick",
            "right-stick",
            "a",
            "b",
            "x",
            "y",
            "dpad-up",
            "dpad-down",
            "dpad-left",
            "dpad-right"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static ControllerShortcutSettingsSnapshot Default { get; } = new(
        [DefaultSteamButton],
        [DefaultSteamButton],
        [DefaultInGameButton],
        [DefaultInGameButton],
        DefaultSteamHoldMilliseconds,
        DefaultInGameOverlayHoldMilliseconds,
        DefaultInGameQuickAccessHoldMilliseconds);

    // Compatibility helpers for code and saved settings from the earlier
    // single-button implementation. New UI uses the per-action combinations.
    public string SteamButton => SteamMenuButtons.FirstOrDefault() ?? DefaultSteamButton;

    public string InGameButton => InGameOverlayButtons.FirstOrDefault() ?? DefaultInGameButton;

    public static ControllerShortcutSettingsSnapshot Normalize(
        IEnumerable<string>? steamMenuButtons,
        IEnumerable<string>? steamQuickAccessButtons,
        IEnumerable<string>? inGameOverlayButtons,
        IEnumerable<string>? inGameQuickAccessButtons,
        string? legacySteamButton,
        string? legacyInGameButton,
        int? steamHoldMilliseconds,
        int? inGameOverlayHoldMilliseconds,
        int? inGameQuickAccessHoldMilliseconds)
    {
        var legacySteamCombination = NormalizeCombination(
            legacySteamButton is null ? null : [legacySteamButton],
            [DefaultSteamButton]);
        var legacyInGameCombination = NormalizeCombination(
            legacyInGameButton is null ? null : [legacyInGameButton],
            [DefaultInGameButton]);
        var overlayHold = Math.Clamp(
            inGameOverlayHoldMilliseconds ?? DefaultInGameOverlayHoldMilliseconds,
            MinimumHoldMilliseconds,
            MaximumPrimaryHoldMilliseconds);
        var quickAccessHold = Math.Clamp(
            inGameQuickAccessHoldMilliseconds ?? DefaultInGameQuickAccessHoldMilliseconds,
            overlayHold + MinimumExtendedHoldGapMilliseconds,
            MaximumQuickAccessHoldMilliseconds);

        return new ControllerShortcutSettingsSnapshot(
            NormalizeCombination(steamMenuButtons, legacySteamCombination),
            NormalizeCombination(steamQuickAccessButtons, legacySteamCombination),
            NormalizeCombination(inGameOverlayButtons, legacyInGameCombination),
            NormalizeCombination(inGameQuickAccessButtons, legacyInGameCombination),
            Math.Clamp(
                steamHoldMilliseconds ?? DefaultSteamHoldMilliseconds,
                MinimumHoldMilliseconds,
                MaximumPrimaryHoldMilliseconds),
            overlayHold,
            quickAccessHold);
    }

    public static IReadOnlyList<string> NormalizeCombination(
        IEnumerable<string>? buttonIds,
        IEnumerable<string> fallback)
    {
        var normalized = (buttonIds ?? [])
            .Select(buttonId => (buttonId ?? string.Empty).Trim().ToLowerInvariant())
            .Where(SupportedButtonIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumButtonsPerCombination)
            .ToArray();
        if (normalized.Length > 0)
        {
            return normalized;
        }

        var normalizedFallback = fallback
            .Select(buttonId => (buttonId ?? string.Empty).Trim().ToLowerInvariant())
            .Where(SupportedButtonIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumButtonsPerCombination)
            .ToArray();
        return normalizedFallback.Length > 0 ? normalizedFallback : [DefaultSteamButton];
    }

    public static string NormalizeButton(string? buttonId, string fallback)
    {
        return NormalizeCombination(
            buttonId is null ? null : [buttonId],
            [fallback])[0];
    }

    public static bool IsSupportedButton(string? buttonId)
    {
        return !string.IsNullOrWhiteSpace(buttonId) &&
            SupportedButtonIds.Contains(buttonId.Trim());
    }
}
