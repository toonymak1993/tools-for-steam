using System.Runtime.InteropServices;
using System.Management;
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
    private const int RidDeviceName = 0x20000007;
    private const int RidDevicePreparsedData = 0x20000005;
    private const int RidevInputSink = 0x00000100;
    private const int RidevDevNotify = 0x00002000;
    private const int RidevPageOnly = 0x00000020;
    private const int RawInputTypeKeyboard = 1;
    private const int RawInputTypeHid = 2;
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort ButtonUsagePage = 0x09;
    private const ushort MsiDirectInputUsagePage = 0xFFF0;
    private const ushort MsiDirectInputUsage = 0x0040;
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
    private readonly Dictionary<nint, ushort[]> _lastButtonUsages = [];
    private readonly Dictionary<nint, byte[]> _lastRawReports = [];
    private readonly Thread _thread;

    private Dispatcher? _dispatcher;
    private HwndSource? _source;
    private ManagementEventWatcher? _msiSpecialKeyWatcher;
    private bool _disposed;
    private volatile bool _isBackDown;
    private volatile bool _isMenuDown;
    private volatile ushort[] _controllerButtonMasks = [];

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

    public IReadOnlyList<ushort> ControllerButtonMasks => _controllerButtonMasks;

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
            StartMsiSpecialKeyWatcher();
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

        StopMsiSpecialKeyWatcher();

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

            var header = Marshal.PtrToStructure<RawInputHeader>(rawInputBuffer);
            if (header.Type == RawInputTypeKeyboard)
            {
                ProcessRawKeyboard(rawInputBuffer, header);
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
                PublishReport(envelope.Header.Device, metadata, pressedButtonUsages, report);

                if (pressedButtonUsages.Contains(XboxBackButtonUsage))
                {
                    isBackDown = true;
                }

                if (pressedButtonUsages.Contains(XboxMenuButtonUsage))
                {
                    isMenuDown = true;
                }
            }

            // MSI's DirectInput mode exposes LT/RT as buttons 6/7 in addition
            // to their analog axes. HID numbers those as usages 7/8, which are
            // Back/Menu on an Xbox HID descriptor. Keep publishing the report
            // for Live Detect, but never let the physical MSI endpoint drive
            // global Steam shortcuts; the VIIPER XInput device is authoritative.
            if (CanContributeToShortcutState(metadata.DeviceName))
            {
                UpdateDeviceState(envelope.Header.Device, isBackDown, isMenuDown);
            }
            else
            {
                UpdateDeviceState(envelope.Header.Device, isBackDown: false, isMenuDown: false);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(rawInputBuffer);
        }
    }

    internal static bool CanContributeToShortcutState(string deviceName) =>
        !deviceName.Contains("VID_0DB0&PID_1902", StringComparison.OrdinalIgnoreCase);

    private void ProcessRawKeyboard(nint rawInputBuffer, RawInputHeader header)
    {
        var envelope = Marshal.PtrToStructure<RawInputKeyboardEnvelope>(rawInputBuffer);
        var isPressed = envelope.Keyboard.Message is 0x0100 or 0x0104;
        var isReleased = envelope.Keyboard.Message is 0x0101 or 0x0105;
        if (!isPressed && !isReleased)
        {
            return;
        }

        var deviceName = ReadDeviceName(header.Device);
        var inputCode = $"keyboard:vk-{envelope.Keyboard.VirtualKey:X2}:scan-{envelope.Keyboard.MakeCode:X2}:flags-{(envelope.Keyboard.Flags & 0x0003):X}";
        ReportObserved?.Invoke(new HidMenuButtonReport(
            header.Device,
            [],
            false,
            Marshal.SizeOf<RawKeyboard>(),
            deviceName,
            "keyboard",
            inputCode,
            isPressed,
            $"VK 0x{envelope.Keyboard.VirtualKey:X2}, scan 0x{envelope.Keyboard.MakeCode:X2}"));
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
            var capsStatus = HidP_GetCaps(gcHandle.AddrOfPinnedObject(), out var caps);
            metadata = new HidDeviceMetadata(
                preparsedData,
                maxUsageCount,
                ReadDeviceName(deviceHandle),
                capsStatus == HidpStatusSuccess ? caps.UsagePage : (ushort)0,
                capsStatus == HidpStatusSuccess ? caps.Usage : (ushort)0);
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
        if (metadata.MaxUsageCount == 0)
        {
            return [];
        }

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
        _lastButtonUsages.Remove(deviceHandle);
        _lastRawReports.Remove(deviceHandle);
        UpdateAggregateButtonStates();
    }

    private void PublishReport(
        nint deviceHandle,
        HidDeviceMetadata metadata,
        IReadOnlyList<ushort> buttonUsages,
        byte[] report)
    {
        var handler = ReportObserved;
        if (handler is null)
        {
            _lastButtonUsages[deviceHandle] = buttonUsages.ToArray();
            _lastRawReports[deviceHandle] = report;
            return;
        }

        var previousUsages = _lastButtonUsages.GetValueOrDefault(deviceHandle) ?? [];
        var addedUsages = buttonUsages.Except(previousUsages).Order().ToArray();
        var removedUsages = previousUsages.Except(buttonUsages).Order().ToArray();
        var previousReport = _lastRawReports.GetValueOrDefault(deviceHandle);
        var rawChanges = BuildRawChanges(previousReport, report);
        _lastButtonUsages[deviceHandle] = buttonUsages.ToArray();
        _lastRawReports[deviceHandle] = report;

        var usesRawReportIdentity = metadata.UsagePage != GenericDesktopUsagePage;
        var inputCode = addedUsages.Length > 0
            ? $"hid-button:usage-{addedUsages[0]}"
            : removedUsages.Length > 0
                ? $"hid-button:usage-{removedUsages[0]}"
                : usesRawReportIdentity && rawChanges.Count > 0
                    ? $"hid-raw:{string.Join(",", rawChanges.Take(8))}"
                    : string.Empty;
        if (string.IsNullOrWhiteSpace(inputCode))
        {
            return;
        }

        var usageSignature = $"{inputCode}:{string.Join(",", buttonUsages)}";
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
            report.Length,
            metadata.DeviceName,
            "hid",
            inputCode,
            addedUsages.Length > 0 || (usesRawReportIdentity && rawChanges.Any(change => !change.EndsWith("=00", StringComparison.Ordinal))),
            addedUsages.Length > 0
                ? $"HID button usage {addedUsages[0]}"
                : $"HID usage page 0x{metadata.UsagePage:X4}, usage 0x{metadata.Usage:X4}, changed {string.Join(", ", rawChanges.Take(8))}"));
    }

    private static IReadOnlyList<string> BuildRawChanges(byte[]? previous, byte[] current)
    {
        if (previous is null || previous.Length != current.Length)
        {
            return [];
        }

        var changes = new List<string>();
        for (var index = 0; index < current.Length; index++)
        {
            if (previous[index] != current[index])
            {
                changes.Add($"b{index}={current[index]:X2}");
            }
        }

        return changes;
    }

    private static string ReadDeviceName(nint deviceHandle)
    {
        uint characterCount = 0;
        if (GetRawInputDeviceInfo(deviceHandle, RidDeviceName, nint.Zero, ref characterCount) == unchecked((uint)-1) ||
            characterCount == 0)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)(characterCount + 1) * sizeof(char)));
        try
        {
            if (GetRawInputDeviceInfo(deviceHandle, RidDeviceName, buffer, ref characterCount) == unchecked((uint)-1))
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(buffer, (int)characterCount)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
        _lastButtonUsages.Clear();
        _lastRawReports.Clear();
        _isBackDown = false;
        _isMenuDown = false;
        _controllerButtonMasks = [];
    }

    private void UpdateAggregateButtonStates()
    {
        _isBackDown = _deviceStates.Values.Any(value => value.IsBackDown);
        _isMenuDown = _deviceStates.Values.Any(value => value.IsMenuDown);
        _controllerButtonMasks = _lastButtonUsages
            .Where(entry =>
                _devices.TryGetValue(entry.Key, out var metadata) &&
                metadata.UsagePage == GenericDesktopUsagePage &&
                CanContributeToShortcutState(metadata.DeviceName))
            .Select(entry => ConvertButtonUsagesToXInputMask(entry.Value))
            .Where(mask => mask != 0)
            .ToArray();
    }

    internal static ushort ConvertButtonUsagesToXInputMask(IReadOnlyList<ushort> buttonUsages)
    {
        ushort mask = 0;
        foreach (var usage in buttonUsages)
        {
            mask |= usage switch
            {
                1 => (ushort)0x1000,  // A
                2 => (ushort)0x2000,  // B
                3 => (ushort)0x4000,  // X
                4 => (ushort)0x8000,  // Y
                5 => (ushort)0x0100,  // LB
                6 => (ushort)0x0200,  // RB
                7 => (ushort)0x0020,  // View / Back
                8 => (ushort)0x0010,  // Menu / Start
                9 => (ushort)0x0040,  // Left stick click
                10 => (ushort)0x0080, // Right stick click
                _ => (ushort)0
            };
        }

        return mask;
    }

    private static void RegisterRawInputTargets(nint targetWindowHandle)
    {
        var devices = new List<RawInputDevice>
        {
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = 0,
                Flags = RidevInputSink | RidevDevNotify | RidevPageOnly,
                Target = targetWindowHandle
            },
            new RawInputDevice
            {
                UsagePage = MsiDirectInputUsagePage,
                Usage = MsiDirectInputUsage,
                Flags = RidevInputSink | RidevDevNotify,
                Target = targetWindowHandle
            }
        };

        foreach (var target in EnumerateAdditionalRawInputTargets(targetWindowHandle))
        {
            if (!devices.Any(device => device.UsagePage == target.UsagePage && device.Usage == target.Usage))
            {
                devices.Add(target);
            }
        }

        _ = RegisterRawInputDevices(devices.ToArray(), (uint)devices.Count, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private void StartMsiSpecialKeyWatcher()
    {
        try
        {
            var scope = new ManagementScope("\\\\.\\root\\WMI");
            _msiSpecialKeyWatcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM MSI_Event"));
            _msiSpecialKeyWatcher.EventArrived += OnMsiSpecialKeyEvent;
            _msiSpecialKeyWatcher.Start();
        }
        catch
        {
            StopMsiSpecialKeyWatcher();
        }
    }

    private void StopMsiSpecialKeyWatcher()
    {
        if (_msiSpecialKeyWatcher is null)
        {
            return;
        }

        try
        {
            _msiSpecialKeyWatcher.EventArrived -= OnMsiSpecialKeyEvent;
            _msiSpecialKeyWatcher.Stop();
            _msiSpecialKeyWatcher.Dispose();
        }
        catch
        {
        }
        finally
        {
            _msiSpecialKeyWatcher = null;
        }
    }

    private void OnMsiSpecialKeyEvent(object sender, EventArrivedEventArgs args)
    {
        var rawValue = args.NewEvent.Properties["MSIEvt"]?.Value;
        var eventCode = Convert.ToInt32(rawValue) & 0xFF;
        var inputCode = eventCode switch
        {
            41 => "msi-wmi:event-41",
            88 => "msi-wmi:event-88",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(inputCode))
        {
            return;
        }

        ReportObserved?.Invoke(new HidMenuButtonReport(
            0,
            [],
            false,
            0,
            "MSI_ACPI:MSI Claw",
            "wmi",
            inputCode,
            true,
            eventCode == 41 ? "MSI Center button (ACPI event 41)" : "Quick Settings button (ACPI event 88)"));
    }

    private static IEnumerable<RawInputDevice> EnumerateAdditionalRawInputTargets(nint targetWindowHandle)
    {
        uint count = 0;
        var itemSize = (uint)Marshal.SizeOf<RawInputDeviceListEntry>();
        if (GetRawInputDeviceList(null, ref count, itemSize) == unchecked((uint)-1) || count == 0)
        {
            yield break;
        }

        var entries = new RawInputDeviceListEntry[count];
        if (GetRawInputDeviceList(entries, ref count, itemSize) == unchecked((uint)-1))
        {
            yield break;
        }

        foreach (var entry in entries.Take((int)count).Where(entry => entry.Type == RawInputTypeHid))
        {
            uint size = 0;
            if (GetRawInputDeviceInfo(entry.Device, RidDevicePreparsedData, nint.Zero, ref size) == unchecked((uint)-1) || size == 0)
            {
                continue;
            }

            var data = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputDeviceInfo(entry.Device, RidDevicePreparsedData, data, ref size) == unchecked((uint)-1) ||
                    HidP_GetCaps(data, out var caps) != HidpStatusSuccess ||
                    caps.UsagePage == GenericDesktopUsagePage)
                {
                    continue;
                }

                yield return new RawInputDevice
                {
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    Flags = RidevInputSink | RidevDevNotify,
                    Target = targetWindowHandle
                };
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
    }

    private sealed class HidDeviceMetadata : IDisposable
    {
        public HidDeviceMetadata(
            byte[] preparsedData,
            uint maxUsageCount,
            string deviceName,
            ushort usagePage,
            ushort usage)
        {
            PreparsedData = preparsedData;
            MaxUsageCount = maxUsageCount;
            DeviceName = deviceName;
            UsagePage = usagePage;
            Usage = usage;
        }

        public byte[] PreparsedData { get; }

        public uint MaxUsageCount { get; }

        public string DeviceName { get; }

        public ushort UsagePage { get; }

        public ushort Usage { get; }

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
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputKeyboardEnvelope
    {
        public RawInputHeader Header;
        public RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceListEntry
    {
        public nint Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
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

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        int command,
        nint data,
        ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RawInputDeviceListEntry[]? devices,
        ref uint deviceCount,
        uint size);

    [DllImport("hid.dll")]
    private static extern uint HidP_MaxUsageListLength(
        HidpReportType reportType,
        ushort usagePage,
        nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

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
    int ReportLength,
    string DeviceName = "",
    string InputKind = "hid",
    string InputCode = "",
    bool IsPressed = true,
    string Detail = "");
