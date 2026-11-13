using SteamLoader.App.Infrastructure.Audio;
using SteamLoader.App.Infrastructure.Display;
using SteamLoader.App.Infrastructure.SystemTools;

namespace SteamLoader.App.Infrastructure.PerfTab;

public sealed record QuickSettingsVolume(bool Available, int Volume, bool Muted, string DeviceName);

public sealed record QuickSettingsResolution(string Id, string Title, string Description, bool Selected);

public sealed record QuickSettingsDisplay(
    bool BrightnessSupported,
    int Brightness,
    IReadOnlyList<QuickSettingsResolution> Resolutions,
    bool HdrSupported,
    bool HdrEnabled);

public sealed record QuickSettingsDevice(string Id, string Name, bool Selected);

public sealed record QuickSettingsApp(string Id, string Name, int Volume, bool Muted);

public sealed record QuickSettingsState(
    QuickSettingsVolume Output,
    QuickSettingsVolume Input,
    QuickSettingsDisplay Display,
    IReadOnlyList<QuickSettingsDevice> OutputDevices,
    IReadOnlyList<QuickSettingsDevice> InputDevices,
    IReadOnlyList<QuickSettingsApp> Apps);

/// <summary>
/// The rows TFS adds to Steam's own Quick Settings tab: system and microphone
/// volume (Steam on Windows only offers the device pickers) and a display
/// section (brightness, resolution, HDR, internal/external screen).
/// </summary>
public sealed class QuickSettingsService
{
    private readonly IAudioOutputDeviceService _audio;
    private readonly DisplaySwitchService _display;
    private readonly HdrDisplayService _hdr;

    public QuickSettingsService(IAudioOutputDeviceService audio, DisplaySwitchService display, HdrDisplayService hdr)
    {
        _audio = audio;
        _display = display;
        _hdr = hdr;
    }

    public QuickSettingsState GetState() => new(
        ReadOutput(),
        ReadInput(),
        ReadDisplay(),
        ReadDevices(_audio.GetPlaybackDevices),
        ReadDevices(_audio.GetCaptureDevices),
        ReadApps());

    public QuickSettingsState SetOutputDevice(string deviceId)
    {
        _audio.SetDefaultPlaybackDevice(RequireDevice(_audio.GetPlaybackDevices(), deviceId));
        return GetState();
    }

    public QuickSettingsState SetInputDevice(string deviceId)
    {
        _audio.SetDefaultCaptureDevice(RequireDevice(_audio.GetCaptureDevices(), deviceId));
        return GetState();
    }

    public QuickSettingsState SetAppVolume(string sessionId, int volume)
    {
        _audio.SetMixerSessionVolume(RequireSession(sessionId), Math.Clamp(volume, 0, 100));
        return GetState();
    }

    public QuickSettingsState SetAppMuted(string sessionId, bool muted)
    {
        var id = RequireSession(sessionId);
        var session = _audio.GetActiveMixerSessions().FirstOrDefault(candidate => candidate.SessionId == id);
        if (session is not null && session.IsMuted != muted)
        {
            _audio.ToggleMixerSessionMute(id);
        }

        return GetState();
    }

    private static string RequireDevice(IReadOnlyList<Models.AudioOutputDeviceInfo> devices, string deviceId)
        => devices.Any(device => string.Equals(device.Id, deviceId, StringComparison.Ordinal))
            ? deviceId
            : throw new ArgumentException("The audio device is no longer available.");

    private static string RequireSession(string sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? throw new ArgumentException("No app was selected.") : sessionId;

    /// <summary>
    /// Short endpoint names ("Speakers") unless several devices share one; then the
    /// adapter name is added ("Speakers (Realtek Audio)") so the entries stay distinguishable.
    /// </summary>
    internal static IReadOnlyList<QuickSettingsDevice> ToDevices(IEnumerable<Models.AudioOutputDeviceInfo> devices)
    {
        var list = devices.ToArray();
        static string ShortName(Models.AudioOutputDeviceInfo device)
            => string.IsNullOrWhiteSpace(device.DisplayName) ? device.Name : device.DisplayName;
        var duplicates = list
            .GroupBy(ShortName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list
            .Select(device =>
            {
                var name = ShortName(device);
                if (duplicates.Contains(name))
                {
                    name = !string.IsNullOrWhiteSpace(device.InterfaceName)
                        ? $"{name} ({device.InterfaceName})"
                        : string.IsNullOrWhiteSpace(device.Name) ? name : device.Name;
                }

                return new QuickSettingsDevice(device.Id, name, device.IsDefault);
            })
            .ToArray();
    }

    private static IReadOnlyList<QuickSettingsDevice> ReadDevices(Func<IReadOnlyList<Models.AudioOutputDeviceInfo>> read)
    {
        try
        {
            return ToDevices(read());
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    private IReadOnlyList<QuickSettingsApp> ReadApps()
    {
        try
        {
            return _audio.GetActiveMixerSessions()
                .Where(session => !session.IsSystemSession)
                .Select(session => new QuickSettingsApp(session.SessionId, session.DisplayName, (int)Math.Round(session.Volume), session.IsMuted))
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    public QuickSettingsState SetOutputVolume(int volume)
    {
        _audio.SetDefaultPlaybackVolume(Math.Clamp(volume, 0, 100));
        return GetState();
    }

    public QuickSettingsState SetInputVolume(int volume)
    {
        _audio.SetDefaultCaptureVolume(Math.Clamp(volume, 0, 100));
        return GetState();
    }

    public QuickSettingsState SetOutputMuted(bool muted)
    {
        if (_audio.GetDefaultPlaybackVolume().IsMuted != muted)
        {
            _audio.ToggleDefaultPlaybackMute();
        }

        return GetState();
    }

    public QuickSettingsState SetInputMuted(bool muted)
    {
        if (_audio.GetDefaultCaptureVolume().IsMuted != muted)
        {
            _audio.ToggleDefaultCaptureMute();
        }

        return GetState();
    }

    public QuickSettingsState SetBrightness(int brightness)
    {
        _display.SetBrightness(Math.Clamp(brightness, 0, 100));
        return GetState();
    }

    public QuickSettingsState SetResolution(string presetId)
    {
        _display.SetResolutionPreset(presetId);
        return GetState();
    }

    public QuickSettingsState SetHdr(bool enabled)
    {
        _hdr.SetEnabled(enabled);
        return GetState();
    }

    /// <summary>
    /// Built-in or external screen only, through Windows' own display switch
    /// (Win + P). Formerly the TFS "Display" Quick Access plugin.
    /// </summary>
    public QuickSettingsState SetDisplayOutput(string mode)
    {
        switch (mode)
        {
            case "internal":
                _display.SwitchToInternalDisplay();
                break;
            case "external":
                _display.SwitchToExternalDisplay();
                break;
            default:
                throw new ArgumentException("Unknown display output.");
        }

        return GetState();
    }

    private QuickSettingsVolume ReadOutput()
    {
        try
        {
            var volume = _audio.GetDefaultPlaybackVolume();
            return new(true, (int)Math.Round(volume.Volume), volume.IsMuted, volume.DeviceName);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return new(false, 0, false, string.Empty);
        }
    }

    private QuickSettingsVolume ReadInput()
    {
        try
        {
            var volume = _audio.GetDefaultCaptureVolume();
            return new(!string.IsNullOrWhiteSpace(volume.DeviceId), (int)Math.Round(volume.Volume), volume.IsMuted, volume.DeviceName);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return new(false, 0, false, string.Empty);
        }
    }

    private QuickSettingsDisplay ReadDisplay()
    {
        var resolutions = Array.Empty<QuickSettingsResolution>();
        var brightnessSupported = false;
        var brightness = 0;
        try
        {
            var modes = _display.GetModeSnapshot();
            resolutions = modes.ResolutionPresets
                .Where(preset => preset.Available)
                .Select(preset => new QuickSettingsResolution(preset.Id, preset.Title, preset.Description, preset.Selected))
                .ToArray();
            brightnessSupported = modes.Brightness.Supported;
            brightness = modes.Brightness.Value;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        var hdr = _hdr.GetSnapshot();
        return new QuickSettingsDisplay(brightnessSupported, brightness, resolutions, hdr.Available && hdr.Supported, hdr.Enabled);
    }
}
