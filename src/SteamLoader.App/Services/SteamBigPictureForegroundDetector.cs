using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLoader.App.Services;

internal static class SteamBigPictureForegroundDetector
{
    private const int DwmwaCloaked = 14;
    private const int GwOwner = 4;

    public static bool IsBigPictureForeground() => Capture().IsSteamForeground;

    public static SteamWindowSnapshot Capture(string? expectedSteamRoot = null)
    {
        var foregroundHandle = GetForegroundWindow();
        var candidates = new List<SteamWindowCandidate>();
        EnumWindows(
            (windowHandle, _) =>
            {
                var candidate = TryCreateCandidate(windowHandle, foregroundHandle, expectedSteamRoot);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }

                return true;
            },
            IntPtr.Zero);

        var preferred = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.PixelArea)
            .FirstOrDefault();
        var foreground = candidates.FirstOrDefault(candidate => candidate.IsForeground);

        return new SteamWindowSnapshot(
            HasVisibleSteamWindow: preferred is not null,
            HasLikelyGamepadWindow: candidates.Any(candidate => candidate.IsLikelyGamepad),
            IsSteamForeground: foreground is not null,
            PreferredWindowHandle: (preferred ?? foreground)?.WindowHandle ?? IntPtr.Zero,
            ForegroundWindowHandle: foreground?.WindowHandle ?? IntPtr.Zero,
            PreferredWindowTitle: (preferred ?? foreground)?.Title ?? string.Empty,
            PreferredWindowClass: (preferred ?? foreground)?.ClassName ?? string.Empty);
    }

    private static SteamWindowCandidate? TryCreateCandidate(
        IntPtr windowHandle,
        IntPtr foregroundHandle,
        string? expectedSteamRoot)
    {
        if (windowHandle == IntPtr.Zero ||
            !IsWindowVisible(windowHandle) ||
            IsIconic(windowHandle) ||
            IsWindowCloaked(windowHandle) ||
            !GetWindowRect(windowHandle, out var bounds))
        {
            return null;
        }

        var width = Math.Max(0, bounds.Right - bounds.Left);
        var height = Math.Max(0, bounds.Bottom - bounds.Top);
        if (width < 320 || height < 180)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (rawProcessId == 0)
        {
            return null;
        }

        string processName;
        string processPath;
        try
        {
            using var process = Process.GetProcessById((int)rawProcessId);
            if (process.HasExited)
            {
                return null;
            }

            processName = process.ProcessName;
            processPath = TryGetProcessPath(process);
        }
        catch
        {
            return null;
        }

        if (!IsSteamUiProcess(processName) ||
            !IsExpectedSteamProcessPath(processPath, expectedSteamRoot))
        {
            return null;
        }

        var title = GetWindowTitle(windowHandle);
        var className = GetWindowClassName(windowHandle);
        var normalizedTitle = title.Replace('-', ' ');
        var titleLooksGamepad =
            normalizedTitle.Contains("Big Picture", StringComparison.OrdinalIgnoreCase) ||
            normalizedTitle.Contains("Gamepad", StringComparison.OrdinalIgnoreCase);
        var chromiumOrSdlWindow =
            className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("SDL", StringComparison.OrdinalIgnoreCase);
        var largeSurface = width >= 800 && height >= 450;
        var isForeground = windowHandle == foregroundHandle;
        var isLikelyGamepad = titleLooksGamepad ||
            (largeSurface && chromiumOrSdlWindow &&
             string.Equals(processName, "steamwebhelper", StringComparison.OrdinalIgnoreCase));

        var score = 0;
        score += isForeground ? 100 : 0;
        score += isLikelyGamepad ? 80 : 0;
        score += titleLooksGamepad ? 40 : 0;
        score += chromiumOrSdlWindow ? 20 : 0;
        score += largeSurface ? 20 : 0;
        score += string.Equals(processName, "steamwebhelper", StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        score -= GetWindow(windowHandle, GwOwner) != IntPtr.Zero ? 10 : 0;

        return new SteamWindowCandidate(
            windowHandle,
            title,
            className,
            isForeground,
            isLikelyGamepad,
            (long)width * height,
            score);
    }

    private static bool IsSteamUiProcess(string processName) =>
        processName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("steam", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedSteamProcessPath(string processPath, string? expectedSteamRoot)
    {
        if (string.IsNullOrWhiteSpace(expectedSteamRoot) || string.IsNullOrWhiteSpace(processPath))
        {
            return true;
        }

        try
        {
            var root = Path.GetFullPath(expectedSteamRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var executable = Path.GetFullPath(processPath);
            return executable.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsWindowCloaked(IntPtr windowHandle) =>
        DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 &&
        cloaked != 0;

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = GetWindowTextLengthW(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowTextW(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var builder = new StringBuilder(256);
        return GetClassNameW(windowHandle, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private sealed record SteamWindowCandidate(
        IntPtr WindowHandle,
        string Title,
        string ClassName,
        bool IsForeground,
        bool IsLikelyGamepad,
        long PixelArea,
        int Score);

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr windowHandle, StringBuilder builder, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr windowHandle, StringBuilder builder, int count);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr windowHandle, int attribute, out int value, int size);
}

internal sealed record SteamWindowSnapshot(
    bool HasVisibleSteamWindow,
    bool HasLikelyGamepadWindow,
    bool IsSteamForeground,
    IntPtr PreferredWindowHandle,
    IntPtr ForegroundWindowHandle,
    string PreferredWindowTitle,
    string PreferredWindowClass);
