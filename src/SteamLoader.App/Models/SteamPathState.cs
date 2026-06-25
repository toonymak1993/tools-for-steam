namespace SteamLoader.App.Models;

public sealed record SteamPathState(
    string EffectivePath,
    string AutoDetectedPath,
    string ManualOverridePath,
    bool UsingManualOverride);
