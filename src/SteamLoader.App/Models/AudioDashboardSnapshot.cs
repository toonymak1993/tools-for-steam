namespace SteamLoader.App.Models;

public sealed record AudioDashboardSnapshot(
    AudioVolumeInfo? PlaybackVolume,
    AudioVolumeInfo? CaptureVolume,
    IReadOnlyList<AudioOutputDeviceInfo> PlaybackDevices,
    IReadOnlyList<AudioOutputDeviceInfo> CaptureDevices,
    IReadOnlyList<AudioMixerSessionInfo> MixerSessions);
