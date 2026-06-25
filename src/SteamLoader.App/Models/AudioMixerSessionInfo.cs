namespace SteamLoader.App.Models;

public sealed record AudioMixerSessionInfo(
    string SessionId,
    string DisplayName,
    string SecondaryLabel,
    int? ProcessId,
    bool IsSystemSession,
    double Volume,
    bool IsMuted,
    int SessionCount);
