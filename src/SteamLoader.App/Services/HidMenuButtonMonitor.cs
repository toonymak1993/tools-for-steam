using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SteamLoader.App.Services;

/// <summary>
/// Receives background Raw Input HID reports from gamepads/joysticks and tracks
/// whether the Xbox-style Menu / Start button (button usage 8) is currently
/// held. This path is used only while a non-Steam game owns the foreground so
/// Tools for Steam can raise the Steam overlay without affecting Big Picture's
/// own controller handling.
/// </summary>
public sealed class HidMenuButtonMonitor : IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const int GidcRemoval = 2;
    private const int RidInput = 0x10000003;
    private const int RidDevicePreparsedData = 0x20000005;
    private const int RidevInputSink = 0x00000100;
    private const int RidevDevNotify = 0x00002000;
    private const int RawInputTypeHid = 2;
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort JoystickUsage = 0x04;
    private const ushort GamepadUsage = 0x05;
    private const ushort ButtonUsagePage = 0x09;
    private const ushort XboxBackButtonUsage = 7;
    private const ushort XboxMenuButtonUsage = 8;
    private const int HidpStatusSuccess = 0x00110000;
    private const int HidpStatusUsageNotFound = unchecked((int)0xC0110004);
    private static readonly IntPtr MessageOnlyWindowHandle = new(-3);
    private static readonly int RawHidDataOffset =
        Marshal.OffsetOf<RawInputHidEnvelope>(nameof(RawInputHidEnvelope.Hid)).ToInt32() +
        Marshal.OffsetOf<RawHid>(nameof(RawHid.RawDataStart)).ToInt32();

    private readonly ManualResetEventSlim _readyEvent = new(false);
    private readonly Dictionary<nint, HidDeviceMetadata> _devices = [];
    private readonly Dictionary<nint, HidButtonState> _deviceStates = [];
    private readonly Dictionary<nint, string> _lastPublishedUsageSignatures = [];
    private readonly Thread _thread;

    private Dispatcher? _dispatcher;
    private HwndSource? _source;
    private bool _disposed;
    private volatile bool _isBackDown;
    private volatile bool _isMenuDown;

    public static ushort ExpectedMenuButtonUsage => XboxMenuButtonUsage;

    public event Action<HidMenuButtonReport>? ReportObserved;

    public HidMenuButtonMonitor()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "TFS HID Menu Monitor"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _readyEvent.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsMenuDown => _isMenuDown;

    public bool IsBackDown => _isBackDown;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        catch
        {
        }

        try
        {
            if (_thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
        }

        _readyEvent.Dispose();
        DisposeAllMetadata();
    }

    private void ThreadMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            var parameters = new HwndSourceParameters("TfsHidMenuButtonMonitor")
            {
                ParentWindow = MessageOnlyWindowHandle,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            RegisterRawInputTargets(_source.Handle);
        }
        catch
        {
        }
        finally
        {
            _readyEvent.Set();
        }

        if (_source is null || _dispatcher is null)
        {
            return;
        }

        Dispatcher.Run();

        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
        }
        finally
        {
            _source = null;
            _dispatcher = null;
        }
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case WmInput:
                ProcessRawInput(lParam);
                break;
            case WmInputDeviceChange when wParam.ToInt32() == GidcRemoval:
                RemoveDevice(lParam);
                break;
        }

        return 0;
    }

    private void ProcessRawInput(nint rawInputHandle)
    {
        uint rawInputSize = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();

        if (GetRawInputData(rawInputHandle, RidInput, nint.Zero, ref rawInputSize, headerSize) == unchecked((uint)-1) ||
            rawInputSize == 0)
        {
            return;
        }

        var rawInputBuffer = Marshal.AllocHGlobal((int)rawInputSize);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, rawInputBuffer, ref rawInputSize, headerSize) == unchecked((uint)-1))
            {
                return;
            }

            var envelope = Marshal.PtrToStructure<RawInputHidEnvelope>(rawInputBuffer);
            if (envelope.Header.Type != RawInputTypeHid || envelope.Hid.SizeHid == 0 || envelope.Hid.Count == 0)
            {
                return;
            }

            var metadata = GetOrCreateDeviceMetadata(envelope.Header.Device);
            if (metadata is null)
            {
                return;
            }

            var reportSize = (int)envelope.Hid.SizeHid;
            var reportCount = (int)envelope.Hid.Count;
            var isBackDown = false;
            var isMenuDown = false;

            for (var reportIndex = 0; reportIndex < reportCount; reportIndex++)
            {
                var report = new byte[reportSize];
                Marshal.Copy(
                    IntPtr.Add(rawInputBuffer, RawHidDataOffset + (reportIndex * reportSize)),
                    report,
                    0,
                    report.Length);

                var pressedButtonUsages = ReadPressedButtonUsages(metadata, report);
                PublishReport(envelope.Header.Device, pressedButtonUsages, report.Length);

                if (pressedButtonUsages.Contains(XboxBackButtonUsage))
                {
                    isBackDown = true;
                }

                if (pressedButtonUsages.Contains(XboxMenuButtonUsage))
                {
                    isMenuDown = true;
                }
            }

            UpdateDeviceState(envelope.Header.Device, isBackDown, isMenuDown);
        }
        finally
        {
            Marshal.FreeHGlobal(rawInputBuffer);
        }
    }

    private HidDeviceMetadata? GetOrCreateDeviceMetadata(nint deviceHandle)
    {
        if (_devices.TryGetValue(deviceHandle, out var metadata))
        {
            return metadata;
        }

        uint preparsedDataSize = 0;
        if (GetRawInputDeviceInfo(deviceHandle, RidDevicePreparsedData, nint.Zero, ref preparsedDataSize) == unchecked((uint)-1) ||
            preparsedDataSize == 0)
        {
            return null;
        }

        var preparsedData = new byte[preparsedDataSize];
        var gcHandle = GCHandle.Alloc(preparsedData, GCHandleType.Pinned);
        try
        {
            if (GetRawInputDeviceInfo(
                    deviceHandle,
                    RidDevicePreparsedData,
                    gcHandle.AddrOfPinnedObject(),
                    ref preparsedDataSize) == unchecked((uint)-1))
            {
                return null;
            }

            var maxUsageCount = HidP_MaxUsageListLength(
                HidpReportType.Input,
                ButtonUsagePage,
                gcHandle.AddrOfPinnedObject());

            if (maxUsageCount == 0)
            {
                return null;
            }

            metadata = new HidDeviceMetadata(preparsedData, maxUsageCount);
            _devices[deviceHandle] = metadata;
            return metadata;
        }
        finally
        {
            gcHandle.Free();
        }
    }

    private static ushort[] ReadPressedButtonUsages(HidDeviceMetadata metadata, byte[] report)
    {
        var preparsedDataHandle = GCHandle.Alloc(metadata.PreparsedData, GCHandleType.Pinned);
        var reportHandle = GCHandle.Alloc(report, GCHandleType.Pinned);

        try
        {
            var usages = new ushort[metadata.MaxUsageCount];
            uint usageLength = metadata.MaxUsageCount;

            var status = HidP_GetUsages(
                HidpReportType.Input,
                ButtonUsagePage,
                0,
                usages,
                ref usageLength,
                preparsedDataHandle.AddrOfPinnedObject(),
                reportHandle.AddrOfPinnedObject(),
                (uint)report.Length);

            if (status != HidpStatusSuccess && status != HidpStatusUsageNotFound)
            {
                return [];
            }

            return usageLength == 0
                ? []
                : usages.Take((int)usageLength).ToArray();
        }
        finally
        {
            reportHandle.Free();
            preparsedDataHandle.Free();
        }
    }

    private void UpdateDeviceState(nint deviceHandle, bool isBackDown, bool isMenuDown)
    {
        _deviceStates[deviceHandle] = new HidButtonState(isBackDown, isMenuDown);
        UpdateAggregateButtonStates();
    }

    private void RemoveDevice(nint deviceHandle)
    {
        if (_devices.Remove(deviceHandle, out var metadata))
        {
            metadata.Dispose();
        }

        _deviceStates.Remove(deviceHandle);
        _lastPublishedUsageSignatures.Remove(deviceHandle);
        UpdateAggregateButtonStates();
    }

    private void PublishReport(nint deviceHandle, IReadOnlyList<ushort> buttonUsages, int reportLength)
    {
        var handler = ReportObserved;
        if (handler is null)
        {
            return;
        }

        var usageSignature = buttonUsages.Count == 0
            ? "-"
            : string.Join(",", buttonUsages);
        if (_lastPublishedUsageSignatures.TryGetValue(deviceHandle, out var previousSignature) &&
            string.Equals(previousSignature, usageSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastPublishedUsageSignatures[deviceHandle] = usageSignature;
        handler(new HidMenuButtonReport(
            deviceHandle,
            buttonUsages.ToArray(),
            buttonUsages.Contains(XboxMenuButtonUsage),
            reportLength));
    }

    private void DisposeAllMetadata()
    {
        foreach (var metadata in _devices.Values)
        {
            metadata.Dispose();
        }

        _devices.Clear();
        _deviceStates.Clear();
        _lastPublishedUsageSignatures.Clear();
        _isBackDown = false;
        _isMenuDown = false;
    }

    private void UpdateAggregateButtonStates()
    {
        _isBackDown = _deviceStates.Values.Any(value => value.IsBackDown);
        _isMenuDown = _deviceStates.Values.Any(value => value.IsMenuDown);
    }

    private static void RegisterRawInputTargets(nint targetWindowHandle)
    {
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = JoystickUsage,
                Flags = RidevInputSink | RidevDevNotify,
                Target = targetWindowHandle
            },
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = GamepadUsage,
                Flags = RidevInputSink | RidevDevNotify,
                Target = targetWindowHandle
            }
        };

        _ = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private sealed class HidDeviceMetadata : IDisposable
    {
        public HidDeviceMetadata(byte[] preparsedData, uint maxUsageCount)
        {
            PreparsedData = preparsedData;
            MaxUsageCount = Math.Max(8u, maxUsageCount);
        }

        public byte[] PreparsedData { get; }

        public uint MaxUsageCount { get; }

        public void Dispose()
        {
        }
    }

    private readonly record struct HidButtonState(bool IsBackDown, bool IsMenuDown);

    private enum HidpReportType : short
    {
        Input = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawHid
    {
        public uint SizeHid;
        public uint Count;
        public byte RawDataStart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHidEnvelope
    {
        public RawInputHeader Header;
        public RawHid Hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public int Flags;
        public nint Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInput,
        int command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        int command,
        nint data,
        ref uint size);

    [DllImport("hid.dll")]
    private static extern uint HidP_MaxUsageListLength(
        HidpReportType reportType,
        ushort usagePage,
        nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(
        HidpReportType reportType,
        ushort usagePage,
        ushort linkCollection,
        [Out] ushort[] usageList,
        ref uint usageLength,
        nint preparsedData,
        nint report,
        uint reportLength);
}

public sealed record HidMenuButtonReport(
    nint DeviceHandle,
    IReadOnlyList<ushort> ButtonUsages,
    bool IsExpectedMenuUsagePressed,
    int ReportLength);
