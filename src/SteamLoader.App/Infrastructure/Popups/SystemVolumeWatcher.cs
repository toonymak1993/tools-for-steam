using System.Runtime.InteropServices;

namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>
/// Reports changes of the default playback device's master volume and mute
/// state through Windows' own endpoint notifications (no polling). Follows the
/// default device when it changes. Media keys and input are never hooked.
/// </summary>
internal sealed class SystemVolumeWatcher : IDisposable
{
    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly Action<float, bool> _changed;
    private readonly object _gate = new();
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private VolumeCallback? _volumeCallback;
    private DeviceCallback? _deviceCallback;
    private bool _disposed;

    public SystemVolumeWatcher(Action<float, bool> changed)
    {
        _changed = changed;
    }

    /// <summary>Starts listening; false when Windows audio is not available.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed || _enumerator is not null)
            {
                return _enumerator is not null;
            }

            try
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _deviceCallback = new DeviceCallback(this);
                Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_deviceCallback));
                AttachDefaultDevice();
                return true;
            }
            catch
            {
                DetachAll();
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            DetachAll();
        }
    }

    private void AttachDefaultDevice()
    {
        DetachVolume();
        if (_enumerator is null)
        {
            return;
        }

        if (_enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device) != 0 || device is null)
        {
            return;
        }

        try
        {
            var id = AudioEndpointVolumeId;
            if (device.Activate(ref id, ClsCtx.All, IntPtr.Zero, out var activated) != 0 || activated is not IAudioEndpointVolume volume)
            {
                return;
            }

            _volume = volume;
            _volumeCallback = new VolumeCallback(this);
            if (_volume.RegisterControlChangeNotify(_volumeCallback) != 0)
            {
                DetachVolume();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void DetachVolume()
    {
        if (_volume is not null)
        {
            try
            {
                if (_volumeCallback is not null)
                {
                    _volume.UnregisterControlChangeNotify(_volumeCallback);
                }
            }
            catch
            {
            }

            try
            {
                Marshal.ReleaseComObject(_volume);
            }
            catch
            {
            }
        }

        _volume = null;
        _volumeCallback = null;
    }

    private void DetachAll()
    {
        DetachVolume();
        if (_enumerator is not null)
        {
            try
            {
                if (_deviceCallback is not null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
                }
            }
            catch
            {
            }

            try
            {
                Marshal.ReleaseComObject(_enumerator);
            }
            catch
            {
            }
        }

        _enumerator = null;
        _deviceCallback = null;
    }

    private void OnDefaultDeviceChanged()
    {
        // Never re-register from inside Windows' callback thread.
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                if (!_disposed && _enumerator is not null)
                {
                    try
                    {
                        AttachDefaultDevice();
                    }
                    catch
                    {
                    }
                }
            }
        });
    }

    private void OnVolume(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        // AUDIO_VOLUME_NOTIFICATION_DATA: GUID (16), BOOL bMuted (4), float fMasterVolume (4), ...
        var muted = Marshal.ReadInt32(data, 16) != 0;
        var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
        try
        {
            _changed(Math.Clamp(level, 0f, 1f), muted);
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------
    // Core Audio interop
    // ------------------------------------------------------------------

    private enum DataFlow
    {
        Render = 0,
    }

    private enum Role
    {
        Multimedia = 1,
    }

    [Flags]
    private enum ClsCtx : uint
    {
        All = 0x17,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid id, ClsCtx context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);

        [PreserveSig]
        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    }

    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr notifyData);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(DataFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComVisible(true)]
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly SystemVolumeWatcher _owner;

        public VolumeCallback(SystemVolumeWatcher owner) => _owner = owner;

        public int OnNotify(IntPtr notifyData)
        {
            _owner.OnVolume(notifyData);
            return 0;
        }
    }

    [ComVisible(true)]
    private sealed class DeviceCallback : IMMNotificationClient
    {
        private readonly SystemVolumeWatcher _owner;

        public DeviceCallback(SystemVolumeWatcher owner) => _owner = owner;

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(DataFlow flow, Role role, string? defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                _owner.OnDefaultDeviceChanged();
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
