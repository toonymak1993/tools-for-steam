using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLoader.App.Infrastructure.Performance;

internal static class PerformanceForegroundTargetResolver
{
    private const int ForegroundTargetMinimumWidth = 640;
    private const int ForegroundTargetMinimumHeight = 360;
    private const int GwOwner = 4;
    private const int DwmwaCloaked = 14;

    private static readonly HashSet<string> IgnoredForegroundTargetProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "Code",
        "Codex",
        "explorer",
        "Idle",
        "LockApp",
        "powershell",
        "pwsh",
        "SearchApp",
        "SearchHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "steam",
        "steamwebhelper",
        "System",
        "TextInputHost",
        "ToolsForSteam",
        "Widgets",
        "WindowsTerminal"
    };

    public static ForegroundTargetCandidate? TryResolve()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero || !IsWindowVisible(windowHandle))
        {
            return null;
        }

        if (GetWindow(windowHandle, GwOwner) != IntPtr.Zero || IsWindowCloaked(windowHandle))
        {
            return null;
        }

        var title = GetWindowTitle(windowHandle);
        if (string.IsNullOrWhiteSpace(title) || !GetWindowRect(windowHandle, out var bounds))
        {
            return null;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < ForegroundTargetMinimumWidth || height < ForegroundTargetMinimumHeight)
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
            if (process.HasExited || string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return null;
            }

            if (IgnoredForegroundTargetProcesses.Contains(process.ProcessName))
            {
                return null;
            }

            var executablePath = TryGetExecutablePath(process);
            return new ForegroundTargetCandidate(process.Id, process.ProcessName, title.Trim(), executablePath)
            {
                WindowHandle = $"0x{windowHandle.ToInt64():X}",
            };
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetExecutablePath(Process process)
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

    private static bool IsWindowCloaked(nint windowHandle)
    {
        if (DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out var cloaked, sizeof(int)) != 0)
        {
            return false;
        }

        return cloaked != 0;
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
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}

internal sealed record ForegroundTargetCandidate(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string ExecutablePath)
{
    public string WindowHandle { get; init; } = string.Empty;
}

