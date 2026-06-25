using System.Collections.Generic;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Audio;

public interface IAudioOutputDeviceService
{
    IReadOnlyList<AudioOutputDeviceInfo> GetPlaybackDevices();

    IReadOnlyList<AudioOutputDeviceInfo> GetCaptureDevices();

    void SetDefaultPlaybackDevice(string deviceId);

    void SetDefaultCaptureDevice(string deviceId);

    AudioVolumeInfo GetDefaultPlaybackVolume();

    AudioVolumeInfo GetDefaultCaptureVolume();

    AudioVolumeInfo SetDefaultPlaybackVolume(double volume);

    AudioVolumeInfo SetDefaultCaptureVolume(double volume);

    AudioVolumeInfo AdjustDefaultPlaybackVolume(double delta);

    AudioVolumeInfo AdjustDefaultCaptureVolume(double delta);

    AudioVolumeInfo ToggleDefaultPlaybackMute();

    AudioVolumeInfo ToggleDefaultCaptureMute();

    AudioDashboardSnapshot GetDashboardSnapshot();

    IReadOnlyList<AudioMixerSessionInfo> GetActiveMixerSessions();

    AudioMixerSessionInfo SetMixerSessionVolume(string sessionId, double volume);

    AudioMixerSessionInfo ToggleMixerSessionMute(string sessionId);
}
