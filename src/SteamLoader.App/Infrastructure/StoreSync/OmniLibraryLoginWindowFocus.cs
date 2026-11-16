using System.Diagnostics;
using System.Runtime.InteropServices;
using SteamLoader.App.Services;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Placement and focus for the OmniLibrary sign-in window.
///
/// The sign-in window is its own process, started by the TFS background host
/// while Steam Big Picture owns the foreground. Windows refuses a background
/// process the foreground, so the window used to open behind Big Picture
/// (controller and keyboard still driving Steam), and a second Sign In press
/// restored the maximized window to a small default size. This helper:
///  - covers the monitor Big Picture is on (not always the primary one, the
///    internal/external screen switch can move Big Picture);
///  - takes the foreground with the same permission an interactive window
///    switch gets, retried while Steam is still settling;
///  - hands the foreground back to Big Picture when sign-in ends.
/// UnifyDeck gets the same result on SteamOS by letting Steam launch its
/// sign-in browser; on Windows the window has to claim it itself.
/// </summary>
internal static class OmniLibraryLoginWindowFocus
{
    private const int MonitorDefaultToNearest = 2;
    private const int MonitorDefaultToPrimary = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const int ShowWindowShow = 5;
    private const int ShowWindowRestore = 9;
    private const byte VirtualKeyMenu = 0x12;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint AsfwAny = unchecked((uint)-1);
    private static readonly IntPtr WindowTopMost = new(-1);
    private static readonly IntPtr WindowNotTopMost = new(-2);
    private static readonly IntPtr WindowTop = IntPtr.Zero;

    /// <summary>Name of the event a running sign-in window waits on to come back in front.</summary>
    public static string ActivationEventName(string storeId) =>
        $"Local\\ToolsForSteam.OmniLibraryLogin.{storeId}.Activate";

    /// <summary>The visible Big Picture window, or zero.</summary>
    public static IntPtr FindBigPictureWindow()
    {
        try
        {
            return SteamBigPictureForegroundDetector.FindGamepadUiWindows().FirstOrDefault();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Full bounds (in this process's coordinate space) of the monitor the
    /// window should cover: Big Picture's monitor, else the monitor of the
    /// current foreground window, else the primary monitor.
    /// </summary>
    public static bool TryGetTargetMonitorBounds(out Rect bounds)
    {
        bounds = default;
        var anchor = FindBigPictureWindow();
        if (anchor == IntPtr.Zero)
        {
            anchor = GetForegroundWindow();
        }

        var monitor = anchor != IntPtr.Zero
            ? MonitorFromWindow(anchor, MonitorDefaultToNearest)
            : MonitorFromPoint(default, MonitorDefaultToPrimary);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        bounds = info.Monitor;
        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    /// <summary>Covers the target monitor completely and keeps the window top-most.</summary>
    public static void CoverTargetMonitor(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !TryGetTargetMonitorBounds(out var bounds))
        {
            return;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, ShowWindowRestore);
        }

        SetWindowPos(
            windowHandle,
            WindowTopMost,
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            SwpShowWindow | SwpFrameChanged);
    }

    public static bool IsForeground(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero && GetForegroundWindow() == windowHandle;

    /// <summary>True when Steam's Big Picture window took the foreground from us.</summary>
    public static bool IsBigPictureForeground()
    {
        try
        {
            return SteamBigPictureForegroundDetector.IsGamepadUiForeground();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Brings <paramref name="windowHandle"/> to the foreground. Returns true
    /// when Windows confirms it. Never throws.
    /// </summary>
    public static bool TryBringToForeground(IntPtr windowHandle, bool keepTopMost)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (IsIconic(windowHandle))
            {
                ShowWindow(windowHandle, ShowWindowRestore);
            }
            else if (!IsWindowVisible(windowHandle))
            {
                ShowWindow(windowHandle, ShowWindowShow);
            }

            if (TryFocus(windowHandle))
            {
                return true;
            }

            // Windows only lets the process that received the last input
            // event take the foreground. A lone ALT tap gives this process the
            // same right an interactive window switch has, without leaving a
            // modifier held down.
            keybd_event(VirtualKeyMenu, 0, 0, UIntPtr.Zero);
            try
            {
                if (TryFocus(windowHandle))
                {
                    return true;
                }
            }
            finally
            {
                keybd_event(VirtualKeyMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
            }

            SwitchToThisWindow(windowHandle, true);
            Thread.Sleep(60);
            if (GetForegroundWindow() == windowHandle)
            {
                return true;
            }

            // Last resort: a z-order pulse above Big Picture's window.
            SetWindowPos(windowHandle, WindowTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            if (!keepTopMost)
            {
                SetWindowPos(windowHandle, WindowNotTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            }

            return TryFocus(windowHandle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lets a freshly started sign-in process take the foreground. Only works
    /// while the caller itself may set it; harmless otherwise.
    /// </summary>
    public static void AllowForeground(Process process)
    {
        try
        {
            AllowSetForegroundWindow((uint)process.Id);
        }
        catch
        {
            // Best effort; the sign-in window claims the foreground itself.
        }
    }

    /// <summary>Gives the foreground back to Big Picture after sign-in.</summary>
    public static void ReturnToBigPicture()
    {
        var bigPicture = FindBigPictureWindow();
        if (bigPicture != IntPtr.Zero)
        {
            TryBringToForeground(bigPicture, keepTopMost: false);
        }
    }

    /// <summary>Lets any process (Steam) take the foreground back from us.</summary>
    public static void ReleaseForeground()
    {
        try
        {
            AllowSetForegroundWindow(AsfwAny);
        }
        catch
        {
        }
    }

    private static bool TryFocus(IntPtr windowHandle)
    {
        if (GetForegroundWindow() == windowHandle)
        {
            return true;
        }

        var foregroundWindow = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow != IntPtr.Zero
            ? GetWindowThreadProcessId(foregroundWindow, out _)
            : 0;
        var targetThreadId = GetWindowThreadProcessId(windowHandle, out _);
        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != 0 &&
                targetThreadId != currentThreadId &&
                targetThreadId != foregroundThreadId)
            {
                attachedTarget = AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            SetWindowPos(windowHandle, WindowTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
            SetActiveWindow(windowHandle);
            SetFocus(windowHandle);
            Thread.Sleep(40);
            return GetForegroundWindow() == windowHandle;
        }
        finally
        {
            if (attachedForeground)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }

            if (attachedTarget)
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
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
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr windowHandle, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
