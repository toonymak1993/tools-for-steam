using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLoader.App.Infrastructure.Curtain;

/// <summary>
/// The small set of Win32 window operations TFS Curtain needs: list the
/// visible top-level windows with their process and monitor, pin/unpin a
/// window in the topmost band, and hide/restore launcher windows without
/// activating anything.
/// </summary>
internal static class CurtainWindowSystem
{
    private const int GwlExStyle = -20;
    private const int GwOwner = 4;
    private const long WsExTopmost = 0x00000008;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public static IntPtr ForegroundWindow => GetForegroundWindow();

    public static bool IsAlive(IntPtr windowHandle) => windowHandle != IntPtr.Zero && IsWindow(windowHandle);

    public static bool IsTopmost(IntPtr windowHandle) =>
        IsAlive(windowHandle) && (GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64() & WsExTopmost) != 0;

    public static bool SetTopmost(IntPtr windowHandle, bool topmost) =>
        IsAlive(windowHandle) &&
        SetWindowPos(
            windowHandle,
            topmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>Moves <paramref name="windowHandle"/> directly behind <paramref name="anchor"/> without activating it.</summary>
    public static bool PlaceBehind(IntPtr windowHandle, IntPtr anchor) =>
        IsAlive(windowHandle) &&
        IsAlive(anchor) &&
        SetWindowPos(windowHandle, anchor, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    public static bool Hide(IntPtr windowHandle)
    {
        if (!IsAlive(windowHandle) || !IsWindowVisible(windowHandle))
        {
            return false;
        }

        _ = ShowWindow(windowHandle, SwHide);
        return !IsWindowVisible(windowHandle);
    }

    public static bool ShowWithoutActivation(IntPtr windowHandle)
    {
        if (!IsAlive(windowHandle))
        {
            return false;
        }

        _ = ShowWindow(windowHandle, SwShowNoActivate);
        return IsWindowVisible(windowHandle);
    }

    public static uint GetProcessId(IntPtr windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId;
    }

    /// <summary>Visible, uncloaked, non-minimized top-level windows (bounded).</summary>
    public static IReadOnlyList<CurtainWindowInfo> EnumerateWindows(int limit = 200)
    {
        var foreground = GetForegroundWindow();
        var windows = new List<CurtainWindowInfo>();
        EnumWindows(
            (windowHandle, _unused) =>
            {
                if (windows.Count >= limit)
                {
                    return false;
                }

                if (!IsWindowVisible(windowHandle) ||
                    IsIconic(windowHandle) ||
                    IsCloaked(windowHandle) ||
                    !GetWindowRect(windowHandle, out var rect))
                {
                    return true;
                }

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width < 120 || height < 80)
                {
                    return true;
                }

                _ = GetWindowThreadProcessId(windowHandle, out var processId);
                if (processId == 0)
                {
                    return true;
                }

                var exStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
                var monitor = GetMonitorBounds(windowHandle);
                windows.Add(new CurtainWindowInfo(
                    windowHandle,
                    processId,
                    GetTitle(windowHandle),
                    new CurtainRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    monitor,
                    (exStyle & WsExTopmost) != 0,
                    (exStyle & WsExToolWindow) != 0,
                    GetWindow(windowHandle, GwOwner) != IntPtr.Zero,
                    windowHandle == foreground));
                return true;
            },
            IntPtr.Zero);
        return windows;
    }

    public static CurtainRect GetMonitorBounds(IntPtr windowHandle)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new CurtainRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
        }

        return new CurtainRect(0, 0, 1920, 1080);
    }

    private static bool IsCloaked(IntPtr windowHandle) =>
        DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string GetTitle(IntPtr windowHandle)
    {
        var length = GetWindowTextLengthW(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(length, 512) + 1);
        _ = GetWindowTextW(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr windowHandle, StringBuilder builder, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr windowHandle, int attribute, out int value, int size);
}

internal readonly record struct CurtainRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public long Area => (long)Width * Height;
}

internal sealed record CurtainWindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string Title,
    CurtainRect Bounds,
    CurtainRect Monitor,
    bool IsTopmost,
    bool IsToolWindow,
    bool IsOwned,
    bool IsForeground);

/// <summary>Per-session process facts (name, start time), resolved once per PID.</summary>
internal sealed class CurtainProcessCache
{
    private readonly Dictionary<uint, CurtainProcessInfo> _processes = [];

    public CurtainProcessInfo? Get(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        if (_processes.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        CurtainProcessInfo? info = null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!process.HasExited)
            {
                DateTimeOffset startedAt;
                try
                {
                    startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                }
                catch
                {
                    // Elevated or protected processes refuse StartTime.
                    startedAt = DateTimeOffset.UtcNow;
                }

                info = new CurtainProcessInfo(processId, process.ProcessName, startedAt);
            }
        }
        catch
        {
            info = null;
        }

        if (info is not null)
        {
            _processes[processId] = info;
        }

        return info;
    }
}

internal sealed record CurtainProcessInfo(uint ProcessId, string Name, DateTimeOffset StartedAt);
