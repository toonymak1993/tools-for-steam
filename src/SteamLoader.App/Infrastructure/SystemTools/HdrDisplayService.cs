using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SteamLoader.App.Infrastructure.SystemTools;

public sealed class HdrDisplayService
{
    private const uint QueryOnlyActivePaths = 0x00000002;
    private const uint QueryVirtualModeAware = 0x00000010;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int GetTargetName = 2;
    private const int GetAdvancedColorInfo = 9;
    private const int SetAdvancedColorState = 10;
    private const int GetAdvancedColorInfo2 = 15;
    private const int SetHdrState = 16;
    private readonly object _gate = new();

    public HdrDisplaySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            try
            {
                return ReadSnapshot();
            }
            catch (Exception exception)
            {
                return new HdrDisplaySnapshot(
                    Available: false,
                    Supported: false,
                    Enabled: false,
                    Mixed: false,
                    Displays: [],
                    StatusText: $"HDR state is unavailable: {exception.Message}");
            }
        }
    }

    public HdrDisplaySnapshot SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            var targets = ReadTargets();
            var compatibleTargets = targets
                .Where(target => target.Supported && !target.LimitedByPolicy)
                .ToArray();
            if (compatibleTargets.Length == 0)
            {
                throw new InvalidOperationException("No active HDR-compatible display is available.");
            }

            foreach (var target in compatibleTargets)
            {
                SetTargetEnabled(target.AdapterId, target.TargetId, enabled);
            }

            return ReadSnapshot();
        }
    }

    private static HdrDisplaySnapshot ReadSnapshot()
    {
        var targets = ReadTargets();
        var displays = targets
            .Select(target => new HdrDisplayItem(
                Id: FormatTargetId(target.AdapterId, target.TargetId),
                Name: target.Name,
                Supported: target.Supported,
                Enabled: target.Enabled,
                LimitedByPolicy: target.LimitedByPolicy,
                BitsPerColorChannel: target.BitsPerColorChannel))
            .ToArray();
        var compatible = displays.Where(display => display.Supported).ToArray();
        var enabledCount = compatible.Count(display => display.Enabled);
        var supported = compatible.Length > 0;
        var enabled = enabledCount > 0;
        var mixed = enabledCount > 0 && enabledCount < compatible.Length;
        var status = !supported
            ? "No active HDR-compatible display was detected."
            : mixed
                ? "HDR is enabled on some active displays."
                : enabled
                    ? "HDR is enabled."
                    : "HDR is disabled.";

        return new HdrDisplaySnapshot(
            Available: true,
            Supported: supported,
            Enabled: enabled,
            Mixed: mixed,
            Displays: displays,
            StatusText: status);
    }

    private static IReadOnlyList<HdrTargetState> ReadTargets()
    {
        var flags = QueryOnlyActivePaths | QueryVirtualModeAware;
        DisplayConfigPathInfo[] paths = [];
        DisplayConfigModeInfo[] modes = [];
        uint pathCount = 0;
        uint modeCount = 0;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ThrowIfFailed(GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount));
            paths = new DisplayConfigPathInfo[pathCount];
            modes = new DisplayConfigModeInfo[modeCount];
            var result = QueryDisplayConfig(
                flags,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);
            if (result == ErrorSuccess)
            {
                break;
            }

            if (result != ErrorInsufficientBuffer || attempt == 2)
            {
                ThrowIfFailed(result);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<HdrTargetState>();
        foreach (var path in paths.Take((int)pathCount))
        {
            var key = FormatTargetId(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            if (!seen.Add(key))
            {
                continue;
            }

            var name = ReadTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            targets.Add(ReadTargetState(
                path.TargetInfo.AdapterId,
                path.TargetInfo.Id,
                string.IsNullOrWhiteSpace(name) ? $"Display {targets.Count + 1}" : name));
        }

        return targets;
    }

    private static HdrTargetState ReadTargetState(Luid adapterId, uint targetId, string name)
    {
        var modernInfo = new DisplayConfigGetAdvancedColorInfo2
        {
            Header = CreateHeader(GetAdvancedColorInfo2, adapterId, targetId)
        };
        var modernResult = DisplayConfigGetDeviceInfo(ref modernInfo);
        if (modernResult == ErrorSuccess)
        {
            return new HdrTargetState(
                adapterId,
                targetId,
                name,
                Supported: (modernInfo.Value & (1u << 4)) != 0,
                Enabled: (modernInfo.Value & (1u << 5)) != 0,
                LimitedByPolicy: (modernInfo.Value & (1u << 3)) != 0,
                BitsPerColorChannel: modernInfo.BitsPerColorChannel);
        }

        var legacyInfo = new DisplayConfigGetAdvancedColorInfo
        {
            Header = CreateHeader(GetAdvancedColorInfo, adapterId, targetId)
        };
        var legacyResult = DisplayConfigGetDeviceInfo(ref legacyInfo);
        if (legacyResult != ErrorSuccess)
        {
            return new HdrTargetState(
                adapterId,
                targetId,
                name,
                Supported: false,
                Enabled: false,
                LimitedByPolicy: false,
                BitsPerColorChannel: 0);
        }

        return new HdrTargetState(
            adapterId,
            targetId,
            name,
            Supported: (legacyInfo.Value & 1u) != 0,
            Enabled: (legacyInfo.Value & (1u << 1)) != 0,
            LimitedByPolicy: (legacyInfo.Value & (1u << 3)) != 0,
            BitsPerColorChannel: legacyInfo.BitsPerColorChannel);
    }

    private static void SetTargetEnabled(Luid adapterId, uint targetId, bool enabled)
    {
        var modernState = new DisplayConfigSetColorState
        {
            Header = CreateHeader(SetHdrState, adapterId, targetId),
            Value = enabled ? 1u : 0u
        };
        var modernResult = DisplayConfigSetDeviceInfo(ref modernState);
        if (modernResult == ErrorSuccess)
        {
            return;
        }

        var legacyState = new DisplayConfigSetColorState
        {
            Header = CreateHeader(SetAdvancedColorState, adapterId, targetId),
            Value = enabled ? 1u : 0u
        };
        ThrowIfFailed(DisplayConfigSetDeviceInfo(ref legacyState));
    }

    private static string ReadTargetName(Luid adapterId, uint targetId)
    {
        var targetName = new DisplayConfigTargetDeviceName
        {
            Header = CreateHeader(GetTargetName, adapterId, targetId)
        };
        return DisplayConfigGetDeviceInfo(ref targetName) == ErrorSuccess
            ? targetName.MonitorFriendlyDeviceName?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static DisplayConfigDeviceInfoHeader CreateHeader(int type, Luid adapterId, uint targetId)
        => new()
        {
            Type = type,
            Size = type switch
            {
                GetTargetName => (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                GetAdvancedColorInfo => (uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(),
                GetAdvancedColorInfo2 => (uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo2>(),
                _ => (uint)Marshal.SizeOf<DisplayConfigSetColorState>()
            },
            AdapterId = adapterId,
            Id = targetId
        };

    private static string FormatTargetId(Luid adapterId, uint targetId)
        => $"{adapterId.HighPart:x8}{adapterId.LowPart:x8}:{targetId:x8}";

    private static void ThrowIfFailed(int errorCode)
    {
        if (errorCode != ErrorSuccess)
        {
            throw new Win32Exception(errorCode);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo2 requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int DisplayConfigSetDeviceInfo(ref DisplayConfigSetColorState requestPacket);

    private sealed record HdrTargetState(
        Luid AdapterId,
        uint TargetId,
        string Name,
        bool Supported,
        bool Enabled,
        bool LimitedByPolicy,
        uint BitsPerColorChannel);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DisplayConfigModeUnion
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public DisplayConfigModeUnion Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo2
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSetColorState
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Value;
    }
}

public sealed record HdrDisplaySnapshot(
    bool Available,
    bool Supported,
    bool Enabled,
    bool Mixed,
    IReadOnlyList<HdrDisplayItem> Displays,
    string StatusText);

public sealed record HdrDisplayItem(
    string Id,
    string Name,
    bool Supported,
    bool Enabled,
    bool LimitedByPolicy,
    uint BitsPerColorChannel);
