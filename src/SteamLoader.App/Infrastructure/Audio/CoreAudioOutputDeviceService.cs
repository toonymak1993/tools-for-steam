using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using AudioSwitcher.AudioApi.Session;
using System.Diagnostics;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Audio;

public sealed class CoreAudioOutputDeviceService : IAudioOutputDeviceService
{
    private readonly CoreAudioController _controller = new();

    public IReadOnlyList<AudioOutputDeviceInfo> GetPlaybackDevices()
    {
        return CreateDeviceList(
            _controller.GetPlaybackDevices(DeviceState.Active));
    }

    public IReadOnlyList<AudioOutputDeviceInfo> GetCaptureDevices()
    {
        return CreateDeviceList(
            _controller.GetCaptureDevices(DeviceState.Active));
    }

    public void SetDefaultPlaybackDevice(string deviceId)
    {
        SetDefaultDevice(deviceId);
    }

    public void SetDefaultCaptureDevice(string deviceId)
    {
        SetDefaultDevice(deviceId);
    }

    public AudioVolumeInfo GetDefaultPlaybackVolume()
    {
        return CreateVolumeInfo(GetDefaultPlaybackDevice());
    }

    public AudioVolumeInfo GetDefaultCaptureVolume()
    {
        return CreateVolumeInfo(GetDefaultCaptureDevice());
    }

    public AudioVolumeInfo SetDefaultPlaybackVolume(double volume)
    {
        return SetDeviceVolume(GetDefaultPlaybackDevice(), volume);
    }

    public AudioVolumeInfo SetDefaultCaptureVolume(double volume)
    {
        return SetDeviceVolume(GetDefaultCaptureDevice(), volume);
    }

    public AudioVolumeInfo AdjustDefaultPlaybackVolume(double delta)
    {
        return AdjustDeviceVolume(GetDefaultPlaybackDevice(), delta);
    }

    public AudioVolumeInfo AdjustDefaultCaptureVolume(double delta)
    {
        return AdjustDeviceVolume(GetDefaultCaptureDevice(), delta);
    }

    public AudioVolumeInfo ToggleDefaultPlaybackMute()
    {
        var device = GetDefaultPlaybackDevice();
        device.ToggleMute();
        return CreateVolumeInfo(device);
    }

    public AudioVolumeInfo ToggleDefaultCaptureMute()
    {
        var device = GetDefaultCaptureDevice();
        device.ToggleMute();
        return CreateVolumeInfo(device);
    }

    public AudioDashboardSnapshot GetDashboardSnapshot()
    {
        var playbackDevice = GetDefaultPlaybackDevice();
        var captureDevice = GetDefaultCaptureDevice();

        return new AudioDashboardSnapshot(
            CreateVolumeInfo(playbackDevice),
            CreateVolumeInfo(captureDevice),
            GetPlaybackDevices(),
            GetCaptureDevices(),
            GetMixerSessionGroups(playbackDevice)
                .Select(group => group.Info)
                .ToArray());
    }

    public IReadOnlyList<AudioMixerSessionInfo> GetActiveMixerSessions()
    {
        return GetMixerSessionGroups(GetDefaultPlaybackDevice())
            .Select(group => group.Info)
            .ToArray();
    }

    public AudioMixerSessionInfo SetMixerSessionVolume(string sessionId, double volume)
    {
        var group = GetMixerSessionGroup(GetDefaultPlaybackDevice(), sessionId);
        var nextVolume = Math.Clamp(volume, 0d, 100d);

        foreach (var session in group.Sessions)
        {
            session.Volume = nextVolume;
            if (nextVolume > 0d)
            {
                session.IsMuted = false;
            }
        }

        return BuildMixerSessionInfo(group);
    }

    public AudioMixerSessionInfo ToggleMixerSessionMute(string sessionId)
    {
        var group = GetMixerSessionGroup(GetDefaultPlaybackDevice(), sessionId);
        var shouldMute = !AreAllSessionsMuted(group.Sessions);

        foreach (var session in group.Sessions)
        {
            session.IsMuted = shouldMute;
        }

        return BuildMixerSessionInfo(group);
    }

    private static AudioVolumeInfo CreateVolumeInfo(CoreAudioDevice device)
    {
        return new AudioVolumeInfo(
            device.Id.ToString(),
            device.FullName,
            device.Volume,
            device.IsMuted);
    }

    private static IReadOnlyList<AudioOutputDeviceInfo> CreateDeviceList(
        IEnumerable<CoreAudioDevice> devices)
    {
        return devices
            .Select(device => new AudioOutputDeviceInfo(
                device.Id.ToString(),
                device.FullName,
                device.IsDefaultDevice))
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device ID is required.", nameof(deviceId));
        }

        var device = _controller.GetDevice(Guid.Parse(deviceId));
        if (device is null)
        {
            throw new InvalidOperationException("The requested device was not found.");
        }

        device.SetAsDefault();
        device.SetAsDefaultCommunications();
    }

    private static AudioVolumeInfo SetDeviceVolume(CoreAudioDevice device, double volume)
    {
        device.Volume = Math.Clamp(volume, 0d, 100d);
        if (device.Volume > 0d && device.IsMuted)
        {
            device.ToggleMute();
        }

        return CreateVolumeInfo(device);
    }

    private static AudioVolumeInfo AdjustDeviceVolume(CoreAudioDevice device, double delta)
    {
        device.Volume = Math.Clamp(device.Volume + delta, 0d, 100d);
        if (device.Volume > 0d && device.IsMuted)
        {
            device.ToggleMute();
        }

        return CreateVolumeInfo(device);
    }

    private CoreAudioDevice GetDefaultPlaybackDevice()
    {
        var device = _controller.DefaultPlaybackDevice;
        if (device is null)
        {
            throw new InvalidOperationException("No default playback device is available.");
        }

        return device;
    }

    private CoreAudioDevice GetDefaultCaptureDevice()
    {
        var device = _controller.DefaultCaptureDevice;
        if (device is null)
        {
            throw new InvalidOperationException("No default capture device is available.");
        }

        return device;
    }

    private static MixerSessionGroup GetMixerSessionGroup(CoreAudioDevice device, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session ID is required.", nameof(sessionId));
        }

        return GetMixerSessionGroups(device).FirstOrDefault(group =>
                   string.Equals(group.GroupId, sessionId, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("The requested audio session was not found.");
    }

    private static IReadOnlyList<MixerSessionGroup> GetMixerSessionGroups(CoreAudioDevice device)
    {
        var sessionController = device.SessionController;
        if (sessionController is null)
        {
            return Array.Empty<MixerSessionGroup>();
        }

        return sessionController
            .ActiveSessions()
            .Where(session => session is not null)
            .GroupBy(GetMixerGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(CreateMixerSessionGroup)
            .Where(group => group is not null)
            .OrderBy(group => group!.IsSystemSession)
            .ThenBy(group => group!.SortName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group!.ProcessId ?? int.MaxValue)
            .Cast<MixerSessionGroup>()
            .ToArray();
    }

    private static MixerSessionGroup? CreateMixerSessionGroup(IGrouping<string, IAudioSession> grouping)
    {
        var sessions = grouping
            .Where(session => session is not null)
            .ToArray();

        if (sessions.Length == 0)
        {
            return null;
        }

        var primarySession = sessions
            .OrderByDescending(session => !string.IsNullOrWhiteSpace(session.DisplayName))
            .ThenByDescending(session => !string.IsNullOrWhiteSpace(session.ExecutablePath))
            .ThenBy(session => session.Id, StringComparer.OrdinalIgnoreCase)
            .First();

        var displayName = ResolveMixerDisplayName(primarySession);
        var secondaryLabel = ResolveMixerSecondaryLabel(primarySession, sessions.Length);
        int? processId = primarySession.ProcessId > 0 ? primarySession.ProcessId : null;
        var isSystemSession = primarySession.IsSystemSession;

        return new MixerSessionGroup(
            grouping.Key,
            displayName,
            secondaryLabel,
            processId,
            isSystemSession,
            displayName,
            sessions);
    }

    private static AudioMixerSessionInfo BuildMixerSessionInfo(MixerSessionGroup group)
    {
        var averageVolume = group.Sessions.Length == 0
            ? 0d
            : group.Sessions.Average(session => Math.Clamp(session.Volume, 0d, 100d));

        return new AudioMixerSessionInfo(
            group.GroupId,
            group.DisplayName,
            group.SecondaryLabel,
            group.ProcessId,
            group.IsSystemSession,
            Math.Round(averageVolume, MidpointRounding.AwayFromZero),
            AreAllSessionsMuted(group.Sessions),
            group.Sessions.Length);
    }

    private static bool AreAllSessionsMuted(IReadOnlyList<IAudioSession> sessions)
    {
        return sessions.Count > 0 && sessions.All(IsSessionEffectivelyMuted);
    }

    private static bool IsSessionEffectivelyMuted(IAudioSession session)
    {
        return session.IsMuted || session.Volume <= 0.5d;
    }

    private static string GetMixerGroupKey(IAudioSession session)
    {
        if (session.ProcessId > 0)
        {
            return $"pid:{session.ProcessId}";
        }

        if (session.IsSystemSession)
        {
            var systemLabel = !string.IsNullOrWhiteSpace(session.DisplayName)
                ? session.DisplayName
                : session.Id;
            return $"system:{NormalizeKey(systemLabel)}";
        }

        if (!string.IsNullOrWhiteSpace(session.ExecutablePath))
        {
            return $"exe:{NormalizeKey(session.ExecutablePath)}";
        }

        return $"session:{NormalizeKey(session.Id)}";
    }

    private static string ResolveMixerDisplayName(IAudioSession session)
    {
        var explicitName = session.DisplayName?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        var processName = TryResolveProcessFriendlyName(session.ProcessId);
        if (!string.IsNullOrWhiteSpace(processName))
        {
            return processName;
        }

        var executableStem = GetExecutableStem(session.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(executableStem))
        {
            return executableStem;
        }

        return session.IsSystemSession
            ? "System Sounds"
            : session.ProcessId > 0
                ? $"Process {session.ProcessId}"
                : "Unknown Audio Session";
    }

    private static string ResolveMixerSecondaryLabel(IAudioSession session, int sessionCount)
    {
        var segments = new List<string>();

        var executableName = GetExecutableName(session.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(executableName))
        {
            segments.Add(executableName);
        }

        if (session.ProcessId > 0)
        {
            segments.Add($"PID {session.ProcessId}");
        }
        else if (session.IsSystemSession)
        {
            segments.Add("Windows audio");
        }

        if (sessionCount > 1)
        {
            segments.Add($"{sessionCount} streams");
        }

        return segments.Count > 0
            ? string.Join(" - ", segments.Distinct(StringComparer.OrdinalIgnoreCase))
            : "Active audio session";
    }

    private static string TryResolveProcessFriendlyName(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var description = process.MainModule?.FileVersionInfo?.FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            return process.ProcessName?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetExecutableStem(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileNameWithoutExtension(executablePath)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetExecutableName(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(executablePath)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private sealed record MixerSessionGroup(
        string GroupId,
        string DisplayName,
        string SecondaryLabel,
        int? ProcessId,
        bool IsSystemSession,
        string SortName,
        IAudioSession[] Sessions)
    {
        public AudioMixerSessionInfo Info => BuildMixerSessionInfo(this);
    }
}
