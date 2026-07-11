using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLoader.App.Services;

internal static class SteamBigPictureForegroundDetector
{
    public static bool IsBigPictureForeground()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero || !IsWindowVisible(windowHandle))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (rawProcessId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)rawProcessId);
            if (process.HasExited || string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return false;
            }

            if (!string.Equals(process.ProcessName, "steamwebhelper", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(process.ProcessName, "steam", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var title = GetWindowTitle(windowHandle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            return title.Replace('-', ' ').Contains("Big Picture", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Steam", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowTitle(nint windowHandle)
    {
        var length = GetWindowTextLengthW(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowTextW(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint windowHandle, StringBuilder builder, int count);
}
