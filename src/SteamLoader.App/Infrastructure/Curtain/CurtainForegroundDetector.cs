using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLoader.App.Infrastructure.Curtain;

internal static class CurtainForegroundDetector
{
    private const int GwOwner = 4;
    private const int DwmwaCloaked = 14;

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Agent",
        "ApplicationFrameHost",
        "Battle.net",
        "ChatGPT",
        "Code",
        "Codex",
        "EADesktop",
        "EALauncher",
        "EpicGamesLauncher",
        "explorer",
        "GalaxyClient",
        "GalaxyClientService",
        "GamingServices",
        "LauncherPatcher",
        "Origin",
        "powershell",
        "pwsh",
        "RockstarService",
        "SocialClubHelper",
        "steam",
        "steamwebhelper",
        "ToolsForSteam",
        "UbisoftConnect",
        "upc",
        "WindowsTerminal"
    };

    public static IntPtr CaptureForegroundWindow() => GetForegroundWindow();

    public static CurtainForegroundCandidate? TryResolve()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero ||
            !IsWindowVisible(windowHandle) ||
            GetWindow(windowHandle, GwOwner) != IntPtr.Zero ||
            IsWindowCloaked(windowHandle) ||
            !GetWindowRect(windowHandle, out var bounds))
        {
            return null;
        }

        var width = Math.Max(0, bounds.Right - bounds.Left);
        var height = Math.Max(0, bounds.Bottom - bounds.Top);
        if (width < 640 || height < 360)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (rawProcessId == 0 || rawProcessId == Environment.ProcessId)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)rawProcessId);
            if (process.HasExited || IgnoredProcesses.Contains(process.ProcessName))
            {
                return null;
            }

            var screen = System.Windows.Forms.Screen.FromHandle(windowHandle).Bounds;
            var isFullscreen =
                width >= screen.Width * 0.9 &&
                height >= screen.Height * 0.85;

            return new CurtainForegroundCandidate(
                windowHandle,
                process.ProcessName,
                GetWindowTitle(windowHandle),
                isFullscreen);
        }
        catch
        {
            return null;
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
        _ = GetWindowTextW(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr windowHandle, int attribute, out int value, int size);
}

internal sealed record CurtainForegroundCandidate(
    IntPtr WindowHandle,
    string ProcessName,
    string WindowTitle,
    bool IsFullscreen);
